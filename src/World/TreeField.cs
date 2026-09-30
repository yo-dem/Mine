using System.Collections.Concurrent;
using System.Numerics;
using Mine.Rendering;

namespace Mine.World;

/// <summary>A tree in the world: where it stands, how it is turned and sized, and which model it uses.</summary>
public readonly record struct TreeInstance(Vector3 Position, float Yaw, float Scale, int Variant);

/// <summary>
/// Where the trees grow (forests and lone trees, placed deterministically per 32 m cell on gentle,
/// grassy ground) and where the glowing decorations sit: lotus flowers floating in the shallows,
/// crystal clusters and dark rocks along the shores, glowing bells in patches on the meadows. The
/// biomes (<see cref="GroundMaterials.Biome"/>) pick the family of tree models and the flowers of a
/// region: indigo woods, pink woods, turquoise woods, and deserts of dry trees. Cells around the player are
/// generated on the thread pool; <see cref="Version"/> changes whenever the set of trees does.
/// Trees, palms and rocks can be broken (<see cref="Pick"/>, <see cref="Gather"/>): they are
/// remembered by position and never come back; nothing without a trunk grows where a floor covers the ground.
/// </summary>
public sealed class TreeField
{
    public const float CellSize = 32f;
    private const float GroveSize = 80f;        // groves are Voronoi regions around points jittered on this grid
    private const float OtherSpeciesChance = 0.1f;
    public const float Radius = 950f;
    private const int CellsPerTask = 48;

    // Families of models (see TreeModels) for each biome, in the order of GroundMaterials.Biome:
    // the trees of the canopy and those of the undergrowth.
    // Indices: 0 indigo, 1 cypress, 2 pink, 3 teal, 4 lilac blossom, 5 fireflies, 6 blue orbs,
    // 7 umbrella, 8 giant, 9 willow, 10 pine, 11 sapling, 12 shrub, 13 dry, 14 gnarled, 15 dry shrub.
    private static readonly int[][] Canopy =
    [
        [0, 0, 1, 10, 10, 8, 5], // indigo woods
        [2, 2, 4, 4, 7],         // pink woods
        [3, 3, 6, 9, 9],         // turquoise woods
        [13, 13, 14, 14, 15],    // desert
    ];
    private static readonly int[][] Undergrowth = [[12, 12, 11], [11, 11, 12], [12, 11, 12], [15, 15, 14]];
    private const int DesertBiome = 3;

    // Forest noise above this: woods (the same threshold for the trees, their flowers and gardens).
    // Woods grow where the forest noise exceeds this (set by the world preset).
    private static float WoodsThreshold => WorldPreset.Current.WoodsThreshold;

    private readonly TerrainField _terrain;
    private readonly PerlinNoise _forest;
    private readonly int _seed;
    private readonly Dictionary<(int X, int Z), TreeInstance[]> _cells = new();
    private readonly HashSet<(int X, int Z)> _pending = new();
    private readonly ConcurrentQueue<((int X, int Z) Key, TreeInstance[] Trees)> _done = new();
    private readonly List<(int X, int Z)> _batch = new();
    private readonly List<(int X, int Z)> _toDrop = new();
    // Instances placed by others (the floating islands' trees, the paths' stones and arches), by source.
    private readonly Dictionary<string, TreeInstance[]> _fixed = new();

    public int Version { get; private set; }

    // What the player broke, by position (generation is deterministic, so positions repeat
    // exactly): it is left out of the cells for good. Read by the worker threads.
    private readonly ConcurrentDictionary<Vector3, bool> _gathered = new();

    /// <summary>Whether a building covers the ground at (x, z): no flowers there. Called from worker threads.</summary>
    public Func<float, float, bool>? Covered { get; set; }

    public TreeField(TerrainField terrain, int seed)
    {
        _terrain = terrain;
        _seed = seed;
        _forest = new PerlinNoise(seed + 20);
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

    /// <summary>Regenerates the cell around (x, z) at once, if it is loaded (after a change there).</summary>
    public void Refresh(float x, float z)
    {
        var key = ((int)MathF.Floor(x / CellSize), (int)MathF.Floor(z / CellSize));
        if (!_cells.ContainsKey(key)) return;
        _cells[key] = Generate(key.Item1, key.Item2);
        Version++;
    }

    /// <summary>
    /// The gatherable instance the ray passes through nearest (within <paramref name="maxDistance"/>),
    /// each aimed at as a vertical capsule around its trunk or body (see <see cref="TreeModels.YieldOf"/>).
    /// </summary>
    public (TreeInstance Tree, TreeModels.Yield Yield, float Distance)? Pick(Vector3 origin, Vector3 direction, float maxDistance)
    {
        (TreeInstance, TreeModels.Yield, float)? best = null;
        float nearest = maxDistance;
        int reach = (int)MathF.Ceiling(maxDistance / CellSize);
        int cx = (int)MathF.Floor(origin.X / CellSize), cz = (int)MathF.Floor(origin.Z / CellSize);
        for (int dz = -reach; dz <= reach; dz++)
        for (int dx = -reach; dx <= reach; dx++)
        {
            if (!_cells.TryGetValue((cx + dx, cz + dz), out var trees)) continue;
            foreach (var tree in trees)
            {
                if (Vector3.DistanceSquared(tree.Position, origin) > (maxDistance + 5f) * (maxDistance + 5f)) continue;
                if (TreeModels.YieldOf(tree.Variant, tree.Scale) is not { } yield) continue;
                var (t, miss) = WorldObjects.ClosestToSegment(origin, direction, tree.Position, yield.PickHeight);
                if (t < 0 || t > nearest || miss > yield.PickRadius) continue;
                nearest = t;
                best = (tree, yield, t);
            }
        }
        return best;
    }

    /// <summary>Takes a broken instance out of the world for good.</summary>
    public void Gather(TreeInstance tree)
    {
        _gathered[tree.Position] = true;
        var key = ((int)MathF.Floor(tree.Position.X / CellSize), (int)MathF.Floor(tree.Position.Z / CellSize));
        if (!_cells.TryGetValue(key, out var trees)) return;
        _cells[key] = trees.Where(t => t != tree).ToArray();
        Version++;
    }

    /// <summary>Where the things broken stood, for saving.</summary>
    public List<Vector3> SaveGathered() => _gathered.Keys.ToList();

    /// <summary>Restores what was broken (before the cells around are generated).</summary>
    public void LoadGathered(IEnumerable<Vector3> gathered)
    {
        _gathered.Clear();
        foreach (var position in gathered) _gathered[position] = true;
    }

    /// <summary>Whether a trunk (a tree, a palm, a rock, a crystal cluster) stands in the rectangle min..max (xz).</summary>
    public bool TrunkIn(Vector2 min, Vector2 max)
    {
        int x0 = (int)MathF.Floor((min.X - 2f) / CellSize), x1 = (int)MathF.Floor((max.X + 2f) / CellSize);
        int z0 = (int)MathF.Floor((min.Y - 2f) / CellSize), z1 = (int)MathF.Floor((max.Y + 2f) / CellSize);
        for (int cz = z0; cz <= z1; cz++)
        for (int cx = x0; cx <= x1; cx++)
        {
            if (!_cells.TryGetValue((cx, cz), out var trees)) continue;
            foreach (var tree in trees)
            {
                float r = TreeModels.TrunkRadius(tree.Variant) * tree.Scale;
                if (r <= 0) continue;
                float nx = Math.Clamp(tree.Position.X, min.X, max.X), nz = Math.Clamp(tree.Position.Z, min.Y, max.Y);
                float ox = tree.Position.X - nx, oz = tree.Position.Z - nz;
                if (ox * ox + oz * oz < r * r) return true;
            }
        }
        return false;
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

    /// <summary>
    /// Coloured lights of the nearest flowers born of light (at most <paramref name="max"/>, within
    /// <paramref name="radius"/>). Each fades out toward a cutoff that moves smoothly with the
    /// distance of the first flower left out, so lights never pop as the player walks.
    /// </summary>
    public List<PointLight> FlowerLights(Vector3 center, float radius = 14f, int max = 8)
    {
        _flowerScratch.Clear();
        int reach = (int)MathF.Ceiling(radius / CellSize);
        int cx = (int)MathF.Floor(center.X / CellSize), cz = (int)MathF.Floor(center.Z / CellSize);
        for (int dz = -reach; dz <= reach; dz++)
        for (int dx = -reach; dx <= reach; dx++)
        {
            if (!_cells.TryGetValue((cx + dx, cz + dz), out var trees)) continue;
            foreach (var tree in trees)
            {
                if (TreeModels.FlowerLight(tree.Variant) is null) continue;
                float d = Vector3.Distance(tree.Position, center);
                if (d < radius) _flowerScratch.Add((d, tree));
            }
        }
        _flowerScratch.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        float cutoff = _flowerScratch.Count > max ? _flowerScratch[max].Distance : radius;
        var lights = new List<PointLight>(max);
        for (int i = 0; i < Math.Min(max, _flowerScratch.Count); i++)
        {
            var (d, tree) = _flowerScratch[i];
            float t = Math.Clamp((cutoff - d) / (cutoff * 0.4f), 0f, 1f);
            float fade = t * t * (3 - 2 * t);
            if (fade <= 0f) continue;
            var color = TreeModels.FlowerLight(tree.Variant)!.Value;
            lights.Add(new PointLight(tree.Position + new Vector3(0, 0.8f * tree.Scale, 0), color * 0.7f * fade, 4f * tree.Scale));
        }
        return lights;
    }

    private readonly List<(float Distance, TreeInstance Tree)> _flowerScratch = new();

    /// <summary>
    /// Gardens of flowers born of light (irises, poppies, lilies), the only place they grow: glowing
    /// patches under the thick woods (a garden noise picks about half of them), densest at their
    /// heart and where the trees crowd. The biome picks the kind.
    /// </summary>
    private void AddLightGardens(List<TreeInstance> list, int cx, int cz, Random random)
    {
        const float water = TerrainField.WaterLevel;
        TreeModels.Decoration[] kinds = [TreeModels.Decoration.Irises, TreeModels.Decoration.Poppies, TreeModels.Decoration.Lilies];
        for (int i = 0; i < 45; i++)
        {
            float x = TerrainField.InsideTile((cx + random.NextSingle()) * CellSize, 0.35f);
            float z = TerrainField.InsideTile((cz + random.NextSingle()) * CellSize, 0.35f);
            float garden = _forest.Fractal(x * 0.012f + 300f, z * 0.012f - 200f, 2);
            // The same forest noise as the trees: the thicker the wood, the more flowers.
            float woods = Math.Clamp((Forest(x, z) - WoodsThreshold) / 0.15f, 0f, 1f);
            float density = Math.Clamp(garden / 0.15f, 0f, 1f) * woods;
            if (random.NextSingle() >= density * 0.8f) continue;
            float y = _terrain.Height(x, z);
            if (y < water + 2f || _terrain.Normal(x, z, 1f).Y < 0.8f) continue;
            // Each wood has its flower: irises in the indigo woods, poppies in the pink, lilies in the
            // turquoise; none in the desert.
            int biome = PickBiome(x, z, random);
            if (biome == DesertBiome) continue;
            var kind = kinds[random.NextSingle() < 0.15f ? random.Next(kinds.Length) : biome];
            list.Add(new TreeInstance(new Vector3(x, y - 0.03f, z), random.NextSingle() * MathF.Tau,
                1.1f + 0.45f * random.NextSingle(), TreeModels.VariantOf(kind)));
        }
    }

    /// <summary>
    /// Flowers mixed with the grass: in the meadows, patches where one of the biome's kinds dominates
    /// (daisies, tulips, lupins, starflowers) with a few others mixed in; under the woods, glowing
    /// starflowers and bells; in the deserts, the odd starflower.
    /// </summary>
    private void AddFlowers(List<TreeInstance> list, int cx, int cz, Random random)
    {
        const float water = TerrainField.WaterLevel;
        // The flowers born of light grow only in their gardens (AddLightGardens). Each biome has its
        // meadow flowers (indigo: lupins and starflowers, pink: tulips and daisies, turquoise:
        // starflowers and daisies); the deserts only the odd starflower.
        var meadows = new[]
        {
            new[] { TreeModels.Decoration.Lupins, TreeModels.Decoration.Starflowers, TreeModels.Decoration.Lupins, TreeModels.Decoration.Daisies },
            new[] { TreeModels.Decoration.Tulips, TreeModels.Decoration.Daisies, TreeModels.Decoration.Tulips, TreeModels.Decoration.Lupins },
            new[] { TreeModels.Decoration.Starflowers, TreeModels.Decoration.Daisies, TreeModels.Decoration.Starflowers, TreeModels.Decoration.Tulips },
        };
        for (int i = 0; i < 40; i++)
        {
            float x = TerrainField.InsideTile((cx + random.NextSingle()) * CellSize, 0.35f);
            float z = TerrainField.InsideTile((cz + random.NextSingle()) * CellSize, 0.35f);
            float y = _terrain.Height(x, z);
            float roll = random.NextSingle();
            if (y < water + 2f || _terrain.Normal(x, z, 1f).Y < 0.8f) continue;

            TreeModels.Decoration kind;
            int biome = PickBiome(x, z, random);
            if (biome == DesertBiome)
            {
                if (roll > 0.015f) continue;
                kind = TreeModels.Decoration.Starflowers;
            }
            else if (Forest(x, z) > WoodsThreshold)
            {
                // Under the woods.
                if (roll > 0.35f) continue;
                kind = random.NextSingle() < 0.6f ? TreeModels.Decoration.Starflowers : TreeModels.Decoration.GlowBells;
            }
            else
            {
                // Everywhere in moderation, a little thicker in patches.
                float patch = _forest.Fractal(x * 0.015f + 90f, z * 0.015f - 30f, 2);
                if (roll > 0.3f + 0.2f * Math.Clamp(patch + 0.1f, 0f, 1f)) continue;
                var meadow = meadows[biome];
                float species = _forest.Fractal(x * 0.008f - 70f, z * 0.008f + 55f, 2);
                // Bands of the species noise (it mostly spans about -0.4..0.4), one per kind of flower.
                int dominant = Math.Clamp((int)((species + 0.35f) / 0.7f * meadow.Length), 0, meadow.Length - 1);
                kind = meadow[random.NextSingle() < 0.2f ? random.Next(meadow.Length) : dominant];
            }
            list.Add(new TreeInstance(new Vector3(x, y - 0.03f, z), random.NextSingle() * MathF.Tau,
                1.05f + 0.5f * random.NextSingle(), TreeModels.VariantOf(kind)));
        }
    }

    /// <summary>
    /// Groves of palms, mostly along the lake and sea shores: a grove noise picks where they gather,
    /// thickest just above the water and thinning out up the banks (a few also stand farther
    /// inland). Palms of three builds and many sizes, leaning every way.
    /// </summary>
    private void AddPalmGroves(List<TreeInstance> list, int cx, int cz, Random random)
    {
        const float water = TerrainField.WaterLevel;
        // Where lone trees are rare, palms grow only in the hearts of their groves, close together.
        float lone = WorldPreset.Current.LoneTrees;
        float start = 0.05f + 0.15f * (1f - lone), ramp = 0.2f - 0.12f * (1f - lone);
        for (int i = 0; i < 30; i++)
        {
            float x = TerrainField.InsideTile((cx + random.NextSingle()) * CellSize, 0.5f);
            float z = TerrainField.InsideTile((cz + random.NextSingle()) * CellSize, 0.5f);
            float grove = _forest.Fractal(x * 0.011f - 510f, z * 0.011f + 330f, 2);
            if (grove < start) continue;
            float y = _terrain.Height(x, z);
            if (y < water + 0.8f) continue;
            float shore = Math.Clamp((water + 12f - y) / 9f, 0f, 1f); // 1 up to 3 m above the water, 0 from 12 m
            float density = Math.Clamp((grove - start) / ramp, 0f, 1f) * (0.08f * lone + (1f - 0.08f * lone) * shore);
            if (random.NextSingle() >= density * 0.5f * WorldPreset.Current.TreeDensity || _terrain.Normal(x, z, 1f).Y < 0.8f) continue;
            list.Add(RandomPalm(x, y, z, random.NextSingle() * MathF.Tau, random));
        }
    }

    // A palm of a random build (mostly the middling one) and size.
    private static TreeInstance RandomPalm(float x, float y, float z, float yaw, Random random)
    {
        float roll = random.NextSingle();
        var kind = roll < 0.25f ? TreeModels.Decoration.TallPalm : roll < 0.5f ? TreeModels.Decoration.ShortPalm : TreeModels.Decoration.Palm;
        return new TreeInstance(new Vector3(x, y - 0.2f, z), yaw, 0.7f + 0.65f * random.NextSingle(), TreeModels.VariantOf(kind));
    }

    /// <summary>
    /// Scattered reed clumps in the reed beds along the shores and in the shallows, of very different
    /// heights: mostly middling, some low, the odd one towering over the rest.
    /// </summary>
    private void AddReeds(List<TreeInstance> list, int cx, int cz, Random random)
    {
        const float water = TerrainField.WaterLevel;
        const float t = TerrainField.TileSize;
        for (float tz = cz * CellSize; tz < (cz + 1) * CellSize; tz += t)
        for (float tx = cx * CellSize; tx < (cx + 1) * CellSize; tx += t)
        {
            var center = _terrain.TileCenter(tx, tz);
            if (center.Y < water - 1.2f || center.Y > water + 1.0f) continue;
            float bed = GroundMaterials.Reeds(center.X, center.Z);
            if (random.NextSingle() > bed * 0.3f) continue;
            float roll = random.NextSingle();
            float scale = roll < 0.3f ? 0.35f + 0.3f * random.NextSingle()   // low
                : roll < 0.85f ? 0.7f + 0.4f * random.NextSingle()            // middling
                : 1.3f + 0.5f * random.NextSingle();                          // towering
            list.Add(new TreeInstance(center, random.NextSingle() * MathF.Tau, scale,
                TreeModels.VariantOf(TreeModels.Decoration.Reeds)));
        }
    }

    /// <summary>
    /// The species of the grove a point belongs to: the nearest of a set of points jittered on a
    /// <see cref="GroveSize"/> grid (so groves have irregular, organic borders) picks it from the family.
    /// </summary>
    private int GroveSpecies(float x, float z, int[] family)
    {
        int gx = (int)MathF.Floor(x / GroveSize), gz = (int)MathF.Floor(z / GroveSize);
        float best = float.MaxValue;
        uint bestHash = 0;
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            uint h = Hash(gx + dx + 7919, gz + dz - 104729);
            float px = (gx + dx + (h & 0xFF) / 255f) * GroveSize;
            float pz = (gz + dz + ((h >> 8) & 0xFF) / 255f) * GroveSize;
            float d = (px - x) * (px - x) + (pz - z) * (pz - z);
            if (d < best) { best = d; bestHash = h; }
        }
        return family[(int)((bestHash >> 16) % (uint)family.Length)];
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

    /// <summary>
    /// The forest noise at (x, z), which decides where the woods are. In an archipelago it rises
    /// toward the middle of the islands, so their hearts are thickly wooded.
    /// </summary>
    private float Forest(float x, float z)
    {
        float forest = _forest.Fractal(x * 0.0025f, z * 0.0025f, 3);
        if (WorldPreset.Current.Islands) forest += 0.4f * Math.Clamp((GroundMaterials.Inland(x, z) - 0.8f) / 1.2f, 0f, 1f);
        return forest;
    }

    private TreeInstance[] Generate(int cx, int cz)
    {
        float centerX = (cx + 0.5f) * CellSize, centerZ = (cz + 0.5f) * CellSize;
        float forest = Forest(centerX, centerZ);
        var random = new Random((int)Hash(cx, cz));
        float desert = GroundMaterials.Desert(centerX, centerZ);

        // Thick woods where the forest noise is high, the odd lone tree elsewhere; in the deserts,
        // sparse groves of dry trees. Under the canopy, shrubs and saplings.
        int count = forest > WoodsThreshold ? 8 + (int)((forest - WoodsThreshold) * 55) : random.NextSingle() < 0.3f * WorldPreset.Current.LoneTrees ? 1 + random.Next(2) : 0;
        count = (int)MathF.Round(float.Lerp(count, forest > WoodsThreshold ? 1 + forest * 10 : random.NextSingle() < 0.25f * WorldPreset.Current.LoneTrees ? 1 : 0, desert));
        count = (int)MathF.Round(count * WorldPreset.Current.TreeDensity);
        int undergrowth = forest > WoodsThreshold ? count / 2 : 0;

        var trees = new List<TreeInstance>(count + undergrowth);
        for (int i = 0; i < count + undergrowth; i++)
        {
            bool under = i >= count;
            // Well inside a terrain tile, so the trunk stands on its flat top.
            float x = TerrainField.InsideTile((cx + random.NextSingle()) * CellSize, 0.6f);
            float z = TerrainField.InsideTile((cz + random.NextSingle()) * CellSize, 0.6f);
            float y = _terrain.Height(x, z);
            // Gentle ground only, and not on the shore sand or the rock (see the terrain materials).
            if (y < TerrainField.WaterLevel + 5f || _terrain.Normal(x, z, 1f).Y < 0.86f) continue;
            // Tall grass meadows are open land.
            if (GroundMaterials.TallGrass(x, z) > 0.2f) continue;
            int biome = PickBiome(x, z, random);
            int variant;
            float scale;
            if (under)
            {
                var family = Undergrowth[biome];
                variant = family[random.Next(family.Length)];
                scale = 0.7f + 0.6f * random.NextSingle();
            }
            else
            {
                // One species rules each grove; now and then another one grows among it.
                var family = Canopy[biome];
                variant = random.NextSingle() < OtherSpeciesChance ? family[random.Next(family.Length)] : GroveSpecies(x, z, family);
                scale = 0.75f + 0.6f * random.NextSingle();
            }
            trees.Add(new TreeInstance(new Vector3(x, y - 0.05f, z), random.NextSingle() * MathF.Tau, scale, variant));
        }

        AddDecorations(trees, cx, cz, random);
        // Nothing grows in the ponds under the islands' waterfalls, or in the rock of the spires.
        trees.RemoveAll(t => _terrain.InPond(t.Position.X, t.Position.Z, 1.5f)
            || _terrain.SpireHeight(t.Position.X, t.Position.Z) > 0f);
        // What the player broke is gone for good.
        if (!_gathered.IsEmpty) trees.RemoveAll(t => _gathered.ContainsKey(t.Position));
        // No flowers or reeds through the floors.
        if (Covered is { } covered)
            trees.RemoveAll(t => TreeModels.TrunkRadius(t.Variant) <= 0 && covered(t.Position.X, t.Position.Z));
        return trees.ToArray();
    }

    /// <summary>
    /// The biome at a point, drawn at random by the biomes' weights there: inside a biome it is
    /// always that one, and across a border the two mix, thinning out into each other.
    /// </summary>
    private static int PickBiome(float x, float z, Random random)
    {
        var w = GroundMaterials.Biome(x, z);
        float roll = random.NextSingle();
        if ((roll -= w.X) < 0) return 0;
        if ((roll -= w.Y) < 0) return 1;
        if ((roll -= w.Z) < 0) return 2;
        return DesertBiome;
    }

    // Giant crystal clusters are rare finds: this share of the sampled points on shores, desert
    // sand and rocky slopes tries one (see TryCrystal, which turns many of them down).
    private const float CrystalChance = 0.006f;
    private const float ClearShare = 0.45f; // of them translucent (glass), picked by place

    /// <summary>
    /// A crystal cluster at (x, z), only where it touches no terrain but the ground it stands on:
    /// the ground under its base must be one flat tile top (it stands on it, never sunk), and no
    /// ground within the reach of its leaning crystals may rise above it.
    /// </summary>
    private void TryCrystal(List<TreeInstance> list, float x, float y, float z, float yaw, float scale, int variant)
    {
        // Some are translucent, whatever the biome (chosen by the place, so no random number is drawn).
        uint place = unchecked((uint)((int)MathF.Floor(x * 8f) * 73856093 ^ (int)MathF.Floor(z * 8f) * 19349663));
        place = unchecked((place ^ (place >> 13)) * 1274126177u);
        if ((place >> 16) % 1000 < ClearShare * 1000) variant = TreeModels.VariantOf(TreeModels.Decoration.ClearCrystals);
        float baseRadius = (TreeModels.CrystalBaseRadius + 0.2f) * scale, reach = TreeModels.CrystalReach * scale;
        for (float dz = -reach; dz <= reach; dz += 0.5f)
        for (float dx = -reach; dx <= reach; dx += 0.5f)
        {
            float d2 = dx * dx + dz * dz;
            if (d2 > reach * reach) continue;
            float h = _terrain.Height(x + dx, z + dz);
            if (d2 <= baseRadius * baseRadius ? h != y : h > y) return;
        }
        list.Add(new TreeInstance(new Vector3(x, y, z), yaw, scale, variant));
    }

    /// <summary>Samples a few points of the cell and decorates each according to the ground there.</summary>
    private void AddDecorations(List<TreeInstance> list, int cx, int cz, Random random)
    {
        AddReeds(list, cx, cz, random);
        AddPalmGroves(list, cx, cz, random);
        AddFlowers(list, cx, cz, random);
        AddLightGardens(list, cx, cz, random);
        const float water = TerrainField.WaterLevel;
        var weights = GroundMaterials.Biome((cx + 0.5f) * CellSize, (cz + 0.5f) * CellSize);
        // Cyan crystals by the turquoise woods, violet ones elsewhere.
        var crystals = TreeModels.VariantOf(weights.Z > 0.5f ? TreeModels.Decoration.CyanCrystals : TreeModels.Decoration.VioletCrystals);
        bool desert = weights.W > 0.5f;
        for (int i = 0; i < 20; i++)
        {
            float x = TerrainField.InsideTile((cx + random.NextSingle()) * CellSize, 0.4f);
            float z = TerrainField.InsideTile((cz + random.NextSingle()) * CellSize, 0.4f);
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
                // The shore: palms, and a rare crystal cluster.
                if (roll < 0.03f && y > water + 0.8f)
                    list.Add(RandomPalm(x, y, z, yaw, random));
                else if (roll < 0.03f + CrystalChance)
                    TryCrystal(list, x, y, z, yaw, 0.7f + 1.5f * random.NextSingle(), crystals);
            }
            else if (y >= water + 2f)
            {
                float normalY = _terrain.Normal(x, z, 1f).Y;
                if (desert)
                {
                    // A rare crystal cluster rising from the desert sand.
                    if (roll < CrystalChance)
                        TryCrystal(list, x, y, z, yaw, 0.7f + 1.6f * random.NextSingle(), crystals);
                }
                else if (normalY < 0.75f)
                {
                    // Rocky slopes: a rare crystal cluster (where a ledge is flat and wide enough).
                    if (roll < CrystalChance)
                        TryCrystal(list, x, y, z, yaw, 0.6f + 1.2f * random.NextSingle(), crystals);
                }
                else if (roll < 0.3f && _forest.Fractal(x * 0.02f + 40f, z * 0.02f, 2) > 0.0f)
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
