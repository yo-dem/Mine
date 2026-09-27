using System.Numerics;

namespace Mine.World;

/// <summary>
/// C# mirror of the ground material functions in the terrain shader (<c>hash13</c>,
/// <c>valueNoise3</c>, <c>grassColor</c>, <c>rockSand</c> in TerrainShaders.Materials), so grass
/// blades can be coloured and culled once on the CPU instead of in every vertex. Keep the two in sync.
/// </summary>
public static class GroundMaterials
{
    // Grass tints of the biomes (see Biome): each biome pulls its patches of colour toward its hue.
    private static readonly Vector3 IndigoHue = new(0.20f, 0.24f, 0.56f);
    private static readonly Vector3 PinkHue = new(0.62f, 0.24f, 0.50f);
    private static readonly Vector3 TealHue = new(0.10f, 0.44f, 0.48f);
    private static readonly Vector3 DesertHue = new(0.60f, 0.46f, 0.36f);
    private const float BiomeTint = 0.4f;

    /// <summary>
    /// How much each biome rules at (x, z), summing to 1: the indigo woods (pines, cypresses, giants),
    /// the pink woods (blossoms, umbrellas), the turquoise woods (willows, blue orbs) and the desert
    /// (sand, dunes, dry trees). The borders are a few tens of metres wide, so biomes blend.
    /// Mirrored by <c>biomeWeights</c> in the terrain shader: keep the two in sync.
    /// </summary>
    public static Vector4 Biome(float x, float z)
    {
        float desert = Desert(x, z);
        float flavour = Noise2(x * 0.0011f, z * 0.0011f, 21f) * 0.7f + Noise2(x * 0.004f, z * 0.004f, 22f) * 0.3f;
        float pink = SmoothStep(0.58f, 0.64f, flavour), teal = SmoothStep(0.47f, 0.41f, flavour);
        float wet = 1f - desert;
        return new Vector4((1f - pink - teal) * wet, pink * wet, teal * wet, desert);
    }

    /// <summary>The desert's share of the ground at (x, z): 0 outside, 1 in its heart.</summary>
    public static float Desert(float x, float z) =>
        SmoothStep(0.70f, 0.76f, Noise2(x * 0.0008f, z * 0.0008f, 23f) * 0.75f + Noise2(x * 0.003f, z * 0.003f, 24f) * 0.25f);

    public static Vector3 GrassColor(float x, float z)
    {
        float patch = Noise2(x * 0.012f, z * 0.012f, 11f) * 0.75f + Noise2(x * 0.04f, z * 0.04f, 12f) * 0.25f;
        float shade = Noise2(x * 0.09f, z * 0.09f, 13f);
        var c = new Vector3(0.14f, 0.34f, 0.38f);                                              // teal
        c = Vector3.Lerp(c, new Vector3(0.34f, 0.22f, 0.54f), SmoothStep(0.30f, 0.36f, patch)); // violet
        c = Vector3.Lerp(c, new Vector3(0.56f, 0.20f, 0.46f), SmoothStep(0.44f, 0.50f, patch)); // magenta
        c = Vector3.Lerp(c, new Vector3(0.58f, 0.46f, 0.42f), SmoothStep(0.56f, 0.62f, patch)); // lilac gold
        c = Vector3.Lerp(c, new Vector3(0.16f, 0.32f, 0.58f), SmoothStep(0.68f, 0.74f, patch)); // sky blue
        var b = Biome(x, z);
        c = Vector3.Lerp(c, IndigoHue * b.X + PinkHue * b.Y + TealHue * b.Z + DesertHue * b.W, BiomeTint);
        return c * (0.85f + 0.3f * shade);
    }

    /// <summary>Reed beds: 0 where none grow, up to 1 in the thickest ones (only near the water).</summary>
    public static float Reeds(float x, float z) => SmoothStep(0.3f, 0.45f, Noise2(x * 0.05f, z * 0.05f, 14f));

    /// <summary>
    /// Tall grass meadows: 0 on ordinary ground, 0.5 where the grass grows thigh-high, 1 in the
    /// hearts of the meadows where it towers over the player. (C#-only: the shader does not need it.)
    /// </summary>
    public static float TallGrass(float x, float z)
    {
        float v = Noise2(x * 0.018f, z * 0.018f, 7f) * 0.7f + Noise2(x * 0.05f, z * 0.05f, 8f) * 0.3f;
        return SmoothStep(0.6f, 0.68f, v) * 0.5f + SmoothStep(0.74f, 0.8f, v) * 0.5f;
    }

    /// <summary>How much of the ground is grass (1 on gentle, not-too-low ground; 0 on rock or sand, deserts included).</summary>
    public static float GrassWeight(Vector3 p, float normalY)
    {
        float mid = Noise2(p.X * 0.045f, p.Z * 0.045f, 2f);
        float rock = SmoothStep(0.30f, 0.46f, 1f - normalY + (mid - 0.5f) * 0.12f);
        float sand = MathF.Max(SmoothStep(19.5f, 14f, p.Y + (mid - 0.5f) * 4f), DesertSand(p.X, p.Z, mid)) * (1f - rock);
        return (1f - rock) * (1f - sand);
    }

    // Sand covering the desert, frayed at its edges.
    private static float DesertSand(float x, float z, float mid) => SmoothStep(0.25f, 0.7f, Desert(x, z) + (mid - 0.5f) * 0.3f);

    private static float Noise2(float x, float y, float layer) => ValueNoise3(new Vector3(x, y, layer));

    private static float ValueNoise3(Vector3 p)
    {
        var i = new Vector3(MathF.Floor(p.X), MathF.Floor(p.Y), MathF.Floor(p.Z));
        var f = p - i;
        var u = f * f * (new Vector3(3f) - 2f * f);
        float a = float.Lerp(Hash13(i), Hash13(i + new Vector3(1, 0, 0)), u.X);
        float b = float.Lerp(Hash13(i + new Vector3(0, 1, 0)), Hash13(i + new Vector3(1, 1, 0)), u.X);
        float c = float.Lerp(Hash13(i + new Vector3(0, 0, 1)), Hash13(i + new Vector3(1, 0, 1)), u.X);
        float d = float.Lerp(Hash13(i + new Vector3(0, 1, 1)), Hash13(i + new Vector3(1, 1, 1)), u.X);
        return float.Lerp(float.Lerp(a, b, u.Y), float.Lerp(c, d, u.Y), u.Z);
    }

    private static float Hash13(Vector3 p)
    {
        p = Fract(p * 0.1031f);
        p += new Vector3(Vector3.Dot(p, new Vector3(p.Z, p.Y, p.X) + new Vector3(31.32f)));
        return Fract((p.X + p.Y) * p.Z);
    }

    private static Vector3 Fract(Vector3 v) => v - new Vector3(MathF.Floor(v.X), MathF.Floor(v.Y), MathF.Floor(v.Z));
    private static float Fract(float v) => v - MathF.Floor(v);

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3 - 2 * t);
    }
}
