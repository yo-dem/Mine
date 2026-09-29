using System.Numerics;

namespace Mine.World;

/// <summary>A small cube to draw: which model (<see cref="Debris.ChipModel"/> or a material's), where, how turned, how big, how tinted.</summary>
public readonly record struct DebrisCube(int Model, Vector3 Position, Quaternion Rotation, float Size, Vector3 Tint);

/// <summary>
/// What flies off the things the player breaks: chips (tiny tinted cubes that spray from where the
/// blow lands, bounce and shrink away) and, when something breaks, cubes of its material that
/// scatter, settle and then float spinning over the ground until the player comes near: then they
/// are drawn to the player and go into the inventory (if there is room).
/// </summary>
public sealed class Debris
{
    public const int ChipModel = 0;
    public static int ModelOf(Resource resource) => 1 + (int)resource;

    private const float Gravity = 14f;
    private const float DropSize = 0.28f;
    private const float AttractRadius = 5f;   // from the player's chest
    private const float CollectRadius = 1.1f;
    private const float AttractSpeed = 18f;

    private struct Chip
    {
        public Vector3 Position, Velocity, Axis, Color;
        public float Angle, Spin, Size, Life, Age;
    }

    private sealed class Drop
    {
        public Resource Resource;
        public Vector3 Position, Velocity;
        public float Angle, Age, Phase;
        public bool Resting, Attracted;
    }

    private readonly List<Chip> _chips = new();
    private readonly List<Drop> _drops = new();
    private readonly Random _random = new();

    private Vector3 RandomUnit()
    {
        var v = new Vector3(_random.NextSingle() * 2 - 1, _random.NextSingle() * 2 - 1, _random.NextSingle() * 2 - 1);
        return v.LengthSquared() > 1e-4f ? Vector3.Normalize(v) : Vector3.UnitY;
    }

    private Vector3 Horizontal()
    {
        float a = _random.NextSingle() * MathF.Tau;
        return new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
    }

    /// <summary>A spray of chips from where a blow lands, mostly back toward <paramref name="toward"/> (the player).</summary>
    public void Chips(Vector3 at, Vector3 toward, int count, Vector3 color, Vector3 color2)
    {
        for (int i = 0; i < count; i++)
        {
            var direction = Vector3.Normalize(toward * 1.1f + RandomUnit() * 0.9f + Vector3.UnitY * 0.7f);
            AddChip(at, direction * (2f + 3f * _random.NextSingle()), 0.035f + 0.05f * _random.NextSingle(),
                0.5f + 0.6f * _random.NextSingle(), _random.NextSingle() < 0.35f ? color2 : color);
        }
    }

    /// <summary>The burst of a whole thing breaking: chips thrown out from its body (from the foot up to <paramref name="height"/>).</summary>
    public void Burst(Vector3 foot, float height, float radius, int count, Vector3 color, Vector3 color2)
    {
        for (int i = 0; i < count; i++)
        {
            var outward = Horizontal();
            var at = foot + outward * radius * _random.NextSingle() + Vector3.UnitY * height * _random.NextSingle();
            var velocity = outward * (2f + 3f * _random.NextSingle()) + Vector3.UnitY * (2f + 4f * _random.NextSingle());
            AddChip(at, velocity, 0.05f + 0.09f * _random.NextSingle(), 0.8f + 0.8f * _random.NextSingle(),
                _random.NextSingle() < 0.5f ? color2 : color);
        }
    }

    private void AddChip(Vector3 at, Vector3 velocity, float size, float life, Vector3 color) => _chips.Add(new Chip
    {
        Position = at,
        Velocity = velocity,
        Axis = RandomUnit(),
        Spin = 6f + 10f * _random.NextSingle(),
        Size = size,
        Life = life,
        Color = color * (0.85f + 0.3f * _random.NextSingle()),
    });

    /// <summary><paramref name="count"/> cubes of a material thrown out of a broken thing.</summary>
    public void Spill(Resource resource, int count, Vector3 foot, float height)
    {
        for (int i = 0; i < count; i++)
        {
            var outward = Horizontal();
            _drops.Add(new Drop
            {
                Resource = resource,
                Position = foot + outward * 0.3f + Vector3.UnitY * (0.4f + height * _random.NextSingle()),
                Velocity = outward * (1.5f + 2f * _random.NextSingle()) + Vector3.UnitY * (2.5f + 2f * _random.NextSingle()),
                Angle = _random.NextSingle() * MathF.Tau,
                Phase = _random.NextSingle() * MathF.Tau,
            });
        }
    }

    public List<DropSave> Save() => _drops.Select(d => new DropSave(d.Resource, d.Position.X, d.Position.Y, d.Position.Z)).ToList();

    /// <summary>Restores the cubes left lying on the ground.</summary>
    public void Load(IEnumerable<DropSave> drops)
    {
        _drops.Clear();
        foreach (var d in drops)
            _drops.Add(new Drop { Resource = d.Resource, Position = new Vector3(d.X, d.Y, d.Z), Resting = true, Age = 10f, Phase = _random.NextSingle() * MathF.Tau });
    }

    /// <summary>Moves everything; cubes near the player fly to them and into the inventory. Returns how many were picked up.</summary>
    public int Update(float dt, Ground ground, Vector3 feet, Inventory inventory)
    {
        for (int i = _chips.Count - 1; i >= 0; i--)
        {
            var c = _chips[i];
            c.Age += dt;
            if (c.Age >= c.Life)
            {
                _chips.RemoveAt(i);
                continue;
            }
            Fall(ref c.Position, ref c.Velocity, c.Size, ground, dt, out _);
            c.Angle += c.Spin * dt;
            _chips[i] = c;
        }

        int collected = 0;
        var chest = feet + new Vector3(0, 1f, 0);
        for (int i = _drops.Count - 1; i >= 0; i--)
        {
            var d = _drops[i];
            d.Age += dt;
            var toPlayer = chest - d.Position;
            float distance = toPlayer.Length();
            // Drawn in from a few metres, as soon as it has settled (or after a moment in the air).
            if (!d.Attracted && (d.Resting || d.Age > 0.8f) && distance < AttractRadius && inventory.HasRoomFor(d.Resource)) d.Attracted = true;
            if (d.Attracted)
            {
                // Drawn to the player, faster and faster.
                var target = toPlayer / MathF.Max(distance, 1e-3f) * AttractSpeed;
                d.Velocity = Vector3.Lerp(d.Velocity, target, 1f - MathF.Exp(-14f * dt));
                d.Position += d.Velocity * dt;
                d.Angle += 10f * dt;
                if (distance < CollectRadius && inventory.Add(d.Resource, 1))
                {
                    _drops.RemoveAt(i);
                    collected++;
                    continue;
                }
                if (distance > AttractRadius * 3f || !inventory.HasRoomFor(d.Resource))
                {
                    d.Attracted = false;
                    d.Resting = false;
                }
                continue;
            }
            if (!d.Resting)
            {
                Fall(ref d.Position, ref d.Velocity, DropSize, ground, dt, out bool landed);
                if (landed && d.Velocity.LengthSquared() < 0.5f)
                {
                    d.Resting = true;
                    d.Velocity = Vector3.Zero;
                }
            }
            d.Angle += (d.Resting ? 1.6f : 6f) * dt;
        }
        return collected;
    }

    // Gravity, bounces off the blocks' sides and ceilings, and bounces on the ground that sap the speed.
    private static void Fall(ref Vector3 position, ref Vector3 velocity, float size, Ground ground, float dt, out bool landed)
    {
        velocity.Y -= Gravity * dt;
        // Sideways first: into a block's side, it bounces back off it.
        var moved = position + new Vector3(velocity.X, 0, velocity.Z) * dt;
        if (!ground.Solid(position) && ground.Solid(moved))
        {
            velocity.X *= -0.3f;
            velocity.Z *= -0.3f;
        }
        else position = moved;
        // Then up or down: rising into a block, it stops under it (falling, the ground catches it).
        float y = position.Y + velocity.Y * dt;
        if (velocity.Y > 0 && !ground.Solid(position) && ground.Solid(position with { Y = y + size / 2 })) velocity.Y = 0;
        else position.Y = y;
        // Only blocks under the cube hold it up (one whose bottom is above it is a wall or a ceiling:
        // standing it on that block's top would lift it through the rock).
        float floor = ground.Height(position.X, position.Z, position.Y, blockRadius: 0f) + size / 2;
        landed = position.Y <= floor;
        if (!landed) return;
        position.Y = floor;
        if (velocity.Y < 0) velocity.Y *= -0.3f;
        velocity.X *= 0.55f;
        velocity.Z *= 0.55f;
    }

    /// <summary>Everything to draw now: chips shrink away at the end of their life, resting cubes bob and spin.</summary>
    public void Collect(List<DebrisCube> cubes, float time)
    {
        foreach (var c in _chips)
        {
            float shrink = MathF.Min(1f, (c.Life - c.Age) / (c.Life * 0.3f));
            cubes.Add(new DebrisCube(ChipModel, c.Position, Quaternion.CreateFromAxisAngle(c.Axis, c.Angle), c.Size * shrink, c.Color));
        }
        foreach (var d in _drops)
        {
            float bob = d.Resting ? 0.3f + 0.08f * MathF.Sin(time * 2.5f + d.Phase) : 0f; // floating over the grass
            var rotation = Quaternion.CreateFromYawPitchRoll(d.Angle, d.Resting ? 0.2f : d.Angle * 0.7f, 0f);
            cubes.Add(new DebrisCube(ModelOf(d.Resource), d.Position + new Vector3(0, bob, 0), rotation, DropSize, Vector3.One));
        }
    }
}
