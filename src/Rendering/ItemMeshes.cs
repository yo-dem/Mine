using System.Numerics;
using Mine.World;

namespace Mine.Rendering;

/// <summary>
/// Models of the materials as things you hold, see lying on the ground and in the inventory
/// (object vertex layout, centred on the origin, about one unit across): a wood block with bark
/// sides and rings on its ends, a speckled stone block, a crystal; and the plain cube of a chip,
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
                    if (side) return bark * (0.8f + 0.35f * h) * (x % 2 == 0 ? 1f : 0.9f);
                    return ((x + z) % 2 == 0 ? light : dark) * (0.92f + 0.12f * h); // the rings of the end grain
                });
                break;
            }
            case Resource.Stone:
            {
                var stone = new Vector3(0.54f, 0.50f, 0.58f);
                Voxels(m, (_, _, _, h) => stone * (h < 0.15f ? 0.7f : 0.82f + 0.3f * h));
                break;
            }
            default:
            {
                var violet = new Vector3(0.66f, 0.46f, 1.0f);
                m.Octahedron(new(0, -0.5f, 0), 0.2f, 1f, violet, 0.75f, tilt: new(0.08f, 0, 0.03f));
                m.Octahedron(new(0.17f, -0.5f, 0.07f), 0.12f, 0.57f, violet * 0.9f, 0.75f, tilt: new(0.25f, 0, 0.1f));
                m.Octahedron(new(-0.13f, -0.5f, 0.13f), 0.1f, 0.43f, violet * 1.1f, 0.75f, tilt: new(-0.2f, 0, 0.22f));
                break;
            }
        }
        return m.Vertices;
    }

    // The outer voxels of a 4×4×4 cube, each coloured by `color(x, y, z, random 0..1)`.
    private static void Voxels(MeshBuilder m, Func<int, int, int, float, Vector3> color)
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
            m.Box(min, min + new Vector3(size), color(x, y, z, h));
        }
    }
}
