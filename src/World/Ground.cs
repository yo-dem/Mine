using System.Numerics;

namespace Mine.World;

/// <summary>What the player can stand on: the terrain, and the tops of the floating islands.</summary>
public sealed class Ground(TerrainField terrain, IslandField islands)
{
    /// <summary>Height of the surface under (x, z) for something currently at height <paramref name="y"/>.</summary>
    public float Height(float x, float z, float y)
    {
        float land = terrain.Height(x, z);
        return islands.GroundBelow(x, z, y, out float top) ? MathF.Max(land, top) : land;
    }

    /// <summary>Surface normal under (x, z); island tops count as flat.</summary>
    public Vector3 Normal(float x, float z, float y) =>
        islands.GroundBelow(x, z, y, out float top) && top > terrain.Height(x, z) ? Vector3.UnitY : terrain.Normal(x, z);
}
