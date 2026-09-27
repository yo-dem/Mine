using System.Collections.Concurrent;
using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Grass blades around the camera, drawn with instancing. The ground is cut into 32 m tiles;
/// each tile's blades are grouped in tufts (a few blades sharing one root and fanning out from
/// it) scattered on the thread pool (standing on the flat terrain tiles,
/// kept only where the ground is painted as grass, coloured like it) and uploaded as one
/// instance buffer. Every blade is the same 7-vertex strip, shaped and swayed in the vertex
/// shader, which also thins the blades out with distance.
/// </summary>
public sealed unsafe class GrassRenderer : IDisposable
{
    public const float Radius = 70f;
    private const float TileSize = 32f;
    private const float BladesPerSquareMetre = 42f;
    // Every blade is at least this tall; the tallest reach MaxHeight on ordinary ground, and much
    // more in the tall grass meadows (see GroundMaterials.TallGrass).
    private const float MinHeight = 0.6f;
    private const float MaxHeight = 1.45f, ThighHeight = 1.7f, GiantHeight = 2.6f;
    // Blades per tuft, and tufts per clump (tufts crowd together, a hand's width apart).
    private const int MinTuft = 3, MaxTuft = 9, MaxTuftsPerClump = 3;
    private const float ClumpRadius = 0.3f;
    // Short, spreading tufts that carpet the ground between the taller ones.
    private const float CarpetMin = 0.25f, CarpetMax = 0.5f;
    private const int FloatsPerBlade = 11;
    private const int UploadsPerFrame = 4;

    private sealed class Tile
    {
        public uint Vao, Vbo;
        public int Count;
        public bool Ready;
        // The blades are sorted by thinning key: Keys[i] is blade i's, so the blades that survive
        // the thinning at a given distance are a prefix (see Draw).
        public float[] Keys = [];
    }

    private readonly GL _gl;
    private readonly TerrainField _terrain;
    private readonly uint _bladeVbo;
    private readonly Dictionary<(int X, int Z), Tile> _tiles = new();
    private readonly ConcurrentQueue<((int X, int Z) Key, (float[] Blades, float[] Keys) Built)> _done = new();
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
            if (_tiles.TryGetValue(result.Key, out var tile)) Upload(tile, result.Built.Blades, result.Built.Keys);

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
            // The vertex shader drops the blades whose key is above the share kept at their distance
            // (the same formula as there): none nearer than the tile's nearest point survives, so
            // only that prefix of the sorted blades is drawn. Far tiles skip most of their blades.
            float kept = float.Lerp(1f, 0.07f, SmoothStep(8f, Radius * 0.85f, DistanceToTile(camera, key.X, key.Z)));
            int count = UpperBound(tile.Keys, kept);
            if (count == 0) continue;
            _gl.BindVertexArray(tile.Vao);
            _gl.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 7, (uint)count);
        }
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // How many of the ascending keys are at most `value`.
    private static int UpperBound(float[] keys, float value)
    {
        int lo = 0, hi = keys.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (keys[mid] <= value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private (float[] Blades, float[] Keys) BuildBlades(int tx, int tz)
    {
        // The smooth height at the centre of every terrain tile under this grass tile (plus a ring,
        // for slopes): blades stand on the tile's layer, and the smooth slope picks the material.
        const float t = TerrainField.TileSize;
        const int n = (int)(TileSize / t) + 2;
        float x0 = tx * TileSize, z0 = tz * TileSize;
        int i0 = (int)MathF.Floor(x0 / t), j0 = (int)MathF.Floor(z0 / t);
        var smooth = new float[n * n];
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
            smooth[j * n + i] = _terrain.SmoothHeight((i0 + i - 1 + 0.5f) * t, (j0 + j - 1 + 0.5f) * t);
        float S(int i, int j) => smooth[(j + 1) * n + (i + 1)];

        var random = new Random(tx * 73856093 ^ tz * 19349663);
        // Tall meadows are a little denser: more attempts everywhere, some dropped on ordinary ground.
        const float averageClump = 11f; // blades per clump on average: ~2 tufts of ~5.5 blades
        int count = (int)(TileSize * TileSize * BladesPerSquareMetre * WorldPreset.Current.GrassDensity * 1.6f / averageClump);
        var blades = new List<float>((int)(count * averageClump * 0.7f) * FloatsPerBlade);
        for (int b = 0; b < count; b++)
        {
            float lx = random.NextSingle() * TileSize, lz = random.NextSingle() * TileSize;
            float tall = GroundMaterials.TallGrass(x0 + lx, z0 + lz);
            if (random.NextSingle() > 0.9f + 0.1f * tall) continue;
            int i = (int)(lx / t), j = (int)(lz / t);
            float y = TerrainField.Layer(S(i, j));
            float gx = (S(i + 1, j) - S(i - 1, j)) / (2 * t), gz = (S(i, j + 1) - S(i, j - 1)) / (2 * t);
            float normalY = 1f / MathF.Sqrt(1 + gx * gx + gz * gz);
            var center = new Vector3(x0 + lx, y - 0.02f, z0 + lz);
            const float water = TerrainField.WaterLevel; // shores and shallows hold reed clumps instead (TreeField)
            if (normalY < 0.6f || y < water + 1f) continue; // certainly rock, sand or water: skip early
            if (_terrain.InPond(center.X, center.Z, 0.5f)) continue; // under a waterfall's pond
            float grass = GroundMaterials.GrassWeight(center, normalY);
            if (grass < 0.35f) continue;
            var color = GroundMaterials.GrassColor(center.X, center.Z);
            color = Vector3.Lerp(color, new Vector3(0.55f, 0.38f, 0.62f), tall * 0.4f); // meadows turn lilac-gold

            float maxHeight = tall <= 0.5f
                ? float.Lerp(MaxHeight, ThighHeight, tall * 2f)
                : float.Lerp(ThighHeight, GiantHeight, (tall - 0.5f) * 2f);
            // A clump: a few tufts crowded together, kept on this terrain tile's flat top (so none
            // floats over or sinks under a neighbouring layer).
            float tileX0 = (i0 + i) * t, tileZ0 = (j0 + j) * t;
            int tufts = 1 + random.Next(MaxTuftsPerClump);
            for (int q = 0; q < tufts; q++)
            {
                float a = random.NextSingle() * MathF.Tau, d = ClumpRadius * MathF.Sqrt(random.NextSingle());
                var root = new Vector3(
                    Math.Clamp(center.X + MathF.Cos(a) * d, tileX0 + 0.03f, tileX0 + t - 0.03f), center.Y,
                    Math.Clamp(center.Z + MathF.Sin(a) * d, tileZ0 + 0.03f, tileZ0 + t - 0.03f));
                // One tuft: its blades spring from (almost) the same root, fanned out all around it
                // (each leans the way it faces) and share the thinning key, so a whole tuft thins out
                // together. The middle blades stand tallest and straightest, the outer ones bow out.
                // Some tufts are short and spreading, carpeting the ground (fewer in tall meadows).
                bool carpet = random.NextSingle() < 0.25f * (1f - tall);
                float size = random.NextSingle();
                int bladeCount = Math.Min(MinTuft + (int)((MaxTuft - MinTuft + 1) * size), MaxTuft);
                float r = random.NextSingle();
                float tuftHeight = carpet
                    ? CarpetMin + (CarpetMax - CarpetMin) * r
                    : MinHeight + (maxHeight - MinHeight) * (0.6f * r + 0.4f * r * r);
                float thinning = random.NextSingle();
                float spin = random.NextSingle() * MathF.Tau;
                for (int k = 0; k < bladeCount; k++)
                {
                    float facing = spin + (k + 0.4f * random.NextSingle()) * MathF.Tau / bladeCount;
                    float outer = k / (float)(bladeCount - 1);
                    var jitter = new Vector2(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f) * 0.05f;
                    float bend = carpet ? 0.5f + 0.5f * random.NextSingle() : 0.05f + 0.5f * outer + 0.15f * random.NextSingle();
                    blades.Add(root.X + jitter.X);
                    blades.Add(root.Y);
                    blades.Add(root.Z + jitter.Y);
                    blades.Add(bend);
                    blades.Add(facing);
                    blades.Add(tuftHeight * (1f - 0.4f * outer) * (0.85f + 0.3f * random.NextSingle()));
                    blades.Add(thinning);                                   // thinning key
                    blades.Add(random.NextSingle() * random.NextSingle());  // colour variation
                    blades.Add(color.X);
                    blades.Add(color.Y);
                    blades.Add(color.Z);
                }
            }
        }
        // Sorted by thinning key (see Draw).
        int total = blades.Count / FloatsPerBlade;
        var keys = new float[total];
        var order = new int[total];
        for (int i = 0; i < total; i++) (keys[i], order[i]) = (blades[i * FloatsPerBlade + 6], i);
        Array.Sort(keys, order);
        var sorted = new float[blades.Count];
        for (int i = 0; i < total; i++)
            for (int f = 0; f < FloatsPerBlade; f++)
                sorted[i * FloatsPerBlade + f] = blades[order[i] * FloatsPerBlade + f];
        return (sorted, keys);
    }

    private void Upload(Tile tile, float[] blades, float[] keys)
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
        tile.Keys = keys;
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
