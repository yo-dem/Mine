namespace Mine.World;

/// <summary>
/// The weather: rain or snow, switched on and off by hand (one stops the other). <see cref="Rain"/>
/// and <see cref="Snow"/> ease toward the wanted state (in over ~8 s, out over ~12 s), so clouds
/// gather and clear, and drops or flakes thicken and thin out, instead of snapping. While it snows,
/// <see cref="SnowCover"/> grows (full in about a minute and a quarter); afterwards it melts
/// slowly, faster in the rain. Each can turn heavy: a storm (denser, wind-driven rain and
/// lightning every few seconds) or a blizzard (thick snow blown by the wind, settling twice as fast).
/// In the snowy lands it snows by itself most of the time (<see cref="Update"/>): spells of snow with
/// calmer breaks, which stop when the player walks out of them. That snow lays no cover of its own:
/// the snowy lands are always white (the shaders' snowCover), and the rest of the world stays as it is.
/// Now and then, rarely, a shower passes by itself anywhere but the snowy lands (seldom over the
/// desert): see <see cref="Update"/>.
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
    private bool _localSnow; // snowing by itself, in the snowy lands

    // In the snowy lands: snow falls for SnowSpell seconds of every SnowCycle.
    private const float SnowCycle = 330f, SnowSpell = 250f;

    // Showers: one may come every ShowerGapMin..ShowerGapMax seconds of play, lasting
    // ShowerMin..ShowerMax; over the desert most pass it by (DesertShowers of them fall there).
    private const float ShowerGapMin = 900f, ShowerGapMax = 2100f, ShowerMin = 100f, ShowerMax = 220f, DesertShowers = 0.15f;
    private float _nextShower = float.NaN, _showerEnd;
    private bool _localRain; // raining by itself, a passing shower
    private float _clock, _strikeStart = -100f, _nextStrike;

    /// <summary>0 = clear, 1 = full rain.</summary>
    public float Rain { get; private set; }

    /// <summary>0 = clear, 1 = full snowfall.</summary>
    public float Snow { get; private set; }

    /// <summary>Snow lying on the world: 0 = none, 1 = everything white.</summary>
    public float SnowCover { get; private set; }

    public void Toggle()
    {
        Raining = !(Raining || _localRain); // stopping a passing shower by hand, too
        _localRain = false;
        Storming = false;
        if (Raining) Snowing = Blizzarding = false;
    }

    public void ToggleSnow()
    {
        Snowing = !Snowing;
        Blizzarding = false;
        if (Snowing) Raining = Storming = _localRain = false;
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
        Raining = Storming = _localRain = false;
    }

    /// <summary>Debug: snowing at full strength (a blizzard if asked) with the snow already lying everywhere.</summary>
    public void StartSnowed(bool blizzard = false)
    {
        Snowing = true;
        Raining = Storming = _localRain = false;
        Blizzarding = blizzard;
        Snow = 1f;
        Blizzard = blizzard ? 1f : 0f;
        SnowCover = 1f;
    }

    /// <summary>
    /// Moves the weather on; <paramref name="snowyLands"/> and <paramref name="desert"/> are how much
    /// the player stands in the snowy lands and in the desert (0..1). In the snowy lands it snows by
    /// itself in spells (unless it rains by hand); elsewhere a shower comes now and then, rarely,
    /// and over the desert seldom (never starting in the snow, and stopping as the player walks in).
    /// </summary>
    public void Update(float dt, float snowyLands, float desert)
    {
        _clock += dt;
        if (float.IsNaN(_nextShower)) _nextShower = _clock + float.Lerp(ShowerGapMin, ShowerGapMax, _random.NextSingle());
        if (!_localRain && _clock >= _nextShower)
        {
            _nextShower = _clock + float.Lerp(ShowerGapMin, ShowerGapMax, _random.NextSingle());
            if (snowyLands < 0.3f && !Snowing && _random.NextSingle() < float.Lerp(1f, DesertShowers, desert))
            {
                _localRain = true;
                _showerEnd = _clock + float.Lerp(ShowerMin, ShowerMax, _random.NextSingle());
            }
        }
        if (_localRain && (_clock >= _showerEnd || snowyLands > 0.55f)) _localRain = false;
        bool raining = Raining || _localRain;

        bool spell = _clock % SnowCycle < SnowSpell;
        _localSnow = spell && !raining && (_localSnow ? snowyLands > 0.45f : snowyLands > 0.55f);
        Rain = Ease(Rain, raining, dt);
        Snow = Ease(Snow, Snowing || _localSnow, dt);
        Storm = Ease(Storm, Storming && Raining, dt);
        Blizzard = Ease(Blizzard, Blizzarding && Snowing, dt);
        // Only the snow called by hand lays its cover over the whole world.
        float change = Snowing && Snow > 0.3f ? Snow * (1f + Blizzard) / 75f : -(Rain > 0.3f ? 1f / 40f : 1f / 180f);
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
