using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Where the blocks are around the camera, for the grass and plant shaders: an RG32F texture of
/// one texel per 0.5 m column of cells (<see cref="Texels"/>² around a centre snapped to
/// <see cref="Snap"/>): R is the height of the top of the column's highest block (or
/// <see cref="None"/>), G the highest such top in it and the 8 columns round it. A blade stays
/// only where it stands on the top of its column (on a mountain's ledge) or where no block is,
/// and nothing higher stands right beside it; so the grass leaves the ground under and round the
/// blocks, and a ledge whose block is broken. Rebuilt when the blocks change or the camera moves on.
/// </summary>
public sealed unsafe class BlockMask : IDisposable
{
    public const int Unit = 7;
    public const int Texels = 320; // 160 m: the grass reaches 70 m
    private const float Snap = 16f;

    private readonly GL _gl;
    private readonly uint _texture;
    public const float None = -1e9f;
    private readonly float[] _tops = new float[Texels * Texels];
    private readonly float[] _data = new float[Texels * Texels * 2];
    private int _version = -1;

    /// <summary>World X, Z of the texture's first texel corner.</summary>
    public Vector2 Origin { get; private set; } = new(float.NaN);

    public BlockMask(GL gl)
    {
        _gl = gl;
        _texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        Array.Fill(_data, None);
        fixed (float* p = _data)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.RG32f, Texels, Texels, 0, PixelFormat.RG, PixelType.Float, p);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    public void Update(BlockWorld blocks, Vector3 camera)
    {
        const float half = Texels * BlockWorld.CellSize / 2;
        var origin = new Vector2(MathF.Round(camera.X / Snap) * Snap - half, MathF.Round(camera.Z / Snap) * Snap - half);
        if (origin == Origin && blocks.Version == _version) return;
        Origin = origin;
        _version = blocks.Version;

        Array.Fill(_tops, None);
        int x0 = (int)MathF.Round(origin.X / BlockWorld.CellSize), z0 = (int)MathF.Round(origin.Y / BlockWorld.CellSize);
        foreach (var (x, z) in blocks.Columns)
        {
            int i = x - x0, j = z - z0;
            if (i < 0 || j < 0 || i >= Texels || j >= Texels) continue;
            if (blocks.Top(x, z) is { } top) _tops[j * Texels + i] = top;
        }
        for (int j = 0; j < Texels; j++)
        for (int i = 0; i < Texels; i++)
        {
            float highest = None;
            for (int dj = -1; dj <= 1; dj++)
            for (int di = -1; di <= 1; di++)
            {
                int u = i + di, v = j + dj;
                if (u >= 0 && v >= 0 && u < Texels && v < Texels) highest = MathF.Max(highest, _tops[v * Texels + u]);
            }
            _data[(j * Texels + i) * 2] = _tops[j * Texels + i];
            _data[(j * Texels + i) * 2 + 1] = highest;
        }
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        fixed (float* p = _data)
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, Texels, Texels, PixelFormat.RG, PixelType.Float, p);
    }

    public void Bind()
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + Unit);
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    public void Dispose() => _gl.DeleteTexture(_texture);
}
