namespace Mine.World;

/// <summary>
/// The weather: for now only rain, switched on and off by hand. <see cref="Rain"/> eases toward
/// the wanted state (in over ~8 s, out over ~12 s), so clouds gather and clear, and drops thicken
/// and thin out, instead of snapping.
/// </summary>
public sealed class Weather
{
    public bool Raining { get; private set; }

    /// <summary>0 = clear, 1 = full rain.</summary>
    public float Rain { get; private set; }

    public void Toggle() => Raining = !Raining;

    public void Update(float dt)
    {
        float target = Raining ? 1f : 0f;
        float rate = Raining ? 1f / 8f : 1f / 12f;
        Rain = Rain < target ? MathF.Min(Rain + rate * dt, target) : MathF.Max(Rain - rate * dt, target);
    }
}
