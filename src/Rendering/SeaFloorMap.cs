using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// The smooth shape of the land (<see cref="TerrainField.SmoothHeight"/>, before it is cut into
/// layers) around the camera, as a small float texture the water shader reads to lay its breaking
/// waves along smooth depth contours: the layered tiles would make them run in blocky steps.
/// Computed on the thread pool whenever the camera has moved far enough, then uploaded.
/// </summary>
public sealed unsafe class SeaFloorMap : IDisposable
{
    public const int Unit = 6;
    private const int Resolution = 256;
    public const float Extent = 512f;           // metres covered, centred on the camera
    private const float Recenter = Extent / 8f; // rebuild after moving this far (also the grid it snaps to)

    private readonly GL _gl;
    private readonly TerrainField _terrain;
    private readonly uint _texture;
    private Vector2 _origin = new(float.NaN);   // world xz of the texture's corner, once uploaded
    private Vector2 _building = new(float.NaN); // corner of the map being built
    private Task<float[]>? _task;

    public SeaFloorMap(GL gl, TerrainField terrain)
    {
        _gl = gl;
        _terrain = terrain;
        _texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _texture);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R32f, Resolution, Resolution, 0, PixelFormat.Red, PixelType.Float, (void*)0);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    /// <summary>World xz of the map's corner (NaN until the first map is ready).</summary>
    public Vector2 Origin => _origin;

    public void Update(Vector3 camera)
    {
        if (_task is { IsCompleted: true } done)
        {
            var heights = done.Result;
            _gl.BindTexture(TextureTarget.Texture2D, _texture);
            fixed (float* p = heights)
                _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, Resolution, Resolution, PixelFormat.Red, PixelType.Float, p);
            _origin = _building;
            _task = null;
        }
        if (_task is not null) return;

        var corner = new Vector2(MathF.Round(camera.X / Recenter) * Recenter, MathF.Round(camera.Z / Recenter) * Recenter) - new Vector2(Extent / 2);
        if (corner == _origin) return;
        _building = corner;
        _task = Task.Run(() =>
        {
            var heights = new float[Resolution * Resolution];
            float step = Extent / Resolution;
            for (int j = 0; j < Resolution; j++)
            for (int i = 0; i < Resolution; i++)
                heights[j * Resolution + i] = _terrain.SmoothHeight(corner.X + (i + 0.5f) * step, corner.Y + (j + 0.5f) * step);
            return heights;
        });
    }

    public void Bind()
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + Unit);
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    public void Dispose() => _gl.DeleteTexture(_texture);
}
