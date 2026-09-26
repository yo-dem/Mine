using System.Numerics;
using Mine.World;

namespace Mine;

/// <summary>First-person player with gravity, jumping, AABB collisions and automatic half-step climbing.</summary>
public sealed class Player
{
    public const float Radius = 0.3f;   // half width of the bounding box
    public const float Height = 1.8f;
    public const float EyeHeight = 1.62f;
    public const float SneakEyeHeight = 1.32f;

    private const float Gravity = 28f;
    private const float JumpSpeed = 8.4f; // ~1.25 blocks high
    private const float MaxFallSpeed = 50f;
    private const float WalkSpeed = 4.5f;
    private const float SneakSpeed = 1.3f;
    private const float EyeLerpRate = 15f; // how fast the camera drops/rises when sneaking
    private const float StepHeight = Chunk.CellHeight; // walking climbs half-block steps on its own
    private const float StepSmoothRate = 14f; // how fast the camera catches up after a step
    private const float SprintSpeed = 7f;
    private const float FlySpeed = 12f;
    private const float FlySprintSpeed = 30f;

    // Inertia: how fast the velocity approaches the target (1/s, higher = snappier).
    private const float GroundAccel = 10f;  // ~95% of top speed in 0.3 s
    private const float GroundFriction = 8f; // stops in ~0.4 s
    private const float AirAccel = 2.5f;     // limited control mid-jump
    private const float AirFriction = 0.5f;  // momentum is mostly kept while airborne
    private const float FlyAccel = 4f;
    private const float FlyFriction = 3f;

    public Vector3 Position; // feet, centre of the bounding box
    public Vector3 Velocity;
    public float Yaw;
    public float Pitch;
    public bool Flying;
    public bool OnGround { get; private set; }
    public bool Sneaking { get; private set; }

    private float _eyeHeight = EyeHeight;
    private float _stepOffset; // negative right after climbing a step, eased back to 0

    public Vector3 Eye => Position + new Vector3(0, _eyeHeight + _stepOffset, 0);

    public Vector3 LookDirection => new(
        MathF.Cos(Pitch) * MathF.Cos(Yaw),
        MathF.Sin(Pitch),
        MathF.Cos(Pitch) * MathF.Sin(Yaw));

    public void Look(float deltaYaw, float deltaPitch)
    {
        const float limit = 89f * MathF.PI / 180f;
        Yaw += deltaYaw;
        Pitch = Math.Clamp(Pitch + deltaPitch, -limit, limit);
    }

    /// <param name="move">x = strafe right, y = forward, both in [-1, 1].</param>
    /// <param name="down">Descends while flying, sneaks while walking.</param>
    public void Update(VoxelWorld world, float dt, Vector2 move, bool up, bool down, bool sprint)
    {
        Sneaking = down && !Flying;
        float eyeTarget = Sneaking ? SneakEyeHeight : EyeHeight;
        _eyeHeight = eyeTarget + (_eyeHeight - eyeTarget) * MathF.Exp(-EyeLerpRate * dt);
        _stepOffset *= MathF.Exp(-StepSmoothRate * dt);

        var forward = new Vector3(MathF.Cos(Yaw), 0, MathF.Sin(Yaw));
        var right = new Vector3(-MathF.Sin(Yaw), 0, MathF.Cos(Yaw));
        var wish = forward * move.Y + right * move.X;
        if (wish.LengthSquared() > 0) wish = Vector3.Normalize(wish);

        if (Flying)
        {
            float speed = sprint ? FlySprintSpeed : FlySpeed;
            var target = wish * speed;
            target.Y = ((up ? 1 : 0) - (down ? 1 : 0)) * speed;
            Velocity = Approach(Velocity, target, target == Vector3.Zero ? FlyFriction : FlyAccel, dt);
        }
        else
        {
            float speed = Sneaking ? SneakSpeed : sprint ? SprintSpeed : WalkSpeed;
            var target = wish * speed;
            bool moving = target.LengthSquared() > 0;
            float rate = OnGround ? (moving ? GroundAccel : GroundFriction) : (moving ? AirAccel : AirFriction);
            var horizontal = Approach(new Vector3(Velocity.X, 0, Velocity.Z), target, rate, dt);
            Velocity.X = horizontal.X;
            Velocity.Z = horizontal.Z;
            Velocity.Y = MathF.Max(Velocity.Y - Gravity * dt, -MaxFallSpeed);
            if (up && OnGround) Velocity.Y = JumpSpeed;
        }

        Move(world, Velocity * dt);
    }

    /// <summary>Exponential approach, independent of frame rate.</summary>
    private static Vector3 Approach(Vector3 current, Vector3 target, float rate, float dt) =>
        target + (current - target) * MathF.Exp(-rate * dt);

    /// <summary>Moves one axis at a time in small steps, snapping against blocks on contact.</summary>
    private void Move(VoxelWorld world, Vector3 delta)
    {
        float largest = MathF.Max(MathF.Abs(delta.X), MathF.Max(MathF.Abs(delta.Y), MathF.Abs(delta.Z)));
        int steps = Math.Max(1, (int)MathF.Ceiling(largest / 0.4f));
        var step = delta / steps;
        const float skin = 0.001f;

        OnGround = false;
        for (int i = 0; i < steps; i++)
        {
            Position.Y += step.Y;
            if (Collides(world, 0, out float highestTop, out float lowestBottom))
            {
                if (step.Y < 0)
                {
                    Position.Y = highestTop;
                    OnGround = true;
                }
                else
                {
                    Position.Y = lowestBottom - Height - skin;
                }
                Velocity.Y = 0;
            }

            Position.X += step.X;
            if (Sneaking && OnGround && !Collides(world) && !HasGroundBelow(world))
            {
                Position.X -= step.X; // sneaking: never walk off an edge
                Velocity.X = 0;
            }
            else if (Collides(world) && !TryStepUp(world))
            {
                Position.X = step.X > 0
                    ? MathF.Floor(Position.X + Radius) - Radius - skin
                    : MathF.Floor(Position.X - Radius) + 1 + Radius + skin;
                Velocity.X = 0;
            }

            Position.Z += step.Z;
            if (Sneaking && OnGround && !Collides(world) && !HasGroundBelow(world))
            {
                Position.Z -= step.Z;
                Velocity.Z = 0;
            }
            else if (Collides(world) && !TryStepUp(world))
            {
                Position.Z = step.Z > 0
                    ? MathF.Floor(Position.Z + Radius) - Radius - skin
                    : MathF.Floor(Position.Z - Radius) + 1 + Radius + skin;
                Velocity.Z = 0;
            }
        }
    }

    /// <summary>
    /// After bumping into something while walking on the ground, climbs onto it
    /// if it is at most a half block high and there is room above.
    /// </summary>
    private bool TryStepUp(VoxelWorld world)
    {
        if (Flying || !OnGround) return false;
        Collides(world, 0, out float highestTop, out _);
        float rise = highestTop - Position.Y;
        if (rise <= 0 || rise > StepHeight + 0.001f) return false;

        Position.Y += rise;
        if (Collides(world))
        {
            Position.Y -= rise;
            return false;
        }
        _stepOffset -= rise; // the camera eases up instead of jumping
        return true;
    }

    /// <summary>True when a solid block lies just under the bounding box.</summary>
    private bool HasGroundBelow(VoxelWorld world) => Collides(world, -0.05f);

    private bool Collides(VoxelWorld world, float yOffset = 0) => Collides(world, yOffset, out _, out _);

    /// <summary>
    /// True when the bounding box, shifted down by <paramref name="yOffset"/>, overlaps a solid cell.
    /// Also returns the highest top and lowest bottom among the overlapping cells, for snapping.
    /// </summary>
    private bool Collides(VoxelWorld world, float yOffset, out float highestTop, out float lowestBottom)
    {
        const float e = 0.0001f;
        float bottom = Position.Y + yOffset, top = bottom + Height;
        int x0 = (int)MathF.Floor(Position.X - Radius + e), x1 = (int)MathF.Floor(Position.X + Radius - e);
        int y0 = Chunk.CellY(bottom + e), y1 = Chunk.CellY(top - e);
        int z0 = (int)MathF.Floor(Position.Z - Radius + e), z1 = (int)MathF.Floor(Position.Z + Radius - e);

        bool hit = false;
        highestTop = float.NegativeInfinity;
        lowestBottom = float.PositiveInfinity;
        for (int by = y0; by <= y1; by++)
        for (int bz = z0; bz <= z1; bz++)
        for (int bx = x0; bx <= x1; bx++)
        {
            if (!Blocks.IsSolid(world.GetBlock(bx, by, bz))) continue;
            hit = true;
            highestTop = MathF.Max(highestTop, (by + 1) * Chunk.CellHeight);
            lowestBottom = MathF.Min(lowestBottom, by * Chunk.CellHeight);
        }
        return hit;
    }

    /// <summary>True when the cell overlaps the player (used to forbid placing inside yourself).</summary>
    public bool Intersects(BlockPos cell) =>
        cell.X < Position.X + Radius && cell.X + 1 > Position.X - Radius &&
        cell.Y * Chunk.CellHeight < Position.Y + Height && (cell.Y + 1) * Chunk.CellHeight > Position.Y &&
        cell.Z < Position.Z + Radius && cell.Z + 1 > Position.Z - Radius;
}
