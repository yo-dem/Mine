using System.Numerics;

namespace Mine.World;

/// <summary>What the player can stand on: the terrain tiles, the tops of the floating islands, and the blocks they placed.</summary>
public sealed class Ground(TerrainField terrain, IslandField islands, Blocks blocks)
{
    /// <summary>
    /// Height of the surface under (x, z) for something currently at height <paramref name="y"/>;
    /// blocks count within <paramref name="blockRadius"/> (the player stands on an edge, a small
    /// cube only on the block under it: beside a wall it would find the wall's top).
    /// </summary>
    public float Height(float x, float z, float y, float blockRadius = 0.2f)
    {
        float land = terrain.Height(x, z);
        if (islands.GroundBelow(x, z, y, out float top)) land = MathF.Max(land, top);
        if (blocks.GroundBelow(x, z, y, out float block, blockRadius)) land = MathF.Max(land, block);
        return land;
    }

    /// <summary>Whether a point is inside a block (small things falling bounce off them).</summary>
    public bool Solid(Vector3 p) => blocks.Inside(p);
}
