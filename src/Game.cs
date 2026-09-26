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
    private Ground _ground = null!;
    private Creatures _creatures = null!;
    private CreatureRenderer _creatureRenderer = null!;
    private Shader _creatureShader = null!;
    private CloudNoise _cloudNoise = null!;
    private MoteRenderer _motes = null!;
    private PostProcess _post = null!;
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
        _ground = new Ground(_terrainField, _islands);
        _creatures = new Creatures(_terrainField);
        _creatureRenderer = new CreatureRenderer(_gl);
        _creatureShader = new Shader(_gl, TerrainShaders.CreatureVertex, TerrainShaders.CreatureFragment);
        _cloudNoise = new CloudNoise(_gl);
        _motes = new MoteRenderer(_gl);
        _post = new PostProcess(_gl);
        _waterShader = new Shader(_gl, TerrainShaders.WaterVertex, TerrainShaders.WaterFragment);
        _water = new WaterRenderer(_gl);
        // Integrated GPUs get the lighter clouds and a lower render scale; Q switches at any time.
        string renderer = _gl.GetStringS(StringName.Renderer) ?? "";
        SetLowQuality(renderer.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                      || renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase));

        Respawn();

        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        OnFramebufferResize(_window.FramebufferSize);
    }

    private void Respawn()
    {
        var spawn = FlatSpawn();
        _player.Position = new Vector3(spawn.X, _terrainField.Height(spawn.X, spawn.Y), spawn.Y);
        _player.Velocity = Vector3.Zero;
    }

    /// <summary>
    /// The flattest spot near the origin, searched on a widening spiral: dry land well above the
    /// water (on the grass, not the sand) where the terrain tiles within 12 m sit at most one layer
    /// from the centre's, and as many as possible on the same layer. Nearer spots win ties.
    /// The layered land is rarely perfectly flat, so the flattest is taken rather than a flat one.
    /// </summary>
    private Vector2 FlatSpawn()
    {
        const float step = 16f, radius = 12f;
        var best = Vector2.Zero;
        float bestScore = float.MinValue;
        for (int ring = 0; ring < 30; ring++)
        {
            int points = Math.Max(1, ring * 6);
            for (int i = 0; i < points; i++)
            {
                float a = i * MathF.Tau / points + ring * 0.7f;
                var center = _terrainField.TileCenter(MathF.Cos(a) * ring * step, MathF.Sin(a) * ring * step);
                if (center.Y < TerrainField.WaterLevel + 6f) continue;
                int same = 0, total = 0;
                bool gentle = true;
                for (float dz = -radius; dz <= radius && gentle; dz += TerrainField.TileSize)
                for (float dx = -radius; dx <= radius && gentle; dx += TerrainField.TileSize)
                {
                    if (dx * dx + dz * dz > radius * radius) continue;
                    float h = _terrainField.Height(center.X + dx, center.Z + dz);
                    gentle = MathF.Abs(h - center.Y) <= TerrainField.LayerHeight;
                    total++;
                    if (h == center.Y) same++;
                }
                if (!gentle) continue;
                float score = same / (float)total - ring * 0.004f;
                if (score > bestScore) (bestScore, best) = (score, new Vector2(center.X, center.Z));
            }
        }
        return best;
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

        int islandVersion = _islands.Version;
        _islands.Update(_player.Position);
        if (_islands.Version != islandVersion) _treeField.SetFixedTrees("islands", _islands.Trees);
        _islandRenderer.Update(_islands);
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

        var atmosphere = _dayCycle.Sample();
        float time = (float)_time;
        _lightCount = _objects.CollectLights(eye, time, _lights, _treeField.GlowingLights(eye, 80f).Concat(_treeField.FlowerLights(eye)));
        var shadowCenter = _player.Position;
        _shadowMap.Render(shadowCenter, atmosphere.LightDirection, time, () =>
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

        _shadowMap.Bind(0);
        SetWorldUniforms(_terrainShader, view * projection, eye, atmosphere, time);
        _terrain.Draw(eye, look);

        // Grass blades are seen from both sides.
        SetWorldUniforms(_grassShader, view * projection, eye, atmosphere, time);
        _grassShader.Set("uGrassRadius", GrassRenderer.Radius);
        _gl.Disable(EnableCap.CullFace);
        _grass.Draw(eye, look);
        _gl.Enable(EnableCap.CullFace);

        SetWorldUniforms(_treeShader, view * projection, eye, atmosphere, time);
        _trees.Draw();

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

        // The sky last, only where nothing covers it (see SkyRenderer).
        Matrix4x4.Invert(skyViewProjection, out var inverseSkyViewProj);
        _sky.Draw(inverseSkyViewProj, eye, (float)_dayCycle.Elapsed, atmosphere, time);

        // The water, over a snapshot of everything beneath it.
        _post.SnapshotForWater(colorUnit: 3, depthUnit: 4);
        SetWorldUniforms(_waterShader, view * projection, eye, atmosphere, time);
        _waterShader.Set("uUnderColor", 3);
        _waterShader.Set("uUnderDepth", 4);
        _waterShader.Set("uScreenSize", _post.SceneSize);
        _waterShader.Set("uNear", NearPlane);
        _waterShader.Set("uFar", FarPlane);
        _waterShader.Set("uWaterLevel", TerrainField.WaterLevel);
        _waterShader.Set("uWaterExtent", WaterRenderer.Extent);
        _water.Draw();

        float heightAboveGround = eye.Y - _ground.Height(eye.X, eye.Z, eye.Y);
        float pointScale = _post.SceneHeight / (2f * MathF.Tan(FieldOfView / 2));
        _motes.Draw(view * projection, eye, atmosphere, time, heightAboveGround, pointScale);

        _post.Finish(atmosphere.Night);

        _crosshair.Draw();
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
        shader.Set("uLightColor", atmosphere.LightColor);
        shader.Set("uLightDir", atmosphere.LightDirection);
        shader.Set("uFogStart", fogEnd * 0.75f); // only the last stretch, to hide the world's edge
        shader.Set("uFogEnd", fogEnd);
        shader.Set("uMistDensity", float.Lerp(0.0012f, 0.0025f, atmosphere.Haze)); // a bit more at dawn and dusk
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
        _window.Title = $"Mine | {hand} | {mode} | ore {hours:00}:{minutes:00} | {fps} FPS | " +
                        $"{p.X:0} {p.Y:0} {p.Z:0}{hint}";
        _titleTimer = 0;
        _frames = 0;
    }

    // GL resources must be freed here, while the context is still current:
    // Run() destroys the window (and its context) before Dispose is called.
    private void OnClosing()
    {
        _terrain?.Dispose();
        _islandRenderer?.Dispose();
        _cloudNoise?.Dispose();
        _motes?.Dispose();
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
