namespace Mine.World;

public enum BlockType : byte
{
    Air = 0,
    Grass,
    Dirt,
    Stone,
    Sand,
    Log,
    Leaves,
    Planks,
    Cobblestone,
}

/// <summary>Indices of the tiles inside the texture atlas (see TextureAtlas).</summary>
public enum Tile
{
    GrassTop,
    GrassSide,
    Dirt,
    Stone,
    Sand,
    LogSide,
    LogTop,
    Leaves,
    Planks,
    Cobblestone,
}

public static class Blocks
{
    public static bool IsSolid(BlockType block) => block != BlockType.Air;

    /// <summary>Tile used for a given face. Face order: +X, -X, +Y, -Y, +Z, -Z.</summary>
    public static Tile GetTile(BlockType block, int face)
    {
        const int Top = 2, Bottom = 3;
        return block switch
        {
            BlockType.Grass => face switch { Top => Tile.GrassTop, Bottom => Tile.Dirt, _ => Tile.GrassSide },
            BlockType.Dirt => Tile.Dirt,
            BlockType.Stone => Tile.Stone,
            BlockType.Sand => Tile.Sand,
            BlockType.Log => face is Top or Bottom ? Tile.LogTop : Tile.LogSide,
            BlockType.Leaves => Tile.Leaves,
            BlockType.Planks => Tile.Planks,
            BlockType.Cobblestone => Tile.Cobblestone,
            _ => Tile.Stone,
        };
    }

    /// <summary>Blocks the player can place, bound to keys 1..N.</summary>
    public static readonly BlockType[] Hotbar =
    [
        BlockType.Grass, BlockType.Dirt, BlockType.Stone, BlockType.Cobblestone,
        BlockType.Planks, BlockType.Log, BlockType.Leaves, BlockType.Sand,
    ];
}

public readonly record struct BlockPos(int X, int Y, int Z);
