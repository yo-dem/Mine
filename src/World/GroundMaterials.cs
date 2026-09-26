using System.Numerics;

namespace Mine.World;

/// <summary>
/// C# mirror of the ground material functions in the terrain shader (<c>hash13</c>,
/// <c>valueNoise3</c>, <c>grassColor</c>, <c>rockSand</c> in TerrainShaders.Materials), so grass
/// blades can be coloured and culled once on the CPU instead of in every vertex. Keep the two in sync.
/// </summary>
public static class GroundMaterials
{
    public static Vector3 GrassColor(float x, float z)
    {
        float broad = Noise2(x * 0.0035f, z * 0.0035f, 0f);
        float tint = Noise2(x * 0.0012f + 7f, z * 0.0012f + 7f, 1f);
        var grass = Vector3.Lerp(new Vector3(0.33f, 0.50f, 0.15f), new Vector3(0.64f, 0.60f, 0.22f), broad);
        return Vector3.Lerp(grass, new Vector3(0.20f, 0.48f, 0.38f), SmoothStep(0.65f, 0.9f, tint) * 0.35f);
    }

    /// <summary>How much of the ground is grass (1 on gentle, not-too-low ground; 0 on rock or sand).</summary>
    public static float GrassWeight(Vector3 p, float normalY)
    {
        float mid = Noise2(p.X * 0.045f, p.Z * 0.045f, 2f);
        float rock = SmoothStep(0.30f, 0.46f, 1f - normalY + (mid - 0.5f) * 0.12f);
        float sand = SmoothStep(14f, 6f, p.Y + (mid - 0.5f) * 6f) * (1f - rock);
        return (1f - rock) * (1f - sand);
    }

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
