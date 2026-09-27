using System.Collections.Concurrent;
using System.Numerics;

namespace Mine.World;

/// <summary>
/// The shape of the land, in metres. Pure and thread-safe, so terrain meshes can be built on
/// background threads. The land is made of layers: square tiles <see cref="TileSize"/> wide, each
/// flat at a height that is a multiple of <see cref="LayerHeight"/>, with vertical walls between.
/// <see cref="SmoothHeight"/> is the continuous surface they are cut from: slowly rolling ground,
/// domain-warped hills, long dune ridges, deep basins under the water, and rare rock spires.
/// Slopes are kept gentle enough that neighbouring tiles almost always differ by one layer at most
/// (walkable); larger steps are rare, apart from the spires.
/// </summary>
public sealed class TerrainField
{
    /// <summary>Height of the sea and lakes: everything below is under water. Between two layers,
    /// so no tile top lies exactly at the surface.</summary>
    public const float WaterLevel = 14.8f;

    public const float TileSize = 2f;
    public const float LayerHeight = 0.5f;

    // Below the water the land drops this many times faster than above, so beaches stay gentle
    // but a few metres out the water is deep enough to swim.
    private const float DepthScale = 3f;

    // Lakes (see Lakes): at most one per cell of LakeCell, on low and middling land. Each has a
    // wobbly shore LakeRadius across, banks sloping gently down to it over LakeBank metres, and a
    // floor that sinks toward its middle (then DepthScale deepens it further, like all water).
    private const float LakeCell = 400f;
    private const float LakeChance = 0.6f;
    private const float LakeMinRadius = 40f, LakeMaxRadius = 120f;
    private const float LakeBank = 130f;
    private const float LakeMinDepth = 2f, LakeMaxDepth = 11f;

    // Ponds under the islands' waterfalls (see Ponds): a basin PondRadius-ish wide, ringed by a
    // bank at least half a layer above the water, then a slope back down to the land.
    private const float PondBank = 2f, PondSlope = 4f;

    private const float SpireCell = 180f;      // at most one spire per cell of this size
    private const float SpireChance = 0.22f;

    private readonly PerlinNoise _continent;
    private readonly PerlinNoise _hills;
    private readonly PerlinNoise _warp;
    private readonly PerlinNoise _dunes;
    private readonly PerlinNoise _detail;
    private readonly PerlinNoise _lakes;
    private readonly int _seed;
    private readonly ConcurrentDictionary<(int X, int Z), Pond[]> _ponds = new();

    /// <summary>A pond at the foot of a waterfall: centre, radius and water surface height.</summary>
    public readonly record struct Pond(float X, float Z, float Radius, float Surface);

    /// <summary>The seed must be the one the islands use, since the ponds lie under their waterfalls.</summary>
    public TerrainField(int seed)
    {
        _seed = seed;
        _continent = new PerlinNoise(seed);
        _hills = new PerlinNoise(seed + 1);
        _warp = new PerlinNoise(seed + 2);
        _dunes = new PerlinNoise(seed + 3);
        _detail = new PerlinNoise(seed + 4);
        _lakes = new PerlinNoise(seed + 5);
    }

    /// <summary>Height of the tile under (x, z): the smooth height at its centre, snapped to a layer.</summary>
    public float Height(float x, float z) =>
        TileHeight((int)MathF.Floor(x / TileSize), (int)MathF.Floor(z / TileSize));

    /// <summary>Height of tile (i, j), whose centre is at ((i + 0.5) * TileSize, (j + 0.5) * TileSize).</summary>
    public float TileHeight(int i, int j) => Layer(SmoothHeight((i + 0.5f) * TileSize, (j + 0.5f) * TileSize));

    /// <summary>A coordinate moved inside its tile, at least <paramref name="margin"/> from the tile's edges.</summary>
    public static float InsideTile(float x, float margin)
    {
        float start = MathF.Floor(x / TileSize) * TileSize;
        return start + Math.Clamp(x - start, margin, TileSize - margin);
    }

    /// <summary>The centre of the tile containing (x, z), at the tile's height.</summary>
    public Vector3 TileCenter(float x, float z)
    {
        float cx = (MathF.Floor(x / TileSize) + 0.5f) * TileSize, cz = (MathF.Floor(z / TileSize) + 0.5f) * TileSize;
        return new Vector3(cx, Height(cx, cz), cz);
    }

    /// <summary>A height snapped to the nearest layer.</summary>
    public static float Layer(float height) => MathF.Round(height / LayerHeight) * LayerHeight;

    public float SmoothHeight(float x, float z) => Ponds(x, z, BaseHeight(x, z));

    /// <summary>The pond whose basin, bank or slope contains (x, z), if any.</summary>
    public bool PondAt(float x, float z, out Pond pond)
    {
        foreach (var p in PondsIn((int)MathF.Floor(x / IslandField.CellSize), (int)MathF.Floor(z / IslandField.CellSize)))
        {
            float dx = x - p.X, dz = z - p.Z;
            if (dx * dx + dz * dz < (p.Radius + PondBank + PondSlope) * (p.Radius + PondBank + PondSlope)) { pond = p; return true; }
        }
        pond = default;
        return false;
    }

    /// <summary>Whether (x, z) is in a pond's water, or within <paramref name="margin"/> metres of it.</summary>
    public bool InPond(float x, float z, float margin = 0f)
    {
        if (!PondAt(x, z, out var p)) return false;
        float dx = x - p.X, dz = z - p.Z;
        return dx * dx + dz * dz < (p.Radius + margin) * (p.Radius + margin);
    }

    /// <summary>
    /// The ponds of an island cell, where its waterfalls land on dry land (not into the sea or a
    /// lake). The water sits a little under the land's layer there, so the basin is dug in.
    /// </summary>
    private Pond[] PondsIn(int cx, int cz) => _ponds.GetOrAdd((cx, cz), key =>
    {
        if (IslandField.Site(_seed, key.X, key.Z) is not { } site) return [];
        var ponds = new List<Pond>();
        for (int i = 0; i < site.FallAngles.Length; i++)
        {
            var foot = IslandField.FallFoot(site, i);
            float ground = BaseHeight(foot.X, foot.Y);
            if (ground < WaterLevel + 1.5f) continue;
            ponds.Add(new Pond(foot.X, foot.Y, 3f + 2f * site.FallWidths[i], Layer(ground) - 0.2f));
        }
        return ponds.ToArray();
    });

    /// <summary>Digs the ponds: a rounded basin under the water, a bank round it, a slope back to the land.</summary>
    private float Ponds(float x, float z, float h)
    {
        if (!PondAt(x, z, out var p)) return h;
        float d = MathF.Sqrt((x - p.X) * (x - p.X) + (z - p.Z) * (z - p.Z));
        if (d < p.Radius)
        {
            float f = d / p.Radius;
            return MathF.Min(h, p.Surface - 0.5f - 1.2f * (1f - f * f));
        }
        float bank = p.Surface + 0.45f;
        if (d < p.Radius + PondBank) return MathF.Max(h, bank);
        float t = Smooth(p.Radius + PondBank, p.Radius + PondBank + PondSlope, d);
        return MathF.Max(h, bank + (h - bank) * t);
    }

    private float BaseHeight(float x, float z)
    {
        // Large, slow swells of the land.
        float continent = _continent.Fractal(x * 0.0006f, z * 0.0006f, 3);

        // Hills, with their coordinates bent by another noise so they flow instead of
        // looking like a regular grid of bumps.
        float wx = _warp.Fractal(x * 0.0025f, z * 0.0025f, 2) * 90f;
        float wz = _warp.Fractal(x * 0.0025f + 31.7f, z * 0.0025f - 17.3f, 2) * 90f;
        float hills = _hills.Fractal((x + wx) * 0.004f, (z + wz) * 0.004f, 5);

        // Long rounded dune ridges, strongest where the land is low, and tallest in the deserts
        // (whose hills are also gentler).
        float desert = GroundMaterials.Desert(x, z);
        float duneNoise = _dunes.Fractal(x * 0.006f + hills * 0.3f, z * 0.0025f, 3);
        float dune = 1f - MathF.Sqrt(duneNoise * duneNoise + 0.02f); // rounded crest, no sharp ridge
        float lowland = MathF.Max(Smooth(0.2f, -0.3f, continent), desert);

        float h = 20f + continent * 35f + hills * 30f * (1f - 0.4f * desert) + dune * dune * (9f + 6f * desert) * lowland;

        h = Lakes(x, z, h);

        h += _detail.Fractal(x * 0.05f, z * 0.05f, 2) * 0.4f;
        if (h < WaterLevel) h = WaterLevel - (WaterLevel - h) * DepthScale;
        return h + Spires(x, z);
    }

    /// <summary>
    /// Normal of the smooth surface, from central differences <paramref name="epsilon"/> metres apart:
    /// how steep the land is around a point (tile tops themselves are flat).
    /// </summary>
    public Vector3 Normal(float x, float z, float epsilon = 1f)
    {
        float dx = SmoothHeight(x + epsilon, z) - SmoothHeight(x - epsilon, z);
        float dz = SmoothHeight(x, z + epsilon) - SmoothHeight(x, z - epsilon);
        return Vector3.Normalize(new Vector3(-dx, 2 * epsilon, -dz));
    }

    /// <summary>
    /// First point where a ray meets the ground, within <paramref name="maxDistance"/>: small steps
    /// until the ray dips below the surface, then a bisection to pin the crossing down.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 hit)
    {
        const float step = 0.1f;
        float previous = 0;
        for (float t = step; t <= maxDistance; t += step)
        {
            var p = origin + direction * t;
            if (p.Y > Height(p.X, p.Z)) { previous = t; continue; }

            float lo = previous, hi = t;
            for (int i = 0; i < 12; i++)
            {
                float mid = (lo + hi) * 0.5f;
                var m = origin + direction * mid;
                if (m.Y > Height(m.X, m.Z)) lo = mid; else hi = mid;
            }
            hit = origin + direction * hi;
            return true;
        }
        hit = default;
        return false;
    }

    /// <summary>
    /// Scoops lakes out of the land at height <paramref name="h"/>: the ground sinks gently to just
    /// under the water at the shore, then drops to a floor deeper toward the middle.
    /// </summary>
    private float Lakes(float x, float z, float h)
    {
        int cx = (int)MathF.Floor(x / LakeCell), cz = (int)MathF.Floor(z / LakeCell);
        float bank = 0, floor = 0;
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            uint hash = Hash(cx + dx + 9001, cz + dz - 4507);
            if ((hash & 0xFFFF) / 65536f > LakeChance) continue;
            float px = (cx + dx + 0.2f + 0.6f * ((hash >> 8) & 0xFF) / 255f) * LakeCell;
            float pz = (cz + dz + 0.2f + 0.6f * ((hash >> 16) & 0xFF) / 255f) * LakeCell;
            float radius = LakeMinRadius + (LakeMaxRadius - LakeMinRadius) * ((hash >> 24) & 0xF) / 15f;
            float ox = x - px, oz = z - pz;
            float r = MathF.Sqrt(ox * ox + oz * oz);
            if (r >= (radius + LakeBank) * 1.4f) continue;
            // Not up in the high lands, where the banks would have to be cliffs.
            if (_continent.Fractal(px * 0.0006f, pz * 0.0006f, 3) > 0.2f) continue;

            // A wobbly shore: the distance is measured against a radius that changes around the
            // lake (noise sampled on a circle, so it wraps seamlessly).
            float cos = r > 1e-3f ? ox / r : 1f, sin = r > 1e-3f ? oz / r : 0f;
            float seed = (hash & 0xFF) * 0.53f;
            float wobble = 1f + 0.3f * _lakes.Noise(cos * 1.3f + seed, sin * 1.3f - seed)
                              + 0.12f * _lakes.Noise(cos * 3.1f - seed, sin * 3.1f + seed);
            float d = r / wobble;
            float depth = LakeMinDepth + (LakeMaxDepth - LakeMinDepth) * ((hash >> 28) & 0xF) / 15f;
            bank = MathF.Max(bank, Smooth(radius + LakeBank, radius, d));
            floor = MathF.Max(floor, Smooth(radius, radius * 0.25f, d) * depth);
        }
        if (bank <= 0) return h;
        // Only ever lowers the land (the sea floor stays where it is).
        float shore = WaterLevel - 1f;
        if (h > shore) h += (shore - h) * bank;
        return h - floor;
    }

    /// <summary>Rare tall rock needles with a steep flank and a rounded top.</summary>
    private float Spires(float x, float z)
    {
        int cx = (int)MathF.Floor(x / SpireCell), cz = (int)MathF.Floor(z / SpireCell);
        float total = 0;
        // A spire can lean over the border of its cell, so check the neighbours too.
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            uint h = Hash(cx + dx, cz + dz);
            if ((h & 0xFFFF) / 65536f > SpireChance) continue;

            float px = (cx + dx + 0.2f + 0.6f * ((h >> 8) & 0xFF) / 255f) * SpireCell;
            float pz = (cz + dz + 0.2f + 0.6f * ((h >> 16) & 0xFF) / 255f) * SpireCell;
            float radius = 7f + 9f * ((h >> 24) & 0xF) / 15f;
            float height = 35f + 55f * ((h >> 28) & 0xF) / 15f;

            // A ragged outline: the radius wobbles around the spire (noise sampled on a circle,
            // so it wraps seamlessly), and narrows toward the top.
            float ox = x - px, oz = z - pz;
            float r = MathF.Sqrt(ox * ox + oz * oz);
            if (r >= radius * 1.4f) continue;
            float cos = r > 1e-3f ? ox / r : 1f, sin = r > 1e-3f ? oz / r : 0f;
            float seed = (h & 0xFF) * 0.37f;
            float wobble = 1f + 0.35f * _detail.Noise(cos * 1.4f + seed, sin * 1.4f - seed);
            float d = r / (radius * wobble);
            if (d >= 1f) continue;
            float profile = MathF.Pow(1f - d, 1.6f);
            total = MathF.Max(total, height * profile * (1f - 0.35f * profile * profile));
        }
        return total;
    }

    private static float Smooth(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    private uint Hash(int x, int z)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + z * 668265263 + _seed * 144269504);
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }
}
