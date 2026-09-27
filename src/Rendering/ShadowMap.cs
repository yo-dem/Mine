using System.Numerics;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Depth map rendered from the sun (or moon), covering a square area around the player.
/// The terrain shader compares against it to find which fragments are in shadow.
/// </summary>
public sealed unsafe class ShadowMap : IDisposable
{
    public const int Size = 2048;
    public const float Radius = 150f; // half-width of the shadowed area, in metres
    private const float DepthRange = 256f; // metres in front of and behind the player, along the light

    /// <summary>
    /// Depth bias for the shadow test, in shadow-map depth units: five centimetres.
    /// Kept tiny so shadows stay attached to what casts them; acne on lit faces
    /// is handled by polygon offset and the shader's normal offset instead.
    /// </summary>
    public const float DepthBias = 0.05f / (4 * DepthRange); // window depth spans 2*DepthRange over [0.5, 1]

    // Two paths: plain meshes with a model matrix, and instanced trees placed and swayed exactly
    // as in the tree shader, so their shadows move with them.
    private const string VertexSource = "#version 330 core\n" + TerrainShaders.TreeTransform + """

        layout(location = 0) in vec3 aPos;
        layout(location = 4) in float aSway;
        layout(location = 5) in vec4 aInstance;
        layout(location = 6) in float aScale;
        uniform mat4 uLightViewProj;
        uniform mat4 uModel;
        uniform int uInstanced;
        uniform float uTime;

        void main()
        {
            vec3 world = uInstanced == 1
                ? treeWorld(aPos, aSway, aInstance, aScale, uTime, 0.0)
                : (uModel * vec4(aPos, 1.0)).xyz;
            gl_Position = uLightViewProj * vec4(world, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        void main() { }
        """;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _framebuffer;
    private readonly uint _texture;

    public Matrix4x4 LightViewProjection { get; private set; }

    private const float StepAngle = 0.0045f; // radians
    private Vector3 _direction = Vector3.UnitY;

    public ShadowMap(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);

        _texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _texture);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, Size, Size, 0,
            PixelFormat.DepthComponent, PixelType.Float, (void*)0);
        // Linear filtering + compare mode = hardware 2x2 PCF on sampler2DShadow.
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)GLEnum.CompareRefToTexture);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)GLEnum.Lequal);

        _framebuffer = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, _texture, 0);
        gl.DrawBuffer(DrawBufferMode.None);
        gl.ReadBuffer(ReadBufferMode.None);
        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException($"Shadow framebuffer incomplete: {status}");
    }

    /// <summary>
    /// Renders the scene around <paramref name="center"/> as seen from <paramref name="lightDirection"/>
    /// (pointing toward the light). <paramref name="drawCasters"/> draws the geometry within
    /// <see cref="Radius"/> plus a margin, with only positions at attribute 0. Leaves the shadow
    /// framebuffer unbound; the caller restores the viewport.
    /// </summary>
    public void Render(Vector3 center, Vector3 lightDirection, float time, Action drawCasters)
    {
        // The sun moves all the time: following it every frame would turn the shadow texel grid a
        // little each frame and make every shadow edge crawl. Follow it in small steps instead
        // (a quarter of a degree: a few centimetres at the tip of a tall tree's shadow).
        if (Vector3.Dot(lightDirection, _direction) < MathF.Cos(StepAngle)) _direction = lightDirection;
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, -_direction, Vector3.UnitY);

        // Snap the centre to whole shadow texels so the edges don't shimmer as the player moves.
        var c = Vector3.Transform(center, view);
        float texel = 2 * Radius / Size;
        c.X = MathF.Round(c.X / texel) * texel;
        c.Y = MathF.Round(c.Y / texel) * texel;
        var projection = Matrix4x4.CreateOrthographicOffCenter(
            c.X - Radius, c.X + Radius, c.Y - Radius, c.Y + Radius, -c.Z - DepthRange, -c.Z + DepthRange);
        LightViewProjection = view * projection;

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        _gl.Viewport(0, 0, Size, Size);
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        // Push the stored depth slightly away from the light, more on sloped surfaces,
        // so lit faces don't shadow themselves.
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(2f, 4f);

        _shader.Use();
        _shader.Set("uLightViewProj", LightViewProjection);
        _shader.Set("uModel", Matrix4x4.Identity);
        _shader.Set("uInstanced", 0);
        _shader.Set("uTime", time);
        drawCasters();

        _gl.Disable(EnableCap.PolygonOffsetFill);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>Model matrix for the casters drawn next; valid only inside <see cref="Render"/>'s callback.</summary>
    public void SetCasterModel(Matrix4x4 model) => _shader.Set("uModel", model);

    /// <summary>Switches the casters drawn next to the instanced tree layout; valid only inside <see cref="Render"/>'s callback.</summary>
    public void SetInstanced(bool instanced) => _shader.Set("uInstanced", instanced ? 1 : 0);

    public void Bind(int unit)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
    }

    public void Dispose()
    {
        _gl.DeleteFramebuffer(_framebuffer);
        _gl.DeleteTexture(_texture);
        _shader.Dispose();
    }
}
