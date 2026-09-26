using System.Numerics;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// The scene is rendered into an HDR framebuffer, then finished here:
/// <list type="bullet">
/// <item><b>Bloom</b>: bright parts are extracted with a soft threshold, blurred down a chain of
/// half-size textures and added back up (a wide, soft glow around lights and the sun).</item>
/// <item><b>Light shafts</b>: a radial blur toward the sun (or the moon) of the sky pixels near it,
/// so light streams past clouds, trees and rocks.</item>
/// <item><b>Grading</b>: exposure, tone mapping, saturation, split toning (cool shadows, warm
/// highlights), vignette and a little dither against banding.</item>
/// </list>
/// </summary>
public sealed unsafe class PostProcess : IDisposable
{
    private const int BloomLevels = 6;

    private const string VertexSource = """
        #version 330 core
        out vec2 vUv;
        void main()
        {
            vec2 ndc = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2) * 2.0 - 1.0;
            vUv = ndc * 0.5 + 0.5;
            gl_Position = vec4(ndc, 0.0, 1.0);
        }
        """;

    // Downsample with a 13-tap filter (Jimenez 2014); the first pass also applies the soft threshold.
    private const string DownsampleSource = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uSource;
        uniform vec2 uTexel;     // size of one source texel
        uniform int uPrefilter;  // 1 on the first pass
        uniform float uThreshold;
        out vec4 FragColor;

        vec3 tap(vec2 offset) { return texture(uSource, vUv + offset * uTexel).rgb; }

        void main()
        {
            vec3 a = tap(vec2(-2, 2)), b = tap(vec2(0, 2)), c = tap(vec2(2, 2));
            vec3 d = tap(vec2(-2, 0)), e = tap(vec2(0, 0)), f = tap(vec2(2, 0));
            vec3 g = tap(vec2(-2, -2)), h = tap(vec2(0, -2)), i = tap(vec2(2, -2));
            vec3 j = tap(vec2(-1, 1)), k = tap(vec2(1, 1)), l = tap(vec2(-1, -1)), m = tap(vec2(1, -1));
            vec3 color = e * 0.125 + (a + c + g + i) * 0.03125 + (b + d + f + h) * 0.0625 + (j + k + l + m) * 0.125;

            if (uPrefilter == 1)
            {
                color = min(color, vec3(40.0)); // tame single super-bright pixels
                float brightness = max(color.r, max(color.g, color.b));
                const float knee = 0.5;
                float soft = clamp(brightness - uThreshold + knee, 0.0, 2.0 * knee);
                soft = soft * soft / (4.0 * knee + 1e-4);
                color *= max(soft, brightness - uThreshold) / max(brightness, 1e-4);
            }
            FragColor = vec4(color, 1.0);
        }
        """;

    // Upsample with a 3x3 tent filter, added onto the next larger level.
    private const string UpsampleSource = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uSource;
        uniform vec2 uTexel;
        out vec4 FragColor;

        void main()
        {
            vec3 sum = texture(uSource, vUv).rgb * 4.0;
            sum += (texture(uSource, vUv + vec2(uTexel.x, 0)).rgb + texture(uSource, vUv - vec2(uTexel.x, 0)).rgb
                  + texture(uSource, vUv + vec2(0, uTexel.y)).rgb + texture(uSource, vUv - vec2(0, uTexel.y)).rgb) * 2.0;
            sum += texture(uSource, vUv + uTexel).rgb + texture(uSource, vUv - uTexel).rgb
                 + texture(uSource, vUv + vec2(uTexel.x, -uTexel.y)).rgb + texture(uSource, vUv + vec2(-uTexel.x, uTexel.y)).rgb;
            FragColor = vec4(sum / 16.0, 1.0);
        }
        """;

    // Light shafts: march from each pixel toward the light on screen, collecting the sky near it.
    private const string ShaftsSource = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uScene;
        uniform sampler2D uDepth;
        uniform vec2 uLightUv;     // light position on screen
        uniform float uAspect;
        uniform int uSamples;      // quality
        out vec4 FragColor;

        vec3 skyNearLight(vec2 uv)
        {
            if (texture(uDepth, uv).r < 0.99999) return vec3(0.0); // something is in the way
            vec2 d = (uv - uLightUv) * vec2(uAspect, 1.0);
            float nearLight = exp(-dot(d, d) * 18.0);
            return min(texture(uScene, uv).rgb, vec3(6.0)) * nearLight;
        }

        void main()
        {
            vec2 step = (uLightUv - vUv) / float(uSamples) * 0.9;
            vec2 uv = vUv;
            float weight = 1.0;
            float decay = pow(0.965, 48.0 / float(uSamples)); // same falloff whatever the sample count
            vec3 sum = vec3(0.0);
            for (int i = 0; i < 64; i++)
            {
                if (i >= uSamples) break;
                sum += skyNearLight(uv) * weight;
                weight *= decay;
                uv += step;
            }
            FragColor = vec4(sum / float(uSamples), 1.0);
        }
        """;

    private const string CompositeSource = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uScene;
        uniform sampler2D uBloom;
        uniform sampler2D uShafts;
        uniform float uBloomStrength;
        uniform vec3 uShaftColor;  // light colour times strength (0 when the light is off-screen)
        uniform float uExposure;
        uniform float uNight;
        out vec4 FragColor;

        // Soft shoulder above 0.7: bright colours saturate instead of clipping to white.
        vec3 toneMap(vec3 c)
        {
            vec3 over = max(c - 0.7, 0.0);
            return min(c, 0.7) + 0.3 * (1.0 - exp(-over / 0.3));
        }

        void main()
        {
            vec3 color = texture(uScene, vUv).rgb;
            color += texture(uBloom, vUv).rgb * uBloomStrength;
            color += texture(uShafts, vUv).rgb * uShaftColor;
            color = toneMap(color * uExposure);

            // Dreamy grade: richer colour, shadows drifting to teal-violet, highlights to gold.
            float luma = dot(color, vec3(0.3, 0.59, 0.11));
            color = mix(vec3(luma), color, 1.18);
            vec3 shadowTint = mix(vec3(0.02, 0.05, 0.09), vec3(0.05, 0.03, 0.10), uNight);
            color += shadowTint * (1.0 - smoothstep(0.0, 0.45, luma));
            color *= mix(vec3(1.0), vec3(1.05, 1.0, 0.92), smoothstep(0.5, 1.0, luma));

            // Vignette and a touch of dither.
            vec2 centered = vUv - 0.5;
            color *= 1.0 - dot(centered, centered) * 0.45;
            float noise = fract(sin(dot(gl_FragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453);
            color += (noise - 0.5) / 255.0;
            FragColor = vec4(clamp(color, 0.0, 1.0), 1.0);
        }
        """;

    private readonly GL _gl;
    private readonly Shader _down, _up, _shafts, _composite;
    private readonly uint _vao;
    private uint _sceneFbo, _sceneColor, _sceneDepth;
    private readonly uint[] _bloomFbo = new uint[BloomLevels];
    private readonly uint[] _bloomTex = new uint[BloomLevels];
    private readonly (int W, int H)[] _bloomSize = new (int, int)[BloomLevels];
    private uint _shaftFbo, _shaftTex;
    private int _width, _height;             // scene buffer size
    private int _screenWidth, _screenHeight; // window size

    public float BloomThreshold = 1.3f;
    public float BloomStrength = 0.55f;
    public float Exposure = 1.0f;

    /// <summary>Low quality (integrated GPUs): the scene is rendered at 70% resolution, with fewer light-shaft samples.</summary>
    public bool LowQuality
    {
        get => _lowQuality;
        set
        {
            if (_lowQuality == value) return;
            _lowQuality = value;
            Resize(_screenWidth, _screenHeight, force: true);
        }
    }
    private bool _lowQuality;

    /// <summary>Height of the scene buffer in pixels (for effects sized in pixels).</summary>
    public int SceneHeight => _height;

    public PostProcess(GL gl)
    {
        _gl = gl;
        _down = new Shader(gl, VertexSource, DownsampleSource);
        _up = new Shader(gl, VertexSource, UpsampleSource);
        _shafts = new Shader(gl, VertexSource, ShaftsSource);
        _composite = new Shader(gl, VertexSource, CompositeSource);
        _vao = gl.GenVertexArray();
    }

    /// <summary>(Re)creates the render targets for the window size.</summary>
    public void Resize(int screenWidth, int screenHeight, bool force = false)
    {
        if (screenWidth <= 0 || screenHeight <= 0) return;
        if (!force && screenWidth == _screenWidth && screenHeight == _screenHeight) return;
        DeleteTargets();
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
        float scale = _lowQuality ? 0.7f : 1f;
        int width = Math.Max(1, (int)(screenWidth * scale)), height = Math.Max(1, (int)(screenHeight * scale));
        _width = width;
        _height = height;

        _sceneColor = CreateTexture(width, height, InternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.HalfFloat);
        _sceneDepth = CreateTexture(width, height, InternalFormat.DepthComponent24, PixelFormat.DepthComponent, PixelType.UnsignedInt);
        _sceneFbo = CreateFramebuffer(_sceneColor, _sceneDepth);

        int w = width, h = height;
        for (int i = 0; i < BloomLevels; i++)
        {
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
            _bloomSize[i] = (w, h);
            _bloomTex[i] = CreateTexture(w, h, InternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.HalfFloat);
            _bloomFbo[i] = CreateFramebuffer(_bloomTex[i], 0);
        }

        _shaftTex = CreateTexture(width / 2, height / 2, InternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.HalfFloat);
        _shaftFbo = CreateFramebuffer(_shaftTex, 0);
    }

    /// <summary>Binds the HDR scene framebuffer; everything drawn until <see cref="Finish"/> goes there.</summary>
    public void BeginScene()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
    }

    /// <summary>
    /// Runs bloom, light shafts and grading into the default framebuffer.
    /// <paramref name="lightUv"/> is the sun (or moon) position on screen, <paramref name="shaftColor"/>
    /// its colour times strength (zero to skip the shafts).
    /// </summary>
    public void Finish(Vector2 lightUv, Vector3 shaftColor, float night)
    {
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.BindVertexArray(_vao);

        // Bloom: down the chain...
        _down.Use();
        _down.Set("uSource", 0);
        _down.Set("uThreshold", BloomThreshold);
        _gl.ActiveTexture(TextureUnit.Texture0);
        for (int i = 0; i < BloomLevels; i++)
        {
            var (w, h) = _bloomSize[i];
            var source = i == 0 ? _sceneColor : _bloomTex[i - 1];
            var (sw, sh) = i == 0 ? (_width, _height) : _bloomSize[i - 1];
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _bloomFbo[i]);
            _gl.Viewport(0, 0, (uint)w, (uint)h);
            _gl.BindTexture(TextureTarget.Texture2D, source);
            _down.Set("uTexel", new Vector2(1f / sw, 1f / sh));
            _down.Set("uPrefilter", i == 0 ? 1 : 0);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }

        // ...and back up, adding each level onto the larger one.
        _up.Use();
        _up.Set("uSource", 0);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        for (int i = BloomLevels - 1; i > 0; i--)
        {
            var (w, h) = _bloomSize[i - 1];
            var (sw, sh) = _bloomSize[i];
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _bloomFbo[i - 1]);
            _gl.Viewport(0, 0, (uint)w, (uint)h);
            _gl.BindTexture(TextureTarget.Texture2D, _bloomTex[i]);
            _up.Set("uTexel", new Vector2(1f / sw, 1f / sh));
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }
        _gl.Disable(EnableCap.Blend);

        // Light shafts at half resolution.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shaftFbo);
        _gl.Viewport(0, 0, (uint)(_width / 2), (uint)(_height / 2));
        bool shafts = shaftColor.LengthSquared() > 1e-6f;
        if (shafts)
        {
            _shafts.Use();
            _shafts.Set("uScene", 0);
            _shafts.Set("uDepth", 1);
            _shafts.Set("uLightUv", lightUv);
            _shafts.Set("uAspect", _width / (float)_height);
            _shafts.Set("uSamples", LowQuality ? 24 : 48);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _sceneColor);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, _sceneDepth);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }
        else
        {
            _gl.ClearColor(0, 0, 0, 1);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
        }

        // Composite to the screen (scaling the scene up if it was rendered smaller).
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)_screenWidth, (uint)_screenHeight);
        _composite.Use();
        _composite.Set("uScene", 0);
        _composite.Set("uBloom", 1);
        _composite.Set("uShafts", 2);
        _composite.Set("uBloomStrength", BloomStrength);
        _composite.Set("uShaftColor", shaftColor);
        _composite.Set("uExposure", Exposure);
        _composite.Set("uNight", night);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _sceneColor);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _bloomTex[0]);
        _gl.ActiveTexture(TextureUnit.Texture2);
        _gl.BindTexture(TextureTarget.Texture2D, _shaftTex);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.ActiveTexture(TextureUnit.Texture0);

        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
    }

    private uint CreateTexture(int width, int height, InternalFormat format, PixelFormat pixelFormat, PixelType type)
    {
        uint texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, format, (uint)width, (uint)height, 0, pixelFormat, type, (void*)0);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        return texture;
    }

    private uint CreateFramebuffer(uint color, uint depth)
    {
        uint fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, color, 0);
        if (depth != 0)
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, depth, 0);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException($"Post-process framebuffer incomplete: {status}");
        return fbo;
    }

    private void DeleteTargets()
    {
        if (_width == 0) return;
        _gl.DeleteFramebuffer(_sceneFbo);
        _gl.DeleteTexture(_sceneColor);
        _gl.DeleteTexture(_sceneDepth);
        for (int i = 0; i < BloomLevels; i++)
        {
            _gl.DeleteFramebuffer(_bloomFbo[i]);
            _gl.DeleteTexture(_bloomTex[i]);
        }
        _gl.DeleteFramebuffer(_shaftFbo);
        _gl.DeleteTexture(_shaftTex);
    }

    public void Dispose()
    {
        DeleteTargets();
        _gl.DeleteVertexArray(_vao);
        _down.Dispose();
        _up.Dispose();
        _shafts.Dispose();
        _composite.Dispose();
    }
}
