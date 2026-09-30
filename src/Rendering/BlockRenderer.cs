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
/// 2×2 grid (spires are big: this keeps their meshes light); a block of a vein keeps the 4×4 grid,
/// half its squares nuggets (<see cref="Vein"/>: cold pale glass, or glowing violet crystal in violet-stained rock). Glass blocks go into a second mesh per
/// chunk, drawn in the see-through pass (<see cref="DrawGlass"/>, not into the shadow map): a clear
/// pane in each face within a slightly frosted, faintly glowing border, so a wall of glass reads as
/// panes; glass hides no face of the blocks behind it, and faces between glass blocks are left out.
/// </summary>
public sealed unsafe class BlockRenderer : IDisposable
{
    private const int FloatsPerVertex = 10;
    private const int Grid = 4;        // squares per face side
    private const int NaturalGrid = 2; // the same for the rock of the spires

    private readonly GL _gl;
    private readonly Dictionary<(int X, int Z), (uint Vao, uint Vbo, int Count)> _chunks = new();
    private readonly Dictionary<(int X, int Z), (uint Vao, uint Vbo, int Count)> _glassChunks = new();

    public BlockRenderer(GL gl) => _gl = gl;

    /// <summary>Remeshes the chunks that changed.</summary>
    public void Update(Blocks blocks)
    {
        foreach (var chunk in blocks.TakeDirtyChunks())
        {
            var m = new MeshBuilder();
            var glass = new MeshBuilder();
            foreach (var (p, resource) in blocks.InChunk(chunk))
            {
                bool isGlass = resource == Resource.Glass;
                if (isGlass) GlassBlock(glass, blocks, p);
                else Block(m, p, resource, blocks.IsNatural(p), blocks.VeinAt(p), (face, half) => !Covered(blocks, p, face, half, false));
            }
            Store(_chunks, chunk, m.Vertices);
            Store(_glassChunks, chunk, glass.Vertices);
        }
    }

    private void Store(Dictionary<(int X, int Z), (uint Vao, uint Vbo, int Count)> chunks, (int X, int Z) chunk, List<float> vertices)
    {
        if (!chunks.TryGetValue(chunk, out var buffers))
        {
            if (vertices.Count == 0) return;
            var (vao, vbo) = CreateBuffers();
            buffers = (vao, vbo, 0);
        }
        chunks[chunk] = buffers with { Count = Upload(buffers.Vbo, vertices) };
    }

    /// <summary>The glass blocks, for the see-through pass.</summary>
    public void DrawGlass()
    {
        foreach (var (vao, _, count) in _glassChunks.Values)
        {
            if (count == 0) continue;
            _gl.BindVertexArray(vao);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)count);
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

    // A face is hidden when a block sits right against it or when it is buried. A side face is
    // judged by halves (a block is two steps tall and its neighbours may sit a step higher or lower):
    // a half is hidden by a block in the next column spanning it, or by that column's ground reaching
    // its top; otherwise faces a step apart would lie in one plane and flicker (z-fighting, seen
    // through glass). The top and bottom (half ignored) only by the block right above or below, the
    // bottom also by the ground. Glass hides only glass faces (a solid block's face shows through it).
    private static bool Covered(Blocks blocks, BlockPos p, int face, int half, bool glass)
    {
        bool Hides(BlockPos n) => blocks.At(n) is { } r && (glass || r != Resource.Glass);
        if (face == 2) return Hides(p with { Y = p.Y + Blocks.Tall });
        if (face == 3) return Hides(p with { Y = p.Y - Blocks.Tall }) || blocks.GroundAt(p.X, p.Z) >= p.Bottom;
        var (dx, dz) = face switch { 0 => (1, 0), 1 => (-1, 0), 4 => (0, 1), _ => (0, -1) };
        int x = p.X + dx, z = p.Z + dz, y = p.Y + half; // the half spans steps y..y+1
        return Hides(new BlockPos(x, y, z)) || Hides(new BlockPos(x, y - 1, z))
            || blocks.GroundAt(x, z) >= (y + 1) * Blocks.Step - 0.001f;
    }

    // A face is shown if any part of it is.
    private static bool Covered(Blocks blocks, BlockPos p, int face, bool glass) =>
        Covered(blocks, p, face, 0, glass) && Covered(blocks, p, face, 1, glass);

    private static readonly Vector3 GlassTint = new(0.82f, 0.93f, 1.0f);
    private const float GlassBorder = 0.07f; // the frosted rim round each pane

    /// <summary>
    /// A glass block's visible faces: a clear pane within a frosted, faintly glowing rim. Where the
    /// face goes on flush into the same face of a neighbouring glass block, that side has no rim, so
    /// glass blocks joined together read as one piece of glass (breaking one brings the rims back:
    /// the chunks around a change are remeshed).
    /// </summary>
    private static void GlassBlock(MeshBuilder m, Blocks blocks, BlockPos p)
    {
        var center = p.Center;
        const float half = Blocks.Size / 2, b = GlassBorder, s = Blocks.Size, rim = 0.22f;
        // Each face as a 3×3 layout: the rim's strips and corners round the pane in the middle.
        // Rows split at mid-height too, so each half of a side face can be left out on its own.
        ReadOnlySpan<float> cuts = [0, b, s - b, s], rows = [0, b, s / 2, s - b, s];
        for (int f = 0; f < 6; f++)
        {
            var (n, u, v) = Faces[f];
            var corner = center + n * half - u * half - v * half;
            // Whether the face goes on into the neighbour at (du, dv) steps along u and v.
            bool Joins(int du, int dv)
            {
                var d = u * du + v * dv;
                var q = new BlockPos(p.X + (int)d.X, p.Y + (int)d.Y * Blocks.Tall, p.Z + (int)d.Z);
                return blocks.At(q) == Resource.Glass && !Covered(blocks, q, f, true);
            }
            for (int j = 0; j < 4; j++)
            for (int i = 0; i < 3; i++)
            {
                if (Covered(blocks, p, f, j / 2, true)) continue; // the lower or upper half (v is up on the sides)
                int du = i - 1, dv = j == 0 ? -1 : j == 3 ? 1 : 0;
                // The pane in the middle; a side strip is rim unless the face goes on that way; a
                // corner is rim unless the face goes on along both sides and across the corner.
                bool isRim = (du, dv) switch
                {
                    (0, 0) => false,
                    (0, _) => !Joins(0, dv),
                    (_, 0) => !Joins(du, 0),
                    _ => !(Joins(du, 0) && Joins(0, dv) && Joins(du, dv)),
                };
                var a = corner + u * cuts[i] + v * rows[j];
                var bb = corner + u * cuts[i + 1] + v * rows[j];
                var c = corner + u * cuts[i + 1] + v * rows[j + 1];
                var d = corner + u * cuts[i] + v * rows[j + 1];
                float glow = isRim ? rim : 0f;
                m.Triangle(a, bb, c, GlassTint, glow);
                m.Triangle(a, c, d, GlassTint, glow);
            }
        }
    }

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
    private static void Block(MeshBuilder m, BlockPos p, Resource resource, bool natural, Resource? vein, Func<int, int, bool> visible)
    {
        var center = p.Center;
        const float half = Blocks.Size / 2;
        int grid = natural && vein is null ? NaturalGrid : Grid; // veins in the fine grid: their nuggets are small
        float size = Blocks.Size / grid;
        // The spires' strata: bands a couple of metres tall, wavering around the spire.
        float strata = 0.5f + 0.5f * MathF.Sin(p.Bottom * 0.45f + 0.6f * MathF.Sin(p.X * 0.11f) + 0.6f * MathF.Sin(p.Z * 0.13f));
        for (int f = 0; f < 6; f++)
        {
            if (!visible(f, 0) && !visible(f, 1)) continue;
            var (n, u, v) = Faces[f];
            bool end = f is 2 or 3; // the top and bottom (the log's rings)
            var corner = center + n * half - u * half - v * half;
            for (int j = 0; j < grid; j++)
            for (int i = 0; i < grid; i++)
            {
                if (!visible(f, 2 * j / grid)) continue; // the lower or upper half (v is up on the sides)
                float h = Hash(p.X * 7 + f * 131 + i * 17, p.Y * 13 + j * 29, p.Z * 11 + i * j);
                var rock = Vector3.Lerp(RockDark, RockLight, 0.3f + 0.45f * strata) * (0.86f + 0.22f * h) * (f == 2 ? 1.08f : 1f);
                var (color, emissive) = !natural ? Shade(resource, end, i, j, h)
                    : vein is { } ore ? Vein(ore, rock, Hash(p.X * 5 + f * 97 + i * 31, p.Y * 19 + j * 43, p.Z * 3 + i * 7 + j), h)
                    : (rock, 0f);
                var a = corner + u * (i * size) + v * (j * size);
                var b = a + u * size;
                var c = b + v * size;
                var d = a + v * size;
                m.Triangle(a, b, c, color, emissive);
                m.Triangle(a, c, d, color, emissive);
            }
        }
    }

    // Veins, told apart at a glance: glass is cold, clear and lit only by the world (pale cyan
    // nuggets, some catching a white glint, in a paler, greyer rock); crystal glows (hot violet
    // nuggets shining in the dark, the rock around them stained violet and glowing faintly).
    private static readonly Vector3 GlassNugget = new(0.62f, 0.86f, 0.95f), GlassGlint = new(0.95f, 0.98f, 1.0f);
    private static readonly Vector3 CrystalNugget = new(0.80f, 0.36f, 1.0f), CrystalStain = new(0.42f, 0.20f, 0.55f);

    /// <summary>A little square of a vein block: a nugget of its mineral (<paramref name="pick"/> under its share) or the rock around it.</summary>
    private static (Vector3 Color, float Emissive) Vein(Resource ore, Vector3 rock, float pick, float h)
    {
        if (ore == Resource.Crystal)
            return pick < 0.55f ? (CrystalNugget * (0.85f + 0.3f * h), 1.3f) : (Vector3.Lerp(rock, CrystalStain, 0.6f), 0.12f);
        if (pick < 0.12f) return (GlassGlint, 0.05f);
        return pick < 0.5f ? (GlassNugget * (0.8f + 0.25f * h), 0f) : (Vector3.Lerp(rock, new Vector3(0.62f), 0.3f), 0f);
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
        foreach (var (vao, vbo, _) in _chunks.Values.Concat(_glassChunks.Values))
        {
            _gl.DeleteBuffer(vbo);
            _gl.DeleteVertexArray(vao);
        }
    }
}
