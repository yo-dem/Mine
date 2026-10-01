using System.Numerics;

namespace Mine.Rendering;

/// <summary>
/// The title in the black at the start: ALONE in huge, heavy, extended capitals (modelled on a
/// reference the user picked: a trapezoid A with a small triangular counter and a notch between
/// its feet, a wide L, a slightly oval O with a small round counter, an N cut by two diagonal
/// notches, an E with thin slits), as holes in the black through which the world shows. The
/// letters are measured in cap heights (<see cref="LetterSpans"/>) and rasterised row by row at
/// screen resolution with the HUD's rectangles, so they stay crisp at any size. The caller says
/// what the letters are (Game.DrawLoadingFade: white in the black, then see-through).
/// </summary>
public static class TitleCard
{
    // Where each letter starts, in cap heights from the A's left edge, and the whole word's width.
    private static readonly float[] LetterX = [0f, 1.218f, 2.120f, 3.338f, 4.421f];
    private const float WordWidth = 5.331f;
    private const float Overshoot = 0.03f; // the O rises and sinks this much past the cap height

    // Flying into the title: the point it grows around (the bottom of the O's ring, between its
    // counter and its outer edge).
    private static readonly Vector2 ZoomPivot = new(2.120f + 0.564f, 0.85f);

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
    /// colour <paramref name="letters"/> (alpha 0: holes the world shows through), and under it a
    /// thin loading bar filled to <paramref name="progress"/> (0..1), as opaque as <paramref name="bar"/>.
    /// <paramref name="zoom"/> (0..1) flies into the title, through the bottom of the O's ring
    /// (<see cref="ZoomPivot"/>), which slides to the middle of the screen, until it fills the screen
    /// and only the world is left. Call between Hud.Begin and Hud.End.
    /// </summary>
    public static void Draw(Hud hud, int width, int height, float black, Vector4 letters, float progress, float bar, float zoom = 0f)
    {
        var dark = new Vector4(0f, 0f, 0f, black);
        float cap = MathF.Min(width * 0.94f / WordWidth, height * 0.6f);
        float left = (width - WordWidth * cap) / 2, top = (height - cap) / 2;

        // Flying in: the black is a slab Thickness thick (in distances from the title at rest, 1)
        // that the view moves through, from 1 to the far side of it. Its front face is seen at a
        // scale of 1 / d, its back face at 1 / (d + thickness), both around the vanishing point,
        // the pivot on the O sliding to the middle of the screen; between the two edges of every
        // hole its inner wall shows. The thickness grows in at the start, so the title at rest is flat.
        bool front = true;
        float frontScale = 1f, backScale = 1f;
        var pivot = new Vector2(left + ZoomPivot.X * cap, top + ZoomPivot.Y * cap);
        if (zoom > 0f)
        {
            float t = Math.Clamp(zoom / 0.15f, 0f, 1f);
            float thickness = Thickness * t * t * (3f - 2f * t);
            // It ends just before the back face, already EndScale times the size it had at rest:
            // past its own walls, the O's opening then covers the screen.
            float d = 1f - zoom * (1f + thickness - 1f / EndScale);
            front = d > 1e-4f;
            frontScale = front ? MathF.Min(1f / d, 1e4f) : 0f;
            backScale = MathF.Min(1f / MathF.Max(d + thickness, 1e-4f), 1e4f);
            pivot = Vector2.Lerp(pivot, new Vector2(width / 2f, height / 2f), zoom);
        }
        float frontCap = cap * frontScale, backCap = cap * backScale;
        float frontLeft = pivot.X - ZoomPivot.X * frontCap, frontTop = pivot.Y - ZoomPivot.Y * frontCap;
        float backLeft = pivot.X - ZoomPivot.X * backCap, backTop = pivot.Y - ZoomPivot.Y * backCap;
        bool walls = zoom > 0f;

        // The rows the letters can touch (on either face); the rest is black (or, inside the slab, wall).
        float spanTop = MathF.Min(front ? frontTop : float.MaxValue, walls ? backTop : float.MaxValue);
        float spanBottom = MathF.Max(front ? frontTop + frontCap : float.MinValue, walls ? backTop + backCap : float.MinValue);
        if (!walls) (spanTop, spanBottom) = (frontTop, frontTop + frontCap);
        int y0 = Math.Clamp((int)MathF.Floor(spanTop - Overshoot * MathF.Max(frontCap, backCap)), 0, height);
        int y1 = Math.Clamp((int)MathF.Ceiling(spanBottom + Overshoot * MathF.Max(frontCap, backCap)), 0, height);
        // Past the front face the view is inside the hole: walls all round, shaded row by row.
        if (!front) (y0, y1) = (0, height);
        hud.Rect(0, 0, width, y0, dark);
        hud.Rect(0, y1, width, height - y1, dark);

        for (int py = y0; py < y1; py++)
        {
            RowSpans(py, frontLeft, frontTop, frontCap, Spans);
            if (walls) RowSpans(py, backLeft, backTop, backCap, BackSpans);
            else BackSpans.Clear();

            // The row cut at every edge of either face; each piece is black (outside the front's
            // holes), wall (in a front hole but outside the back's), or the letters (through both).
            Edges.Clear();
            Edges.Add(0f);
            Edges.Add(width);
            if (front) foreach (var (a, b) in Spans) { Edges.Add(Math.Clamp(a, 0, width)); Edges.Add(Math.Clamp(b, 0, width)); }
            if (walls) foreach (var (a, b) in BackSpans) { Edges.Add(Math.Clamp(a, 0, width)); Edges.Add(Math.Clamp(b, 0, width)); }
            Edges.Sort();
            Row.Clear();
            for (int e = 0; e + 1 < Edges.Count; e++)
            {
                float a = Edges[e], b = Edges[e + 1];
                if (b <= a) continue;
                float mid = (a + b) / 2;
                bool inFront = !front || Inside(Spans, mid), inBack = !walls || Inside(BackSpans, mid);
                if (!inFront || inBack) Add(b, !inFront ? dark : letters);
                else
                    // A wall, shaded in short pieces, so its light changes smoothly across it.
                    for (float x = a; x < b; x += WallPiece)
                        Add(MathF.Min(b, x + WallPiece), WallColor(MathF.Min(b, x + WallPiece / 2), py + 0.5f, pivot, black));
            }

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

    private const float Thickness = 0.1f, EndScale = 40f, WallPiece = 24f;
    private static readonly List<(float A, float B)> BackSpans = new();
    private static readonly List<float> Edges = new();

    // The letters' spans on screen row py, for the title laid out at (left, top) with this cap height.
    private static void RowSpans(int py, float left, float top, float cap, List<(float A, float B)> spans)
    {
        spans.Clear();
        float y = (py + 0.5f - top) / cap;
        if (y < -Overshoot || y > 1f + Overshoot) return;
        for (int k = 0; k < LetterX.Length; k++)
        {
            int before = spans.Count;
            LetterSpans(k, y, spans);
            for (int i = before; i < spans.Count; i++)
                spans[i] = (MathF.Round(left + (LetterX[k] + spans[i].A) * cap), MathF.Round(left + (LetterX[k] + spans[i].B) * cap));
        }
    }

    private static bool Inside(List<(float A, float B)> spans, float x)
    {
        foreach (var (a, b) in spans) if (x > a && x < b) return true;
        return false;
    }

    // The holes' inner walls: a very dark violet, lit from above, so the walls seen below the
    // vanishing point (facing up) are lighter than those above it (facing down), and the sides in between.
    private static Vector4 WallColor(float x, float y, Vector2 vanishing, float black)
    {
        var d = new Vector2(x, y) - vanishing;
        float light = 0.5f + 0.5f * d.Y / MathF.Max(d.Length(), 1f);
        light = MathF.Round(light * 12f) / 12f; // in a few steps, so runs merge
        return new Vector4(0.05f + 0.10f * light, 0.035f + 0.07f * light, 0.08f + 0.14f * light, black);
    }

    // Extends the row up to x in this colour, merging with the run before if it is the same.
    private static void Add(float x, Vector4 color)
    {
        if (Row.Count > 0 && Row[^1].Color == color) Row[^1] = (x, color);
        else Row.Add((x, color));
    }
}
