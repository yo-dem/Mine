using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Draws the creatures with instancing: one small procedural mesh per kind (wings, body, tail),
/// animated in <see cref="TerrainShaders.CreatureVertex"/> (flapping wings, a swishing tail) and
/// placed by an instance buffer refreshed every frame (position, heading, scale, phase).
/// Mesh vertex layout: position (3), colour (3), emissive (1), anim (1): for wings the side
/// (+1 / -1) they rotate around the body on, for fish how much the vertex swings with the tail.
/// </summary>
public sealed unsafe class CreatureRenderer : IDisposable
{
    private const int FloatsPerVertex = 8;
    private const int FloatsPerInstance = 6;

    private readonly GL _gl;
    private readonly (uint Vao, uint MeshVbo, uint InstanceVbo, int VertexCount)[] _batches;
    private readonly int[] _instanceCounts;
    private readonly List<float> _scratch = new();

    public CreatureRenderer(GL gl)
    {
        _gl = gl;
        var kinds = Enum.GetValues<CreatureKind>();
        _batches = kinds.Select(k => Create(BuildMesh(k))).ToArray();
        _instanceCounts = new int[kinds.Length];
    }

    public void Update(Creatures creatures)
    {
        foreach (var kind in Enum.GetValues<CreatureKind>())
        {
            _scratch.Clear();
            foreach (var c in creatures.Of(kind))
            {
                if (!c.Active) continue;
                _scratch.Add(c.Position.X);
                _scratch.Add(c.Position.Y);
                _scratch.Add(c.Position.Z);
                _scratch.Add(MathF.Atan2(c.Velocity.Z, c.Velocity.X));
                _scratch.Add(c.Scale);
                _scratch.Add(c.Phase);
            }
            _instanceCounts[(int)kind] = _scratch.Count / FloatsPerInstance;
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _batches[(int)kind].InstanceVbo);
            var data = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_scratch);
            fixed (float* p = data)
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
        }
    }

    /// <summary>Draws every kind with the creature shader, which must be bound (it reads <c>uKind</c>).</summary>
    public void Draw(Shader shader)
    {
        _gl.Disable(EnableCap.CullFace); // wings and fins are seen from both sides
        foreach (var kind in Enum.GetValues<CreatureKind>())
        {
            int count = _instanceCounts[(int)kind];
            if (count == 0) continue;
            shader.Set("uKind", (int)kind);
            var batch = _batches[(int)kind];
            _gl.BindVertexArray(batch.Vao);
            _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, (uint)batch.VertexCount, (uint)count);
        }
        _gl.Enable(EnableCap.CullFace);
    }

    private static List<float> BuildMesh(CreatureKind kind)
    {
        var m = new List<float>();
        void V(float x, float y, float z, Vector3 c, float emissive, float anim)
        {
            m.Add(x); m.Add(y); m.Add(z);
            m.Add(c.X); m.Add(c.Y); m.Add(c.Z);
            m.Add(emissive); m.Add(anim);
        }
        void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 color, float emissive, float anim)
        {
            V(a.X, a.Y, a.Z, color, emissive, anim);
            V(b.X, b.Y, b.Z, color, emissive, anim);
            V(c.X, c.Y, c.Z, color, emissive, anim);
        }

        switch (kind)
        {
            case CreatureKind.Butterfly:
            {
                // Four wings (fore and hind on each side), bright at the edges; the body along +X.
                var glow = Vector3.One;
                foreach (float side in new[] { 1f, -1f })
                {
                    Vector3 W(float x, float z) => new(x, 0, z * side);
                    Tri(W(0.03f, 0.01f), W(0.12f, 0.12f), W(0.02f, 0.2f), glow, 0.9f, side);   // forewing
                    Tri(W(0.03f, 0.01f), W(0.02f, 0.2f), W(-0.06f, 0.14f), glow, 0.75f, side);
                    Tri(W(-0.01f, 0.01f), W(-0.02f, 0.13f), W(-0.12f, 0.1f), glow, 0.7f, side); // hindwing
                }
                var body = new Vector3(0.15f, 0.1f, 0.25f);
                Tri(new(0.06f, 0, 0), new(-0.08f, 0.01f, 0.012f), new(-0.08f, 0.01f, -0.012f), body, 0.3f, 0);
                break;
            }
            case CreatureKind.Bird:
            {
                // A gull-like silhouette: swept wings with a bent elbow, a short tail.
                var c = Vector3.One;
                foreach (float side in new[] { 1f, -1f })
                {
                    Vector3 W(float x, float z) => new(x, 0, z * side);
                    Tri(W(0.12f, 0.02f), W(0.02f, 0.36f), W(-0.08f, 0.02f), c, 0.6f, side);
                    Tri(W(0.02f, 0.36f), W(-0.16f, 0.7f), W(-0.08f, 0.3f), c, 0.6f, side);
                }
                Tri(new(0.28f, 0, 0), new(-0.1f, 0.02f, 0.05f), new(-0.1f, 0.02f, -0.05f), c, 0.6f, 0);
                Tri(new(-0.1f, 0, 0.05f), new(-0.28f, 0, 0.1f), new(-0.28f, 0, -0.1f), c, 0.6f, 0);
                Tri(new(-0.1f, 0, 0.05f), new(-0.28f, 0, -0.1f), new(-0.1f, 0, -0.05f), c, 0.6f, 0);
                break;
            }
            case CreatureKind.Fish:
            {
                // A slim silvery body with coloured fins in the manner of the deep fish: a tall
                // sail of a dorsal fin, side fins that flutter, a keel fin beneath and a long forked
                // tail. Colour channel: body (1, 0, 0), fin (0, 1, how far toward the fin's tip);
                // the shader picks each fish's fin colour.
                var body = new Vector3(1, 0, 0);
                Vector3 head = new(0.28f, 0, 0), tail = new(-0.26f, 0, 0);
                Vector3 l = new(0.03f, 0, 0.075f), r = new(0.03f, 0, -0.075f), up = new(0.03f, 0.085f, 0), down = new(0.03f, -0.07f, 0);
                float Swing(Vector3 p) => Math.Clamp((0.2f - p.X) / 0.7f, 0f, 1f);
                void T(Vector3 a, Vector3 b, Vector3 d, float e)
                {
                    V(a.X, a.Y, a.Z, body, e, Swing(a));
                    V(b.X, b.Y, b.Z, body, e, Swing(b));
                    V(d.X, d.Y, d.Z, body, e, Swing(d));
                }
                void F(Vector3 a, float ta, Vector3 b, float tb, Vector3 d, float td)
                {
                    V(a.X, a.Y, a.Z, new(0, 1, ta), 0.45f, Swing(a));
                    V(b.X, b.Y, b.Z, new(0, 1, tb), 0.45f, Swing(b));
                    V(d.X, d.Y, d.Z, new(0, 1, td), 0.45f, Swing(d));
                }
                T(head, l, up, 0.3f); T(head, up, r, 0.3f); T(head, r, down, 0.3f); T(head, down, l, 0.3f);
                T(tail, up, l, 0.25f); T(tail, r, up, 0.25f); T(tail, down, r, 0.25f); T(tail, l, down, 0.25f);
                // Dorsal sail.
                F(new(0.14f, 0.075f, 0), 0, new(-0.02f, 0.24f, 0), 1, new(-0.16f, 0.2f, 0), 1);
                F(new(0.14f, 0.075f, 0), 0, new(-0.16f, 0.2f, 0), 1, new(-0.18f, 0.05f, 0), 0);
                // Side fins.
                foreach (float side in new[] { 1f, -1f })
                    F(new(0.13f, -0.02f, 0.06f * side), 0, new(-0.05f, -0.07f, 0.22f * side), 1, new(0.01f, -0.03f, 0.075f * side), 0.3f);
                // Keel fin.
                F(new(-0.02f, -0.06f, 0), 0, new(-0.2f, -0.17f, 0), 1, new(-0.2f, -0.04f, 0), 0);
                // Long forked tail.
                F(tail, 0, new(-0.6f, 0.2f, 0), 1, new(-0.46f, 0.02f, 0), 0.5f);
                F(tail, 0, new(-0.46f, -0.02f, 0), 0.5f, new(-0.6f, -0.2f, 0), 1);
                break;
            }
            case CreatureKind.DeepFish:
            {
                // A deep-bodied fish of the depths: a dim body with a tall dorsal fin, side fins and
                // a broad forked tail, and a row of glowing spots along each flank.
                var c = Vector3.One;
                Vector3 head = new(0.34f, 0.01f, 0), tail = new(-0.32f, 0, 0);
                Vector3 l = new(0.04f, 0, 0.09f), r = new(0.04f, 0, -0.09f), up = new(0.04f, 0.14f, 0), down = new(0.04f, -0.12f, 0);
                float Swing(Vector3 p) => Math.Clamp((0.15f - p.X) / 0.7f, 0f, 1f);
                void T(Vector3 a, Vector3 b, Vector3 d, float e)
                {
                    V(a.X, a.Y, a.Z, c, e, Swing(a));
                    V(b.X, b.Y, b.Z, c, e, Swing(b));
                    V(d.X, d.Y, d.Z, c, e, Swing(d));
                }
                T(head, l, up, 0.3f); T(head, up, r, 0.3f); T(head, r, down, 0.3f); T(head, down, l, 0.3f);
                T(tail, up, l, 0.25f); T(tail, r, up, 0.25f); T(tail, down, r, 0.25f); T(tail, l, down, 0.25f);
                // Dorsal fin, side fins, tail.
                T(new(0.12f, 0.12f, 0), new(-0.2f, 0.24f, 0), new(-0.22f, 0.06f, 0), 0.7f);
                T(new(0.14f, -0.04f, 0.07f), new(-0.02f, -0.1f, 0.2f), new(0.0f, -0.05f, 0.08f), 0.6f);
                T(new(0.14f, -0.04f, -0.07f), new(0.0f, -0.05f, -0.08f), new(-0.02f, -0.1f, -0.2f), 0.6f);
                T(tail, new(-0.55f, 0.16f, 0), new(-0.46f, 0, 0), 0.9f);
                T(tail, new(-0.46f, 0, 0), new(-0.55f, -0.16f, 0), 0.9f);
                // Glowing spots: small diamonds just outside each flank.
                for (int k = 0; k < 5; k++)
                {
                    float x = 0.22f - k * 0.1f, z = 0.085f - Math.Abs(x - 0.04f) * 0.12f + 0.01f;
                    foreach (float side in new[] { 1f, -1f })
                    {
                        Vector3 p0 = new(x + 0.025f, 0.01f, z * side), p1 = new(x, 0.035f, z * side), p2 = new(x - 0.025f, 0.01f, z * side), p3 = new(x, -0.015f, z * side);
                        T(p0, p1, p2, 1f); T(p0, p2, p3, 1f);
                    }
                }
                break;
            }
        }
        return m;
    }

    private (uint, uint, uint, int) Create(List<float> mesh)
    {
        uint vao = _gl.GenVertexArray(), meshVbo = _gl.GenBuffer(), instanceVbo = _gl.GenBuffer();
        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, meshVbo);
        var data = mesh.ToArray();
        fixed (float* p = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        uint stride = FloatsPerVertex * sizeof(float);
        Attribute(0, 3, stride, 0);
        Attribute(1, 3, stride, 3);
        Attribute(2, 1, stride, 6);
        Attribute(3, 1, stride, 7);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, instanceVbo);
        uint instanceStride = FloatsPerInstance * sizeof(float);
        Attribute(4, 4, instanceStride, 0); // position, heading
        Attribute(5, 2, instanceStride, 4); // scale, phase
        _gl.VertexAttribDivisor(4, 1);
        _gl.VertexAttribDivisor(5, 1);
        _gl.BindVertexArray(0);
        return (vao, meshVbo, instanceVbo, data.Length / FloatsPerVertex);
    }

    private void Attribute(uint index, int size, uint stride, int offsetFloats)
    {
        _gl.VertexAttribPointer(index, size, VertexAttribPointerType.Float, false, stride, (void*)(offsetFloats * sizeof(float)));
        _gl.EnableVertexAttribArray(index);
    }

    public void Dispose()
    {
        foreach (var (vao, meshVbo, instanceVbo, _) in _batches)
        {
            _gl.DeleteBuffer(meshVbo);
            _gl.DeleteBuffer(instanceVbo);
            _gl.DeleteVertexArray(vao);
        }
    }
}
