using System.Numerics;

namespace Mine.World;

public enum CreatureKind
{
    Butterfly,
    Bird,
    Fish,
}

/// <summary>One creature: where it is, where it is heading, its size, and a phase that desynchronises its animation.</summary>
public struct Creature
{
    public Vector3 Position;
    public Vector3 Velocity;
    public float Scale;
    public float Phase;
    public bool Active;
}

/// <summary>
/// The living things around the player, simulated on the CPU (they are few):
/// glowing butterflies meandering over the land, fish gliding through open water, and flocks of
/// birds of every size and shape (V, slanted line, single file, loose cloud) wheeling high in the
/// sky. Butterflies and fish that stray too far are respawned near the player; fish are born
/// only in open water, far from the shores, and only a few ever swim into the shallows. Birds fill the sky
/// by day (flocks fly in from afar in the morning and leave as the evening falls; only a lone
/// pair stays through the evening and night); butterflies
/// swarm at night.
/// </summary>
public sealed class Creatures
{
    private const int ButterflyCount = 170, DayButterflies = 30, FishCount = 28;
    private const int FlockCount = 12, NightFlocks = 1, MaxBirdsPerFlock = 24;
    private const float ButterflyRange = 32f, FishRange = 30f;
    // Fish are born in open water: deep, and with deep water all around (FishSpawnClearance metres
    // each way). Most keep to water at least FishMinDepth deep; only one in BraveFishEvery ventures
    // into the shallows, down to BraveFishMinDepth.
    private const float FishSpawnDepth = 5f, FishSpawnClearance = 12f, FishMinDepth = 3f, BraveFishMinDepth = 1f;
    private const int BraveFishEvery = 10;
    private const float FlockAwayDistance = 700f; // how far away a flock that has left the sky wheels

    private readonly TerrainField _terrain;
    private readonly Random _random = new(777);
    private readonly Creature[] _butterflies = new Creature[ButterflyCount];
    private readonly Creature[] _fish = new Creature[FishCount];
    private readonly float[] _fishMinDepth = new float[FishCount];
    private readonly Creature[] _birds = new Creature[FlockCount * MaxBirdsPerFlock];
    private readonly Vector3[] _birdSlots = new Vector3[FlockCount * MaxBirdsPerFlock]; // (back, side, up) in a loose cloud
    private readonly Flock[] _flocks = new Flock[FlockCount];

    private enum Formation { Vee, Echelon, Line, Cloud }

    private struct Flock
    {
        public float Angle, Spin, Radius, Height, Spacing;
        public float Altitude; // eased toward the ground below plus Height, NaN until first placed
        public float Presence; // 1 = wheeling around the player, 0 = gone far away (and hidden)
        public int Count;
        public Formation Formation;
    }
    private Vector3 _flockAnchor = new(float.NaN);
    private float _time;

    public Creatures(TerrainField terrain)
    {
        _terrain = terrain;
        // Each flock has its own size (from a pair to a big wedge), shape, spacing, orbit and direction.
        // The first flock is the one that stays through the evening and night: a lone pair.
        int[] sizes = [2, 3, 4, 5, 7, 8, 10, 12, 14, 17, 20, 24];
        _random.Shuffle(sizes.AsSpan(1));
        var formations = new Formation[FlockCount];
        for (int i = 0; i < FlockCount; i++) formations[i] = (Formation)(i % 4);
        _random.Shuffle(formations);
        for (int i = 0; i < FlockCount; i++)
            _flocks[i] = new Flock
            {
                Angle = _random.NextSingle() * MathF.Tau,
                Spin = (0.04f + 0.07f * _random.NextSingle()) * (_random.NextSingle() < 0.5f ? -1f : 1f),
                Radius = 50f + 170f * _random.NextSingle(),
                Height = 40f + 16f * i + 15f * _random.NextSingle(),
                Spacing = 2.4f + 1.8f * _random.NextSingle(),
                Altitude = float.NaN,
                Presence = float.NaN,
                Count = Math.Min(sizes[i], MaxBirdsPerFlock),
                Formation = sizes[i] <= 2 ? Formation.Cloud : formations[i],
            };
        for (int i = 0; i < _birds.Length; i++)
        {
            var flock = _flocks[i / MaxBirdsPerFlock];
            _birds[i] = new Creature
            {
                Scale = 3.2f + 1.0f * _random.NextSingle(),
                Phase = _random.NextSingle() * 10f,
            };
            _birdSlots[i] = new Vector3(_random.NextSingle() * 4f, _random.NextSingle() * 2f - 1f, _random.NextSingle() * 2f - 1f);
        }
    }

    public ReadOnlySpan<Creature> Of(CreatureKind kind) => kind switch
    {
        CreatureKind.Butterfly => _butterflies,
        CreatureKind.Bird => _birds,
        _ => _fish,
    };

    public void Update(Vector3 player, in Atmosphere atmosphere, float dt)
    {
        float night = atmosphere.Night;
        // Birds want the full light: they leave as soon as the sun starts sinking in the evening.
        float daylight = Math.Clamp((atmosphere.SunDirection.Y - 0.2f) / 0.16f, 0f, 1f);
        _time += dt;
        const float water = TerrainField.WaterLevel;

        // Butterflies beyond the wanted count are not respawned once they stray, so they thin out gently.
        int butterflies = (int)float.Lerp(DayButterflies, ButterflyCount, night);
        for (int i = 0; i < _butterflies.Length; i++)
        {
            ref var b = ref _butterflies[i];
            if (!b.Active || HorizontalDistance(b.Position, player) > ButterflyRange)
            {
                if (i >= butterflies) { b.Active = false; continue; }
                b.Active = TrySpawn(player, 6f, ButterflyRange * 0.8f, (x, z, h) => h > water + 0.3f, out var p);
                if (!b.Active) continue;
                b.Position = p + new Vector3(0, 0.6f + 1.8f * _random.NextSingle(), 0);
                b.Velocity = RandomHorizontal() * 1.2f;
                b.Scale = 1.2f + 0.8f * _random.NextSingle();
                b.Phase = _random.NextSingle() * 10f;
            }
            // Meander: a gentle random steer, and float at a fluttering height over the ground.
            // They shy away from the player, so none flutters right in front of the eyes.
            b.Velocity += RandomHorizontal() * 2.5f * dt;
            var away = new Vector3(b.Position.X - player.X, 0, b.Position.Z - player.Z);
            float near = away.Length();
            if (near < 1.5f) { b.Active = false; continue; }
            if (near < 4f) b.Velocity += away / near * (4f - near) * 3f * dt;
            float speed = b.Velocity.Length();
            if (speed > 1.6f) b.Velocity *= 1.6f / speed;
            float ground = MathF.Max(_terrain.Height(b.Position.X, b.Position.Z), water);
            float target = ground + 1.2f + 0.8f * MathF.Sin(b.Phase + b.Position.X * 0.3f);
            b.Velocity.Y += (target - b.Position.Y) * 1.5f * dt - b.Velocity.Y * 0.8f * dt;
            b.Position += b.Velocity * dt;
        }

        for (int i = 0; i < _fish.Length; i++)
        {
            ref var f = ref _fish[i];
            if (!f.Active || HorizontalDistance(f.Position, player) > FishRange)
            {
                f.Active = TrySpawn(player, 6f, FishRange * 0.9f, AcceptFish, out var p);
                if (!f.Active) continue;
                f.Position = new Vector3(p.X, water - 0.35f - 0.2f * _random.NextSingle(), p.Z);
                f.Velocity = RandomHorizontal() * 1.5f;
                f.Scale = 1.0f + 0.9f * _random.NextSingle();
                f.Phase = _random.NextSingle() * 10f;
                _fishMinDepth[i] = _random.Next(BraveFishEvery) == 0 ? BraveFishMinDepth : FishMinDepth;
            }
            // Glide in wide curves. When the water ahead gets shallower than the fish likes, turn
            // toward the nearest heading that stays deep; a fish that finds none (or still ends up
            // in the shallows) swims off and respawns in open water.
            float minDepth = _fishMinDepth[i];
            if (water - _terrain.Height(f.Position.X, f.Position.Z) < minDepth * 0.5f) { f.Active = false; continue; }
            f.Velocity += RandomHorizontal() * 1.2f * dt;
            var forward = Vector3.Normalize(f.Velocity + new Vector3(1e-4f, 0, 0));
            if (!IsDeep(f.Position + forward * 5f, minDepth))
            {
                if (!TryDeepHeading(f.Position, forward, minDepth, out var heading)) { f.Active = false; continue; }
                var turned = Vector3.Normalize(Vector3.Lerp(forward, heading, 1f - MathF.Exp(-4f * dt)) + new Vector3(1e-4f, 0, 0));
                f.Velocity = turned * f.Velocity.Length();
            }
            float speed = f.Velocity.Length();
            if (speed > 1.8f) f.Velocity *= 1.8f / speed;
            if (speed < 0.6f) f.Velocity *= 0.6f / MathF.Max(speed, 1e-3f);
            f.Position += f.Velocity * dt;
        }

        // Flocks wheel around an anchor that drifts after the player, keeping them in view.
        if (float.IsNaN(_flockAnchor.X)) _flockAnchor = player;
        _flockAnchor = Vector3.Lerp(_flockAnchor, player, 1f - MathF.Exp(-0.05f * dt));
        float flocks = float.Lerp(NightFlocks, FlockCount, daylight * daylight * (3f - 2f * daylight));
        for (int k = 0; k < FlockCount; k++)
        {
            ref var flock = ref _flocks[k];
            // Flocks arrive from afar at dawn and fly off at dusk (spread out, each on its own cue, as the sun sinks),
            // widening their orbit far beyond the view instead of popping in or out.
            float wanted = k < flocks ? 1f : 0f;
            flock.Presence = float.IsNaN(flock.Presence) ? wanted
                : flock.Presence + (wanted - flock.Presence) * (1f - MathF.Exp(-0.04f * dt));
            bool visible = flock.Presence > 0.02f;
            for (int j = 0; j < MaxBirdsPerFlock; j++)
                _birds[k * MaxBirdsPerFlock + j].Active = visible && j < flock.Count;
            if (!visible) continue;
            float away = 1f - flock.Presence;
            flock.Angle += flock.Spin * dt;
            float angle = flock.Angle, spin = flock.Spin;
            float radius = flock.Radius + away * away * FlockAwayDistance, height = flock.Height + away * 60f;
            var center = _flockAnchor + new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
            // Follow the smooth land shape, eased, never the layered tiles: their half-metre steps
            // would make the whole flock jump.
            float target = MathF.Max(_terrain.SmoothHeight(center.X, center.Z), water) + height;
            flock.Altitude = float.IsNaN(flock.Altitude) ? target : flock.Altitude + (target - flock.Altitude) * (1f - MathF.Exp(-0.3f * dt));
            center.Y = flock.Altitude;
            var heading = new Vector3(-MathF.Sin(angle), 0, MathF.Cos(angle)) * spin * radius;
            var forward = Vector3.Normalize(heading);
            var side = new Vector3(-forward.Z, 0, forward.X);
            float s = flock.Spacing;
            for (int j = 0; j < flock.Count; j++)
            {
                int index = k * MaxBirdsPerFlock + j;
                ref var bird = ref _birds[index];
                var slot = _birdSlots[index];
                int row = (j + 1) / 2;
                float sideSign = j % 2 == 0 ? 1f : -1f;
                // Nobody keeps a perfect place: every bird drifts a little around its slot.
                var drift = new Vector3(MathF.Sin(_time * 0.5f + bird.Phase), MathF.Sin(_time * 0.7f + bird.Phase * 1.3f),
                    MathF.Sin(_time * 0.4f + bird.Phase * 0.7f)) * 0.6f;
                var offset = flock.Formation switch
                {
                    // A V: the leader in front, the others trailing on alternate sides, one arm longer.
                    Formation.Vee => -forward * row * s * 1.2f + side * sideSign * row * s * (sideSign > 0 ? 1f : 0.8f),
                    // A single slanted line trailing off to one side.
                    Formation.Echelon => (-forward * 1.1f + side * 0.9f) * j * s,
                    // Single file, gently snaking.
                    Formation.Line => -forward * j * s * 1.3f + side * MathF.Sin(j * 0.9f + _time * 0.3f) * s * 0.6f,
                    // A loose cloud, each bird at its own random spot.
                    _ => -forward * slot.X * s * 1.5f + side * slot.Y * s * 2.5f,
                };
                offset += side * drift.X + forward * drift.Z;
                offset.Y = drift.Y + slot.Z * s * 0.4f;
                bird.Position = center + offset;
                bird.Velocity = heading;
            }
        }
    }

    /// <summary>Fish spawn only in open water: deep, and deep all around, far from any shore.</summary>
    private bool AcceptFish(float x, float z, float h)
    {
        if (TerrainField.WaterLevel - h < FishSpawnDepth) return false;
        for (int k = 0; k < 8; k++)
        {
            float a = k * MathF.Tau / 8f;
            for (float r = FishSpawnClearance * 0.5f; r <= FishSpawnClearance; r += FishSpawnClearance * 0.5f)
                if (!IsDeep(new Vector3(x + MathF.Cos(a) * r, 0, z + MathF.Sin(a) * r), FishMinDepth)) return false;
        }
        return true;
    }

    private bool IsDeep(Vector3 p, float depth) => TerrainField.WaterLevel - _terrain.Height(p.X, p.Z) >= depth;

    /// <summary>The heading closest to <paramref name="forward"/> along which the water stays deep for a few metres.</summary>
    private bool TryDeepHeading(Vector3 p, Vector3 forward, float depth, out Vector3 heading)
    {
        ReadOnlySpan<float> angles = [0.5f, -0.5f, 1f, -1f, 1.6f, -1.6f, 2.3f, -2.3f, MathF.PI];
        float side = _random.NextSingle() < 0.5f ? 1f : -1f; // no bias toward one side
        foreach (float angle in angles)
        {
            float a = angle * side, c = MathF.Cos(a), s = MathF.Sin(a);
            var d = new Vector3(forward.X * c - forward.Z * s, 0, forward.X * s + forward.Z * c);
            if (IsDeep(p + d * 2.5f, depth) && IsDeep(p + d * 5f, depth)) { heading = d; return true; }
        }
        heading = default;
        return false;
    }

    private bool TrySpawn(Vector3 player, float minDistance, float maxDistance, Func<float, float, float, bool> accept, out Vector3 position)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            float a = _random.NextSingle() * MathF.Tau;
            float r = minDistance + (maxDistance - minDistance) * _random.NextSingle();
            float x = player.X + MathF.Cos(a) * r, z = player.Z + MathF.Sin(a) * r;
            float h = _terrain.Height(x, z);
            if (!accept(x, z, h)) continue;
            position = new Vector3(x, h, z);
            return true;
        }
        position = default;
        return false;
    }

    private Vector3 RandomHorizontal()
    {
        float a = _random.NextSingle() * MathF.Tau;
        return new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();
}
