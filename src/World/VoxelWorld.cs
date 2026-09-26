using System.Numerics;
using Mine.Rendering;
using Silk.NET.OpenGL;

namespace Mine.World;

/// <summary>
/// Owns the loaded chunks: streams them in and out around the player,
/// rebuilds their meshes and answers block queries.
/// </summary>
public sealed class VoxelWorld : IDisposable
{
    public const int RenderDistance = 8; // in chunks

    private const int GeneratePerFrame = 6;
    private const int MeshPerFrame = 4;

    private readonly GL _gl;
    private readonly TerrainGenerator _generator;
    private readonly ChunkMesher _mesher = new();
    private readonly Dictionary<(int X, int Z), Chunk> _chunks = new();
    private readonly List<(int X, int Z, int DistSq)> _candidates = new();
    private readonly List<(int X, int Z)> _toUnload = new();
    private readonly HashSet<Chunk> _edited = new();
    private readonly Chunk?[] _neighbours = new Chunk?[9];

    public VoxelWorld(GL gl, int seed)
    {
        _gl = gl;
        _generator = new TerrainGenerator(seed);
    }

    public IEnumerable<Chunk> Chunks => _chunks.Values;

    public Chunk? GetChunk(int chunkX, int chunkZ) =>
        _chunks.TryGetValue((chunkX, chunkZ), out var chunk) ? chunk : null;

    public BlockType GetBlock(int x, int y, int z)
    {
        if ((uint)y >= Chunk.Height) return BlockType.Air;
        var chunk = GetChunk(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
        return chunk?.Get(x & (Chunk.Size - 1), y, z & (Chunk.Size - 1)) ?? BlockType.Air;
    }

    /// <summary>Changes a block and immediately rebuilds the affected meshes.</summary>
    public bool SetBlock(BlockPos pos, BlockType block)
    {
        if ((uint)pos.Y >= Chunk.Height) return false;
        int cx = pos.X >> Chunk.SizeShift, cz = pos.Z >> Chunk.SizeShift;
        var chunk = GetChunk(cx, cz);
        if (chunk is null) return false;

        int lx = pos.X & (Chunk.Size - 1), lz = pos.Z & (Chunk.Size - 1);
        chunk.Set(lx, pos.Y, lz, block);
        MarkEdited(chunk);

        // A block on the border also changes the faces (and AO) of the neighbour.
        if (lx == 0) MarkEdited(GetChunk(cx - 1, cz));
        if (lx == Chunk.Size - 1) MarkEdited(GetChunk(cx + 1, cz));
        if (lz == 0) MarkEdited(GetChunk(cx, cz - 1));
        if (lz == Chunk.Size - 1) MarkEdited(GetChunk(cx, cz + 1));

        foreach (var edited in _edited)
            if (edited.Mesh is not null) BuildMesh(edited);
        _edited.Clear();
        return true;
    }

    private void MarkEdited(Chunk? chunk)
    {
        if (chunk is null) return;
        chunk.Dirty = true;
        _edited.Add(chunk);
    }

    /// <summary>Generates the chunk containing a world column right away (used for spawning).</summary>
    public int GetSurfaceY(int x, int z)
    {
        int cx = x >> Chunk.SizeShift, cz = z >> Chunk.SizeShift;
        if (GetChunk(cx, cz) is null) LoadChunk(cx, cz);
        for (int y = Chunk.Height - 1; y >= 0; y--)
            if (Blocks.IsSolid(GetBlock(x, y, z))) return y;
        return 0;
    }

    public void Update(Vector3 playerPosition)
    {
        int pcx = (int)MathF.Floor(playerPosition.X) >> Chunk.SizeShift;
        int pcz = (int)MathF.Floor(playerPosition.Z) >> Chunk.SizeShift;

        // 1) Generate block data one ring beyond the render distance, so every
        //    visible chunk has its neighbours available when it is meshed.
        int genRadius = RenderDistance + 1;
        CollectCandidates(pcx, pcz, genRadius, (x, z) => !_chunks.ContainsKey((x, z)));
        for (int i = 0; i < Math.Min(GeneratePerFrame, _candidates.Count); i++)
            LoadChunk(_candidates[i].X, _candidates[i].Z);

        // 2) Mesh the closest dirty chunks whose four neighbours are loaded.
        CollectCandidates(pcx, pcz, RenderDistance, (x, z) =>
            GetChunk(x, z) is { Dirty: true }
            && _chunks.ContainsKey((x + 1, z)) && _chunks.ContainsKey((x - 1, z))
            && _chunks.ContainsKey((x, z + 1)) && _chunks.ContainsKey((x, z - 1)));
        for (int i = 0; i < Math.Min(MeshPerFrame, _candidates.Count); i++)
            BuildMesh(_chunks[(_candidates[i].X, _candidates[i].Z)]);

        // 3) Unload chunks that are well outside the render distance.
        int unloadSq = (RenderDistance + 3) * (RenderDistance + 3);
        _toUnload.Clear();
        foreach (var key in _chunks.Keys)
        {
            int dx = key.X - pcx, dz = key.Z - pcz;
            if (dx * dx + dz * dz > unloadSq) _toUnload.Add(key);
        }
        foreach (var key in _toUnload)
        {
            _chunks[key].Mesh?.Dispose();
            _chunks.Remove(key);
        }
    }

    private void CollectCandidates(int pcx, int pcz, int radius, Func<int, int, bool> filter)
    {
        _candidates.Clear();
        int radiusSq = radius * radius;
        for (int dz = -radius; dz <= radius; dz++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            int distSq = dx * dx + dz * dz;
            if (distSq > radiusSq || !filter(pcx + dx, pcz + dz)) continue;
            _candidates.Add((pcx + dx, pcz + dz, distSq));
        }
        _candidates.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
    }

    private void LoadChunk(int cx, int cz)
    {
        var chunk = new Chunk(cx, cz);
        _generator.Generate(chunk);
        _chunks[(cx, cz)] = chunk;
    }

    private void BuildMesh(Chunk chunk)
    {
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
            _neighbours[(dz + 1) * 3 + (dx + 1)] = GetChunk(chunk.ChunkX + dx, chunk.ChunkZ + dz);

        var vertices = _mesher.Build(_neighbours);
        chunk.Mesh ??= new ChunkMesh(_gl);
        chunk.Mesh.Upload(vertices);
        chunk.Dirty = false;
    }

    /// <summary>
    /// Walks the voxel grid along a ray (Amanatides &amp; Woo DDA).
    /// Returns the first solid block hit and the empty cell right before it.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out BlockPos hit, out BlockPos before)
    {
        int x = (int)MathF.Floor(origin.X), y = (int)MathF.Floor(origin.Y), z = (int)MathF.Floor(origin.Z);
        int stepX = Math.Sign(direction.X), stepY = Math.Sign(direction.Y), stepZ = Math.Sign(direction.Z);

        float tDeltaX = stepX != 0 ? MathF.Abs(1f / direction.X) : float.PositiveInfinity;
        float tDeltaY = stepY != 0 ? MathF.Abs(1f / direction.Y) : float.PositiveInfinity;
        float tDeltaZ = stepZ != 0 ? MathF.Abs(1f / direction.Z) : float.PositiveInfinity;

        float tMaxX = stepX > 0 ? (x + 1 - origin.X) * tDeltaX : stepX < 0 ? (origin.X - x) * tDeltaX : float.PositiveInfinity;
        float tMaxY = stepY > 0 ? (y + 1 - origin.Y) * tDeltaY : stepY < 0 ? (origin.Y - y) * tDeltaY : float.PositiveInfinity;
        float tMaxZ = stepZ > 0 ? (z + 1 - origin.Z) * tDeltaZ : stepZ < 0 ? (origin.Z - z) * tDeltaZ : float.PositiveInfinity;

        before = new BlockPos(x, y, z);
        float t = 0;
        while (t <= maxDistance)
        {
            if (Blocks.IsSolid(GetBlock(x, y, z)))
            {
                hit = new BlockPos(x, y, z);
                return true;
            }

            before = new BlockPos(x, y, z);
            if (tMaxX < tMaxY && tMaxX < tMaxZ) { x += stepX; t = tMaxX; tMaxX += tDeltaX; }
            else if (tMaxY < tMaxZ) { y += stepY; t = tMaxY; tMaxY += tDeltaY; }
            else { z += stepZ; t = tMaxZ; tMaxZ += tDeltaZ; }
        }

        hit = default;
        return false;
    }

    public void Dispose()
    {
        foreach (var chunk in _chunks.Values) chunk.Mesh?.Dispose();
        _chunks.Clear();
    }
}
