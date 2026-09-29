using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// The hotbar at the bottom of the screen: one square per inventory slot, each with its item's 3D
/// model turning inside and the stack's count in small pixel digits; the slot in hand is framed
/// in bright cyan. Also owns the items' models (<see cref="DrawItem"/>), which the game uses for
/// the item held in view and the drops on the ground, all in the object vertex layout and
/// centred on the origin, about 1 unit across.
/// </summary>
public sealed unsafe class Hud : IDisposable
{
    private const string FlatVertex = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec4 aColor;
        uniform vec2 uScreen;
        out vec4 vColor;
        void main()
        {
            vColor = aColor;
            gl_Position = vec4(aPos / uScreen * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string FlatFragment = """
        #version 330 core
        in vec4 vColor;
        out vec4 FragColor;
        void main() { FragColor = vColor; }
        """;

    private const string ItemVertex = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec3 aColor;
        layout(location = 3) in float aEmissive;
        uniform mat4 uModel;
        uniform mat4 uViewProj;
        out vec3 vNormal;
        out vec3 vColor;
        out float vEmissive;
        void main()
        {
            vNormal = mat3(uModel) * aNormal;
            vColor = aColor;
            vEmissive = aEmissive;
            gl_Position = uViewProj * (uModel * vec4(aPos, 1.0));
        }
        """;

    private const string ItemFragment = """
        #version 330 core
        in vec3 vNormal;
        in vec3 vColor;
        in float vEmissive;
        out vec4 FragColor;
        void main()
        {
            vec3 n = normalize(vNormal);
            float light = 0.45 + 0.6 * max(dot(n, normalize(vec3(-0.4, 0.8, 0.6))), 0.0);
            vec3 color = mix(vColor * light, vColor * 1.5, vEmissive);
            FragColor = vec4(color, 1.0);
        }
        """;

    // 3 × 5 pixel digits, rows from the top.
    private static readonly string[] Digits =
    [
        "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
        "111100111001111", "111100111101111", "111001010010010", "111101111101111", "111101111001111",
    ];

    private readonly GL _gl;
    private readonly Shader _flat, _item;
    private readonly uint _flatVao, _flatVbo;
    private readonly List<float> _quads = new();
    private readonly Dictionary<Item, (uint Vao, uint Vbo, int Count)> _models = new();

    public Hud(GL gl)
    {
        _gl = gl;
        _flat = new Shader(gl, FlatVertex, FlatFragment);
        _item = new Shader(gl, ItemVertex, ItemFragment);
        _flatVao = gl.GenVertexArray();
        _flatVbo = gl.GenBuffer();
        gl.BindVertexArray(_flatVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _flatVbo);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)(2 * sizeof(float)));
        gl.EnableVertexAttribArray(1);
        gl.BindVertexArray(0);
    }

    /// <summary>Draws the item's model with whatever shader is bound (object vertex layout).</summary>
    public void DrawItem(Item item)
    {
        var (vao, _, count) = Model(item);
        _gl.BindVertexArray(vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)count);
    }

    private (uint Vao, uint Vbo, int Count) Model(Item item)
    {
        if (_models.TryGetValue(item, out var model)) return model;
        var m = new MeshBuilder();
        switch (item.Kind)
        {
            case ItemKind.Block:
                m.Box(new Vector3(-0.5f), new Vector3(0.5f), Materials.Color(item.Material));
                break;
            default:
            {
                // The crystal object's model, stood on its middle and scaled to fit.
                var crystal = ObjectRenderer.BuildMesh(ObjectKind.Crystal);
                for (int i = 0; i < crystal.Count; i += 10)
                    m.Vertex(new Vector3(crystal[i], crystal[i + 1] - 0.6f, crystal[i + 2]) * 0.8f,
                             new Vector3(crystal[i + 3], crystal[i + 4], crystal[i + 5]),
                             new Vector3(crystal[i + 6], crystal[i + 7], crystal[i + 8]), crystal[i + 9]);
                break;
            }
        }
        uint vao = _gl.GenVertexArray(), vbo = _gl.GenBuffer();
        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        var data = m.Vertices.ToArray();
        fixed (float* p = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        uint stride = 10 * sizeof(float);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)(9 * sizeof(float)));
        _gl.EnableVertexAttribArray(3);
        _gl.BindVertexArray(0);
        return _models[item] = (vao, vbo, data.Length / 10);
    }

    /// <summary>Draws the hotbar over the finished frame (the default framebuffer, <paramref name="width"/> × <paramref name="height"/> pixels).</summary>
    public void Draw(Inventory inventory, int width, int height, float time)
    {
        if (width == 0 || height == 0) return;
        float unit = MathF.Max(0.75f, height / 720f);
        int size = (int)(54 * unit), gap = (int)(6 * unit), border = Math.Max(2, (int)(2 * unit));
        int total = Inventory.SlotCount * size + (Inventory.SlotCount - 1) * gap;
        int x0 = (width - total) / 2, y0 = (int)(16 * unit);

        // The squares.
        _quads.Clear();
        for (int i = 0; i < Inventory.SlotCount; i++)
        {
            int x = x0 + i * (size + gap);
            bool selected = i == inventory.SelectedSlot;
            Rect(x, y0, size, size, new Vector4(0.08f, 0.05f, 0.14f, selected ? 0.6f : 0.45f));
            var edge = selected ? new Vector4(0.55f, 0.95f, 1f, 1f) : new Vector4(0.85f, 0.8f, 1f, 0.35f);
            int b = selected ? border + 1 : border;
            Rect(x - (selected ? 1 : 0), y0 - (selected ? 1 : 0), size + (selected ? 2 : 0), b, edge);
            Rect(x - (selected ? 1 : 0), y0 + size - b + (selected ? 1 : 0), size + (selected ? 2 : 0), b, edge);
            Rect(x - (selected ? 1 : 0), y0, b, size, edge);
            Rect(x + size - b + (selected ? 1 : 0), y0, b, size, edge);
        }
        _gl.Disable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        DrawQuads(width, height);

        // The items, each turning in its own little view.
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _item.Use();
        var viewProj = Matrix4x4.CreateLookAt(new Vector3(0, 0, 3.4f), Vector3.Zero, Vector3.UnitY)
                       * Matrix4x4.CreatePerspectiveFieldOfView(0.5f, 1f, 0.1f, 10f);
        _item.Set("uViewProj", viewProj);
        for (int i = 0; i < Inventory.SlotCount; i++)
        {
            if (inventory.Slot(i).Item is not { } item) continue;
            int x = x0 + i * (size + gap);
            _gl.Viewport(x, y0, (uint)size, (uint)size);
            _gl.Scissor(x, y0, (uint)size, (uint)size);
            _gl.Clear(ClearBufferMask.DepthBufferBit);
            bool selected = i == inventory.SelectedSlot;
            float spin = time * (selected ? 1.6f : 0.7f) + i * 0.9f;
            float scale = item.Kind == ItemKind.Block ? (selected ? 0.95f : 0.8f) : (selected ? 1.25f : 1.05f);
            _item.Set("uModel", Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(spin) * Matrix4x4.CreateRotationX(0.45f));
            DrawItem(item);
        }
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Viewport(0, 0, (uint)width, (uint)height);

        // The counts, in the bottom right corner of each square.
        _quads.Clear();
        int pixel = Math.Max(2, size / 18);
        for (int i = 0; i < Inventory.SlotCount; i++)
        {
            var (item, count) = inventory.Slot(i);
            if (item is null || count < 2) continue;
            string text = count.ToString();
            int right = x0 + i * (size + gap) + size - pixel * 2;
            int x = right - text.Length * pixel * 4 + pixel;
            foreach (char c in text)
            {
                Digit(x + pixel / 2 + 1, y0 + pixel * 2 - pixel / 2 - 1, pixel, c - '0', new Vector4(0.05f, 0.02f, 0.1f, 0.9f)); // shadow
                Digit(x, y0 + pixel * 2, pixel, c - '0', new Vector4(1f, 0.97f, 1f, 1f));
                x += pixel * 4;
            }
        }
        _gl.Disable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        DrawQuads(width, height);
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
    }

    private void Digit(int x, int y, int pixel, int digit, Vector4 color)
    {
        string bits = Digits[digit];
        for (int row = 0; row < 5; row++)
        for (int col = 0; col < 3; col++)
            if (bits[row * 3 + col] == '1') Rect(x + col * pixel, y + (4 - row) * pixel, pixel, pixel, color);
    }

    private void Rect(float x, float y, float w, float h, Vector4 c)
    {
        ReadOnlySpan<Vector2> corners = [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y), new(x + w, y + h), new(x, y + h)];
        foreach (var p in corners)
        {
            _quads.Add(p.X); _quads.Add(p.Y);
            _quads.Add(c.X); _quads.Add(c.Y); _quads.Add(c.Z); _quads.Add(c.W);
        }
    }

    private void DrawQuads(int width, int height)
    {
        if (_quads.Count == 0) return;
        _flat.Use();
        _flat.Set("uScreen", new Vector2(width, height));
        _gl.BindVertexArray(_flatVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _flatVbo);
        var data = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_quads);
        fixed (float* p = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(_quads.Count / 6));
    }

    public void Dispose()
    {
        foreach (var (vao, vbo, _) in _models.Values)
        {
            _gl.DeleteBuffer(vbo);
            _gl.DeleteVertexArray(vao);
        }
        _gl.DeleteBuffer(_flatVbo);
        _gl.DeleteVertexArray(_flatVao);
        _flat.Dispose();
        _item.Dispose();
    }
}
