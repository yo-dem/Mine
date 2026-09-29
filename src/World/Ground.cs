using System.Numerics;

namespace Mine.World;

/// <summary>What the player can stand on: the terrain tiles, the tops of the floating islands and the blocks.</summary>
public sealed class Ground(TerrainField terrain, IslandField islands, BlockWorld blocks)
{
    /// <summary>Height of the surface under (x, z) for something currently at height <paramref name="y"/>.</summary>
    public float Height(float x, float z, float y)
    {
        float land = terrain.Height(x, z);
        if (islands.GroundBelow(x, z, y, out float top)) land = MathF.Max(land, top);
        return MathF.Max(land, blocks.Height(x, z, y, land));
    }

    /// <summary>
    /// Like <see cref="Height"/>, but the blocks count under a whole square footprint (so a body can
    /// stand on a block's edge).
    /// </summary>
    public float Height(float x, float z, float y, float halfWidth)
    {
        float land = Height(x, z, y);
        return MathF.Max(land, blocks.FootprintHeight(x, z, halfWidth, y, land));
    }

    /// <summary>Whether a body standing at <paramref name="feet"/> overlaps a block between the given heights above its feet.</summary>
    public bool BlockedByBlocks(Vector3 feet, float halfWidth, float from, float to) =>
        blocks.Overlaps(new Vector3(feet.X - halfWidth, feet.Y + from, feet.Z - halfWidth),
                        new Vector3(feet.X + halfWidth, feet.Y + to, feet.Z + halfWidth));
}
