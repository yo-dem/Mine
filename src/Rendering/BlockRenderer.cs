using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Draws the placed blocks with the object shader (identity model, world-space vertices, into the
/// shadow map too): one mesh per chunk of <see cref="Blocks.ChunkSize"/>² columns, rebuilt only when
/// a block in or next to it changes, with only the faces not covered by a neighbour. Each face is a
/// 4×4 grid of little squares shaded like the material cubes (<see cref="ItemMeshes"/>): bark with
/// rings on the ends for wood, speckled lilac grey for stone, glowing violet glass for crystal.
/// The natural stone of the rock spires is the terrain's sandstone with its strata, in a coarser
/// 2×2 grid (spires are big: this keeps their meshes light).
/// </summary>
public sealed unsafe class BlockRenderer : IDisposable
{
    private const int FloatsPerVertex = 10;
    private const int Grid = 4;        // squares per face side
    private const int NaturalGrid = 2; // the same for the rock of the spires

    private readonly GL _gl;
    private readonly Dictionary<(int X, int Z), (uint Vao, uint Vbo, int Count)> _chunks = new();

    public BlockRenderer(GL gl) => _gl = gl;

    /// <summary>Remeshes the chunks that changed.</summary>
    public void Update(Blocks blocks)
    {
        foreach (var chunk in blocks.TakeDirtyChunks())
        {
            var m = new MeshBuilder();
            foreach (var (p, resource) in blocks.InChunk(chunk))
                Block(m, p, resource, blocks.IsNatural(p), face => !Covered(blocks, p, face));
            if (!_chunks.TryGetValue(chunk, out var buffers))
            {
                if (m.Vertices.Count == 0) continue;
                var (vao, vbo) = CreateBuffers();
                buffers = (vao, vbo, 0);
            }
            _chunks[chunk] = buffers with { Count = Upload(buffers.Vbo, m.Vertices) };
        }
    }

    public void Draw()
    {
        foreach (var (vao, _, count) in _chunks.Values)
        {
            if (count == 0) continue;
            _gl.BindVertexArray(vao);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)count);
        }
    }

    // A face is hidden when a block sits right against it (exactly level with it, for the sides),
    // or when it is buried: a bottom at or under the ground, a side under the neighbouring ground.
    private static bool Covered(Blocks blocks, BlockPos p, int face) => face switch
    {
        0 => blocks.At(p with { X = p.X + 1 }) is not null || blocks.GroundAt(p.X + 1, p.Z) >= p.Top,
        1 => blocks.At(p with { X = p.X - 1 }) is not null || blocks.GroundAt(p.X - 1, p.Z) >= p.Top,
        2 => blocks.At(p with { Y = p.Y + Blocks.Tall }) is not null,
        3 => blocks.At(p with { Y = p.Y - Blocks.Tall }) is not null || blocks.GroundAt(p.X, p.Z) >= p.Bottom,
        4 => blocks.At(p with { Z = p.Z + 1 }) is not null || blocks.GroundAt(p.X, p.Z + 1) >= p.Top,
        _ => blocks.At(p with { Z = p.Z - 1 }) is not null || blocks.GroundAt(p.X, p.Z - 1) >= p.Top,
    };

    // The six faces as (normal, u axis, v axis): +x, -x, +y, -y, +z, -z.
    private static readonly (Vector3 N, Vector3 U, Vector3 V)[] Faces =
    [
        (Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY), (-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
        (Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ), (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
        (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY), (-Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY),
    ];

    private static readonly Vector3 Bark = new(0.34f, 0.23f, 0.21f);
    private static readonly Vector3 WoodLight = new(0.70f, 0.52f, 0.38f), WoodDark = new(0.56f, 0.40f, 0.30f);
    private static readonly Vector3 Stone = new(0.54f, 0.50f, 0.58f);
    // The terrain's rock (TerrainShaders: `rock`), dark and light strata.
    private static readonly Vector3 RockDark = new(0.30f, 0.23f, 0.36f), RockLight = new(0.46f, 0.35f, 0.50f);

    /// <summary>A block's visible faces, each a grid of little shaded squares.</summary>
    private static void Block(MeshBuilder m, BlockPos p, Resource resource, bool natural, Func<int, bool> visible)
    {
        var center = p.Center;
        const float half = Blocks.Size / 2;
        int grid = natural ? NaturalGrid : Grid;
        float size = Blocks.Size / grid;
        // The spires' strata: bands a couple of metres tall, wavering around the spire.
        float strata = 0.5f + 0.5f * MathF.Sin(p.Bottom * 0.45f + 0.6f * MathF.Sin(p.X * 0.11f) + 0.6f * MathF.Sin(p.Z * 0.13f));
        for (int f = 0; f < 6; f++)
        {
            if (!visible(f)) continue;
            var (n, u, v) = Faces[f];
            bool end = f is 2 or 3; // the top and bottom (the log's rings)
            var corner = center + n * half - u * half - v * half;
            for (int j = 0; j < grid; j++)
            for (int i = 0; i < grid; i++)
            {
                float h = Hash(p.X * 7 + f * 131 + i * 17, p.Y * 13 + j * 29, p.Z * 11 + i * j);
                var (color, emissive) = natural
                    ? (Vector3.Lerp(RockDark, RockLight, 0.3f + 0.45f * strata) * (0.86f + 0.22f * h) * (f == 2 ? 1.08f : 1f), 0f)
                    : Shade(resource, end, i, j, h);
                var a = corner + u * (i * size) + v * (j * size);
                var b = a + u * size;
                var c = b + v * size;
                var d = a + v * size;
                m.Triangle(a, b, c, color, emissive);
                m.Triangle(a, c, d, color, emissive);
            }
        }
    }

    private static (Vector3 Color, float Emissive) Shade(Resource resource, bool end, int i, int j, float h)
    {
        switch (resource)
        {
            case Resource.Wood:
            {
                // Rings on the ends (the outer ring is bark), bark with vertical grain on the sides.
                int ring = Math.Max(Math.Abs(2 * i - 3), Math.Abs(2 * j - 3)); // 1 in the middle, 3 at the rim
                if (end && ring < 3) return ((ring == 1 ? WoodLight : WoodDark) * (0.93f + 0.12f * h), 0f);
                return (Bark * (0.8f + 0.35f * h) * (i % 2 == 0 ? 1f : 0.88f), 0f);
            }
            case Resource.Stone:
                return (Stone * (h < 0.12f ? 0.7f : 0.82f + 0.3f * h), 0f);
            default:
            {
                // Violet glass, brighter toward the middle of each face.
                int rim = Math.Max(Math.Abs(2 * i - 3), Math.Abs(2 * j - 3));
                return (Blocks.CrystalLight * (rim == 3 ? 0.75f : 1f) * (0.9f + 0.2f * h), rim == 3 ? 0.55f : 0.8f);
            }
        }
    }

    private static float Hash(int x, int y, int z)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }
    }

    private (uint, uint) CreateBuffers()
    {
        uint vao = _gl.GenVertexArray(), vbo = _gl.GenBuffer();
        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        uint stride = FloatsPerVertex * sizeof(float);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)(9 * sizeof(float)));
        _gl.EnableVertexAttribArray(3);
        _gl.BindVertexArray(0);
        return (vao, vbo);
    }

    private int Upload(uint vbo, List<float> vertices)
    {
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        var data = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices);
        fixed (float* p = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);
        return data.Length / FloatsPerVertex;
    }

    public void Dispose()
    {
        foreach (var (vao, vbo, _) in _chunks.Values)
        {
            _gl.DeleteBuffer(vbo);
            _gl.DeleteVertexArray(vao);
        }
    }
}
