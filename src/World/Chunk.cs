using Mine.Rendering;

namespace Mine.World;

/// <summary>
/// A 16 x 16 column of cells plus its GPU mesh handle. Cells are one block wide and deep
/// but only half a block tall, so the world is built from half blocks: a cube is two cells.
/// </summary>
public sealed class Chunk
{
    public const int Size = 16;
    public const int SizeShift = 4; // log2(Size), for fast world -> chunk conversion
    public const int Height = 256;  // in cells, i.e. 128 blocks
    public const float CellHeight = 0.5f;

    private readonly BlockType[] _blocks = new BlockType[Size * Size * Height];
    private ushort[]? _light; // packed RGB block light (see BlockLight), allocated on first use

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

    /// <summary>Cell row containing a world height.</summary>
    public static int CellY(float worldY) => (int)MathF.Floor(worldY / CellHeight);

    public static bool InBounds(int x, int y, int z) =>
        (uint)x < Size && (uint)y < Height && (uint)z < Size;

    private static int Index(int x, int y, int z) => (y * Size + z) * Size + x;

    public BlockType Get(int x, int y, int z) => _blocks[Index(x, y, z)];

    public void Set(int x, int y, int z, BlockType block) => _blocks[Index(x, y, z)] = block;

    /// <summary>True once any cell of this chunk has received block light.</summary>
    public bool HasLight => _light is not null;

    public ushort GetLight(int x, int y, int z) => _light?[Index(x, y, z)] ?? 0;

    public void SetLight(int x, int y, int z, ushort light)
    {
        if (_light is null)
        {
            if (light == 0) return;
            _light = new ushort[Size * Size * Height];
        }
        _light[Index(x, y, z)] = light;
    }
}
