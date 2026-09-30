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
    private readonly (uint Vao, uint Vbo, int Count)[] _items;
    private readonly (uint Vao, uint Vbo, int Count)?[] _itemGlass; // the see-through part of each (ItemMeshes.GlassOf)

    public ObjectRenderer(GL gl)
    {
        _gl = gl;
        _meshes = Enum.GetValues<ObjectKind>().Select(kind => Upload(BuildMesh(kind))).ToArray();
        _items = Enum.GetValues<Resource>().Select(r => Upload(ItemMeshes.Carried(r))).ToArray();
        _itemGlass = Enum.GetValues<Resource>().Select(r => ItemMeshes.GlassOf(r, carried: true) is { } glass ? Upload(glass) : ((uint, uint, int)?)null).ToArray();
    }

    /// <summary>Whether a material's held model has a see-through part (drawn by <see cref="DrawItem"/> with <c>glass</c>).</summary>
    public bool HasGlass(Resource resource) => _itemGlass[(int)resource] is not null;

    /// <summary>
    /// Draws the model of a material as held (one unit across, centred on the origin: see
    /// <see cref="ItemMeshes.Carried"/>): its solid part, or with <paramref name="glass"/> its see-through part.
    /// </summary>
    public void DrawItem(Resource resource, bool glass = false)
    {
        if ((glass ? _itemGlass[(int)resource] : _items[(int)resource]) is not var (vao, _, count)) return;
        _gl.BindVertexArray(vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)count);
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
        foreach (var (vao, vbo, _) in _meshes.Concat(_items).Concat(_itemGlass.OfType<(uint, uint, int)>()))
        {
            _gl.DeleteBuffer(vbo);
            _gl.DeleteVertexArray(vao);
        }
    }
}
