namespace Mine.World;

/// <summary>
/// Coloured block light, packed in a ushort: 5 bits per channel (0..31), red in the low bits.
/// Light loses <see cref="HorizontalFalloff"/> per block sideways and <see cref="VerticalFalloff"/>
/// per half-block cell vertically, so it spreads about equally far in every direction.
/// </summary>
public static class BlockLight
{
    public const int MaxLevel = 31;
    public const int HorizontalFalloff = 2;
    public const int VerticalFalloff = 1;

    /// <summary>Farthest a light can reach, in blocks, horizontally.</summary>
    public const int MaxReach = MaxLevel / HorizontalFalloff + 1;

    public static ushort Pack(int r, int g, int b) => (ushort)(r | (g << 5) | (b << 10));

    public static int R(ushort light) => light & 31;
    public static int G(ushort light) => (light >> 5) & 31;
    public static int B(ushort light) => (light >> 10) & 31;

    /// <summary>Per channel: the brighter of <paramref name="current"/> and <paramref name="source"/> dimmed by <paramref name="falloff"/>.</summary>
    public static ushort Spread(ushort current, ushort source, int falloff) => Pack(
        Math.Max(R(current), R(source) - falloff),
        Math.Max(G(current), G(source) - falloff),
        Math.Max(B(current), B(source) - falloff));
}
