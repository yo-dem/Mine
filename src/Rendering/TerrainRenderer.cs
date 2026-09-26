using System.Collections.Concurrent;
using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Draws the <see cref="TerrainField"/> as square tiles whose vertex spacing grows with
/// distance (level of detail). Tile meshes are built on background threads and uploaded
/// on the main thread; a tile keeps its old mesh until the new one arrives, so nothing
/// pops out. Vertical "skirts" around every tile hide the cracks between detail levels.
/// Vertex layout: position (3), normal (3).
/// </summary>
public sealed unsafe class TerrainRenderer : IDisposable
{
    public const float TileSize = 64f;
    public const float ViewDistance = 1300f;
    private const int FloatsPerVertex = 6;
    private const int UploadsPerFrame = 12;

    // Vertex spacing per detail level, and how far from the camera each level is used.
    private static readonly int[] LodSteps = [1, 2, 4, 8, 16];
    private static readonly float[] LodRanges = [110f, 220f, 420f, 800f, float.MaxValue];

    private sealed class Tile
    {
        public uint Vao, Vbo;
        public int Lod = -1;       // detail level of the mesh currently uploaded
        public int WantedLod = -1; // detail level last requested
    }

    private readonly GL _gl;
    private readonly TerrainField _field;
    private readonly uint[] _indexBuffers = new uint[LodSteps.Length];
    private readonly int[] _indexCounts = new int[LodSteps.Length];
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
        // Element buffers are VAO state in the core profile: bind a scratch VAO while creating them.
        uint scratch = gl.GenVertexArray();
        gl.BindVertexArray(scratch);
        for (int lod = 0; lod < LodSteps.Length; lod++)
            _indexBuffers[lod] = CreateIndexBuffer(GridSize(lod), out _indexCounts[lod]);
        gl.BindVertexArray(0);
        gl.DeleteVertexArray(scratch);

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
        if (tile.Lod < 0) return;
        _gl.BindVertexArray(tile.Vao);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)_indexCounts[tile.Lod], DrawElementsType.UnsignedShort, (void*)0);
    }

    private static int GridSize(int lod) => (int)TileSize / LodSteps[lod] + 1;

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
    /// Grid vertices followed by the skirt vertices (a copy of the border, pushed down).
    /// Normals come from central differences over the grid, so one extra ring of heights is sampled.
    /// </summary>
    private float[] BuildVertices(int tx, int tz, int lod)
    {
        int step = LodSteps[lod], n = GridSize(lod);
        float x0 = tx * TileSize, z0 = tz * TileSize;

        var heights = new float[(n + 2) * (n + 2)];
        for (int j = -1; j <= n; j++)
        for (int i = -1; i <= n; i++)
            heights[(j + 1) * (n + 2) + (i + 1)] = _field.Height(x0 + i * step, z0 + j * step);
        float H(int i, int j) => heights[(j + 1) * (n + 2) + (i + 1)];

        var vertices = new float[(n * n + 4 * n) * FloatsPerVertex];
        int v = 0;
        void Add(int i, int j, float drop)
        {
            var normal = Vector3.Normalize(new Vector3(H(i - 1, j) - H(i + 1, j), 2 * step, H(i, j - 1) - H(i, j + 1)));
            vertices[v++] = x0 + i * step;
            vertices[v++] = H(i, j) - drop;
            vertices[v++] = z0 + j * step;
            vertices[v++] = normal.X;
            vertices[v++] = normal.Y;
            vertices[v++] = normal.Z;
        }

        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
            Add(i, j, 0);

        // Deep enough to cover the height difference to a coarser neighbour.
        float skirt = step * 3f + 2f;
        for (int k = 0; k < n; k++) Add(k, 0, skirt);
        for (int k = 0; k < n; k++) Add(k, n - 1, skirt);
        for (int k = 0; k < n; k++) Add(0, k, skirt);
        for (int k = 0; k < n; k++) Add(n - 1, k, skirt);
        return vertices;
    }

    /// <summary>Triangles of an n x n grid plus its double-sided skirts; shared by every tile of a detail level.</summary>
    private uint CreateIndexBuffer(int n, out int count)
    {
        var indices = new List<ushort>();
        ushort G(int i, int j) => (ushort)(j * n + i);

        // Counter-clockwise seen from above.
        for (int j = 0; j < n - 1; j++)
        for (int i = 0; i < n - 1; i++)
        {
            indices.AddRange([G(i, j), G(i, j + 1), G(i + 1, j)]);
            indices.AddRange([G(i + 1, j), G(i, j + 1), G(i + 1, j + 1)]);
        }

        // Skirts: each border edge joined to its lowered copy, with both windings.
        int skirtStart = n * n;
        (Func<int, ushort> Edge, int Offset)[] sides =
        [
            (k => G(k, 0), 0), (k => G(k, n - 1), n), (k => G(0, k), 2 * n), (k => G(n - 1, k), 3 * n),
        ];
        foreach (var (edge, offset) in sides)
            for (int k = 0; k < n - 1; k++)
            {
                ushort a = edge(k), b = edge(k + 1);
                ushort c = (ushort)(skirtStart + offset + k), d = (ushort)(skirtStart + offset + k + 1);
                indices.AddRange([a, c, b, b, c, d]);
                indices.AddRange([a, b, c, b, d, c]);
            }

        count = indices.Count;
        uint ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        var array = indices.ToArray();
        fixed (ushort* data = array)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(array.Length * sizeof(ushort)), data, BufferUsageARB.StaticDraw);
        return ebo;
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
        }

        _gl.BindVertexArray(tile.Vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, tile.Vbo);
        fixed (float* data = vertices)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), data, BufferUsageARB.StaticDraw);
        // The element buffer binding is part of the VAO state: switch it to this level's indices.
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _indexBuffers[lod]);
        _gl.BindVertexArray(0);
        tile.Lod = lod;
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
        foreach (uint ebo in _indexBuffers) _gl.DeleteBuffer(ebo);
    }
}
