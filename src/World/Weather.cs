namespace Mine.World;

/// <summary>
/// The weather: rain or snow, switched on and off by hand (one stops the other). <see cref="Rain"/>
/// and <see cref="Snow"/> ease toward the wanted state (in over ~8 s, out over ~12 s), so clouds
/// gather and clear, and drops or flakes thicken and thin out, instead of snapping. While it snows,
/// <see cref="SnowCover"/> grows (full in about a minute and a quarter); afterwards it melts
/// slowly, faster in the rain.
/// </summary>
public sealed class Weather
{
    public bool Raining { get; private set; }
    public bool Snowing { get; private set; }

    /// <summary>0 = clear, 1 = full rain.</summary>
    public float Rain { get; private set; }

    /// <summary>0 = clear, 1 = full snowfall.</summary>
    public float Snow { get; private set; }

    /// <summary>Snow lying on the world: 0 = none, 1 = everything white.</summary>
    public float SnowCover { get; private set; }

    public void Toggle()
    {
        Raining = !Raining;
        if (Raining) Snowing = false;
    }

    public void ToggleSnow()
    {
        Snowing = !Snowing;
        if (Snowing) Raining = false;
    }

    /// <summary>Debug: snowing at full strength with the snow already lying everywhere.</summary>
    public void StartSnowed()
    {
        Snowing = true;
        Raining = false;
        Snow = 1f;
        SnowCover = 1f;
    }

    public void Update(float dt)
    {
        Rain = Ease(Rain, Raining, dt);
        Snow = Ease(Snow, Snowing, dt);
        float change = Snow > 0.3f ? Snow / 75f : -(Rain > 0.3f ? 1f / 40f : 1f / 180f);
        SnowCover = Math.Clamp(SnowCover + change * dt, 0f, 1f);
    }

    private static float Ease(float value, bool on, float dt)
    {
        float target = on ? 1f : 0f;
        float rate = on ? 1f / 8f : 1f / 12f;
        return value < target ? MathF.Min(value + rate * dt, target) : MathF.Max(value - rate * dt, target);
    }
}
