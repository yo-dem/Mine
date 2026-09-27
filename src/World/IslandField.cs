using System.Numerics;
using Mine.Rendering;

namespace Mine.World;

/// <summary>A floating island: its grassy top is centred at <see cref="Center"/>.</summary>
public sealed record Island(long Id, Vector3 Center, float Radius, float Depth, float Seed, TreeInstance[] Trees, Waterfall[] Falls);

/// <summary>
/// A waterfall pouring off an island: a stream runs across the top along <see cref="Direction"/>
/// to <see cref="Lip"/> on the edge, then the water arcs out and falls to <see cref="Bottom"/>,
/// into a pond <see cref="PondRadius"/> wide (0 when it falls into the sea or a lake instead).
/// </summary>
public readonly record struct Waterfall(Vector3 Lip, Vector3 Direction, float Width, float Bottom, float PondRadius);

/// <summary>
/// Where an island is and how it is shaped, apart from its height (which depends on the land below).
/// A pure function of the seed and the cell, so the terrain can dig the ponds under its waterfalls.
/// </summary>
public readonly record struct IslandSite(float X, float Z, float Radius, float HeightRoll, float DepthRoll, float Seed,
    float[] FallAngles, float[] FallWidths);

/// <summary>
/// Floating islands hanging in the sky: at most one per 420 m cell, 70–190 m above the land,
/// with a gently domed grassy top, a rocky underside tapering to a point, a few trees on top, and
/// on about half of them one or two waterfalls of glowing water pouring into a pond on the ground.
/// The shape functions (<see cref="EdgeRadius"/>, <see cref="TopHeight"/>) are shared by the mesh
/// and by <see cref="GroundBelow"/>, so the player can land on an island and walk on it.
/// </summary>
public sealed class IslandField
{
    public const float CellSize = 420f;
    public const float Radius = 1700f;
    private const float Chance = 0.4f;

    // Waterfalls: the share of islands with one (a few have two), how wide they are, and how far
    // out from the edge the water lands once it has fallen (the curve is the same for every height).
    private const float FallChance = 0.55f, TwoFallsChance = 0.3f;
    private const float FallMinWidth = 2.5f, FallMaxWidth = 5.5f;
    public const float FallReach = 6f;

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
    public static float EdgeRadius(Island island, float angle) => EdgeRadius(island.Radius, island.Seed, angle);

    public static float EdgeRadius(float radius, float seed, float angle) =>
        radius * (1f + 0.16f * MathF.Sin(3 * angle + seed) + 0.09f * MathF.Sin(5 * angle + 2 * seed)
                     + 0.05f * MathF.Sin(9 * angle + 3 * seed));

    /// <summary>Where the water of a site's waterfall lands (horizontally): <see cref="FallReach"/> past the edge.</summary>
    public static Vector2 FallFoot(in IslandSite site, int fall)
    {
        float a = site.FallAngles[fall];
        float r = EdgeRadius(site.Radius, site.Seed, a) + FallReach;
        return new Vector2(site.X + MathF.Cos(a) * r, site.Z + MathF.Sin(a) * r);
    }

    /// <summary>The island of a cell, if it has one (see <see cref="IslandSite"/>).</summary>
    public static IslandSite? Site(int seed, int cx, int cz)
    {
        var random = new Random((int)Hash(seed, cx, cz));
        if (random.NextSingle() > Chance) return null;
        float x = (cx + 0.2f + 0.6f * random.NextSingle()) * CellSize;
        float z = (cz + 0.2f + 0.6f * random.NextSingle()) * CellSize;
        float radius = 12f + 22f * random.NextSingle();
        float heightRoll = random.NextSingle(), depthRoll = random.NextSingle();
        float islandSeed = random.NextSingle() * 100f;

        int falls = random.NextSingle() > FallChance ? 0 : random.NextSingle() < TwoFallsChance ? 2 : 1;
        var angles = new float[falls];
        var widths = new float[falls];
        float first = random.NextSingle() * MathF.Tau;
        for (int i = 0; i < falls; i++)
        {
            // A second one well round the island from the first.
            angles[i] = first + i * (MathF.PI * (0.6f + 0.8f * random.NextSingle()));
            widths[i] = FallMinWidth + (FallMaxWidth - FallMinWidth) * random.NextSingle();
        }
        return new IslandSite(x, z, radius, heightRoll, depthRoll, islandSeed, angles, widths);
    }

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
        if (Site(_seed, cx, cz) is not { } site) return null;
        var random = new Random((int)Hash(_seed, cx, cz) ^ 0x5bd1e995);
        float x = site.X, z = site.Z, radius = site.Radius;

        // Float well clear of whatever lies below, spires included.
        float ground = _terrain.Height(x, z);
        for (int i = 0; i < 8; i++)
        {
            float a = i * MathF.Tau / 8;
            ground = MathF.Max(ground, _terrain.Height(x + MathF.Cos(a) * radius * 1.5f, z + MathF.Sin(a) * radius * 1.5f));
        }
        var center = new Vector3(x, ground + 70f + 120f * site.HeightRoll, z);
        float depth = radius * (1.3f + 0.9f * site.DepthRoll);
        float seed = site.Seed;
        long id = ((long)cx << 32) ^ (uint)cz;

        var island = new Island(id, center, radius, depth, seed, [], []);
        var falls = new Waterfall[site.FallAngles.Length];
        for (int i = 0; i < falls.Length; i++)
        {
            float a = site.FallAngles[i];
            var dir = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
            var lip = center + dir * EdgeRadius(island, a);
            lip.Y = TopHeight(island, 1f);
            var foot = FallFoot(site, i);
            bool pond = _terrain.PondAt(foot.X, foot.Y, out var p) && MathF.Abs(p.X - foot.X) + MathF.Abs(p.Z - foot.Y) < 0.1f;
            float bottom = pond ? p.Surface : MathF.Max(_terrain.Height(foot.X, foot.Y), TerrainField.WaterLevel);
            falls[i] = new Waterfall(lip, dir, site.FallWidths[i], bottom, pond ? p.Radius : 0f);
        }
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
        return island with { Trees = trees.ToArray(), Falls = falls };
    }

    private static float CellDistance(Vector3 p, int cx, int cz)
    {
        float dx = (cx + 0.5f) * CellSize - p.X, dz = (cz + 0.5f) * CellSize - p.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static uint Hash(int seed, int x, int z)
    {
        unchecked
        {
            uint h = (uint)(x * 668265263 + z * 374761393 + seed * 83492791 + 31337);
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }
}
