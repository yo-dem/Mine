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
    private const float MouseSensitivity = 0.0025f;
    private const double DoubleTapWindow = 0.3; // seconds between two W presses to start sprinting
    private const float FastTimeScale = 60f;     // holding T speeds up the day

    private readonly IWindow _window;
    private GL _gl = null!;
    private IInputContext _input = null!;
    private IKeyboard _keyboard = null!;
    private Shader _terrainShader = null!;
    private Crosshair _crosshair = null!;
    private SkyRenderer _sky = null!;
    private ShadowMap _shadowMap = null!;
    private TerrainField _terrainField = null!;
    private TerrainRenderer _terrain = null!;
    private readonly Player _player = new();
    private readonly DayCycle _dayCycle = new();

    private Vector2? _lastMouse;
    private bool _mouseCaptured;
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
        }
        SetMouseCaptured(true);

        _terrainShader = new Shader(_gl, TerrainShaders.Vertex, TerrainShaders.Fragment);
        _crosshair = new Crosshair(_gl);
        _sky = new SkyRenderer(_gl);
        _shadowMap = new ShadowMap(_gl);
        _terrainField = new TerrainField(seed: 1337);
        _terrain = new TerrainRenderer(_gl, _terrainField);

        Respawn();

        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        OnFramebufferResize(_window.FramebufferSize);
    }

    private void Respawn()
    {
        _player.Position = new Vector3(0, _terrainField.Height(0, 0), 0);
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

        _player.Update(_terrainField, dt, move,
            up: _mouseCaptured && _keyboard.IsKeyPressed(Key.Space),
            down: _mouseCaptured && _keyboard.IsKeyPressed(Key.ShiftLeft),
            sprint: _sprinting || _keyboard.IsKeyPressed(Key.ControlLeft));

        bool fastTime = _mouseCaptured && _keyboard.IsKeyPressed(Key.T);
        _dayCycle.Update((float)deltaTime * (fastTime ? FastTimeScale : 1f));

        _terrain.Update(_player.Eye);
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
            70f * MathF.PI / 180f, (float)size.X / size.Y, 0.1f, 4000f);

        var atmosphere = _dayCycle.Sample();
        float time = (float)_time;
        var shadowCenter = _player.Position;
        _shadowMap.Render(shadowCenter, atmosphere.LightDirection,
            () => _terrain.DrawNear(shadowCenter, ShadowMap.Radius * 1.5f));
        _gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        var skyView = Matrix4x4.CreateLookAt(Vector3.Zero, look, Vector3.UnitY);
        Matrix4x4.Invert(skyView * projection, out var inverseSkyViewProj);
        _sky.Draw(inverseSkyViewProj, eye, (float)_dayCycle.Elapsed, atmosphere, time);

        _shadowMap.Bind(0);
        SetWorldUniforms(_terrainShader, view * projection, eye, atmosphere, time);
        _terrain.Draw(eye, look);

        _crosshair.Draw();
    }

    /// <summary>Binds a world shader and sets everything it needs for this frame.</summary>
    private void SetWorldUniforms(Shader shader, Matrix4x4 viewProjection, Vector3 eye, in Atmosphere atmosphere, float time)
    {
        const float fogEnd = TerrainRenderer.ViewDistance;
        shader.Use();
        SkyRenderer.SetUniforms(shader, atmosphere, time);
        shader.Set("uViewProj", viewProjection);
        shader.Set("uCameraPos", eye);
        shader.Set("uAmbient", atmosphere.Ambient);
        shader.Set("uLightColor", atmosphere.LightColor);
        shader.Set("uLightDir", atmosphere.LightDirection);
        shader.Set("uFogStart", fogEnd * 0.75f); // only the last stretch, to hide the world's edge
        shader.Set("uFogEnd", fogEnd);
        shader.Set("uMistDensity", float.Lerp(0.0012f, 0.0025f, atmosphere.Haze)); // a bit more at dawn and dusk
        shader.Set("uShadowMap", 0);
        shader.Set("uShadowTexel", 1f / ShadowMap.Size);
        shader.Set("uShadowBias", ShadowMap.DepthBias);
        shader.Set("uLightViewProj", _shadowMap.LightViewProjection);
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
        if (!_mouseCaptured) SetMouseCaptured(true);
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
        _window.Title = $"Mine | {mode} | ore {hours:00}:{minutes:00} | {fps} FPS | " +
                        $"{p.X:0} {p.Y:0} {p.Z:0}{hint}";
        _titleTimer = 0;
        _frames = 0;
    }

    // GL resources must be freed here, while the context is still current:
    // Run() destroys the window (and its context) before Dispose is called.
    private void OnClosing()
    {
        _terrain?.Dispose();
        _crosshair?.Dispose();
        _sky?.Dispose();
        _shadowMap?.Dispose();
        _terrainShader?.Dispose();
        _input?.Dispose();
    }

    public void Dispose() => _window.Dispose();
}
