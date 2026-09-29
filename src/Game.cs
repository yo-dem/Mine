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
    private Shader _blockShader = null!;
    private Shader _grassShader = null!;
    private Shader _treeShader = null!;
    private GrassRenderer _grass = null!;
    private TreeField _treeField = null!;
    private TreeRenderer _trees = null!;
    private ObjectRenderer _objectRenderer = null!;
    private WorldObjects _objects = null!;
    private BlockWorld _blocks = null!;
    private BlockRenderer _blockRenderer = null!;
    private BlockMask _blockMask = null!;
    private Drops _drops = null!;
    private Hud _hud = null!;
    private Crosshair _crosshair = null!;
    private SkyRenderer _sky = null!;
    private ShadowMap _shadowMap = null!;
    private TerrainField _terrainField = null!;
    private TerrainRenderer _terrain = null!;
    private IslandField _islands = null!;
    private IslandRenderer _islandRenderer = null!;
    private WaterfallRenderer _waterfalls = null!;
    private SeaFloorMap _seaFloor = null!;
    private Ground _ground = null!;
    private Creatures _creatures = null!;
    private CreatureRenderer _creatureRenderer = null!;
    private Shader _creatureShader = null!;
    private CloudNoise _cloudNoise = null!;
    private MoteRenderer _motes = null!;
    private RainRenderer _rain = null!;
    private SnowRenderer _snow = null!;
    private readonly Weather _weather = new();
    private PostProcess _post = null!;
    private GpuProfiler _profiler = null!;
    private Shader _waterShader = null!;
    private WaterRenderer _water = null!;
    private readonly Player _player = new();
    private readonly DayCycle _dayCycle = new();
    private readonly PointLight[] _lights = new PointLight[TerrainShaders.MaxPointLights];
    private int _lightCount;

    // What the player carries.
    private readonly Inventory _inventory = new();
    // What is under the crosshair, within reach: the nearest of an object or a block.
    private enum AimKind { None, Object, Block }
    private AimKind _aimKind;
    private WorldObject? _aimed;
    private Cell _aimCell;
    private Vector3 _aimNormal;
    private float _aimDistance;
    // Breaking a block takes holding the button on it: the block being broken and how far along.
    private const float BreakTime = 2.2f; // seconds
    private Cell? _breaking;
    private float _breakProgress;
    private float _flakeTimer;
    private Vector3? _aimGround; // where the view meets the terrain, when nothing nearer is aimed at
    private Cell? _placeCell;    // where the block in hand would go (its lowest corner cell)

    private Vector2? _lastMouse;
    private bool _mouseCaptured;
    private double _titleTimer;
    private int _frames;
    private double _time;
    private double _lastForwardTap = double.NegativeInfinity;
    private const double WeatherDoubleTap = 0.4; // seconds between two presses of 2 or 3 for a storm or a blizzard
    private double _lastRainTap = double.NegativeInfinity, _lastSnowTap = double.NegativeInfinity;
    private bool _sprinting; // latched by double-tapping W, lasts until W is released

    public Game()
    {
        var options = WindowOptions.Default with
        {
            Size = WindowedSize,
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
            mouse.Scroll += (_, wheel) =>
            {
                if (_mouseCaptured && wheel.Y != 0) _inventory.Cycle(wheel.Y > 0 ? -1 : 1);
            };
        }
        _inventory.Add(Item.Crystal, 3);
        SetMouseCaptured(true);

        _terrainShader = new Shader(_gl, TerrainShaders.TerrainVertex, TerrainShaders.TerrainFragment);
        _objectShader = new Shader(_gl, TerrainShaders.ObjectVertex, TerrainShaders.ObjectFragment);
        _blockShader = new Shader(_gl, TerrainShaders.BlockVertex, TerrainShaders.BlockFragment);
        _objectRenderer = new ObjectRenderer(_gl);
        _grassShader = new Shader(_gl, TerrainShaders.GrassVertex, TerrainShaders.GrassFragment);
        _treeShader = new Shader(_gl, TerrainShaders.TreeVertex, TerrainShaders.TreeFragment);
        _crosshair = new Crosshair(_gl);
        _hud = new Hud(_gl);
        _sky = new SkyRenderer(_gl);
        _shadowMap = new ShadowMap(_gl);
        if (int.TryParse(Environment.GetEnvironmentVariable("MINE_WORLD"), out int world) && world >= 1 && world <= WorldPreset.All.Length)
            WorldPreset.Current = WorldPreset.All[world - 1];
        BuildWorld();
        _creatureRenderer = new CreatureRenderer(_gl);
        _creatureShader = new Shader(_gl, TerrainShaders.CreatureVertex, TerrainShaders.CreatureFragment);
        _cloudNoise = new CloudNoise(_gl);
        _motes = new MoteRenderer(_gl);
        _rain = new RainRenderer(_gl);
        _snow = new SnowRenderer(_gl);
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
        if (Environment.GetEnvironmentVariable("MINE_SNOW") == "1") _weather.StartSnowed();
        if (Environment.GetEnvironmentVariable("MINE_STORM") == "1") _weather.StartStorm();
        if (Environment.GetEnvironmentVariable("MINE_BLIZZARD") == "1") _weather.StartSnowed(blizzard: true);
        if (float.TryParse(Environment.GetEnvironmentVariable("MINE_TIME"), System.Globalization.CultureInfo.InvariantCulture, out float timeOfDay))
            _dayCycle.TimeOfDay = timeOfDay;
        if (float.TryParse(Environment.GetEnvironmentVariable("MINE_PITCH"), System.Globalization.CultureInfo.InvariantCulture, out float pitch))
            _player.Pitch = pitch;
        if (float.TryParse(Environment.GetEnvironmentVariable("MINE_YAW"), System.Globalization.CultureInfo.InvariantCulture, out float yaw))
            _player.Yaw = yaw;

        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        OnFramebufferResize(_window.FramebufferSize);
        SetFullscreen(Environment.GetEnvironmentVariable("MINE_WINDOWED") != "1");
    }

    private static readonly Vector2D<int> WindowedSize = new(1280, 720);

    /// <summary>
    /// Full screen at the monitor's own resolution (so the display mode does not change and
    /// switching away is quick), or back to a centred window.
    /// </summary>
    private void SetFullscreen(bool fullscreen)
    {
        if (fullscreen)
        {
            // Going full screen lands on the primary monitor whatever the window is on, so move it
            // back to the window's monitor afterwards.
            var monitor = _window.Monitor;
            if (monitor?.VideoMode.Resolution is { } resolution) _window.Size = resolution;
            _window.WindowState = WindowState.Fullscreen;
            if (monitor is not null && _window.Monitor?.Index != monitor.Index) _window.Monitor = monitor;
        }
        else
        {
            _window.WindowState = WindowState.Normal;
            _window.Size = WindowedSize;
            if (_window.Monitor is { } monitor)
                _window.Position = monitor.Bounds.Origin + (monitor.Bounds.Size - WindowedSize) / 2;
        }
    }

    /// <summary>
    /// Moves the game to the next monitor, staying full screen (at that monitor's resolution) or
    /// windowed as it was: the window is centred on the next monitor, then made full screen there.
    /// </summary>
    private void NextMonitor()
    {
        var monitors = Silk.NET.Windowing.Monitor.GetMonitors(_window).ToList();
        if (monitors.Count < 2) return;
        int current = monitors.FindIndex(m => m.Index == _window.Monitor?.Index);
        var next = monitors[(current + 1) % monitors.Count];
        bool fullscreen = _window.WindowState == WindowState.Fullscreen;
        _window.WindowState = WindowState.Normal;
        _window.Size = WindowedSize;
        _window.Position = next.Bounds.Origin + (next.Bounds.Size - WindowedSize) / 2;
        if (fullscreen) SetFullscreen(true);
    }

    // Debugging aids, from environment variables: MINE_FPS_LOG=1 prints the HUD line to the console,
    // MINE_RAIN=1 starts with rain, MINE_SNOW=1 starts snowing with the snow already lying, MINE_STORM=1 / MINE_BLIZZARD=1 start a storm / a blizzard, MINE_WORLD=2 starts in world preset 2 (Ctrl+2), MINE_CAVE=1 / 0 starts before the nearest mountain's cavern (the classic world does by default) / by the lake, MINE_TIME=0.45 sets the time of day, MINE_PITCH=0.2 the view
    // pitch (radians), MINE_YAW=1.5 the view heading, MINE_POS=800,-300 spawns exactly there, MINE_WINDOWED=1 starts in a window, MINE_GPU_PROFILE=1 prints the GPU time of each pass (GpuProfiler).
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
        var spawn = SpawnAt ?? (WorldPreset.Current == WorldPreset.Classic ? Spawn : FindSpawn());
        // In the classic world (or anywhere with MINE_CAVE=1) start before the tunnel of the
        // mountain nearest the usual start, looking in; MINE_CAVE=0 starts by the lake as before.
        string caveStart = Environment.GetEnvironmentVariable("MINE_CAVE") ?? (WorldPreset.Current == WorldPreset.Classic ? "1" : "0");
        if (SpawnAt is null && caveStart == "1"
            && _terrainField.Mountains.Near(spawn.X, spawn.Y, 4000f).OrderBy(m => Vector2.DistanceSquared(new Vector2(m.X, m.Z), spawn)).FirstOrDefault() is { Radius: > 0 } mountain)
        {
            spawn = mountain.Entrance + mountain.Mouth * 10f;
            _player.Yaw = MathF.Atan2(-mountain.Mouth.Y, -mountain.Mouth.X);
        }
        else if (SpawnAt is null && WorldPreset.Current == WorldPreset.Stone && NearestOutcrop(spawn) is { } outcrop)
        {
            // Where there is stone to break, start below the nearest heap of scree, looking up at it.
            spawn = new Vector2(outcrop.X, outcrop.Z) - outcrop.Uphill * (outcrop.Depth + 4f);
            _player.Yaw = MathF.Atan2(outcrop.Uphill.Y, outcrop.Uphill.X);
        }
        _player.Position = new Vector3(spawn.X, _terrainField.Height(spawn.X, spawn.Y), spawn.Y);
        _player.Velocity = Vector3.Zero;
        _player.Flying = false;
    }

    /// <summary>The stone outcrop nearest a point, within 600 m, in worlds that have them.</summary>
    private StoneOutcrops.Outcrop? NearestOutcrop(Vector2 point)
    {
        if (WorldPreset.Current.StoneFields <= 0) return null;
        StoneOutcrops.Outcrop? best = null;
        float bestDistance = 600f;
        int cx = (int)MathF.Floor(point.X / StoneOutcrops.CellSize), cz = (int)MathF.Floor(point.Y / StoneOutcrops.CellSize);
        for (int dz = -10; dz <= 10; dz++)
        for (int dx = -10; dx <= 10; dx++)
            foreach (var o in _terrainField.Outcrops.In(cx + dx, cz + dz))
            {
                float d = Vector2.Distance(point, new Vector2(o.X, o.Z));
                if (d < bestDistance) (best, bestDistance) = (o, d);
            }
        return best;
    }

    /// <summary>
    /// A place to start in a generated world: the dry, gentle ground nearest the origin, preferring
    /// the world's own character: the sand of a desert world, the green heart of an island.
    /// </summary>
    private Vector2 FindSpawn()
    {
        Vector2? fallback = null;
        for (float r = 0; r < 4000f; r += 24f)
        {
            int steps = Math.Max(1, (int)(r * MathF.Tau / 24f));
            for (int k = 0; k < steps; k++)
            {
                float a = k * MathF.Tau / steps;
                float x = MathF.Cos(a) * r, z = MathF.Sin(a) * r;
                if (_terrainField.Height(x, z) < TerrainField.WaterLevel + 2f || _terrainField.Normal(x, z).Y < 0.9f) continue;
                bool sandy = GroundMaterials.Desert(x, z) > 0.7f;
                if (sandy == (WorldPreset.Current == WorldPreset.Desert)) return new Vector2(x, z);
                fallback ??= new Vector2(x, z);
            }
            if (fallback is { } dry && r > 1500f) return dry;
        }
        return fallback ?? Vector2.Zero;
    }

    /// <summary>
    /// Builds everything that depends on the shape of the world (terrain, grass, trees, islands,
    /// objects, creatures) for <see cref="WorldPreset.Current"/>, disposing the previous world.
    /// </summary>
    private void BuildWorld()
    {
        _terrain?.Dispose();
        _grass?.Dispose();
        _trees?.Dispose();
        _islandRenderer?.Dispose();
        _waterfalls?.Dispose();
        _seaFloor?.Dispose();
        _blockRenderer?.Dispose();

        _terrainField = new TerrainField(seed: 1337);
        _terrain = new TerrainRenderer(_gl, _terrainField);
        _objects = new WorldObjects(_terrainField, seed: 1337);
        _grass = new GrassRenderer(_gl, _terrainField);
        _treeField = new TreeField(_terrainField, seed: 1337);
        _trees = new TreeRenderer(_gl);
        _islands = new IslandField(_terrainField, seed: 1337);
        _islandRenderer = new IslandRenderer(_gl);
        _waterfalls = new WaterfallRenderer(_gl);
        _seaFloor = new SeaFloorMap(_gl, _terrainField);
        _blocks = new BlockWorld(_terrainField);
        _blockRenderer = new BlockRenderer(_gl);
        _blockMask ??= new BlockMask(_gl);
        _drops = new Drops();
        _ground = new Ground(_terrainField, _islands, _blocks);
        _creatures = new Creatures(_terrainField);
    }

    /// <summary>Switches to another kind of world (Ctrl+1..3) and starts over in it.</summary>
    private void SetWorld(WorldPreset preset)
    {
        if (preset == WorldPreset.Current) return;
        WorldPreset.Current = preset;
        BuildWorld();
        Respawn();
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
        SkyRenderer.Snow = _weather.Snow;
        SkyRenderer.SnowCover = _weather.SnowCover;
        SkyRenderer.Storm = _weather.Storm;
        SkyRenderer.Blizzard = _weather.Blizzard;
        SkyRenderer.Lightning = _weather.Lightning;
        SkyRenderer.LightningBolt = new Vector2(_weather.LightningAzimuth, _weather.LightningSeed);

        int islandVersion = _islands.Version;
        _islands.Update(_player.Position);
        if (_islands.Version != islandVersion) _treeField.SetFixedTrees("islands", _islands.Trees);
        _islandRenderer.Update(_islands);
        _waterfalls.Update(_islands);
        _seaFloor.Update(_player.Eye);
        _treeField.Update(_player.Position);
        _treeField.ResolveCollision(ref _player.Position, 0.35f);
        _terrain.Update(_player.Eye);
        _trees.Update(_treeField, _player.Eye);
        _grass.Update(_player.Eye);
        _objects.Update(_player.Position);
        _blocks.Update(_player.Position);
        _blockRenderer.Update(_blocks, _player.Eye);
        _blockMask.Update(_blocks, _player.Eye);
        _drops.Update(dt, _ground, _player.Position, _inventory);
        _creatures.Update(_player.Position, _dayCycle.Sample(), dt);
        _creatureRenderer.Update(_creatures);
        UpdateAim();
        UpdateBreaking(dt);
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
            _blockRenderer.Draw(shadowCenter, ShadowMap.Radius * 1.5f);
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
        _blockMask.Bind(); // grass and plants keep off the blocks
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

        // The blocks (world space too), and the building tools' overlay.
        SetWorldUniforms(_blockShader, view * projection, eye, atmosphere, time);
        if (On("blocks")) _blockRenderer.Draw(eye);
        _objectShader.Use(); // the overlay is drawn with the object shader
        _objectShader.Set("uModel", Matrix4x4.Identity);
        _objectShader.Set("uGlow", 1f);
        _objectShader.Set("uHighlight", 0f);
        _blockRenderer.DrawOverlay(eye, _drops.All, _breaking is { } breaking ? (breaking, BreakingColor(breaking), _breakProgress) : null, Outline());

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
        _seaFloor.Bind();
        _waterShader.Set("uSeaFloor", SeaFloorMap.Unit);
        _waterShader.Set("uSeaFloorOrigin", float.IsNaN(_seaFloor.Origin.X) ? new Vector2(1e9f) : _seaFloor.Origin);
        _waterShader.Set("uSeaFloorExtent", SeaFloorMap.Extent);
        _waterShader.Set("uScreenSize", _post.SceneSize);
        _waterShader.Set("uNear", NearPlane);
        _waterShader.Set("uFar", FarPlane);
        _waterShader.Set("uWaterLevel", TerrainField.WaterLevel);
        _waterShader.Set("uWaterExtent", WaterRenderer.Extent);
        if (On("water")) _water.Draw();

        // The islands' waterfalls and their ponds: translucent and glowing, over everything opaque.
        _profiler.Section("cascate");
        if (On("falls")) _waterfalls.Draw(view * projection, eye, atmosphere.Night, time, 2f * MathF.Tan(FieldOfView / 2) / _post.SceneHeight);

        _profiler.Section("pulviscolo+pioggia");
        float heightAboveGround = eye.Y - _ground.Height(eye.X, eye.Z, eye.Y);
        float pointScale = _post.SceneHeight / (2f * MathF.Tan(FieldOfView / 2));
        _motes.Draw(view * projection, eye, atmosphere, time, heightAboveGround, pointScale);
        if (eye.Y > TerrainField.WaterLevel) _rain.Draw(view * projection, eye, time, _weather.Rain, _weather.Storm, atmosphere.Night, pointScale);
        if (eye.Y > TerrainField.WaterLevel) _snow.Draw(view * projection, eye, time, _weather.Snow, _weather.Blizzard, atmosphere.Night, pointScale);

        // The item in hand, last, over everything (so it never sinks into a wall).
        if (_inventory.Selected is { } held && _mouseCaptured) DrawHeld(held, view, time);

        _profiler.Section("post");
        if (On("post")) _post.Finish(atmosphere.Night, MathF.Min(MathF.Max(_weather.Snow, _weather.SnowCover * 0.6f) + 0.2f * _weather.Blizzard, 1f));

        _crosshair.Draw();
        _hud.Draw(_inventory, size.X, size.Y, time);
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
        var preset = WorldPreset.Current;
        shader.Set("uDesertRange", new Vector2(preset.DesertLow, preset.DesertHigh));
        shader.Set("uIslands", preset.Islands ? 1f : 0f);
        shader.Set("uColorVariety", preset.ColorVariety);
        shader.Set("uAmbient", atmosphere.Ambient + new Vector3(0.7f, 0.75f, 1f) * _weather.Lightning * 0.9f); // lightning lights the world
        shader.Set("uUnderwater", eye.Y < TerrainField.WaterLevel ? 1f : 0f);
        shader.Set("uLightColor", atmosphere.LightColor * (1f - 0.45f * MathF.Max(_weather.Rain, 0.8f * _weather.Snow))); // rain and snow veil the sun
        shader.Set("uLightDir", atmosphere.LightDirection);
        shader.Set("uFogStart", fogEnd * 0.75f); // only the last stretch, to hide the world's edge
        shader.Set("uFogEnd", fogEnd);
        shader.Set("uMistDensity", float.Lerp(0.0012f, 0.0025f, atmosphere.Haze) * (1f + 2.5f * MathF.Max(_weather.Rain, _weather.Snow) + 6f * _weather.Blizzard + _weather.Storm)); // more at dawn, dusk, in rain and snow
        shader.Set("uShadowMap", 0);
        shader.Set("uShadowTexel", 1f / ShadowMap.Size);
        shader.Set("uShadowBias", ShadowMap.DepthBias);
        shader.Set("uLightViewProj", _shadowMap.LightViewProjection);
        shader.Set("uBlockMask", BlockMask.Unit);
        shader.Set("uBlockMaskOrigin", _blockMask.Origin);
        // The caverns of the nearest mountains, where the sky's light does not reach.
        int caves = 0;
        foreach (var m in _terrainField.Mountains.Near(eye.X, eye.Z, 400f)
                     .OrderBy(c => (c.X - eye.X) * (c.X - eye.X) + (c.Z - eye.Z) * (c.Z - eye.Z)).Take(4))
        {
            shader.Set($"uCaveCenter[{caves}]", m.CavernCenter);
            shader.Set($"uCaveRadii[{caves}]", new Vector3(m.CavernRadius, m.CavernHeight, m.CavernRadius) * 1.05f);
            caves++;
        }
        shader.Set("uCaveCount", caves);
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
        bool ctrl = keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
        if (ctrl && key >= Key.Number1 && key < Key.Number1 + WorldPreset.All.Length)
        {
            SetWorld(WorldPreset.All[key - Key.Number1]); // Ctrl+1..6: another kind of world
            return;
        }
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
            case Key.Number2: // rain on / off; pressed twice quickly: a storm with lightning
                if (_time - _lastRainTap <= WeatherDoubleTap) _weather.StartStorm(); else _weather.Toggle();
                _lastRainTap = _time;
                break;
            case Key.Number3: // snow on / off; pressed twice quickly: a blizzard
                if (_time - _lastSnowTap <= WeatherDoubleTap) _weather.StartBlizzard(); else _weather.ToggleSnow();
                _lastSnowTap = _time;
                break;
            case Key.Number1: // debug: a shooting star across the view
                SkyRenderer.LaunchShootingStar((float)_time, _player.LookDirection);
                break;
            case Key.F10:
                NextMonitor();
                break;
            case Key.F11:
            case Key.Enter when keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight):
                SetFullscreen(_window.WindowState != WindowState.Fullscreen);
                break;
            case Key.Tab when _mouseCaptured:
                _inventory.Cycle(keyboard.IsKeyPressed(Key.ShiftLeft) ? -1 : 1);
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

        var look = _player.LookDirection;
        if (button == MouseButton.Left)
        {
            // Crystals are picked up at once; blocks break by holding the button (UpdateBreaking).
            if (_aimKind == AimKind.Object && _aimed is { } obj)
            {
                if (_inventory.HasRoomFor(Item.Crystal))
                {
                    _objects.Remove(obj);
                    _inventory.Add(Item.Crystal);
                }
                UpdateAim();
            }
            return;
        }
        if (button != MouseButton.Right || _inventory.Selected is not { } item) return;

        switch (item.Kind)
        {
            case ItemKind.Crystal when _aimKind is AimKind.None or AimKind.Object && _aimGround is { } ground:
            {
                // Place it in the middle of the tile aimed at (stepping back a hair from the hit point,
                // so a hit on a wall picks the tile in front of it), facing the player.
                var aimed = ground - look * 0.05f;
                if (_inventory.Take(item)) _objects.Place(ObjectKind.Crystal, _terrainField.TileCenter(aimed.X, aimed.Z), -_player.Yaw);
                break;
            }
            case ItemKind.Block when _placeCell is { } anchor:
                if (_inventory.Take(item)) _blocks.PlaceBlock(anchor, item.Material, natural: false);
                break;
        }
        UpdateAim();
    }

    /// <summary>
    /// The item in hand, in the lower right of the view as if held: it sways as the player walks
    /// and swings while a block is being broken. Drawn with the object shader (lit like the world),
    /// over a cleared depth buffer.
    /// </summary>
    private void DrawHeld(Item item, Matrix4x4 view, float time)
    {
        if (!Matrix4x4.Invert(view, out var cameraToWorld)) return;
        float speed = MathF.Min(new Vector2(_player.Velocity.X, _player.Velocity.Z).Length() / 9.5f, 1f);
        float walk = _player.OnGround ? speed : 0f;
        var bob = new Vector3(MathF.Cos(time * 4.5f) * 0.012f, -MathF.Abs(MathF.Sin(time * 4.5f)) * 0.02f, 0) * walk;
        float swing = _breaking is null ? 0f : 0.5f + 0.5f * MathF.Sin(time * 16f);
        // Large, as if held close and coming into view from the corner: there is no hand to show.
        // The same bulk whatever the item: a slim crystal is drawn larger than a block.
        float scale = item.Kind == ItemKind.Block ? 0.28f : 0.5f;
        var local = Matrix4x4.CreateScale(scale)
                    * Matrix4x4.CreateRotationY(0.65f)
                    * Matrix4x4.CreateRotationX(0.25f - 0.5f * swing)
                    * Matrix4x4.CreateTranslation(new Vector3(0.36f, -0.32f, -0.62f + 0.06f * swing) + bob);
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        _objectShader.Use();
        _objectShader.Set("uModel", local * cameraToWorld);
        _objectShader.Set("uGlow", 1f);
        _objectShader.Set("uHighlight", 0f);
        _hud.DrawItem(item);
    }

    /// <summary>
    /// While the left button is held on a block, wears it down: patches of wear spread over it and
    /// flakes fall off, and after <see cref="BreakTime"/> it breaks, bursting into flakes and leaving
    /// a small copy of itself, to be picked up by walking over it. Looking away or letting go starts over.
    /// </summary>
    private void UpdateBreaking(float dt)
    {
        bool holding = _mouseCaptured && _input.Mice.Any(mouse => mouse.IsButtonPressed(MouseButton.Left));
        if (!holding || _aimKind != AimKind.Block || !_blocks.TryGet(_aimCell, out var piece))
        {
            _breaking = null;
            _breakProgress = 0;
            return;
        }
        var anchor = piece.Anchor(_aimCell);
        if (_breaking != anchor)
        {
            _breaking = anchor;
            _breakProgress = 0;
            _flakeTimer = 0;
        }
        _breakProgress += dt / BreakTime;

        // Flakes come off the whole block from the first stroke, thicker as it gives way.
        _flakeTimer -= dt;
        if (_flakeTimer <= 0)
        {
            _flakeTimer = float.Lerp(0.09f, 0.035f, _breakProgress);
            _drops.Flake(anchor, piece.Material, count: _breakProgress > 0.5f ? 3 : 2);
        }

        if (_breakProgress < 1f) return;
        _blocks.RemoveBlock(_aimCell, out _, out _);
        _drops.Shatter(anchor, piece.Material);
        _drops.Spawn(Item.Block(piece.Material), anchor.Min + new Vector3(BlockWorld.BlockSize / 2));
        _breaking = null;
        _breakProgress = 0;
        UpdateAim();
    }

    /// <summary>
    /// Finds what the view meets first within reach (an object, a block, or the ground), and where the block in hand would go: against the face of the block aimed at, or
    /// on the ground; never inside the terrain, another block or the player.
    /// </summary>
    private void UpdateAim()
    {
        _aimKind = AimKind.None;
        _aimed = null;
        _aimGround = null;
        _placeCell = null;
        if (!_mouseCaptured) return;

        var eye = _player.Eye;
        var look = _player.LookDirection;
        float best = ReachDistance;
        if (_terrainField.Raycast(eye, look, ReachDistance, out var ground))
        {
            _aimGround = ground;
            best = MathF.Min(best, Vector3.Distance(eye, ground) + 0.3f); // nothing is aimed at through the ground
        }
        if (_objects.Pick(eye, look, best, out float objectDistance) is { } obj)
        {
            _aimKind = AimKind.Object;
            _aimed = obj;
            best = objectDistance;
        }
        if (_blocks.Raycast(eye, look, best, out var cell, out var normal, out float blockDistance) && blockDistance < best)
        {
            _aimKind = AimKind.Block;
            _aimed = null;
            (_aimCell, _aimNormal, _aimDistance) = (cell, normal, blockDistance);
            best = blockDistance;
        }
        if (_aimKind == AimKind.Block) _aimGround = null;

        if (_inventory.Selected is not { Kind: ItemKind.Block }) return;
        // Blocks are 1 m: against a block, the next one lines up with it; on the ground it sits on the
        // 1 m grid, on the ground's own height (which steps by 0.5 m).
        Cell anchor;
        if (_aimKind == AimKind.Block && _aimNormal != Vector3.Zero && _blocks.TryGet(_aimCell, out var hit))
        {
            var n = _aimNormal * BlockWorld.BlockCells;
            anchor = hit.Anchor(_aimCell).Offset((int)n.X, (int)n.Y, (int)n.Z);
        }
        else if (_aimKind == AimKind.None && _aimGround is { } g)
        {
            var p = g - look * 0.05f;
            int x = (int)MathF.Floor(p.X / BlockWorld.BlockSize) * BlockWorld.BlockCells, z = (int)MathF.Floor(p.Z / BlockWorld.BlockSize) * BlockWorld.BlockCells;
            float groundHeight = _terrainField.Height((x + 1) * BlockWorld.CellSize, (z + 1) * BlockWorld.CellSize);
            anchor = new Cell(x, (int)MathF.Round(groundHeight / BlockWorld.CellSize), z);
        }
        else return;

        var min = anchor.Min;
        var max = min + new Vector3(BlockWorld.BlockSize);
        var feet = _player.Position;
        bool inPlayer = min.X < feet.X + 0.25f && max.X > feet.X - 0.25f && min.Z < feet.Z + 0.25f && max.Z > feet.Z - 0.25f
                        && min.Y < feet.Y + 2.3f && max.Y > feet.Y;
        bool inGround = min.Y < _terrainField.Height(min.X + 0.5f, min.Z + 0.5f) - 0.01f;
        if (_blocks.Fits(anchor) && !inPlayer && !inGround) _placeCell = anchor;
    }

    /// <summary>The colour of the block being broken, as the renderer shades it (for its wear).</summary>
    private Vector3 BreakingColor(Cell anchor) =>
        _blocks.TryGet(anchor, out var piece) ? BlockRenderer.Shade(piece, anchor) : Vector3.One;

    /// <summary>The outline to draw: the edges of the block aimed at.</summary>
    private (Vector3, Vector3, Vector3)? Outline()
    {
        const float size = BlockWorld.BlockSize;
        if (_aimKind == AimKind.Block && _blocks.TryGet(_aimCell, out var piece))
        {
            var min = piece.Anchor(_aimCell).Min;
            return (min, min + new Vector3(size), new Vector3(0.95f, 0.85f, 1f));
        }
        return null;
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
        string hand = "in mano: " + _inventory.Describe();
        if (_sky.LowQuality) mode += " | qualità bassa";
        if (_weather.Raining) mode += " | pioggia";
        _window.Title = $"Mine | mondo {WorldPreset.Current.Name} | {hand} | {mode} | ore {hours:00}:{minutes:00} | {fps} FPS | " +
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
        _blockRenderer?.Dispose();
        _blockMask?.Dispose();
        _islandRenderer?.Dispose();
        _waterfalls?.Dispose();
        _seaFloor?.Dispose();
        _cloudNoise?.Dispose();
        _motes?.Dispose();
        _rain?.Dispose();
        _snow?.Dispose();
        _post?.Dispose();
        _waterShader?.Dispose();
        _creatureShader?.Dispose();
        _creatureRenderer?.Dispose();
        _water?.Dispose();
        _crosshair?.Dispose();
        _hud?.Dispose();
        _sky?.Dispose();
        _shadowMap?.Dispose();
        _terrainShader?.Dispose();
        _objectShader?.Dispose();
        _blockShader?.Dispose();
        _objectRenderer?.Dispose();
        _grassShader?.Dispose();
        _treeShader?.Dispose();
        _grass?.Dispose();
        _trees?.Dispose();
        _input?.Dispose();
    }

    public void Dispose() => _window.Dispose();
}
