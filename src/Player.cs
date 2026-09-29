using System.Numerics;
using Mine.World;

namespace Mine;

/// <summary>
/// First-person player on the layered terrain (and the floating islands): gravity, jumping and
/// inertia; steps of one layer are climbed by walking, higher walls block the way (or must be
/// jumped); in deep water the player swims, moving in 3D like flying but slowly, held back by
/// the water, and floats back up to the surface when idle.
/// </summary>
public sealed class Player
{
    // The body: 2 m tall (a passage two blocks high lets it through) and BodyRadius wide, for the
    // blocks. The eyes are 0.2 m under the top of the head, so the view never reaches into a
    // ceiling; they still look over ordinary grass (up to 1.45 m, see GrassRenderer): chest-deep in
    // the tall meadows, and lost in their giant hearts.
    public const float Height = 2f;
    public const float BodyRadius = 0.35f;
    public const float EyeHeight = 1.8f;
    public const float SneakEyeHeight = 1.45f;

    private const float Gravity = 28f;
    private const float JumpSpeed = 8.4f;
    private const float MaxFallSpeed = 50f;
    private const float WalkSpeed = 5.5f;
    private const float SneakSpeed = 1.3f;
    private const float SprintSpeed = 10f;
    private const float FlySpeed = 30f;
    private const float FlySprintSpeed = 50f;
    private const float EyeLerpRate = 15f; // how fast the camera drops/rises when sneaking

    // Inertia: how fast the velocity approaches the target (1/s, higher = snappier).
    private const float GroundAccel = 10f;  // ~95% of top speed in 0.3 s
    private const float GroundFriction = 8f; // stops in ~0.4 s
    private const float AirAccel = 2.5f;     // limited control mid-jump
    private const float AirFriction = 0.5f;  // momentum is mostly kept while airborne
    private const float FlyAccel = 4f;
    private const float FlyFriction = 3f;

    // Walls: a step up to this height is climbed by walking; higher ones block (a jump clears ~1.2 m).
    private const float StepHeight = 0.55f;
    private const float Radius = 0.3f;        // how far ahead of the feet walls are felt
    private const float StepSmoothRate = 14f; // how fast the camera catches up after a step
    // While walking, stay glued to ground that drops away by less than this in one frame
    // (two layers), so walking down steps never turns into a short fall.
    private const float GroundSnap = 1.05f;
    // For this long after leaving the ground, still climb steps like when walking: stepping off one
    // layer onto the next must not count as being airborne.
    private const float GroundGrace = 0.15f;

    // Swimming: slow, with heavy drag; idle swimmers float up to rest with the head above water.
    private const float SwimSpeed = 2.2f;
    private const float SwimSprintSpeed = 3.6f;
    private const float SwimAccel = 1.8f;
    private const float SwimDrag = 1.2f;
    private const float SwimDepth = 1.0f;    // feet this far below the surface: swimming
    private const float FloatDepth = 1.7f;   // where idle swimmers rest (eye ~0.4 m above water)
    private const float SwimClimb = 1.6f;    // swimmers can pull themselves onto ledges this high

    public Vector3 Position; // feet
    public Vector3 Velocity;
    public float Yaw;
    public float Pitch;
    public bool Flying;
    public bool OnGround { get; private set; }
    public bool Sneaking { get; private set; }
    public bool Swimming { get; private set; }

    private float _eyeHeight = EyeHeight;

    private float _stepOffset; // camera offset right after a step (up: negative, down: positive), eased to 0
    private float _sinceGround = float.MaxValue;

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
    public void Update(Ground ground, float dt, Vector2 move, bool up, bool down, bool sprint)
    {
        Swimming = !Flying && Position.Y < TerrainField.WaterLevel - SwimDepth;
        Sneaking = down && !Flying && !Swimming;
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
        else if (Swimming)
        {
            float speed = sprint ? SwimSprintSpeed : SwimSpeed;
            var target = wish * speed;
            float vertical = (up ? 1 : 0) - (down ? 1 : 0);
            // Idle: drift gently toward the floating depth.
            float rest = TerrainField.WaterLevel - FloatDepth;
            target.Y = vertical != 0 ? vertical * speed : Math.Clamp((rest - Position.Y) * 0.8f, -0.6f, 0.6f);
            Velocity = Approach(Velocity, target, target == Vector3.Zero ? SwimDrag : SwimAccel, dt);
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

        Move(ground, Velocity * dt);
        TrackGround(dt);
    }

    private void Move(Ground surface, Vector3 delta)
    {
        bool wasOnGround = OnGround || _sinceGround < GroundGrace;
        // How high a wall ahead can be and still let the player through: a step when walking, a
        // ledge when swimming, nothing while airborne (unless the feet are already above it).
        float climb = Flying ? float.MaxValue : Swimming ? SwimClimb : wasOnGround ? StepHeight : 0.05f;

        Position.X += delta.X;
        if (delta.X != 0 && surface.Height(Position.X + MathF.Sign(delta.X) * Radius, Position.Z, Position.Y + climb) - Position.Y > climb)
        {
            Position.X -= delta.X;
            Velocity.X = 0;
        }
        Position.Z += delta.Z;
        if (delta.Z != 0 && surface.Height(Position.X, Position.Z + MathF.Sign(delta.Z) * Radius, Position.Y + climb) - Position.Y > climb)
        {
            Position.Z -= delta.Z;
            Velocity.Z = 0;
        }

        float startY = Position.Y;
        Position.Y += delta.Y;
        float ground = surface.Height(Position.X, Position.Z, Position.Y + climb);

        OnGround = false;
        if (Position.Y <= ground)
        {
            // A step is how far the ground rose over where the feet were, not over where this
            // frame's fall took them: after a long frame (a garbage collection) gravity sinks the
            // feet several centimetres into the ground, which must not ease the camera up like a step.
            float rise = ground - startY;
            if (rise > 0.05f && !Flying) _stepOffset -= rise; // the camera eases up the step
            Position.Y = ground;
            if (Velocity.Y < 0) Velocity.Y = 0;
            OnGround = !Flying && !Swimming;
        }
        else if (!Flying && !Swimming && wasOnGround && Velocity.Y <= 0 && Position.Y - ground < GroundSnap)
        {
            // Walking down a step: follow the ground instead of hopping off it, the camera easing down.
            _stepOffset += Position.Y - ground;
            Position.Y = ground;
            Velocity.Y = 0;
            OnGround = true;
        }
    }

    /// <summary>Call once per frame after moving: tracks how long ago the player last touched the ground.</summary>
    private void TrackGround(float dt) => _sinceGround = OnGround ? 0f : _sinceGround + dt;

    /// <summary>Exponential approach, independent of frame rate.</summary>
    private static Vector3 Approach(Vector3 current, Vector3 target, float rate, float dt) =>
        target + (current - target) * MathF.Exp(-rate * dt);
}
