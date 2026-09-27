using System.Collections.Concurrent;
using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Meshes for the floating islands, built on the thread pool and drawn with the object shader
/// (same vertex layout). An island is one tube of rings: from the centre of its domed grassy top
/// out to the wavy edge, then down its rocky underside to a point, with smooth normals.
/// Glowing crystals hang from the underside.
/// </summary>
public sealed unsafe class IslandRenderer : IDisposable
{
    private const int Segments = 56;
    private const int TopRings = 7;
    private const int UnderRings = 12;

    private sealed class Mesh
    {
        public uint Vao, Vbo;
        public int Count;
    }

    private readonly GL _gl;
    private readonly Dictionary<long, Mesh?> _meshes = new(); // null while building
    private readonly ConcurrentQueue<(long Id, float[] Vertices)> _done = new();
    private readonly List<long> _toDelete = new();

    public IslandRenderer(GL gl) => _gl = gl;

    public void Update(IslandField field)
    {
        var alive = new HashSet<long>();
        foreach (var island in field.All)
        {
            alive.Add(island.Id);
            if (_meshes.ContainsKey(island.Id)) continue;
            _meshes[island.Id] = null;
            var captured = island;
            ThreadPool.QueueUserWorkItem(_ => _done.Enqueue((captured.Id, Build(captured))));
        }

        while (_done.TryDequeue(out var result))
        {
            if (!_meshes.ContainsKey(result.Id)) continue;
            _meshes[result.Id] = Upload(result.Vertices);
        }

        _toDelete.Clear();
        foreach (var (id, mesh) in _meshes)
            if (!alive.Contains(id) && mesh is not null) _toDelete.Add(id);
        foreach (var id in _toDelete)
        {
            var mesh = _meshes[id]!;
            _gl.DeleteBuffer(mesh.Vbo);
            _gl.DeleteVertexArray(mesh.Vao);
            _meshes.Remove(id);
        }
    }

    /// <summary>Draws every island (the current shader's model matrix must be the identity).</summary>
    public void Draw()
    {
        foreach (var mesh in _meshes.Values)
        {
            if (mesh is null) continue;
            _gl.BindVertexArray(mesh.Vao);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)mesh.Count);
        }
    }

    private static float[] Build(Island island)
    {
        var random = new Random((int)(island.Seed * 1000));
        int rows = 1 + TopRings + UnderRings;
        var positions = new Vector3[rows, Segments];
        var colors = new Vector3[rows, Segments];
        var c = island.Center;

        for (int s = 0; s < Segments; s++)
        {
            float angle = s * MathF.Tau / Segments;
            float edge = IslandField.EdgeRadius(island, angle);
            var dir = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));

            // The grassy top, from the centre out to the edge.
            for (int ring = 0; ring <= TopRings; ring++)
            {
                float f = ring / (float)TopRings;
                var p = c + dir * edge * f;
                p.Y = IslandField.TopHeight(island, f);
                positions[ring, s] = p;
                colors[ring, s] = GroundMaterials.GrassColor(p.X, p.Z) * (1.05f - 0.2f * f * f);
            }

            // The underside, down to the tip: dirt under the lip, then warm layered rock that
            // darkens and turns lilac toward the bottom.
            for (int k = 1; k <= UnderRings; k++)
            {
                float t = k / (float)UnderRings;
                // The wobble grows in below the lip, so the rock never juts out past the edge of
                // the top (where the waterfalls pour over).
                float wobble = 1f + (0.15f * MathF.Sin(4 * angle + t * 7 + island.Seed) + 0.08f * MathF.Sin(11 * angle - t * 5))
                                    * MathF.Min(t / 0.3f, 1f);
                float radius = edge * MathF.Pow(1 - t, 0.75f) * wobble;
                float y = c.Y - 0.6f - island.Depth * MathF.Pow(t, 1.15f) + 1.5f * MathF.Sin(angle * 3 + t * 5 + island.Seed) * t * (1 - t);
                positions[TopRings + k, s] = new Vector3(c.X, y, c.Z) + dir * radius;

                float strata = 0.5f + 0.5f * MathF.Sin(y * 0.5f + MathF.Sin(angle * 2 + island.Seed) * 2);
                var rock = Vector3.Lerp(new Vector3(0.66f, 0.52f, 0.44f), new Vector3(0.82f, 0.69f, 0.57f), strata) * (1 - 0.45f * t);
                rock = Vector3.Lerp(rock, new Vector3(0.45f, 0.34f, 0.62f), t * t * 0.6f);
                colors[TopRings + k, s] = k == 1 ? new Vector3(0.42f, 0.31f, 0.22f) : rock;
            }
        }

        // Smooth normals: accumulate the normals of the faces around each vertex.
        var normals = new Vector3[rows, Segments];
        for (int r = 0; r < rows - 1; r++)
        for (int s = 0; s < Segments; s++)
        {
            int s1 = (s + 1) % Segments;
            var a = positions[r, s]; var b = positions[r, s1];
            var d = positions[r + 1, s]; var e = positions[r + 1, s1];
            var n1 = Vector3.Cross(b - a, d - a);
            var n2 = Vector3.Cross(e - b, d - b);
            normals[r, s] += n1; normals[r, s1] += n1 + n2; normals[r + 1, s] += n1 + n2; normals[r + 1, s1] += n2;
        }

        var mesh = new MeshBuilder();
        for (int r = 0; r < rows - 1; r++)
        for (int s = 0; s < Segments; s++)
        {
            int s1 = (s + 1) % Segments;
            // Angles turn from +X toward +Z (clockwise seen from above) and rows go outward then
            // downward, so (r, s) -> (r, s+1) -> (r+1, s) is counter-clockwise seen from outside.
            Add(r, s); Add(r, s1); Add(r + 1, s);
            Add(r, s1); Add(r + 1, s1); Add(r + 1, s);
        }

        // Glowing crystals hanging from the lower underside.
        int crystals = 3 + random.Next(4);
        for (int i = 0; i < crystals; i++)
        {
            int ring = TopRings + (int)(UnderRings * (0.35f + 0.45f * random.NextSingle()));
            var p = positions[ring, random.Next(Segments)];
            var color = random.NextSingle() < 0.5f ? new Vector3(0.7f, 0.45f, 1.0f) : new Vector3(0.4f, 0.85f, 1.0f);
            float length = 1.5f + 2.5f * random.NextSingle();
            mesh.Octahedron(p + new Vector3(0, 0.3f, 0), 0.25f + 0.3f * random.NextSingle(), -length, color, 1f,
                tilt: new Vector3(random.NextSingle() - 0.5f, 0, random.NextSingle() - 0.5f) * 0.3f);
        }
        return mesh.Vertices.ToArray();

        void Add(int r, int s) => mesh.Vertex(positions[r, s], Vector3.Normalize(normals[r, s] + new Vector3(0, 1e-5f, 0)), colors[r, s], 0f);
    }

    private Mesh Upload(float[] vertices)
    {
        var mesh = new Mesh { Vao = _gl.GenVertexArray(), Vbo = _gl.GenBuffer(), Count = vertices.Length / 10 };
        _gl.BindVertexArray(mesh.Vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, mesh.Vbo);
        fixed (float* p = vertices)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        uint stride = 10 * sizeof(float);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)(9 * sizeof(float)));
        _gl.EnableVertexAttribArray(3);
        _gl.BindVertexArray(0);
        return mesh;
    }

    public void Dispose()
    {
        foreach (var mesh in _meshes.Values)
        {
            if (mesh is null) continue;
            _gl.DeleteBuffer(mesh.Vbo);
            _gl.DeleteVertexArray(mesh.Vao);
        }
        _meshes.Clear();
    }
}
