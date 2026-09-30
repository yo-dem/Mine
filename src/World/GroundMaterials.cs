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
    // The one tone grass drifts to in worlds with little colour variety (WorldPreset.ColorVariety).
    private static readonly Vector3 MutedGrass = new(0.36f, 0.32f, 0.44f);

    /// <summary>How much each of the world's biomes rules at a point; the five sum to 1.</summary>
    public readonly record struct BiomeMix(float Desert, float Prairie, float Hills, float Islands, float Snow)
    {
        public static readonly string[] Names = ["Deserto", "Prateria", "Colline", "Isole", "Innevato"];

        /// <summary>The index (in <see cref="Names"/>) of the biome that rules most.</summary>
        public int Dominant()
        {
            float[] w = [Desert, Prairie, Hills, Islands, Snow];
            int best = 0;
            for (int i = 1; i < w.Length; i++) if (w[i] > w[best]) best = i;
            return best;
        }
    }

    // The climate: a very broad temperature noise (TemperatureAt) is hot in the deserts and cold in
    // the snowy lands, mild in between, so the two never meet. In the mild lands a sea noise raises
    // archipelagos and a relief noise hills; the rest is prairie. Each change is a smoothstep over a
    // good stretch of its noise (a couple of hundred metres), so biomes always blend gradually.
    // About a fifth of the land each; regions a kilometre or two across. Mirrored in the shader
    // (Materials: temperature, desertWeight, snowWeight): keep the two in sync.
    private const float HotLow = 0.61f, HotHigh = 0.69f, ColdHigh = 0.39f, ColdLow = 0.31f;
    private const float SeaLow = 0.55f, SeaHigh = 0.63f, HillLow = 0.46f, HillHigh = 0.54f;

    private static float TemperatureAt(float x, float z) => Noise2(x * 0.0004f, z * 0.0004f, 41f) * 0.75f + Noise2(x * 0.0016f, z * 0.0016f, 42f) * 0.25f;

    /// <summary>The five biomes' shares at (x, z): desert, prairie, hills, islands, snowy lands.</summary>
    public static BiomeMix Biomes(float x, float z)
    {
        float temperature = TemperatureAt(x, z);
        float desert = SmoothStep(HotLow, HotHigh, temperature), snow = SmoothStep(ColdHigh, ColdLow, temperature);
        float mild = 1f - desert - snow;
        float sea = SmoothStep(SeaLow, SeaHigh, Noise2(x * 0.0005f, z * 0.0005f, 43f) * 0.8f + Noise2(x * 0.002f, z * 0.002f, 44f) * 0.2f);
        float hill = SmoothStep(HillLow, HillHigh, Noise2(x * 0.0007f, z * 0.0007f, 45f) * 0.8f + Noise2(x * 0.0025f, z * 0.0025f, 46f) * 0.2f);
        return new BiomeMix(desert, mild * (1f - sea) * (1f - hill), mild * (1f - sea) * hill, mild * sea, snow);
    }

    /// <summary>
    /// The colour families at (x, z), summing to 1: the indigo woods (pines, cypresses, giants, and
    /// the snowy lands' pines), the pink woods (blossoms, umbrellas), the turquoise woods (willows,
    /// blue orbs) and the desert (sand, dunes, dry shrubs). They split the mild biomes by a flavour
    /// noise, so every biome has woods of all three kinds; the snowy lands are indigo.
    /// Mirrored by <c>biomeWeightsFor</c> in the terrain shader: keep the two in sync.
    /// </summary>
    public static Vector4 Biome(float x, float z)
    {
        float temperature = TemperatureAt(x, z);
        float desert = SmoothStep(HotLow, HotHigh, temperature), snow = SmoothStep(ColdHigh, ColdLow, temperature);
        float flavour = Noise2(x * 0.0011f, z * 0.0011f, 21f) * 0.7f + Noise2(x * 0.004f, z * 0.004f, 22f) * 0.3f;
        float pink = SmoothStep(0.58f, 0.64f, flavour), teal = SmoothStep(0.47f, 0.41f, flavour);
        float mild = 1f - desert - snow;
        return new Vector4((1f - pink - teal) * mild + snow, pink * mild, teal * mild, desert);
    }

    /// <summary>The desert's share of the ground at (x, z): 0 outside, 1 in its heart.</summary>
    public static float Desert(float x, float z) => SmoothStep(HotLow, HotHigh, TemperatureAt(x, z));

    /// <summary>The snowy lands' share at (x, z): 0 outside, 1 in their heart (always white, often snowing).</summary>
    public static float Snow(float x, float z) => SmoothStep(ColdHigh, ColdLow, TemperatureAt(x, z));

    /// <summary>The islands' land noise: land where it exceeds WorldPreset.IslandCoast.</summary>
    public static float IslandLand(float x, float z) =>
        Noise2(x * 0.0065f, z * 0.0065f, 31f) * 0.7f + Noise2(x * 0.02f, z * 0.02f, 32f) * 0.3f;

    /// <summary>How far inland a point of an archipelago is: below 0 at sea, 0 at the coast, 1 well inland.</summary>
    public static float Inland(float x, float z) => (IslandLand(x, z) - WorldPreset.IslandCoast) / WorldPreset.IslandSpan;

    /// <summary>
    /// How thick the grass grows at (x, z), a share of the usual: thickest on the prairie, sparse
    /// under the snow, hardly any in the desert (whose sand holds none anyway, but at its fringes).
    /// </summary>
    public static float GrassDensity(float x, float z)
    {
        var b = Biomes(x, z);
        return b.Desert * 0.12f + b.Prairie * 1.15f + b.Hills * 0.85f + b.Islands * 0.9f + b.Snow * 0.3f;
    }

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
        c = Vector3.Lerp(MutedGrass, c, WorldPreset.Current.ColorVariety);
        return c * (0.85f + 0.3f * shade);
    }

    /// <summary>Reed beds: 0 where none grow, up to 1 in the thickest ones (only near the water).</summary>
    public static float Reeds(float x, float z) => SmoothStep(0.3f, 0.45f, Noise2(x * 0.05f, z * 0.05f, 14f));

    /// <summary>
    /// Tall grass meadows: 0 on ordinary ground, 0.5 where the grass grows thigh-high, 1 in the
    /// hearts of the meadows where it towers over the player; many on the prairie, hardly any in the
    /// snow or the desert. (C#-only: the shader does not need it.)
    /// </summary>
    public static float TallGrass(float x, float z)
    {
        float v = Noise2(x * 0.018f, z * 0.018f, 7f) * 0.7f + Noise2(x * 0.05f, z * 0.05f, 8f) * 0.3f;
        // The prairie is their home; the snowy lands and the desert have hardly any.
        var biomes = Biomes(x, z);
        v += 0.1f * biomes.Prairie - 0.15f * (biomes.Snow + biomes.Desert);
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
