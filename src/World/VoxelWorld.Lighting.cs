namespace Mine.World;

/// <summary>
/// Block light propagation. Light spreads from glowing blocks through air with a
/// breadth-first flood fill. After an edit, the whole box the edited cell could influence
/// is cleared and refilled: from the emitters inside it and from the lit cells just outside
/// it, which the edit cannot have changed because they are beyond any light's reach.
/// </summary>
public sealed partial class VoxelWorld
{
    private const int RelightRadiusX = BlockLight.MaxReach;                                 // blocks
    private const int RelightRadiusY = BlockLight.MaxLevel / BlockLight.VerticalFalloff + 1; // cells

    private readonly Queue<BlockPos> _lightQueue = new();
    private readonly HashSet<Chunk> _relit = new();

    private ushort GetLight(int x, int y, int z)
    {
        if ((uint)y >= Chunk.Height) return 0;
        var chunk = GetChunk(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
        return chunk?.GetLight(x & (Chunk.Size - 1), y, z & (Chunk.Size - 1)) ?? 0;
    }

    /// <summary>Recomputes the light around an edited cell; returns the chunks whose light changed.</summary>
    private HashSet<Chunk> Relight(BlockPos center)
    {
        _relit.Clear();
        int x0 = center.X - RelightRadiusX, x1 = center.X + RelightRadiusX;
        int z0 = center.Z - RelightRadiusX, z1 = center.Z + RelightRadiusX;
        int y0 = Math.Max(0, center.Y - RelightRadiusY), y1 = Math.Min(Chunk.Height - 1, center.Y + RelightRadiusY);

        // 1) Clear the box, seeding the emitters found inside it.
        bool anyLight = false;
        for (int cz = z0 >> Chunk.SizeShift; cz <= z1 >> Chunk.SizeShift; cz++)
        for (int cx = x0 >> Chunk.SizeShift; cx <= x1 >> Chunk.SizeShift; cx++)
        {
            var chunk = GetChunk(cx, cz);
            if (chunk is null) continue;
            int bx = cx << Chunk.SizeShift, bz = cz << Chunk.SizeShift;
            int lx0 = Math.Max(x0 - bx, 0), lx1 = Math.Min(x1 - bx, Chunk.Size - 1);
            int lz0 = Math.Max(z0 - bz, 0), lz1 = Math.Min(z1 - bz, Chunk.Size - 1);
            bool hadLight = chunk.HasLight;

            for (int y = y0; y <= y1; y++)
            for (int z = lz0; z <= lz1; z++)
            for (int x = lx0; x <= lx1; x++)
            {
                ushort emission = Blocks.Emission(chunk.Get(x, y, z));
                if (hadLight) chunk.SetLight(x, y, z, emission);
                else if (emission != 0) chunk.SetLight(x, y, z, emission);
                if (emission != 0) _lightQueue.Enqueue(new BlockPos(bx + x, y, bz + z));
            }

            if (hadLight || chunk.HasLight)
            {
                _relit.Add(chunk);
                anyLight = true;
            }
        }
        if (!anyLight) return _relit;

        // 2) Seed from the lit cells bordering the box.
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
        {
            SeedIfLit(x0 - 1, y, z);
            SeedIfLit(x1 + 1, y, z);
        }
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            SeedIfLit(x, y, z0 - 1);
            SeedIfLit(x, y, z1 + 1);
        }
        for (int z = z0; z <= z1; z++)
        for (int x = x0; x <= x1; x++)
        {
            SeedIfLit(x, y0 - 1, z);
            SeedIfLit(x, y1 + 1, z);
        }

        // 3) Flood fill.
        Propagate();
        return _relit;
    }

    /// <summary>
    /// Lights a freshly generated chunk: its own emitters (placed by the player and replayed
    /// from the edit log) and the light flowing in from already loaded neighbours.
    /// </summary>
    private void LightNewChunk(Chunk chunk)
    {
        int bx = chunk.ChunkX << Chunk.SizeShift, bz = chunk.ChunkZ << Chunk.SizeShift;
        _relit.Clear();
        if (_edits.TryGetValue((chunk.ChunkX, chunk.ChunkZ), out var edits))
            foreach (var (key, block) in edits)
            {
                ushort emission = Blocks.Emission(block);
                if (emission == 0) continue;
                int x = key & (Chunk.Size - 1), y = key >> (2 * Chunk.SizeShift), z = (key >> Chunk.SizeShift) & (Chunk.Size - 1);
                chunk.SetLight(x, y, z, emission);
                _lightQueue.Enqueue(new BlockPos(bx + x, y, bz + z));
            }

        for (int y = 0; y < Chunk.Height; y++)
        for (int i = 0; i < Chunk.Size; i++)
        {
            SeedIfLit(bx - 1, y, bz + i);
            SeedIfLit(bx + Chunk.Size, y, bz + i);
            SeedIfLit(bx + i, y, bz - 1);
            SeedIfLit(bx + i, y, bz + Chunk.Size);
        }
        Propagate();
    }

    private void SeedIfLit(int x, int y, int z)
    {
        if (GetLight(x, y, z) != 0) _lightQueue.Enqueue(new BlockPos(x, y, z));
    }

    private void Propagate()
    {
        while (_lightQueue.TryDequeue(out var p))
        {
            ushort light = GetLight(p.X, p.Y, p.Z);
            SpreadTo(p.X + 1, p.Y, p.Z, light, BlockLight.HorizontalFalloff);
            SpreadTo(p.X - 1, p.Y, p.Z, light, BlockLight.HorizontalFalloff);
            SpreadTo(p.X, p.Y, p.Z + 1, light, BlockLight.HorizontalFalloff);
            SpreadTo(p.X, p.Y, p.Z - 1, light, BlockLight.HorizontalFalloff);
            SpreadTo(p.X, p.Y + 1, p.Z, light, BlockLight.VerticalFalloff);
            SpreadTo(p.X, p.Y - 1, p.Z, light, BlockLight.VerticalFalloff);
        }
    }

    private void SpreadTo(int x, int y, int z, ushort source, int falloff)
    {
        if ((uint)y >= Chunk.Height) return;
        var chunk = GetChunk(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
        if (chunk is null) return;
        int lx = x & (Chunk.Size - 1), lz = z & (Chunk.Size - 1);
        if (Blocks.IsSolid(chunk.Get(lx, y, lz))) return; // light only travels through air

        ushort current = chunk.GetLight(lx, y, lz);
        ushort spread = BlockLight.Spread(current, source, falloff);
        if (spread == current) return;

        chunk.SetLight(lx, y, lz, spread);
        _relit.Add(chunk);
        _lightQueue.Enqueue(new BlockPos(x, y, z));
    }
}
