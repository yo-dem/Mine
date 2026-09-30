using System.Numerics;

namespace Mine.World;

/// <summary>
/// A block's place: the 1 m column (X, Z) and the height of its bottom in steps of
/// <see cref="Blocks.Step"/> (0.5 m, the terrain's layers), so a block set on the ground always sits on it.
/// </summary>
public readonly record struct BlockPos(int X, int Y, int Z)
{
    public float Bottom => Y * Blocks.Step;
    public float Top => Bottom + Blocks.Size;
    public Vector3 Center => new(X + 0.5f, Bottom + Blocks.Size / 2, Z + 0.5f);
}

public readonly record struct BlockSave(Resource Resource, int X, int Y, int Z);

/// <summary>A 1 m column of torn-up or planted grass (<see cref="At"/>: when it was planted, Unix ms).</summary>
public readonly record struct ColumnSave(int X, int Z, long At);

/// <summary>A block of a rock spire the player dug out: it is not laid again.</summary>
public readonly record struct DugSave(int X, int Y, int Z);

/// <summary>Where the view ray meets a block: which, where, and the face's outward normal.</summary>
public readonly record struct BlockHit(BlockPos Block, Vector3 Point, Vector3 Normal, float Distance);

/// <summary>Where the next block would go (on the face or the ground aimed at), and why it cannot, if it cannot.</summary>
public readonly record struct BlockPlan(BlockPos Position, Vector3 Normal, string? Problem)
{
    public bool Valid => Problem is null;
}

/// <summary>
/// What the player builds: 1 m blocks of wood, stone or crystal, placed like in Minecraft on the face
/// aimed at. Answers what the player and the falling cubes stand on (<see cref="GroundBelow"/>), what
/// stops the player sideways and overhead (<see cref="ResolveCollision"/>), what the view ray meets
/// (<see cref="Raycast"/>), where the next block goes (<see cref="Plan"/>), where the grass has been
/// cleared around the blocks and how far it has grown back (<see cref="GrassGrowth"/>), and what is
/// under a roof, where nothing from outside gets (<see cref="Indoors"/>).
/// The rock spires are natural stone blocks too (<see cref="UpdateSpires"/>): laid within
/// <see cref="SpireRadius"/> of the player, solid through, all on one 1 m grid, and dug like any
/// block; only the blocks dug out of them are saved. Veins of glass and of glowing crystal wind
/// through them (<see cref="VeinAt"/>): the only place those materials are found.
/// </summary>
public sealed class Blocks
{
    public const float Size = 1f;
    public const float Step = TerrainField.LayerHeight; // 0.5 m
    public const int Tall = 2;                          // a block is two steps tall
    public const int ChunkSize = 16;                     // columns per side of a mesh chunk
    public const float RegrowSeconds = 90f;              // how long the grass takes to grow back
    public const float SpireRadius = 600f;               // rock spires are laid as blocks within this
    private const float SpireForget = 700f;              // and taken away beyond this

    private readonly TerrainField _terrain;
    private readonly Dictionary<BlockPos, Resource> _blocks = new();
    private readonly Dictionary<(int X, int Z), List<int>> _columns = new(); // bottoms (in steps) of the blocks in each column
    private readonly HashSet<(int X, int Z)> _dirty = new();                 // chunks to remesh
    private readonly HashSet<BlockPos> _crystals = new();                    // the crystal blocks (the lights)

    // The rock spires: their natural blocks, those dug out of them (never laid again), the blocks
    // each spire laid (by its centre), the ground under each column (cached: noise is slow), the
    // columns holding blocks the player placed, and where the spires were last looked for.
    private readonly HashSet<BlockPos> _natural = new();
    private readonly Dictionary<BlockPos, Resource> _veins = new(); // the natural blocks holding glass or crystal
    private readonly HashSet<BlockPos> _dug = new();
    private readonly Dictionary<(float X, float Z), List<BlockPos>> _spires = new();
    private readonly Dictionary<(int X, int Z), float> _ground = new();
    private readonly Dictionary<(int X, int Z), int> _placed = new();
    private Vector2? _spiresCheckedAt;

    // Columns cleared of grass and flowers (under and around blocks near the ground), and those growing
    // back since when (ms). Read by the grass and flower builders on worker threads: replaced whole.
    private volatile HashSet<(int X, int Z)> _cleared = new();
    private volatile Dictionary<(int X, int Z), long> _regrowing = new();
    private long _nextRegrowTick;

    // The meadows the player tore up (no grass ever grows there again, unless planted) and the
    // columns planted with tufts (when, in Unix ms: they keep growing while the game is closed).
    // Read by the grass builders on worker threads: replaced whole.
    private volatile HashSet<(int X, int Z)> _torn = new();
    private volatile Dictionary<(int X, int Z), long> _planted = new();
    private readonly HashSet<(int X, int Z)> _plantedGrowing = new(); // planted, not grown yet (main thread)
    public const float PlantGrowSeconds = 120f; // a planted tuft grows from small to full in this long

    private static long UnixNow => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public Blocks(TerrainField terrain) => _terrain = terrain;

    /// <summary>Changes whenever a block is added or removed.</summary>
    public int Version { get; private set; }

    /// <summary>Changes whenever grass is planted or torn up (see <see cref="Plantings"/>).</summary>
    public int PlantingsVersion { get; private set; }

    // Just sown, the ground looks watered for WetSeconds, then dries by DrySeconds.
    private const float WetSeconds = 3f, DrySeconds = 9f;

    /// <summary>Whether some sown ground is still wet (see <see cref="Plantings"/>).</summary>
    public bool GroundDrying
    {
        get
        {
            long now = UnixNow, planted = now - (long)(DrySeconds * 1000);
            var all = _planted;
            return _plantedGrowing.Any(c => all.TryGetValue(c, out long at) && at > planted);
        }
    }

    /// <summary>How wet the ground of each planted column looks: dark as if watered just after sowing, soon dry.</summary>
    public IEnumerable<((int X, int Z) Column, float Wet)> Plantings()
    {
        long now = UnixNow;
        return _planted.Select(p => (p.Key, 1f - SmoothStep(WetSeconds, DrySeconds, (now - p.Value) / 1000f)));
    }

    private static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public int Count => _blocks.Count;

    public IEnumerable<KeyValuePair<BlockPos, Resource>> All => _blocks;

    public Resource? At(BlockPos p) => _blocks.TryGetValue(p, out var r) ? r : null;

    /// <summary>The bottoms (in steps) of the blocks in a column, or null.</summary>
    public IReadOnlyList<int>? Column(int x, int z) => _columns.TryGetValue((x, z), out var list) ? list : null;

    public static (int X, int Z) ColumnOf(float x, float z) => ((int)MathF.Floor(x), (int)MathF.Floor(z));

    /// <summary>Raised with a column's centre when its ground gets cleared, or has grown back fully.</summary>
    public event Action<float, float>? GroundChanged;

    /// <summary>Raised now and then with the centre of each column whose grass is growing back.</summary>
    public event Action<float, float>? GrassGrowing;

    // ---- Adding and removing -----------------------------------------------------------------

    /// <summary>Whether a block at <paramref name="p"/> would overlap one already there.</summary>
    public bool Overlaps(BlockPos p) => _columns.TryGetValue((p.X, p.Z), out var list) && list.Any(y => Math.Abs(y - p.Y) < Tall);

    public bool Add(BlockPos p, Resource resource)
    {
        if (Overlaps(p)) return false;
        Insert(p, resource);
        _placed[(p.X, p.Z)] = _placed.GetValueOrDefault((p.X, p.Z)) + 1;
        Changed(p);
        return true;
    }

    /// <summary>Takes a block away (a natural one is remembered as dug).</summary>
    public Resource? Remove(BlockPos p)
    {
        if (Delete(p) is not { } resource) return null;
        if (_natural.Remove(p))
        {
            _dug.Add(p);
            if (_veins.Remove(p, out var ore)) resource = ore; // a vein gives its mineral
        }
        else if (_placed.TryGetValue((p.X, p.Z), out int placed))
        {
            if (placed <= 1) _placed.Remove((p.X, p.Z));
            else _placed[(p.X, p.Z)] = placed - 1;
        }
        Changed(p);
        return resource;
    }

    /// <summary>Whether a block belongs to a rock spire.</summary>
    public bool IsNatural(BlockPos p) => _natural.Contains(p);

    /// <summary>The mineral a spire's block holds (glass or crystal), if it is part of a vein.</summary>
    public Resource? VeinAt(BlockPos p) => _veins.TryGetValue(p, out var r) ? r : null;

    /// <summary>What breaking the block gives: its mineral for a vein, else its own material.</summary>
    public Resource? MaterialAt(BlockPos p) => VeinAt(p) ?? At(p);

    private void Insert(BlockPos p, Resource resource)
    {
        _blocks[p] = resource;
        if (resource == Resource.Crystal) _crystals.Add(p);
        if (!_columns.TryGetValue((p.X, p.Z), out var list)) _columns[(p.X, p.Z)] = list = new List<int>();
        list.Add(p.Y);
    }

    private Resource? Delete(BlockPos p)
    {
        if (!_blocks.Remove(p, out var resource)) return null;
        _crystals.Remove(p);
        if (_columns.TryGetValue((p.X, p.Z), out var list))
        {
            list.Remove(p.Y);
            if (list.Count == 0) _columns.Remove((p.X, p.Z));
        }
        return resource;
    }

    /// <summary>The ground (terrain tile top) under a column, cached. Main thread only.</summary>
    public float GroundAt(int x, int z)
    {
        if (!_ground.TryGetValue((x, z), out float ground))
            _ground[(x, z)] = ground = _terrain.Height(x + 0.5f, z + 0.5f);
        return ground;
    }

    // ---- The rock spires -----------------------------------------------------------------------

    /// <summary>
    /// Lays the rock spires near the camera as natural stone blocks, and takes away those left far
    /// behind. Every column of a spire is a solid stack from the 1 m level at or just under its
    /// ground up to the spire's height there, so all its blocks line up and hide each other's faces.
    /// </summary>
    public void UpdateSpires(Vector3 camera)
    {
        var at = new Vector2(camera.X, camera.Z);
        if (_spiresCheckedAt is { } last && Vector2.Distance(last, at) < 16f) return;
        _spiresCheckedAt = at;
        bool changed = false;

        foreach (var key in _spires.Keys.ToList())
        {
            if (Vector2.Distance(new Vector2(key.X, key.Z), at) < SpireForget) continue;
            foreach (var p in _spires[key])
                if (_natural.Remove(p) && Delete(p) is not null) MarkDirty(p);
            foreach (var p in _spires[key]) _veins.Remove(p);
            _spires.Remove(key);
            changed = true;
        }

        foreach (var spire in _terrain.SpiresNear(at.X, at.Y, SpireRadius))
        {
            if (_spires.ContainsKey((spire.X, spire.Z))) continue;
            var laid = new List<BlockPos>();
            for (int z = (int)MathF.Floor(spire.Z - spire.Reach); z <= (int)MathF.Ceiling(spire.Z + spire.Reach); z++)
            for (int x = (int)MathF.Floor(spire.X - spire.Reach); x <= (int)MathF.Ceiling(spire.X + spire.Reach); x++)
            {
                float rise = _terrain.SpireHeight(x + 0.5f, z + 0.5f);
                if (rise < Size / 2) continue;
                float ground = GroundAt(x, z);
                int bottom = (int)MathF.Floor(ground / Size);
                int count = (int)MathF.Round(ground + rise - bottom * Size);
                for (int k = 0; k < count; k++)
                {
                    var p = new BlockPos(x, (bottom + k) * Tall, z);
                    if (_dug.Contains(p) || Overlaps(p)) continue;
                    Insert(p, Resource.Stone);
                    _natural.Add(p);
                    if (Vein(p) is { } ore) _veins[p] = ore;
                    laid.Add(p);
                }
            }
            foreach (var p in laid) MarkDirty(p);
            _spires[(spire.X, spire.Z)] = laid;
            changed = true;
        }
        if (changed) Version++;
    }

    // Veins: thin sheets winding through the rock where a 3D noise crosses zero, broken into patches
    // by a second noise; crystal veins are thinner and rarer than glass ones, and win where they cross.
    private const float VeinScale = 0.11f;                      // per metre: sheets a few metres apart
    private const float GlassVeinWidth = 0.09f, CrystalVeinWidth = 0.02f;

    private static Resource? Vein(BlockPos p)
    {
        float x = p.X + 0.5f, y = p.Bottom + Size / 2, z = p.Z + 0.5f;
        bool Sheet(int seed, float width, float patches) =>
            MathF.Abs(ValueNoise(x * VeinScale, y * VeinScale, z * VeinScale, seed)) < width
            && ValueNoise(x * 0.05f, y * 0.05f, z * 0.05f, seed + 1) > patches;
        if (Sheet(71, CrystalVeinWidth, 0.25f)) return Resource.Crystal;
        if (Sheet(37, GlassVeinWidth, -0.2f)) return Resource.Glass;
        return null;
    }

    // Smooth 3D value noise in [-1, 1].
    private static float ValueNoise(float x, float y, float z, int seed)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y), z0 = (int)MathF.Floor(z);
        float fx = x - x0, fy = y - y0, fz = z - z0;
        fx *= fx * (3 - 2 * fx);
        fy *= fy * (3 - 2 * fy);
        fz *= fz * (3 - 2 * fz);
        float Corner(int i, int j, int k)
        {
            unchecked
            {
                uint h = (uint)((x0 + i) * 374761393 + (y0 + j) * 668265263 + (z0 + k) * 1274126177 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xFFFF) / 32767.5f - 1f;
            }
        }
        float Lerp(float a, float b, float t) => a + (b - a) * t;
        return Lerp(
            Lerp(Lerp(Corner(0, 0, 0), Corner(1, 0, 0), fx), Lerp(Corner(0, 1, 0), Corner(1, 1, 0), fx), fy),
            Lerp(Lerp(Corner(0, 0, 1), Corner(1, 0, 1), fx), Lerp(Corner(0, 1, 1), Corner(1, 1, 1), fx), fy), fz);
    }

    public List<DugSave> SaveDug() => _dug.Select(p => new DugSave(p.X, p.Y, p.Z)).ToList();

    /// <summary>Restores what was dug out of the spires (before they are laid).</summary>
    public void LoadDug(IEnumerable<DugSave> dug)
    {
        _dug.Clear();
        foreach (var d in dug) _dug.Add(new BlockPos(d.X, d.Y, d.Z));
    }

    private void Changed(BlockPos p)
    {
        Version++;
        MarkDirty(p);
        UpdateCleared();
    }

    // The block's chunk, and its neighbours' (their faces against it appear or disappear).
    private void MarkDirty(BlockPos p)
    {
        _dirty.Add(ChunkOf(p.X - 1, p.Z - 1));
        _dirty.Add(ChunkOf(p.X + 1, p.Z - 1));
        _dirty.Add(ChunkOf(p.X - 1, p.Z + 1));
        _dirty.Add(ChunkOf(p.X + 1, p.Z + 1));
    }

    public static (int X, int Z) ChunkOf(int x, int z) => ((int)MathF.Floor(x / (float)ChunkSize), (int)MathF.Floor(z / (float)ChunkSize));

    /// <summary>The chunks whose mesh must be rebuilt since the last call.</summary>
    public List<(int X, int Z)> TakeDirtyChunks()
    {
        var chunks = _dirty.ToList();
        _dirty.Clear();
        return chunks;
    }

    /// <summary>The blocks in a chunk.</summary>
    public IEnumerable<(BlockPos Position, Resource Resource)> InChunk((int X, int Z) chunk)
    {
        for (int z = chunk.Z * ChunkSize; z < (chunk.Z + 1) * ChunkSize; z++)
        for (int x = chunk.X * ChunkSize; x < (chunk.X + 1) * ChunkSize; x++)
        {
            if (!_columns.TryGetValue((x, z), out var list)) continue;
            foreach (int y in list) yield return (new BlockPos(x, y, z), _blocks[new BlockPos(x, y, z)]);
        }
    }

    /// <summary>The blocks the player placed (the spires' are generated).</summary>
    public List<BlockSave> Save() => _blocks.Where(b => !_natural.Contains(b.Key)).Select(b => new BlockSave(b.Value, b.Key.X, b.Key.Y, b.Key.Z)).ToList();

    /// <summary>Replaces every block (before the world around is generated: no events).</summary>
    public void Load(IEnumerable<BlockSave> blocks)
    {
        _blocks.Clear();
        _columns.Clear();
        _crystals.Clear();
        _placed.Clear();
        foreach (var b in blocks)
        {
            var p = new BlockPos(b.X, b.Y, b.Z);
            if (!Enum.IsDefined(b.Resource) || b.Resource == Resource.Seeds || Overlaps(p)) continue;
            Insert(p, b.Resource);
            _placed[(p.X, p.Z)] = _placed.GetValueOrDefault((p.X, p.Z)) + 1;
            _dirty.Add(ChunkOf(p.X, p.Z));
        }
        Version++;
        _cleared = ComputeCleared();
    }

    // ---- The grass around the blocks -----------------------------------------------------------

    /// <summary>How grown the grass is at (x, z): 0 where blocks cleared it, up to 1 where it is untouched. Thread-safe.</summary>
    public float GrassGrowth(float x, float z)
    {
        if (_terrain.SpireHeight(x, z) > 0f) return 0f; // nothing grows in the rock of the spires
        var column = ColumnOf(x, z);
        if (_cleared.Contains(column)) return 0f;
        if (_planted.TryGetValue(column, out long planted)) return PlantedGrowth(planted);
        if (_torn.Contains(column)) return 0f;
        if (_regrowing.TryGetValue(column, out long start)) return Math.Clamp((Environment.TickCount64 - start) / (RegrowSeconds * 1000f), 0f, 1f);
        return 1f;
    }

    // Small when just planted, full grown after PlantGrowSeconds.
    private static float PlantedGrowth(long plantedAt) => Math.Clamp(0.08f + (UnixNow - plantedAt) / (PlantGrowSeconds * 1000f), 0f, 1f);

    /// <summary>Whether grass was planted at (x, z). Thread-safe.</summary>
    public bool Planted(float x, float z) => _planted.ContainsKey(ColumnOf(x, z));

    /// <summary>Whether the grass at (x, z) was torn up (and not planted again).</summary>
    public bool Torn(float x, float z) => _torn.Contains(ColumnOf(x, z)) && !Planted(x, z);

    /// <summary>The columns of the terrain tile containing (x, z).</summary>
    public static IEnumerable<(int X, int Z)> TileColumns(float x, float z)
    {
        int size = (int)TerrainField.TileSize;
        int x0 = (int)MathF.Floor(x / size) * size, z0 = (int)MathF.Floor(z / size) * size;
        for (int dz = 0; dz < size; dz++)
        for (int dx = 0; dx < size; dx++)
            yield return (x0 + dx, z0 + dz);
    }

    /// <summary>
    /// Tears up the grass of some columns, for good: nothing grows there again unless planted.
    /// Returns how many of them held grass (<paramref name="grassy"/>), for the seeds it gives.
    /// </summary>
    public int TearUp(IEnumerable<(int X, int Z)> columns, Func<int, int, bool> grassy)
    {
        var torn = new HashSet<(int X, int Z)>(_torn);
        var planted = new Dictionary<(int X, int Z), long>(_planted);
        var regrowing = new Dictionary<(int X, int Z), long>(_regrowing);
        var changed = new List<(int X, int Z)>();
        int withGrass = 0;
        foreach (var column in columns)
        {
            if (_cleared.Contains(column)) continue;
            if (grassy(column.X, column.Z)) withGrass++;
            torn.Add(column);
            planted.Remove(column);
            regrowing.Remove(column);
            _plantedGrowing.Remove(column);
            changed.Add(column);
        }
        if (withGrass == 0) return 0;
        (_torn, _planted, _regrowing) = (torn, planted, regrowing);
        PlantingsVersion++;
        RaiseGroundChanged(changed);
        return withGrass;
    }

    /// <summary>Whether any of some columns could be planted (see <see cref="Plant"/>).</summary>
    public bool CanPlant(IEnumerable<(int X, int Z)> columns, Func<int, int, bool> bare) =>
        columns.Any(c => !_cleared.Contains(c) && !_planted.ContainsKey(c) && bare(c.X, c.Z));

    /// <summary>Plants grass in some columns (where <paramref name="bare"/> says it has none): it starts small and grows.</summary>
    public int Plant(IEnumerable<(int X, int Z)> columns, Func<int, int, bool> bare)
    {
        var planted = new Dictionary<(int X, int Z), long>(_planted);
        var changed = new List<(int X, int Z)>();
        long now = UnixNow;
        foreach (var column in columns)
        {
            if (_cleared.Contains(column) || planted.ContainsKey(column) || !bare(column.X, column.Z)) continue;
            planted[column] = now;
            _plantedGrowing.Add(column);
            changed.Add(column);
        }
        if (changed.Count == 0) return 0;
        _planted = planted;
        PlantingsVersion++;
        _nextRegrowTick = 0;
        RaiseGroundChanged(changed);
        return changed.Count;
    }

    public List<ColumnSave> SaveTorn() => _torn.Select(c => new ColumnSave(c.X, c.Z, 0)).ToList();
    public List<ColumnSave> SavePlanted() => _planted.Select(p => new ColumnSave(p.Key.X, p.Key.Z, p.Value)).ToList();

    /// <summary>Restores the torn-up and planted grass (before the world around is generated).</summary>
    public void LoadGrass(IEnumerable<ColumnSave> torn, IEnumerable<ColumnSave> planted)
    {
        _torn = torn.Select(c => (c.X, c.Z)).ToHashSet();
        _planted = planted.ToDictionary(p => (p.X, p.Z), p => p.At);
        _plantedGrowing.Clear();
        foreach (var (column, at) in _planted)
            if (PlantedGrowth(at) < 1f) _plantedGrowing.Add(column);
        PlantingsVersion++;
    }

    // One GroundChanged per 32 m cell (the size of the grass tiles and of the tree cells, which
    // rebuild whole): a mown patch or a stretch of grass grown back spans only a few.
    private void RaiseGroundChanged(IEnumerable<(int X, int Z)> columns)
    {
        const int cell = 32;
        foreach (var column in columns.DistinctBy(c => (Math.Floor(c.X / (double)cell), Math.Floor(c.Z / (double)cell))))
            GroundChanged?.Invoke(column.X + 0.5f, column.Z + 0.5f);
    }

    /// <summary>Lets the grass grow back: now and then the columns growing are announced, those grown fully released.</summary>
    public void Update()
    {
        var regrowing = _regrowing;
        long now = Environment.TickCount64;
        if ((regrowing.Count == 0 && _plantedGrowing.Count == 0) || now < _nextRegrowTick) return;
        _nextRegrowTick = now + 2500;
        if (_plantedGrowing.Count > 0)
        {
            // Planted grass growing: shown taller now and then, and let go of once grown.
            var planted = _planted;
            var grown = _plantedGrowing.Where(c => !planted.TryGetValue(c, out long at) || PlantedGrowth(at) >= 1f).ToList();
            foreach (var column in grown) _plantedGrowing.Remove(column);
            if (grown.Count > 0) RaiseGroundChanged(grown);
            foreach (var column in _plantedGrowing) GrassGrowing?.Invoke(column.X + 0.5f, column.Z + 0.5f);
        }
        var done = regrowing.Where(r => now - r.Value >= RegrowSeconds * 1000).Select(r => r.Key).ToList();
        if (done.Count > 0)
        {
            var next = new Dictionary<(int X, int Z), long>(regrowing);
            foreach (var column in done) next.Remove(column);
            _regrowing = next;
            RaiseGroundChanged(done);
        }
        foreach (var column in _regrowing.Keys) GrassGrowing?.Invoke(column.X + 0.5f, column.Z + 0.5f);
    }

    private void UpdateCleared()
    {
        var before = _cleared;
        var after = ComputeCleared();
        _cleared = after;
        var regrowing = new Dictionary<(int X, int Z), long>(_regrowing);
        foreach (var column in before.Where(c => !after.Contains(c))) regrowing[column] = Environment.TickCount64;
        foreach (var column in after) regrowing.Remove(column);
        _regrowing = regrowing;
        foreach (var column in before.Where(c => !after.Contains(c)).Concat(after.Where(c => !before.Contains(c))))
            GroundChanged?.Invoke(column.X + 0.5f, column.Z + 0.5f);
    }

    // A placed block near the ground clears its column and the ring around it, so no blade pokes
    // through it (the spires keep their footprint bare by themselves: see GrassGrowth).
    private HashSet<(int X, int Z)> ComputeCleared()
    {
        var cleared = new HashSet<(int X, int Z)>();
        foreach (var (x, z) in _placed.Keys)
        {
            float ground = GroundAt(x, z);
            if (_columns[(x, z)].Where(y => !_natural.Contains(new BlockPos(x, y, z))).Min() * Step - ground > 1.5f) continue;
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                cleared.Add((x + dx, z + dz));
        }
        return cleared;
    }

    // ---- Under a roof ------------------------------------------------------------------------

    /// <summary>
    /// Whether a point is under a block (a roof, a ceiling, a bridge): butterflies, fireflies, rain,
    /// snow and lightning stay out.
    /// </summary>
    public bool Indoors(Vector3 p)
    {
        if (!_columns.TryGetValue(ColumnOf(p.X, p.Z), out var list)) return false;
        return p.Y >= _terrain.Height(p.X, p.Z) - 1f && list.Any(y => y * Step > p.Y);
    }

    /// <summary>For each column with blocks: the ground under it, less a metre, and the bottom of its highest block (under which it is indoors).</summary>
    public IEnumerable<((int X, int Z) Column, float Floor, float Ceiling)> Ceilings() =>
        _columns.Select(c => (c.Key, GroundAt(c.Key.X, c.Key.Z) - 1f, c.Value.Max() * Step));

    // ---- What the player meets ---------------------------------------------------------------

    /// <summary>Whether a point is within <paramref name="margin"/> of a block (inside it, or nearly touching it).</summary>
    public bool Near(Vector3 p, float margin)
    {
        for (int z = (int)MathF.Floor(p.Z - margin); z <= (int)MathF.Floor(p.Z + margin); z++)
        for (int x = (int)MathF.Floor(p.X - margin); x <= (int)MathF.Floor(p.X + margin); x++)
        {
            if (!_columns.TryGetValue((x, z), out var list)) continue;
            foreach (int y in list)
                if (p.Y > y * Step - margin && p.Y < y * Step + Size + margin) return true;
        }
        return false;
    }

    /// <summary>Whether a point is inside a block.</summary>
    public bool Inside(Vector3 p) =>
        _columns.TryGetValue(ColumnOf(p.X, p.Z), out var list) && list.Any(y => y * Step <= p.Y && p.Y < y * Step + Size);

    /// <summary>
    /// The highest block top under a disc of <paramref name="radius"/> around (x, z) among the blocks
    /// whose bottom is at height <paramref name="y"/> or below (like <see cref="IslandField.GroundBelow"/>):
    /// the player stands on a block while overlapping it, and blocks above the head do not count.
    /// </summary>
    public bool GroundBelow(float x, float z, float y, out float top, float radius = 0.2f)
    {
        top = float.MinValue;
        for (int cz = (int)MathF.Floor(z - radius); cz <= (int)MathF.Floor(z + radius); cz++)
        for (int cx = (int)MathF.Floor(x - radius); cx <= (int)MathF.Floor(x + radius); cx++)
        {
            if (!_columns.TryGetValue((cx, cz), out var list)) continue;
            foreach (int b in list)
                if (b * Step <= y) top = MathF.Max(top, b * Step + Size);
        }
        return top > float.MinValue;
    }

    /// <summary>
    /// Pushes the player (feet at <paramref name="feet"/>, a cylinder <paramref name="radius"/> wide and
    /// <paramref name="height"/> tall) out of the blocks beside them, and stops them rising into a block
    /// overhead. Blocks at the feet, no higher than <paramref name="climb"/>, are the ground's business.
    /// </summary>
    public void ResolveCollision(ref Vector3 feet, ref Vector3 velocity, float radius, float height, float climb)
    {
        for (int cz = (int)MathF.Floor(feet.Z - radius); cz <= (int)MathF.Floor(feet.Z + radius); cz++)
        for (int cx = (int)MathF.Floor(feet.X - radius); cx <= (int)MathF.Floor(feet.X + radius); cx++)
        {
            if (!_columns.TryGetValue((cx, cz), out var list)) continue;
            foreach (int b in list)
            {
                float bottom = b * Step, top = bottom + Size;
                if (top <= feet.Y + climb || bottom >= feet.Y + height) continue;
                float nx = Math.Clamp(feet.X, cx, cx + 1), nz = Math.Clamp(feet.Z, cz, cz + 1);
                float ox = feet.X - nx, oz = feet.Z - nz, d2 = ox * ox + oz * oz;
                if (d2 >= radius * radius) continue;
                if (velocity.Y > 0 && bottom > feet.Y + height - 0.45f)
                {
                    // Head against a block while rising: stop under it.
                    feet.Y = bottom - height;
                    velocity.Y = 0;
                    continue;
                }
                if (d2 > 1e-8f)
                {
                    float d = MathF.Sqrt(d2);
                    feet.X = nx + ox / d * radius;
                    feet.Z = nz + oz / d * radius;
                }
                else
                {
                    // Inside the column: out the nearest side.
                    float left = feet.X - cx, right = cx + 1 - feet.X, back = feet.Z - cz, front = cz + 1 - feet.Z;
                    float least = MathF.Min(MathF.Min(left, right), MathF.Min(back, front));
                    if (least == left) feet.X = cx - radius;
                    else if (least == right) feet.X = cx + 1 + radius;
                    else if (least == back) feet.Z = cz - radius;
                    else feet.Z = cz + 1 + radius;
                }
            }
        }
    }

    /// <summary>The nearest block along the ray within <paramref name="maxDistance"/>: the columns are walked in order (2D DDA).</summary>
    public BlockHit? Raycast(Vector3 origin, Vector3 direction, float maxDistance)
    {
        int x = (int)MathF.Floor(origin.X), z = (int)MathF.Floor(origin.Z);
        int stepX = direction.X > 0 ? 1 : -1, stepZ = direction.Z > 0 ? 1 : -1;
        float tDeltaX = MathF.Abs(direction.X) > 1e-6f ? MathF.Abs(1f / direction.X) : float.MaxValue;
        float tDeltaZ = MathF.Abs(direction.Z) > 1e-6f ? MathF.Abs(1f / direction.Z) : float.MaxValue;
        float tMaxX = MathF.Abs(direction.X) > 1e-6f ? ((stepX > 0 ? x + 1 - origin.X : origin.X - x) * tDeltaX) : float.MaxValue;
        float tMaxZ = MathF.Abs(direction.Z) > 1e-6f ? ((stepZ > 0 ? z + 1 - origin.Z : origin.Z - z) * tDeltaZ) : float.MaxValue;
        float entered = 0f;
        BlockHit? best = null;
        float nearest = maxDistance;
        while (entered <= nearest)
        {
            if (_columns.TryGetValue((x, z), out var list))
                foreach (int b in list)
                {
                    var p = new BlockPos(x, b, z);
                    var box = new Box(new Vector3(x, p.Bottom, z), new Vector3(x + 1, p.Top, z + 1));
                    if (!RayBox(origin, direction, box, out float t, out var normal) || t > nearest) continue;
                    nearest = t;
                    best = new BlockHit(p, origin + direction * t, normal, t);
                }
            if (tMaxX < tMaxZ) { entered = tMaxX; tMaxX += tDeltaX; x += stepX; }
            else { entered = tMaxZ; tMaxZ += tDeltaZ; z += stepZ; }
            if (entered > maxDistance) break;
        }
        return best;
    }

    private static bool RayBox(Vector3 origin, Vector3 direction, Box box, out float t, out Vector3 normal)
    {
        float tMin = 0f, tMax = float.MaxValue;
        normal = Vector3.UnitY;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = origin[axis], d = direction[axis], lo = box.Min[axis], hi = box.Max[axis];
            if (MathF.Abs(d) < 1e-7f)
            {
                if (o < lo || o > hi) { t = 0; return false; }
                continue;
            }
            float t0 = (lo - o) / d, t1 = (hi - o) / d;
            float sign = -1f;
            if (t0 > t1) { (t0, t1) = (t1, t0); sign = 1f; }
            if (t0 > tMin)
            {
                tMin = t0;
                normal = Vector3.Zero;
                normal[axis] = sign;
            }
            tMax = MathF.Min(tMax, t1);
            if (tMin > tMax) { t = 0; return false; }
        }
        t = tMin;
        return true;
    }

    // ---- Placing -------------------------------------------------------------------------

    /// <summary>
    /// Where the next block goes: against the face of the block aimed at, or on the ground aimed at
    /// (on the terrain tile's top, so it sits on it). Not where it would overlap a block, the player
    /// (<paramref name="feet"/>, a cylinder <paramref name="radius"/> × <paramref name="height"/>),
    /// a trunk (<paramref name="trunkIn"/>, a rectangle in xz) or where it would be buried.
    /// </summary>
    public BlockPlan? Plan(Vector3 eye, Vector3 look, float reach, Func<Vector2, Vector2, bool> trunkIn, Vector3 feet, float radius, float height)
    {
        var hit = Raycast(eye, look, reach);
        bool groundHit = _terrain.Raycast(eye, look, reach, out var ground);
        BlockPos p;
        Vector3 normal;
        if (hit is { } h && (!groundHit || h.Distance <= Vector3.Distance(eye, ground) + 0.01f))
        {
            var n = h.Normal;
            normal = n;
            p = n.Y > 0.5f ? h.Block with { Y = h.Block.Y + Tall }
                : n.Y < -0.5f ? h.Block with { Y = h.Block.Y - Tall }
                : new BlockPos(h.Block.X + (int)MathF.Round(n.X), h.Block.Y, h.Block.Z + (int)MathF.Round(n.Z));
        }
        else if (groundHit)
        {
            // The column aimed at (stepping back a hair, so a hit on a terrain step picks the tile in
            // front of it), on the top of its tile.
            var aimed = ground - look * 0.02f;
            var (x, z) = ColumnOf(aimed.X, aimed.Z);
            p = new BlockPos(x, (int)MathF.Round(_terrain.Height(x + 0.5f, z + 0.5f) / Step), z);
            normal = Vector3.UnitY;
        }
        else return null;

        return new BlockPlan(p, normal, Problem(p, trunkIn, feet, radius, height));
    }

    /// <summary>Why a block cannot go at <paramref name="p"/> (null if it can): see <see cref="Plan"/>.</summary>
    public string? Problem(BlockPos p, Func<Vector2, Vector2, bool> trunkIn, Vector3 feet, float radius, float height)
    {
        if (Overlaps(p)) return "Qui c'è già un blocco";
        if (p.Top <= _terrain.Height(p.X + 0.5f, p.Z + 0.5f) + 0.01f) return "Sotto terra";
        // Not into the player.
        float nx = Math.Clamp(feet.X, p.X, p.X + 1), nz = Math.Clamp(feet.Z, p.Z, p.Z + 1);
        if ((feet.X - nx) * (feet.X - nx) + (feet.Z - nz) * (feet.Z - nz) < radius * radius
            && p.Bottom < feet.Y + height && p.Top > feet.Y + 0.05f) return "Sei in mezzo";
        float ground = _terrain.Height(p.X + 0.5f, p.Z + 0.5f);
        if (p.Bottom - ground < 6f && trunkIn(new Vector2(p.X - 0.3f, p.Z - 0.3f), new Vector2(p.X + 1.3f, p.Z + 1.3f)))
            return "Un albero o una roccia è d'intralcio";
        return null;
    }

    // ---- Light -------------------------------------------------------------------------------

    public static readonly Vector3 CrystalLight = new(0.66f, 0.46f, 1.0f);

    /// <summary>The nearest crystal blocks within <paramref name="radius"/> (at most <paramref name="max"/>), as point lights.</summary>
    public IEnumerable<PointLight> Lights(Vector3 center, float radius, int max = 6) => _crystals
        .Where(p => Vector3.DistanceSquared(p.Center, center) < radius * radius)
        .OrderBy(p => Vector3.DistanceSquared(p.Center, center))
        .Take(max)
        .Select(p => new PointLight(p.Center, CrystalLight * 1.5f, 7f));
}

/// <summary>An axis-aligned box in world space.</summary>
public readonly record struct Box(Vector3 Min, Vector3 Max);
