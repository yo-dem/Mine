using System.Collections.Concurrent;
using System.Numerics;

namespace Mine.World;

/// <summary>
/// A mountain of 1 m blocks standing on the land, terraced like the terrain (grass on its ledges,
/// rock on its sides), hollowed by a great natural cavern reached through a winding tunnel from
/// <see cref="Mouth"/>'s side. Centred on (X, Z), the cavern's floor at <see cref="Floor"/>.
/// </summary>
public readonly record struct Mountain(float X, float Z, float Radius, float Height, float Floor,
                                       float CavernRadius, float CavernHeight, Vector2 Mouth, int Seed)
{
    public Vector3 CavernCenter => new(X, Floor, Z);
    /// <summary>Where the tunnel comes out, on the ground before the mountain.</summary>
    public Vector2 Entrance => new Vector2(X, Z) + Mouth * (Radius + 2f);
}

/// <summary>
/// Where the mountains stand (rarely: at most one per <see cref="CellSize"/> cell, on broad even
/// dry land) and their shape, as pure functions of the seed and the terrain: the blocks' columns
/// (<see cref="Column"/>), the cavern and tunnel inside (<see cref="Hollow"/>) and the top for the
/// grass (<see cref="Top"/>), safe from any thread.
/// </summary>
public sealed class Mountains(TerrainField terrain, int seed)
{
    public const float CellSize = 512f;
    private const float Chance = 0.6f;

    private readonly ConcurrentDictionary<(int, int), Mountain?> _cache = new();

    public Mountain? Of(int mx, int mz) => _cache.GetOrAdd((mx, mz), key => Generate(key.Item1, key.Item2));

    /// <summary>The mountains reaching within <paramref name="radius"/> of a point.</summary>
    public IEnumerable<Mountain> Near(float x, float z, float radius)
    {
        if (WorldPreset.Current.StoneFields <= 0) yield break;
        const float largest = 45f;
        int x0 = (int)MathF.Floor((x - radius - largest) / CellSize), x1 = (int)MathF.Floor((x + radius + largest) / CellSize);
        int z0 = (int)MathF.Floor((z - radius - largest) / CellSize), z1 = (int)MathF.Floor((z + radius + largest) / CellSize);
        for (int mz = z0; mz <= z1; mz++)
        for (int mx = x0; mx <= x1; mx++)
            if (Of(mx, mz) is { } m)
            {
                float reach = radius + m.Radius;
                if ((m.X - x) * (m.X - x) + (m.Z - z) * (m.Z - z) < reach * reach) yield return m;
            }
    }

    /// <summary>Whether a mountain stands over (x, z), with <paramref name="margin"/> metres more round it.</summary>
    public bool Covers(float x, float z, float margin) => Near(x, z, margin + 2f).Any();

    /// <summary>
    /// The top of the mountain's blocks over (x, z) (where the grass grows), or null where no
    /// mountain stands.
    /// </summary>
    public float? Top(float x, float z)
    {
        int bx = (int)MathF.Floor(x), bz = (int)MathF.Floor(z);
        foreach (var m in Near(x, z, 0f))
        {
            int blocks = Column(m, bx, bz, out int ground);
            if (blocks > 0) return ground * BlockWorld.CellSize + blocks * BlockWorld.BlockSize;
        }
        return null;
    }

    /// <summary>
    /// How many 1 m blocks the mountain stacks on the 1 m column (bx, bz), and the cell its first
    /// block sits on (the ground's). A lobed dome with a few lesser peaks, cut into terraces, in
    /// whole blocks.
    /// </summary>
    public int Column(Mountain m, int bx, int bz, out int groundCell)
    {
        float cx = bx + 0.5f, cz = bz + 0.5f;
        groundCell = (int)MathF.Round(terrain.Height(cx, cz) / BlockWorld.CellSize);
        float dx = cx - m.X, dz = cz - m.Z;
        float phase = (m.Seed & 1023) * 0.0123f;
        float angle = MathF.Atan2(dz, dx);
        float reach = m.Radius * (0.86f + 0.1f * MathF.Sin(angle * 3 + phase) + 0.06f * MathF.Sin(angle * 5 - phase * 1.7f));
        float d = MathF.Sqrt(dx * dx + dz * dz) / reach;
        if (d >= 1f) return 0;
        float body = MathF.Pow(1f - d * d, 1.2f) * (0.88f + 0.24f * Noise.Value3(cx * 0.05f, cz * 0.05f, phase));
        float peaks = 0.14f * MathF.Max(0f, MathF.Sin(cx * 0.11f + phase) * MathF.Sin(cz * 0.09f - phase)) * (1f - d);
        // It rises from the ground at its foot and from the cavern's floor further in, so it meets the
        // land without a wall wherever the land slopes. Terraces: broad ledges at fixed heights
        // (level all round), each rising about 2 m to the next over a narrow band (usually with a
        // 1 m step in it, so they can be climbed).
        float ground = groundCell * BlockWorld.CellSize;
        float inward = Math.Clamp((1f - d) / 0.45f, 0f, 1f);
        float surface = float.Lerp(ground, m.Floor, inward * inward * (3 - 2 * inward)) + m.Height * (body + peaks);
        const float terrace = 2f;
        float q = surface / terrace;
        float step = MathF.Floor(q), rise = Math.Clamp((q - step - 0.55f) / 0.45f, 0f, 1f);
        float top = terrace * (step + rise * rise * (3 - 2 * rise));
        return Math.Max(0, (int)MathF.Round(top - ground));
    }

    /// <summary>
    /// Whether the point, in a column whose ground is at <paramref name="ground"/>, lies in the
    /// cavern (a great hollow, its walls bulging and sagging with 3D noise) or the tunnel winding
    /// out to the mouth.
    /// </summary>
    public bool Hollow(Mountain m, Vector3 p, float ground) => CavernDistance(m, p) < 1f || InTunnel(m, p, ground);

    /// <summary>How far out through the cavern's wall a point is: under 1 inside, 1 on the wall.</summary>
    public static float CavernDistance(Mountain m, Vector3 p)
    {
        var rel = p - m.CavernCenter;
        float e = new Vector3(rel.X / m.CavernRadius, rel.Y / m.CavernHeight, rel.Z / m.CavernRadius).Length();
        float phase = (m.Seed & 1023) * 0.0123f;
        return e + 0.34f * (Noise.Value3(p.X * 0.08f, p.Y * 0.08f + phase, p.Z * 0.08f) - 0.5f)
                 + 0.14f * (Noise.Value3(p.X * 0.23f, p.Y * 0.23f, p.Z * 0.23f + phase) - 0.5f);
    }

    public static bool InTunnel(Mountain m, Vector3 p, float ground)
    {
        var rel = new Vector2(p.X - m.X, p.Z - m.Z);
        float along = Vector2.Dot(rel, m.Mouth);
        if (along < m.CavernRadius * 0.5f || along > m.Radius + 3f) return false;
        float phase = (m.Seed & 1023) * 0.0123f;
        // It winds a little and swells and narrows as it goes.
        float side = Vector2.Dot(rel, new Vector2(-m.Mouth.Y, m.Mouth.X)) - 2.5f * MathF.Sin(along * 0.12f + phase);
        float width = 2.3f + 0.6f * MathF.Sin(along * 0.31f - phase);
        float height = 4.3f + 0.8f * MathF.Sin(along * 0.23f + phase * 2f);
        return MathF.Abs(side) < width && p.Y - ground < height;
    }

    private Mountain? Generate(int mx, int mz)
    {
        if (WorldPreset.Current.StoneFields <= 0) return null;
        var random = new Random(unchecked(mx * 73856093 ^ mz * 19349663 ^ seed * 83492791 ^ 0x3417)); // not HashCode.Combine: it changes every run
        if (random.NextSingle() > Chance) return null;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            float x = (mx + 0.2f + 0.6f * random.NextSingle()) * CellSize;
            float z = (mz + 0.2f + 0.6f * random.NextSingle()) * CellSize;
            float radius = 28f + 14f * random.NextSingle(), height = 18f + 10f * random.NextSingle();
            int mountainSeed = random.Next();
            float floor = terrain.Height(x, z);
            if (floor < TerrainField.WaterLevel + 2f || terrain.InPond(x, z, radius + 25f)) continue;
            // Broad even land: the ground round its foot stays dry and within a few metres of the middle.
            bool fits = true;
            var mouth = Vector2.UnitX;
            float lowest = float.MaxValue;
            for (int k = 0; k < 12 && fits; k++)
            {
                var dir = new Vector2(MathF.Cos(k * MathF.Tau / 12), MathF.Sin(k * MathF.Tau / 12));
                float g = terrain.Height(x + dir.X * radius, z + dir.Y * radius);
                float outside = terrain.Height(x + dir.X * (radius + 6f), z + dir.Y * (radius + 6f));
                fits = g > TerrainField.WaterLevel + 1f && MathF.Abs(g - floor) < 7f;
                // The tunnel comes out where the land outside is lowest, and dry.
                if (outside > TerrainField.WaterLevel + 1f && outside < lowest) (lowest, mouth) = (outside, dir);
            }
            if (!fits || lowest == float.MaxValue) continue;
            return new Mountain(x, z, radius, height, floor, radius * 0.55f, MathF.Min(height * 0.45f, 12f), mouth, mountainSeed);
        }
        return null;
    }
}

/// <summary>3D value noise in [0, 1], for shapes that must look the same from any thread.</summary>
public static class Noise
{
    public static float Value3(float x, float y, float z)
    {
        float fx = MathF.Floor(x), fy = MathF.Floor(y), fz = MathF.Floor(z);
        float tx = x - fx, ty = y - fy, tz = z - fz;
        tx = tx * tx * (3 - 2 * tx);
        ty = ty * ty * (3 - 2 * ty);
        tz = tz * tz * (3 - 2 * tz);
        int ix = (int)fx, iy = (int)fy, iz = (int)fz;
        float Corner(int dx, int dy, int dz) => Hash(ix + dx, iy + dy, iz + dz);
        float a = float.Lerp(Corner(0, 0, 0), Corner(1, 0, 0), tx), b = float.Lerp(Corner(0, 1, 0), Corner(1, 1, 0), tx);
        float c = float.Lerp(Corner(0, 0, 1), Corner(1, 0, 1), tx), d = float.Lerp(Corner(0, 1, 1), Corner(1, 1, 1), tx);
        return float.Lerp(float.Lerp(a, b, ty), float.Lerp(c, d, ty), tz);
    }

    private static float Hash(int x, int y, int z)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177);
            h = (h ^ (h >> 13)) * 1103515245u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
        }
    }
}
