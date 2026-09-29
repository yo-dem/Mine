using System.Numerics;
using Mine.World;

namespace Mine.Rendering;

/// <summary>
/// Models of the materials as things you hold, see lying on the ground and in the inventory
/// (object vertex layout, centred on the origin, about one unit across): a wood block with bark
/// sides and rings on its ends, a speckled stone block, a glowing crystal block, flat pixel-art grass seeds; and the plain cube of a chip,
/// white, to be tinted.
/// </summary>
internal static class ItemMeshes
{
    public static List<float> Chip()
    {
        var m = new MeshBuilder();
        m.Box(new Vector3(-0.5f), new Vector3(0.5f), Vector3.One);
        return m.Vertices;
    }

    public static List<float> Item(Resource resource)
    {
        var m = new MeshBuilder();
        switch (resource)
        {
            case Resource.Wood:
            {
                // A log section in 4×4×4 voxels: bark on the sides, rings on the ends.
                var bark = new Vector3(0.34f, 0.23f, 0.21f);
                var light = new Vector3(0.70f, 0.52f, 0.38f);
                var dark = new Vector3(0.56f, 0.40f, 0.30f);
                Voxels(m, (x, y, z, h) =>
                {
                    bool side = x == 0 || x == 3 || z == 0 || z == 3;
                    if (side) return (bark * (0.8f + 0.35f * h) * (x % 2 == 0 ? 1f : 0.9f), 0f);
                    return (((x + z) % 2 == 0 ? light : dark) * (0.92f + 0.12f * h), 0f); // the rings of the end grain
                });
                break;
            }
            case Resource.Stone:
            {
                var stone = new Vector3(0.54f, 0.50f, 0.58f);
                Voxels(m, (_, _, _, h) => (stone * (h < 0.15f ? 0.7f : 0.82f + 0.3f * h), 0f));
                break;
            }
            case Resource.Seeds:
                Seeds(m);
                break;
            default:
            {
                // The crystal block as it is placed: violet glass glowing brighter toward the middle
                // of each face (like BlockRenderer's crystal).
                Voxels(m, (x, y, z, h) =>
                {
                    bool rim = (x == 0 || x == 3 ? 1 : 0) + (y == 0 || y == 3 ? 1 : 0) + (z == 0 || z == 3 ? 1 : 0) >= 2;
                    return (Blocks.CrystalLight * (rim ? 0.75f : 1f) * (0.9f + 0.2f * h), rim ? 0.55f : 0.8f);
                });
                break;
            }
        }
        return m.Vertices;
    }

    // Grass seeds, drawn flat like a pixel-art icon: four little lozenge grains on a 16×16 grid
    // (outlined dark, teal or violet, with a pale glint), a card one unit across seen from both
    // sides, glowing faintly.
    private static void Seeds(MeshBuilder m)
    {
        const int n = 16;
        const float pixel = 1f / n;
        (float X, float Y, float Angle, bool Violet)[] grains =
            [(4.5f, 10.5f, -0.7f, false), (11f, 11f, 0.9f, true), (6.5f, 4.5f, 0.35f, true), (12.5f, 4.5f, -1.2f, false)];
        const float halfLength = 3.9f, halfWidth = 2.1f;
        var outline = new Vector3(0.20f, 0.13f, 0.30f);
        var teal = new Vector3(0.30f, 0.85f, 0.75f);
        var violet = new Vector3(0.66f, 0.46f, 1.0f);
        var glint = new Vector3(0.92f, 0.95f, 1.0f);
        for (int py = 0; py < n; py++)
        for (int px = 0; px < n; px++)
        {
            Vector3? color = null;
            float emissive = 0f;
            foreach (var g in grains)
            {
                float dx = px + 0.5f - g.X, dy = py + 0.5f - g.Y;
                float u = dx * MathF.Cos(g.Angle) + dy * MathF.Sin(g.Angle), v = -dx * MathF.Sin(g.Angle) + dy * MathF.Cos(g.Angle);
                float d = MathF.Abs(u) / halfLength + MathF.Abs(v) / halfWidth; // 1 on the lozenge's edge
                if (d > 1f) continue;
                if (d > 0.8f) color = outline;
                else
                {
                    bool shine = u > 0.3f && u < 1.8f && v < 0f && v > -1.1f;
                    color = shine ? glint : g.Violet ? violet : teal;
                    emissive = shine ? 0.6f : 0.35f;
                }
                break;
            }
            if (color is not { } c) continue;
            // Pixel (px, py), row 0 at the top.
            var a = new Vector3(px * pixel - 0.5f, 0.5f - py * pixel, 0);
            var b = a + new Vector3(pixel, 0, 0);
            var d2 = a - new Vector3(0, pixel, 0);
            var e = b - new Vector3(0, pixel, 0);
            m.Triangle(a, d2, e, c, emissive); m.Triangle(a, e, b, c, emissive);
            m.Triangle(a, e, d2, c, emissive); m.Triangle(a, b, e, c, emissive);
        }
    }

    // The outer voxels of a 4×4×4 cube, each coloured by `shade(x, y, z, random 0..1)` (colour, emissive).
    private static void Voxels(MeshBuilder m, Func<int, int, int, float, (Vector3 Color, float Emissive)> shade)
    {
        const int n = 4;
        const float size = 1f / n;
        var random = new Random(7);
        for (int y = 0; y < n; y++)
        for (int z = 0; z < n; z++)
        for (int x = 0; x < n; x++)
        {
            bool outer = x == 0 || y == 0 || z == 0 || x == n - 1 || y == n - 1 || z == n - 1;
            float h = random.NextSingle();
            if (!outer) continue;
            var min = new Vector3(x, y, z) * size - new Vector3(0.5f);
            var (color, emissive) = shade(x, y, z, h);
            m.Box(min, min + new Vector3(size), color, emissive);
        }
    }
}
