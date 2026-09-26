using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Magic motes floating in the air around the player, drawn as glowing, additively blended point
/// sprites. There is no particle data at all: each point derives its home position, drift and
/// colour from its index, wrapped into a box that follows the camera, so the air is full of them
/// wherever you go. By day they are sparse golden sparkles, at dusk lilac spores drifting upward,
/// at night fireflies wandering and blinking. They fade out when flying high above the ground.
/// </summary>
public sealed class MoteRenderer : IDisposable
{
    private const int Count = 1800;

    private const string VertexSource = "#version 330 core\n" + SkyRenderer.Hash + """
        uniform mat4 uViewProj;
        uniform vec3 uCameraPos;
        uniform float uTime;
        uniform float uNight;
        uniform float uDusk;           // 0..1, strongest around sunrise and sunset
        uniform float uHeightAboveGround;
        uniform float uPointScale;     // viewport height / (2 tan(fov / 2))

        out vec3 vColor;

        const vec3 Box = vec3(70.0, 18.0, 70.0);

        void main()
        {
            float id = float(gl_VertexID);
            vec3 seed = vec3(hash13(vec3(id, 1.0, 7.0)), hash13(vec3(id, 2.0, 3.0)), hash13(vec3(id, 3.0, 5.0)));
            float kind = hash13(vec3(id, 4.0, 1.0));

            // Drift: spores rise, everything meanders.
            vec3 drift = vec3(0.3, 0.1 + 0.5 * uDusk, 0.2) * uTime;
            vec3 home = seed * Box + drift;
            vec3 center = uCameraPos + vec3(0.0, 3.0, 0.0); // the box follows the camera, a bit above the eyes
            vec3 rel = mod(home - center, Box) - Box * 0.5;
            vec3 wander = vec3(sin(uTime * (0.4 + seed.x) + id), sin(uTime * (0.3 + seed.y) + id * 1.7) * 0.6, cos(uTime * (0.5 + seed.z) + id * 0.3)) * 1.2;
            vec3 pos = center + rel + wander;

            // Fade at the edges of the box so nothing pops, and when flying high.
            vec3 edge = abs(rel) / (Box * 0.5);
            float fade = smoothstep(1.0, 0.75, max(edge.x, max(edge.y, edge.z))) * smoothstep(35.0, 12.0, uHeightAboveGround);

            // Day sparkles, dusk spores, night fireflies: pick by time of day, and thin out by day.
            float blink = pow(0.5 + 0.5 * sin(uTime * (1.5 + 2.0 * seed.y) + id * 3.1), 6.0);
            vec3 sparkle = vec3(1.0, 0.85, 0.5) * (0.6 + 0.4 * sin(uTime * 7.0 + id)) * step(0.72, kind) * 0.7;
            vec3 spore = mix(vec3(0.85, 0.55, 1.0), vec3(1.0, 0.6, 0.75), seed.z) * 1.2;
            vec3 firefly = mix(vec3(0.8, 1.0, 0.35), vec3(1.0, 0.8, 0.3), seed.x) * blink * 3.5;
            vec3 dayColor = mix(sparkle, spore, uDusk);
            vColor = mix(dayColor, firefly, uNight) * fade;

            vec4 clip = uViewProj * vec4(pos, 1.0);
            gl_Position = clip;
            float size = mix(0.07, 0.11, uNight);
            gl_PointSize = clamp(size * uPointScale / max(clip.w, 0.1), 1.0, 24.0);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        in vec3 vColor;
        out vec4 FragColor;

        void main()
        {
            vec2 c = gl_PointCoord * 2.0 - 1.0;
            float r = dot(c, c);
            if (r > 1.0) discard;
            float glow = exp(-r * 4.0);
            FragColor = vec4(vColor * glow, 1.0);
        }
        """;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;

    public MoteRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _vao = gl.GenVertexArray();
    }

    /// <summary>Draws the motes into the HDR scene (after the opaque geometry, which hides them).</summary>
    public void Draw(Matrix4x4 viewProjection, Vector3 camera, in Atmosphere atmosphere, float time,
        float heightAboveGround, float pointScale)
    {
        _shader.Use();
        _shader.Set("uViewProj", viewProjection);
        _shader.Set("uCameraPos", camera);
        _shader.Set("uTime", time);
        _shader.Set("uNight", atmosphere.Night);
        _shader.Set("uDusk", Math.Clamp((atmosphere.Haze - 0.3f) / 0.5f, 0f, 1f));
        _shader.Set("uHeightAboveGround", heightAboveGround);
        _shader.Set("uPointScale", pointScale);

        _gl.Enable(EnableCap.ProgramPointSize);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
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
