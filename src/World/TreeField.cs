using System.Collections.Concurrent;
using System.Numerics;
using Mine.Rendering;

namespace Mine.World;

/// <summary>A tree in the world: where it stands, how it is turned and sized, and which model it uses.</summary>
public readonly record struct TreeInstance(Vector3 Position, float Yaw, float Scale, int Variant);

/// <summary>
/// Where the trees grow (forests and lone trees, placed deterministically per 32 m cell on gentle,
/// grassy ground) and where the glowing decorations sit: lotus flowers floating in the shallows,
/// crystal clusters and dark rocks along the shores, glowing bells in patches on the meadows. A slow "biome" noise picks which family of tree models a region uses, so forests
/// look coherent (golden woods, dreamy teal and lilac groves...). Cells around the player are
/// generated on the thread pool; <see cref="Version"/> changes whenever the set of trees does.
/// </summary>
public sealed class TreeField
{
    public const float CellSize = 32f;
    public const float Radius = 950f;
    private const int CellsPerTask = 48;

    // Families of models (see TreeModels) grouped by the biome noise.
    private static readonly int[] Dreamy = [3, 6, 4];
    private static readonly int[] Green = [0, 5, 1, 7, 0];
    private static readonly int[] Golden = [2, 7, 2, 0];

    private readonly TerrainField _terrain;
    private readonly PerlinNoise _forest;
    private readonly PerlinNoise _biome;
    private readonly int _seed;
    private readonly Dictionary<(int X, int Z), TreeInstance[]> _cells = new();
    private readonly HashSet<(int X, int Z)> _pending = new();
    private readonly ConcurrentQueue<((int X, int Z) Key, TreeInstance[] Trees)> _done = new();
    private readonly List<(int X, int Z)> _batch = new();
    private readonly List<(int X, int Z)> _toDrop = new();
    // Instances placed by others (the floating islands' trees, the paths' stones and arches), by source.
    private readonly Dictionary<string, TreeInstance[]> _fixed = new();

    public int Version { get; private set; }

    public TreeField(TerrainField terrain, int seed)
    {
        _terrain = terrain;
        _seed = seed;
        _forest = new PerlinNoise(seed + 20);
        _biome = new PerlinNoise(seed + 21);
    }

    public IEnumerable<TreeInstance> All => _cells.Values.SelectMany(c => c).Concat(_fixed.Values.SelectMany(f => f));

    /// <summary>Replaces the instances from one outside source (they do not come from the cells).</summary>
    public void SetFixedTrees(string source, IEnumerable<TreeInstance> trees)
    {
        _fixed[source] = trees.ToArray();
        Version++;
    }

    public void Update(Vector3 player)
    {
        int radius = (int)MathF.Ceiling(Radius / CellSize);
        int pcx = (int)MathF.Floor(player.X / CellSize), pcz = (int)MathF.Floor(player.Z / CellSize);

        // Request missing cells, nearest rings first, in batches for the thread pool.
        for (int ring = 0; ring <= radius; ring++)
        for (int cz = pcz - ring; cz <= pcz + ring; cz++)
        for (int cx = pcx - ring; cx <= pcx + ring; cx++)
        {
            if (Math.Max(Math.Abs(cx - pcx), Math.Abs(cz - pcz)) != ring) continue;
            if (_cells.ContainsKey((cx, cz)) || _pending.Contains((cx, cz)) || CellDistance(player, cx, cz) > Radius) continue;
            _pending.Add((cx, cz));
            _batch.Add((cx, cz));
            if (_batch.Count == CellsPerTask) Dispatch();
        }
        if (_batch.Count > 0) Dispatch();

        while (_done.TryDequeue(out var result))
        {
            _pending.Remove(result.Key);
            _cells[result.Key] = result.Trees;
            if (result.Trees.Length > 0) Version++;
        }

        _toDrop.Clear();
        foreach (var key in _cells.Keys)
            if (CellDistance(player, key.X, key.Z) > Radius + 2 * CellSize) _toDrop.Add(key);
        foreach (var key in _toDrop)
            if (_cells.Remove(key, out var trees) && trees.Length > 0) Version++;
    }

    /// <summary>Point lights for the glowing decorations (crystal clusters) within <paramref name="radius"/>.</summary>
    public IEnumerable<PointLight> GlowingLights(Vector3 center, float radius)
    {
        int reach = (int)MathF.Ceiling(radius / CellSize);
        int cx = (int)MathF.Floor(center.X / CellSize), cz = (int)MathF.Floor(center.Z / CellSize);
        for (int dz = -reach; dz <= reach; dz++)
        for (int dx = -reach; dx <= reach; dx++)
        {
            if (!_cells.TryGetValue((cx + dx, cz + dz), out var trees)) continue;
            foreach (var tree in trees)
            {
                if (TreeModels.GlowColor(tree.Variant) is not { } color) continue;
                if (Vector3.DistanceSquared(tree.Position, center) > radius * radius) continue;
                yield return new PointLight(tree.Position + new Vector3(0, TreeModels.GlowHeight(tree.Variant) * tree.Scale, 0), color * 1.1f * tree.Scale, 7f * tree.Scale + 3f);
            }
        }
    }

    /// <summary>Pushes a point (the player's feet) out of any tree trunk it overlaps.</summary>
    public void ResolveCollision(ref Vector3 position, float radius)
    {
        foreach (var group in _fixed.Values)
            foreach (var tree in group) PushOut(ref position, radius, tree);
        int cx = (int)MathF.Floor(position.X / CellSize), cz = (int)MathF.Floor(position.Z / CellSize);
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if (!_cells.TryGetValue((cx + dx, cz + dz), out var trees)) continue;
            foreach (var tree in trees) PushOut(ref position, radius, tree);
        }
    }

    private static void PushOut(ref Vector3 position, float radius, in TreeInstance tree)
    {
        // Only the lower trunk blocks the way; flying over the crowns (or under them) is fine.
        if (position.Y > tree.Position.Y + 4f * tree.Scale || position.Y < tree.Position.Y - 2f) return;
        float trunk = TreeModels.TrunkRadius(tree.Variant);
        if (trunk <= 0) return; // flowers do not block the way
        float reach = trunk * tree.Scale + radius;
        var offset = new Vector2(position.X - tree.Position.X, position.Z - tree.Position.Z);
        float distance = offset.Length();
        if (distance >= reach || distance < 1e-4f) return;
        offset *= reach / distance;
        position.X = tree.Position.X + offset.X;
        position.Z = tree.Position.Z + offset.Y;
    }

    private void Dispatch()
    {
        var keys = _batch.ToArray();
        _batch.Clear();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (var key in keys)
                _done.Enqueue((key, Generate(key.X, key.Z)));
        });
    }

    private TreeInstance[] Generate(int cx, int cz)
    {
        float centerX = (cx + 0.5f) * CellSize, centerZ = (cz + 0.5f) * CellSize;
        float forest = _forest.Fractal(centerX * 0.0025f, centerZ * 0.0025f, 3);
        var random = new Random((int)Hash(cx, cz));

        // Dense woods where the forest noise is high, the odd lone tree elsewhere.
        int count = forest > 0.15f ? 3 + (int)((forest - 0.15f) * 22) : random.NextSingle() < 0.1f ? 1 : 0;

        float biome = _biome.Fractal(centerX * 0.0012f, centerZ * 0.0012f, 2);
        int[] family = biome < -0.2f ? Dreamy : biome > 0.15f ? Golden : Green;

        var trees = new List<TreeInstance>(count);
        for (int i = 0; i < count; i++)
        {
            float x = (cx + random.NextSingle()) * CellSize, z = (cz + random.NextSingle()) * CellSize;
            float y = _terrain.Height(x, z);
            // Gentle ground only, and not on the sand or the rock (see the terrain materials).
            if (y < TerrainField.WaterLevel + 5f || _terrain.Normal(x, z, 1f).Y < 0.86f) continue;
            int variant = family[random.Next(family.Length)];
            trees.Add(new TreeInstance(new Vector3(x, y - 0.2f, z), random.NextSingle() * MathF.Tau, 0.75f + 0.55f * random.NextSingle(), variant));
        }

        AddDecorations(trees, cx, cz, random, biome);
        return trees.ToArray();
    }

    /// <summary>Samples a few points of the cell and decorates each according to the ground there.</summary>
    private void AddDecorations(List<TreeInstance> list, int cx, int cz, Random random, float biome)
    {
        const float water = TerrainField.WaterLevel;
        var crystals = TreeModels.VariantOf(biome < 0f ? TreeModels.Decoration.CyanCrystals : TreeModels.Decoration.VioletCrystals);
        for (int i = 0; i < 20; i++)
        {
            float x = (cx + random.NextSingle()) * CellSize, z = (cz + random.NextSingle()) * CellSize;
            float y = _terrain.Height(x, z);
            float roll = random.NextSingle();
            float yaw = random.NextSingle() * MathF.Tau;

            if (y < water - 0.3f && y > water - 2.5f)
            {
                // Shallow water: lotus flowers floating on the surface.
                if (roll < 0.22f)
                    list.Add(new TreeInstance(new Vector3(x, water + 0.02f, z), yaw, 0.7f + 0.6f * random.NextSingle(),
                        TreeModels.VariantOf(TreeModels.Decoration.Lotus)));
            }
            else if (y >= water - 1f && y < water + 4f)
            {
                // The shore: crystal clusters and dark rocks.
                if (roll < 0.035f)
                    list.Add(new TreeInstance(new Vector3(x, y - 0.3f, z), yaw, 0.7f + 1.5f * random.NextSingle(), crystals));
                else if (roll < 0.08f)
                    list.Add(new TreeInstance(new Vector3(x, y - 0.4f, z), yaw, 0.8f + 1.7f * random.NextSingle(),
                        TreeModels.VariantOf(TreeModels.Decoration.ShoreRock)));
            }
            else if (y >= water + 2f)
            {
                float normalY = _terrain.Normal(x, z, 1f).Y;
                if (normalY < 0.75f)
                {
                    // Rocky slopes: the odd crystal cluster.
                    if (roll < 0.012f)
                        list.Add(new TreeInstance(new Vector3(x, y - 0.3f, z), yaw, 0.6f + 1.2f * random.NextSingle(), crystals));
                }
                else if (roll < 0.12f && _forest.Fractal(x * 0.02f + 40f, z * 0.02f, 2) > 0.15f)
                {
                    // Meadows: glowing bells, in patches.
                    list.Add(new TreeInstance(new Vector3(x, y - 0.05f, z), yaw, 0.8f + 0.6f * random.NextSingle(),
                        TreeModels.VariantOf(TreeModels.Decoration.GlowBells)));
                }
            }
        }
    }

    private static float CellDistance(Vector3 p, int cx, int cz)
    {
        float dx = (cx + 0.5f) * CellSize - p.X, dz = (cz + 0.5f) * CellSize - p.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private uint Hash(int x, int z)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + z * 668265263 + _seed * 144269504 + 977);
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }
}
