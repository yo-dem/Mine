using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// The inventory's icons: each material's model (<see cref="ItemMeshes"/>) turning slowly in its
/// slot, drawn over the finished image in pixels (origin top left) with a simple fixed light.
/// </summary>
public sealed unsafe class IconRenderer : IDisposable
{
    private const int FloatsPerVertex = 10;

    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec3 aColor;
        layout(location = 3) in float aEmissive;
        uniform mat4 uTransform; // model to screen
        uniform mat4 uTurn;      // the model's rotation, for the normals
        out vec3 vNormal;
        out vec3 vColor;
        out float vEmissive;
        void main()
        {
            vNormal = mat3(uTurn) * aNormal;
            vColor = aColor;
            vEmissive = aEmissive;
            gl_Position = uTransform * vec4(aPos, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        in vec3 vNormal;
        in vec3 vColor;
        in float vEmissive;
        out vec4 FragColor;
        void main()
        {
            vec3 n = normalize(vNormal);
            float light = 0.45 + 0.75 * max(dot(n, normalize(vec3(-0.4, 0.8, 0.5))), 0.0);
            vec3 color = mix(vColor * light * 1.2, vColor * 1.7, vEmissive * 0.6);
            FragColor = vec4(min(color, vec3(1.0)), 1.0);
        }
        """;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly (uint Vao, uint Vbo, int Count)[] _models;

    public IconRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _models = Enum.GetValues<Resource>().Select(r => Upload(ItemMeshes.Item(r))).ToArray();
    }

    /// <summary>Starts drawing icons over a screen of this size (depth cleared, so each model sorts its own faces).</summary>
    public void Begin(int width, int height)
    {
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _gl.DepthMask(true);
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace); // the screen's y points down: faces come out mirrored
        _shader.Use();
        _screen = Matrix4x4.CreateOrthographicOffCenter(0, width, height, 0, -1000, 1000);
    }

    private Matrix4x4 _screen;

    /// <summary>A material's model centred on (x, y), <paramref name="size"/> pixels across, turned by <paramref name="angle"/>.</summary>
    public void Draw(Resource resource, float x, float y, float size, float angle)
    {
        var turn = Matrix4x4.CreateRotationY(angle) * Matrix4x4.CreateRotationX(0.45f);
        var transform = turn * Matrix4x4.CreateScale(size, -size, size) * Matrix4x4.CreateTranslation(x, y, 0) * _screen;
        _shader.Set("uTransform", transform);
        _shader.Set("uTurn", turn);
        var (vao, _, count) = _models[(int)resource];
        _gl.BindVertexArray(vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)count);
    }

    public void End() => _gl.Enable(EnableCap.CullFace);

    private (uint, uint, int) Upload(List<float> vertices)
    {
        uint vao = _gl.GenVertexArray(), vbo = _gl.GenBuffer();
        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        var data = vertices.ToArray();
        fixed (float* p = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        uint stride = FloatsPerVertex * sizeof(float);
        for (uint i = 0; i < 4; i++)
        {
            int size = i == 3 ? 1 : 3;
            _gl.VertexAttribPointer(i, size, VertexAttribPointerType.Float, false, stride, (void*)(new[] { 0, 3, 6, 9 }[i] * sizeof(float)));
            _gl.EnableVertexAttribArray(i);
        }
        _gl.BindVertexArray(0);
        return (vao, vbo, data.Length / FloatsPerVertex);
    }

    public void Dispose()
    {
        foreach (var (vao, vbo, _) in _models)
        {
            _gl.DeleteBuffer(vbo);
            _gl.DeleteVertexArray(vao);
        }
        _shader.Dispose();
    }
}
