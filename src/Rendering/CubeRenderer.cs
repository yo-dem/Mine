using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Draws the debris (<see cref="DebrisCube"/>: chips and material cubes) instanced, one draw per
/// model: the chip cube and each material's model (<see cref="ItemMeshes"/>), with
/// <see cref="TerrainShaders.CubeVertex"/> and the object fragment shader. Models with glass
/// (<see cref="ItemMeshes.GlassOf"/>: the glass cubes' panes) draw it later, in the see-through pass
/// (<see cref="DrawGlass"/>, with the instances uploaded by <see cref="Draw"/>).
/// </summary>
public sealed unsafe class CubeRenderer : IDisposable
{
    private const int FloatsPerVertex = 10;
    private const int FloatsPerInstance = 11; // position, size, rotation, tint

    private readonly GL _gl;
    private readonly (uint Vao, uint Mesh, uint Instances, int Count)[] _models;
    private readonly List<float>[] _batches;
    private readonly (uint Vao, uint Mesh, int Count)?[] _glass; // per model, sharing its instance buffer

    public CubeRenderer(GL gl)
    {
        _gl = gl;
        var meshes = new List<List<float>> { ItemMeshes.Chip() };
        meshes.AddRange(Enum.GetValues<Resource>().Select(ItemMeshes.Item));
        _models = meshes.Select(Create).ToArray();
        _batches = _models.Select(_ => new List<float>()).ToArray();
        _glass = new (uint, uint, int)?[_models.Length];
        foreach (var r in Enum.GetValues<Resource>())
            if (ItemMeshes.GlassOf(r, carried: false) is { } glass)
            {
                var (vao, mesh, _, count) = Create(glass, _models[1 + (int)r].Instances);
                _glass[1 + (int)r] = (vao, mesh, count);
            }
    }

    /// <summary>The see-through parts of the cubes drawn by the last <see cref="Draw"/>.</summary>
    public void DrawGlass()
    {
        for (int i = 0; i < _models.Length; i++)
        {
            if (_glass[i] is not var (vao, _, count) || _batches[i].Count == 0) continue;
            _gl.BindVertexArray(vao);
            _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, (uint)count, (uint)(_batches[i].Count / FloatsPerInstance));
        }
    }

    public void Draw(IReadOnlyList<DebrisCube> cubes)
    {
        foreach (var batch in _batches) batch.Clear();
        foreach (var c in cubes)
        {
            var b = _batches[c.Model];
            b.Add(c.Position.X); b.Add(c.Position.Y); b.Add(c.Position.Z); b.Add(c.Size);
            b.Add(c.Rotation.X); b.Add(c.Rotation.Y); b.Add(c.Rotation.Z); b.Add(c.Rotation.W);
            b.Add(c.Tint.X); b.Add(c.Tint.Y); b.Add(c.Tint.Z);
        }
        for (int i = 0; i < _models.Length; i++)
        {
            var data = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_batches[i]);
            if (data.Length == 0) continue;
            var (vao, _, instances, count) = _models[i];
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, instances);
            fixed (float* p = data)
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
            _gl.BindVertexArray(vao);
            _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, (uint)count, (uint)(data.Length / FloatsPerInstance));
        }
    }

    private (uint Vao, uint Mesh, uint Instances, int Count) Create(List<float> mesh) => Create(mesh, _gl.GenBuffer());

    private (uint Vao, uint Mesh, uint Instances, int Count) Create(List<float> mesh, uint instances)
    {
        uint vao = _gl.GenVertexArray(), vbo = _gl.GenBuffer();
        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        var data = mesh.ToArray();
        fixed (float* p = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        uint stride = FloatsPerVertex * sizeof(float);
        Attribute(0, 3, stride, 0);
        Attribute(1, 3, stride, 3);
        Attribute(2, 3, stride, 6);
        Attribute(3, 1, stride, 9);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, instances);
        uint instanceStride = FloatsPerInstance * sizeof(float);
        Attribute(4, 4, instanceStride, 0);
        Attribute(5, 4, instanceStride, 4);
        Attribute(6, 3, instanceStride, 8);
        _gl.VertexAttribDivisor(4, 1);
        _gl.VertexAttribDivisor(5, 1);
        _gl.VertexAttribDivisor(6, 1);
        _gl.BindVertexArray(0);
        return (vao, vbo, instances, data.Length / FloatsPerVertex);
    }

    private void Attribute(uint index, int size, uint stride, int offset)
    {
        _gl.VertexAttribPointer(index, size, VertexAttribPointerType.Float, false, stride, (void*)(offset * sizeof(float)));
        _gl.EnableVertexAttribArray(index);
    }

    public void Dispose()
    {
        foreach (var (vao, mesh, instances, _) in _models)
        {
            _gl.DeleteBuffer(mesh);
            _gl.DeleteBuffer(instances);
            _gl.DeleteVertexArray(vao);
        }
        foreach (var (vao, mesh, _) in _glass.OfType<(uint, uint, int)>())
        {
            _gl.DeleteBuffer(mesh);
            _gl.DeleteVertexArray(vao);
        }
    }
}
