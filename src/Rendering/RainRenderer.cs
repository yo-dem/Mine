using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Rain: a dense fall of very thin, faintly luminous silver threads, slanting in the wind around
/// the camera. Like the motes there is no particle data: each drop derives its place and speed
/// from its index, wrapped into a box that follows the camera, and is drawn as a thin quad along
/// its fall that faces the camera, additively blended, hidden by what stands in front of it.
/// Drops are never thinner than about a pixel (thinner ones would flicker): past that they get
/// fainter instead.
/// </summary>
public sealed class RainRenderer : IDisposable
{
    private const int Count = 20000;

    private const string VertexSource = "#version 330 core\n" + SkyRenderer.Hash + """
        uniform mat4 uViewProj;
        uniform vec3 uCameraPos;
        uniform float uTime;
        uniform float uRain;
        uniform float uNight;
        uniform float uPixelScale; // viewport height / (2 tan(fov / 2)): pixels per metre at 1 m

        out vec3 vColor;
        out float vAcross;

        const vec3 Box = vec3(44.0, 30.0, 44.0);

        void main()
        {
            // Six vertices per drop: two triangles of a quad along the fall.
            int drop = gl_VertexID / 6, corner = gl_VertexID % 6;
            float id = float(drop);
            vec3 seed = vec3(hash13(vec3(id, 1.0, 7.0)), hash13(vec3(id, 2.0, 3.0)), hash13(vec3(id, 3.0, 5.0)));
            float kind = hash13(vec3(id, 4.0, 1.0));

            // Drops appear gradually as the rain thickens: each has its own threshold.
            float present = smoothstep(kind * 0.9, kind * 0.9 + 0.1, uRain);

            vec3 fall = normalize(vec3(0.22, -1.0, 0.1));
            float speed = 9.0 + 5.0 * seed.y;
            vec3 home = seed * Box + fall * speed * uTime;
            vec3 center = uCameraPos + vec3(0.0, 4.0, 0.0);
            vec3 rel = mod(home - center, Box) - Box * 0.5;
            vec3 pos = center + rel;

            // A thin quad along the fall, turned toward the camera: triangles (0, 1, 2) and (3, 4, 5)
            // over the corners (head, left), (tail, left), (tail, right) / (head, left), (tail, right), (head, right).
            float along = (corner == 1 || corner == 2 || corner == 4) ? 1.0 : 0.0;
            float side = (corner == 0 || corner == 1 || corner == 3) ? -1.0 : 1.0;
            float streakLength = 0.45 + 0.4 * seed.z;
            vec3 toCamera = normalize(uCameraPos - pos);
            vec3 across = normalize(cross(fall, toCamera));
            float thin = 0.0035;
            float pixel = 0.7 * distance(pos, uCameraPos) / uPixelScale;
            float width = max(thin, pixel);
            pos += -fall * streakLength * along + across * side * width;

            // Fade at the edges of the box, so nothing pops, and very close to the eyes.
            vec3 edge = abs(rel) / (Box * 0.5);
            float fade = smoothstep(1.0, 0.7, max(edge.x, max(edge.y, edge.z))) * smoothstep(0.6, 2.0, distance(pos, uCameraPos));

            // Pale silver, a touch brighter at night (the rain catches the glow of the world).
            vec3 silver = vec3(0.8, 0.82, 0.9) * mix(0.22, 0.32, uNight) * (0.7 + 0.3 * seed.x);
            // The head of the streak (its lower end) is the brightest.
            vColor = silver * (thin / width) * mix(1.0, 0.35, along) * fade * present;
            vAcross = side;
            gl_Position = uViewProj * vec4(pos, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        in vec3 vColor;
        in float vAcross;
        out vec4 FragColor;

        void main()
        {
            // Soft across the streak, so it reads as a thread of light, not a hard line.
            float soft = 1.0 - vAcross * vAcross;
            FragColor = vec4(vColor * soft, 1.0);
        }
        """;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;

    public RainRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _vao = gl.GenVertexArray();
    }

    /// <summary>Draws the rain into the HDR scene (after everything that can hide it).</summary>
    public void Draw(Matrix4x4 viewProjection, Vector3 camera, float time, float rain, float night, float pixelScale)
    {
        if (rain <= 0.001f) return;
        _shader.Use();
        _shader.Set("uViewProj", viewProjection);
        _shader.Set("uCameraPos", camera);
        _shader.Set("uTime", time);
        _shader.Set("uRain", rain);
        _shader.Set("uNight", night);
        _shader.Set("uPixelScale", pixelScale);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, Count * 6);
        _gl.Enable(EnableCap.CullFace);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }
}
