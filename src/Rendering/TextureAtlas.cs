using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// A 4x4 grid of 16px block textures, generated procedurally at startup
/// so the game needs no image files. The alpha channel is not transparency
/// but a glow mask: 1 marks texels that emit light (see the terrain shader).
/// </summary>
public sealed unsafe class TextureAtlas : IDisposable
{
    private const int TileSize = 16;
    private const int TilesPerRow = 4;
    private const int AtlasSize = TileSize * TilesPerRow;

    private readonly GL _gl;
    private readonly uint _texture;

    public TextureAtlas(GL gl)
    {
        _gl = gl;
        var pixels = new byte[AtlasSize * AtlasSize * 4];
        foreach (var tile in Enum.GetValues<Tile>())
            PaintTile(pixels, tile);

        _texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _texture);
        fixed (byte* data = pixels)
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, AtlasSize, AtlasSize, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, data);

        // Mip levels up to 4 never mix texels of different tiles (16 = 2^4).
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.NearestMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 4);
        gl.GenerateMipmap(TextureTarget.Texture2D);
    }

    public void Bind(int unit)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
    }

    /// <summary>UV rectangle of a tile, slightly inset to avoid sampling neighbours.</summary>
    public static (float U0, float V0, float U1, float V1) TileUv(Tile tile)
    {
        const float step = 1f / TilesPerRow;
        const float inset = 0.001f;
        int index = (int)tile;
        float u = index % TilesPerRow * step;
        float v = index / TilesPerRow * step;
        return (u + inset, v + inset, u + step - inset, v + step - inset);
    }

    private static void PaintTile(byte[] pixels, Tile tile)
    {
        int ox = (int)tile % TilesPerRow * TileSize;
        int oy = (int)tile / TilesPerRow * TileSize;

        for (int py = 0; py < TileSize; py++)
        for (int px = 0; px < TileSize; px++)
        {
            var (r, g, b) = TileColor(tile, px, py);
            int i = ((oy + py) * AtlasSize + ox + px) * 4;
            pixels[i] = ToByte(r);
            pixels[i + 1] = ToByte(g);
            pixels[i + 2] = ToByte(b);
            pixels[i + 3] = ToByte(TileGlow(tile, px, py) * 255);
        }
    }

    private static (float R, float G, float B) TileColor(Tile tile, int px, int py)
    {
        float n = Noise(px, py, (int)tile);
        (float, float, float) Tint(float r, float g, float b, float k) => (r * k, g * k, b * k);

        var grass = Tint(95, 159, 53, 0.85f + 0.3f * n);
        var dirt = Tint(134, 96, 67, 0.8f + 0.35f * n);

        switch (tile)
        {
            case Tile.GrassTop: return grass;
            case Tile.Dirt: return dirt;
            case Tile.GrassSide:
                // Uneven strip of grass hanging over the dirt.
                return py < 3 + (int)(Noise(px, 0, 99) * 3) ? grass : dirt;
            case Tile.Stone: return Tint(125, 125, 125, 0.8f + 0.3f * n);
            case Tile.Sand: return Tint(219, 207, 163, 0.9f + 0.15f * n);
            case Tile.LogSide:
                return Tint(102, 81, 51, (px % 4 == 0 ? 0.7f : 1f) * (0.85f + 0.2f * n));
            case Tile.LogTop:
            {
                float d = MathF.Sqrt((px - 7.5f) * (px - 7.5f) + (py - 7.5f) * (py - 7.5f));
                if (d > 6.5f) return Tint(102, 81, 51, 0.85f + 0.2f * n);
                return Tint(176, 143, 90, ((int)d % 3 == 0 ? 0.8f : 1f) * (0.9f + 0.1f * n));
            }
            case Tile.Leaves: return Tint(58, 122, 40, 0.55f + 0.55f * n);
            case Tile.Planks:
            {
                bool seam = py % 4 == 3 || px == (py / 4 % 2 == 0 ? 0 : 8);
                return Tint(170, 136, 84, (seam ? 0.7f : 1f) * (0.9f + 0.15f * n));
            }
            case Tile.Cobblestone:
            {
                int row = py / 4;
                bool mortar = py % 4 == 0 || (px + row * 2) % 5 == 0;
                float stone = Noise((px + row * 2) / 5, row, 42);
                return Tint(128, 128, 128, mortar ? 0.55f : 0.75f + 0.35f * stone);
            }
            case Tile.Crystal:
                // Glowing amethyst facets separated by dark edges.
                return CrystalFacet(px, py) is float facet
                    ? Tint(190, 140, 255, facet * (0.95f + 0.1f * n))
                    : Tint(62, 46, 98, 0.8f + 0.35f * n);
            case Tile.GlowMushroom:
                return IsMushroomSpot(px, py)
                    ? Tint(130, 245, 255, 0.9f + 0.1f * n)
                    : Tint(38, 66, 112, 0.8f + 0.3f * n);
            case Tile.Lantern:
                return IsLanternFrame(px, py)
                    ? Tint(70, 50, 32, 0.8f + 0.3f * n)
                    : Tint(255, 196, 110, 0.92f + 0.08f * n);
            default: return (255, 0, 255);
        }
    }

    /// <summary>How much each texel glows, 0..1.</summary>
    private static float TileGlow(Tile tile, int px, int py) => tile switch
    {
        Tile.Crystal => CrystalFacet(px, py) ?? 0f,
        Tile.GlowMushroom => IsMushroomSpot(px, py) ? 1f : 0f,
        Tile.Lantern => IsLanternFrame(px, py) ? 0f : 1f,
        _ => 0f,
    };

    /// <summary>Brightness of the crystal facet under a texel, or null on the dark edges between facets.</summary>
    private static float? CrystalFacet(int px, int py)
    {
        int a = px + py, b = px - py + 16;
        if (a % 8 == 0 || b % 10 == 0) return null;
        return 0.55f + 0.45f * Noise(a / 8, b / 10, 3);
    }

    private static bool IsMushroomSpot(int px, int py)
    {
        int cx = px / 5, cy = py / 5;
        if (Noise(cx, cy, 7) < 0.35f) return false;
        float dx = px % 5 - 2, dy = py % 5 - 2;
        return dx * dx + dy * dy <= 2.5f;
    }

    private static bool IsLanternFrame(int px, int py) =>
        px is 0 or 1 or 14 or 15 || py is 0 or 1 or 14 or 15 || px is 7 or 8;

    /// <summary>Deterministic pseudo-random value in [0, 1).</summary>
    private static float Noise(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 73856093 ^ y * 19349663 ^ seed * 83492791);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFF) / 65536f;
        }
    }

    private static byte ToByte(float value) => (byte)Math.Clamp(value, 0, 255);

    public void Dispose() => _gl.DeleteTexture(_texture);
}
