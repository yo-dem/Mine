using System.Numerics;

namespace Mine.World;

public enum ObjectKind
{
    Crystal,
}

/// <summary>What an object kind looks like to the game: name, light and how it is picked.</summary>
public readonly record struct ObjectDef(
    string Name,
    Vector3 LightColor,
    float LightIntensity,
    float LightRadius,
    float LightHeight, // above the object's base
    float PickHeight,  // height of the vertical capsule used to aim at it
    float PickRadius);

/// <summary>A placed object: on the ground at <see cref="Position"/>, turned by <see cref="Yaw"/>.</summary>
public sealed record WorldObject(long Id, ObjectKind Kind, Vector3 Position, float Yaw)
{
    public ObjectDef Def => WorldObjects.Defs[(int)Kind];
    public Vector3 LightPosition => Position + new Vector3(0, Def.LightHeight, 0);
}

/// <summary>A point light for the shaders, with its flicker already applied.</summary>
public readonly record struct PointLight(Vector3 Position, Vector3 Color, float Radius);

/// <summary>
/// The objects lying in the world (for now only small glowing crystals). Some are scattered by the generator (deterministically, per
/// 64 m cell, generated as the player gets close); the player can pick any of them up and place
/// objects from the inventory. Picked-up generated objects are remembered, so they never come back.
/// </summary>
public sealed class WorldObjects
{
    public static readonly ObjectDef[] Defs =
    [
        new("Cristallo", new Vector3(0.62f, 0.42f, 1.0f), 2.4f, 10f, 0.65f, 1.3f, 0.35f),
    ];

    private const float CellSize = 64f;
    private const float ActiveRadius = 420f; // generated objects exist only within this distance

    private readonly TerrainField _terrain;
    private readonly int _seed;
    private readonly Dictionary<(int X, int Z), List<WorldObject>> _cells = new();
    private readonly List<WorldObject> _placed = new();
    private readonly HashSet<long> _taken = new(); // generated objects the player picked up
    private readonly List<(int X, int Z)> _toDrop = new();
    private long _nextPlacedId = -1; // placed objects use negative ids, generated ones positive

    public WorldObjects(TerrainField terrain, int seed)
    {
        _terrain = terrain;
        _seed = seed;
    }

    public IEnumerable<WorldObject> All => _cells.Values.SelectMany(c => c).Concat(_placed);

    /// <summary>Generates the cells around the player and forgets the far ones.</summary>
    public void Update(Vector3 player)
    {
        int radius = (int)MathF.Ceiling(ActiveRadius / CellSize);
        int pcx = (int)MathF.Floor(player.X / CellSize), pcz = (int)MathF.Floor(player.Z / CellSize);
        for (int cz = pcz - radius; cz <= pcz + radius; cz++)
        for (int cx = pcx - radius; cx <= pcx + radius; cx++)
            if (!_cells.ContainsKey((cx, cz)) && CellDistance(player, cx, cz) <= ActiveRadius)
                _cells[(cx, cz)] = Generate(cx, cz);

        _toDrop.Clear();
        foreach (var key in _cells.Keys)
            if (CellDistance(player, key.X, key.Z) > ActiveRadius + CellSize) _toDrop.Add(key);
        foreach (var key in _toDrop) _cells.Remove(key);
    }

    /// <summary>
    /// The object nearest along the ray whose pick capsule (a vertical segment from its base up to
    /// <see cref="ObjectDef.PickHeight"/>, thickened by <see cref="ObjectDef.PickRadius"/>) the ray
    /// passes through, within <paramref name="maxDistance"/>.
    /// </summary>
    public WorldObject? Pick(Vector3 origin, Vector3 direction, float maxDistance, out float distance)
    {
        WorldObject? best = null;
        distance = maxDistance;
        foreach (var obj in All)
        {
            var (t, miss) = ClosestToSegment(origin, direction, obj.Position, obj.Def.PickHeight);
            if (t < 0 || t > distance || miss > obj.Def.PickRadius) continue;
            distance = t;
            best = obj;
        }
        return best;
    }

    /// <summary>
    /// Closest approach between a ray and the vertical segment [bottom, bottom + height]:
    /// the ray parameter there and the distance between the two.
    /// </summary>
    private static (float T, float Distance) ClosestToSegment(Vector3 origin, Vector3 direction, Vector3 bottom, float height)
    {
        var w = origin - bottom;
        float b = direction.Y;           // dot(direction, up)
        float d = Vector3.Dot(direction, w);
        float e = w.Y;                   // dot(up, w)
        float denom = 1 - b * b;         // |direction| = |up| = 1
        float s = denom > 1e-5f ? Math.Clamp((e - b * d) / denom, 0, height) : Math.Clamp(e, 0, height);
        float t = Vector3.Dot(bottom + new Vector3(0, s, 0) - origin, direction);
        var onRay = origin + direction * MathF.Max(t, 0);
        var onSegment = bottom + new Vector3(0, Math.Clamp(onRay.Y - bottom.Y, 0, height), 0);
        return (t, Vector3.Distance(onRay, onSegment));
    }

    public void Remove(WorldObject obj)
    {
        if (obj.Id < 0)
        {
            _placed.Remove(obj);
            return;
        }
        _taken.Add(obj.Id);
        foreach (var list in _cells.Values)
            if (list.Remove(obj)) return;
    }

    public WorldObject Place(ObjectKind kind, Vector3 position, float yaw)
    {
        var obj = new WorldObject(_nextPlacedId--, kind, position, yaw);
        _placed.Add(obj);
        return obj;
    }

    /// <summary>
    /// The nearest lights, nearest first: the objects' (with torch flicker and crystal pulse applied)
    /// and any <paramref name="extra"/> ones, such as the glowing crystal clusters.
    /// </summary>
    public int CollectLights(Vector3 center, float time, Span<PointLight> lights, IEnumerable<PointLight> extra)
    {
        var candidates = All
            .Where(o => Vector3.DistanceSquared(o.LightPosition, center) < 150f * 150f)
            .Select(o => ObjectLight(o, time))
            .Concat(extra)
            .OrderBy(l => Vector3.DistanceSquared(l.Position, center))
            .Take(lights.Length);

        int count = 0;
        foreach (var light in candidates)
            lights[count++] = light;
        return count;
    }

    private static PointLight ObjectLight(WorldObject obj, float time)
    {
        var def = obj.Def;
        float phase = (obj.Id & 1023) * 0.618f;
        float flicker = 0.8f + 0.2f * MathF.Sin(time * 1.4f + phase); // crystals pulse slowly
        return new PointLight(obj.LightPosition, def.LightColor * def.LightIntensity * flicker, def.LightRadius);
    }

    /// <summary>How bright the object's own glowing parts are right now (matches its light).</summary>
    public static float Glow(WorldObject obj, float time)
    {
        float phase = (obj.Id & 1023) * 0.618f;
        return 0.8f + 0.2f * MathF.Sin(time * 1.4f + phase);
    }

    /// <summary>Crystals in small clusters, only on gentle ground.</summary>
    private List<WorldObject> Generate(int cx, int cz)
    {
        var list = new List<WorldObject>();
        uint h = Hash(cx, cz);
        var random = new Random((int)h);

        if (random.NextSingle() < 0.3f)
        {
            float x = (cx + 0.15f + 0.7f * random.NextSingle()) * CellSize;
            float z = (cz + 0.15f + 0.7f * random.NextSingle()) * CellSize;
            int cluster = 1 + random.Next(3);
            for (int i = 0; i < cluster; i++)
                TryAdd(list, cx, cz, i, ObjectKind.Crystal,
                    x + (random.NextSingle() - 0.5f) * 3f, z + (random.NextSingle() - 0.5f) * 3f, random.NextSingle() * MathF.Tau);
        }
        return list;
    }

    private void TryAdd(List<WorldObject> list, int cx, int cz, int index, ObjectKind kind, float x, float z, float yaw)
    {
        long id = ((long)(cx & 0xFFFFF) << 28) | ((long)(cz & 0xFFFFF) << 8) | (uint)index;
        id += 1; // keep generated ids positive and non-zero
        if (_taken.Contains(id) || _terrain.Normal(x, z).Y < 0.8f) return;
        list.Add(new WorldObject(id, kind, _terrain.TileCenter(x, z), yaw));
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
            uint h = (uint)(x * 73856093 ^ z * 19349663 ^ _seed * 83492791);
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }
}
