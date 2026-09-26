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
/// Game clock and colour palette of a cosmic world: the day/night cycle breathes between long
/// pink-gold twilights under a violet sky, when the sun hangs low, a short bright noon, when it
/// climbs to ~50 degrees and shines stronger, and a deep cosmic night, when the galaxy fills
/// the sky. Time of day is in [0, 1): 0 = midnight, 0.5 = noon.
/// </summary>
public sealed class DayCycle
{
    public const float DayLength = 1200f; // seconds for a full cycle

    // Sun elevation (sine of the angle): Mid + Swing * w + Peak * w⁴ with w = sin(...) when positive.
    // The twilights keep the sun low; around noon the steep extra term lifts it to ~54 degrees
    // for a few minutes of full, bright day (above 30 degrees for about a sixth of the cycle).
    private const float SunMid = -0.12f, SunSwing = 0.55f, SunPeak = 0.38f;

    /// <param name="Horizon">Horizon colour away from the sun.</param>
    /// <param name="SunHorizon">Horizon colour below the sun.</param>
    /// <param name="Sun">Direct sunlight, also tints the glow around the sun.</param>
    /// <param name="Haze">0..1: how thick the coloured haze is.</param>
    private readonly record struct Keyframe(
        float Time, Vector3 Zenith, Vector3 Horizon, Vector3 SunHorizon, Vector3 Sun, Vector3 Ambient, float Haze);

    private static readonly Keyframe Night = new(0, Rgb(8, 4, 30), Rgb(42, 18, 84), Rgb(70, 30, 110), Vector3.Zero, Rgb(52, 34, 104), 0.3f);
    private static readonly Keyframe Dawn = new(0, Rgb(30, 14, 72), Rgb(120, 48, 140), Rgb(255, 90, 130), Rgb(235, 95, 125), Rgb(88, 54, 128), 0.6f);
    private static readonly Keyframe Twilight = new(0, Rgb(48, 30, 116), Rgb(196, 92, 172), Rgb(255, 160, 95), Rgb(255, 176, 128), Rgb(126, 88, 156), 0.75f);
    // The bright hours around noon: warm light under a still violet sky, never a plain blue noon.
    private static readonly Keyframe Day = new(0, Rgb(70, 62, 165), Rgb(214, 128, 176), Rgb(255, 190, 125), Rgb(255, 205, 160), Rgb(150, 116, 172), 0.5f);
    // Noon, the sun high: clearer, bluer violet sky, whiter sunlight, thinner haze.
    private static readonly Keyframe Noon = new(0, Rgb(88, 96, 205), Rgb(225, 165, 205), Rgb(255, 215, 170), Rgb(255, 238, 215), Rgb(170, 150, 195), 0.35f);
    private static readonly Keyframe Dusk = new(0, Rgb(34, 14, 78), Rgb(150, 50, 150), Rgb(255, 80, 115), Rgb(245, 90, 130), Rgb(96, 56, 134), 0.65f);

    private static readonly Keyframe[] Keyframes =
    [
        Night with { Time = 0f },
        Night with { Time = 0.24f },
        Dawn with { Time = 0.30f },
        Twilight with { Time = 0.36f },
        Day with { Time = 0.43f },
        Noon with { Time = 0.47f },
        Noon with { Time = 0.53f },
        Day with { Time = 0.57f },
        Twilight with { Time = 0.64f },
        Dusk with { Time = 0.70f },
        Night with { Time = 0.76f },
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
        float high = MathF.Max(wave, 0f);
        var sun = Direction(azimuth, SunMid + SunSwing * wave + SunPeak * high * high * high * high);
        // The moon rides opposite, high in the night sky.
        var moon = Direction(azimuth + MathF.PI, 0.2f - 0.37f * wave);
        // Stars, nebulae and the galaxy turn slowly with the elapsed time, which never wraps (an angle
        // taken from TimeOfDay would jump by a fraction of a turn every midnight). The galaxy stays
        // high, filling the sky.
        float skyAngle = (float)(Elapsed / DayLength % 8.0 * MathF.Tau);
        var galaxy = Direction(skyAngle * 0.5f + 2.2f, 0.62f + 0.08f * MathF.Sin(skyAngle * 0.5f));

        int i = 1;
        while (Keyframes[i].Time < TimeOfDay) i++;
        var a = Keyframes[i - 1];
        var b = Keyframes[i];
        float t = SmoothStep(a.Time, b.Time, TimeOfDay);

        var sunColor = Vector3.Lerp(a.Sun, b.Sun, t);
        bool sunUp = sun.Y >= 0;
        var lightDirection = sunUp ? sun : moon;
        var lightColor = (sunUp ? sunColor : MoonLight) * SmoothStep(-0.02f, 0.1f, lightDirection.Y);
        // A high sun shines stronger.
        if (sunUp) lightColor *= 1f + 0.35f * SmoothStep(0.45f, 0.75f, sun.Y);
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
            SkyAngle: skyAngle);
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
