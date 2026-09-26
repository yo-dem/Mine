using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Procedural models for the world objects and their drawing. Each kind has one mesh in its
/// own local space (base at the origin); every object is drawn with its own model matrix.
/// Vertex layout: position (3), normal (3), colour (3), emissive (1). Emissive parts (the
/// lantern core, the torch flame, the crystals) glow on their own.
/// </summary>
public sealed unsafe class ObjectRenderer : IDisposable
{
    private const int FloatsPerVertex = 10;

    private readonly GL _gl;
    private readonly (uint Vao, uint Vbo, int Count)[] _meshes;

    public ObjectRenderer(GL gl)
    {
        _gl = gl;
        _meshes = Enum.GetValues<ObjectKind>().Select(kind => Upload(BuildMesh(kind))).ToArray();
    }

    public static Matrix4x4 ModelMatrix(WorldObject obj) =>
        Matrix4x4.CreateRotationY(obj.Yaw) * Matrix4x4.CreateTranslation(obj.Position);

    public void Draw(ObjectKind kind)
    {
        var (vao, _, count) = _meshes[(int)kind];
        _gl.BindVertexArray(vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)count);
    }

    private static List<float> BuildMesh(ObjectKind kind)
    {
        var m = new MeshBuilder();
        switch (kind)
        {
            case ObjectKind.Lantern:
            {
                var metal = new Vector3(0.18f, 0.15f, 0.13f);
                m.Box(new(-0.18f, 0f, -0.18f), new(0.18f, 0.05f, 0.18f), metal);         // base
                m.Box(new(-0.16f, 0.52f, -0.16f), new(0.16f, 0.58f, 0.16f), metal);       // roof
                m.Box(new(-0.07f, 0.58f, -0.07f), new(0.07f, 0.64f, 0.07f), metal);       // cap
                foreach (float x in new[] { -0.16f, 0.13f })
                foreach (float z in new[] { -0.16f, 0.13f })
                    m.Box(new(x, 0.05f, z), new(x + 0.03f, 0.52f, z + 0.03f), metal);      // posts
                m.Box(new(-0.11f, 0.08f, -0.11f), new(0.11f, 0.48f, 0.11f), new(1.0f, 0.78f, 0.45f), 1f); // flame core
                break;
            }
            case ObjectKind.Torch:
            {
                m.Box(new(-0.035f, 0f, -0.035f), new(0.035f, 1.1f, 0.035f), new(0.35f, 0.22f, 0.12f));      // stick
                m.Box(new(-0.07f, 1.02f, -0.07f), new(0.07f, 1.16f, 0.07f), new(0.12f, 0.09f, 0.07f));      // head
                m.Octahedron(new(0, 1.16f, 0), 0.09f, 0.34f, new(1.0f, 0.62f, 0.2f), 1f);                    // flame
                m.Octahedron(new(0, 1.18f, 0), 0.05f, 0.2f, new(1.0f, 0.9f, 0.6f), 1f);                      // hot core
                break;
            }
            case ObjectKind.Crystal:
            {
                var violet = new Vector3(0.66f, 0.46f, 1.0f);
                m.Octahedron(new(0, 0, 0), 0.2f, 1.25f, violet, 0.75f, tilt: new(0.08f, 0, 0.03f));
                m.Octahedron(new(0.22f, 0, 0.1f), 0.12f, 0.7f, violet * 0.9f, 0.75f, tilt: new(0.25f, 0, 0.1f));
                m.Octahedron(new(-0.16f, 0, 0.18f), 0.1f, 0.55f, violet * 1.1f, 0.75f, tilt: new(-0.2f, 0, 0.22f));
                m.Octahedron(new(0.02f, 0, -0.2f), 0.09f, 0.45f, violet, 0.75f, tilt: new(0.05f, 0, -0.3f));
                break;
            }
        }
        return m.Vertices;
    }

    private (uint, uint, int) Upload(List<float> vertices)
    {
        uint vao = _gl.GenVertexArray(), vbo = _gl.GenBuffer();
        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        var data = vertices.ToArray();
        fixed (float* p = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        uint stride = FloatsPerVertex * sizeof(float);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)(9 * sizeof(float)));
        _gl.EnableVertexAttribArray(3);
        _gl.BindVertexArray(0);
        return (vao, vbo, data.Length / FloatsPerVertex);
    }

    public void Dispose()
    {
        foreach (var (vao, vbo, _) in _meshes)
        {
            _gl.DeleteBuffer(vbo);
            _gl.DeleteVertexArray(vao);
        }
    }
}
