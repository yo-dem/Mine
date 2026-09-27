using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Snow: soft flakes drifting slowly down around the camera, swaying and carried a little by the
/// wind. Like the rain there is no particle data: each flake derives its place, speed and sway
/// from its index, wrapped into a box that follows the camera, and is drawn as a round point
/// sprite, blended over the scene (white by day, a faint blue-grey at night), hidden by what
/// stands in front of it.
/// </summary>
public sealed class SnowRenderer : IDisposable
{
    private const int Count = 9000;

    private const string VertexSource = "#version 330 core\n" + SkyRenderer.Hash + """
        uniform mat4 uViewProj;
        uniform vec3 uCameraPos;
        uniform float uTime;
        uniform float uSnow;
        uniform float uNight;
        uniform float uPixelScale; // viewport height / (2 tan(fov / 2)): pixels per metre at 1 m

        out vec3 vColor;
        out float vAlpha;

        const vec3 Box = vec3(40.0, 26.0, 40.0);

        void main()
        {
            float id = float(gl_VertexID);
            vec3 seed = vec3(hash13(vec3(id, 1.0, 7.0)), hash13(vec3(id, 2.0, 3.0)), hash13(vec3(id, 3.0, 5.0)));
            float kind = hash13(vec3(id, 4.0, 1.0));
            // Flakes appear gradually as the snowfall thickens: each has its own threshold.
            float present = smoothstep(kind * 0.9, kind * 0.9 + 0.1, uSnow);

            float speed = 0.9 + 0.8 * seed.y;
            vec3 wind = vec3(0.45, 0.0, 0.15);
            vec3 home = seed * Box + (wind - vec3(0.0, speed, 0.0)) * uTime;
            vec3 center = uCameraPos + vec3(0.0, 3.0, 0.0);
            vec3 rel = mod(home - center, Box) - Box * 0.5;
            float phase = kind * 40.0;
            vec3 sway = vec3(sin(uTime * 0.8 + phase), 0.0, cos(uTime * 0.6 + phase * 1.3)) * 0.5;
            vec3 pos = center + rel + sway;

            vec3 edge = abs(rel) / (Box * 0.5);
            float dist = distance(pos, uCameraPos);
            float fade = smoothstep(1.0, 0.75, max(edge.x, max(edge.y, edge.z))) * smoothstep(0.3, 1.0, dist);
            float size = (0.05 + 0.05 * seed.z) * uPixelScale / max(dist, 0.1);
            gl_PointSize = clamp(size, 1.5, 14.0);
            vAlpha = fade * present * min(size / 1.5, 1.0) * 0.9;
            vColor = mix(vec3(0.95, 0.97, 1.0), vec3(0.35, 0.4, 0.55), uNight);
            gl_Position = uViewProj * vec4(pos, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        in vec3 vColor;
        in float vAlpha;
        out vec4 FragColor;

        void main()
        {
            float d = length(gl_PointCoord - 0.5) * 2.0;
            float a = smoothstep(1.0, 0.3, d) * vAlpha;
            if (a <= 0.0) discard;
            FragColor = vec4(vColor * a, a); // premultiplied
        }
        """;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;

    public SnowRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _vao = gl.GenVertexArray();
    }

    /// <summary>Draws the snowfall into the HDR scene (after everything that can hide it).</summary>
    public void Draw(Matrix4x4 viewProjection, Vector3 camera, float time, float snow, float night, float pixelScale)
    {
        if (snow <= 0.001f) return;
        _shader.Use();
        _shader.Set("uViewProj", viewProjection);
        _shader.Set("uCameraPos", camera);
        _shader.Set("uTime", time);
        _shader.Set("uSnow", snow);
        _shader.Set("uNight", night);
        _shader.Set("uPixelScale", pixelScale);

        _gl.Enable(EnableCap.ProgramPointSize);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Points, 0, Count);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.ProgramPointSize);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }
}
