using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// A 4x4 grid of 16px block textures, generated procedurally at startup
/// so the game needs no image files.
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
            pixels[i + 3] = 255;
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
            default: return (255, 0, 255);
        }
    }

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
