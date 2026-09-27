namespace Mine.World;

/// <summary>
/// The weather: rain or snow, switched on and off by hand (one stops the other). <see cref="Rain"/>
/// and <see cref="Snow"/> ease toward the wanted state (in over ~8 s, out over ~12 s), so clouds
/// gather and clear, and drops or flakes thicken and thin out, instead of snapping. While it snows,
/// <see cref="SnowCover"/> grows (full in about a minute and a quarter); afterwards it melts
/// slowly, faster in the rain. Each can turn heavy: a storm (denser, wind-driven rain and
/// lightning every few seconds) or a blizzard (thick snow blown by the wind, settling twice as fast).
/// </summary>
public sealed class Weather
{
    public bool Raining { get; private set; }
    public bool Snowing { get; private set; }
    public bool Storming { get; private set; }
    public bool Blizzarding { get; private set; }

    /// <summary>0..1: how much of the rain is a storm, and of the snow a blizzard.</summary>
    public float Storm { get; private set; }

    public float Blizzard { get; private set; }

    /// <summary>The brightness of the current lightning flash (0 between strikes).</summary>
    public float Lightning { get; private set; }

    /// <summary>Where the current bolt strikes: its azimuth (radians), and a seed for its shape.</summary>
    public float LightningAzimuth { get; private set; }

    public float LightningSeed { get; private set; }

    private readonly Random _random = new();
    private float _clock, _strikeStart = -100f, _nextStrike;

    /// <summary>0 = clear, 1 = full rain.</summary>
    public float Rain { get; private set; }

    /// <summary>0 = clear, 1 = full snowfall.</summary>
    public float Snow { get; private set; }

    /// <summary>Snow lying on the world: 0 = none, 1 = everything white.</summary>
    public float SnowCover { get; private set; }

    public void Toggle()
    {
        Raining = !Raining;
        Storming = false;
        if (Raining) Snowing = Blizzarding = false;
    }

    public void ToggleSnow()
    {
        Snowing = !Snowing;
        Blizzarding = false;
        if (Snowing) Raining = Storming = false;
    }

    /// <summary>Heavy rain with lightning (the rain starts if it was not falling).</summary>
    public void StartStorm()
    {
        Raining = Storming = true;
        Snowing = Blizzarding = false;
        _nextStrike = _clock + 1.5f;
    }

    /// <summary>A blizzard (the snow starts if it was not falling).</summary>
    public void StartBlizzard()
    {
        Snowing = Blizzarding = true;
        Raining = Storming = false;
    }

    /// <summary>Debug: snowing at full strength (a blizzard if asked) with the snow already lying everywhere.</summary>
    public void StartSnowed(bool blizzard = false)
    {
        Snowing = true;
        Raining = Storming = false;
        Blizzarding = blizzard;
        Snow = 1f;
        Blizzard = blizzard ? 1f : 0f;
        SnowCover = 1f;
    }

    public void Update(float dt)
    {
        _clock += dt;
        Rain = Ease(Rain, Raining, dt);
        Snow = Ease(Snow, Snowing, dt);
        Storm = Ease(Storm, Storming && Raining, dt);
        Blizzard = Ease(Blizzard, Blizzarding && Snowing, dt);
        float change = Snow > 0.3f ? Snow * (1f + Blizzard) / 75f : -(Rain > 0.3f ? 1f / 40f : 1f / 180f);
        SnowCover = Math.Clamp(SnowCover + change * dt, 0f, 1f);

        // Lightning: a strike every 3 to 12 s in a storm, each a bright flash with a second,
        // weaker one right after, as the bolt re-strikes.
        if (Storm > 0.5f && _clock >= _nextStrike)
        {
            _strikeStart = _clock;
            LightningAzimuth = _random.NextSingle() * MathF.Tau;
            LightningSeed = _random.NextSingle() * 100f;
            _nextStrike = _clock + 3f + 9f * _random.NextSingle();
        }
        float age = _clock - _strikeStart;
        Lightning = age < 0.7f
            ? (MathF.Exp(-age * 12f) + (age > 0.18f ? 0.7f * MathF.Exp(-(age - 0.18f) * 14f) : 0f)) * Storm
            : 0f;
    }

    private static float Ease(float value, bool on, float dt)
    {
        float target = on ? 1f : 0f;
        float rate = on ? 1f / 8f : 1f / 12f;
        return value < target ? MathF.Min(value + rate * dt, target) : MathF.Max(value - rate * dt, target);
    }
}
