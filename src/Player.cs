using System.Numerics;
using Mine.World;

namespace Mine;

/// <summary>First-person player with gravity, jumping and AABB collisions.</summary>
public sealed class Player
{
    public const float Radius = 0.3f;   // half width of the bounding box
    public const float Height = 1.8f;
    public const float EyeHeight = 1.62f;

    private const float Gravity = 28f;
    private const float JumpSpeed = 8.4f; // ~1.25 blocks high
    private const float MaxFallSpeed = 50f;
    private const float WalkSpeed = 4.5f;
    private const float SprintSpeed = 7f;
    private const float FlySpeed = 12f;
    private const float FlySprintSpeed = 30f;

    public Vector3 Position; // feet, centre of the bounding box
    public Vector3 Velocity;
    public float Yaw;
    public float Pitch;
    public bool Flying;
    public bool OnGround { get; private set; }

    public Vector3 Eye => Position + new Vector3(0, EyeHeight, 0);

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
    public void Update(VoxelWorld world, float dt, Vector2 move, bool up, bool down, bool sprint)
    {
        var forward = new Vector3(MathF.Cos(Yaw), 0, MathF.Sin(Yaw));
        var right = new Vector3(-MathF.Sin(Yaw), 0, MathF.Cos(Yaw));
        var wish = forward * move.Y + right * move.X;
        if (wish.LengthSquared() > 0) wish = Vector3.Normalize(wish);

        if (Flying)
        {
            float speed = sprint ? FlySprintSpeed : FlySpeed;
            Velocity = wish * speed;
            Velocity.Y = ((up ? 1 : 0) - (down ? 1 : 0)) * speed;
        }
        else
        {
            float speed = sprint ? SprintSpeed : WalkSpeed;
            Velocity.X = wish.X * speed;
            Velocity.Z = wish.Z * speed;
            Velocity.Y = MathF.Max(Velocity.Y - Gravity * dt, -MaxFallSpeed);
            if (up && OnGround) Velocity.Y = JumpSpeed;
        }

        Move(world, Velocity * dt);
    }

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
            if (Collides(world))
            {
                if (step.Y < 0)
                {
                    Position.Y = MathF.Floor(Position.Y) + 1;
                    OnGround = true;
                }
                else
                {
                    Position.Y = MathF.Floor(Position.Y + Height) - Height - skin;
                }
                Velocity.Y = 0;
            }

            Position.X += step.X;
            if (Collides(world))
            {
                Position.X = step.X > 0
                    ? MathF.Floor(Position.X + Radius) - Radius - skin
                    : MathF.Floor(Position.X - Radius) + 1 + Radius + skin;
                Velocity.X = 0;
            }

            Position.Z += step.Z;
            if (Collides(world))
            {
                Position.Z = step.Z > 0
                    ? MathF.Floor(Position.Z + Radius) - Radius - skin
                    : MathF.Floor(Position.Z - Radius) + 1 + Radius + skin;
                Velocity.Z = 0;
            }
        }
    }

    private bool Collides(VoxelWorld world)
    {
        const float e = 0.0001f;
        int x0 = (int)MathF.Floor(Position.X - Radius + e), x1 = (int)MathF.Floor(Position.X + Radius - e);
        int y0 = (int)MathF.Floor(Position.Y + e), y1 = (int)MathF.Floor(Position.Y + Height - e);
        int z0 = (int)MathF.Floor(Position.Z - Radius + e), z1 = (int)MathF.Floor(Position.Z + Radius - e);

        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
        for (int x = x0; x <= x1; x++)
            if (Blocks.IsSolid(world.GetBlock(x, y, z))) return true;
        return false;
    }

    /// <summary>True when the block cell overlaps the player (used to forbid placing inside yourself).</summary>
    public bool Intersects(BlockPos block) =>
        block.X < Position.X + Radius && block.X + 1 > Position.X - Radius &&
        block.Y < Position.Y + Height && block.Y + 1 > Position.Y &&
        block.Z < Position.Z + Radius && block.Z + 1 > Position.Z - Radius;
}
