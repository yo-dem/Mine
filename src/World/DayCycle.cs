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
    Vector3 GalaxyDirection,
    float GalaxyGlow,
    float Haze,
    float Night,
    float SkyAngle);

/// <summary>
/// Game clock and colour palette of a cosmic world: the day/night cycle breathes between a
/// perpetual twilight (the sun never climbs more than a few degrees above the horizon: its
/// "noon" is a pink-gold dusk under a violet sky) and a deep cosmic night, when the galaxy
/// fills the sky. Time of day is in [0, 1): 0 = midnight, 0.5 = the brightest twilight.
/// </summary>
public sealed class DayCycle
{
    public const float DayLength = 1200f; // seconds for a full cycle

    // Sun elevation (sine of the angle): Mid + Swing * sin(...), so at most ~8 degrees at noon.
    private const float SunMid = -0.23f, SunSwing = 0.37f;

    /// <param name="Horizon">Horizon colour away from the sun.</param>
    /// <param name="SunHorizon">Horizon colour below the sun.</param>
    /// <param name="Sun">Direct sunlight, also tints the glow around the sun.</param>
    /// <param name="Haze">0..1: how thick the coloured haze is.</param>
    private readonly record struct Keyframe(
        float Time, Vector3 Zenith, Vector3 Horizon, Vector3 SunHorizon, Vector3 Sun, Vector3 Ambient, float Haze);

    private static readonly Keyframe Night = new(0, Rgb(8, 4, 30), Rgb(42, 18, 84), Rgb(70, 30, 110), Vector3.Zero, Rgb(52, 34, 104), 0.3f);
    private static readonly Keyframe Dawn = new(0, Rgb(30, 14, 72), Rgb(120, 48, 140), Rgb(255, 90, 130), Rgb(235, 95, 125), Rgb(88, 54, 128), 0.6f);
    private static readonly Keyframe Twilight = new(0, Rgb(48, 30, 116), Rgb(196, 92, 172), Rgb(255, 160, 95), Rgb(255, 176, 128), Rgb(126, 88, 156), 0.75f);
    private static readonly Keyframe Dusk = new(0, Rgb(34, 14, 78), Rgb(150, 50, 150), Rgb(255, 80, 115), Rgb(245, 90, 130), Rgb(96, 56, 134), 0.65f);

    private static readonly Keyframe[] Keyframes =
    [
        Night with { Time = 0f },
        Night with { Time = 0.28f },
        Dawn with { Time = 0.35f },
        Twilight with { Time = 0.43f },
        Twilight with { Time = 0.57f },
        Dusk with { Time = 0.65f },
        Night with { Time = 0.72f },
        Night with { Time = 1f },
    ];

    private static readonly Vector3 MoonLight = Rgb(80, 66, 150);

    public float TimeOfDay = 0.45f;

    /// <summary>Game seconds since start; runs faster while time is sped up (drives the clouds).</summary>
    public double Elapsed { get; private set; }

    public void Update(float seconds)
    {
        TimeOfDay = (TimeOfDay + seconds / DayLength) % 1f;
        Elapsed += seconds;
    }

    public Atmosphere Sample()
    {
        // The sun circles low around the horizon: it peeks above it from ~0.36 to ~0.64.
        float azimuth = (TimeOfDay - 0.25f) * MathF.Tau;
        float wave = MathF.Sin(azimuth);
        var sun = Direction(azimuth, SunMid + SunSwing * wave);
        // The moon rides opposite, high in the night sky.
        var moon = Direction(azimuth + MathF.PI, 0.2f - 0.37f * wave);
        // The galaxy drifts slowly, always well above the horizon.
        var galaxy = Direction(azimuth * 0.5f + 2.2f, 0.5f + 0.08f * MathF.Sin(azimuth * 0.5f));

        int i = 1;
        while (Keyframes[i].Time < TimeOfDay) i++;
        var a = Keyframes[i - 1];
        var b = Keyframes[i];
        float t = SmoothStep(a.Time, b.Time, TimeOfDay);

        var sunColor = Vector3.Lerp(a.Sun, b.Sun, t);
        bool sunUp = sun.Y >= 0;
        var lightDirection = sunUp ? sun : moon;
        var lightColor = (sunUp ? sunColor : MoonLight) * SmoothStep(-0.02f, 0.1f, lightDirection.Y);
        float night = SmoothStep(0.02f, -0.25f, sun.Y);

        return new Atmosphere(
            Zenith: Vector3.Lerp(a.Zenith, b.Zenith, t),
            Horizon: Vector3.Lerp(a.Horizon, b.Horizon, t),
            SunHorizon: Vector3.Lerp(a.SunHorizon, b.SunHorizon, t),
            Ambient: Vector3.Lerp(a.Ambient, b.Ambient, t),
            LightColor: lightColor,
            LightDirection: lightDirection,
            SunDirection: sun,
            MoonDirection: moon,
            SunGlow: sunColor * SmoothStep(-0.3f, 0.05f, sun.Y),
            GalaxyDirection: galaxy,
            GalaxyGlow: 0.3f + 0.7f * night,
            Haze: float.Lerp(a.Haze, b.Haze, t),
            Night: night,
            SkyAngle: azimuth);
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

    /// <summary>Unit vector at a given azimuth (around Y, from +X toward +Z) and elevation (its Y).</summary>
    private static Vector3 Direction(float azimuth, float elevation)
    {
        float y = Math.Clamp(elevation, -0.99f, 0.99f);
        float flat = MathF.Sqrt(1 - y * y);
        return new Vector3(MathF.Cos(azimuth) * flat, y, MathF.Sin(azimuth) * flat);
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    private static Vector3 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f);
}
