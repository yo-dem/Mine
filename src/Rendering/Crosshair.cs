using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>A "+" in the middle of the screen that inverts the colours behind it.</summary>
public sealed unsafe class Crosshair : IDisposable
{
    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        void main() { gl_Position = vec4(aPos, 0.0, 1.0); }
        """;

    private const string FragmentSource = """
        #version 330 core
        out vec4 FragColor;
        void main() { FragColor = vec4(1.0); }
        """;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;

    public Crosshair(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.BindVertexArray(0);
    }

    /// <summary>Rebuilds the geometry so the crosshair keeps a fixed size in pixels.</summary>
    public void Resize(int width, int height)
    {
        if (width == 0 || height == 0) return;
        // NDC spans 2 units across the screen, so one pixel is 2/size.
        float px = 2f / width, py = 2f / height;
        float lx = 10 * px, ly = 10 * py; // half length: 10 px
        float tx = px, ty = py;           // half thickness: 1 px (2 px total)

        ReadOnlySpan<float> vertices =
        [
            -lx, -ty, lx, -ty, lx, ty, -lx, -ty, lx, ty, -lx, ty,   // horizontal bar
            -tx, -ly, tx, -ly, tx, -ty, -tx, -ly, tx, -ty, -tx, -ty, // vertical bar, below
            -tx, ty, tx, ty, tx, ly, -tx, ty, tx, ly, -tx, ly,      // vertical bar, above
        ];

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* data = vertices)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), data, BufferUsageARB.StaticDraw);
    }

    public void Draw()
    {
        _gl.Disable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.OneMinusDstColor, BlendingFactor.Zero);

        _shader.Use();
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 18);

        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }
}
