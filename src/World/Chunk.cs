using Mine.Rendering;

namespace Mine.World;

/// <summary>A 16 x Height x 16 column of blocks plus its GPU mesh handle.</summary>
public sealed class Chunk
{
    public const int Size = 16;
    public const int SizeShift = 4; // log2(Size), for fast world -> chunk conversion
    public const int Height = 128;

    private readonly BlockType[] _blocks = new BlockType[Size * Size * Height];

    public int ChunkX { get; }
    public int ChunkZ { get; }

    /// <summary>True when the block data changed and the mesh must be rebuilt.</summary>
    public bool Dirty { get; set; } = true;

    public ChunkMesh? Mesh { get; set; }

    public Chunk(int chunkX, int chunkZ)
    {
        ChunkX = chunkX;
        ChunkZ = chunkZ;
    }

    public static bool InBounds(int x, int y, int z) =>
        (uint)x < Size && (uint)y < Height && (uint)z < Size;

    private static int Index(int x, int y, int z) => (y * Size + z) * Size + x;

    public BlockType Get(int x, int y, int z) => _blocks[Index(x, y, z)];

    public void Set(int x, int y, int z, BlockType block) => _blocks[Index(x, y, z)] = block;
}
