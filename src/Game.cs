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

        uniform mat4 uViewProj;
        uniform vec3 uCameraPos;

        out vec2 vUv;
        out float vLight;
        out float vDistance;

        void main()
        {
            vUv = aUv;
            vLight = aLight;
            vDistance = length(aPos - uCameraPos);
            gl_Position = uViewProj * vec4(aPos, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        in vec2 vUv;
        in float vLight;
        in float vDistance;

        uniform sampler2D uAtlas;
        uniform vec3 uFogColor;
        uniform float uFogStart;
        uniform float uFogEnd;

        out vec4 FragColor;

        void main()
        {
            vec3 color = texture(uAtlas, vUv).rgb * vLight;
            float fog = clamp((vDistance - uFogStart) / (uFogEnd - uFogStart), 0.0, 1.0);
            FragColor = vec4(mix(color, uFogColor, fog), 1.0);
        }
        """;

    private static readonly Vector3 SkyColor = new(0.53f, 0.75f, 0.95f);
    private const float MouseSensitivity = 0.0025f;
    private const float ReachDistance = 6f;

    private readonly IWindow _window;
    private GL _gl = null!;
    private IInputContext _input = null!;
    private IKeyboard _keyboard = null!;
    private Shader _shader = null!;
    private TextureAtlas _atlas = null!;
    private Crosshair _crosshair = null!;
    private VoxelWorld _world = null!;
    private readonly Player _player = new();

    private Vector2? _lastMouse;
    private bool _mouseCaptured;
    private int _selectedSlot;
    private double _titleTimer;
    private int _frames;

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

        _shader = new Shader(_gl, VertexSource, FragmentSource);
        _atlas = new TextureAtlas(_gl);
        _crosshair = new Crosshair(_gl);
        _world = new VoxelWorld(_gl, seed: 1337);

        Respawn();

        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        OnFramebufferResize(_window.FramebufferSize);
    }

    private void Respawn()
    {
        int surface = _world.GetSurfaceY(0, 0);
        _player.Position = new Vector3(0.5f, surface + 1, 0.5f);
        _player.Velocity = Vector3.Zero;
    }

    private void OnUpdate(double deltaTime)
    {
        float dt = (float)Math.Min(deltaTime, 0.05); // avoid huge steps after a hitch

        var move = Vector2.Zero;
        if (_mouseCaptured)
        {
            if (_keyboard.IsKeyPressed(Key.W)) move.Y += 1;
            if (_keyboard.IsKeyPressed(Key.S)) move.Y -= 1;
            if (_keyboard.IsKeyPressed(Key.D)) move.X += 1;
            if (_keyboard.IsKeyPressed(Key.A)) move.X -= 1;
        }

        _player.Update(_world, dt, move,
            up: _mouseCaptured && _keyboard.IsKeyPressed(Key.Space),
            down: _mouseCaptured && _keyboard.IsKeyPressed(Key.ShiftLeft),
            sprint: _keyboard.IsKeyPressed(Key.ControlLeft));

        if (_player.Position.Y < -30) Respawn();

        _world.Update(_player.Position);
        UpdateTitle(deltaTime);
    }

    private void OnRender(double deltaTime)
    {
        _frames++;
        var size = _window.FramebufferSize;
        if (size.X == 0 || size.Y == 0) return; // minimised

        _gl.ClearColor(SkyColor.X, SkyColor.Y, SkyColor.Z, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        var eye = _player.Eye;
        var view = Matrix4x4.CreateLookAt(eye, eye + _player.LookDirection, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            70f * MathF.PI / 180f, (float)size.X / size.Y, 0.05f, 1000f);

        float fogEnd = VoxelWorld.RenderDistance * Chunk.Size - 8;
        _shader.Use();
        _shader.Set("uViewProj", view * projection);
        _shader.Set("uCameraPos", eye);
        _shader.Set("uFogColor", SkyColor);
        _shader.Set("uFogStart", fogEnd * 0.6f);
        _shader.Set("uFogEnd", fogEnd);
        _shader.Set("uAtlas", 0);
        _atlas.Bind(0);

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
            case Key.F:
                _player.Flying = !_player.Flying;
                _player.Velocity = Vector3.Zero;
                break;
            case >= Key.Number1 and <= Key.Number9:
                int slot = key - Key.Number1;
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

        if (!_world.Raycast(_player.Eye, _player.LookDirection, ReachDistance, out var hit, out var before))
            return;

        if (button == MouseButton.Left)
        {
            _world.SetBlock(hit, BlockType.Air);
        }
        else if (button == MouseButton.Right && !_player.Intersects(before))
        {
            _world.SetBlock(before, Blocks.Hotbar[_selectedSlot]);
        }
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
        string hint = _mouseCaptured ? "" : " | clicca per giocare, Esc per uscire";
        _window.Title = $"Mine | {Blocks.Hotbar[_selectedSlot]} | {mode} | {fps} FPS | " +
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
        _atlas?.Dispose();
        _shader?.Dispose();
        _input?.Dispose();
    }

    public void Dispose() => _window.Dispose();
}
