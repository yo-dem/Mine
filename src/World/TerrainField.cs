using System.Numerics;

namespace Mine.World;

/// <summary>
/// The shape of the land: a deterministic height for every point (x, z), in metres.
/// Pure and thread-safe, so terrain meshes can be built on background threads.
/// The recipe, from large to small: slowly rolling ground, domain-warped hills, long dune
/// ridges, softly terraced plateaus, and rare rock spires rising out of the land.
/// </summary>
public sealed class TerrainField
{
    /// <summary>Height of the sea and lakes: everything below is under water.</summary>
    public const float WaterLevel = 15f;

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

    public float Height(float x, float z)
    {
        // Large, slow swells of the land.
        float continent = _continent.Fractal(x * 0.0006f, z * 0.0006f, 3);

        // Hills, with their coordinates bent by another noise so they flow instead of
        // looking like a regular grid of bumps.
        float wx = _warp.Fractal(x * 0.0025f, z * 0.0025f, 2) * 90f;
        float wz = _warp.Fractal(x * 0.0025f + 31.7f, z * 0.0025f - 17.3f, 2) * 90f;
        float hills = _hills.Fractal((x + wx) * 0.004f, (z + wz) * 0.004f, 5);

        // Long rounded dune ridges, strongest where the land is low.
        float dune = 1f - MathF.Abs(_dunes.Fractal(x * 0.006f + hills * 0.3f, z * 0.0025f, 3));
        float lowland = Smooth(0.2f, -0.3f, continent);

        float h = 20f + continent * 35f + hills * 40f + dune * dune * 9f * lowland;

        // Soft terraces on the higher ground: flat steps joined by gentle slopes.
        float highland = Smooth(0.0f, 0.4f, continent);
        const float step = 9f;
        float t = h / step;
        float terraced = (MathF.Floor(t) + Smooth(0.3f, 0.7f, t - MathF.Floor(t))) * step;
        h += (terraced - h) * 0.6f * highland;

        h += _detail.Fractal(x * 0.05f, z * 0.05f, 2) * 1.0f;
        return h + Spires(x, z);
    }

    /// <summary>Surface normal from central differences, <paramref name="epsilon"/> metres apart.</summary>
    public Vector3 Normal(float x, float z, float epsilon = 0.5f)
    {
        float dx = Height(x + epsilon, z) - Height(x - epsilon, z);
        float dz = Height(x, z + epsilon) - Height(x, z - epsilon);
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
