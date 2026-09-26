using System.Runtime.InteropServices;
using Mine.World;

namespace Mine.Rendering;

/// <summary>
/// Turns chunk block data into triangles. Only faces touching air are emitted,
/// and every vertex gets a simple ambient-occlusion term. Cells are half a block tall.
/// Vertex layout: position (3), uv (2), ambient occlusion (1), block light RGB (3).
/// Directional sunlight is computed in the shader; block light is averaged per vertex
/// over the air cells touching it, for smooth coloured lighting.
/// </summary>
public sealed class ChunkMesher
{
    public const int FloatsPerVertex = 9;

    // Face order: +X, -X, +Y, -Y, +Z, -Z (matches Blocks.GetTile).
    private static readonly int[,] Normals =
    {
        { 1, 0, 0 }, { -1, 0, 0 }, { 0, 1, 0 }, { 0, -1, 0 }, { 0, 0, 1 }, { 0, 0, -1 },
    };

    // Four corners per face, counter-clockwise when seen from outside.
    private static readonly int[,,] Corners =
    {
        { { 1, 0, 0 }, { 1, 1, 0 }, { 1, 1, 1 }, { 1, 0, 1 } },
        { { 0, 0, 1 }, { 0, 1, 1 }, { 0, 1, 0 }, { 0, 0, 0 } },
        { { 0, 1, 0 }, { 0, 1, 1 }, { 1, 1, 1 }, { 1, 1, 0 } },
        { { 0, 0, 0 }, { 1, 0, 0 }, { 1, 0, 1 }, { 0, 0, 1 } },
        { { 1, 0, 1 }, { 1, 1, 1 }, { 0, 1, 1 }, { 0, 0, 1 } },
        { { 0, 0, 0 }, { 0, 1, 0 }, { 1, 1, 0 }, { 1, 0, 0 } },
    };

    // Tile-local UVs per corner (v = 0 is the top of the tile).
    private static readonly float[,] SideUvs = { { 1, 1 }, { 1, 0 }, { 0, 0 }, { 0, 1 } };
    private static readonly float[,] FlatUvs = { { 0, 0 }, { 0, 1 }, { 1, 1 }, { 1, 0 } };

    // Brightness for 0..3 unoccluded neighbours.
    private static readonly float[] AoCurve = [0.45f, 0.65f, 0.82f, 1.0f];

    // Two triangles per quad, split along either diagonal.
    private static readonly int[] DefaultOrder = [0, 1, 2, 0, 2, 3];
    private static readonly int[] FlippedOrder = [1, 2, 3, 1, 3, 0];

    // For each face/corner: offsets of the side1, side2 and diagonal cells used for AO.
    private static readonly int[,,,] AoOffsets = BuildAoOffsets();

    private readonly List<float> _vertices = new(64 * 1024);
    private Chunk?[] _chunks = [];
    private readonly int[] _ao = new int[4];
    private readonly (float R, float G, float B)[] _light = new (float, float, float)[4];

    /// <summary>
    /// Builds the mesh of the centre chunk of a 3x3 neighbourhood
    /// (index = (dz + 1) * 3 + (dx + 1)). The returned span is reused on the next call.
    /// </summary>
    public ReadOnlySpan<float> Build(Chunk?[] neighbourhood)
    {
        _chunks = neighbourhood;
        _vertices.Clear();

        var chunk = neighbourhood[4]!;
        int baseX = chunk.ChunkX * Chunk.Size;
        int baseZ = chunk.ChunkZ * Chunk.Size;

        for (int y = 0; y < Chunk.Height; y++)
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
        {
            var block = chunk.Get(x, y, z);
            if (block == BlockType.Air) continue;

            for (int face = 0; face < 6; face++)
            {
                if (IsSolid(x + Normals[face, 0], y + Normals[face, 1], z + Normals[face, 2]))
                    continue;

                var tile = Blocks.GetTile(block, face);
                var (u0, v0, u1, v1) = TextureAtlas.TileUv(tile);
                bool flat = face is 2 or 3;
                var uvs = flat ? FlatUvs : SideUvs;
                // A side is half a block tall: it always shows the upper half of the tile,
                // which every tile draws as a complete half-block design (see TextureAtlas).
                float vScale = flat ? 1f : 0.5f;

                for (int c = 0; c < 4; c++)
                {
                    bool side1 = IsSolid(x + AoOffsets[face, c, 0, 0], y + AoOffsets[face, c, 0, 1], z + AoOffsets[face, c, 0, 2]);
                    bool side2 = IsSolid(x + AoOffsets[face, c, 1, 0], y + AoOffsets[face, c, 1, 1], z + AoOffsets[face, c, 1, 2]);
                    bool corner = IsSolid(x + AoOffsets[face, c, 2, 0], y + AoOffsets[face, c, 2, 1], z + AoOffsets[face, c, 2, 2]);
                    _ao[c] = side1 && side2 ? 0 : 3 - (side1 ? 1 : 0) - (side2 ? 1 : 0) - (corner ? 1 : 0);

                    // Smooth block light: average of the air cells around the vertex, in front of the face.
                    int r = 0, g = 0, b = 0, count = 0;
                    AddLight(x + Normals[face, 0], y + Normals[face, 1], z + Normals[face, 2], ref r, ref g, ref b, ref count);
                    if (!side1) AddLight(x + AoOffsets[face, c, 0, 0], y + AoOffsets[face, c, 0, 1], z + AoOffsets[face, c, 0, 2], ref r, ref g, ref b, ref count);
                    if (!side2) AddLight(x + AoOffsets[face, c, 1, 0], y + AoOffsets[face, c, 1, 1], z + AoOffsets[face, c, 1, 2], ref r, ref g, ref b, ref count);
                    if (!corner && !(side1 && side2)) AddLight(x + AoOffsets[face, c, 2, 0], y + AoOffsets[face, c, 2, 1], z + AoOffsets[face, c, 2, 2], ref r, ref g, ref b, ref count);
                    float scale = 1f / (count * BlockLight.MaxLevel);
                    _light[c] = (r * scale, g * scale, b * scale);
                }

                // Flip the quad's diagonal so the AO gradient interpolates evenly.
                var order = _ao[0] + _ao[2] < _ao[1] + _ao[3] ? FlippedOrder : DefaultOrder;

                foreach (int c in order)
                {
                    _vertices.Add(baseX + x + Corners[face, c, 0]);
                    _vertices.Add((y + Corners[face, c, 1]) * Chunk.CellHeight);
                    _vertices.Add(baseZ + z + Corners[face, c, 2]);
                    _vertices.Add(u0 + (u1 - u0) * uvs[c, 0]);
                    _vertices.Add(v0 + (v1 - v0) * uvs[c, 1] * vScale);
                    _vertices.Add(AoCurve[_ao[c]]);
                    _vertices.Add(_light[c].R);
                    _vertices.Add(_light[c].G);
                    _vertices.Add(_light[c].B);
                }
            }
        }

        return CollectionsMarshal.AsSpan(_vertices);
    }

    private void AddLight(int x, int y, int z, ref int r, ref int g, ref int b, ref int count)
    {
        ushort light = GetLight(x, y, z);
        r += BlockLight.R(light);
        g += BlockLight.G(light);
        b += BlockLight.B(light);
        count++;
    }

    /// <summary>Block light in centre-chunk local cell coordinates; x/z may be -1..16.</summary>
    private ushort GetLight(int x, int y, int z)
    {
        if ((uint)y >= Chunk.Height) return 0;
        int cx = x < 0 ? 0 : x >= Chunk.Size ? 2 : 1;
        int cz = z < 0 ? 0 : z >= Chunk.Size ? 2 : 1;
        var chunk = _chunks[cz * 3 + cx];
        return chunk?.GetLight(x - (cx - 1) * Chunk.Size, y, z - (cz - 1) * Chunk.Size) ?? 0;
    }

    /// <summary>Solid check in centre-chunk local cell coordinates; x/z may be -1..16.</summary>
    private bool IsSolid(int x, int y, int z)
    {
        if (y < 0) return true; // never draw the bottom of the world
        if (y >= Chunk.Height) return false;

        int cx = x < 0 ? 0 : x >= Chunk.Size ? 2 : 1;
        int cz = z < 0 ? 0 : z >= Chunk.Size ? 2 : 1;
        var chunk = _chunks[cz * 3 + cx];
        if (chunk is null) return false;

        return Blocks.IsSolid(chunk.Get(x - (cx - 1) * Chunk.Size, y, z - (cz - 1) * Chunk.Size));
    }

    private static int[,,,] BuildAoOffsets()
    {
        var result = new int[6, 4, 3, 3];
        for (int face = 0; face < 6; face++)
        {
            int normalAxis = Normals[face, 0] != 0 ? 0 : Normals[face, 1] != 0 ? 1 : 2;
            int t1 = (normalAxis + 1) % 3, t2 = (normalAxis + 2) % 3;

            for (int c = 0; c < 4; c++)
            {
                var side1 = new int[3];
                var side2 = new int[3];
                for (int a = 0; a < 3; a++) side1[a] = side2[a] = Normals[face, a];
                side1[t1] += Corners[face, c, t1] == 1 ? 1 : -1;
                side2[t2] += Corners[face, c, t2] == 1 ? 1 : -1;

                for (int a = 0; a < 3; a++)
                {
                    result[face, c, 0, a] = side1[a];
                    result[face, c, 1, a] = side2[a];
                    result[face, c, 2, a] = side1[a] + side2[a] - Normals[face, a];
                }
            }
        }
        return result;
    }
}
