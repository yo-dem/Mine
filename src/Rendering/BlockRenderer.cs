using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Draws the blocks: one mesh per <see cref="BlockWorld"/> chunk in world space, in the object
/// vertex layout plus each vertex's place inside its 1 m block and the material's motif (drawn with
/// TerrainShaders.BlockVertex/BlockFragment), rebuilt when the
/// chunk's blocks change, nearest first and a few per frame. Only the faces between solid and
/// empty space are kept: a whole face where a full block meets nothing, faces of the fine
/// voxels elsewhere. Also draws an overlay (<see cref="DrawOverlay"/>): the wear of the block
/// being broken, the drops lying about, and the outline of the block aimed at or of where the
/// block in hand would go.
/// </summary>
public sealed unsafe class BlockRenderer : IDisposable
{
    private const int FloatsPerVertex = 10;      // the overlay: the object vertex layout
    private const int ChunkFloatsPerVertex = 14; // chunks: that, plus the position inside the block (3) and the motif (1)
    private const int RebuildsPerFrame = 6;
    private const float DrawDistance = 800f; // the mountains are seen from afar

    private readonly GL _gl;
    private readonly Dictionary<BlockWorld.ChunkKey, (uint Vao, uint Vbo, int Count)> _meshes = new();
    private readonly List<BlockWorld.ChunkKey> _order = new();
    private readonly uint _overlayVao, _overlayVbo;
    private int _overlayCount;

    public BlockRenderer(GL gl)
    {
        _gl = gl;
        (_overlayVao, _overlayVbo) = CreateBuffers();
    }

    /// <summary>Rebuilds the meshes of the changed chunks nearest <paramref name="eye"/>.</summary>
    public void Update(BlockWorld world, Vector3 eye)
    {
        if (world.Dirty.Count == 0) return;
        _order.Clear();
        _order.AddRange(world.Dirty);
        _order.Sort((a, b) => Distance(a, eye).CompareTo(Distance(b, eye)));
        foreach (var key in _order.Take(RebuildsPerFrame))
        {
            world.Dirty.Remove(key);
            var vertices = world.Chunk(key) is { } chunk ? Build(world, chunk) : null;
            if (vertices is null || vertices.Count == 0)
            {
                if (_meshes.Remove(key, out var old)) Delete(old);
                continue;
            }
            if (!_meshes.TryGetValue(key, out var mesh))
            {
                var (vao, vbo) = CreateBuffers(ChunkFloatsPerVertex);
                mesh = (vao, vbo, 0);
            }
            _meshes[key] = (mesh.Vao, mesh.Vbo, Upload(mesh.Vbo, vertices, ChunkFloatsPerVertex));
        }
    }

    private static float Distance(BlockWorld.ChunkKey key, Vector3 eye)
    {
        const float size = BlockWorld.ChunkCells * BlockWorld.CellSize;
        return Vector3.DistanceSquared(new Vector3(key.X + 0.5f, key.Y + 0.5f, key.Z + 0.5f) * size, eye);
    }

    /// <summary>Draws the chunks near <paramref name="center"/> (the block shader, or the shadow pass with an identity model, must be bound).</summary>
    public void Draw(Vector3 center, float radius = DrawDistance)
    {
        const float size = BlockWorld.ChunkCells * BlockWorld.CellSize;
        foreach (var (key, mesh) in _meshes)
        {
            if (Vector3.DistanceSquared(new Vector3(key.X + 0.5f, key.Y + 0.5f, key.Z + 0.5f) * size, center) > radius * radius) continue;
            _gl.BindVertexArray(mesh.Vao);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)mesh.Count);
        }
    }

    /// <summary>The colour a block is drawn in: its material's, one shade per 1 m block.</summary>
    public static Vector3 Shade(Piece piece, Cell cell)
    {
        var anchor = piece.Anchor(cell);
        return Materials.Color(piece.Material) * (0.93f + 0.14f * Hash01(anchor.X, anchor.Y, anchor.Z));
    }

    private static readonly (int X, int Y, int Z)[] Directions = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

    private static List<float> Build(BlockWorld world, IReadOnlyDictionary<Cell, Piece> chunk)
    {
        var m = new MeshBuilder();
        var output = new List<float>();
        const float fine = BlockWorld.FineSize;
        foreach (var (cell, piece) in chunk)
        {
            var min = cell.Min;
            var color = Shade(piece, cell);
            var blockMin = piece.Anchor(cell).Min;
            float pattern = Materials.Pattern(piece.Material);
            float glow = Materials.Emissive(piece.Material);
            m.Vertices.Clear();
            bool full = piece.Profile == Profiles.Full;
            for (int face = 0; face < 6; face++)
            {
                var d = Directions[face];
                var next = cell.Offset(d.X, d.Y, d.Z);
                bool neighbour = world.TryGet(next, out var other);
                if (full && neighbour && other.Profile == Profiles.Full) continue;
                if (full && !neighbour)
                {
                    m.BoxFace(min, min + new Vector3(BlockWorld.CellSize), face, color, glow);
                    continue;
                }
                // Mixed shapes: the faces of the fine voxels that border empty space.
                int fx0 = cell.X * BlockWorld.Fine, fy0 = cell.Y * BlockWorld.Fine, fz0 = cell.Z * BlockWorld.Fine;
                for (int sy = 0; sy < BlockWorld.Fine; sy++)
                for (int sz = 0; sz < BlockWorld.Fine; sz++)
                for (int sx = 0; sx < BlockWorld.Fine; sx++)
                {
                    if (!BlockWorld.Solid(piece, sx, sy, sz)) continue;
                    int nx = sx + d.X, ny = sy + d.Y, nz = sz + d.Z;
                    bool inside = nx is >= 0 and < BlockWorld.Fine && ny is >= 0 and < BlockWorld.Fine && nz is >= 0 and < BlockWorld.Fine;
                    if (inside ? BlockWorld.Solid(piece, nx, ny, nz) : world.Solid(fx0 + nx, fy0 + ny, fz0 + nz)) continue;
                    var p = min + new Vector3(sx, sy, sz) * fine;
                    m.BoxFace(p, p + new Vector3(fine), face, color * (0.97f + 0.06f * Hash01(fx0 + sx, fy0 + sy, fz0 + sz)));
                }
            }
            // Each vertex also carries where it lies inside its 1 m block, and the motif to carve.
            var v = m.Vertices;
            for (int i = 0; i < v.Count; i += FloatsPerVertex)
            {
                for (int k = 0; k < FloatsPerVertex; k++) output.Add(v[i + k]);
                var local = (new Vector3(v[i], v[i + 1], v[i + 2]) - blockMin) / BlockWorld.BlockSize;
                output.Add(local.X); output.Add(local.Y); output.Add(local.Z);
                output.Add(pattern);
            }
        }
        return output;
    }

    /// <summary>
    /// Rebuilds and draws the overlay with the bound object shader (identity model): the wear of
    /// the block being broken, the drops and flakes lying about, and an outline around
    /// <paramref name="outline"/> when set.
    /// </summary>
    public void DrawOverlay(Vector3 eye, IReadOnlyList<Drops.Drop> drops, (Cell Anchor, Vector3 Stone, float Progress)? breaking,
                            (Vector3 Min, Vector3 Max, Vector3 Color)? outline)
    {
        var m = new MeshBuilder();
        if (breaking is { } damage) AddWear(m, damage.Anchor, damage.Stone, damage.Progress);

        foreach (var drop in drops)
        {
            if (Vector3.DistanceSquared(drop.Position, eye) > 80f * 80f) continue;
            if (drop.Flake)
            {
                var tumble = Matrix4x4.CreateRotationY(drop.Spin) * Matrix4x4.CreateRotationX(drop.Spin * 0.7f) * Matrix4x4.CreateTranslation(drop.Position);
                AddCube(m, drop.Size, Materials.Color(drop.Material) * 0.95f, 0f, tumble);
                continue;
            }
            // A small copy of the block, turning slowly upright, glowing faintly so it is found again.
            var color = drop.Item.Kind == ItemKind.Block ? Materials.Color(drop.Item.Material) : new Vector3(0.66f, 0.46f, 1f);
            var turn = Matrix4x4.CreateRotationY(drop.Spin) * Matrix4x4.CreateTranslation(Drops.DrawPosition(drop));
            AddCube(m, Drops.DropSize, color, drop.Pulled ? 0.5f : 0.25f, turn);
        }

        if (outline is { } o2)
        {
            var (min, max, color) = o2;
            min -= new Vector3(0.004f);
            max += new Vector3(0.004f);
            const float t = 0.007f;
            for (int axis = 0; axis < 3; axis++)
            for (int a = 0; a < 2; a++)
            for (int b = 0; b < 2; b++)
            {
                // An edge along `axis`, at corner (a, b) of the other two axes.
                Vector3 lo = min, hi = max;
                int u = (axis + 1) % 3, v = (axis + 2) % 3;
                float cu = a == 0 ? Get(min, u) : Get(max, u), cv = b == 0 ? Get(min, v) : Get(max, v);
                lo = Set(Set(lo, u, cu - t), v, cv - t);
                hi = Set(Set(hi, u, cu + t), v, cv + t);
                m.Box(lo, hi, color, 1f);
            }
        }

        _overlayCount = Upload(_overlayVbo, m.Vertices);
        if (_overlayCount == 0) return;
        _gl.BindVertexArray(_overlayVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_overlayCount);
    }

    private static readonly Vector3[] FaceNormals = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];

    /// <summary>
    /// The block at <paramref name="anchor"/> being ruined as it is broken, after Minecraft's
    /// breaking marks: on a 16 × 16 grid of pixels per face, short strokes of dark pixels (mostly
    /// diagonal) each ringed by paler pixels, as if the surface were chipped away. The first come
    /// at once; more and more follow, until the faces are covered in them.
    /// Fixed per block and face.
    /// </summary>
    private static void AddWear(MeshBuilder m, Cell anchor, Vector3 stone, float progress)
    {
        const int grid = 16, strokes = 44;
        const float size = BlockWorld.BlockSize, pixel = size / grid;
        var center = anchor.Min + new Vector3(size / 2);
        var dark = stone * 0.42f;
        var pale = Vector3.Min(stone * 1.25f, new Vector3(1f));
        var marks = new byte[grid, grid]; // 0 untouched, 1 pale rim, 2 dark
        ReadOnlySpan<(int X, int Y)> directions = [(1, 1), (1, -1), (-1, 1), (-1, -1), (1, 1), (1, -1), (1, 0), (0, 1)]; // diagonals twice as likely
        for (int face = 0; face < 6; face++)
        {
            var n = FaceNormals[face];
            var t1 = MathF.Abs(n.Y) > 0.5f ? Vector3.UnitX : Vector3.UnitY;
            var t2 = Vector3.Cross(n, t1);
            var corner = center + n * (size / 2) - (t1 + t2) * (size / 2);
            Array.Clear(marks);
            var random = new Random(unchecked(anchor.X * 73856093 ^ anchor.Y * 19349663 ^ anchor.Z * 83492791 ^ face * 2654435));
            for (int k = 0; k < strokes; k++)
            {
                // Each stroke has its moment, from a quarter of the way on; all are drawn from the
                // random stream so the pattern stays the same whatever the progress.
                float appear = 0.9f * (k + random.NextSingle()) / strokes;
                int x = random.Next(grid), y = random.Next(grid), length = 2 + random.Next(4);
                var (dx, dy) = directions[random.Next(directions.Length)];
                if (progress < appear) continue;
                for (int i = 0; i < length; i++, x += dx, y += dy)
                {
                    if (x < 0 || y < 0 || x >= grid || y >= grid) break;
                    marks[x, y] = 2;
                }
            }
            // Pale rims round the dark pixels.
            for (int x = 0; x < grid; x++)
            for (int y = 0; y < grid; y++)
            {
                if (marks[x, y] != 2) continue;
                foreach (var (dx, dy) in (ReadOnlySpan<(int, int)>)[(1, 0), (-1, 0), (0, 1), (0, -1)])
                {
                    int u = x + dx, v = y + dy;
                    if (u >= 0 && v >= 0 && u < grid && v < grid && marks[u, v] == 0) marks[u, v] = 1;
                }
            }
            for (int x = 0; x < grid; x++)
            for (int y = 0; y < grid; y++)
            {
                if (marks[x, y] == 0) continue;
                bool isDark = marks[x, y] == 2;
                var lift = n * (isDark ? 0.004f : 0.003f);
                var a = corner + lift + t1 * (x * pixel) + t2 * (y * pixel);
                // A touch of variation between pixels, as in a pixel texture.
                float shade = 0.94f + 0.12f * ((x * 7 + y * 13 + face * 5) % 5) / 4f;
                m.Quad(a, a + t1 * pixel, a + t1 * pixel + t2 * pixel, a + t2 * pixel, (isDark ? dark : pale) * shade, 0f);
            }
        }
    }

    private static void AddCube(MeshBuilder m, float size, Vector3 color, float emissive, Matrix4x4 transform)
    {
        var local = new MeshBuilder();
        local.Box(new Vector3(-size / 2), new Vector3(size / 2), color, emissive);
        Append(m, local, transform);
    }

    private static void Append(MeshBuilder m, MeshBuilder local, Matrix4x4 transform)
    {
        var v = local.Vertices;
        for (int i = 0; i < v.Count; i += FloatsPerVertex)
        {
            var p = Vector3.Transform(new Vector3(v[i], v[i + 1], v[i + 2]), transform);
            var n = Vector3.Normalize(Vector3.TransformNormal(new Vector3(v[i + 3], v[i + 4], v[i + 5]), transform));
            m.Vertex(p, n, new Vector3(v[i + 6], v[i + 7], v[i + 8]), v[i + 9]);
        }
    }

    private static float Get(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
    private static Vector3 Set(Vector3 v, int axis, float value) => axis switch
    {
        0 => v with { X = value },
        1 => v with { Y = value },
        _ => v with { Z = value },
    };

    private static float Hash01(int x, int y, int z)
    {
        unchecked
        {
            uint h = (uint)(x * 73856093 ^ y * 19349663 ^ z * 83492791);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }
    }

    private (uint Vao, uint Vbo) CreateBuffers(int floatsPerVertex = FloatsPerVertex)
    {
        uint vao = _gl.GenVertexArray(), vbo = _gl.GenBuffer();
        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        uint stride = (uint)(floatsPerVertex * sizeof(float));
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)(9 * sizeof(float)));
        _gl.EnableVertexAttribArray(3);
        if (floatsPerVertex == ChunkFloatsPerVertex)
        {
            _gl.VertexAttribPointer(4, 3, VertexAttribPointerType.Float, false, stride, (void*)(10 * sizeof(float)));
            _gl.EnableVertexAttribArray(4);
            _gl.VertexAttribPointer(5, 1, VertexAttribPointerType.Float, false, stride, (void*)(13 * sizeof(float)));
            _gl.EnableVertexAttribArray(5);
        }
        _gl.BindVertexArray(0);
        return (vao, vbo);
    }

    private int Upload(uint vbo, List<float> vertices, int floatsPerVertex = FloatsPerVertex)
    {
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        var data = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices);
        fixed (float* p = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);
        return data.Length / floatsPerVertex;
    }

    private void Delete((uint Vao, uint Vbo, int Count) mesh)
    {
        _gl.DeleteBuffer(mesh.Vbo);
        _gl.DeleteVertexArray(mesh.Vao);
    }

    public void Dispose()
    {
        foreach (var mesh in _meshes.Values) Delete(mesh);
        _meshes.Clear();
        _gl.DeleteBuffer(_overlayVbo);
        _gl.DeleteVertexArray(_overlayVao);
    }
}
