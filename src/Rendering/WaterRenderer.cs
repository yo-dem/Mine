using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// The sea and the lakes: a sheet at the water level that follows the camera, lifted by the swell
/// in <see cref="TerrainShaders.WaterVertex"/> and shaded by <see cref="TerrainShaders.WaterFragment"/>.
/// Where the terrain rises above the water it hides the sheet, so shores and lakes come out of the
/// depth test for free. The sheet is a grid of rings round the camera: <see cref="Segments"/> round
/// each, <see cref="NearSpacing"/> apart near the camera and then each a share
/// (<see cref="RingGrowth"/>) of its radius farther out, so the waves are finely drawn where they
/// can be seen and the far water costs little.
/// </summary>
public sealed unsafe class WaterRenderer : IDisposable
{
    /// <summary>Radius of the water sheet around the camera, a bit past the view distance.</summary>
    public const float Extent = TerrainRenderer.ViewDistance + 100f;

    private const int Segments = 256;
    private const float NearSpacing = 0.25f, RingGrowth = 0.025f;

    private readonly GL _gl;
    private readonly uint _vao, _vbo, _ebo;
    private readonly int _indexCount;

    public WaterRenderer(GL gl)
    {
        _gl = gl;
        var radii = new List<float> { 0f };
        while (radii[^1] < Extent)
            radii.Add(MathF.Min(radii[^1] + MathF.Max(NearSpacing, radii[^1] * RingGrowth), Extent));

        // The centre, then each ring's vertices (offsets from the camera in metres).
        var offsets = new List<float> { 0f, 0f };
        for (int r = 1; r < radii.Count; r++)
            for (int s = 0; s < Segments; s++)
            {
                float a = s * MathF.Tau / Segments;
                offsets.Add(MathF.Cos(a) * radii[r]);
                offsets.Add(MathF.Sin(a) * radii[r]);
            }
        var indices = new List<uint>();
        uint Ring(int r, int s) => (uint)(1 + (r - 1) * Segments + (s % Segments));
        for (int s = 0; s < Segments; s++)
        {
            indices.Add(0);
            indices.Add(Ring(1, s));
            indices.Add(Ring(1, s + 1));
        }
        for (int r = 1; r < radii.Count - 1; r++)
            for (int s = 0; s < Segments; s++)
            {
                uint a = Ring(r, s), b = Ring(r, s + 1), c = Ring(r + 1, s), d = Ring(r + 1, s + 1);
                indices.AddRange([a, c, b, b, c, d]);
            }
        _indexCount = indices.Count;

        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        _ebo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        var vertexData = offsets.ToArray();
        fixed (float* p = vertexData)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertexData.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        var indexData = indices.ToArray();
        fixed (uint* p = indexData)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indexData.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.BindVertexArray(0);
    }

    /// <summary>Draws the sheet (seen from above or below, so without face culling).</summary>
    public void Draw()
    {
        _gl.Disable(EnableCap.CullFace);
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)_indexCount, DrawElementsType.UnsignedInt, (void*)0);
        _gl.BindVertexArray(0);
        _gl.Enable(EnableCap.CullFace);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
    }
}
