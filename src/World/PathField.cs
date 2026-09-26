using System.Numerics;
using Mine.Rendering;

namespace Mine.World;

/// <summary>A stepping stone the player can walk on: its centre, the height of its top and its radius.</summary>
public readonly record struct Stone(Vector3 Position, float Top, float Radius);

/// <summary>
/// Paths of crystal stepping stones across the water, under arches of glowing wisteria. At most
/// one per 280 m cell: it starts on a shore, heads downhill into the water and curves gently
/// across it until it reaches land again. Stones are walkable (<see cref="GroundBelow"/>).
/// </summary>
public sealed class PathField
{
    public const float CellSize = 280f;
    public const float Radius = 1000f;
    private const float StoneSpacing = 2.3f;
    private const int StonesPerArch = 7;
    private const float StoneRadius = 1.2f; // walkable radius, a little wider than it looks, so gaps are not traps

    private readonly TerrainField _terrain;
    private readonly int _seed;
    private readonly Dictionary<(int X, int Z), (TreeInstance[] Models, Stone[] Stones)> _cells = new();
    private readonly Dictionary<(int X, int Z), List<Stone>> _stoneGrid = new(); // 16 m buckets, for GroundBelow
    private readonly List<(int X, int Z)> _toDrop = new();

    public int Version { get; private set; }

    public PathField(TerrainField terrain, int seed)
    {
        _terrain = terrain;
        _seed = seed;
    }

    /// <summary>The stones and arches, as instances for the tree renderer.</summary>
    public IEnumerable<TreeInstance> Models => _cells.Values.SelectMany(c => c.Models);

    public void Update(Vector3 player)
    {
        int radius = (int)MathF.Ceiling(Radius / CellSize);
        int pcx = (int)MathF.Floor(player.X / CellSize), pcz = (int)MathF.Floor(player.Z / CellSize);
        bool changed = false;
        for (int cz = pcz - radius; cz <= pcz + radius; cz++)
        for (int cx = pcx - radius; cx <= pcx + radius; cx++)
        {
            if (_cells.ContainsKey((cx, cz)) || CellDistance(player, cx, cz) > Radius) continue;
            _cells[(cx, cz)] = Generate(cx, cz);
            changed |= _cells[(cx, cz)].Models.Length > 0;
        }

        _toDrop.Clear();
        foreach (var key in _cells.Keys)
            if (CellDistance(player, key.X, key.Z) > Radius + CellSize) _toDrop.Add(key);
        foreach (var key in _toDrop)
            if (_cells.Remove(key, out var cell) && cell.Models.Length > 0) changed = true;

        if (!changed) return;
        Version++;
        _stoneGrid.Clear();
        foreach (var cell in _cells.Values)
            foreach (var stone in cell.Stones)
            {
                var key = ((int)MathF.Floor(stone.Position.X / 16f), (int)MathF.Floor(stone.Position.Z / 16f));
                if (!_stoneGrid.TryGetValue(key, out var list)) _stoneGrid[key] = list = new List<Stone>();
                list.Add(stone);
            }
    }

    /// <summary>
    /// The top of a stone under (x, z) that something at height <paramref name="y"/> stands on, or can
    /// step up onto from the water.
    /// </summary>
    public bool GroundBelow(float x, float z, float y, out float height)
    {
        height = float.NegativeInfinity;
        if (!_stoneGrid.TryGetValue(((int)MathF.Floor(x / 16f), (int)MathF.Floor(z / 16f)), out var stones)) return false;
        foreach (var stone in stones)
        {
            float dx = x - stone.Position.X, dz = z - stone.Position.Z;
            if (dx * dx + dz * dz > stone.Radius * stone.Radius) continue;
            if (y >= stone.Top - 1.4f && stone.Top > height) height = stone.Top;
        }
        return height > float.NegativeInfinity;
    }

    private (TreeInstance[], Stone[]) Generate(int cx, int cz)
    {
        var random = new Random((int)Hash(cx, cz));
        const float water = TerrainField.WaterLevel;
        if (random.NextSingle() > 0.55f) return ([], []);

        // A start on the shore, a little above the water.
        Vector2 start = default;
        bool found = false;
        for (int i = 0; i < 40 && !found; i++)
        {
            start = new Vector2((cx + random.NextSingle()) * CellSize, (cz + random.NextSingle()) * CellSize);
            float h = _terrain.Height(start.X, start.Y);
            found = h > water + 0.3f && h < water + 2.5f;
        }
        if (!found) return ([], []);

        // Head downhill, into the water.
        var normal = _terrain.Normal(start.X, start.Y, 2f);
        var dir = Vector2.Normalize(new Vector2(normal.X, normal.Z) + new Vector2(1e-4f, 0));
        float bend = (random.NextSingle() - 0.5f) * 0.12f;

        var models = new List<TreeInstance>();
        var stones = new List<Stone>();
        var point = start;
        int onWater = 0;
        bool leftShore = false;
        for (int i = 0; i < 60; i++)
        {
            float ground = _terrain.Height(point.X, point.Y);
            bool wet = ground < water - 0.1f;
            if (wet) { onWater++; leftShore = true; }
            else if (leftShore && ground > water + 0.4f) break; // reached the other shore

            var side = new Vector2(-dir.Y, dir.X) * (random.NextSingle() - 0.5f) * 0.5f;
            var p = point + side;
            float baseY = MathF.Max(_terrain.Height(p.X, p.Y), water) + (wet ? 0.1f : 0.05f);
            float scale = 0.85f + 0.3f * random.NextSingle();
            models.Add(new TreeInstance(new Vector3(p.X, baseY, p.Y), random.NextSingle() * MathF.Tau, scale,
                TreeModels.VariantOf(TreeModels.Decoration.CrystalStone)));
            stones.Add(new Stone(new Vector3(p.X, baseY, p.Y), baseY + 0.26f * scale, StoneRadius * scale));

            if (i % StonesPerArch == 3)
                models.Add(new TreeInstance(new Vector3(point.X, baseY - 0.1f, point.Y), MathF.Atan2(dir.Y, dir.X), 1f,
                    TreeModels.VariantOf(TreeModels.Decoration.WisteriaArch)));

            // Curve gently, like a path laid by hand.
            float turn = bend + MathF.Sin(i * 0.35f + cx) * 0.06f;
            dir = Vector2.Normalize(new Vector2(dir.X * MathF.Cos(turn) - dir.Y * MathF.Sin(turn), dir.X * MathF.Sin(turn) + dir.Y * MathF.Cos(turn)));
            point += dir * StoneSpacing;
        }

        // Only keep real crossings.
        return onWater >= 5 ? (models.ToArray(), stones.ToArray()) : ([], []);
    }

    private static float CellDistance(Vector3 p, int cx, int cz)
    {
        float dx = (cx + 0.5f) * CellSize - p.X, dz = (cz + 0.5f) * CellSize - p.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private uint Hash(int x, int z)
    {
        unchecked
        {
            uint h = (uint)(x * 1597334677 + z * 3812015801 + _seed * 2654435761 + 12345);
            h = (h ^ (h >> 15)) * 2246822519u;
            return h ^ (h >> 13);
        }
    }
}
