using System.Numerics;

namespace Mine.World;

/// <summary>
/// Things lying on the ground to be picked up, and the flakes a block sheds while it is being
/// broken. A broken block leaves a small copy of itself that hops out, settles, and turns slowly
/// just above the ground; a player walking within <see cref="PullRadius"/> draws it in, and it
/// goes into the first free inventory slot. Flakes are only for the eye and soon gone.
/// </summary>
public sealed class Drops
{
    public const float DropSize = 0.28f;
    public const float FlakeSize = 0.05f; // on average: each flake's own Size varies around it
    private const float Gravity = 18f;
    private const float PullRadius = 2.2f;
    private const float SettleTime = 0.5f;  // before this a drop only hops out and falls
    private const float Lifetime = 900f;
    private const float FlakeLifetime = 1.2f;

    public sealed class Drop
    {
        public Vector3 Position, Velocity;
        public Item Item;
        public BlockMaterial Material; // what a flake is made of
        public float Age, Spin, Size;
        public bool Pulled, Flake;
    }

    private readonly List<Drop> _drops = new();
    private readonly Random _random = new();

    public IReadOnlyList<Drop> All => _drops;

    /// <summary>A drop of <paramref name="item"/> hopping out of <paramref name="center"/>.</summary>
    public void Spawn(Item item, Vector3 center)
    {
        var velocity = new Vector3(_random.NextSingle() - 0.5f, 4f, _random.NextSingle() - 0.5f) * new Vector3(2f, 1f, 2f);
        _drops.Add(new Drop { Position = center, Velocity = velocity, Item = item, Spin = _random.NextSingle() * MathF.Tau });
    }

    private static readonly Vector3[] Faces = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ]; // not the bottom

    /// <summary>
    /// Flakes coming off a 1 m block being struck, anywhere on its sides and top, flying out from
    /// the face they leave and scattering.
    /// </summary>
    public void Flake(Cell anchor, BlockMaterial material, int count = 1)
    {
        const float half = BlockWorld.BlockSize / 2;
        var center = anchor.Min + new Vector3(half);
        for (int i = 0; i < count; i++)
        {
            var normal = Faces[_random.Next(Faces.Length)];
            // A random point on that face.
            var t1 = MathF.Abs(normal.Y) > 0.5f ? Vector3.UnitX : Vector3.UnitY;
            var t2 = Vector3.Cross(normal, t1);
            var at = center + normal * (half + 0.02f) + t1 * ((_random.NextSingle() - 0.5f) * 0.9f) + t2 * ((_random.NextSingle() - 0.5f) * 0.9f);
            var scatter = new Vector3(_random.NextSingle() - 0.5f, _random.NextSingle() - 0.3f, _random.NextSingle() - 0.5f) * 2f;
            var velocity = normal * (1.2f + 1.8f * _random.NextSingle()) + scatter + new Vector3(0, 1.2f, 0);
            _drops.Add(new Drop
            {
                Position = at, Velocity = velocity, Material = material, Spin = _random.NextSingle() * MathF.Tau,
                Size = FlakeSize * (0.6f + 0.8f * _random.NextSingle()), Flake = true,
            });
        }
    }

    /// <summary>A burst of flakes out of the whole 1 m block anchored at <paramref name="anchor"/>, as it breaks.</summary>
    public void Shatter(Cell anchor, BlockMaterial material, int count = 22)
    {
        var center = anchor.Min + new Vector3(BlockWorld.BlockSize / 2);
        for (int i = 0; i < count; i++)
        {
            var direction = Vector3.Normalize(new Vector3(_random.NextSingle() - 0.5f, _random.NextSingle() * 0.8f, _random.NextSingle() - 0.5f) + new Vector3(0, 0.1f, 0));
            _drops.Add(new Drop
            {
                Position = center + direction * 0.35f, Velocity = direction * (2f + 3f * _random.NextSingle()) + new Vector3(0, 1.5f, 0),
                Material = material, Spin = _random.NextSingle() * MathF.Tau, Size = FlakeSize * (0.8f + 1.2f * _random.NextSingle()), Flake = true,
            });
        }
    }

    /// <summary>Moves the drops; those reaching the player go into <paramref name="inventory"/> if it has room.</summary>
    public void Update(float dt, Ground ground, Vector3 feet, Inventory inventory)
    {
        var chest = feet + new Vector3(0, 1.0f, 0);
        for (int i = _drops.Count - 1; i >= 0; i--)
        {
            var d = _drops[i];
            d.Age += dt;
            if (d.Flake ? d.Age > FlakeLifetime : d.Age > Lifetime)
            {
                _drops.RemoveAt(i);
                continue;
            }
            float distance = Vector3.Distance(d.Position, chest);
            if (!d.Flake && d.Age > SettleTime && distance < PullRadius && inventory.HasRoomFor(d.Item)) d.Pulled = true;
            if (d.Pulled)
            {
                // Drawn in, faster and faster.
                var toward = (chest - d.Position) / MathF.Max(distance, 1e-3f);
                d.Velocity = Vector3.Lerp(d.Velocity, toward * (6f + 10f * MathF.Min(d.Age, 1.5f)), 1f - MathF.Exp(-12f * dt));
                d.Position += d.Velocity * dt;
                d.Spin += dt * 8f;
                if (distance < 0.4f)
                {
                    inventory.Add(d.Item);
                    _drops.RemoveAt(i);
                }
                else if (distance > PullRadius * 3f) d.Pulled = false; // the player got away: fall again
                continue;
            }

            d.Velocity.Y -= Gravity * dt;
            var next = d.Position + d.Velocity * dt;
            float size = d.Flake ? d.Size : DropSize;
            // Drops rest a little above the ground, where they turn; flakes lie on it.
            float rest = d.Flake ? size * 0.5f : size * 0.5f + 0.12f;
            float floor = ground.Height(next.X, next.Z, d.Position.Y + 0.05f) + rest;
            if (next.Y < floor)
            {
                next.Y = floor;
                d.Velocity *= new Vector3(0.5f, -0.25f, 0.5f); // a small bounce, sliding to a stop
                if (MathF.Abs(d.Velocity.Y) < 0.6f) d.Velocity.Y = 0;
            }
            d.Spin += dt * (d.Flake ? 6f : 1.2f);
            d.Position = next;
        }
    }

    /// <summary>Where a drop is drawn: resting drops bob gently.</summary>
    public static Vector3 DrawPosition(Drop d) =>
        d.Flake || d.Pulled ? d.Position : d.Position + new Vector3(0, 0.05f * MathF.Sin(d.Age * 2.2f + d.Spin), 0);
}
