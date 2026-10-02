using System.Numerics;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// The small waves on the water round the player: a height field <see cref="Size"/>² cells over
/// <see cref="Extent"/> metres (a cell an eighth of a metre) following the camera, moved on by the
/// wave equation on the GPU at a fixed <see cref="StepsPerSecond"/>: waves spread at
/// <see cref="WaveSpeed"/>, die away, and are soaked up at the edges. Whatever stands or moves in
/// the water pushes it (<see cref="Step"/>'s sources): the swimmer's wake, a V when swimming faster
/// than the waves, rings round the reeds, lotus and blocks. A swimmer also leaves foam (blue), which
/// spreads a little and fades away over a couple of seconds (<see cref="FoamFade"/>). The water
/// shader reads the slope and the foam from it (<c>rippleSlope</c>, <c>rippleFoam</c>, texture unit
/// <see cref="Unit"/>). Red holds the height now, green a step ago, blue the foam.
/// </summary>
public sealed unsafe class RippleSim : IDisposable
{
    public const int Unit = 10;
    public const int Size = 512;
    public const float Extent = 64f;
    public const float Cell = Extent / Size;
    public const int MaxSources = 48;
    private const float StepsPerSecond = 60f;
    private const float WaveSpeed = 0.7f;   // metres a second
    private const float Damping = 0.994f;   // per step: half gone in about two seconds
    private const int MaxStepsPerFrame = 3;
    private const float FoamFade = 0.995f;  // per step: half gone in about two seconds and a half

    private const string Vertex = """
        #version 330 core
        void main()
        {
            vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private static readonly string Fragment = $$"""
        #version 330 core
        uniform sampler2D uPrevious;  // red: height now, green: a step ago
        uniform ivec2 uShift;         // cells the field moved since the last step
        uniform vec2 uOrigin;         // world xz of the corner, now
        uniform float uCourant;       // (speed * step / cell)^2
        uniform float uDamping;
        uniform float uFoamFade;
        out vec4 FragColor;

        const int N = {{Size}};
        const float Cell = {{Cell.ToString(System.Globalization.CultureInfo.InvariantCulture)}};

        vec4 at(ivec2 c)
        {
            if (c.x < 0 || c.y < 0 || c.x >= N || c.y >= N) return vec4(0.0);
            return texelFetch(uPrevious, c, 0);
        }

        void main()
        {
            ivec2 cell = ivec2(gl_FragCoord.xy);
            ivec2 c = cell + uShift; // where this cell was before the field moved
            vec4 h = at(c);
            vec4 r = at(c + ivec2(1, 0)), l = at(c - ivec2(1, 0)), u = at(c + ivec2(0, 1)), d = at(c - ivec2(0, 1));
            float around = r.r + l.r + u.r + d.r;
            // The foam spreads a little into its neighbours as it fades.
            float foam = mix(h.b, (r.b + l.b + u.b + d.b) * 0.25, 0.08) * uFoamFade;
            // (A slight pull back to the rest level, so nothing is left raised or sunk.)
            float next = (2.0 * h.r - h.g + uCourant * (around - 4.0 * h.r) - 0.002 * h.r) * uDamping;
            // The edges soak the waves up, so none comes back from them.
            float edge = float(min(min(cell.x, cell.y), min(N - 1 - cell.x, N - 1 - cell.y)));
            float soak = smoothstep(0.0, 24.0, edge);
            FragColor = vec4(next * soak, h.r, foam * soak, 0.0);
        }
        """;

    // The sources, added after each step: a quad round each (instanced, no vertex data) with a
    // bell of push, blended onto the height (only the cells near a source pay for it).
    private static readonly string SourceVertex = $$"""
        #version 330 core
        uniform vec4 uSource[{{MaxSources}}]; // x, z, radius, how much it pushes the water this step
        uniform float uSourceFoam[{{MaxSources}}]; // how much foam it leaves this step
        uniform vec2 uOrigin;
        out vec2 vOffset; // from the source, in radii
        out float vPush;
        out float vFoam;
        void main()
        {
            vec4 s = uSource[gl_InstanceID];
            vec2 corner = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2) - 1.0; // a triangle covering the square -1..1
            corner = corner * 2.0 + 1.0;
            vOffset = corner * 3.0;
            vPush = s.w;
            vFoam = uSourceFoam[gl_InstanceID];
            vec2 world = s.xy + vOffset * s.z;
            gl_Position = vec4((world - uOrigin) / {{Extent.ToString(System.Globalization.CultureInfo.InvariantCulture)}} * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string SourceFragment = """
        #version 330 core
        in vec2 vOffset;
        in float vPush;
        in float vFoam;
        out vec4 FragColor;
        void main()
        {
            float bell = exp(-dot(vOffset, vOffset));
            FragColor = vec4(vPush * bell, 0.0, vFoam * bell, 0.0);
        }
        """;

    private readonly GL _gl;
    private readonly Shader _shader, _sourceShader;
    private readonly uint _vao;
    private readonly uint[] _textures = new uint[2], _fbos = new uint[2];
    private readonly string[] _sourceNames = Enumerable.Range(0, MaxSources).Select(i => $"uSource[{i}]").ToArray();
    private readonly string[] _foamNames = Enumerable.Range(0, MaxSources).Select(i => $"uSourceFoam[{i}]").ToArray();
    private int _current;
    private (int X, int Z) _corner = (int.MinValue, int.MinValue);
    private float _pending;

    /// <summary>World xz of the field's corner.</summary>
    public Vector2 Origin => new(_corner.X * Cell, _corner.Z * Cell);

    public RippleSim(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, Vertex, Fragment);
        _sourceShader = new Shader(gl, SourceVertex, SourceFragment);
        _vao = gl.GenVertexArray();
        for (int i = 0; i < 2; i++)
        {
            _textures[i] = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, _textures[i]);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, Size, Size, 0, PixelFormat.Rgba, PixelType.HalfFloat, (void*)0);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _fbos[i] = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbos[i]);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _textures[i], 0);
            gl.ClearColor(0f, 0f, 0f, 0f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
        }
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>
    /// Moves the waves on by <paramref name="dt"/> seconds (in fixed steps), the field centred on
    /// <paramref name="center"/>. <paramref name="sources"/> (x, z, radius, push per second) push
    /// the water at every step, each leaving <paramref name="foam"/> (per second) too. Binds the
    /// result to <see cref="Unit"/>; leaves its framebuffer bound.
    /// </summary>
    public void Step(Vector3 center, float dt, ReadOnlySpan<Vector4> sources, ReadOnlySpan<float> foam)
    {
        _pending = MathF.Min(_pending + dt, MaxStepsPerFrame / StepsPerSecond);
        int steps = (int)(_pending * StepsPerSecond);
        _pending -= steps / StepsPerSecond;

        int count = Math.Min(sources.Length, MaxSources);
        _sourceShader.Use();
        for (int i = 0; i < count; i++)
        {
            _sourceShader.Set(_sourceNames[i], sources[i] with { W = sources[i].W / StepsPerSecond });
            _sourceShader.Set(_foamNames[i], i < foam.Length ? foam[i] / StepsPerSecond : 0f);
        }
        _shader.Use();
        _shader.Set("uPrevious", Unit);
        float courant = WaveSpeed / StepsPerSecond / Cell;
        _shader.Set("uCourant", courant * courant);
        _shader.Set("uDamping", Damping);
        _shader.Set("uFoamFade", FoamFade);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(false);
        _gl.Viewport(0, 0, Size, Size);
        _gl.BindVertexArray(_vao);
        for (int s = 0; s < steps; s++)
        {
            // The field follows the camera in whole cells; far jumps start it afresh.
            var corner = ((int)MathF.Floor(center.X / Cell) - Size / 2, (int)MathF.Floor(center.Z / Cell) - Size / 2);
            var shift = _corner.X == int.MinValue ? (Size, Size) : (corner.Item1 - _corner.X, corner.Item2 - _corner.Z);
            _corner = corner;
            _shader.Set("uShift", shift.Item1, shift.Item2);
            _shader.Set("uOrigin", Origin);
            int next = 1 - _current;
            // (Read through its own unit: unit 0 holds the shadow map, which the shaders drawn
            // after the water still sample.)
            _gl.ActiveTexture(TextureUnit.Texture0 + Unit);
            _gl.BindTexture(TextureTarget.Texture2D, _textures[_current]);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbos[next]);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            if (count > 0)
            {
                _sourceShader.Use();
                _sourceShader.Set("uOrigin", Origin);
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
                _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 3, (uint)count);
                _gl.Disable(EnableCap.Blend);
                _shader.Use();
            }
            _current = next;
        }
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
        _gl.ActiveTexture(TextureUnit.Texture0 + Unit);
        _gl.BindTexture(TextureTarget.Texture2D, _textures[_current]);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    public void Dispose()
    {
        _shader.Dispose();
        _sourceShader.Dispose();
        _gl.DeleteVertexArray(_vao);
        foreach (var fbo in _fbos) _gl.DeleteFramebuffer(fbo);
        foreach (var texture in _textures) _gl.DeleteTexture(texture);
    }
}
