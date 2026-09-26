using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// The sea and the lakes: one flat quad at the water level that follows the camera, shaded by
/// <see cref="TerrainShaders.WaterFragment"/>. Where the terrain rises above the water level it hides
/// the quad, so shores and lakes come out of the depth test for free.
/// </summary>
public sealed unsafe class WaterRenderer : IDisposable
{
    /// <summary>Half-width of the water sheet around the camera, a bit past the view distance.</summary>
    public const float Extent = TerrainRenderer.ViewDistance + 100f;

    private readonly GL _gl;
    private readonly uint _vao, _vbo;

    public WaterRenderer(GL gl)
    {
        _gl = gl;
        float[] corners = [-1, -1, 1, -1, 1, 1, -1, -1, 1, 1, -1, 1];
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = corners)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(corners.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.BindVertexArray(0);
    }

    /// <summary>Draws the sheet (seen from above or below, so without face culling).</summary>
    public void Draw()
    {
        _gl.Disable(EnableCap.CullFace);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        _gl.Enable(EnableCap.CullFace);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
    }
}
