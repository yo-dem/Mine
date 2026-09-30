using System.Globalization;
using System.Numerics;
using System.Text;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// The on-screen interface: text in a small pixel font (capitals, digits and punctuation; accented
/// letters lose their accent) and coloured rectangles, collected in pixels (origin top left) and
/// drawn in one batch over the finished image (<see cref="IconRenderer"/> draws the inventory's
/// models in between). The glyphs live in a 128×128 atlas of 8×8 cells generated at startup: a
/// character's cell is its code, and cell 255 is solid for rectangles.
/// </summary>
public sealed unsafe class Hud : IDisposable
{
    private const int AtlasSize = 128, CellPixels = 8, CellsPerRow = AtlasSize / CellPixels;
    private const int Middot = 127, Solid = 255;
    private const int GlyphWidth = 5, GlyphHeight = 7, Advance = 6;
    private const int FloatsPerVertex = 8; // position (2), uv (2), colour (4)

    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aUv;
        layout(location = 2) in vec4 aColor;
        uniform vec2 uScreen;
        out vec2 vUv;
        out vec4 vColor;
        void main()
        {
            vUv = aUv;
            vColor = aColor;
            gl_Position = vec4(aPos.x / uScreen.x * 2.0 - 1.0, 1.0 - aPos.y / uScreen.y * 2.0, 0.0, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        in vec2 vUv;
        in vec4 vColor;
        uniform sampler2D uAtlas;
        out vec4 FragColor;
        void main() { FragColor = vec4(vColor.rgb, vColor.a * texture(uAtlas, vUv).r); }
        """;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao, _vbo, _atlas;
    private readonly List<float> _vertices = new();
    private int _width, _height;

    public Hud(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        uint stride = FloatsPerVertex * sizeof(float);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)(4 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        gl.BindVertexArray(0);
        _atlas = CreateAtlas();
    }

    /// <summary>Pixels per pixel of the font: the interface grows with the screen.</summary>
    public int Scale => Math.Max(2, (int)MathF.Round(_height / 400f));

    public int Width => _width;
    public int Height => _height;

    public void Begin(int width, int height)
    {
        _width = width;
        _height = height;
        _vertices.Clear();
    }

    public void Rect(float x, float y, float w, float h, Vector4 color) => Cell(Solid, x, y, w, h, 1, 1, color);

    /// <summary>A rectangle outline <paramref name="thickness"/> pixels wide.</summary>
    public void Frame(float x, float y, float w, float h, float thickness, Vector4 color)
    {
        Rect(x, y, w, thickness, color);
        Rect(x, y + h - thickness, w, thickness, color);
        Rect(x, y + thickness, thickness, h - 2 * thickness, color);
        Rect(x + w - thickness, y + thickness, thickness, h - 2 * thickness, color);
    }

    /// <summary>Whether pixel (column, row) of a character's 5×7 glyph is lit (row 0 at the top).</summary>
    public static bool GlyphPixel(char c, int column, int row) =>
        Glyph.TryGetValue(c, out var rows) && row >= 0 && row < GlyphHeight && column >= 0 && column < GlyphWidth && rows[row][column] == '#';

    /// <summary>The width of a string in glyph pixels (the advance between letters included, none after the last).</summary>
    public static int TextPixels(string text) => Math.Max(0, text.Length * Advance - 1);

    /// <summary>The distance between the starts of two letters, in glyph pixels.</summary>
    public const int LetterAdvance = Advance;

    public static float TextWidth(string text, int scale) => Math.Max(0, Clean(text).Length * Advance - 1) * scale;

    /// <summary>Writes <paramref name="text"/> with its top left at (x, y), over a soft dark shadow; returns its width.</summary>
    public float Text(string text, float x, float y, int scale, Vector4 color, bool shadow = true)
    {
        string clean = Clean(text);
        if (shadow)
        {
            var dark = new Vector4(0.05f, 0.03f, 0.1f, 0.75f * color.W);
            Glyphs(clean, x + scale, y + scale, scale, dark);
        }
        Glyphs(clean, x, y, scale, color);
        return TextWidth(text, scale);
    }

    /// <summary>Writes centred on <paramref name="centerX"/>.</summary>
    public void TextCentered(string text, float centerX, float y, int scale, Vector4 color) =>
        Text(text, centerX - TextWidth(text, scale) / 2, y, scale, color);

    private void Glyphs(string clean, float x, float y, int scale, Vector4 color)
    {
        foreach (char c in clean)
        {
            if (c != ' ') Cell(c, x, y, GlyphWidth * scale, GlyphHeight * scale, GlyphWidth, GlyphHeight, color);
            x += Advance * scale;
        }
    }

    // Upper case, accents dropped, and anything the font lacks shown as a space.
    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD).ToUpperInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c == '·' ? (char)Middot : c < 128 && Glyph.ContainsKey(c) ? c : ' ');
        }
        return sb.ToString();
    }

    private void Cell(int index, float x, float y, float w, float h, int texelsX, int texelsY, Vector4 color)
    {
        float u0 = (index % CellsPerRow * CellPixels) / (float)AtlasSize, v0 = (index / CellsPerRow * CellPixels) / (float)AtlasSize;
        float u1 = u0 + texelsX / (float)AtlasSize, v1 = v0 + texelsY / (float)AtlasSize;
        if (index == Solid) { u0 += 2f / AtlasSize; v0 += 2f / AtlasSize; u1 = u0 + 1f / AtlasSize; v1 = v0 + 1f / AtlasSize; }
        Vertex(x, y, u0, v0, color); Vertex(x + w, y, u1, v0, color); Vertex(x + w, y + h, u1, v1, color);
        Vertex(x, y, u0, v0, color); Vertex(x + w, y + h, u1, v1, color); Vertex(x, y + h, u0, v1, color);
    }

    private void Vertex(float x, float y, float u, float v, Vector4 c)
    {
        _vertices.Add(x); _vertices.Add(y); _vertices.Add(u); _vertices.Add(v);
        _vertices.Add(c.X); _vertices.Add(c.Y); _vertices.Add(c.Z); _vertices.Add(c.W);
    }

    /// <summary>Draws everything collected since <see cref="Begin"/> over the screen.</summary>
    public void End()
    {
        if (_vertices.Count == 0) return;
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        var data = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_vertices);
        fixed (float* p = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);

        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _shader.Use();
        _shader.Set("uScreen", new Vector2(_width, _height));
        _shader.Set("uAtlas", 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _atlas);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(data.Length / FloatsPerVertex));
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.CullFace);
        _gl.Enable(EnableCap.DepthTest);
    }

    private uint CreateAtlas()
    {
        var pixels = new byte[AtlasSize * AtlasSize];
        void Paint(int index, string[] rows)
        {
            int ox = index % CellsPerRow * CellPixels, oy = index / CellsPerRow * CellPixels;
            for (int y = 0; y < rows.Length; y++)
            for (int x = 0; x < rows[y].Length; x++)
                pixels[(oy + y) * AtlasSize + ox + x] = rows[y][x] switch { '#' => 255, '+' => 150, _ => 0 };
        }
        foreach (var (c, rows) in Glyph) Paint(c, rows);
        Paint(Middot, [".....", ".....", ".....", "..#..", ".....", ".....", "....."]);
        Paint(Solid, Enumerable.Repeat("########", 8).ToArray());

        uint texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = pixels)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8, AtlasSize, AtlasSize, 0, PixelFormat.Red, PixelType.UnsignedByte, p);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        return texture;
    }

    private static readonly Dictionary<char, string[]> Glyph = new()
    {
        ['A'] = [".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"],
        ['B'] = ["####.", "#...#", "#...#", "####.", "#...#", "#...#", "####."],
        ['C'] = [".###.", "#...#", "#....", "#....", "#....", "#...#", ".###."],
        ['D'] = ["###..", "#..#.", "#...#", "#...#", "#...#", "#..#.", "###.."],
        ['E'] = ["#####", "#....", "#....", "####.", "#....", "#....", "#####"],
        ['F'] = ["#####", "#....", "#....", "####.", "#....", "#....", "#...."],
        ['G'] = [".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".####"],
        ['H'] = ["#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"],
        ['I'] = [".###.", "..#..", "..#..", "..#..", "..#..", "..#..", ".###."],
        ['J'] = ["..###", "...#.", "...#.", "...#.", "...#.", "#..#.", ".##.."],
        ['K'] = ["#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#"],
        ['L'] = ["#....", "#....", "#....", "#....", "#....", "#....", "#####"],
        ['M'] = ["#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#"],
        ['N'] = ["#...#", "#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#"],
        ['O'] = [".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
        ['P'] = ["####.", "#...#", "#...#", "####.", "#....", "#....", "#...."],
        ['Q'] = [".###.", "#...#", "#...#", "#...#", "#.#.#", "#..#.", ".##.#"],
        ['R'] = ["####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#"],
        ['S'] = [".####", "#....", "#....", ".###.", "....#", "....#", "####."],
        ['T'] = ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.."],
        ['U'] = ["#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
        ['V'] = ["#...#", "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#.."],
        ['W'] = ["#...#", "#...#", "#...#", "#.#.#", "#.#.#", "#.#.#", ".#.#."],
        ['X'] = ["#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#"],
        ['Y'] = ["#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.."],
        ['Z'] = ["#####", "....#", "...#.", "..#..", ".#...", "#....", "#####"],
        ['0'] = [".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###."],
        ['1'] = ["..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###."],
        ['2'] = [".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####"],
        ['3'] = ["#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###."],
        ['4'] = ["...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#."],
        ['5'] = ["#####", "#....", "####.", "....#", "....#", "#...#", ".###."],
        ['6'] = ["..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###."],
        ['7'] = ["#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..."],
        ['8'] = [".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###."],
        ['9'] = [".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.."],
        [':'] = [".....", ".##..", ".##..", ".....", ".##..", ".##..", "....."],
        ['.'] = [".....", ".....", ".....", ".....", ".....", ".##..", ".##.."],
        [','] = [".....", ".....", ".....", ".....", ".##..", "..#..", ".#..."],
        ['-'] = [".....", ".....", ".....", "#####", ".....", ".....", "....."],
        ['+'] = [".....", "..#..", "..#..", "#####", "..#..", "..#..", "....."],
        ['/'] = [".....", "....#", "...#.", "..#..", ".#...", "#....", "....."],
        ['('] = ["...#.", "..#..", ".#...", ".#...", ".#...", "..#..", "...#."],
        [')'] = [".#...", "..#..", "...#.", "...#.", "...#.", "..#..", ".#..."],
        ['['] = [".###.", ".#...", ".#...", ".#...", ".#...", ".#...", ".###."],
        [']'] = [".###.", "...#.", "...#.", "...#.", "...#.", "...#.", ".###."],
        ['<'] = ["...#.", "..#..", ".#...", "#....", ".#...", "..#..", "...#."],
        ['>'] = [".#...", "..#..", "...#.", "....#", "...#.", "..#..", ".#..."],
        ['!'] = ["..#..", "..#..", "..#..", "..#..", "..#..", ".....", "..#.."],
        ['?'] = [".###.", "#...#", "....#", "...#.", "..#..", ".....", "..#.."],
        ['\''] = ["..#..", "..#..", ".#...", ".....", ".....", ".....", "....."],
        [' '] = [".....", ".....", ".....", ".....", ".....", ".....", "....."],
    };

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteTexture(_atlas);
        _shader.Dispose();
    }
}
