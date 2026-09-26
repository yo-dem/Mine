using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// A tileable 64³ texture of smooth 3D noise (two octaves of value noise on wrapping lattices),
/// generated at startup. Sampling it is far cheaper than computing noise in the shader, which
/// matters for the clouds, where every sky pixel takes dozens of samples.
/// </summary>
public sealed unsafe class CloudNoise : IDisposable
{
    private const int Size = 64;

    private readonly GL _gl;
    private readonly uint _texture;

    public CloudNoise(GL gl)
    {
        _gl = gl;
        var data = new byte[Size * Size * Size];
        var random = new Random(4242);
        var coarse = Lattice(random, 8);  // features 8 texels wide
        var fine = Lattice(random, 16);   // and 4 texels wide

        for (int z = 0; z < Size; z++)
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            float v = 0.65f * Sample(coarse, 8, x, y, z) + 0.35f * Sample(fine, 16, x, y, z);
            data[(z * Size + y) * Size + x] = (byte)Math.Clamp(v * 255f, 0f, 255f);
        }

        _texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture3D, _texture);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = data)
            gl.TexImage3D(TextureTarget.Texture3D, 0, InternalFormat.R8, Size, Size, Size, 0, PixelFormat.Red, PixelType.UnsignedByte, p);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.Repeat);
        gl.GenerateMipmap(TextureTarget.Texture3D);
    }

    public void Bind(int unit)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.Texture3D, _texture);
    }

    private static float[] Lattice(Random random, int cells)
    {
        var values = new float[cells * cells * cells];
        for (int i = 0; i < values.Length; i++) values[i] = random.NextSingle();
        return values;
    }

    /// <summary>Smoothly interpolated lattice value; the lattice wraps, so the texture tiles.</summary>
    private static float Sample(float[] lattice, int cells, int x, int y, int z)
    {
        float scale = cells / (float)Size;
        float fx = x * scale, fy = y * scale, fz = z * scale;
        int x0 = (int)fx, y0 = (int)fy, z0 = (int)fz;
        float tx = Smooth(fx - x0), ty = Smooth(fy - y0), tz = Smooth(fz - z0);
        float V(int i, int j, int k) => lattice[((k % cells) * cells + (j % cells)) * cells + (i % cells)];

        float a = float.Lerp(V(x0, y0, z0), V(x0 + 1, y0, z0), tx);
        float b = float.Lerp(V(x0, y0 + 1, z0), V(x0 + 1, y0 + 1, z0), tx);
        float c = float.Lerp(V(x0, y0, z0 + 1), V(x0 + 1, y0, z0 + 1), tx);
        float d = float.Lerp(V(x0, y0 + 1, z0 + 1), V(x0 + 1, y0 + 1, z0 + 1), tx);
        return float.Lerp(float.Lerp(a, b, ty), float.Lerp(c, d, ty), tz);
    }

    private static float Smooth(float t) => t * t * (3 - 2 * t);

    public void Dispose() => _gl.DeleteTexture(_texture);
}
