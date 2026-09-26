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
    Crystal,
    GlowMushroom,
    Lantern,
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
    Crystal,
    GlowMushroom,
    Lantern,
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
            BlockType.Crystal => Tile.Crystal,
            BlockType.GlowMushroom => Tile.GlowMushroom,
            BlockType.Lantern => Tile.Lantern,
            _ => Tile.Stone,
        };
    }

    /// <summary>Blocks the player can place: keys 1..9 and 0 pick the first ten, the mouse wheel cycles through all.</summary>
    public static readonly BlockType[] Hotbar =
    [
        BlockType.Grass, BlockType.Dirt, BlockType.Stone, BlockType.Cobblestone,
        BlockType.Planks, BlockType.Log, BlockType.Leaves, BlockType.Sand,
        BlockType.Crystal, BlockType.Lantern, BlockType.GlowMushroom,
    ];
}

public readonly record struct BlockPos(int X, int Y, int Z);
