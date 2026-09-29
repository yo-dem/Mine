namespace Mine.World;

/// <summary>What the player can stand on: the terrain tiles, the tops of the floating islands, and the blocks they placed.</summary>
public sealed class Ground(TerrainField terrain, IslandField islands, Blocks blocks)
{
    /// <summary>Height of the surface under (x, z) for something currently at height <paramref name="y"/>.</summary>
    public float Height(float x, float z, float y)
    {
        float land = terrain.Height(x, z);
        if (islands.GroundBelow(x, z, y, out float top)) land = MathF.Max(land, top);
        if (blocks.GroundBelow(x, z, y, out float block)) land = MathF.Max(land, block);
        return land;
    }
}
