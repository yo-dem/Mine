namespace Mine.World;

/// <summary>Fills chunks with terrain: rolling hills, beaches and trees.</summary>
public sealed class TerrainGenerator
{
    private const int SeaLevel = 40;

    private readonly PerlinNoise _height;
    private readonly PerlinNoise _detail;
    private readonly int _seed;

    public TerrainGenerator(int seed)
    {
        _seed = seed;
        _height = new PerlinNoise(seed);
        _detail = new PerlinNoise(seed + 1);
    }

    public int GetHeight(int worldX, int worldZ)
    {
        float hills = _height.Fractal(worldX * 0.006f, worldZ * 0.006f, 5);
        float bumps = _detail.Fractal(worldX * 0.03f, worldZ * 0.03f, 3);
        int h = (int)(SeaLevel + 4 + hills * 28 + bumps * 3);
        return Math.Clamp(h, 1, Chunk.Height - 10);
    }

    public void Generate(Chunk chunk)
    {
        int baseX = chunk.ChunkX * Chunk.Size;
        int baseZ = chunk.ChunkZ * Chunk.Size;

        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
        {
            int height = GetHeight(baseX + x, baseZ + z);
            bool beach = height <= SeaLevel + 1;

            for (int y = 0; y <= height; y++)
            {
                BlockType block;
                if (y < height - 3) block = BlockType.Stone;
                else if (beach) block = BlockType.Sand;
                else if (y < height) block = BlockType.Dirt;
                else block = BlockType.Grass;
                chunk.Set(x, y, z, block);
            }
        }

        PlaceTrees(chunk, baseX, baseZ);
    }

    private void PlaceTrees(Chunk chunk, int baseX, int baseZ)
    {
        // Trees are kept 2 blocks away from the chunk border so the leaves never
        // cross into a neighbouring chunk. Simple, and good enough for now.
        for (int z = 2; z < Chunk.Size - 2; z++)
        for (int x = 2; x < Chunk.Size - 2; x++)
        {
            if (Hash(baseX + x, baseZ + z) % 97 != 0) continue;

            int ground = GetHeight(baseX + x, baseZ + z);
            if (chunk.Get(x, ground, z) != BlockType.Grass) continue;

            int trunk = 4 + (int)(Hash(baseX + x + 7, baseZ + z - 3) % 3);
            int top = ground + trunk;
            if (top + 2 >= Chunk.Height) continue;

            for (int y = ground + 1; y <= top; y++)
                chunk.Set(x, y, z, BlockType.Log);

            for (int ly = top - 2; ly <= top + 1; ly++)
            {
                int radius = ly >= top ? 1 : 2;
                for (int lz = -radius; lz <= radius; lz++)
                for (int lx = -radius; lx <= radius; lx++)
                {
                    // Trim the corners for a rounder crown.
                    if (radius == 2 && Math.Abs(lx) == 2 && Math.Abs(lz) == 2) continue;
                    if (chunk.Get(x + lx, ly, z + lz) == BlockType.Air)
                        chunk.Set(x + lx, ly, z + lz, BlockType.Leaves);
                }
            }
        }
    }

    private uint Hash(int x, int z)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + z * 668265263 + _seed * 144269504);
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }
}
