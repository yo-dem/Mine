using System.Collections.Concurrent;
using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Draws the layered <see cref="TerrainField"/> in square chunks. Each chunk is a grid of flat
/// blocks (the terrain tiles near the camera, merged 2x2, 4x4... farther away: level of detail),
/// with vertical walls wherever two neighbouring blocks sit at different layers. Chunk meshes are
/// built on background threads and uploaded on the main thread; a chunk keeps its old mesh until
/// the new one arrives, so nothing pops out. Walls on the chunk borders reach deeper ("skirts")
/// to hide the cracks against chunks at another level of detail.
/// Vertex layout: position (3), normal (3), slope (1: normal.y of the smooth land, which picks
/// the material), ambient occlusion (1: darker at the foot of the walls).
/// </summary>
public sealed unsafe class TerrainRenderer : IDisposable
{
    public const float TileSize = 64f;
    public const float ViewDistance = 1300f;
    private const int FloatsPerVertex = 8;
    private const int UploadsPerFrame = 12;

    // Block size (in terrain tiles) per detail level, and how far from the camera each level is used.
    private static readonly int[] LodSteps = [1, 2, 4, 8, 16];
    private static readonly float[] LodRanges = [110f, 220f, 420f, 800f, float.MaxValue];
    // The rock spires are not part of the terrain at any distance: near they are blocks laid by
    // Blocks, farther BlockRenderer draws the same blocks as plain far meshes.

    private sealed class Tile
    {
        public uint Vao, Vbo;
        public int Lod = -1;       // detail level of the mesh currently uploaded
        public int VertexCount;
        public int WantedLod = -1; // detail level last requested
    }

    private readonly GL _gl;
    private readonly TerrainField _field;
    private readonly Dictionary<(int X, int Z), Tile> _tiles = new();
    private readonly HashSet<(int X, int Z)> _wanted = new();
    private readonly List<(int X, int Z)> _toRemove = new();

    private readonly BlockingCollection<(int X, int Z, int Lod)> _requests = new();
    private readonly ConcurrentQueue<(int X, int Z, int Lod, float[] Vertices)> _results = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly Thread[] _workers;

    public TerrainRenderer(GL gl, TerrainField field)
    {
        _gl = gl;
        _field = field;
        _workers = new Thread[Math.Clamp(Environment.ProcessorCount - 1, 1, 4)];
        for (int i = 0; i < _workers.Length; i++)
        {
            _workers[i] = new Thread(BuildLoop) { IsBackground = true, Name = $"Terrain {i}" };
            _workers[i].Start();
        }
    }

    /// <summary>Requests the tiles and detail levels needed around the camera and uploads finished meshes.</summary>
    public void Update(Vector3 camera)
    {
        int radius = (int)MathF.Ceiling(ViewDistance / TileSize);
        int ctx = (int)MathF.Floor(camera.X / TileSize), ctz = (int)MathF.Floor(camera.Z / TileSize);

        _wanted.Clear();
        var requests = new List<(int X, int Z, int Lod, float Dist)>();
        for (int tz = ctz - radius; tz <= ctz + radius; tz++)
        for (int tx = ctx - radius; tx <= ctx + radius; tx++)
        {
            float dist = DistanceToTile(camera, tx, tz);
            if (dist > ViewDistance) continue;
            _wanted.Add((tx, tz));

            int lod = 0;
            while (dist > LodRanges[lod]) lod++;
            if (!_tiles.TryGetValue((tx, tz), out var tile))
                _tiles[(tx, tz)] = tile = new Tile();
            if (tile.WantedLod != lod)
            {
                tile.WantedLod = lod;
                requests.Add((tx, tz, lod, dist));
            }
        }
        // Nearest first, so the ground around the player appears before the horizon.
        foreach (var r in requests.OrderBy(r => r.Dist))
            _requests.Add((r.X, r.Z, r.Lod));

        _toRemove.Clear();
        foreach (var key in _tiles.Keys)
            if (!_wanted.Contains(key)) _toRemove.Add(key);
        foreach (var key in _toRemove)
        {
            DeleteMesh(_tiles[key]);
            _tiles.Remove(key);
        }

        for (int i = 0; i < UploadsPerFrame && _results.TryDequeue(out var result); i++)
        {
            // Results for tiles that left the view, or whose wanted detail changed meanwhile, are stale.
            if (!_tiles.TryGetValue((result.X, result.Z), out var tile) || tile.WantedLod != result.Lod)
                continue;
            Upload(tile, result.Lod, result.Vertices);
        }
    }

    /// <summary>Draws the visible tiles, skipping those entirely behind the camera.</summary>
    public void Draw(Vector3 camera, Vector3 forward)
    {
        var flatForward = new Vector2(forward.X, forward.Z);
        if (flatForward.LengthSquared() > 1e-4f) flatForward = Vector2.Normalize(flatForward);
        foreach (var (key, tile) in _tiles)
        {
            var center = new Vector2((key.X + 0.5f) * TileSize - camera.X, (key.Z + 0.5f) * TileSize - camera.Z);
            // A tile is behind when its centre is over a tile's diagonal behind the camera plane.
            if (Vector2.Dot(center, flatForward) < -TileSize * 0.75f && MathF.Abs(forward.Y) < 0.9f) continue;
            DrawTile(tile);
        }
    }

    /// <summary>Draws only the tiles within <paramref name="radius"/> of a point (for the shadow map).</summary>
    public void DrawNear(Vector3 center, float radius)
    {
        foreach (var (key, tile) in _tiles)
            if (DistanceToTile(center, key.X, key.Z) <= radius)
                DrawTile(tile);
    }

    private void DrawTile(Tile tile)
    {
        if (tile.Lod < 0 || tile.VertexCount == 0) return;
        _gl.BindVertexArray(tile.Vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)tile.VertexCount);
    }

    /// <summary>Horizontal distance from a point to the nearest point of a tile.</summary>
    private static float DistanceToTile(Vector3 p, int tx, int tz)
    {
        float dx = MathF.Max(MathF.Max(tx * TileSize - p.X, p.X - (tx + 1) * TileSize), 0);
        float dz = MathF.Max(MathF.Max(tz * TileSize - p.Z, p.Z - (tz + 1) * TileSize), 0);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private void BuildLoop()
    {
        try
        {
            foreach (var (x, z, lod) in _requests.GetConsumingEnumerable(_cancel.Token))
                _results.Enqueue((x, z, lod, BuildVertices(x, z, lod)));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Flat block tops plus the walls between blocks at different heights. Block heights (and the
    /// smooth slope used for the material) are sampled with one extra ring, for the border walls.
    /// </summary>
    private float[] BuildVertices(int tx, int tz, int lod)
    {
        float size = TerrainField.TileSize * LodSteps[lod];
        int n = (int)(TileSize / size);
        float x0 = tx * TileSize, z0 = tz * TileSize;

        var smooth = new float[(n + 2) * (n + 2)];
        for (int j = -1; j <= n; j++)
        for (int i = -1; i <= n; i++)
        {
            float x = x0 + (i + 0.5f) * size, z = z0 + (j + 0.5f) * size;
            // The rock spires are blocks near the player (Blocks.SpireRadius); far away the terrain draws them.
            smooth[(j + 1) * (n + 2) + (i + 1)] = _field.SmoothHeight(x, z);
        }
        float S(int i, int j) => smooth[(j + 1) * (n + 2) + (i + 1)];
        float H(int i, int j) => TerrainField.Layer(S(i, j));

        var v = new List<float>(n * n * 6 * FloatsPerVertex * 2);
        void Vertex(Vector3 p, Vector3 normal, float slope, float ao)
        {
            v.Add(p.X); v.Add(p.Y); v.Add(p.Z);
            v.Add(normal.X); v.Add(normal.Y); v.Add(normal.Z);
            v.Add(slope); v.Add(ao);
        }
        // A quad a-b-c-d (in order around it), wound counter-clockwise seen from the normal's side.
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, float slope, float aoA, float aoB, float aoC, float aoD)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0)
            {
                (b, d) = (d, b);
                (aoB, aoD) = (aoD, aoB);
            }
            Vertex(a, normal, slope, aoA); Vertex(b, normal, slope, aoB); Vertex(c, normal, slope, aoC);
            Vertex(a, normal, slope, aoA); Vertex(c, normal, slope, aoC); Vertex(d, normal, slope, aoD);
        }
        // A vertical wall along one block edge, from `top` down to `bottom`, facing `normal`.
        void Wall(Vector3 edgeA, Vector3 edgeB, float top, float bottom, Vector3 normal)
        {
            const float footShade = 0.55f;
            Quad(edgeA with { Y = top }, edgeB with { Y = top }, edgeB with { Y = bottom }, edgeA with { Y = bottom },
                normal, 0f, 1f, 1f, footShade, footShade);
        }

        float skirt = size * 1.5f + 1f; // deep enough to cover a neighbour chunk at another level of detail
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            float h = H(i, j);
            float xa = x0 + i * size, xb = xa + size, za = z0 + j * size, zb = za + size;
            float gx = (S(i + 1, j) - S(i - 1, j)) / (2 * size), gz = (S(i, j + 1) - S(i, j - 1)) / (2 * size);
            float slope = 1f / MathF.Sqrt(1 + gx * gx + gz * gz);

            Quad(new(xa, h, za), new(xb, h, za), new(xb, h, zb), new(xa, h, zb), Vector3.UnitY, slope, 1, 1, 1, 1);

            // Walls toward +X and +Z neighbours, on the side of the lower block; on the chunk border
            // (all four sides) always, reaching down past the neighbour as a skirt.
            float hx = H(i + 1, j), hz = H(i, j + 1);
            if (i == n - 1) Wall(new(xb, 0, za), new(xb, 0, zb), h, MathF.Min(h, hx) - skirt, Vector3.UnitX);
            else if (h > hx) Wall(new(xb, 0, za), new(xb, 0, zb), h, hx, Vector3.UnitX);
            else if (hx > h) Wall(new(xb, 0, za), new(xb, 0, zb), hx, h, -Vector3.UnitX);

            if (j == n - 1) Wall(new(xa, 0, zb), new(xb, 0, zb), h, MathF.Min(h, hz) - skirt, Vector3.UnitZ);
            else if (h > hz) Wall(new(xa, 0, zb), new(xb, 0, zb), h, hz, Vector3.UnitZ);
            else if (hz > h) Wall(new(xa, 0, zb), new(xb, 0, zb), hz, h, -Vector3.UnitZ);

            if (i == 0) Wall(new(xa, 0, za), new(xa, 0, zb), h, MathF.Min(h, H(i - 1, j)) - skirt, -Vector3.UnitX);
            if (j == 0) Wall(new(xa, 0, za), new(xb, 0, za), h, MathF.Min(h, H(i, j - 1)) - skirt, -Vector3.UnitZ);
        }
        return v.ToArray();
    }

    private void Upload(Tile tile, int lod, float[] vertices)
    {
        if (tile.Vao == 0)
        {
            tile.Vao = _gl.GenVertexArray();
            tile.Vbo = _gl.GenBuffer();
            _gl.BindVertexArray(tile.Vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, tile.Vbo);
            uint stride = FloatsPerVertex * sizeof(float);
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
            _gl.EnableVertexAttribArray(2);
            _gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)(7 * sizeof(float)));
            _gl.EnableVertexAttribArray(3);
        }

        _gl.BindVertexArray(tile.Vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, tile.Vbo);
        fixed (float* data = vertices)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), data, BufferUsageARB.StaticDraw);
        _gl.BindVertexArray(0);
        tile.Lod = lod;
        tile.VertexCount = vertices.Length / FloatsPerVertex;
    }

    private void DeleteMesh(Tile tile)
    {
        if (tile.Vao == 0) return;
        _gl.DeleteBuffer(tile.Vbo);
        _gl.DeleteVertexArray(tile.Vao);
    }

    public void Dispose()
    {
        _cancel.Cancel();
        _requests.CompleteAdding();
        foreach (var tile in _tiles.Values) DeleteMesh(tile);
        _tiles.Clear();
    }
}
