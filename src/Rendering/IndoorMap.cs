using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// What is under the blocks the player placed (<see cref="Blocks.Indoors"/>) around the camera, as a
/// texture the shaders read so that motes, rain, snow and the flash of lightning stay out: one texel
/// per 1 m column over <see cref="Size"/>×<see cref="Size"/> columns, red 1 where there are blocks, green
/// and blue the heights between which it is indoors (the ground, and the bottom of the highest block).
/// Alpha holds how wet the ground looks where grass was sown (<see cref="Blocks.Plantings"/>), for
/// the terrain shader. Bound to <see cref="Unit"/>; <see cref="Glsl"/> reads it.
/// </summary>
public sealed unsafe class IndoorMap : IDisposable
{
    public const int Unit = 7;
    private const int Size = 128;

    /// <summary><c>indoors(p)</c>: 1 under a block, 0 elsewhere. Needs <c>uIndoor</c> and <c>uIndoorOrigin</c>.</summary>
    public const string Glsl = """
        uniform sampler2D uIndoor;
        uniform vec2 uIndoorOrigin; // world xz of the map's corner
        float indoors(vec3 p)
        {
            ivec2 c = ivec2(floor(p.xz - uIndoorOrigin)); // 1 m columns
            if (c.x < 0 || c.y < 0 || c.x >= 128 || c.y >= 128) return 0.0;
            vec4 v = texelFetch(uIndoor, c, 0);
            return v.r * step(v.g, p.y) * step(p.y, v.b);
        }
        // 0..1: how wet the ground of p's column looks (grass sown there, still growing).
        float wetGround(vec3 p)
        {
            ivec2 c = ivec2(floor(p.xz - uIndoorOrigin));
            if (c.x < 0 || c.y < 0 || c.x >= 128 || c.y >= 128) return 0.0;
            return texelFetch(uIndoor, c, 0).a;
        }
        """;

    private readonly GL _gl;
    private readonly uint _texture;
    private readonly float[] _data = new float[Size * Size * 4];
    private int _version = -1, _plantingsVersion = -1;
    private double _driedAt; // when the wet ground was last refreshed while drying
    private const double DryingRefresh = 0.25; // seconds between refreshes while sown ground dries
    private (int X, int Z) _corner = (int.MinValue, 0);

    /// <summary>World xz of the map's corner (far away until anything is enclosed near the camera).</summary>
    public Vector2 Origin { get; private set; } = new(-1e9f);

    public IndoorMap(GL gl)
    {
        _gl = gl;
        _texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _texture);
        fixed (float* p = _data)
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, Size, Size, 0, PixelFormat.Rgba, PixelType.Float, p);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
    }

    /// <summary>
    /// Refills the map when the blocks or the plantings change, the camera moves a quarter of the map
    /// away, or every <see cref="DryingRefresh"/> seconds while sown ground is drying.
    /// </summary>
    public void Update(Blocks blocks, Vector3 camera, double time)
    {
        var (cx, cz) = Blocks.ColumnOf(camera.X, camera.Z);
        bool moved = Math.Abs(cx - (_corner.X + Size / 2)) > Size / 4 || Math.Abs(cz - (_corner.Z + Size / 2)) > Size / 4;
        bool drying = time - _driedAt > DryingRefresh && blocks.GroundDrying;
        if (blocks.Version == _version && blocks.PlantingsVersion == _plantingsVersion && !moved && !drying) return;
        _version = blocks.Version;
        _plantingsVersion = blocks.PlantingsVersion;
        _driedAt = time;
        _corner = (cx - Size / 2, cz - Size / 2);
        Origin = new Vector2(_corner.X, _corner.Z);

        Array.Clear(_data);
        foreach (var ((x, z), bottom, top) in blocks.Ceilings())
        {
            int i = x - _corner.X, j = z - _corner.Z;
            if (i < 0 || j < 0 || i >= Size || j >= Size) continue;
            int k = (j * Size + i) * 4;
            _data[k] = 1f;
            _data[k + 1] = bottom;
            _data[k + 2] = top;
        }
        foreach (var ((x, z), wet) in blocks.Plantings())
        {
            int i = x - _corner.X, j = z - _corner.Z;
            if (wet <= 0f || i < 0 || j < 0 || i >= Size || j >= Size) continue;
            _data[(j * Size + i) * 4 + 3] = wet;
        }
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        fixed (float* p = _data)
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, Size, Size, PixelFormat.Rgba, PixelType.Float, p);
    }

    public void Bind()
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + Unit);
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    /// <summary>Sets <c>uIndoor</c> and <c>uIndoorOrigin</c> on a shader that pastes <see cref="Glsl"/>.</summary>
    public void SetUniforms(Shader shader)
    {
        shader.Set("uIndoor", Unit);
        shader.Set("uIndoorOrigin", Origin);
    }

    public void Dispose() => _gl.DeleteTexture(_texture);
}
