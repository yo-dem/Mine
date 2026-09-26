using System.Numerics;
using Mine.Rendering;

namespace Mine.World;

/// <summary>A floating island: its grassy top is centred at <see cref="Center"/>.</summary>
public sealed record Island(long Id, Vector3 Center, float Radius, float Depth, float Seed, TreeInstance[] Trees);

/// <summary>
/// Floating islands hanging in the sky: at most one per 420 m cell, 70–190 m above the land,
/// with a gently domed grassy top, a rocky underside tapering to a point, and a few trees on top.
/// The shape functions (<see cref="EdgeRadius"/>, <see cref="TopHeight"/>) are shared by the mesh
/// and by <see cref="GroundBelow"/>, so the player can land on an island and walk on it.
/// </summary>
public sealed class IslandField
{
    public const float CellSize = 420f;
    public const float Radius = 1700f;
    private const float Chance = 0.4f;

    // Tree models that suit the sky: dreamy teal, lilac blossom, glowing orbs, fireflies.
    private static readonly int[] SkyTrees = [3, 4, 6, 5, 4];

    private readonly TerrainField _terrain;
    private readonly int _seed;
    private readonly Dictionary<(int X, int Z), Island?> _cells = new();
    private readonly List<(int X, int Z)> _toDrop = new();

    public int Version { get; private set; }

    public IslandField(TerrainField terrain, int seed)
    {
        _terrain = terrain;
        _seed = seed;
    }

    public IEnumerable<Island> All => _cells.Values.OfType<Island>();

    public IEnumerable<TreeInstance> Trees => All.SelectMany(i => i.Trees);

    public void Update(Vector3 player)
    {
        int radius = (int)MathF.Ceiling(Radius / CellSize);
        int pcx = (int)MathF.Floor(player.X / CellSize), pcz = (int)MathF.Floor(player.Z / CellSize);
        for (int cz = pcz - radius; cz <= pcz + radius; cz++)
        for (int cx = pcx - radius; cx <= pcx + radius; cx++)
        {
            if (_cells.ContainsKey((cx, cz)) || CellDistance(player, cx, cz) > Radius) continue;
            var island = Generate(cx, cz);
            _cells[(cx, cz)] = island;
            if (island is not null) Version++;
        }

        _toDrop.Clear();
        foreach (var key in _cells.Keys)
            if (CellDistance(player, key.X, key.Z) > Radius + CellSize) _toDrop.Add(key);
        foreach (var key in _toDrop)
            if (_cells.Remove(key, out var island) && island is not null) Version++;
    }

    /// <summary>Distance from the centre to the edge of the top, in a given direction (a wavy outline).</summary>
    public static float EdgeRadius(Island island, float angle) =>
        island.Radius * (1f + 0.16f * MathF.Sin(3 * angle + island.Seed) + 0.09f * MathF.Sin(5 * angle + 2 * island.Seed)
                            + 0.05f * MathF.Sin(9 * angle + 3 * island.Seed));

    /// <summary>Height of the top at a fraction of the way from the centre (0) to the edge (1): a gentle dome.</summary>
    public static float TopHeight(Island island, float fromCenter) =>
        island.Center.Y + 1.8f * (1f - fromCenter * fromCenter);

    /// <summary>
    /// The highest island top under (x, z) that the point at height <paramref name="y"/> stands on
    /// or just sank into this frame. False when there is none.
    /// </summary>
    public bool GroundBelow(float x, float z, float y, out float height)
    {
        height = float.NegativeInfinity;
        foreach (var island in All)
        {
            float dx = x - island.Center.X, dz = z - island.Center.Z;
            float r = MathF.Sqrt(dx * dx + dz * dz);
            if (r > island.Radius * 1.35f) continue;
            float edge = EdgeRadius(island, MathF.Atan2(dz, dx));
            if (r >= edge) continue;
            float top = TopHeight(island, r / edge);
            if (y >= top - 1.5f && top > height) height = top;
        }
        return height > float.NegativeInfinity;
    }

    private Island? Generate(int cx, int cz)
    {
        var random = new Random((int)Hash(cx, cz));
        if (random.NextSingle() > Chance) return null;

        float x = (cx + 0.2f + 0.6f * random.NextSingle()) * CellSize;
        float z = (cz + 0.2f + 0.6f * random.NextSingle()) * CellSize;
        float radius = 12f + 22f * random.NextSingle();

        // Float well clear of whatever lies below, spires included.
        float ground = _terrain.Height(x, z);
        for (int i = 0; i < 8; i++)
        {
            float a = i * MathF.Tau / 8;
            ground = MathF.Max(ground, _terrain.Height(x + MathF.Cos(a) * radius * 1.5f, z + MathF.Sin(a) * radius * 1.5f));
        }
        var center = new Vector3(x, ground + 70f + 120f * random.NextSingle(), z);
        float depth = radius * (1.3f + 0.9f * random.NextSingle());
        float seed = random.NextSingle() * 100f;
        long id = ((long)cx << 32) ^ (uint)cz;

        var island = new Island(id, center, radius, depth, seed, []);
        int treeCount = 1 + (int)(radius / 12f) + random.Next(2);
        var trees = new List<TreeInstance>();
        for (int i = 0; i < treeCount; i++)
        {
            float a = random.NextSingle() * MathF.Tau;
            float r = MathF.Sqrt(random.NextSingle()) * 0.55f;
            float edge = EdgeRadius(island, a);
            var p = new Vector3(center.X + MathF.Cos(a) * r * edge, 0, center.Z + MathF.Sin(a) * r * edge);
            p.Y = TopHeight(island, r) - 0.2f;
            trees.Add(new TreeInstance(p, random.NextSingle() * MathF.Tau, 0.7f + 0.4f * random.NextSingle(), SkyTrees[random.Next(SkyTrees.Length)]));
        }
        return island with { Trees = trees.ToArray() };
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
            uint h = (uint)(x * 668265263 + z * 374761393 + _seed * 83492791 + 31337);
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }
}
