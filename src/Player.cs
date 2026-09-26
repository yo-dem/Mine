using System.Numerics;
using Mine.World;

namespace Mine;

/// <summary>
/// First-person player walking on the smooth terrain: gravity, jumping, inertia,
/// and sliding down slopes too steep to climb.
/// </summary>
public sealed class Player
{
    public const float EyeHeight = 1.62f;
    public const float SneakEyeHeight = 1.32f;

    private const float Gravity = 28f;
    private const float JumpSpeed = 8.4f;
    private const float MaxFallSpeed = 50f;
    private const float WalkSpeed = 4.5f;
    private const float SneakSpeed = 1.3f;
    private const float SprintSpeed = 7f;
    private const float FlySpeed = 12f;
    private const float FlySprintSpeed = 30f;
    private const float EyeLerpRate = 15f; // how fast the camera drops/rises when sneaking

    // Inertia: how fast the velocity approaches the target (1/s, higher = snappier).
    private const float GroundAccel = 10f;  // ~95% of top speed in 0.3 s
    private const float GroundFriction = 8f; // stops in ~0.4 s
    private const float AirAccel = 2.5f;     // limited control mid-jump
    private const float AirFriction = 0.5f;  // momentum is mostly kept while airborne
    private const float FlyAccel = 4f;
    private const float FlyFriction = 3f;

    // Slopes: steeper than this (normal.Y below it, about 50 degrees) cannot be climbed.
    private const float MaxWalkableNormalY = 0.64f;
    private const float SlideAccel = 14f;
    // While walking, stay glued to ground that drops away by less than this in one frame.
    private const float GroundSnap = 0.6f;

    public Vector3 Position; // feet
    public Vector3 Velocity;
    public float Yaw;
    public float Pitch;
    public bool Flying;
    public bool OnGround { get; private set; }
    public bool Sneaking { get; private set; }

    private float _eyeHeight = EyeHeight;

    public Vector3 Eye => Position + new Vector3(0, _eyeHeight, 0);

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
    public void Update(TerrainField terrain, float dt, Vector2 move, bool up, bool down, bool sprint)
    {
        Sneaking = down && !Flying;
        float eyeTarget = Sneaking ? SneakEyeHeight : EyeHeight;
        _eyeHeight = eyeTarget + (_eyeHeight - eyeTarget) * MathF.Exp(-EyeLerpRate * dt);

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

            var normal = terrain.Normal(Position.X, Position.Z);
            bool tooSteep = normal.Y < MaxWalkableNormalY;
            if (OnGround && tooSteep)
            {
                // No walking uphill, and gravity drags the player down the slope.
                var downhill = Vector3.Normalize(new Vector3(normal.X, 0, normal.Z));
                float uphill = -Vector3.Dot(horizontal, downhill);
                if (uphill > 0) horizontal += downhill * uphill;
                horizontal += downhill * SlideAccel * (1 - normal.Y) * dt;
            }

            Velocity.X = horizontal.X;
            Velocity.Z = horizontal.Z;
            Velocity.Y = MathF.Max(Velocity.Y - Gravity * dt, -MaxFallSpeed);
            if (up && OnGround && !tooSteep) Velocity.Y = JumpSpeed;
        }

        Move(terrain, Velocity * dt);
    }

    private void Move(TerrainField terrain, Vector3 delta)
    {
        bool wasOnGround = OnGround;
        Position += delta;
        float ground = terrain.Height(Position.X, Position.Z);

        OnGround = false;
        if (Position.Y <= ground)
        {
            Position.Y = ground;
            if (Velocity.Y < 0) Velocity.Y = 0;
            OnGround = !Flying;
        }
        else if (!Flying && wasOnGround && Velocity.Y <= 0 && Position.Y - ground < GroundSnap)
        {
            // Walking downhill: follow the ground instead of hopping off every bump.
            Position.Y = ground;
            Velocity.Y = 0;
            OnGround = true;
        }
    }

    /// <summary>Exponential approach, independent of frame rate.</summary>
    private static Vector3 Approach(Vector3 current, Vector3 target, float rate, float dt) =>
        target + (current - target) * MathF.Exp(-rate * dt);
}
