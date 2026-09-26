using System.Numerics;
using Mine.Rendering;
using Mine.World;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Shader = Mine.Rendering.Shader;

namespace Mine;

public sealed class Game : IDisposable
{
    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUv;
        layout(location = 2) in float aLight;
        layout(location = 3) in vec3 aBlockLight;

        uniform mat4 uViewProj;

        out vec2 vUv;
        out float vLight;
        out vec3 vBlockLight;
        out vec3 vWorldPos;

        void main()
        {
            vUv = aUv;
            vLight = aLight;
            vBlockLight = aBlockLight;
            vWorldPos = aPos;
            gl_Position = uViewProj * vec4(aPos, 1.0);
        }
        """;

    private const string FragmentSource = "#version 330 core\n" + SkyRenderer.Glsl + """

        in vec2 vUv;
        in float vLight; // ambient occlusion
        in vec3 vBlockLight; // coloured light from glowing blocks, 0..1 per channel
        in vec3 vWorldPos;

        uniform sampler2D uAtlas; // alpha = glow mask
        uniform vec3 uCameraPos;
        uniform vec3 uAmbient;
        uniform vec3 uLightColor;
        uniform vec3 uLightDir;
        uniform sampler2DShadow uShadowMap;
        uniform mat4 uLightViewProj;
        uniform float uShadowTexel;
        uniform float uShadowBias;
        uniform float uFogStart;
        uniform float uFogEnd;
        uniform float uMistDensity; // soft aerial haze, per block of distance

        out vec4 FragColor;

        // 1 = fully lit, 0 = in shadow; 3x3 PCF taps for soft edges, fading out
        // toward the border of the area covered by the shadow map.
        float shadow(vec3 n)
        {
            vec4 lightSpace = uLightViewProj * vec4(vWorldPos + n * 0.04, 1.0);
            vec3 p = lightSpace.xyz / lightSpace.w * 0.5 + 0.5;
            float border = max(abs(p.x - 0.5), abs(p.y - 0.5)) * 2.0;
            if (border > 1.0 || p.z > 1.0) return 1.0;

            float sum = 0.0;
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
                sum += texture(uShadowMap, vec3(p.xy + vec2(x, y) * uShadowTexel, p.z - uShadowBias));
            return mix(sum / 9.0, 1.0, smoothstep(0.85, 1.0, border));
        }

        float valueNoise3(vec3 p)
        {
            vec3 i = floor(p), f = fract(p);
            vec3 u = f * f * (3.0 - 2.0 * f);
            float a = mix(hash13(i), hash13(i + vec3(1, 0, 0)), u.x);
            float b = mix(hash13(i + vec3(0, 1, 0)), hash13(i + vec3(1, 1, 0)), u.x);
            float c = mix(hash13(i + vec3(0, 0, 1)), hash13(i + vec3(1, 0, 1)), u.x);
            float d = mix(hash13(i + vec3(0, 1, 1)), hash13(i + vec3(1, 1, 1)), u.x);
            return mix(mix(a, b, u.y), mix(c, d, u.y), u.z);
        }

        // Gentle aerial haze: grows slowly with distance to soften contrast, never a wall.
        // A faint, slowly drifting variation keeps the air from looking flat.
        float mist(vec3 ro, vec3 rd, float dist)
        {
            vec3 p = ro + rd * dist * 0.5;
            float drift = 0.85 + 0.3 * valueNoise3(p * 0.05 + vec3(uTime * 0.03, 0.0, uTime * 0.02));
            return 1.0 - exp(-uMistDensity * dist * drift);
        }

        void main()
        {
            // Faces are flat, so screen-space derivatives give the exact normal.
            vec3 n = normalize(cross(dFdx(vWorldPos), dFdy(vWorldPos)));
            vec4 tex = texture(uAtlas, vUv);

            // Plain Lambert: faces turned away from the light get no direct light at all,
            // so the shadow map (unreliable on faces edge-on to the light) never matters there.
            float diffuse = max(dot(n, uLightDir), 0.0);
            // Sky light: each face also picks up the colour of the sky it faces,
            // so at sunset the sides turned to the sun glow orange.
            vec3 skyLight = skyColor(normalize(n + vec3(0.0, 0.6, 0.0)), false);
            vec3 ambient = mix(uAmbient, skyLight, 0.35) * (0.95 + 0.05 * n.y);
            // Ambient occlusion only darkens the light coming from the sky: applying it to
            // direct sunlight too would smear triangle-shaped gradients over sunlit faces.
            // Block light: a steep curve so it pools around the source and fades out gently.
            // Weaker in daylight, where the sun would drown it out anyway.
            vec3 blockLight = pow(vBlockLight, vec3(2.6)) * 1.5 * mix(0.35, 1.0, uNight);
            vec3 light = (ambient + blockLight) * vLight + uLightColor * diffuse * shadow(n);
            vec3 color = tex.rgb * light;

            float glow = tex.a;
            if (glow > 0.0)
            {
                // Glowing blocks ignore the daylight and pulse slowly, each out of phase.
                vec3 block = floor(vWorldPos - n * 0.01);
                float pulse = 0.5 + 0.5 * sin(uTime * 1.8 + dot(block, vec3(1.7, 2.3, 1.1)));
                color = mix(color, tex.rgb * (1.25 + 0.35 * pulse), glow);
            }

            vec3 toFragment = vWorldPos - uCameraPos;
            float dist = length(toFragment);
            vec3 rd = toFragment / dist;
            vec3 sky = skyColor(rd, false);
            float cutThrough = 1.0 - 0.7 * glow; // glowing blocks shine through the air

            // Soft haze: tints distant things with the hue of the air (golden at dawn, blue at
            // night) and flattens their contrast, but keeps their brightness, so dark trees
            // far away stay dark instead of turning white.
            const vec3 luma = vec3(0.3, 0.59, 0.11);
            vec3 airHue = sky / max(dot(sky, luma), 1e-3);
            float brightness = dot(color, luma);
            vec3 tinted = airHue * brightness;
            vec3 hazeTarget = mix(tinted, vec3(brightness), 0.4);
            color = mix(color, hazeTarget, mist(uCameraPos, rd, dist) * cutThrough);

            // Edge fog: only the last stretch before the end of the loaded world fades into
            // the sky, to hide where the terrain stops.
            float edge = smoothstep(uFogStart, uFogEnd, dist) * cutThrough;
            FragColor = vec4(toneMap(mix(color, sky, edge)), 1.0);
        }
        """;

    private const float MouseSensitivity = 0.0025f;
    private const float ReachDistance = 6f;
    private const double DoubleTapWindow = 0.3; // seconds between two W presses to start sprinting
    private const float FastTimeScale = 60f;     // holding T speeds up the day

    private readonly IWindow _window;
    private GL _gl = null!;
    private IInputContext _input = null!;
    private IKeyboard _keyboard = null!;
    private Shader _shader = null!;
    private TextureAtlas _atlas = null!;
    private Crosshair _crosshair = null!;
    private SkyRenderer _sky = null!;
    private ShadowMap _shadowMap = null!;
    private VoxelWorld _world = null!;
    private readonly Player _player = new();
    private readonly DayCycle _dayCycle = new();

    private Vector2? _lastMouse;
    private bool _mouseCaptured;
    private int _selectedSlot;
    private double _titleTimer;
    private int _frames;
    private double _time;
    private double _lastForwardTap = double.NegativeInfinity;
    private bool _sprinting; // latched by double-tapping W, lasts until W is released

    public Game()
    {
        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(1280, 720),
            Title = "Mine",
            VSync = true,
            // 3.3 core + forward compatible also runs on macOS (which caps at 4.1).
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
        };

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.FramebufferResize += OnFramebufferResize;
        _window.Closing += OnClosing;
    }

    public void Run() => _window.Run();

    private void OnLoad()
    {
        _gl = GL.GetApi(_window);
        _input = _window.CreateInput();
        _keyboard = _input.Keyboards[0];
        _keyboard.KeyDown += OnKeyDown;

        foreach (var mouse in _input.Mice)
        {
            mouse.MouseMove += OnMouseMove;
            mouse.MouseDown += OnMouseDown;
            mouse.Scroll += OnScroll;
        }
        SetMouseCaptured(true);

        _shader = new Shader(_gl, VertexSource, FragmentSource);
        _atlas = new TextureAtlas(_gl);
        _crosshair = new Crosshair(_gl);
        _sky = new SkyRenderer(_gl);
        _shadowMap = new ShadowMap(_gl);
        _world = new VoxelWorld(_gl, seed: 1337);

        Respawn();

        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        OnFramebufferResize(_window.FramebufferSize);
    }

    private void Respawn()
    {
        _player.Position = new Vector3(0.5f, _world.GetSurfaceHeight(0, 0), 0.5f);
        _player.Velocity = Vector3.Zero;
    }

    private void OnUpdate(double deltaTime)
    {
        _time += deltaTime;
        float dt = (float)Math.Min(deltaTime, 0.05); // avoid huge steps after a hitch

        var move = Vector2.Zero;
        if (_mouseCaptured)
        {
            if (_keyboard.IsKeyPressed(Key.W)) move.Y += 1;
            if (_keyboard.IsKeyPressed(Key.S)) move.Y -= 1;
            if (_keyboard.IsKeyPressed(Key.D)) move.X += 1;
            if (_keyboard.IsKeyPressed(Key.A)) move.X -= 1;
        }

        bool sneak = _mouseCaptured && !_player.Flying && _keyboard.IsKeyPressed(Key.ShiftLeft);
        if (move.Y <= 0 || sneak) _sprinting = false;

        _player.Update(_world, dt, move,
            up: _mouseCaptured && _keyboard.IsKeyPressed(Key.Space),
            down: _mouseCaptured && _keyboard.IsKeyPressed(Key.ShiftLeft),
            sprint: _sprinting || _keyboard.IsKeyPressed(Key.ControlLeft));

        if (_player.Position.Y < -30) Respawn();

        bool fastTime = _mouseCaptured && _keyboard.IsKeyPressed(Key.T);
        _dayCycle.Update((float)deltaTime * (fastTime ? FastTimeScale : 1f));

        _world.Update(_player.Position);
        UpdateTitle(deltaTime);
    }

    private void OnRender(double deltaTime)
    {
        _frames++;
        var size = _window.FramebufferSize;
        if (size.X == 0 || size.Y == 0) return; // minimised

        _gl.Clear(ClearBufferMask.DepthBufferBit); // the sky covers every pixel

        var eye = _player.Eye;
        var look = _player.LookDirection;
        var view = Matrix4x4.CreateLookAt(eye, eye + look, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            70f * MathF.PI / 180f, (float)size.X / size.Y, 0.05f, 1000f);

        var atmosphere = _dayCycle.Sample();
        _shadowMap.Render(_player.Position, atmosphere.LightDirection, _world.Chunks);
        _gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        float time = (float)_time;
        var skyView = Matrix4x4.CreateLookAt(Vector3.Zero, look, Vector3.UnitY);
        Matrix4x4.Invert(skyView * projection, out var inverseSkyViewProj);
        _sky.Draw(inverseSkyViewProj, eye, (float)_dayCycle.Elapsed, atmosphere, time);

        float fogEnd = VoxelWorld.RenderDistance * Chunk.Size - 8;
        _shader.Use();
        SkyRenderer.SetUniforms(_shader, atmosphere, time);
        _shader.Set("uViewProj", view * projection);
        _shader.Set("uCameraPos", eye);
        _shader.Set("uAmbient", atmosphere.Ambient);
        _shader.Set("uLightColor", atmosphere.LightColor);
        _shader.Set("uLightDir", atmosphere.LightDirection);
        _shader.Set("uFogStart", fogEnd * 0.75f); // only the last stretch, to hide the world's edge
        _shader.Set("uFogEnd", fogEnd);
        _shader.Set("uMistDensity", float.Lerp(0.006f, 0.012f, atmosphere.Haze)); // a bit more at dawn and dusk
        _shader.Set("uAtlas", 0);
        _atlas.Bind(0);
        _shader.Set("uShadowMap", 1);
        _shader.Set("uShadowTexel", 1f / ShadowMap.Size);
        _shader.Set("uShadowBias", ShadowMap.DepthBias);
        _shader.Set("uLightViewProj", _shadowMap.LightViewProjection);
        _shadowMap.Bind(1);

        foreach (var chunk in _world.Chunks)
            chunk.Mesh?.Draw();

        _crosshair.Draw();
    }

    private void OnFramebufferResize(Vector2D<int> size)
    {
        _gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        _crosshair.Resize(size.X, size.Y);
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        switch (key)
        {
            case Key.Escape when _mouseCaptured:
                SetMouseCaptured(false);
                break;
            case Key.Escape:
                _window.Close();
                break;
            case Key.W when _mouseCaptured:
                if (_time - _lastForwardTap <= DoubleTapWindow) _sprinting = true;
                _lastForwardTap = _time;
                break;
            case Key.F:
                _player.Flying = !_player.Flying;
                _player.Velocity = Vector3.Zero;
                break;
            case >= Key.Number0 and <= Key.Number9:
                int slot = key == Key.Number0 ? 9 : key - Key.Number1;
                if (slot < Blocks.Hotbar.Length) _selectedSlot = slot;
                break;
        }
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (!_mouseCaptured) return;
        if (_lastMouse is { } last)
        {
            var delta = position - last;
            _player.Look(delta.X * MouseSensitivity, -delta.Y * MouseSensitivity);
        }
        _lastMouse = position;
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        if (!_mouseCaptured)
        {
            SetMouseCaptured(true);
            return;
        }

        if (!_world.Raycast(_player.Eye, _player.LookDirection, ReachDistance, out var hit))
            return;

        if (button == MouseButton.Left)
            _world.SetBlock(hit.Block, BlockType.Air);
        else if (button == MouseButton.Right)
            PlaceBlock(hit);
    }

    /// <summary>Places the selected half block in the cell in front of the face hit.</summary>
    private void PlaceBlock(RayHit hit)
    {
        var target = hit.Adjacent;
        if (_world.GetBlock(target.X, target.Y, target.Z) == BlockType.Air && !_player.Intersects(target))
            _world.SetBlock(target, Blocks.Hotbar[_selectedSlot]);
    }

    private void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        if (!_mouseCaptured || wheel.Y == 0) return;
        int count = Blocks.Hotbar.Length;
        _selectedSlot = ((_selectedSlot - Math.Sign(wheel.Y)) % count + count) % count;
    }

    private void SetMouseCaptured(bool captured)
    {
        _mouseCaptured = captured;
        _lastMouse = null;
        foreach (var mouse in _input.Mice)
            mouse.Cursor.CursorMode = captured ? CursorMode.Raw : CursorMode.Normal;
    }

    private void UpdateTitle(double deltaTime)
    {
        _titleTimer += deltaTime;
        if (_titleTimer < 0.5) return;

        int fps = (int)(_frames / _titleTimer);
        var p = _player.Position;
        string mode = _player.Flying ? "volo" : "a piedi";
        if (_sprinting) mode += " (corsa)";
        else if (_player.Sneaking) mode += " (furtivo)";
        string hint = _mouseCaptured ? "" : " | clicca per giocare, Esc per uscire";
        var (hours, minutes) = _dayCycle.Clock;
        _window.Title = $"Mine | {Blocks.Hotbar[_selectedSlot]} | {mode} | ore {hours:00}:{minutes:00} | {fps} FPS | " +
                        $"{p.X:0} {p.Y:0} {p.Z:0}{hint}";
        _titleTimer = 0;
        _frames = 0;
    }

    // GL resources must be freed here, while the context is still current:
    // Run() destroys the window (and its context) before Dispose is called.
    private void OnClosing()
    {
        _world?.Dispose();
        _crosshair?.Dispose();
        _sky?.Dispose();
        _shadowMap?.Dispose();
        _atlas?.Dispose();
        _shader?.Dispose();
        _input?.Dispose();
    }

    public void Dispose() => _window.Dispose();
}
