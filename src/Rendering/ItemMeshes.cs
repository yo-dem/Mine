using System.Numerics;
using Mine.World;

namespace Mine.Rendering;

/// <summary>
/// Models of the materials as things you hold, see lying on the ground and in the inventory
/// (object vertex layout, centred on the origin, about one unit across): a wood block with bark
/// sides and rings on its ends, a speckled stone block, a glowing crystal block, pixel-art grass seeds extruded into relief (held in a glass jar, see <see cref="SeedJar"/>); and the plain cube of a chip,
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

    // A seed grain: a lozenge on the pixel grid, centred at (X, Y) in pixels (y down), turned by Angle.
    private readonly record struct Grain(float X, float Y, float Angle, bool Violet);

    private static readonly Vector3 SeedOutline = new(0.20f, 0.13f, 0.30f);
    private static readonly Vector3 SeedTeal = new(0.30f, 0.85f, 0.75f);
    private static readonly Vector3 SeedViolet = new(0.66f, 0.46f, 1.0f);
    private static readonly Vector3 SeedGlint = new(0.92f, 0.95f, 1.0f);

    // Grass seeds as they lie on the ground and in the inventory: the pixel-art icon (four lozenge
    // grains on a 16×16 grid, outlined dark, teal or violet with a pale glint) extruded into relief,
    // each grain thickest along its middle, one unit across.
    private static void Seeds(MeshBuilder m)
    {
        Grain[] grains = [new(4.5f, 10.5f, -0.7f, false), new(11f, 11f, 0.9f, true), new(6.5f, 4.5f, 0.35f, true), new(12.5f, 4.5f, -1.2f, false)];
        SeedPixels(m, grains, 0, 16, Matrix4x4.CreateScale(1f / 16) * Matrix4x4.CreateTranslation(-0.5f, 0.5f, 0));
    }

    /// <summary>
    /// The seeds as they are held: a squat glass jar (cubic, or round and faceted: <see cref="JarSides"/>) with a wooden lid and a twine band,
    /// two thirds full of glowing grains. Two meshes: the solid parts, and the glass (drawn after them,
    /// see-through, with the object shader's glass mode).
    /// </summary>
    public static (List<float> Solid, List<float> Glass) SeedJar()
    {
        const int sides = JarSides;
        var solid = new MeshBuilder();
        var glass = new MeshBuilder();

        // The glass: a bevelled foot, straight sides, a shoulder, a short neck and a thick lip.
        var tint = new Vector3(0.75f, 0.85f, 1.0f);
        Lathe(glass, [(0f, -0.5f), (0.30f, -0.5f), (0.34f, -0.45f), (0.34f, 0.10f), (0.27f, 0.22f), (0.23f, 0.26f), (0.23f, 0.31f), (0.26f, 0.32f), (0.26f, 0.36f), (0.21f, 0.36f)],
            sides, tint, 0f);
        // Light caught along one side: a broad streak and a thin one.
        float faceAngle = MathF.Round(2.1f / (MathF.Tau / sides)) * MathF.Tau / sides;
        var normal = new Vector3(MathF.Cos(faceAngle), 0, MathF.Sin(faceAngle));
        var along = new Vector3(-normal.Z, 0, normal.X);
        for (int k = 0; k < 2; k++)
        {
            float offset = (k == 0 ? -0.2f : -0.07f) * (sides == 4 ? 1f : 0.4f), w = k == 0 ? 0.025f : 0.012f;
            float y0 = k == 0 ? -0.36f : -0.1f, y1 = k == 0 ? 0.04f : 0.06f;
            var across = along * w;
            var c0 = normal * 0.343f + along * offset + new Vector3(0, y0, 0);
            var c1 = normal * 0.343f + along * offset + new Vector3(0, y1, 0);
            glass.Triangle(c0 - across, c1 + across, c1 - across, Vector3.One, 1f);
            glass.Triangle(c0 - across, c0 + across, c1 + across, Vector3.One, 1f);
        }

        // The lid: a wooden cap over the lip, a lighter ring on top, and a knob.
        var wood = new Vector3(0.56f, 0.40f, 0.30f);
        var bark = new Vector3(0.34f, 0.23f, 0.21f);
        Lathe(solid, [(0f, 0.34f), (0.285f, 0.34f), (0.285f, 0.43f), (0.25f, 0.46f), (0f, 0.46f)], sides, bark, 0f);
        Lathe(solid, [(0.23f, 0.461f), (0.17f, 0.461f)], sides, wood * 1.15f, 0f);
        Lathe(solid, [(0f, 0.46f), (0.08f, 0.46f), (0.08f, 0.51f), (0.05f, 0.53f), (0f, 0.53f)], sides, wood, 0f);
        // Twine tied round the neck.
        Lathe(solid, [(0f, 0.255f), (0.245f, 0.255f), (0.245f, 0.30f), (0f, 0.30f)], sides, new Vector3(0.62f, 0.42f, 0.66f), 0f);

        // The grains, filling two thirds of it, each turned its own way, kept clear of the glass.
        var random = new Random(11);
        Grain[] grain = [new(0, 0, 0, false)];
        Grain[] violet = [new(0, 0, 0, true)];
        const float inside = 0.34f - 0.08f; // the glass's apothem, less a grain's half-length
        for (int i = 0; i < 120; i++)
        {
            float x, z;
            do (x, z) = ((random.NextSingle() * 2f - 1f) * inside, (random.NextSingle() * 2f - 1f) * inside);
            while (!InsidePolygon(x, z, sides, inside));
            float y = -0.42f + 0.5f * random.NextSingle();
            var turn = Quaternion.CreateFromYawPitchRoll(random.NextSingle() * MathF.Tau, (random.NextSingle() - 0.5f) * 2.4f, random.NextSingle() * MathF.Tau);
            var place = Matrix4x4.CreateScale(0.02f) * Matrix4x4.CreateFromQuaternion(turn) * Matrix4x4.CreateTranslation(x, y, z);
            SeedPixels(solid, i % 2 == 0 ? grain : violet, -5, 5, place);
        }
        return (solid.Vertices, glass.Vertices);
    }

    // The seed jar's facets round its axis: 4 makes it cubic, 10 a round faceted jar.
    private const int JarSides = 4;

    // Whether (x, z) is inside the regular polygon of Lathe with this apothem.
    private static bool InsidePolygon(float x, float z, int sides, float apothem)
    {
        for (int k = 0; k < sides; k++)
        {
            float a = k * MathF.Tau / sides;
            if (x * MathF.Cos(a) + z * MathF.Sin(a) > apothem) return false;
        }
        return true;
    }

    /// <summary>
    /// Grains drawn on a pixel grid (pixels <paramref name="from"/>..<paramref name="to"/> each way),
    /// extruded: one box per pixel, the outline thin and the grain thickening toward its middle.
    /// <paramref name="place"/> maps pixel space (x right, y up, z out; pixel (px, py) centred at
    /// (px + 0.5, -(py + 0.5))) into the model.
    /// </summary>
    private static void SeedPixels(MeshBuilder m, Grain[] grains, int from, int to, Matrix4x4 place)
    {
        const float halfLength = 3.9f, halfWidth = 2.1f;
        for (int py = from; py < to; py++)
        for (int px = from; px < to; px++)
        {
            Vector3? color = null;
            float emissive = 0f, thickness = 0f;
            foreach (var g in grains)
            {
                float dx = px + 0.5f - g.X, dy = py + 0.5f - g.Y;
                float u = dx * MathF.Cos(g.Angle) + dy * MathF.Sin(g.Angle), v = -dx * MathF.Sin(g.Angle) + dy * MathF.Cos(g.Angle);
                float d = MathF.Abs(u) / halfLength + MathF.Abs(v) / halfWidth; // 1 on the lozenge's edge
                if (d > 1f) continue;
                thickness = 0.6f + 2.2f * MathF.Sqrt(MathF.Max(1f - d, 0f)); // half-thickness, in pixels
                if (d > 0.8f) color = SeedOutline;
                else
                {
                    bool shine = u > 0.3f && u < 1.8f && v < 0f && v > -1.1f;
                    color = shine ? SeedGlint : g.Violet ? SeedViolet : SeedTeal;
                    emissive = shine ? 0.6f : 0.35f;
                }
                break;
            }
            if (color is not { } c) continue;
            var center = Vector3.Transform(new Vector3(px + 0.5f, -(py + 0.5f), 0), place);
            m.OrientedBox(center,
                Vector3.TransformNormal(new Vector3(0.5f, 0, 0), place),
                Vector3.TransformNormal(new Vector3(0, 0.5f, 0), place),
                Vector3.TransformNormal(new Vector3(0, 0, thickness), place), c, emissive);
        }
    }

    /// <summary>
    /// A faceted solid of revolution around the Y axis: the profile's (radius, height) points from
    /// bottom to top, turned through <paramref name="sides"/> flat facets, faces wound outward. The
    /// radius is the apothem (to the middle of a facet), and facet k faces the angle k·τ/sides, so
    /// four sides make a square aligned with the axes.
    /// </summary>
    private static void Lathe(MeshBuilder m, (float R, float Y)[] profile, int sides, Vector3 color, float emissive)
    {
        profile = profile.Select(p => (p.R / MathF.Cos(MathF.PI / sides), p.Y)).ToArray();
        for (int k = 0; k < sides; k++)
        {
            float a0 = (k - 0.5f) * MathF.Tau / sides, a1 = (k + 0.5f) * MathF.Tau / sides;
            var d0 = new Vector3(MathF.Cos(a0), 0, MathF.Sin(a0));
            var d1 = new Vector3(MathF.Cos(a1), 0, MathF.Sin(a1));
            var mid = Vector3.Normalize(d0 + d1);
            for (int i = 0; i + 1 < profile.Length; i++)
            {
                var (r0, y0) = profile[i];
                var (r1, y1) = profile[i + 1];
                // Outward, the profile's tangent turned a quarter to the right.
                var outward = mid * (y1 - y0) - Vector3.UnitY * (r1 - r0);
                Vector3 p00 = d0 * r0 + Vector3.UnitY * y0, p10 = d1 * r0 + Vector3.UnitY * y0;
                Vector3 p01 = d0 * r1 + Vector3.UnitY * y1, p11 = d1 * r1 + Vector3.UnitY * y1;
                Face(p00, p10, p11, outward);
                if (r1 > 0f) Face(p00, p11, p01, outward);
            }
        }

        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 outward)
        {
            if ((b - a).LengthSquared() < 1e-10f || (c - a).LengthSquared() < 1e-10f || (c - b).LengthSquared() < 1e-10f) return;
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0) (b, c) = (c, b);
            m.Triangle(a, b, c, color, emissive);
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
