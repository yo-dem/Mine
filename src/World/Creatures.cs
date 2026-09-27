using System.Numerics;

namespace Mine.World;

public enum CreatureKind
{
    Butterfly,
    Bird,
    Fish,
    DeepFish,
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
/// birds of every size and shape (ragged V, drifting cloud, scattered knots, swirling ball) wheeling high in the
/// sky. Butterflies and fish that stray too far are respawned near the player; fish are born
/// only in open water, far from the shores, and only a few ever swim into the shallows. Birds fill the sky
/// by day (flocks fly in from afar in the morning and leave as the evening falls; only a lone
/// pair stays through the evening and night); butterflies
/// swarm at night.
/// </summary>
public sealed class Creatures
{
    private const int ButterflyCount = 90, DayButterflies = 30, FishCount = 28;
    private const int FlockCount = 7, NightFlocks = 1, MaxBirdsPerFlock = 12;
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

    // Deep water schools: glowing fish swimming well below the surface, to be seen when diving.
    // Each school keeps together around a centre that roams the deep water near the player.
    private const int DeepSchools = 3, MaxPerSchool = 6;
    private const float DeepRange = 38f, DeepSpawnDepth = 6f, DeepMinDepth = 4f;
    // Schools are born and roam only where the water stays deep all around (far from the shores),
    // and keep this far apart from each other.
    private const float DeepClearance = 18f, SchoolSpacing = 16f;
    private readonly Creature[] _deepFish = new Creature[DeepSchools * MaxPerSchool];
    private readonly Vector3[] _deepOffsets = new Vector3[DeepSchools * MaxPerSchool]; // (back, side, up) within the school, in fish lengths
    private readonly float[] _deepResponse = new float[DeepSchools * MaxPerSchool]; // how quickly each fish follows (its own lag)
    private readonly Vector3[] _deepHeading = new Vector3[DeepSchools * MaxPerSchool]; // where each fish points (horizontal)
    private readonly float[] _deepSpeed = new float[DeepSchools * MaxPerSchool];
    private readonly School[] _schools = new School[DeepSchools];

    private struct School
    {
        public Vector3 Position, Velocity;
        public Vector3 Heading; // horizontal unit vector, turned toward where the school wants to go at a limited rate
        public float Level;  // 0 = just over the bottom .. 1 = just under the surface
        public float Scale, Speed, Spread;
        public int Count;
        public bool Active;
    }
    private readonly Creature[] _birds = new Creature[FlockCount * MaxBirdsPerFlock];
    private readonly Vector3[] _birdSlots = new Vector3[FlockCount * MaxBirdsPerFlock]; // random in [-1, 1]³: each bird's own spot
    private readonly Vector3[] _birdQuirks = new Vector3[FlockCount * MaxBirdsPerFlock]; // random in [0, 1]³: arm, place in the V, straggling
    private readonly Flock[] _flocks = new Flock[FlockCount];

    // No formation is ever neat: real flocks are ragged, uneven and always reshaping.
    private enum Formation { RaggedVee, Cloud, Knots, Swirl }

    private struct Flock
    {
        public float Angle, Spin, Radius, Height, Spacing;
        public float Seed; // shapes the flock: its V's opening, where its knots sit
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
        int[] sizes = [2, 3, 4, 5, 7, 9, 12];
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
                Height = 40f + 25f * i + 15f * _random.NextSingle(),
                Spacing = 2.4f + 1.8f * _random.NextSingle(),
                Altitude = float.NaN,
                Presence = float.NaN,
                Count = Math.Min(sizes[i], MaxBirdsPerFlock),
                Formation = sizes[i] <= 2 ? Formation.Cloud : formations[i],
                Seed = _random.NextSingle() * 100f,
            };
        for (int i = 0; i < _birds.Length; i++)
        {
            var flock = _flocks[i / MaxBirdsPerFlock];
            _birds[i] = new Creature
            {
                Scale = 3.2f + 1.0f * _random.NextSingle(),
                Phase = _random.NextSingle() * 10f,
            };
            _birdSlots[i] = new Vector3(_random.NextSingle() * 2f - 1f, _random.NextSingle() * 2f - 1f, _random.NextSingle() * 2f - 1f);
            _birdQuirks[i] = new Vector3(_random.NextSingle(), _random.NextSingle(), _random.NextSingle());
        }
    }

    public ReadOnlySpan<Creature> Of(CreatureKind kind) => kind switch
    {
        CreatureKind.Butterfly => _butterflies,
        CreatureKind.Bird => _birds,
        CreatureKind.DeepFish => _deepFish,
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

        UpdateDeepFish(player, dt);

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
            // The flock slowly breathes: it stretches and squeezes, so its shape never settles.
            float breathe = 1f + 0.25f * MathF.Sin(_time * 0.13f + flock.Seed);
            float stretch = 1f + 0.35f * MathF.Sin(_time * 0.09f + flock.Seed * 1.7f);
            for (int j = 0; j < flock.Count; j++)
            {
                int index = k * MaxBirdsPerFlock + j;
                ref var bird = ref _birds[index];
                var slot = _birdSlots[index];
                var quirk = _birdQuirks[index];
                // Everyone wanders around their spot, each at their own slow pace.
                var drift = new Vector3(
                    MathF.Sin(_time * (0.3f + 0.3f * quirk.X) + bird.Phase),
                    MathF.Sin(_time * (0.4f + 0.3f * quirk.Y) + bird.Phase * 1.3f),
                    MathF.Sin(_time * (0.25f + 0.3f * quirk.Z) + bird.Phase * 0.7f)) * s * 0.45f;
                Vector3 offset;
                switch (flock.Formation)
                {
                    case Formation.RaggedVee:
                    {
                        // A lopsided V: arms of uneven length (each bird picks one at random, one arm
                        // favoured), birds out of step along them, gaps, and stragglers trailing behind.
                        float opening = 0.6f + 0.6f * Frac(flock.Seed);
                        float sideSign = quirk.X < 0.4f + 0.25f * Frac(flock.Seed * 3.1f) ? 1f : -1f;
                        float along = j == 0 ? 0f : (0.6f + 0.9f * quirk.Y) * (1f + j * 0.45f);
                        offset = -forward * along * s * stretch + side * sideSign * along * s * opening * breathe
                            + (side * slot.X + forward * slot.Y) * s * 0.9f;
                        if (quirk.Z > 0.8f) offset += -forward * (2f + 6f * (quirk.Z - 0.8f) / 0.2f) * s + side * slot.Z * s * 2f;
                        break;
                    }
                    case Formation.Knots:
                    {
                        // Two or three loose knots of birds that drift apart and together.
                        int knots = 2 + (int)(Frac(flock.Seed * 5.3f) * 2f);
                        int knot = (int)(quirk.X * knots);
                        float a = flock.Seed * 2.9f + knot * 2.4f + _time * 0.05f;
                        var knotCenter = (-forward * MathF.Cos(a) + side * MathF.Sin(a)) * s * (3f + 2f * knot) * breathe;
                        offset = knotCenter + (-forward * slot.X + side * slot.Y) * s * (1.2f + quirk.Y);
                        break;
                    }
                    case Formation.Swirl:
                    {
                        // A ball of birds circling around each other.
                        float a = MathF.Atan2(slot.Y, slot.X) + _time * (0.25f + 0.2f * quirk.Y) * (quirk.Z < 0.5f ? 1f : -1f);
                        float r = s * (1f + 2.2f * MathF.Sqrt(quirk.X)) * breathe;
                        offset = (-forward * MathF.Cos(a) + side * MathF.Sin(a)) * r;
                        break;
                    }
                    default:
                        // A loose cloud, longer or wider as it breathes, each bird at its own spot.
                        offset = -forward * slot.X * s * 2.6f * stretch + side * slot.Y * s * 3f * breathe;
                        break;
                }
                offset += side * drift.X + forward * drift.Z;
                offset.Y = drift.Y + slot.Z * s * 0.8f;
                bird.Position = center + offset;
                bird.Velocity = heading;
            }
        }
    }

    /// <summary>
    /// Schools in the deep. Each school is a point that roams near the player at its own level
    /// between the bottom and the surface, meandering gently and turning (at a limited rate, never
    /// abruptly) away from the other schools and toward deep water well before the bottom rises.
    /// Its fish are not tied to it: each swims on its own, with its own inertia and lag, easing
    /// toward a loose place around the school that slowly drifts, and keeping clear of the others;
    /// so they glide like the fish at the surface, each a little out of step. A school that
    /// strays too far (or finds no way on) respawns in deep water.
    /// </summary>
    private void UpdateDeepFish(Vector3 player, float dt)
    {
        const float water = TerrainField.WaterLevel;
        const float TurnRate = 0.3f; // radians per second
        for (int k = 0; k < DeepSchools; k++)
        {
            ref var school = ref _schools[k];
            if (!school.Active || HorizontalDistance(school.Position, player) > DeepRange)
            {
                school.Active = false; // so it does not keep itself away from its own old place
                school.Active = TrySpawn(player, 8f, DeepRange * 0.85f, AcceptSchool, out var p);
                for (int j = 0; j < MaxPerSchool; j++) _deepFish[k * MaxPerSchool + j].Active = false;
                if (!school.Active) continue;
                school.Level = 0.15f + 0.6f * _random.NextSingle();
                school.Heading = RandomHorizontal();
                school.Scale = 1.3f + 1.4f * _random.NextSingle();
                school.Speed = 0.7f + 0.6f * _random.NextSingle();
                school.Spread = 1f + 0.3f * _random.NextSingle();
                school.Count = 3 + _random.Next(MaxPerSchool - 2);
                school.Position = new Vector3(p.X, float.Lerp(p.Y + 1.2f, water - 1.5f, school.Level), p.Z);
                school.Velocity = school.Heading * school.Speed;
                for (int j = 0; j < MaxPerSchool; j++)
                {
                    int index = k * MaxPerSchool + j;
                    _deepOffsets[index] = SchoolSlot(k, j);
                    _deepResponse[index] = 0.5f + 0.7f * _random.NextSingle();
                    _deepFish[index] = new Creature
                    {
                        Active = j < school.Count,
                        Scale = school.Scale * (0.8f + 0.4f * _random.NextSingle()),
                        Phase = k * 20f + _random.NextSingle() * 10f, // whole school: same floor(phase / 20), same glow
                    };
                    _deepFish[index].Position = SchoolTarget(school, index, _deepFish[index].Phase);
                    _deepFish[index].Velocity = school.Velocity;
                    _deepHeading[index] = school.Heading;
                    _deepSpeed[index] = school.Speed;
                }
            }

            // Where the school would like to go: a gentle meander, away from the other schools.
            var heading = school.Heading;
            float meander = 0.35f * MathF.Sin(_time * 0.09f + k * 2.3f) + 0.2f * MathF.Sin(_time * 0.23f + k * 5.1f);
            var desired = Rotate(heading, meander);
            for (int o = 0; o < DeepSchools; o++)
            {
                if (o == k || !_schools[o].Active) continue;
                var away = school.Position - _schools[o].Position;
                away.Y = 0;
                float distance = away.Length();
                if (distance < SchoolSpacing && distance > 1e-3f) desired += away / distance * (SchoolSpacing - distance) / SchoolSpacing * 2f;
            }
            desired = Vector3.Normalize(desired + new Vector3(1e-4f, 0, 0));
            // Deep water ahead and on both flanks (the school is a few metres wide), or look for it.
            var flank = new Vector3(-desired.Z, 0, desired.X) * (4f * school.Scale);
            if (!IsDeep(school.Position + desired * 12f, DeepMinDepth + 1f)
                || !IsDeep(school.Position + desired * 4f + flank, DeepMinDepth) || !IsDeep(school.Position + desired * 4f - flank, DeepMinDepth))
            {
                if (!TryDeepHeading(school.Position, heading, DeepMinDepth + 1f, out var deep, 12f)) { school.Active = false; continue; }
                desired = deep;
            }
            // Turn toward it at a limited rate.
            float turn = MathF.Atan2(heading.X * desired.Z - heading.Z * desired.X, Vector3.Dot(heading, desired));
            school.Heading = Rotate(heading, Math.Clamp(turn, -TurnRate * dt, TurnRate * dt));

            // Keep to the school's level: the whole school (up to ~0.7 fish lengths above and below
            // its centre) stays clear of both the bottom and the surface, easing up and down.
            float bottom = _terrain.Height(school.Position.X, school.Position.Z);
            if (water - bottom < DeepMinDepth * 0.6f) { school.Active = false; continue; } // wedged in the shallows
            float halfHeight = 0.7f * 0.9f * school.Scale * school.Spread + 0.3f;
            float targetY = float.Lerp(bottom + 0.8f + halfHeight, water - 1.2f - halfHeight, school.Level);
            float vy = Math.Clamp((targetY - school.Position.Y) * 0.3f, -0.4f, 0.4f);
            school.Velocity = school.Heading * school.Speed + new Vector3(0, vy, 0);
            school.Position += school.Velocity * dt;

            float length = 0.9f * school.Scale;
            for (int j = 0; j < school.Count; j++)
            {
                int index = k * MaxPerSchool + j;
                ref var fish = ref _deepFish[index];
                // Ease toward its place, match the school's pace, and keep clear of the others.
                var desiredVelocity = school.Velocity + (SchoolTarget(school, index, fish.Phase) - fish.Position) * 0.35f;
                for (int i = 0; i < school.Count; i++)
                {
                    if (i == j) continue;
                    var away = fish.Position - _deepFish[k * MaxPerSchool + i].Position;
                    float distance = away.Length();
                    if (distance < 1.6f * length && distance > 1e-3f) desiredVelocity += away / distance * (1.6f * length - distance) * 2f;
                }
                // A fish never stops nor spins on the spot: it turns its heading toward where it wants
                // to go at a limited rate, and only changes its pace (between 40% and 180% of the
                // school's), easing its climb or dive.
                float response = 1f - MathF.Exp(-_deepResponse[index] * dt);
                ref var fishHeading = ref _deepHeading[index];
                var flat = new Vector3(desiredVelocity.X, 0, desiredVelocity.Z);
                if (flat.LengthSquared() > 0.01f)
                {
                    flat = Vector3.Normalize(flat);
                    float fishTurn = MathF.Atan2(fishHeading.X * flat.Z - fishHeading.Z * flat.X, Vector3.Dot(fishHeading, flat));
                    float maxTurn = 1f * dt;
                    fishHeading = Rotate(fishHeading, Math.Clamp(fishTurn * response * 8f, -maxTurn, maxTurn));
                }
                float pace = Math.Clamp(Vector3.Dot(desiredVelocity, fishHeading), school.Speed * 0.4f, school.Speed * 1.8f);
                _deepSpeed[index] += (pace - _deepSpeed[index]) * response;
                float climb = fish.Velocity.Y + (Math.Clamp(desiredVelocity.Y, -0.5f, 0.5f) - fish.Velocity.Y) * response;
                fish.Velocity = fishHeading * _deepSpeed[index] + new Vector3(0, climb, 0);
                fish.Position += fish.Velocity * dt;
                // Should two still come too close, nudge them apart gently (never into each other).
                for (int i = 0; i < j; i++)
                {
                    ref var other = ref _deepFish[k * MaxPerSchool + i];
                    var away = fish.Position - other.Position;
                    float distance = away.Length();
                    if (distance >= length || distance < 1e-3f) continue;
                    var push = away / distance * MathF.Min(length - distance, 0.5f * dt * 4f) * 0.5f;
                    fish.Position += push;
                    other.Position -= push;
                }
                // Never through the bottom nor out of the water.
                float floor = _terrain.Height(fish.Position.X, fish.Position.Z) + 0.4f;
                if (fish.Position.Y < floor) { fish.Position.Y = floor; fish.Velocity.Y = MathF.Max(fish.Velocity.Y, 0f); }
                if (fish.Position.Y > water - 0.8f) { fish.Position.Y = water - 0.8f; fish.Velocity.Y = MathF.Min(fish.Velocity.Y, 0f); }
            }
        }
    }

    /// <summary>A fish's place in its school: its slot behind/beside/above the centre, slowly drifting.</summary>
    private Vector3 SchoolTarget(in School school, int index, float phase)
    {
        var o = _deepOffsets[index];
        o += new Vector3(MathF.Sin(_time * 0.11f + phase), MathF.Sin(_time * 0.08f + phase * 1.7f), MathF.Sin(_time * 0.13f + phase * 0.6f)) * 0.35f;
        var side = new Vector3(-school.Heading.Z, 0, school.Heading.X);
        float spacing = 0.9f * school.Scale * school.Spread; // offsets are in fish lengths (the mesh is ~0.9 long)
        return school.Position + (-school.Heading * o.X + side * o.Y) * spacing + new Vector3(0, o.Z * spacing, 0);
    }

    /// <summary>A horizontal vector turned by <paramref name="angle"/> radians around the vertical.</summary>
    private static Vector3 Rotate(Vector3 v, float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return new Vector3(v.X * c - v.Z * s, 0, v.X * s + v.Z * c);
    }

    /// <summary>
    /// A school's place for fish <paramref name="j"/>: random within a loose box (in fish lengths),
    /// but at least <c>MinGap</c> from the places before it, so the fish never overlap.
    /// </summary>
    private Vector3 SchoolSlot(int school, int j)
    {
        const float MinGap = 1.4f;
        Vector3 best = default;
        float bestGap = -1f;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            var candidate = new Vector3((_random.NextSingle() * 2f - 1f) * 2.4f, (_random.NextSingle() * 2f - 1f) * 1.8f, (_random.NextSingle() * 2f - 1f) * 0.7f);
            float gap = float.MaxValue;
            for (int i = 0; i < j; i++) gap = MathF.Min(gap, Vector3.Distance(candidate, _deepOffsets[school * MaxPerSchool + i]));
            if (gap >= MinGap) return candidate;
            if (gap > bestGap) (bestGap, best) = (gap, candidate);
        }
        return best;
    }

    /// <summary>Schools spawn only in deep water that stays deep for <see cref="DeepClearance"/> metres all around.</summary>
    private bool AcceptSchool(float x, float z, float h)
    {
        if (TerrainField.WaterLevel - h < DeepSpawnDepth) return false;
        for (int k = 0; k < 8; k++)
        {
            float a = k * MathF.Tau / 8f;
            for (float r = DeepClearance * 0.5f; r <= DeepClearance; r += DeepClearance * 0.5f)
                if (!IsDeep(new Vector3(x + MathF.Cos(a) * r, 0, z + MathF.Sin(a) * r), DeepMinDepth)) return false;
        }
        for (int o = 0; o < DeepSchools; o++)
            if (_schools[o].Active && HorizontalDistance(_schools[o].Position, new Vector3(x, 0, z)) < SchoolSpacing) return false;
        return true;
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
    private bool TryDeepHeading(Vector3 p, Vector3 forward, float depth, out Vector3 heading, float reach = 5f)
    {
        ReadOnlySpan<float> angles = [0.5f, -0.5f, 1f, -1f, 1.6f, -1.6f, 2.3f, -2.3f, MathF.PI];
        float side = _random.NextSingle() < 0.5f ? 1f : -1f; // no bias toward one side
        foreach (float angle in angles)
        {
            float a = angle * side, c = MathF.Cos(a), s = MathF.Sin(a);
            var d = new Vector3(forward.X * c - forward.Z * s, 0, forward.X * s + forward.Z * c);
            if (IsDeep(p + d * (reach * 0.5f), depth) && IsDeep(p + d * reach, depth)) { heading = d; return true; }
        }
        heading = default;
        return false;
    }

    private static float Frac(float x) => x - MathF.Floor(x);

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
