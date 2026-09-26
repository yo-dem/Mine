namespace Mine.World;

/// <summary>Classic 2D Perlin noise with fractal (fBm) layering.</summary>
public sealed class PerlinNoise
{
    private readonly int[] _perm = new int[512];

    public PerlinNoise(int seed)
    {
        var p = new int[256];
        for (int i = 0; i < 256; i++) p[i] = i;
        new Random(seed).Shuffle(p);
        for (int i = 0; i < 512; i++) _perm[i] = p[i & 255];
    }

    /// <summary>Returns a value roughly in [-1, 1].</summary>
    public float Noise(float x, float y)
    {
        float fx = MathF.Floor(x), fy = MathF.Floor(y);
        int xi = (int)fx & 255, yi = (int)fy & 255;
        float xf = x - fx, yf = y - fy;
        float u = Fade(xf), v = Fade(yf);

        int aa = _perm[_perm[xi] + yi];
        int ab = _perm[_perm[xi] + yi + 1];
        int ba = _perm[_perm[xi + 1] + yi];
        int bb = _perm[_perm[xi + 1] + yi + 1];

        float x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1, yf), u);
        float x2 = Lerp(Grad(ab, xf, yf - 1), Grad(bb, xf - 1, yf - 1), u);
        return Lerp(x1, x2, v);
    }

    /// <summary>Sum of several octaves of noise, normalised to about [-1, 1].</summary>
    public float Fractal(float x, float y, int octaves, float lacunarity = 2f, float gain = 0.5f)
    {
        float sum = 0, amplitude = 1, frequency = 1, norm = 0;
        for (int i = 0; i < octaves; i++)
        {
            sum += Noise(x * frequency, y * frequency) * amplitude;
            norm += amplitude;
            amplitude *= gain;
            frequency *= lacunarity;
        }
        return sum / norm;
    }

    private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Grad(int hash, float x, float y) => (hash & 7) switch
    {
        0 => x + y,
        1 => -x + y,
        2 => x - y,
        3 => -x - y,
        4 => x,
        5 => -x,
        6 => y,
        _ => -y,
    };
}
