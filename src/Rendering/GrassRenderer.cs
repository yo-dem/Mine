using System.Collections.Concurrent;
using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Grass blades around the camera, drawn with instancing. The ground is cut into 32 m tiles;
/// each tile's blades are scattered on the thread pool (heights interpolated from a 1 m grid,
/// kept only where the ground is painted as grass, coloured like it) and uploaded as one
/// instance buffer. Every blade is the same 7-vertex strip, shaped and swayed in the vertex
/// shader, which also thins the blades out with distance.
/// </summary>
public sealed unsafe class GrassRenderer : IDisposable
{
    public const float Radius = 70f;
    private const float TileSize = 32f;
    private const float BladesPerSquareMetre = 14f;
    private const int FloatsPerBlade = 11;
    private const int UploadsPerFrame = 4;

    private sealed class Tile
    {
        public uint Vao, Vbo;
        public int Count;
        public bool Ready;
    }

    private readonly GL _gl;
    private readonly TerrainField _terrain;
    private readonly uint _bladeVbo;
    private readonly Dictionary<(int X, int Z), Tile> _tiles = new();
    private readonly ConcurrentQueue<((int X, int Z) Key, float[] Blades)> _done = new();
    private readonly List<(int X, int Z)> _toRemove = new();

    public GrassRenderer(GL gl, TerrainField terrain)
    {
        _gl = gl;
        _terrain = terrain;

        // Across (-1..1) and up (0..1) coordinates of a tapering blade, as a triangle strip.
        float[] blade = [-1, 0, 1, 0, -1, 0.35f, 1, 0.35f, -1, 0.7f, 1, 0.7f, 0, 1];
        _bladeVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _bladeVbo);
        fixed (float* p = blade)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(blade.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
    }

    public void Update(Vector3 camera)
    {
        int radius = (int)MathF.Ceiling(Radius / TileSize);
        int ctx = (int)MathF.Floor(camera.X / TileSize), ctz = (int)MathF.Floor(camera.Z / TileSize);
        for (int tz = ctz - radius; tz <= ctz + radius; tz++)
        for (int tx = ctx - radius; tx <= ctx + radius; tx++)
        {
            if (_tiles.ContainsKey((tx, tz)) || DistanceToTile(camera, tx, tz) > Radius) continue;
            _tiles[(tx, tz)] = new Tile();
            var key = (tx, tz);
            ThreadPool.QueueUserWorkItem(_ => _done.Enqueue((key, BuildBlades(key.tx, key.tz))));
        }

        for (int i = 0; i < UploadsPerFrame && _done.TryDequeue(out var result); i++)
            if (_tiles.TryGetValue(result.Key, out var tile)) Upload(tile, result.Blades);

        _toRemove.Clear();
        foreach (var (key, tile) in _tiles)
            if (tile.Ready && DistanceToTile(camera, key.X, key.Z) > Radius + TileSize) _toRemove.Add(key);
        foreach (var key in _toRemove)
        {
            var tile = _tiles[key];
            _gl.DeleteBuffer(tile.Vbo);
            _gl.DeleteVertexArray(tile.Vao);
            _tiles.Remove(key);
        }
    }

    public void Draw(Vector3 camera, Vector3 forward)
    {
        var flatForward = new Vector2(forward.X, forward.Z);
        if (flatForward.LengthSquared() > 1e-4f) flatForward = Vector2.Normalize(flatForward);
        foreach (var (key, tile) in _tiles)
        {
            if (!tile.Ready || tile.Count == 0 || DistanceToTile(camera, key.X, key.Z) > Radius) continue;
            var center = new Vector2((key.X + 0.5f) * TileSize - camera.X, (key.Z + 0.5f) * TileSize - camera.Z);
            if (Vector2.Dot(center, flatForward) < -TileSize * 0.75f && MathF.Abs(forward.Y) < 0.9f) continue;
            _gl.BindVertexArray(tile.Vao);
            _gl.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 7, (uint)tile.Count);
        }
    }

    private float[] BuildBlades(int tx, int tz)
    {
        const int n = (int)TileSize + 3; // 1 m grid with a one-metre border for slopes
        float x0 = tx * TileSize, z0 = tz * TileSize;
        var heights = new float[n * n];
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
            heights[j * n + i] = _terrain.Height(x0 + i - 1, z0 + j - 1);
        float H(int i, int j) => heights[(j + 1) * n + (i + 1)];

        var random = new Random(tx * 73856093 ^ tz * 19349663);
        int count = (int)(TileSize * TileSize * BladesPerSquareMetre);
        var blades = new List<float>(count * FloatsPerBlade);
        for (int b = 0; b < count; b++)
        {
            float lx = random.NextSingle() * TileSize, lz = random.NextSingle() * TileSize;
            int i = (int)lx, j = (int)lz;
            float fx = lx - i, fz = lz - j;
            float y = float.Lerp(float.Lerp(H(i, j), H(i + 1, j), fx), float.Lerp(H(i, j + 1), H(i + 1, j + 1), fx), fz);
            float normalY = 2f / MathF.Sqrt(MathF.Pow(H(i + 1, j) - H(i - 1, j), 2) + MathF.Pow(H(i, j + 1) - H(i, j - 1), 2) + 4f);
            if (normalY < 0.6f || y < TerrainField.WaterLevel + 1f) continue; // certainly rock, sand or water: skip early
            var root = new Vector3(x0 + lx, y - 0.05f, z0 + lz);
            float grass = GroundMaterials.GrassWeight(root, normalY);
            if (grass < 0.35f) continue;
            var color = GroundMaterials.GrassColor(root.X, root.Z);

            blades.Add(root.X);
            blades.Add(root.Y);
            blades.Add(root.Z);
            blades.Add(random.NextSingle());                        // bend
            blades.Add(random.NextSingle() * MathF.Tau);            // facing
            float height = 0.25f + 0.45f * random.NextSingle() * random.NextSingle() + 0.15f * random.NextSingle();
            blades.Add(height * (0.4f + 0.6f * Math.Clamp((grass - 0.35f) / 0.45f, 0f, 1f)));
            blades.Add(random.NextSingle());                        // thinning key
            blades.Add(random.NextSingle() * random.NextSingle());  // colour variation
            blades.Add(color.X);
            blades.Add(color.Y);
            blades.Add(color.Z);
        }
        return blades.ToArray();
    }

    private void Upload(Tile tile, float[] blades)
    {
        tile.Vao = _gl.GenVertexArray();
        tile.Vbo = _gl.GenBuffer();
        _gl.BindVertexArray(tile.Vao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _bladeVbo);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(0);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, tile.Vbo);
        fixed (float* p = blades)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(blades.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        uint stride = FloatsPerBlade * sizeof(float);
        _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)(4 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribDivisor(1, 1);
        _gl.VertexAttribDivisor(2, 1);
        _gl.VertexAttribDivisor(3, 1);

        _gl.BindVertexArray(0);
        tile.Count = blades.Length / FloatsPerBlade;
        tile.Ready = true;
    }

    private static float DistanceToTile(Vector3 p, int tx, int tz)
    {
        float dx = MathF.Max(MathF.Max(tx * TileSize - p.X, p.X - (tx + 1) * TileSize), 0);
        float dz = MathF.Max(MathF.Max(tz * TileSize - p.Z, p.Z - (tz + 1) * TileSize), 0);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public void Dispose()
    {
        foreach (var tile in _tiles.Values)
        {
            if (!tile.Ready) continue;
            _gl.DeleteBuffer(tile.Vbo);
            _gl.DeleteVertexArray(tile.Vao);
        }
        _tiles.Clear();
        _gl.DeleteBuffer(_bladeVbo);
    }
}
