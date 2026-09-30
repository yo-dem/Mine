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
    private const float BuildReach = 8f;
    private const double AutosaveSeconds = 20;
    private const int TestStock = 100;
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
    private Shader _cubeShader = null!;
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
    private SeaFloorMap _seaFloor = null!;
    private GroundMap _groundMap = null!;
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
    private Blocks _blocks = null!;
    private BlockRenderer _blockRenderer = null!;
    private Hud _hud = null!;
    private IndoorMap _indoor = null!;
    private IconRenderer _icons = null!;
    private Debris _debris = null!;
    private CubeRenderer _cubes = null!;
    private readonly List<DebrisCube> _cubeList = new();
    private readonly Player _player = new();
    private readonly DayCycle _dayCycle = new();
    private readonly PointLight[] _lights = new PointLight[TerrainShaders.MaxPointLights];
    private int _lightCount;

    // Inventory: the slots of materials the player carries, and which slot is in hand.
    private readonly Inventory _inventory = new();
    private int _slot;
    // The wheel picks the slot one notch at a time. A trackpad sends a stream of small scroll events
    // (and keeps sending them as the swipe coasts on), so after the first step of a gesture the next
    // needs ScrollNotch of scrolling and at least ScrollInterval seconds since the last step; a pause
    // of ScrollGestureGap (or scrolling the other way) starts a new gesture, which steps at once.
    private const float ScrollNotch = 2.5f;
    private const double ScrollInterval = 0.18, ScrollGestureGap = 0.25;
    private float _scrollAccum;
    private double _lastScrollAt = double.NegativeInfinity, _lastScrollStepAt = double.NegativeInfinity;
    private int _scrollSign;
    private Resource? Held => _inventory[_slot]?.Resource;
    private WorldObject? _aimed; // the object under the crosshair, within reach

    // Gathering: what is aimed at, and how long the button has been held on it.
    private (TreeInstance Tree, TreeModels.Yield Yield, float Distance)? _gatherTarget;
    private object? _gathering; // the tree, rock or block being hit
    private float _gatherProgress;
    private float _chipTimer; // chips fly off the thing being hit at a steady rate

    // Building: where the right button would put a block of the material in hand (one per click), and
    // the block under the crosshair (the left button breaks it).
    private BlockPlan? _plan;
    private BlockHit? _aimedBlock;

    // Short messages over the crosshair ("+4 Legno"), and the swing of the hand while walking.
    private readonly List<(string Text, Vector4 Color, double Until)> _toasts = new();
    private float _bobPhase;

    private SaveData _save = new();
    private bool _dirty;
    private double _sinceSave;

    private Vector2? _lastMouse;
    private bool _mouseCaptured;
    private double _titleTimer;
    private int _frames;
    private double _time;
    private double _lastForwardTap = double.NegativeInfinity;
    private const double WeatherDoubleTap = 0.4; // seconds between two presses of 2 or 3 for a storm or a blizzard
    private double _lastRainTap = double.NegativeInfinity, _lastSnowTap = double.NegativeInfinity;
    private bool _sprinting; // latched by double-tapping W, lasts until W is released

    // The inventory slides out of the screen to the left when left alone (InventoryHideDelay seconds
    // after the wheel last turned, over InventoryOutSeconds), and back in place when the wheel turns
    // (over InventoryInSeconds). _inventoryShown goes from 0 (out) to 1 (in place).
    // Coming in it springs past its place and settles with little bounces; going out it draws back
    // first (see DrawHud).
    private const float InventoryHideDelay = 3f, InventoryOutSeconds = 0.4f, InventoryInSeconds = 0.6f;
    private double _inventoryUsedAt;
    private float _inventoryShown = 1f;
    private bool _inventoryEntering = true;

    // The menu (Esc): the game's state (what the window title shows) and the options, switched by
    // their keys only while it is open.
    private bool _panelOpen = Environment.GetEnvironmentVariable("MINE_MENU") == "1"; // MINE_MENU=1: open from the start
    private string[] _info = [];
    private static readonly (Key Key, string Name)[] OptionKeys =
        [(Key.V, "Sincronizzazione verticale"), (Key.M, "Salvataggio del mondo"), (Key.I, "Schermo intero"), (Key.X, "Esci dal gioco")];
    private const int VSyncOption = 0, SaveWorldOption = 1, FullscreenOption = 2, QuitOption = 3; // quitting is an action, not a switch

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
        if (LogHud) Console.WriteLine($"GPU: {_gl.GetStringS(StringName.Renderer)}");
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

        _terrainShader = new Shader(_gl, TerrainShaders.TerrainVertex, TerrainShaders.TerrainFragment);
        _objectShader = new Shader(_gl, TerrainShaders.ObjectVertex, TerrainShaders.ObjectFragment);
        _cubeShader = new Shader(_gl, TerrainShaders.CubeVertex, TerrainShaders.ObjectFragment);
        _objectRenderer = new ObjectRenderer(_gl);
        _grassShader = new Shader(_gl, TerrainShaders.GrassVertex, TerrainShaders.GrassFragment);
        _treeShader = new Shader(_gl, TerrainShaders.TreeVertex, TerrainShaders.TreeFragment);
        _crosshair = new Crosshair(_gl);
        _hud = new Hud(_gl);
        _indoor = new IndoorMap(_gl);
        _icons = new IconRenderer(_gl);
        // The saved game: the inventory now, each world's changes as it is built.
        if (SaveGame.Load() is { } save) _save = save;
        if (_save.Slots is { } slots) _inventory.Load(slots);
        // For now, while building is being tried out: exactly TestStock of everything at every start.
        foreach (var resource in Enum.GetValues<Resource>())
        {
            int have = _inventory.Count(resource);
            if (have > TestStock) _inventory.Take(resource, have - TestStock);
            else _inventory.Add(resource, TestStock - have);
        }
        // MINE_SLOT=seeds (or wood, stone, crystal): starts with that material in hand.
        if (Enum.TryParse<Resource>(Environment.GetEnvironmentVariable("MINE_SLOT"), true, out var inHand))
            _slot = Math.Max(Enumerable.Range(0, Inventory.SlotCount).FirstOrDefault(i => _inventory[i]?.Resource == inHand, -1), 0);
        _sky = new SkyRenderer(_gl);
        _shadowMap = new ShadowMap(_gl);
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
        if (_save.Options.VSync is { } vsync && Environment.GetEnvironmentVariable("MINE_NO_VSYNC") != "1") _window.VSync = vsync;

        Respawn();
        if (Environment.GetEnvironmentVariable("MINE_GIVE") == "1")
            foreach (var resource in Enum.GetValues<Resource>()) _inventory.Add(resource, 99);
        if (Environment.GetEnvironmentVariable("MINE_DEMO") == "1") BuildDemoHouse();
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
        SetFullscreen(Environment.GetEnvironmentVariable("MINE_FULLSCREEN") == "1"); // windowed, for debugging
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
    // MINE_RAIN=1 starts with rain, MINE_SNOW=1 starts snowing with the snow already lying, MINE_STORM=1 / MINE_BLIZZARD=1 start a storm / a blizzard, MINE_TIME=0.45 sets the time of day, MINE_PITCH=0.2 the view
    // pitch (radians), MINE_YAW=1.5 the view heading, MINE_POS=800,-300 spawns exactly there, MINE_FULLSCREEN=1 starts full screen (windowed by default, for debugging), MINE_GIVE=1 gives 99 of every material, MINE_DEMO=1 builds a small block house ahead, MINE_SHOT=file.png saves a frame and quits (Screenshot), MINE_BREAK=10 holds the left button from 10 s on, MINE_SAVE=path|none picks the save file, MINE_GPU_PROFILE=1 prints the GPU time of each pass (GpuProfiler).
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
    // Debug (MINE_BREAK=seconds): from then on, act as if the left button were held.
    private static readonly double AutoBreakAt = double.TryParse(Environment.GetEnvironmentVariable("MINE_BREAK"),
        System.Globalization.CultureInfo.InvariantCulture, out double breakAt) ? breakAt : double.MaxValue;
    private static readonly bool LogHud = Environment.GetEnvironmentVariable("MINE_FPS_LOG") == "1";
    // Debug (MINE_HEIGHT=metres): start flying that high above the ground, for a view over the land.
    private static readonly float SpawnHeight = float.TryParse(Environment.GetEnvironmentVariable("MINE_HEIGHT"),
        System.Globalization.CultureInfo.InvariantCulture, out float height) ? height : 0f;

    private void Respawn()
    {
        var spawn = SpawnAt ?? FindSpawn();
        _player.Position = new Vector3(spawn.X, _terrainField.Height(spawn.X, spawn.Y), spawn.Y);
        _player.Velocity = Vector3.Zero;
        _player.Flying = SpawnHeight > 0f;
        _player.Position += new Vector3(0, SpawnHeight, 0);
    }

    /// <summary>
    /// A place to start: the dry, gentle ground nearest the origin in the heart of a prairie (or
    /// failing that, any dry gentle ground).
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
                if (_terrainField.Height(x, z) < TerrainField.WaterLevel + 2f || _terrainField.Normal(x, z).Y < 0.9f
                    || _terrainField.SpireHeight(x, z) > 0f) continue;
                if (GroundMaterials.Biomes(x, z).Prairie > 0.8f) return new Vector2(x, z);
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
        _groundMap?.Dispose();
        _blockRenderer?.Dispose();
        _cubes?.Dispose();

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
        _groundMap = new GroundMap(_gl, TerrainShaders.GroundMapFragment);
        _blocks = new Blocks(_terrainField);
        _blockRenderer = new BlockRenderer(_gl);
        _debris = new Debris();
        _cubes = new CubeRenderer(_gl);
        _ground = new Ground(_terrainField, _islands, _blocks);
        _creatures = new Creatures(_terrainField);

        // Nothing grows under and around the buildings (the grass grows back slowly when they go),
        // and nothing from outside gets into the spaces walls close in.
        _grass.Growth = _blocks.GrassGrowth;
        _grass.Planted = _blocks.Planted;
        _treeField.Covered = (x, z) => _blocks.GrassGrowth(x, z) < 1f;
        _creatures.Indoors = _blocks.Indoors;
        _creatures.NearBlock = _blocks.Near;
        _creatures.GrassFor = (x, z) => _blocks.Planted(x, z) ? _blocks.GrassGrowth(x, z) >= ButterflyGrass
            : GrassAt(new Vector3(x, _terrainField.Height(x, z), z));
        _blocks.GroundChanged += (x, z) =>
        {
            _grass.InvalidateCell(x, z, TreeField.CellSize); // raised once per tree cell (Blocks.RaiseGroundChanged)
            _treeField.Refresh(x, z);
        };
        _blocks.GrassGrowing += _grass.Invalidate;
        LoadWorldState();
    }

    /// <summary>Restores what the player built and gathered in this world (before anything around is generated).</summary>
    private void LoadWorldState()
    {
        if (!_save.Options.SaveWorld || !_save.Worlds.TryGetValue(WorldPreset.Current.Name, out var world)) return;
        _blocks.Load(world.Blocks);
        _blocks.LoadDug(world.Dug);
        _blocks.LoadGrass(world.TornGrass, world.PlantedGrass);
        _treeField.LoadGathered(world.Gathered.Select(g => new Vector3(g.X, g.Y, g.Z)));
        _debris.Load(world.Drops);
        _objects.Load(world.TakenObjects, world.PlacedObjects);
    }

    /// <summary>
    /// Debug (MINE_DEMO=1): a small block house a few metres ahead of the classic spawn: a 6×6 stone
    /// floor, wooden walls three blocks high with a doorway and window openings, a flat wooden roof,
    /// and a crystal block for a lamp.
    /// </summary>
    private void BuildDemoHouse()
    {
        int x0 = (int)MathF.Floor(Spawn.X) + 4, z0 = (int)MathF.Floor(Spawn.Y) - 3;
        const int size = 6;
        int floor = 0;
        for (int x = x0; x < x0 + size; x++)
        for (int z = z0; z < z0 + size; z++)
            floor = Math.Max(floor, (int)MathF.Round(_terrainField.Height(x + 0.5f, z + 0.5f) / Blocks.Step));
        for (int x = x0; x < x0 + size; x++)
        for (int z = z0; z < z0 + size; z++)
        {
            _blocks.Add(new BlockPos(x, floor, z), Resource.Stone);
            bool edge = x == x0 || z == z0 || x == x0 + size - 1 || z == z0 + size - 1;
            for (int h = 1; h <= 3 && edge; h++)
            {
                bool door = x == x0 && (z == z0 + 2 || z == z0 + 3);
                bool window = h == 2 && (z == z0 + size - 1 || x == x0 + size - 1) && (x == x0 + 2 || x == x0 + 3 || z == z0 + 2 || z == z0 + 3);
                if (!door) _blocks.Add(new BlockPos(x, floor + h * Blocks.Tall, z), window ? Resource.Glass : Resource.Wood);
            }
            _blocks.Add(new BlockPos(x, floor + 4 * Blocks.Tall, z), Resource.Wood);
        }
        _blocks.Add(new BlockPos(x0 + 3, floor + Blocks.Tall, z0 + 3), Resource.Crystal);
    }

    /// <summary>Writes the inventory and this world's changes to the save file.</summary>
    private void SaveState()
    {
        _save.Slots = _inventory.Save();
        if (!_save.Options.SaveWorld)
        {
            // Nothing of the worlds is kept: the next start finds them as they were generated.
            _save.Worlds.Clear();
            SaveGame.Write(_save);
            _dirty = false;
            _sinceSave = 0;
            return;
        }
        _save.Worlds[WorldPreset.Current.Name] = new WorldSave
        {
            Blocks = _blocks.Save(),
            Dug = _blocks.SaveDug(),
            TornGrass = _blocks.SaveTorn(),
            PlantedGrass = _blocks.SavePlanted(),
            Gathered = _treeField.SaveGathered().Select(g => new GatheredSave(g.X, g.Y, g.Z)).ToList(),
            Drops = _debris.Save(),
            TakenObjects = _objects.SaveTaken(),
            PlacedObjects = _objects.SavePlaced(),
        };
        SaveGame.Write(_save);
        _dirty = false;
        _sinceSave = 0;
    }

    private void OnUpdate(double deltaTime)
    {
        _time += deltaTime;
        float dt = (float)Math.Min(deltaTime, 0.05); // avoid huge steps after a hitch

        var move = Vector2.Zero;
        bool playing = _mouseCaptured;
        if (playing)
        {
            if (_keyboard.IsKeyPressed(Key.W)) move.Y += 1;
            if (_keyboard.IsKeyPressed(Key.S)) move.Y -= 1;
            if (_keyboard.IsKeyPressed(Key.D)) move.X += 1;
            if (_keyboard.IsKeyPressed(Key.A)) move.X -= 1;
        }

        bool sneak = playing && !_player.Flying && _keyboard.IsKeyPressed(Key.ShiftLeft);
        if (move.Y <= 0 || sneak) _sprinting = false;

        _player.Update(_ground, dt, move,
            up: playing && _keyboard.IsKeyPressed(Key.Space),
            down: playing && _keyboard.IsKeyPressed(Key.ShiftLeft),
            sprint: _sprinting || _keyboard.IsKeyPressed(Key.ControlLeft));

        bool fastTime = playing && _keyboard.IsKeyPressed(Key.T);
        _dayCycle.Update((float)deltaTime * (fastTime ? FastTimeScale : 1f));
        _weather.Update(dt, GroundMaterials.Snow(_player.Position.X, _player.Position.Z), GroundMaterials.Desert(_player.Position.X, _player.Position.Z));
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
        _blocks.ResolveCollision(ref _player.Position, ref _player.Velocity, Player.BodyRadius, Player.Height, 0.55f);
        _terrain.Update(_player.Eye);
        _trees.Update(_treeField, _player.Eye);
        _grass.Update(_player.Eye);
        _objects.Update(_player.Position);
        _creatures.Update(_player.Position, _dayCycle.Sample(), dt);
        _creatureRenderer.Update(_creatures);
        Aim();
        Gather(dt);
        Sow(dt);
        if (_debris.Update(dt, _ground, _player.Position, _inventory) > 0)
        {
            _dirty = true;
            _inventoryUsedAt = _time; // something went into the bag: the inventory slides in to show it
        }
        _blocks.UpdateSpires(_player.Position);
        _blockRenderer.Update(_blocks);
        _blockRenderer.UpdateFar(_player.Eye, _blocks, _terrainField);
        _blocks.Update();
        _indoor.Update(_blocks, _player.Eye, _time);
        if (double.IsNaN(_loadedAt) && ((_terrain.Ready && _grass.Ready && _treeField.Ready) || _time > LoadingMaxSeconds))
        {
            _loadedAt = _time;
            if (LogHud) Console.WriteLine($"Mondo caricato in {_time:0.0} s");
        }
        var walk = new Vector2(_player.Velocity.X, _player.Velocity.Z).Length();
        if (_player.OnGround) _bobPhase += walk * dt * 1.2f;
        UpdateHeldSwap(dt);
        _toasts.RemoveAll(t => t.Until < _time);
        bool inventoryWanted = _time - _inventoryUsedAt < InventoryHideDelay;
        _inventoryEntering = inventoryWanted;
        _inventoryShown = inventoryWanted
            ? MathF.Min(1f, _inventoryShown + (float)deltaTime / InventoryInSeconds)
            : MathF.Max(0f, _inventoryShown - (float)deltaTime / InventoryOutSeconds);

        _sinceSave += deltaTime;
        if (_dirty && _sinceSave > AutosaveSeconds) SaveState();
        UpdateTitle(deltaTime);
    }

    /// <summary>
    /// What is under the crosshair: in building mode, where the piece in hand would go and the piece
    /// that would be taken apart; otherwise the nearest crystal or gatherable thing in reach (not
    /// through the ground or a wall).
    /// </summary>
    private void Aim()
    {
        _aimed = null;
        _gatherTarget = null;
        _plan = null;
        _aimedBlock = null;
        _mowTarget = null;
        if (!_mouseCaptured) return;
        var eye = _player.Eye;
        var look = _player.LookDirection;
        if (Held is { } material && material != Resource.Seeds) _plan = _blocks.Plan(eye, look, BuildReach, _treeField.TrunkIn, _player.Position, Player.BodyRadius, Player.Height);
        float limit = ReachDistance;
        bool groundHit = _terrainField.Raycast(eye, look, ReachDistance, out var ground);
        if (groundHit) limit = Vector3.Distance(eye, ground) + 0.3f;
        if (_blocks.Raycast(eye, look, limit) is { } block)
        {
            limit = block.Distance;
            _aimedBlock = block;
        }
        _aimed = _objects.Pick(eye, look, limit, out float objectDistance);
        if (_aimed is not null) _aimedBlock = null;
        _gatherTarget = _treeField.Pick(eye, look, objectDistance);
        if (_gatherTarget is not null) (_aimed, _aimedBlock) = (null, null);
        // Nothing else aimed at: the tile under the crosshair can be mown if any of it holds grass
        // (as far as seeds can be thrown, and not past a tree).
        if (_gatherTarget is null && _aimed is null && _aimedBlock is null && AimedTile() is { } tile
            && _treeField.Pick(eye, look, tile.Distance) is null
            && Blocks.TileColumns(tile.Center.X, tile.Center.Z).Any(c => GrassAt(c.X, c.Z)))
            _mowTarget = tile.Center;
    }

    private void PlaceBlock(BlockPlan plan)
    {
        if (Held is not { } material) return;
        if (!_inventory.Take(material, 1))
        {
            Toast("Mancano materiali", new Vector4(1f, 0.6f, 0.6f, 1f));
            return;
        }
        _blocks.Add(plan.Position, material);
        _dirty = true;
    }

    // How long a block takes to break, and the colours of the chips.
    private static readonly (Vector3, Vector3) WoodChips = (new(0.42f, 0.29f, 0.26f), new(0.27f, 0.18f, 0.19f));
    private static readonly (Vector3, Vector3) StoneChips = (new(0.52f, 0.48f, 0.56f), new(0.40f, 0.37f, 0.46f));
    private static readonly (Vector3, Vector3) CrystalChips = (new(0.75f, 0.55f, 1.0f), new(0.45f, 0.85f, 1.0f));
    private static float BreakSeconds(Resource r) => r switch { Resource.Wood => 0.45f, Resource.Stone => 0.6f, _ => 0.35f };
    private static (Vector3, Vector3) ChipsOf(Resource r) => r switch
    {
        Resource.Wood => WoodChips, Resource.Stone => StoneChips, Resource.Glass => (TreeModels.GlassChip, TreeModels.GlassChip2), _ => CrystalChips,
    };

    // Tearing up grass: the whole terrain tile aimed at is torn up almost at once, for good
    // (Blocks.TearUp), giving one seed. Sowing a seed (right button) greens the tile where it lands,
    // SowFlight seconds after it is thrown; holding the button sows a strip, a seed every
    // SowInterval seconds wherever the aim has moved on to another tile.
    private const float MowSeconds = 0.12f, SowFlight = 0.55f, SowInterval = 0.3f;
    private static readonly (Vector3, Vector3) SeedGlow = (new(0.45f, 1.0f, 0.85f), new(0.8f, 0.6f, 1.0f));
    private readonly List<(Vector3 At, double LandsAt)> _sown = new(); // seeds in the air
    private Vector3? _lastSown;
    private double _thrownAt = double.NegativeInfinity; // when seeds were last thrown (the jar's flick, see DrawHeldItem)
    private const float FlickSeconds = 0.35f;
    private double _balkedAt = double.NegativeInfinity; // when a throw was cut short (nothing can be sown there)
    private const float BalkSeconds = 0.22f;
    private double _nextSowAt;
    private const float ButterflyGrass = 0.6f; // how grown planted grass must be for butterflies
    private Vector3? _mowTarget; // the centre of the grassy tile under the crosshair, when nothing else is aimed at
    private readonly record struct MowKey(int X, int Z); // what is being mown (for the progress of the blow)

    /// <summary>
    /// Whether grass grows at a point of the ground (so it can be torn up): a meadow not torn up
    /// (growing back counts), or grass planted. The tests follow GrassRenderer's (a little more
    /// generous: it tests each clump, this one point), so no visible grass is left out.
    /// </summary>
    private bool GrassAt(Vector3 p)
    {
        if (_terrainField.InPond(p.X, p.Z, 0.5f)) return false;
        float growth = _blocks.GrassGrowth(p.X, p.Z);
        if (_blocks.Planted(p.X, p.Z)) return growth > 0f; // even just sown (it gives its seed back)
        if (growth <= 0.2f) return false;
        return p.Y >= TerrainField.WaterLevel + 1f && GroundMaterials.GrassWeight(p, _terrainField.Normal(p.X, p.Z).Y) >= 0.3f;
    }

    /// <summary>
    /// The terrain tile under the crosshair within BuildReach, with no block in the way: its centre
    /// (at its height) and how far along the view it was hit. A wall counts for the tile it belongs to.
    /// </summary>
    private (Vector3 Center, float Distance)? AimedTile()
    {
        var eye = _player.Eye;
        var look = _player.LookDirection;
        if (!_terrainField.Raycast(eye, look, BuildReach, out var ground)) return null;
        float distance = Vector3.Distance(eye, ground);
        if (_blocks.Raycast(eye, look, distance) is not null) return null;
        return (_terrainField.TileCenter(ground.X + look.X * 0.05f, ground.Z + look.Z * 0.05f), distance);
    }

    private bool GrassAt(int x, int z) => GrassAt(new Vector3(x + 0.5f, _terrainField.Height(x + 0.5f, z + 0.5f), z + 0.5f));

    /// <summary>Whether grass can be planted in a column: bare ground above the water, not under blocks or rock.</summary>
    private bool Plantable(int x, int z)
    {
        float cx = x + 0.5f, cz = z + 0.5f, y = _terrainField.Height(cx, cz);
        return y > TerrainField.WaterLevel + 0.3f && !_terrainField.InPond(cx, cz, 1f) && _terrainField.SpireHeight(cx, cz) <= 0f
            && !GrassAt(x, z);
    }

    /// <summary>
    /// Throws a seed at the ground aimed at: a glowing handful arcs from the hand, and grass starts
    /// growing where it lands (see <see cref="Sow(float)"/>). A click warns when nothing can grow
    /// there; while the button is held, a seed goes only where the aim has moved on.
    /// </summary>
    private void Sow(bool click)
    {
        var eye = _player.Eye;
        var look = _player.LookDirection;
        if (AimedTile() is not { Center: var ground }) return;
        if (!click && _lastSown == ground) return;
        if (_sown.Any(s => s.At == ground) || !_blocks.CanPlant(Blocks.TileColumns(ground.X, ground.Z), Plantable))
        {
            if (click) _balkedAt = _time; // no seed thrown: the jar's flick starts and stops short
            return;
        }
        _inventory.Take(Resource.Seeds, 1);
        _lastSown = ground;
        var right = Vector3.Normalize(Vector3.Cross(look, Vector3.UnitY));
        var hand = eye + look * 0.5f + right * 0.35f - Vector3.UnitY * 0.35f;
        _debris.Throw(hand, ground, SowFlight, 16, SeedGlow.Item1, SeedGlow.Item2);
        _thrownAt = _time;
        _sown.Add((ground, _time + SowFlight));
    }

    /// <summary>Keeps sowing while the right button is held, and plants the seeds that have landed.</summary>
    private void Sow(float dt)
    {
        bool holding = _mouseCaptured && Held == Resource.Seeds && _input.Mice.Any(m => m.IsButtonPressed(MouseButton.Right));
        if (!holding) _lastSown = null;
        else if (_time >= _nextSowAt)
        {
            Sow(click: false);
            _nextSowAt = _time + SowInterval;
        }
        for (int i = _sown.Count - 1; i >= 0; i--)
        {
            if (_sown[i].LandsAt > _time) continue;
            var at = _sown[i].At;
            _sown.RemoveAt(i);
            if (_blocks.Plant(Blocks.TileColumns(at.X, at.Z), Plantable) > 0) _dirty = true;
        }
    }

    /// <summary>
    /// Holding the left button on a tree, a rock, a crystal (a cluster or a small one) or a block hits it: chips fly off where the blow
    /// lands, and after a moment it breaks, bursting into chips and cubes of its material (a block
    /// gives itself back) that fall around and wait to be picked up (see <see cref="Debris"/>).
    /// Trees, rocks and crystals never come back.
    /// </summary>
    private void Gather(float dt)
    {
        bool holding = _mouseCaptured && (_input.Mice.Any(m => m.IsButtonPressed(MouseButton.Left)) || _time > AutoBreakAt);
        object? key = _gatherTarget?.Tree ?? _aimed ?? (object?)_aimedBlock?.Block
            ?? (_mowTarget is { } aimedGrass ? new MowKey((int)MathF.Floor(aimedGrass.X), (int)MathF.Floor(aimedGrass.Z)) : null);
        if (!holding || key is null)
        {
            _gathering = null;
            _gatherProgress = 0;
            return;
        }
        if (!Equals(_gathering, key))
        {
            _gathering = key;
            _gatherProgress = 0;
        }
        _gatherProgress += dt;

        var look = _player.LookDirection;
        Vector3 chip, chip2, hit;
        float seconds;
        if (_gatherTarget is { } target)
        {
            (chip, chip2, seconds) = (target.Yield.Chip, target.Yield.Chip2, target.Yield.Seconds);
            hit = _player.Eye + look * MathF.Max(target.Distance - target.Yield.PickRadius * 0.3f, 0.3f);
        }
        else if (_aimed is { } small)
        {
            var yield = TreeModels.SmallCrystal;
            (chip, chip2, seconds) = (yield.Chip, yield.Chip2, yield.Seconds);
            hit = small.Position + new Vector3(0, yield.PickHeight * 0.5f, 0) - look * 0.1f;
        }
        else if (_aimedBlock is { } block)
        {
            var material = _blocks.At(block.Block) ?? Resource.Wood;
            (chip, chip2) = ChipsOf(material);
            // A vein is broken like the rock around it, but its chips glint with the mineral.
            if (_blocks.VeinAt(block.Block) is { } ore) chip2 = ChipsOf(ore).Item1;
            seconds = BreakSeconds(material);
            hit = block.Point - look * 0.05f;
        }
        else
        {
            var grass = _mowTarget!.Value;
            var color = GroundMaterials.GrassColor(grass.X, grass.Z);
            (chip, chip2, seconds) = (color, color * 0.6f, MowSeconds);
            hit = grass + new Vector3(0, 0.4f, 0);
        }

        // Chips from the point hit, thrown back toward the player.
        _chipTimer += dt * 30f;
        int chips = (int)_chipTimer;
        _chipTimer -= chips;
        _debris.Chips(hit, -look, chips, chip, chip2);
        if (_gatherProgress < seconds) return;

        if (_gatherTarget is { } broken)
        {
            var yield = broken.Yield;
            var tree = broken.Tree;
            _debris.Burst(tree.Position, yield.PickHeight * 1.5f, yield.PickRadius * 1.5f, 40 + 10 * Math.Min(yield.Amount.Count, 8), chip, chip2);
            _debris.Spill(yield.Amount.Resource, Math.Min(yield.Amount.Count, 32), tree.Position, MathF.Min(yield.PickHeight, 3f));
            _treeField.Gather(tree);
        }
        else if (_aimed is { } small)
        {
            // A small crystal shatters into a couple of crystal cubes.
            var yield = TreeModels.SmallCrystal;
            _debris.Burst(small.Position, yield.PickHeight, yield.PickRadius, 20, chip, chip2);
            _debris.Spill(yield.Amount.Resource, yield.Amount.Count, small.Position, yield.PickHeight);
            _objects.Remove(small);
            _aimed = null;
        }
        else if (_mowTarget is { } mown)
        {
            // The tile's grass bursts into bits of blades, torn up for good, and leaves seeds to
            // plant elsewhere.
            int grassy = _blocks.TearUp(Blocks.TileColumns(mown.X, mown.Z).ToList(), GrassAt);
            if (grassy > 0)
            {
                _debris.Burst(mown, 0.9f, TerrainField.TileSize * 0.6f, 40, chip, chip2);
                _debris.Spill(Resource.Seeds, 1, mown, 0.4f);
            }
            _mowTarget = null;
        }
        else
        {
            // A block breaks into chips and gives itself back as a cube.
            var at = _aimedBlock!.Value.Block;
            var material = _blocks.Remove(at) ?? Resource.Wood;
            _debris.Burst(at.Center - new Vector3(0, 0.5f, 0), 1f, 0.5f, 25, chip, chip2);
            _debris.Spill(material, 1, at.Center - new Vector3(0, 0.5f, 0), 0.5f);
        }
        _gathering = null;
        _gatherProgress = 0;
        _dirty = true;
    }

    private void Toast(string text, Vector4 color) => _toasts.Add((text, color, _time + 2.2));

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
        // The terrain's slow materials around the camera (redrawn only after moving far).
        _groundMap.Update(eye, shader => shader.Set("uColorVariety", WorldPreset.Current.ColorVariety));
        _profiler.Section("ombre");
        var atmosphere = _dayCycle.Sample();
        float time = (float)_time;
        _lightCount = _objects.CollectLights(eye, time, _lights, _treeField.GlowingLights(eye, 80f).Concat(_treeField.FlowerLights(eye)).Concat(_waterfalls.Lights(eye)).Concat(_blocks.Lights(eye, 40f)));
        var shadowCenter = _player.Position;
        if (On("shadow")) _shadowMap.Render(shadowCenter, atmosphere.LightDirection, time, () =>
        {
            _terrain.DrawNear(shadowCenter, ShadowMap.Radius * 1.5f);
            _shadowMap.SetInstanced(true);
            _trees.DrawShadowCasters();
            _shadowMap.SetInstanced(false);
            _shadowMap.SetCasterModel(Matrix4x4.Identity);
            _islandRenderer.Draw(); // floating islands throw their shadows on the land
            _blockRenderer.Draw();
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
        _indoor.Bind();
        _groundMap.Bind();
        var skyView = Matrix4x4.CreateLookAt(Vector3.Zero, look, Vector3.UnitY);
        var skyViewProjection = skyView * projection;

        // Nearest and most covering first: the trees and the grass fill the depth buffer, so the
        // terrain under them is rejected before its costly shading (in a meadow that took the
        // terrain from ~1.9 to ~0.3 ms at 1080p).
        _shadowMap.Bind(0);
        _profiler.Section("alberi");
        SetWorldUniforms(_treeShader, view * projection, eye, atmosphere, time);
        if (On("trees")) _trees.Draw(look, FieldOfView, (float)size.X / size.Y);

        // Grass blades are seen from both sides.
        _profiler.Section("erba");
        SetWorldUniforms(_grassShader, view * projection, eye, atmosphere, time);
        _grassShader.Set("uGrassRadius", GrassRenderer.Radius);
        _gl.Disable(EnableCap.CullFace);
        if (On("grass")) _grass.Draw(eye, view * projection, _grassShader);
        _gl.Enable(EnableCap.CullFace);

        _profiler.Section("terreno");
        SetWorldUniforms(_terrainShader, view * projection, eye, atmosphere, time);
        if (On("terrain")) _terrain.Draw(eye, look);

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
        // What the player built: crystal blocks glow steadily.
        _objectShader.Set("uGlow", 1f);
        _blockRenderer.Draw();
        _blockRenderer.DrawFar(_blocks);

        // Chips and cubes of material (the object fragment shader, with the cube vertex shader).
        _cubeList.Clear();
        _debris.Collect(_cubeList, time);
        if (_cubeList.Count > 0)
        {
            SetWorldUniforms(_cubeShader, view * projection, eye, atmosphere, time);
            _cubeShader.Set("uGlow", 1f);
            _cubeShader.Set("uHighlight", 0f);
            _cubes.Draw(_cubeList);
        }

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

        // Glass: glass blocks, the glass cubes lying around, the translucent crystals.
        _profiler.Section("vetro");
        SetWorldUniforms(_objectShader, view * projection, eye, atmosphere, time);
        _objectShader.Set("uModel", Matrix4x4.Identity);
        _objectShader.Set("uGlow", 1f);
        _objectShader.Set("uHighlight", 0f);
        _objectShader.Set("uGlass", 1f);
        SetWorldUniforms(_treeShader, view * projection, eye, atmosphere, time);
        _treeShader.Set("uGlass", 1f);
        if (_cubeList.Count > 0)
        {
            SetWorldUniforms(_cubeShader, view * projection, eye, atmosphere, time);
            _cubeShader.Set("uGlass", 1f);
        }
        BeginGlass();
        for (int side = 0; side < 2; side++)
        {
            _objectShader.Use();
            _blockRenderer.DrawGlass();
            if (_cubeList.Count > 0)
            {
                _cubeShader.Use();
                _cubes.DrawGlass();
            }
            _treeShader.Use();
            if (On("trees")) _trees.Draw(look, FieldOfView, (float)size.X / size.Y, translucent: true);
            GlassBackFacesDone();
        }
        EndGlass();
        _objectShader.Use();
        _objectShader.Set("uGlass", 0f);
        _treeShader.Use();
        _treeShader.Set("uGlass", 0f);
        if (_cubeList.Count > 0)
        {
            _cubeShader.Use();
            _cubeShader.Set("uGlass", 0f);
        }

        // The islands' waterfalls and their ponds: translucent and glowing, over everything opaque.
        _profiler.Section("cascate");
        if (On("falls")) _waterfalls.Draw(view * projection, eye, atmosphere.Night, time, 2f * MathF.Tan(FieldOfView / 2) / _post.SceneHeight);

        _profiler.Section("pulviscolo+pioggia");
        float heightAboveGround = eye.Y - _ground.Height(eye.X, eye.Z, eye.Y);
        float pointScale = _post.SceneHeight / (2f * MathF.Tan(FieldOfView / 2));
        _motes.Draw(view * projection, eye, atmosphere, time, heightAboveGround, pointScale, _indoor);
        if (eye.Y > TerrainField.WaterLevel) _rain.Draw(view * projection, eye, time, _weather.Rain, _weather.Storm, atmosphere.Night, pointScale, _indoor);
        if (eye.Y > TerrainField.WaterLevel) _snow.Draw(view * projection, eye, time, _weather.Snow, _weather.Blizzard, atmosphere.Night, pointScale, _indoor);

        DrawHeldItem(view, time);

        _profiler.Section("post");
        if (On("post")) _post.Finish(atmosphere.Night, MathF.Min(MathF.Max(_weather.Snow, _weather.SnowCover * 0.6f) + 0.2f * _weather.Blizzard, 1f));

        _crosshair.Draw();
        DrawHud(size.X, size.Y);
        DrawLoadingFade(size.X, size.Y);
        _profiler.EndFrame();

        if (Screenshot.Path is { } shot && _time > Screenshot.At)
        {
            Screenshot.Save(_gl, size.X, size.Y, shot);
            _window.Close();
        }
    }

    /// <summary>
    /// The material in hand, low on the right of the view, swaying as the player walks and chopping
    /// while gathering. Drawn big, so the block fills the corner where a hand would be.
    /// </summary>
    // Switching what is in hand, the item held sinks out of the view below (SwapOutSeconds), as if
    // put back in a pocket, and the new one rises from there (SwapInSeconds), overshooting its place a
    // little. _swapOut goes 0 (in place) .. 1 (out of view); _shownItem is the one drawn meanwhile.
    private const float SwapOutSeconds = 0.14f, SwapInSeconds = 0.26f, SwapDrop = 0.6f;
    private Resource? _shownItem;
    private float _swapOut = 1f;
    private bool _swapComing;

    private void UpdateHeldSwap(float dt)
    {
        if (Held != _shownItem && _shownItem is not null)
        {
            _swapComing = false;
            _swapOut = MathF.Min(1f, _swapOut + dt / SwapOutSeconds);
            if (_swapOut < 1f) return;
        }
        if (Held != _shownItem)
        {
            _shownItem = Held;
            _swapOut = 1f;
        }
        if (_shownItem is null) return;
        _swapComing = _swapOut > 0f;
        _swapOut = MathF.Max(0f, _swapOut - dt / SwapInSeconds);
    }

    private void DrawHeldItem(Matrix4x4 view, float time)
    {
        if (_shownItem is not { } resource) return;
        // Going out it speeds up as it sinks; coming in it slows down, rising a touch past its place (ease out back).
        float swap;
        if (_swapComing)
        {
            float t = 1f - _swapOut - 1f;
            swap = -(2.2f * t * t * t + 1.2f * t * t); // 1 - easeOutBack(1 - _swapOut)
        }
        else swap = _swapOut * _swapOut;
        Matrix4x4.Invert(view, out var cameraToWorld);
        float bobX = MathF.Cos(_bobPhase) * 0.012f, bobY = -MathF.Abs(MathF.Sin(_bobPhase)) * 0.018f;
        float chop = _gathering is not null ? 0.5f + 0.5f * MathF.Sin(time * 14f) : 0f;
        // The seed jar is big and low, its bottom out of the view (so no hand is missed).
        bool jar = resource == Resource.Seeds;
        float lift = jar ? 0.06f : 0f;
        // Throwing seeds, the jar flicks forward and up, tipping its mouth toward the ground aimed at,
        // and swings back.
        float since = (float)(_time - _thrownAt) / FlickSeconds;
        float flick = jar && since is >= 0f and < 1f ? MathF.Sin(MathF.PI * since) * (1f - 0.3f * since) : 0f;
        // Where nothing can be sown (grass there already), the flick is cut short: a small start, and back.
        float balked = (float)(_time - _balkedAt) / BalkSeconds;
        if (jar && balked is >= 0f and < 1f) flick = MathF.Max(flick, 0.28f * MathF.Sin(MathF.PI * MathF.Sqrt(balked)));
        var local = Matrix4x4.CreateScale(jar ? 0.6f : 0.34f) * Matrix4x4.CreateRotationY(-0.55f)
                    * Matrix4x4.CreateRotationX(0.2f + chop * 0.5f - flick * 0.75f + swap * 0.5f) * Matrix4x4.CreateRotationZ(flick * 0.2f - swap * 0.35f)
                    * Matrix4x4.CreateTranslation(0.42f + bobX - flick * 0.04f + swap * 0.08f, -0.38f + lift + bobY - chop * 0.05f + flick * 0.07f - swap * SwapDrop,
                        -0.72f - chop * 0.06f - flick * 0.12f);

        // Over everything, never cut by a wall the player stands against.
        _gl.DepthMask(true);
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        _objectShader.Use();
        _objectShader.Set("uModel", local * cameraToWorld);
        _objectShader.Set("uGlow", 1f);
        _objectShader.Set("uHighlight", 0f);
        _objectShader.Set("uSnowless", 1f);
        _objectRenderer.DrawItem(resource);
        if (!_objectRenderer.HasGlass(resource))
        {
            _objectShader.Set("uSnowless", 0f);
            return;
        }

        // Glass (the seed jar's, a glass block's panes) over the rest.
        _objectShader.Set("uGlass", 1f);
        BeginGlass();
        _objectRenderer.DrawItem(resource, glass: true);
        GlassBackFacesDone();
        _objectRenderer.DrawItem(resource, glass: true);
        EndGlass();
        _objectShader.Set("uGlass", 0f);
        _objectShader.Set("uSnowless", 0f);
    }

    // See-through glass is blended (premultiplied) over what is behind it without writing depth,
    // each thing twice: its far side (front faces culled), then its near side.
    private void BeginGlass()
    {
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        _gl.CullFace(TriangleFace.Front);
    }

    private void GlassBackFacesDone() => _gl.CullFace(TriangleFace.Back);

    private void EndGlass()
    {
        _gl.CullFace(TriangleFace.Back);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
    }

    // Starting, the screen is black while the land, the grass and the trees around are built, then
    // the world fades in over FadeInSeconds (after LoadingMaxSeconds at the latest), so nothing is
    // seen popping in. In the black the title opens up over TitleOpenSeconds: big pixel letters cut
    // through it, the world showing behind them; it stays at least TitleSeconds before the fade.
    private const float FadeInSeconds = 1.6f, LoadingMaxSeconds = 12f, TitleOpenSeconds = 0.9f, TitleSeconds = 2.8f;
    private const string Title = "ALONE";
    private double _loadedAt = double.NaN;

    private void DrawLoadingFade(int width, int height)
    {
        double fadeFrom = double.IsNaN(_loadedAt) ? double.NaN : Math.Max(_loadedAt, TitleSeconds);
        float t = double.IsNaN(fadeFrom) ? 0f : (float)Math.Clamp((_time - fadeFrom) / FadeInSeconds, 0.0, 1.0);
        if (t >= 1f) return;
        float black = 1f - t * t * (3f - 2f * t);
        float open = (float)Math.Clamp(_time / TitleOpenSeconds, 0.0, 1.0);
        float hole = black * (1f - open * open * (3f - 2f * open)); // the black left in the letters
        var dark = new Vector4(0f, 0f, 0f, black);

        // The title's grid of glyph pixels, whole screen pixels each, in the middle of the screen.
        int columns = Hud.TextPixels(Title), rows = 7;
        int cell = Math.Max(2, (int)(width * 0.5f / columns));
        int x0 = (width - columns * cell) / 2, y0 = (height - rows * cell) / 2;
        int x1 = x0 + columns * cell, y1 = y0 + rows * cell;

        _hud.Begin(width, height);
        // The black around the title, then its grid, the letters' cells holding what is left of the black.
        _hud.Rect(0, 0, width, y0, dark);
        _hud.Rect(0, y1, width, height - y1, dark);
        _hud.Rect(0, y0, x0, y1 - y0, dark);
        _hud.Rect(x1, y0, width - x1, y1 - y0, dark);
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            int letter = column / Hud.LetterAdvance, inLetter = column % Hud.LetterAdvance;
            bool lit = Hud.GlyphPixel(Title[letter], inLetter, row);
            _hud.Rect(x0 + column * cell, y0 + row * cell, cell, cell, lit ? dark with { W = hole } : dark);
        }
        _hud.End();
    }

    /// <summary>The interface: the materials column, the progress of a blow, messages and the menu.</summary>
    private void DrawHud(int width, int height)
    {
        _hud.Begin(width, height);
        int s = _hud.Scale;
        var white = new Vector4(1f, 0.97f, 1f, 0.95f);
        var soft = new Vector4(0.9f, 0.86f, 1f, 0.75f);
        var red = new Vector4(1f, 0.45f, 0.5f, 1f);

        // The inventory: a column of slots in the middle of the left edge, each with its material
        // turning in it and the count; the one in hand glows with a pink halo. Left alone, it slides
        // out of the screen to the left; the mouse wheel brings it back in place.
        int slotSize = 20 * s, gap = 3 * s;
        // Coming in, a damped spring, 1 - cos(3.5 pi t) e^(-4.5 t): it springs about a quarter of
        // the way past its place to the right, then settles with smaller and smaller bounces (and
        // is exactly in place at t = 1). Going out, 1 - easeInBack(1 - shown): it first draws back
        // a touch to the right, then slides away.
        const float back = 2.6f;
        static float InBack(float t) => (back + 1f) * t * t * t - back * t * t;
        static float Spring(float t) => 1f - MathF.Cos(3.5f * MathF.PI * t) * MathF.Exp(-4.5f * t);
        float slide = _inventoryEntering ? Spring(_inventoryShown) : 1f - InBack(1f - _inventoryShown);
        float barX = float.Lerp(-slotSize - 12 * s, 10 * s, slide);
        float barY = (height - (Inventory.SlotCount * slotSize + (Inventory.SlotCount - 1) * gap)) / 2f;
        float SlotX(int i) => barX;
        float SlotY(int i) => barY + i * (slotSize + gap);
        var pink = new Vector4(1f, 0.45f, 0.85f, 1f);
        float pulse = 0.8f + 0.2f * MathF.Sin((float)_time * 3f);
        for (int ring = 4; ring >= 1; ring--)
        {
            // The halo: soft rings fading outward around the slot in hand.
            float r = ring * 1.5f * s;
            _hud.Rect(SlotX(_slot) - r, SlotY(_slot) - r, slotSize + 2 * r, slotSize + 2 * r, (pink with { W = 0.12f * pulse }));
        }
        for (int i = 0; i < Inventory.SlotCount; i++)
        {
            bool selected = i == _slot;
            _hud.Rect(SlotX(i), SlotY(i), slotSize, slotSize, (selected ? new Vector4(0.30f, 0.10f, 0.28f, 0.8f) : new Vector4(0.08f, 0.05f, 0.14f, 0.55f)));
            _hud.Frame(SlotX(i), SlotY(i), slotSize, slotSize, selected ? s : Math.Max(1, s / 2), (selected ? pink : new Vector4(1f, 1f, 1f, 0.25f)));
        }
        _hud.End();
        _icons.Begin(width, height);
        for (int i = 0; i < Inventory.SlotCount; i++)
            if (_inventory[i] is { } stack)
            {
                float turn = (float)_time * 1.2f + i * 0.7f;
                _icons.Draw(stack.Resource, SlotX(i) + slotSize / 2f, SlotY(i) + slotSize / 2f - s, slotSize * 0.55f, turn);
            }
        _icons.End();
        _hud.Begin(width, height);
        for (int i = 0; i < Inventory.SlotCount; i++)
        {
            if (_inventory[i] is not { } stack) continue;
            // At most three characters, so counts never run into the next slot: 999, then 1K, 12K...
            string number = stack.Count < 1000 ? stack.Count.ToString() : $"{stack.Count / 1000}K";
            _hud.Text(number, SlotX(i) + slotSize - Hud.TextWidth(number, s) - s, SlotY(i) + slotSize - 8 * s, s, (white));
        }

        // How far the blow has got, while breaking something (nothing is named: the world speaks for itself).
        float centerX = width / 2f;
        if (_gathering is not null && _gathering is not MowKey && (_gatherTarget is not null || _aimedBlock is not null || _aimed is not null))
        {
            var blockMaterial = _aimedBlock is { } aimedBlock ? _blocks.At(aimedBlock.Block) ?? Resource.Wood : Resource.Wood;
            var yield = _gatherTarget?.Yield ?? (_aimed is not null ? TreeModels.SmallCrystal : (TreeModels.Yield?)null);
            float seconds = yield?.Seconds ?? BreakSeconds(blockMaterial);
            float done = Math.Clamp(_gatherProgress / seconds, 0f, 1f);
            float y = height / 2f + 12 * s, barWidth = 50 * s;
            _hud.Rect(centerX - barWidth / 2, y, barWidth, 2 * s, new Vector4(0f, 0f, 0f, 0.5f));
            _hud.Rect(centerX - barWidth / 2, y, barWidth * done, 2 * s, white);
        }

        // Messages, newest at the bottom, fading out.
        for (int i = 0; i < _toasts.Count; i++)
        {
            var (text, color, until) = _toasts[i];
            float fade = (float)Math.Clamp((until - _time) / 0.6, 0, 1);
            _hud.TextCentered(text, centerX, height / 2f - (14 + 9 * (_toasts.Count - 1 - i)) * s, s, color with { W = color.W * fade });
        }

        if (_panelOpen) DrawPanel(s, white, soft, width);

        _hud.End();
    }

    /// <summary>
    /// The menu, at the top in the middle of the screen: the game's state (the position first) in
    /// one column, the options and their keys in the next.
    /// </summary>
    private void DrawPanel(int s, Vector4 white, Vector4 soft, int screenWidth)
    {
        var on = new Vector4(0.55f, 1f, 0.8f, 1f);
        var off = new Vector4(1f, 0.55f, 0.6f, 1f);
        float line = 10 * s, pad = 6 * s, gap = 16 * s, stateGap = 8 * s;
        const string stateOn = "SI", stateOff = "NO";

        var info = new List<(string Text, Vector4 Color)> { ("Info", white) };
        info.AddRange(_info.Select(i => (i, soft)));
        var options = new List<(string Text, Vector4 Color)> { ("Opzioni", white) };
        options.AddRange(OptionKeys.Select(o => ($"[{o.Key}] {o.Name}", soft)));
        options.Add(("Esc: torna al gioco", soft));

        float infoWidth = info.Max(r => Hud.TextWidth(r.Text, s));
        float optionsWidth = MathF.Max(options.Max(r => Hud.TextWidth(r.Text, s)),
            OptionKeys.Max(o => Hud.TextWidth($"[{o.Key}] {o.Name}", s)) + stateGap + Hud.TextWidth(stateOff, s));
        int rows = Math.Max(info.Count, options.Count);
        float total = infoWidth + gap + optionsWidth, height = rows * line - 2 * s;
        float x = MathF.Round((screenWidth - total) / 2f), y = 8 * s + pad;
        _hud.Rect(x - pad, y - pad, total + 2 * pad, height + 2 * pad, new Vector4(0.05f, 0.03f, 0.1f, 0.7f));

        for (int i = 0; i < info.Count; i++)
            _hud.Text(info[i].Text, x, y + i * line, s, info[i].Color);
        float ox = x + infoWidth + gap;
        for (int i = 0; i < options.Count; i++)
        {
            _hud.Text(options[i].Text, ox, y + i * line, s, options[i].Color);
            int option = i - 1;
            if (option >= 0 && option < OptionKeys.Length && option != QuitOption)
            {
                bool enabled = OptionOn(option);
                string state = enabled ? stateOn : stateOff;
                _hud.Text(state, ox + optionsWidth - Hud.TextWidth(state, s), y + i * line, s, enabled ? on : off);
            }
        }
    }

    /// <summary>Binds a world shader and sets everything it needs for this frame.</summary>
    private void SetWorldUniforms(Shader shader, Matrix4x4 viewProjection, Vector3 eye, in Atmosphere atmosphere, float time)
    {
        const float fogEnd = TerrainRenderer.ViewDistance;
        shader.Use();
        SkyRenderer.SetUniforms(shader, atmosphere, time, (float)_dayCycle.Elapsed);
        shader.Set("uViewProj", viewProjection);
        shader.Set("uCameraPos", eye);
        shader.Set("uColorVariety", WorldPreset.Current.ColorVariety);
        // The ground map: the terrain's materials, and the snowy lands for every shader's snow.
        shader.Set("uGroundMap", GroundMap.Unit);
        shader.Set("uGroundMapOrigin", float.IsNaN(_groundMap.Origin.X) ? new Vector2(1e9f) : _groundMap.Origin);
        shader.Set("uGroundMapExtent", GroundMap.Extent);
        shader.Set("uAmbient", atmosphere.Ambient);
        shader.Set("uFlash", new Vector3(0.7f, 0.75f, 1f) * _weather.Lightning * 0.9f); // lightning lights the world (outdoors)
        _indoor.SetUniforms(shader);
        shader.Set("uUnderwater", eye.Y < TerrainField.WaterLevel ? 1f : 0f);
        shader.Set("uLightColor", atmosphere.LightColor * (1f - 0.45f * MathF.Max(_weather.Rain, 0.8f * _weather.Snow))); // rain and snow veil the sun
        shader.Set("uLightDir", atmosphere.LightDirection);
        shader.Set("uFogStart", fogEnd * 0.75f); // only the last stretch, to hide the world's edge
        shader.Set("uFogEnd", fogEnd);
        // The far veil: from 300 m the land melts toward the sky's colour, 85% at the end of the view
        // (far spires turn to pale silhouettes, and the world's edge softens).
        shader.Set("uVeil", 0.85f);
        shader.Set("uVeilStart", 300f);
        shader.Set("uVeilHeight", 0f);
        shader.Set("uMistDensity", float.Lerp(0.0012f, 0.0025f, atmosphere.Haze) * (1f + 2.5f * MathF.Max(_weather.Rain, _weather.Snow) + 6f * _weather.Blizzard + _weather.Storm)); // more at dawn, dusk, in rain and snow
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
        if (key == Key.Escape)
        {
            // The menu lies over the game, which goes on as if nothing were open.
            _panelOpen = !_panelOpen;
            if (!_mouseCaptured) SetMouseCaptured(true);
            return;
        }
        if (_panelOpen && Array.FindIndex(OptionKeys, o => o.Key == key) is var option and >= 0)
        {
            ToggleOption(option);
            return;
        }
        switch (key)
        {
            case Key.W when _mouseCaptured:
                if (_time - _lastForwardTap <= DoubleTapWindow) _sprinting = true;
                _lastForwardTap = _time;
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

        if (button == MouseButton.Right && Held == Resource.Seeds)
        {
            Sow(click: true);
            _nextSowAt = _time + SowInterval;
            return;
        }
        if (button == MouseButton.Right)
        {
            // One block of the material in hand on the face aimed at.
            if (_plan is { Valid: true } plan) PlaceBlock(plan);
            else if (Held is not null && _plan is { Problem: { } problem }) Toast(problem, new Vector4(1f, 0.6f, 0.6f, 1f));
        }
    }

    private void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        if (!_mouseCaptured || wheel.Y == 0) return;
        int sign = Math.Sign(wheel.Y);
        bool newGesture = _time - _lastScrollAt > ScrollGestureGap || sign != _scrollSign;
        _lastScrollAt = _time;
        _scrollSign = sign;
        if (newGesture) _scrollAccum = 0f;
        else
        {
            _scrollAccum += MathF.Abs(wheel.Y);
            if (_scrollAccum < ScrollNotch || _time - _lastScrollStepAt < ScrollInterval) return;
            _scrollAccum = 0f;
        }
        _lastScrollStepAt = _time;
        int step = -sign;
        _slot = (_slot + step + Inventory.SlotCount) % Inventory.SlotCount;
        _inventoryUsedAt = _time; // the inventory shows fully again
    }

    private bool OptionOn(int option) => option switch
    {
        VSyncOption => _window.VSync,
        FullscreenOption => _window.WindowState == WindowState.Fullscreen,
        _ => _save.Options.SaveWorld,
    };

    /// <summary>Switches an option of the menu, and saves the choice (or quits the game).</summary>
    private void ToggleOption(int option)
    {
        if (option == QuitOption)
        {
            _window.Close(); // the game is saved on closing
            return;
        }
        bool on = !OptionOn(option);
        if (option == FullscreenOption) SetFullscreen(on); // not saved: the game starts windowed (for now, debugging)
        else if (option == VSyncOption)
        {
            _window.VSync = on;
            _save.Options.VSync = on;
            SaveGame.Write(_save);
        }
        else
        {
            // Saving the world back on writes this world at once; off, the file forgets every world.
            _save.Options.SaveWorld = on;
            SaveState();
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
        var (hours, minutes) = _dayCycle.Clock;
        string weather = _weather.Storm > 0.5f ? "temporale" : _weather.Blizzard > 0.5f ? "bufera di neve"
            : _weather.Rain > 0.5f ? "pioggia" : _weather.Snow > 0.5f ? "neve" : "sereno";
        _info =
        [
            $"Posizione: {p.X:0} {p.Y:0} {p.Z:0}",
            $"{fps} FPS",
            $"Bioma: {GroundMaterials.BiomeMix.Names[GroundMaterials.Biomes(p.X, p.Z).Dominant()]}",
            $"Ore {hours:00}:{minutes:00}, {weather}",
        ];
        string hint = _mouseCaptured ? "" : " | clicca per giocare";
        _window.Title = $"Mine | {string.Join(" | ", _info)}{hint}";
        if (LogHud) Console.WriteLine($"{_window.Title} | fili d'erba {_grass.DrawnBlades} | alberi per livello " + string.Join(" ", _trees.Drawn.Select(d => $"{d.Instances}/{d.Vertices / 1000}k")));
        _titleTimer = 0;
        _frames = 0;
    }

    // GL resources must be freed here, while the context is still current:
    // Run() destroys the window (and its context) before Dispose is called.
    private void OnClosing()
    {
        SaveState();
        _blockRenderer?.Dispose();
        _hud?.Dispose();
        _indoor?.Dispose();
        _icons?.Dispose();
        _cubes?.Dispose();
        _cubeShader?.Dispose();
        _terrain?.Dispose();
        _islandRenderer?.Dispose();
        _waterfalls?.Dispose();
        _seaFloor?.Dispose();
        _groundMap?.Dispose();
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
