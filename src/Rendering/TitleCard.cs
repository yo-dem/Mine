using System.Numerics;

namespace Mine.Rendering;

/// <summary>
/// The title in the black at the start: "Alone" as holes in the black through which the world
/// shows. Its own pixel lettering: a big, lopsided A, then "lone" in much smaller letters,
/// each set a little higher or lower than the last and floating gently. The holes open pixel by
/// pixel, as if the word were being written left to right (<see cref="WriteStart"/> ..
/// <see cref="Written"/>). Drawn with the HUD's rectangles: the black is everything but the title's
/// pixels, cut into horizontal bands where each pixel lets through as much as it has opened.
/// </summary>
public static class TitleCard
{
    /// <summary>When the first hole starts opening, and when the last one is fully open (seconds).</summary>
    public const float WriteStart = 0.3f, Written = 2.7f;

    private const float PixelOpenSeconds = 0.2f;

    private readonly record struct Letter(string[] Rows, int X, int Baseline, float Start, float Duration);

    // The glyphs, row 0 at the top ('#' lit). The A is 24 rows tall, the others 9.
    // A big, lopsided A: the apex leans right, the left leg is thin and sweeps far out, the right
    // one thick and nearly upright on a broad foot, the crossbar swings out past the left leg.
    private static readonly string[] A =
    [
        ".............###......",
        "............####......",
        "............####......",
        "...........##.##......",
        "...........##..##.....",
        "..........##...###....",
        "..........##...###....",
        ".........##....###....",
        ".........##....###....",
        "........##.....###....",
        "........##.....###....",
        ".......##......###....",
        "......##........###...",
        "..#...##........###...",
        "..#################...",
        "..##################..",
        ".##.##..........####..",
        "....##..........####..",
        "...##...........####..",
        "...##...........####..",
        "..##.............####.",
        "..##.............####.",
        "###..............#####",
        "##.............#######",
    ];
    private static readonly string[] L =
    [
        "###....",
        ".##....",
        ".##....",
        ".##....",
        ".##....",
        ".##....",
        ".##...#",
        ".######",
        "#######",
    ];
    private static readonly string[] O =
    [
        "..####..",
        ".##..##.",
        "##....##",
        "##....##",
        "##....##",
        "##....##",
        "##....##",
        ".##..##.",
        "..####..",
    ];
    private static readonly string[] N =
    [
        "##....##",
        "###...##",
        "####..##",
        "##.##.##",
        "##..####",
        "##...###",
        "##....##",
        "##....##",
        "##....##",
    ];
    private static readonly string[] E =
    [
        "#######",
        "##.....",
        "##.....",
        "##.....",
        "######.",
        "##.....",
        "##.....",
        "##.....",
        "#######",
    ];

    // Layout in glyph pixels: x of each letter's left edge, and its baseline (bottom row) measured
    // from the A's, positive upward: "lone" steps up and down beside the A. Each letter's writing
    // starts and lasts (seconds after WriteStart): the A first and slowest.
    private static readonly Letter[] Letters =
    [
        new(A, 0, 0, 0f, 0.8f),
        new(L, 24, 1, 0.75f, 0.35f),
        new(O, 32, 4, 1.05f, 0.35f),
        new(N, 41, 0, 1.35f, 0.35f),
        new(E, 50, 3, 1.65f, 0.35f),
    ];
    private const int WidthPixels = 57;

    private readonly record struct Cell(int X, int Y, int Size, float Open);

    private static readonly List<Cell> Cells = new();
    private static readonly List<int> Edges = new();

    /// <summary>
    /// Draws the black (<paramref name="black"/> its opacity) with the title cut into it, at
    /// <paramref name="time"/> seconds since the start. Call between Hud.Begin and Hud.End.
    /// </summary>
    public static void Draw(Hud hud, int width, int height, float time, float black)
    {
        var dark = new Vector4(0f, 0f, 0f, black);
        int size = Math.Max(2, (int)(width * 0.55f / WidthPixels));
        int left = (width - WidthPixels * size) / 2;
        int bottom = (height + 24 * size) / 2; // the A's baseline, the A centred

        Cells.Clear();
        for (int k = 0; k < Letters.Length; k++)
        {
            var letter = Letters[k];
            // "lone" floats gently, each letter at its own pace; the A stands still.
            float drift = k == 0 ? 0f : MathF.Sin(time * 1.4f + k * 1.7f) * 0.45f;
            int x0 = left + letter.X * size;
            int y0 = bottom - (int)MathF.Round((letter.Baseline + drift) * size) - letter.Rows.Length * size;
            int w = letter.Rows[0].Length, h = letter.Rows.Length;
            for (int row = 0; row < h; row++)
            for (int column = 0; column < w; column++)
            {
                if (letter.Rows[row][column] != '#') continue;
                // Written like a pen stroke: left to right, a little top to bottom, a touch uneven.
                float along = (column + row * 0.25f) / (w + h * 0.25f);
                float jitter = Hash(k * 131 + column * 17 + row * 29) * 0.08f;
                float opens = WriteStart + letter.Start + along * letter.Duration + jitter;
                float t = Math.Clamp((time - opens) / PixelOpenSeconds, 0f, 1f);
                Cells.Add(new Cell(x0 + column * size, y0 + row * size, size, t * t * (3f - 2f * t)));
            }
        }

        // The black: everything but the cells, band by band between their top and bottom edges;
        // each cell keeps as much black as it has not opened yet.
        Edges.Clear();
        Edges.Add(0);
        Edges.Add(height);
        foreach (var c in Cells)
        {
            Edges.Add(Math.Clamp(c.Y, 0, height));
            Edges.Add(Math.Clamp(c.Y + c.Size, 0, height));
        }
        Edges.Sort();
        var band = new List<Cell>();
        for (int e = 0; e + 1 < Edges.Count; e++)
        {
            int y = Edges[e], y1 = Edges[e + 1];
            if (y1 <= y) continue;
            band.Clear();
            foreach (var c in Cells)
                if (c.Y <= y && c.Y + c.Size >= y1) band.Add(c);
            band.Sort((a, b) => a.X.CompareTo(b.X));
            int x = 0;
            foreach (var c in band)
            {
                if (c.X > x) hud.Rect(x, y, c.X - x, y1 - y, dark);
                int from = Math.Max(c.X, x);
                if (c.X + c.Size > from) hud.Rect(from, y, c.X + c.Size - from, y1 - y, dark with { W = black * (1f - c.Open) });
                x = Math.Max(x, c.X + c.Size);
            }
            if (x < width) hud.Rect(x, y, width - x, y1 - y, dark);
        }
    }

    private static float Hash(int n)
    {
        unchecked
        {
            uint h = (uint)n * 374761393u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }
    }
}
