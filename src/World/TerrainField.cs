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

    private const float SpireCell = 180f;      // at most one spire per cell of this size
    private const float SpireChance = 0.22f;

    private readonly PerlinNoise _continent;
    private readonly PerlinNoise _hills;
    private readonly PerlinNoise _warp;
    private readonly PerlinNoise _dunes;
    private readonly PerlinNoise _detail;
    private readonly int _seed;

    public TerrainField(int seed)
    {
        _seed = seed;
        _continent = new PerlinNoise(seed);
        _hills = new PerlinNoise(seed + 1);
        _warp = new PerlinNoise(seed + 2);
        _dunes = new PerlinNoise(seed + 3);
        _detail = new PerlinNoise(seed + 4);
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

    public float SmoothHeight(float x, float z)
    {
        // Large, slow swells of the land.
        float continent = _continent.Fractal(x * 0.0006f, z * 0.0006f, 3);

        // Hills, with their coordinates bent by another noise so they flow instead of
        // looking like a regular grid of bumps.
        float wx = _warp.Fractal(x * 0.0025f, z * 0.0025f, 2) * 90f;
        float wz = _warp.Fractal(x * 0.0025f + 31.7f, z * 0.0025f - 17.3f, 2) * 90f;
        float hills = _hills.Fractal((x + wx) * 0.004f, (z + wz) * 0.004f, 5);

        // Long rounded dune ridges, strongest where the land is low.
        float duneNoise = _dunes.Fractal(x * 0.006f + hills * 0.3f, z * 0.0025f, 3);
        float dune = 1f - MathF.Sqrt(duneNoise * duneNoise + 0.02f); // rounded crest, no sharp ridge
        float lowland = Smooth(0.2f, -0.3f, continent);

        float h = 20f + continent * 35f + hills * 30f + dune * dune * 9f * lowland;

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
