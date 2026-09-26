using System.Numerics;

namespace Mine.World;

/// <summary>Lighting and sky colours for one moment of the day.</summary>
public readonly record struct Atmosphere(
    Vector3 Zenith,
    Vector3 Horizon,
    Vector3 SunHorizon,
    Vector3 Ambient,
    Vector3 LightColor,
    Vector3 LightDirection,
    Vector3 SunDirection,
    Vector3 MoonDirection,
    Vector3 SunGlow,
    float Haze,
    float Night,
    float SkyAngle);

/// <summary>
/// Game clock and colour palette. Time of day is in [0, 1):
/// 0 = midnight, 0.25 = sunrise, 0.5 = noon, 0.75 = sunset.
/// </summary>
public sealed class DayCycle
{
    public const float DayLength = 1200f; // seconds for a full day: 10 minutes of daylight, 10 of night

    /// <param name="Horizon">Horizon colour away from the sun.</param>
    /// <param name="SunHorizon">Horizon colour below the sun.</param>
    /// <param name="Sun">Direct sunlight, also tints the glow around the sun.</param>
    /// <param name="Haze">0..1: how thick the coloured haze is (fog starts closer).</param>
    private readonly record struct Keyframe(
        float Time, Vector3 Zenith, Vector3 Horizon, Vector3 SunHorizon, Vector3 Sun, Vector3 Ambient, float Haze);

    private static readonly Keyframe Night = new(0, Rgb(6, 8, 28), Rgb(22, 28, 64), Rgb(30, 34, 72), Vector3.Zero, Rgb(40, 44, 86), 0.2f);
    private static readonly Keyframe PreDawn = new(0, Rgb(20, 24, 68), Rgb(28, 32, 76), Rgb(230, 70, 120), Rgb(230, 70, 90), Rgb(62, 56, 102), 0.6f);
    private static readonly Keyframe Dawn = new(0, Rgb(48, 50, 128), Rgb(60, 58, 115), Rgb(255, 95, 25), Rgb(255, 120, 50), Rgb(125, 95, 125), 0.85f);
    private static readonly Keyframe MorningGold = new(0, Rgb(75, 120, 210), Rgb(150, 160, 200), Rgb(255, 170, 50), Rgb(255, 190, 110), Rgb(130, 118, 125), 0.45f);
    private static readonly Keyframe Day = new(0, Rgb(70, 140, 240), Rgb(170, 205, 245), Rgb(215, 228, 245), Rgb(245, 230, 200), Rgb(90, 94, 108), 0.08f);
    private static readonly Keyframe EveningGold = new(0, Rgb(72, 110, 200), Rgb(150, 150, 190), Rgb(255, 150, 40), Rgb(255, 170, 80), Rgb(140, 112, 115), 0.55f);
    private static readonly Keyframe Sunset = new(0, Rgb(40, 40, 110), Rgb(52, 50, 108), Rgb(255, 70, 0), Rgb(255, 95, 15), Rgb(130, 88, 105), 0.9f);
    private static readonly Keyframe Afterglow = new(0, Rgb(22, 22, 72), Rgb(30, 32, 80), Rgb(255, 40, 80), Rgb(255, 50, 90), Rgb(80, 60, 105), 0.75f);
    private static readonly Keyframe Dusk = new(0, Rgb(14, 14, 52), Rgb(68, 44, 95), Rgb(115, 55, 90), Rgb(120, 50, 70), Rgb(55, 50, 100), 0.4f);

    private static readonly Keyframe[] Keyframes =
    [
        Night with { Time = 0f },
        Night with { Time = 0.19f },
        PreDawn with { Time = 0.225f },
        Dawn with { Time = 0.255f },
        MorningGold with { Time = 0.29f },
        Day with { Time = 0.34f },
        Day with { Time = 0.66f },
        EveningGold with { Time = 0.705f },
        Sunset with { Time = 0.748f },
        Afterglow with { Time = 0.78f },
        Dusk with { Time = 0.815f },
        Night with { Time = 0.86f },
        Night with { Time = 1f },
    ];

    private static readonly Vector3 MoonLight = Rgb(36, 46, 90);

    public float TimeOfDay = 0.3f;

    /// <summary>Game seconds since start; runs faster while time is sped up (drives the clouds).</summary>
    public double Elapsed { get; private set; }

    public void Update(float seconds)
    {
        TimeOfDay = (TimeOfDay + seconds / DayLength) % 1f;
        Elapsed += seconds;
    }

    public Atmosphere Sample()
    {
        // The sun rises in +X and sets in -X; the Z tilt keeps the +Z faces lit at noon.
        float angle = (TimeOfDay - 0.25f) * MathF.Tau;
        var sun = Vector3.Normalize(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0.35f));
        var moon = new Vector3(-sun.X, -sun.Y, sun.Z);

        int i = 1;
        while (Keyframes[i].Time < TimeOfDay) i++;
        var a = Keyframes[i - 1];
        var b = Keyframes[i];
        float t = SmoothStep(a.Time, b.Time, TimeOfDay);

        var sunColor = Vector3.Lerp(a.Sun, b.Sun, t);
        bool sunUp = sun.Y >= 0;
        var lightDirection = sunUp ? sun : moon;
        var lightColor = (sunUp ? sunColor : MoonLight) * SmoothStep(-0.02f, 0.12f, lightDirection.Y);

        return new Atmosphere(
            Zenith: Vector3.Lerp(a.Zenith, b.Zenith, t),
            Horizon: Vector3.Lerp(a.Horizon, b.Horizon, t),
            SunHorizon: Vector3.Lerp(a.SunHorizon, b.SunHorizon, t),
            Ambient: Vector3.Lerp(a.Ambient, b.Ambient, t),
            LightColor: lightColor,
            LightDirection: lightDirection,
            SunDirection: sun,
            MoonDirection: moon,
            SunGlow: sunColor * SmoothStep(-0.25f, 0.05f, sun.Y),
            Haze: float.Lerp(a.Haze, b.Haze, t),
            Night: SmoothStep(0.05f, -0.2f, sun.Y),
            SkyAngle: angle);
    }

    /// <summary>Clock time as hours and minutes, for the HUD.</summary>
    public (int Hours, int Minutes) Clock
    {
        get
        {
            int minutes = (int)(TimeOfDay * 24 * 60);
            return (minutes / 60, minutes % 60);
        }
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    private static Vector3 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f);
}
