using System.Collections.Concurrent;
using System.Numerics;

namespace Mine.World;

/// <summary>What a block is made of.</summary>
public enum BlockMaterial : byte
{
    LilacStone,
    IndigoSlate,
    RoseSandstone,
    TealStone,
    CarvedStone, // carved panels (for ruins)
    FlutedStone, // fluted like a column (for ruins)
    Sod,         // earth that takes the look of the ground where it lies: the mountains' body
    CaveRock,    // the dark stone of the caverns' walls
    CrystalVein, // glowing crystal in the caverns' walls
}

/// <summary>
/// What fills one cell of the building grid (<see cref="BlockWorld.CellSize"/> = 0.5 m): one
/// eighth of a 1 m block, <see cref="Part"/> telling which (bit 0: +X half, bit 1: upper half,
/// bit 2: +Z half). Its shape is a profile drawn on a 4 × 4 grid (bit row * 4 + col; row 0 at the
/// bottom, col 0 on the left as seen when placing it) extruded through the cell's depth, turned
/// about Y by <see cref="Rotation"/> quarter turns: for now blocks are whole cubes (<see cref="Profiles.Full"/>).
/// <see cref="Natural"/> blocks were generated with the world (the scree at the foot of slopes) and
/// Natural or placed, a broken block leaves a small copy of itself to pick up.
/// </summary>
public readonly record struct Piece(BlockMaterial Material, ushort Profile, byte Rotation, bool Natural, byte Part = 0)
{
    /// <summary>The lowest corner cell of the block this piece belongs to, given the piece's own cell.</summary>
    public Cell Anchor(Cell cell) => cell.Offset(-(Part & 1), -(Part >> 1 & 1), -(Part >> 2 & 1));
}

public readonly record struct Cell(int X, int Y, int Z)
{
    public static Cell Of(Vector3 p) => new(
        (int)MathF.Floor(p.X / BlockWorld.CellSize), (int)MathF.Floor(p.Y / BlockWorld.CellSize), (int)MathF.Floor(p.Z / BlockWorld.CellSize));
    public Vector3 Min => new Vector3(X, Y, Z) * BlockWorld.CellSize;
    public Cell Offset(int dx, int dy, int dz) => new(X + dx, Y + dy, Z + dz);
}

public static class Profiles
{
    public const ushort Full = 0xFFFF;

    public static bool Has(ushort profile, int row, int col) => (profile >> (row * 4 + col) & 1) != 0;

    public static int Count(ushort profile) => System.Numerics.BitOperations.PopCount(profile);

    /// <summary>The profile column a fine voxel of the cell (sx, sz in 0..3) falls in, for a rotation.</summary>
    public static int Column(int rotation, int sx, int sz) => rotation switch
    {
        0 => sx,
        1 => sz,
        2 => 3 - sx,
        _ => 3 - sz,
    };

    public static string Name(ushort profile) => profile switch
    {
        Full => "blocco",
        0x00FF => "lastra",
        0x000F => "lastra sottile",
        0x0FF0 => "trave",
        0x3333 => "mezzo muro",
        0x6666 => "pilastro",
        0x137F => "scala",
        0xFF99 => "arco",
        0xF99F => "finestra",
        0x99FF => "merlo",
        0xF731 => "mensola",
        _ => $"forma {Count(profile)}/16",
    };
}

public static class Materials
{
    public static string Name(BlockMaterial material) => material switch
    {
        BlockMaterial.LilacStone => "pietra porosa lilla",
        BlockMaterial.IndigoSlate => "ardesia indaco",
        BlockMaterial.RoseSandstone => "arenaria rosa",
        BlockMaterial.TealStone => "pietra turchese",
        BlockMaterial.CarvedStone => "pietra scolpita",
        BlockMaterial.FlutedStone => "pietra scanalata",
        BlockMaterial.Sod => "zolla",
        BlockMaterial.CaveRock => "roccia di caverna",
        BlockMaterial.CrystalVein => "vena di cristallo",
        _ => "?",
    };

    public static Vector3 Color(BlockMaterial material) => material switch
    {
        BlockMaterial.LilacStone => new Vector3(0.55f, 0.46f, 0.66f),
        BlockMaterial.IndigoSlate => new Vector3(0.33f, 0.32f, 0.54f),
        BlockMaterial.RoseSandstone => new Vector3(0.76f, 0.53f, 0.56f),
        BlockMaterial.TealStone => new Vector3(0.33f, 0.55f, 0.58f),
        BlockMaterial.CarvedStone => new Vector3(0.60f, 0.56f, 0.70f),
        BlockMaterial.FlutedStone => new Vector3(0.68f, 0.64f, 0.76f),
        BlockMaterial.Sod => new Vector3(0.36f, 0.30f, 0.46f), // only for icons: in the world it looks like the ground
        BlockMaterial.CaveRock => new Vector3(0.30f, 0.26f, 0.38f),
        BlockMaterial.CrystalVein => new Vector3(0.45f, 0.8f, 1.0f),
        _ => Vector3.One,
    };

    /// <summary>How much a material glows by itself (0 for stone).</summary>
    public static float Emissive(BlockMaterial material) => material == BlockMaterial.CrystalVein ? 0.85f : 0f;

    /// <summary>
    /// The motif the block shader draws on a material (TerrainShaders.BlockFragment): 0 plain
    /// stone, 1 column flutes, 2 carved panel, 3 the look of the ground where the block lies.
    /// </summary>
    public static float Pattern(BlockMaterial material) => material switch
    {
        BlockMaterial.FlutedStone => 1f,
        BlockMaterial.CarvedStone => 2f,
        BlockMaterial.Sod => 3f,
        _ => 0f,
    };
}

/// <summary>
/// Where breakable stone lies: scree at the foot of the slopes, where rock would have come down
/// from the hill above. A few sites per 64 m cell in the worlds that have them
/// (<see cref="WorldPreset.StoneFields"/>), each a heap of 1 m blocks leaning against the rise,
/// with a few loose blocks tumbled further down. The stone takes the colour of the land around
/// (indigo slate in the indigo woods, rose sandstone in the pink ones and the deserts, teal stone
/// by the turquoise woods), with lilac porous stone anywhere. A pure function of the seed and the
/// terrain, so grass and trees can keep off them from any thread.
/// </summary>
public sealed class StoneOutcrops(TerrainField terrain, int seed)
{
    public const float CellSize = 64f;
    private const float Probe = 7f;     // how far round a spot the rise of the land is measured
    private const float MinRise = 2.5f; // metres the land must climb within Probe to count as a hill's foot

    /// <summary>
    /// A heap of scree: its middle, the direction up the slope it leans on, its half-length across
    /// the slope and half-depth along it, its height, and its stone (and a second one mixed in).
    /// </summary>
    public readonly record struct Outcrop(float X, float Z, Vector2 Uphill, float Length, float Depth, float Height,
                                          BlockMaterial Material, BlockMaterial Mixed, int Seed)
    {
        /// <summary>How far round the middle the heap and its loose blocks may reach.</summary>
        public float Reach => MathF.Max(Length, Depth) * 1.2f + 7f;
    }

    private readonly ConcurrentDictionary<(int, int), Outcrop[]> _cache = new();

    public Outcrop[] In(int cx, int cz) => _cache.GetOrAdd((cx, cz), key => Generate(key.Item1, key.Item2));

    /// <summary>
    /// Whether a heap (or its loose blocks) or a mountain may lie near (x, z), within
    /// <paramref name="margin"/> metres more; before a mountain's tunnel a clearing is kept too.
    /// </summary>
    public bool Covers(float x, float z, float margin)
    {
        if (WorldPreset.Current.StoneFields <= 0) return false;
        foreach (var m in terrain.Mountains.Near(x, z, 20f + margin))
        {
            var rel = new Vector2(x - m.X, z - m.Z);
            float r = m.Radius + 1.5f + margin;
            if (rel.LengthSquared() < r * r) return true;
            float along = Vector2.Dot(rel, m.Mouth), side = MathF.Abs(Vector2.Dot(rel, new Vector2(-m.Mouth.Y, m.Mouth.X)));
            if (along > 0 && along < m.Radius + 16f + margin && side < 6f + margin) return true;
        }
        int cx = (int)MathF.Floor(x / CellSize), cz = (int)MathF.Floor(z / CellSize);
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
            foreach (var o in In(cx + dx, cz + dz))
            {
                float r = o.Reach + margin;
                if ((x - o.X) * (x - o.X) + (z - o.Z) * (z - o.Z) < r * r) return true;
            }
        return false;
    }

    private Outcrop[] Generate(int cx, int cz)
    {
        float chance = WorldPreset.Current.StoneFields;
        if (chance <= 0) return [];
        var random = new Random((int)Hash(cx, cz));
        var list = new List<Outcrop>();
        int attempts = (int)(24 * chance);
        for (int i = 0; i < attempts && list.Count < 2; i++)
        {
            float x = (cx + 0.1f + 0.8f * random.NextSingle()) * CellSize;
            float z = (cz + 0.1f + 0.8f * random.NextSingle()) * CellSize;
            float length = 2.5f + 4f * random.NextSingle(), depth = 1.5f + 1.5f * random.NextSingle();
            float height = 1.5f + 2.5f * random.NextSingle();
            int stoneSeed = random.Next();
            float roll = random.NextSingle(), mixRoll = random.NextSingle();
            float ground = terrain.Height(x, z);
            if (ground < TerrainField.WaterLevel + 1.5f || terrain.Normal(x, z, 2f).Y < 0.85f || terrain.InPond(x, z, 12f)) continue;

            // The foot of a hill: the land climbs steeply in some direction close by.
            float bestRise = 0;
            var uphill = Vector2.Zero;
            for (int k = 0; k < 8; k++)
            {
                var dir = new Vector2(MathF.Cos(k * MathF.PI / 4), MathF.Sin(k * MathF.PI / 4));
                float rise = terrain.SmoothHeight(x + dir.X * Probe, z + dir.Y * Probe) - ground;
                if (rise > bestRise) (bestRise, uphill) = (rise, dir);
            }
            if (bestRise < MinRise) continue;
            if (list.Any(o => (o.X - x) * (o.X - x) + (o.Z - z) * (o.Z - z) < 30f * 30f)) continue;
            if (terrain.Mountains.Near(x, z, 20f).Any()) continue; // leave the mountains alone

            // The stone of the land around, now and then lilac porous stone; a little of another mixed in.
            var w = GroundMaterials.Biome(x, z);
            var local = w.W >= MathF.Max(w.X, MathF.Max(w.Y, w.Z)) ? BlockMaterial.RoseSandstone
                      : w.Y >= MathF.Max(w.X, w.Z) ? BlockMaterial.RoseSandstone
                      : w.Z >= w.X ? BlockMaterial.TealStone : BlockMaterial.IndigoSlate;
            var material = roll < 0.35f ? BlockMaterial.LilacStone : local;
            var mixed = mixRoll < 0.5f ? BlockMaterial.LilacStone : local;
            // The heap's middle sits against the rise.
            list.Add(new Outcrop(x + uphill.X * depth * 0.5f, z + uphill.Y * depth * 0.5f, uphill, length, depth,
                                 height + 0.3f * MathF.Min(bestRise, 6f), material, mixed, stoneSeed));
        }
        return list.ToArray();
    }

    private uint Hash(int x, int z)
    {
        unchecked
        {
            uint h = (uint)(x * 73856093 ^ z * 19349663 ^ seed * 83492791 ^ 0x5A17);
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }
}

/// <summary>
/// The blocks in the world, on a grid of <see cref="CellSize"/> cells aligned with the terrain
/// (a terrain tile is 4 × 1 × 4 cells, its top on a cell boundary). Natural blocks are generated
/// around the player from <see cref="StoneOutcrops"/> and dropped far away (broken ones are
/// remembered); placed blocks stay. Blocks are kept per chunk of <see cref="ChunkCells"/>³ cells,
/// and chunks whose blocks change are listed in <see cref="Dirty"/> for the renderer.
/// Solid space is resolved at a finer grid of <see cref="Fine"/> voxels per cell side.
/// </summary>
public sealed class BlockWorld
{
    public const float CellSize = 0.5f;
    public const int BlockCells = 2; // a block is 2 × 2 × 2 cells: 1 m
    public const float BlockSize = CellSize * BlockCells;
    public const int Fine = 4;
    public const float FineSize = CellSize / Fine;
    public const int ChunkCells = 16;
    private const float ActiveRadius = 260f;

    public readonly record struct ChunkKey(int X, int Y, int Z)
    {
        public static ChunkKey Of(Cell c) => new(FloorDiv(c.X, ChunkCells), FloorDiv(c.Y, ChunkCells), FloorDiv(c.Z, ChunkCells));
    }

    private readonly TerrainField _terrain;
    private readonly Dictionary<ChunkKey, Dictionary<Cell, Piece>> _chunks = new();
    private readonly Dictionary<(int, int), int> _columns = new(); // blocks per column of cells
    private readonly Dictionary<(int, int), int> _tops = new();    // the highest cell of each column
    private readonly Dictionary<(int, int), List<Cell>> _mountainCells = new(); // natural blocks per mountain cell
    private const float MountainRadius = 700f; // mountains are laid this far away, so they never pop up near
    private readonly Dictionary<(int, int), List<Cell>> _generated = new(); // natural blocks per outcrop cell
    private readonly HashSet<Cell> _broken = new();
    private readonly List<(int, int)> _toDrop = new();

    public readonly HashSet<ChunkKey> Dirty = new();

    /// <summary>Goes up whenever a block is added or removed.</summary>
    public int Version { get; private set; }

    /// <summary>The columns of cells (X, Z) holding at least one block.</summary>
    public IEnumerable<(int X, int Z)> Columns => _columns.Keys;

    /// <summary>The height of the top of the highest block in a column of cells, if it has any.</summary>
    public float? Top(int x, int z) => _tops.TryGetValue((x, z), out int top) ? (top + 1) * CellSize : null;

    public BlockWorld(TerrainField terrain) => _terrain = terrain;

    public IEnumerable<ChunkKey> Chunks => _chunks.Keys;

    public IReadOnlyDictionary<Cell, Piece>? Chunk(ChunkKey key) => _chunks.GetValueOrDefault(key);

    public bool TryGet(Cell cell, out Piece piece)
    {
        piece = default;
        return _chunks.TryGetValue(ChunkKey.Of(cell), out var chunk) && chunk.TryGetValue(cell, out piece);
    }

    public bool Has(Cell cell) => TryGet(cell, out _);

    public void Set(Cell cell, Piece piece)
    {
        var key = ChunkKey.Of(cell);
        if (!_chunks.TryGetValue(key, out var chunk)) _chunks[key] = chunk = new Dictionary<Cell, Piece>();
        if (!chunk.ContainsKey(cell))
        {
            _columns[(cell.X, cell.Z)] = _columns.GetValueOrDefault((cell.X, cell.Z)) + 1;
            if (!_tops.TryGetValue((cell.X, cell.Z), out int top) || cell.Y > top) _tops[(cell.X, cell.Z)] = cell.Y;
        }
        chunk[cell] = piece;
        Touch(cell);
    }

    public bool Remove(Cell cell)
    {
        var key = ChunkKey.Of(cell);
        if (!_chunks.TryGetValue(key, out var chunk) || !chunk.Remove(cell, out var piece)) return false;
        if (chunk.Count == 0) _chunks.Remove(key);
        int left = _columns[(cell.X, cell.Z)] - 1;
        if (left == 0)
        {
            _columns.Remove((cell.X, cell.Z));
            _tops.Remove((cell.X, cell.Z));
        }
        else
        {
            _columns[(cell.X, cell.Z)] = left;
            if (_tops[(cell.X, cell.Z)] == cell.Y)
            {
                // The top went: look down for the next block.
                int y = cell.Y - 1;
                while (y > cell.Y - 2000 && !Has(new Cell(cell.X, y, cell.Z))) y--;
                _tops[(cell.X, cell.Z)] = y;
            }
        }
        if (piece.Natural) _broken.Add(cell);
        Touch(cell);
        return true;
    }

    /// <summary>The cells of the block anchored at <paramref name="anchor"/>, with their part numbers.</summary>
    public static IEnumerable<(Cell Cell, byte Part)> BlockCellsOf(Cell anchor)
    {
        for (byte part = 0; part < 8; part++)
            yield return (anchor.Offset(part & 1, part >> 1 & 1, part >> 2 & 1), part);
    }

    /// <summary>Whether a whole block anchored at <paramref name="anchor"/> fits in empty cells.</summary>
    public bool Fits(Cell anchor) => BlockCellsOf(anchor).All(c => !Has(c.Cell));

    public void PlaceBlock(Cell anchor, BlockMaterial material, bool natural)
    {
        foreach (var (cell, part) in BlockCellsOf(anchor))
            Set(cell, new Piece(material, Profiles.Full, 0, natural, part));
    }

    /// <summary>Removes the whole block one of whose cells is <paramref name="cell"/>.</summary>
    public bool RemoveBlock(Cell cell, out Piece piece, out Cell anchor)
    {
        anchor = default;
        if (!TryGet(cell, out piece)) return false;
        anchor = piece.Anchor(cell);
        foreach (var (c, part) in BlockCellsOf(anchor))
            if (TryGet(c, out var p) && p.Part == part && p.Natural == piece.Natural) Remove(c);
        return true;
    }

    /// <summary>Marks the cell's chunk dirty, and its neighbours when the cell is on their border.</summary>
    private void Touch(Cell cell)
    {
        Version++;
        var key = ChunkKey.Of(cell);
        Dirty.Add(key);
        int lx = cell.X - key.X * ChunkCells, ly = cell.Y - key.Y * ChunkCells, lz = cell.Z - key.Z * ChunkCells;
        if (lx == 0) Dirty.Add(key with { X = key.X - 1 });
        if (lx == ChunkCells - 1) Dirty.Add(key with { X = key.X + 1 });
        if (ly == 0) Dirty.Add(key with { Y = key.Y - 1 });
        if (ly == ChunkCells - 1) Dirty.Add(key with { Y = key.Y + 1 });
        if (lz == 0) Dirty.Add(key with { Z = key.Z - 1 });
        if (lz == ChunkCells - 1) Dirty.Add(key with { Z = key.Z + 1 });
    }

    /// <summary>Whether the fine voxel (in units of <see cref="FineSize"/>) is solid.</summary>
    public bool Solid(int fx, int fy, int fz)
    {
        var cell = new Cell(FloorDiv(fx, Fine), FloorDiv(fy, Fine), FloorDiv(fz, Fine));
        return TryGet(cell, out var piece) && Solid(piece, fx - cell.X * Fine, fy - cell.Y * Fine, fz - cell.Z * Fine);
    }

    /// <summary>Whether a piece fills its fine voxel (sx, sy, sz), each in 0..3.</summary>
    public static bool Solid(in Piece piece, int sx, int sy, int sz) =>
        Profiles.Has(piece.Profile, sy, Profiles.Column(piece.Rotation, sx, sz));

    /// <summary>Generates the scree and the mountains around the player and forgets the far ones.</summary>
    public void Update(Vector3 player)
    {
        if (WorldPreset.Current.StoneFields <= 0) return;
        const float size = StoneOutcrops.CellSize;
        int radius = (int)MathF.Ceiling(ActiveRadius / size);
        int pcx = (int)MathF.Floor(player.X / size), pcz = (int)MathF.Floor(player.Z / size);
        for (int cz = pcz - radius; cz <= pcz + radius; cz++)
        for (int cx = pcx - radius; cx <= pcx + radius; cx++)
            if (!_generated.ContainsKey((cx, cz)) && CellDistance(player, cx, cz) <= ActiveRadius)
                _generated[(cx, cz)] = Generate(cx, cz);

        _toDrop.Clear();
        foreach (var key in _generated.Keys)
            if (CellDistance(player, key.Item1, key.Item2) > ActiveRadius + size) _toDrop.Add(key);
        foreach (var key in _toDrop)
        {
            Forget(_generated[key]);
            _generated.Remove(key);
        }

        // The mountains, from much further away.
        const float mountainCell = Mountains.CellSize;
        int reach = (int)MathF.Ceiling(MountainRadius / mountainCell);
        int pmx = (int)MathF.Floor(player.X / mountainCell), pmz = (int)MathF.Floor(player.Z / mountainCell);
        for (int mz = pmz - reach; mz <= pmz + reach; mz++)
        for (int mx = pmx - reach; mx <= pmx + reach; mx++)
        {
            if (_mountainCells.ContainsKey((mx, mz))) continue;
            if (_terrain.Mountains.Of(mx, mz) is not { } m)
            {
                _mountainCells[(mx, mz)] = [];
                continue;
            }
            if (Vector2.Distance(new Vector2(m.X, m.Z), new Vector2(player.X, player.Z)) < MountainRadius)
                _mountainCells[(mx, mz)] = LayMountain(m);
        }
        _toDrop.Clear();
        foreach (var (key, cells) in _mountainCells)
            if (_terrain.Mountains.Of(key.Item1, key.Item2) is not { } m
                    ? MathF.Abs(key.Item1 - pmx) > reach + 1 || MathF.Abs(key.Item2 - pmz) > reach + 1
                    : Vector2.Distance(new Vector2(m.X, m.Z), new Vector2(player.X, player.Z)) > MountainRadius + 150f)
                _toDrop.Add(key);
        foreach (var key in _toDrop)
        {
            Forget(_mountainCells[key]);
            _mountainCells.Remove(key);
        }
    }

    /// <summary>Takes away generated blocks that are out of range (dropped, not broken: they come back).</summary>
    private void Forget(List<Cell> cells)
    {
        foreach (var cell in cells)
            if (TryGet(cell, out var piece) && piece.Natural)
            {
                Remove(cell);
                _broken.Remove(cell); // Remove remembered it as broken
            }
    }

    /// <summary>
    /// A mountain's blocks, column by column on the 1 m grid (<see cref="Mountains.Column"/>),
    /// less its cavern and tunnel. Only a skin is laid: the blocks within 3 m of the top, those
    /// showing on its flanks and those round the hollows; the heart, which nobody sees, stays
    /// empty. Sod outside (the look of the ground), dark cave rock round the hollows, with veins of
    /// glowing crystal here and there on the cavern's walls.
    /// </summary>
    private List<Cell> LayMountain(Mountain m)
    {
        var cells = new List<Cell>();
        var mountains = _terrain.Mountains;
        int x0 = (int)MathF.Floor(m.X - m.Radius), x1 = (int)MathF.Floor(m.X + m.Radius);
        int z0 = (int)MathF.Floor(m.Z - m.Radius), z1 = (int)MathF.Floor(m.Z + m.Radius);
        for (int bz = z0; bz <= z1; bz++)
        for (int bx = x0; bx <= x1; bx++)
        {
            int blocks = mountains.Column(m, bx, bz, out int groundCell);
            if (blocks == 0) continue;
            float ground = groundCell * CellSize;
            float top = ground + blocks * BlockSize;
            // The lowest neighbouring top: below it the column's side shows.
            float shown = top;
            foreach (var (nx, nz) in (ReadOnlySpan<(int, int)>)[(1, 0), (-1, 0), (0, 1), (0, -1)])
            {
                int n = mountains.Column(m, bx + nx, bz + nz, out int g);
                shown = MathF.Min(shown, g * CellSize + n * BlockSize);
            }
            for (int k = 0; k < blocks; k++)
            {
                var anchor = new Cell(bx * BlockCells, groundCell + k * BlockCells, bz * BlockCells);
                var center = new Vector3(bx + 0.5f, ground + k + 0.5f, bz + 0.5f);
                float cavern = Mountains.CavernDistance(m, center);
                // The cavern keeps at least a couple of metres of rock over and beside it; only the
                // tunnel breaks out.
                bool deep = top - center.Y > 2.5f && shown - center.Y > 1.5f;
                if (cavern < 1f && deep || Mountains.InTunnel(m, center, ground)) continue;
                bool nearCavern = cavern < 1f + 2.4f / m.CavernRadius;
                bool nearTunnel = Mountains.InTunnel(m, center + new Vector3(1.5f, 0, 0), ground) || Mountains.InTunnel(m, center - new Vector3(1.5f, 0, 0), ground)
                               || Mountains.InTunnel(m, center + new Vector3(0, 0, 1.5f), ground) || Mountains.InTunnel(m, center - new Vector3(0, 0, 1.5f), ground)
                               || Mountains.InTunnel(m, center + new Vector3(0, 1.5f, 0), ground);
                bool skin = top - center.Y < 3f || center.Y > shown - 1f;
                if (!skin && !nearCavern && !nearTunnel) continue; // the unseen heart
                if (_broken.Contains(anchor) || !Fits(anchor)) continue;
                var stone = BlockMaterial.Sod;
                bool outside = center.Y > top - 1f || center.Y > shown - 1f; // seen from outside: always sod
                if ((nearCavern || nearTunnel) && !outside)
                    stone = nearCavern && cavern < 1f + 1.2f / m.CavernRadius && Hash01(bx + k * 17, bz, m.Seed) < 0.05f
                        ? BlockMaterial.CrystalVein : BlockMaterial.CaveRock;
                PlaceBlock(anchor, stone, natural: true);
                cells.AddRange(BlockCellsOf(anchor).Select(c => c.Cell));
            }
        }
        return cells;
    }

    /// <summary>
    /// Lays the 1 m blocks of the scree of one cell on the terrain, column by column: a heap
    /// highest against the slope and lumpy at its rim, a few of a second stone mixed in, and loose
    /// blocks tumbled further down the slope.
    /// </summary>
    private List<Cell> Generate(int cx, int cz)
    {
        var cells = new List<Cell>();
        foreach (var o in _terrain.Outcrops.In(cx, cz))
        {
            var random = new Random(o.Seed);
            float phase = random.NextSingle() * 10f;
            var u = o.Uphill;
            var across = new Vector2(-u.Y, u.X);
            var middle = new Vector2(o.X, o.Z);

            void Lay(int x, int z, int blocks)
            {
                float px = (x + 0.5f) * BlockSize, pz = (z + 0.5f) * BlockSize;
                int ground = (int)MathF.Round(_terrain.Height(px, pz) / CellSize);
                for (int k = 0; k < blocks; k++)
                {
                    var anchor = new Cell(x * BlockCells, ground + k * BlockCells, z * BlockCells);
                    if (_broken.Contains(anchor) || !Fits(anchor)) continue;
                    var stone = Hash01(x + k * 31, z, o.Seed + 3) < 0.15f ? o.Mixed : o.Material;
                    PlaceBlock(anchor, stone, natural: true);
                    cells.AddRange(BlockCellsOf(anchor).Select(c => c.Cell));
                }
            }

            float extent = MathF.Max(o.Length, o.Depth) * 1.2f;
            int x0 = (int)MathF.Floor((o.X - extent) / BlockSize), x1 = (int)MathF.Floor((o.X + extent) / BlockSize);
            int z0 = (int)MathF.Floor((o.Z - extent) / BlockSize), z1 = (int)MathF.Floor((o.Z + extent) / BlockSize);
            for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                var rel = new Vector2((x + 0.5f) * BlockSize, (z + 0.5f) * BlockSize) - middle;
                float a = Vector2.Dot(rel, across) / o.Length, b = Vector2.Dot(rel, u) / o.Depth;
                float d = a * a + b * b;
                float lump = 0.8f + 0.2f * MathF.Sin(rel.X * 1.3f + phase) * MathF.Sin(rel.Y * 1.1f - phase)
                                  + 0.15f * MathF.Sin(MathF.Atan2(b, a) * 3f + phase);
                if (d > lump) continue;
                // Highest against the slope, falling off toward the open ground.
                float lean = 0.45f + 0.55f * Math.Clamp((b + 1f) / 2f, 0f, 1f);
                float height = o.Height * MathF.Sqrt(MathF.Max(0, 1 - d / lump)) * lean * (0.75f + 0.5f * Hash01(x, z, o.Seed));
                Lay(x, z, Math.Max(1, (int)MathF.Round(height / BlockSize)));
            }

            // Loose blocks that rolled down past the heap.
            int loose = 2 + random.Next(5);
            for (int i = 0; i < loose; i++)
            {
                var at = middle - u * (o.Depth + 1f + 5f * random.NextSingle()) + across * ((random.NextSingle() - 0.5f) * o.Length * 1.6f);
                Lay((int)MathF.Floor(at.X / BlockSize), (int)MathF.Floor(at.Y / BlockSize), 1);
            }
        }
        return cells;
    }

    private static float Hash01(int x, int z, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + z * 668265263 + seed * 2246822519u);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }
    }

    private static float CellDistance(Vector3 p, int cx, int cz)
    {
        float dx = (cx + 0.5f) * StoneOutcrops.CellSize - p.X, dz = (cz + 0.5f) * StoneOutcrops.CellSize - p.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    // ---- Solid space queries: standing, walls, ceilings ----

    /// <summary>
    /// The top of the blocks under (x, z) for something at height <paramref name="y"/>: if the point
    /// is inside a block, the top of that stack of solid voxels; otherwise the top of the first solid
    /// voxel below, down to <paramref name="floor"/>. NegativeInfinity if there is none.
    /// </summary>
    public float Height(float x, float z, float y, float floor)
    {
        int fx = (int)MathF.Floor(x / FineSize), fz = (int)MathF.Floor(z / FineSize);
        int cx = FloorDiv(fx, Fine), cz = FloorDiv(fz, Fine);
        if (!_columns.ContainsKey((cx, cz))) return float.NegativeInfinity;
        int sx = fx - cx * Fine, sz = fz - cz * Fine;
        bool SolidAt(int row)
        {
            int cy = FloorDiv(row, Fine);
            return TryGet(new Cell(cx, cy, cz), out var piece) && Solid(piece, sx, row - cy * Fine, sz);
        }

        int fy = (int)MathF.Floor(MathF.Min(y, floor + 64f) / FineSize); // nothing is built that tall
        if (SolidAt(fy))
        {
            int limit = fy + 256;
            while (fy < limit && SolidAt(fy + 1)) fy++;
            return (fy + 1) * FineSize;
        }
        int bottom = (int)MathF.Floor(floor / FineSize);
        for (fy--; fy >= bottom; fy--)
        {
            int cy = FloorDiv(fy, Fine);
            if (!TryGet(new Cell(cx, cy, cz), out var piece))
            {
                fy = cy * Fine; // skip the empty cell (the loop steps below it)
                continue;
            }
            if (Solid(piece, sx, fy - cy * Fine, sz)) return (fy + 1) * FineSize;
        }
        return float.NegativeInfinity;
    }

    /// <summary>Whether any solid voxel lies in the box [min, max].</summary>
    public bool Overlaps(Vector3 min, Vector3 max)
    {
        int x0 = (int)MathF.Floor(min.X / FineSize), x1 = (int)MathF.Floor((max.X - 1e-4f) / FineSize);
        int z0 = (int)MathF.Floor(min.Z / FineSize), z1 = (int)MathF.Floor((max.Z - 1e-4f) / FineSize);
        int y0 = (int)MathF.Floor(min.Y / FineSize), y1 = (int)MathF.Floor((max.Y - 1e-4f) / FineSize);
        for (int x = x0; x <= x1; x++)
        for (int z = z0; z <= z1; z++)
        {
            if (!_columns.ContainsKey((FloorDiv(x, Fine), FloorDiv(z, Fine)))) continue;
            for (int y = y0; y <= y1; y++)
                if (Solid(x, y, z)) return true;
        }
        return false;
    }

    /// <summary>The highest block top under the square footprint around (x, z), for something at height y.</summary>
    public float FootprintHeight(float x, float z, float halfWidth, float y, float floor)
    {
        float best = float.NegativeInfinity;
        int x0 = (int)MathF.Floor((x - halfWidth) / FineSize), x1 = (int)MathF.Floor((x + halfWidth) / FineSize);
        int z0 = (int)MathF.Floor((z - halfWidth) / FineSize), z1 = (int)MathF.Floor((z + halfWidth) / FineSize);
        for (int fx = x0; fx <= x1; fx++)
        for (int fz = z0; fz <= z1; fz++)
            best = MathF.Max(best, Height((fx + 0.5f) * FineSize, (fz + 0.5f) * FineSize, y, floor));
        return best;
    }

    /// <summary>
    /// First solid voxel along a ray within <paramref name="maxDistance"/> (a voxel walk on the fine
    /// grid): its cell, the face normal it was entered through and the distance.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out Cell cell, out Vector3 normal, out float distance)
    {
        var p = origin / FineSize;
        int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y), z = (int)MathF.Floor(p.Z);
        int sx = MathF.Sign(direction.X), sy = MathF.Sign(direction.Y), sz = MathF.Sign(direction.Z);
        float Next(float pos, int i, int s) => s > 0 ? i + 1 - pos : pos - i;
        float dx = sx != 0 ? 1f / MathF.Abs(direction.X) : float.MaxValue;
        float dy = sy != 0 ? 1f / MathF.Abs(direction.Y) : float.MaxValue;
        float dz = sz != 0 ? 1f / MathF.Abs(direction.Z) : float.MaxValue;
        float tx = sx != 0 ? Next(p.X, x, sx) * dx : float.MaxValue;
        float ty = sy != 0 ? Next(p.Y, y, sy) * dy : float.MaxValue;
        float tz = sz != 0 ? Next(p.Z, z, sz) * dz : float.MaxValue;
        float limit = maxDistance / FineSize, t = 0;
        normal = Vector3.Zero;
        while (t <= limit)
        {
            if (Solid(x, y, z))
            {
                cell = new Cell(FloorDiv(x, Fine), FloorDiv(y, Fine), FloorDiv(z, Fine));
                distance = t * FineSize;
                return true;
            }
            if (tx < ty && tx < tz) { x += sx; t = tx; tx += dx; normal = new Vector3(-sx, 0, 0); }
            else if (ty < tz) { y += sy; t = ty; ty += dy; normal = new Vector3(0, -sy, 0); }
            else { z += sz; t = tz; tz += dz; normal = new Vector3(0, 0, -sz); }
        }
        cell = default;
        distance = maxDistance;
        return false;
    }

    public static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}
