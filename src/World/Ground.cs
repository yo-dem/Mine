using System.Numerics;

namespace Mine.World;

/// <summary>What the player can stand on: the terrain, the tops of the floating islands, and the sea
/// bed no deeper than <see cref="WadeDepth"/> (the player wades through the water, never sinks).</summary>
public sealed class Ground(TerrainField terrain, IslandField islands)
{
    public const float WadeDepth = 0.9f;

    /// <summary>Height of the surface under (x, z) for something currently at height <paramref name="y"/>.</summary>
    public float Height(float x, float z, float y)
    {
        float land = MathF.Max(terrain.Height(x, z), TerrainField.WaterLevel - WadeDepth);
        return islands.GroundBelow(x, z, y, out float top) ? MathF.Max(land, top) : land;
    }

    /// <summary>Surface normal under (x, z); island tops count as flat.</summary>
    public Vector3 Normal(float x, float z, float y) =>
        (islands.GroundBelow(x, z, y, out float top) && top > terrain.Height(x, z)) || terrain.Height(x, z) < TerrainField.WaterLevel - WadeDepth
            ? Vector3.UnitY
            : terrain.Normal(x, z);
}
