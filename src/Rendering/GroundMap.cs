using System.Numerics;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// The slowly varying part of the terrain's materials around the camera, drawn by the GPU into a
/// texture the terrain shader reads instead of computing it per pixel: the grass colour of the
/// ground (<c>grassColorFor</c>, from the biome and hue noises) and the desert's share
/// (<c>desertWeight</c>), one texel per 2 m (a terrain tile). Those were seven value noises a
/// pixel, most of the terrain's cost; they vary over tens of metres, so the texture's bilinear
/// filtering loses nothing. Redrawn when the camera has moved <see cref="Recenter"/> metres; past
/// its edge the shader computes them as before. The same GLSL (<c>Materials</c>) draws it, so the
/// two can never disagree.
/// </summary>
public sealed unsafe class GroundMap : IDisposable
{
    public const int Unit = 8;
    private const int Resolution = 1024;
    public const float Extent = 2048f;          // metres covered, centred on the camera (2 m a texel)
    private const float Recenter = 128f;        // redraw after moving this far (also the grid it snaps to)

    private readonly GL _gl;
    private readonly uint _texture, _fbo, _vao;
    private readonly Shader _shader;
    private Vector2 _origin = new(float.NaN);   // world xz of the texture's corner, once drawn

    public GroundMap(GL gl, string fragmentSource)
    {
        _gl = gl;
        _texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _texture);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, Resolution, Resolution, 0, PixelFormat.Rgba, PixelType.HalfFloat, (void*)0);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        _fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _texture, 0);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        _vao = gl.GenVertexArray(); // the full-screen triangle has no vertex data
        _shader = new Shader(gl, Vertex, fragmentSource);
    }

    /// <summary>World xz of the map's corner (NaN until the first map is drawn).</summary>
    public Vector2 Origin => _origin;

    /// <summary>
    /// Redraws the map around the camera if it has moved far enough. <paramref name="setUniforms"/>
    /// sets the world preset's material uniforms on the map shader. Leaves another framebuffer bound.
    /// </summary>
    public void Update(Vector3 camera, Action<Shader> setUniforms)
    {
        var corner = new Vector2(MathF.Round(camera.X / Recenter) * Recenter, MathF.Round(camera.Z / Recenter) * Recenter) - new Vector2(Extent / 2);
        if (corner == _origin) return;
        _origin = corner;

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, Resolution, Resolution);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _shader.Use();
        setUniforms(_shader);
        _shader.Set("uOrigin", corner);
        _shader.Set("uStep", Extent / Resolution);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.Enable(EnableCap.DepthTest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Bind()
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + Unit);
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    private const string Vertex = """
        #version 330 core
        void main()
        {
            vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    public void Dispose()
    {
        _shader.Dispose();
        _gl.DeleteFramebuffer(_fbo);
        _gl.DeleteTexture(_texture);
        _gl.DeleteVertexArray(_vao);
    }
}
