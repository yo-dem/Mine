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
    private const float ReachDistance = 6f;
    private const float ObjectDrawDistance = 350f;
    private const double DoubleTapWindow = 0.3; // seconds between two W presses to start sprinting
    private const float FastTimeScale = 60f;     // holding T speeds up the day
    private const float FieldOfView = 70f * MathF.PI / 180f;
    private const float NearPlane = 0.1f, FarPlane = 4000f;

    private readonly IWindow _window;
    private GL _gl = null!;
    private IInputContext _input = null!;
    private IKeyboard _keyboard = null!;
    private Shader _terrainShader = null!;
    private Shader _objectShader = null!;
    private Shader _grassShader = null!;
    private Shader _treeShader = null!;
    private GrassRenderer _grass = null!;
    private TreeField _treeField = null!;
    private TreeRenderer _trees = null!;
    private ObjectRenderer _objectRenderer = null!;
    private WorldObjects _objects = null!;
    private Crosshair _crosshair = null!;
    private SkyRenderer _sky = null!;
    private ShadowMap _shadowMap = null!;
    private TerrainField _terrainField = null!;
    private TerrainRenderer _terrain = null!;
    private IslandField _islands = null!;
    private IslandRenderer _islandRenderer = null!;
    private WaterfallRenderer _waterfalls = null!;
    private Ground _ground = null!;
    private Creatures _creatures = null!;
    private CreatureRenderer _creatureRenderer = null!;
    private Shader _creatureShader = null!;
    private CloudNoise _cloudNoise = null!;
    private MoteRenderer _motes = null!;
    private RainRenderer _rain = null!;
    private readonly Weather _weather = new();
    private PostProcess _post = null!;
    private GpuProfiler _profiler = null!;
    private Shader _waterShader = null!;
    private WaterRenderer _water = null!;
    private readonly Player _player = new();
    private readonly DayCycle _dayCycle = new();
    private readonly PointLight[] _lights = new PointLight[TerrainShaders.MaxPointLights];
    private int _lightCount;

    // Inventory: how many objects of each kind the player carries, and which one is in hand.
    private readonly int[] _inventory = [3]; // crystals
    private ObjectKind _selected = ObjectKind.Crystal;
    private WorldObject? _aimed; // the object under the crosshair, within reach

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
            VSync = Environment.GetEnvironmentVariable("MINE_NO_VSYNC") != "1",
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

        _terrainShader = new Shader(_gl, TerrainShaders.TerrainVertex, TerrainShaders.TerrainFragment);
        _objectShader = new Shader(_gl, TerrainShaders.ObjectVertex, TerrainShaders.ObjectFragment);
        _objectRenderer = new ObjectRenderer(_gl);
        _grassShader = new Shader(_gl, TerrainShaders.GrassVertex, TerrainShaders.GrassFragment);
        _treeShader = new Shader(_gl, TerrainShaders.TreeVertex, TerrainShaders.TreeFragment);
        _trees = new TreeRenderer(_gl);
        _crosshair = new Crosshair(_gl);
        _sky = new SkyRenderer(_gl);
        _shadowMap = new ShadowMap(_gl);
        _terrainField = new TerrainField(seed: 1337);
        _terrain = new TerrainRenderer(_gl, _terrainField);
        _objects = new WorldObjects(_terrainField, seed: 1337);
        _grass = new GrassRenderer(_gl, _terrainField);
        _treeField = new TreeField(_terrainField, seed: 1337);
        _islands = new IslandField(_terrainField, seed: 1337);
        _islandRenderer = new IslandRenderer(_gl);
        _waterfalls = new WaterfallRenderer(_gl);
        _ground = new Ground(_terrainField, _islands);
        _creatures = new Creatures(_terrainField);
        _creatureRenderer = new CreatureRenderer(_gl);
        _creatureShader = new Shader(_gl, TerrainShaders.CreatureVertex, TerrainShaders.CreatureFragment);
        _cloudNoise = new CloudNoise(_gl);
        _motes = new MoteRenderer(_gl);
        _rain = new RainRenderer(_gl);
        _post = new PostProcess(_gl);
        _profiler = new GpuProfiler(_gl);
        _waterShader = new Shader(_gl, TerrainShaders.WaterVertex, TerrainShaders.WaterFragment);
        _water = new WaterRenderer(_gl);
        // Integrated GPUs get the lighter clouds and a lower render scale; Q switches at any time.
        string renderer = _gl.GetStringS(StringName.Renderer) ?? "";
        SetLowQuality(renderer.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                      || renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase));

        Respawn();
        if (Environment.GetEnvironmentVariable("MINE_RAIN") == "1") _weather.Toggle();
        if (float.TryParse(Environment.GetEnvironmentVariable("MINE_TIME"), System.Globalization.CultureInfo.InvariantCulture, out float timeOfDay))
            _dayCycle.TimeOfDay = timeOfDay;
        if (float.TryParse(Environment.GetEnvironmentVariable("MINE_PITCH"), System.Globalization.CultureInfo.InvariantCulture, out float pitch))
            _player.Pitch = pitch;
        if (float.TryParse(Environment.GetEnvironmentVariable("MINE_YAW"), System.Globalization.CultureInfo.InvariantCulture, out float yaw))
            _player.Yaw = yaw;

        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        OnFramebufferResize(_window.FramebufferSize);
    }

    // Debugging aids, from environment variables: MINE_FPS_LOG=1 prints the HUD line to the console,
    // MINE_RAIN=1 starts with rain, MINE_TIME=0.45 sets the time of day, MINE_PITCH=0.2 the view
    // pitch (radians), MINE_YAW=1.5 the view heading, MINE_POS=800,-300 spawns exactly there, MINE_GPU_PROFILE=1 prints the GPU time of each pass (GpuProfiler).
    private static readonly string Skip = Environment.GetEnvironmentVariable("MINE_SKIP") ?? "";
    private static bool On(string pass) => !Skip.Contains(pass);
    // Where the game starts: a meadow by a lake. MINE_POS overrides it.
    private static readonly Vector2 Spawn = new(-517f, 128f);
    private static readonly Vector2? SpawnAt = ParsePosition(Environment.GetEnvironmentVariable("MINE_POS"));
    private static Vector2? ParsePosition(string? text)
    {
        var parts = text?.Split(',');
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return parts is { Length: 2 } && float.TryParse(parts[0], culture, out float x) && float.TryParse(parts[1], culture, out float z)
            ? new Vector2(x, z) : null;
    }
    private static readonly bool LogHud = Environment.GetEnvironmentVariable("MINE_FPS_LOG") == "1";

    private void Respawn()
    {
        var spawn = SpawnAt ?? Spawn;
        _player.Position = new Vector3(spawn.X, _terrainField.Height(spawn.X, spawn.Y), spawn.Y);
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

        _player.Update(_ground, dt, move,
            up: _mouseCaptured && _keyboard.IsKeyPressed(Key.Space),
            down: _mouseCaptured && _keyboard.IsKeyPressed(Key.ShiftLeft),
            sprint: _sprinting || _keyboard.IsKeyPressed(Key.ControlLeft));

        bool fastTime = _mouseCaptured && _keyboard.IsKeyPressed(Key.T);
        _dayCycle.Update((float)deltaTime * (fastTime ? FastTimeScale : 1f));
        _weather.Update(dt);
        SkyRenderer.Rain = _weather.Rain;

        int islandVersion = _islands.Version;
        _islands.Update(_player.Position);
        if (_islands.Version != islandVersion) _treeField.SetFixedTrees("islands", _islands.Trees);
        _islandRenderer.Update(_islands);
        _waterfalls.Update(_islands);
        _treeField.Update(_player.Position);
        _treeField.ResolveCollision(ref _player.Position, 0.35f);
        _terrain.Update(_player.Eye);
        _trees.Update(_treeField, _player.Eye);
        _grass.Update(_player.Eye);
        _objects.Update(_player.Position);
        _creatures.Update(_player.Position, _dayCycle.Sample(), dt);
        _creatureRenderer.Update(_creatures);
        _aimed = _mouseCaptured ? _objects.Pick(_player.Eye, _player.LookDirection, ReachDistance, out _) : null;
        UpdateTitle(deltaTime);
    }

    private void OnRender(double deltaTime)
    {
        _frames++;
        var size = _window.FramebufferSize;
        if (size.X == 0 || size.Y == 0) return; // minimised

        var eye = _player.Eye;
        var look = _player.LookDirection;
        var view = Matrix4x4.CreateLookAt(eye, eye + look, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, (float)size.X / size.Y, NearPlane, FarPlane);

        _profiler.BeginFrame();
        _profiler.Section("ombre");
        var atmosphere = _dayCycle.Sample();
        float time = (float)_time;
        _lightCount = _objects.CollectLights(eye, time, _lights, _treeField.GlowingLights(eye, 80f).Concat(_treeField.FlowerLights(eye)).Concat(_waterfalls.Lights(eye)));
        var shadowCenter = _player.Position;
        if (On("shadow")) _shadowMap.Render(shadowCenter, atmosphere.LightDirection, time, () =>
        {
            _terrain.DrawNear(shadowCenter, ShadowMap.Radius * 1.5f);
            _shadowMap.SetInstanced(true);
            _trees.DrawShadowCasters();
            _shadowMap.SetInstanced(false);
            _shadowMap.SetCasterModel(Matrix4x4.Identity);
            _islandRenderer.Draw(); // floating islands throw their shadows on the land
            foreach (var obj in _objects.All)
            {
                if (Vector3.DistanceSquared(obj.Position, shadowCenter) > ShadowMap.Radius * ShadowMap.Radius) continue;
                _shadowMap.SetCasterModel(ObjectRenderer.ModelMatrix(obj));
                _objectRenderer.Draw(obj.Kind);
            }
        });
        // Everything from here to the post-process goes into the HDR scene buffer.
        _post.BeginScene();
        _gl.Clear(ClearBufferMask.DepthBufferBit); // the sky covers every pixel
        _cloudNoise.Bind(SkyRenderer.CloudNoiseUnit);
        var skyView = Matrix4x4.CreateLookAt(Vector3.Zero, look, Vector3.UnitY);
        var skyViewProjection = skyView * projection;

        _profiler.Section("terreno");
        _shadowMap.Bind(0);
        SetWorldUniforms(_terrainShader, view * projection, eye, atmosphere, time);
        if (On("terrain")) _terrain.Draw(eye, look);

        // Grass blades are seen from both sides.
        _profiler.Section("erba");
        SetWorldUniforms(_grassShader, view * projection, eye, atmosphere, time);
        _grassShader.Set("uGrassRadius", GrassRenderer.Radius);
        _gl.Disable(EnableCap.CullFace);
        if (On("grass")) _grass.Draw(eye, look);
        _gl.Enable(EnableCap.CullFace);

        _profiler.Section("alberi");
        SetWorldUniforms(_treeShader, view * projection, eye, atmosphere, time);
        if (On("trees")) _trees.Draw(look, FieldOfView, (float)size.X / size.Y);

        _profiler.Section("oggetti+creature+isole");
        SetWorldUniforms(_objectShader, view * projection, eye, atmosphere, time);
        foreach (var obj in _objects.All)
        {
            if (Vector3.DistanceSquared(obj.Position, eye) > ObjectDrawDistance * ObjectDrawDistance) continue;
            _objectShader.Set("uModel", ObjectRenderer.ModelMatrix(obj));
            _objectShader.Set("uGlow", WorldObjects.Glow(obj, time));
            _objectShader.Set("uHighlight", ReferenceEquals(obj, _aimed) ? 1f : 0f);
            _objectRenderer.Draw(obj.Kind);
        }

        // Butterflies, birds and fish (fish before the water, which then refracts them).
        SetWorldUniforms(_creatureShader, view * projection, eye, atmosphere, time);
        _creatureRenderer.Draw(_creatureShader);

        // Floating islands use the object shader, their vertices already in world space.
        _objectShader.Use();
        _objectShader.Set("uModel", Matrix4x4.Identity);
        _objectShader.Set("uGlow", 1f + 0.15f * MathF.Sin(time * 1.3f));
        _objectShader.Set("uHighlight", 0f);
        _islandRenderer.Draw();

        // A snapshot of the opaque scene: the water shows it beneath, the clouds read its depth.
        // (Taken before the sky: where the water covers the sky, it is deep enough to hide it.)
        _profiler.Section("copia");
        _post.SnapshotForWater(colorUnit: 3, depthUnit: 4);

        // The sky last, only where nothing covers it (see SkyRenderer), over the clouds ray-marched
        // at half resolution.
        _profiler.Section("nuvole");
        Matrix4x4.Invert(skyViewProjection, out var inverseSkyViewProj);
        bool underwater = eye.Y < TerrainField.WaterLevel;
        if (On("sky") && !underwater)
        {
            _sky.DrawClouds(inverseSkyViewProj, eye, (float)_dayCycle.Elapsed, atmosphere, time, depthUnit: 4, _post.SceneWidth, _post.SceneHeight);
            _post.BindScene();
        }
        _profiler.Section("cielo");
        if (On("sky")) _sky.Draw(inverseSkyViewProj, eye, (float)_dayCycle.Elapsed, atmosphere, time);

        // The water, over the snapshot.
        _profiler.Section("acqua");
        SetWorldUniforms(_waterShader, view * projection, eye, atmosphere, time);
        _waterShader.Set("uUnderColor", 3);
        _waterShader.Set("uUnderDepth", 4);
        _waterShader.Set("uScreenSize", _post.SceneSize);
        _waterShader.Set("uNear", NearPlane);
        _waterShader.Set("uFar", FarPlane);
        _waterShader.Set("uWaterLevel", TerrainField.WaterLevel);
        _waterShader.Set("uWaterExtent", WaterRenderer.Extent);
        if (On("water")) _water.Draw();

        // The islands' waterfalls and their ponds: translucent and glowing, over everything opaque.
        _profiler.Section("cascate");
        if (On("falls")) _waterfalls.Draw(view * projection, eye, atmosphere.Night, time);

        _profiler.Section("pulviscolo+pioggia");
        float heightAboveGround = eye.Y - _ground.Height(eye.X, eye.Z, eye.Y);
        float pointScale = _post.SceneHeight / (2f * MathF.Tan(FieldOfView / 2));
        _motes.Draw(view * projection, eye, atmosphere, time, heightAboveGround, pointScale);
        if (eye.Y > TerrainField.WaterLevel) _rain.Draw(view * projection, eye, time, _weather.Rain, atmosphere.Night, pointScale);

        _profiler.Section("post");
        if (On("post")) _post.Finish(atmosphere.Night);

        _crosshair.Draw();
        _profiler.EndFrame();
    }

    /// <summary>Binds a world shader and sets everything it needs for this frame.</summary>
    private void SetWorldUniforms(Shader shader, Matrix4x4 viewProjection, Vector3 eye, in Atmosphere atmosphere, float time)
    {
        const float fogEnd = TerrainRenderer.ViewDistance;
        shader.Use();
        SkyRenderer.SetUniforms(shader, atmosphere, time, (float)_dayCycle.Elapsed);
        shader.Set("uViewProj", viewProjection);
        shader.Set("uCameraPos", eye);
        shader.Set("uAmbient", atmosphere.Ambient);
        shader.Set("uUnderwater", eye.Y < TerrainField.WaterLevel ? 1f : 0f);
        shader.Set("uLightColor", atmosphere.LightColor * (1f - 0.45f * _weather.Rain)); // the rain veils the sun
        shader.Set("uLightDir", atmosphere.LightDirection);
        shader.Set("uFogStart", fogEnd * 0.75f); // only the last stretch, to hide the world's edge
        shader.Set("uFogEnd", fogEnd);
        shader.Set("uMistDensity", float.Lerp(0.0012f, 0.0025f, atmosphere.Haze) * (1f + 2.5f * _weather.Rain)); // more at dawn, dusk and in the rain
        shader.Set("uShadowMap", 0);
        shader.Set("uShadowTexel", 1f / ShadowMap.Size);
        shader.Set("uShadowBias", ShadowMap.DepthBias);
        shader.Set("uLightViewProj", _shadowMap.LightViewProjection);
        shader.Set("uPointCount", _lightCount);
        for (int i = 0; i < _lightCount; i++)
        {
            shader.Set($"uPointPos[{i}]", _lights[i].Position);
            shader.Set($"uPointColor[{i}]", _lights[i].Color);
            shader.Set($"uPointRadius[{i}]", _lights[i].Radius);
        }
    }

    private void OnFramebufferResize(Vector2D<int> size)
    {
        _gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        _crosshair.Resize(size.X, size.Y);
        _post.Resize(size.X, size.Y);
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
            case Key.Q:
                SetLowQuality(!_sky.LowQuality);
                break;
            case Key.Number2: // rain on / off
                _weather.Toggle();
                break;
            case Key.Number1: // debug: a shooting star across the view
                SkyRenderer.LaunchShootingStar((float)_time, _player.LookDirection);
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
        if (!_mouseCaptured)
        {
            SetMouseCaptured(true);
            return;
        }

        if (button == MouseButton.Left && _aimed is { } obj)
        {
            // Pick it up.
            _objects.Remove(obj);
            _inventory[(int)obj.Kind]++;
            _selected = obj.Kind;
            _aimed = null;
        }
        else if (button == MouseButton.Right && _inventory[(int)_selected] > 0
                 && _terrainField.Raycast(_player.Eye, _player.LookDirection, ReachDistance, out var ground))
        {
            // Place it in the middle of the tile aimed at (stepping back a hair from the hit point,
            // so a hit on a wall picks the tile in front of it), facing the player.
            var aimed = ground - _player.LookDirection * 0.05f;
            _objects.Place(_selected, _terrainField.TileCenter(aimed.X, aimed.Z), -_player.Yaw);
            _inventory[(int)_selected]--;
        }
    }

    private void SetLowQuality(bool low)
    {
        _sky.LowQuality = low;
        _post.LowQuality = low;
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
        string mode = _player.Flying ? "volo" : _player.Swimming ? "nuoto" : "a piedi";
        if (_sprinting) mode += " (corsa)";
        else if (_player.Sneaking) mode += " (furtivo)";
        string hint = _mouseCaptured ? "" : " | clicca per giocare, Esc per uscire";
        var (hours, minutes) = _dayCycle.Clock;
        string hand = $"{WorldObjects.Defs[(int)_selected].Name} x{_inventory[(int)_selected]}";
        if (_sky.LowQuality) mode += " | qualità bassa";
        if (_weather.Raining) mode += " | pioggia";
        _window.Title = $"Mine | {hand} | {mode} | ore {hours:00}:{minutes:00} | {fps} FPS | " +
                        $"{p.X:0} {p.Y:0} {p.Z:0}{hint}";
        if (LogHud) Console.WriteLine(_window.Title); // MINE_FPS_LOG=1: for measuring without a screen
        _titleTimer = 0;
        _frames = 0;
    }

    // GL resources must be freed here, while the context is still current:
    // Run() destroys the window (and its context) before Dispose is called.
    private void OnClosing()
    {
        _terrain?.Dispose();
        _islandRenderer?.Dispose();
        _waterfalls?.Dispose();
        _cloudNoise?.Dispose();
        _motes?.Dispose();
        _rain?.Dispose();
        _post?.Dispose();
        _waterShader?.Dispose();
        _creatureShader?.Dispose();
        _creatureRenderer?.Dispose();
        _water?.Dispose();
        _crosshair?.Dispose();
        _sky?.Dispose();
        _shadowMap?.Dispose();
        _terrainShader?.Dispose();
        _objectShader?.Dispose();
        _objectRenderer?.Dispose();
        _grassShader?.Dispose();
        _treeShader?.Dispose();
        _grass?.Dispose();
        _trees?.Dispose();
        _input?.Dispose();
    }

    public void Dispose() => _window.Dispose();
}
