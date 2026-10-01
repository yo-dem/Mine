using System.Numerics;

namespace Mine.Rendering;

/// <summary>
/// The title in the black at the start: ALONE in huge, heavy, extended capitals (modelled on a
/// reference the user picked: a trapezoid A with a small triangular counter and a notch between
/// its feet, a wide L, a slightly oval O with a small round counter, an N cut by two diagonal
/// notches, an E with thin slits), white in the black. The letters are measured in cap heights (<see cref="LetterSpans"/>) and rasterised row by row at
/// screen resolution with the HUD's rectangles, so they stay crisp at any size. The caller says
/// how opaque the black and the letters are (Game.DrawLoadingFade: they fade out together).
/// </summary>
public static class TitleCard
{
    // Where each letter starts, in cap heights from the A's left edge, and the whole word's width.
    private static readonly float[] LetterX = [0f, 1.218f, 2.120f, 3.338f, 4.421f];
    private const float WordWidth = 5.331f;
    private const float Overshoot = 0.03f; // the O rises and sinks this much past the cap height

    private static readonly List<(float A, float B)> Spans = new();
    private static readonly List<(float X, Vector4 Color)> Row = new();

    /// <summary>The parts of letter <paramref name="k"/> on the row at height <paramref name="y"/> (0 at the cap line, 1 at the baseline), in cap heights from its left edge.</summary>
    private static void LetterSpans(int k, float y, List<(float, float)> spans)
    {
        switch (k)
        {
            case 0: // A: a trapezoid, minus the triangular counter and the notch between the feet
            {
                if (y < 0f || y > 1f) return;
                float left = 0.308f * (1f - y), right = 0.850f + 0.300f * y;
                float c0 = right, c1 = right;
                if (y > 0.383f && y < 0.645f) { float h = 0.285f * (y - 0.383f); (c0, c1) = (0.575f - h, 0.575f + h); }
                else if (y > 0.93f) { float h = 0.33f * (y - 0.93f); (c0, c1) = (0.406f - h, 0.744f + h); }
                Cut(spans, left, right, c0, c1);
                break;
            }
            case 1: // L
                if (y < 0f || y > 1f) return;
                spans.Add((0f, y < 0.647f ? 0.391f : 0.865f));
                break;
            case 2: // O: a slightly oval ring with a small counter
            {
                float dy = (y - 0.5f) / 0.53f;
                if (MathF.Abs(dy) >= 1f) return;
                float half = 0.564f * MathF.Sqrt(1f - dy * dy);
                float cy = (y - 0.51f) / 0.165f;
                float hole = MathF.Abs(cy) < 1f ? 0.155f * MathF.Sqrt(1f - cy * cy) : 0f;
                Cut(spans, 0.564f - half, 0.564f + half, 0.564f - hole, 0.564f + hole);
                break;
            }
            case 3: // N: a block cut from the top right and the bottom left by diagonal notches
            {
                if (y < 0f || y > 1f) return;
                float c0 = 1f, c1 = 1f;
                float top = 0.421f + 0.51f * y;
                if (top < 0.571f) (c0, c1) = (top, 0.571f);
                else if (y > 0.70f) (c0, c1) = (0.383f, 0.383f + 0.58f * (y - 0.70f));
                Cut(spans, 0f, 0.955f, c0, c1);
                break;
            }
            default: // E: a block with two thin slits from the right, the middle bar a touch shorter
                if (y < 0f || y > 1f) return;
                if ((y > 0.316f && y < 0.372f) || (y > 0.645f && y < 0.695f)) spans.Add((0f, 0.391f));
                else spans.Add((0f, y > 0.372f && y < 0.645f ? 0.887f : 0.910f));
                break;
        }
    }

    // The span a..b with c0..c1 taken out of it.
    private static void Cut(List<(float, float)> spans, float a, float b, float c0, float c1)
    {
        if (c1 <= c0 || c1 <= a || c0 >= b) { spans.Add((a, b)); return; }
        if (c0 > a) spans.Add((a, c0));
        if (c1 < b) spans.Add((c1, b));
    }

    /// <summary>
    /// Draws the black (<paramref name="black"/> its opacity) with the title in it, its letters of
    /// colour <paramref name="letters"/>, and under it a thin loading bar filled to
    /// <paramref name="progress"/> (0..1), as opaque as <paramref name="bar"/>. Call between
    /// Hud.Begin and Hud.End.
    /// </summary>
    public static void Draw(Hud hud, int width, int height, float black, Vector4 letters, float progress, float bar)
    {
        var dark = new Vector4(0f, 0f, 0f, black);
        float cap = MathF.Min(width * 0.94f / WordWidth, height * 0.6f);
        float left = (width - WordWidth * cap) / 2, top = (height - cap) / 2;
        int y0 = Math.Max(0, (int)MathF.Floor(top - Overshoot * cap)), y1 = Math.Min(height, (int)MathF.Ceiling(top + (1f + Overshoot) * cap));
        hud.Rect(0, 0, width, y0, dark);
        hud.Rect(0, y1, width, height - y1, dark);

        for (int py = y0; py < y1; py++)
        {
            float y = (py + 0.5f - top) / cap;
            Spans.Clear();
            for (int k = 0; k < LetterX.Length; k++)
            {
                int before = Spans.Count;
                LetterSpans(k, y, Spans);
                for (int i = before; i < Spans.Count; i++)
                    Spans[i] = (MathF.Round(left + (LetterX[k] + Spans[i].A) * cap), MathF.Round(left + (LetterX[k] + Spans[i].B) * cap));
            }

            // The row as runs of one colour: the black, and the letters.
            Row.Clear();
            foreach (var (a, b) in Spans)
            {
                if (b <= a) continue;
                Add(a, dark);
                Add(b, letters);
            }
            Add(width, dark);

            float from = 0f;
            foreach (var (to, color) in Row)
            {
                if (color.W > 0.002f && to > from) hud.Rect(from, py, to - from, 1, color);
                from = to;
            }
        }

        // The loading bar: a faint track half the word wide under it, and its filled part.
        if (bar > 0.002f)
        {
            float barWidth = WordWidth * cap * 0.5f, barHeight = MathF.Max(2f, MathF.Round(cap * 0.03f));
            float barX = MathF.Round((width - barWidth) / 2), barY = MathF.Round(top + cap * 1.22f);
            hud.Rect(barX, barY, barWidth, barHeight, new Vector4(1f, 1f, 1f, 0.18f * bar));
            hud.Rect(barX, barY, MathF.Round(barWidth * Math.Clamp(progress, 0f, 1f)), barHeight, new Vector4(1f, 1f, 1f, bar));
        }
    }

    // Extends the row up to x in this colour, merging with the run before if it is the same.
    private static void Add(float x, Vector4 color)
    {
        if (Row.Count > 0 && Row[^1].Color == color) Row[^1] = (x, color);
        else Row.Add((x, color));
    }
}
