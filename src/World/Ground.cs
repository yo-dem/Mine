namespace Mine.World;

/// <summary>What the player can stand on: the terrain tiles, and the tops of the floating islands.</summary>
public sealed class Ground(TerrainField terrain, IslandField islands)
{
    /// <summary>Height of the surface under (x, z) for something currently at height <paramref name="y"/>.</summary>
    public float Height(float x, float z, float y)
    {
        float land = terrain.Height(x, z);
        return islands.GroundBelow(x, z, y, out float top) ? MathF.Max(land, top) : land;
    }
}
