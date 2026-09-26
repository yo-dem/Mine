using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Draws the sky (gradient, square sun and moon, stars, clouds) as a full-screen triangle.
/// The sky GLSL is shared with the terrain shader so the fog matches the sky exactly.
/// </summary>
public sealed class SkyRenderer : IDisposable
{
    /// <summary>Uniforms, <c>skyColor(dir, bodies)</c> and <c>toneMap</c>, to paste after <c>#version</c>.</summary>
    public const string Glsl = """
        uniform vec3 uZenith;
        uniform vec3 uHorizon;
        uniform vec3 uSunHorizon;
        uniform vec3 uSunDir;
        uniform vec3 uMoonDir;
        uniform vec3 uSunGlow;
        uniform float uHaze;
        uniform float uNight;
        uniform float uSkyAngle;
        uniform float uTime;

        float hash13(vec3 p)
        {
            p = fract(p * 0.1031);
            p += dot(p, p.zyx + 31.32);
            return fract((p.x + p.y) * p.z);
        }

        // Coordinates of d on a square facing `center`, tilted by `tilt`: |q| < 1 is inside.
        vec2 bodyCoords(vec3 d, vec3 center, float halfSize, float tilt)
        {
            vec3 t1 = normalize(cross(center, vec3(0.0, 0.0, 1.0)));
            vec3 t2 = cross(t1, center);
            vec2 q = vec2(dot(d, t1), dot(d, t2)) / (max(dot(d, center), 0.05) * halfSize);
            float c = cos(tilt), s = sin(tilt);
            return vec2(c * q.x - s * q.y, s * q.x + c * q.y);
        }

        // soft = extra edge blur in q units (0 = crisp).
        float squareMask(vec2 q, vec3 d, vec3 center, float soft)
        {
            float e = max(abs(q.x), abs(q.y));
            // Capped: away from the body q changes wildly between pixels, and an
            // unbounded edge width would draw a line across the whole sky.
            float w = max(min(fwidth(e), 0.1), soft);
            return (1.0 - smoothstep(1.0 - w, 1.0 + w, e)) * step(0.5, dot(d, center));
        }

        // Blocky 16x16 moon, in the style of the block textures: grey-blue with craters.
        vec3 moonSurface(vec2 q)
        {
            vec2 p = floor((q * 0.5 + 0.5) * 16.0) + 0.5;
            float shade = 0.94 + 0.06 * hash13(vec3(p, 7.0));
            if (hash13(vec3(floor(p / 5.0), 3.0)) > 0.6) shade *= 0.93; // darker "seas"

            const vec3 craters[5] = vec3[5](
                vec3(4.5, 5.0, 2.6), vec3(10.5, 10.0, 3.2), vec3(11.5, 3.5, 1.6),
                vec3(4.0, 12.0, 1.9), vec3(8.0, 7.5, 1.2));
            for (int i = 0; i < 5; i++)
            {
                float r = length(p - craters[i].xy) / craters[i].z;
                if (r < 1.0) shade *= r > 0.7 ? 0.92 : 0.84;
            }
            return vec3(0.55, 0.6, 0.72) * shade;
        }

        // Soft shoulder above 0.7: bright sunsets saturate instead of clipping to white.
        vec3 toneMap(vec3 c)
        {
            vec3 over = max(c - 0.7, 0.0);
            return min(c, 0.7) + 0.3 * (1.0 - exp(-over / 0.3));
        }

        // bodies = false leaves out sun, moon and stars (used for fog and sky light).
        vec3 skyColor(vec3 d, bool bodies)
        {
            float up = clamp(d.y, 0.0, 1.0);

            // The horizon is warmer toward the sun and cooler on the opposite side.
            vec2 flatDir = normalize(d.xz + vec2(1e-5, 0.0));
            vec2 flatSun = normalize(uSunDir.xz + vec2(1e-5, 0.0));
            float sunSide = pow(0.5 + 0.5 * dot(flatDir, flatSun), 2.5);
            vec3 horizon = mix(uHorizon, uSunHorizon, sunSide);
            vec3 c = mix(horizon, uZenith, pow(up, 0.45));

            // A warm band hugging the horizon on the sun side, thicker when the air is hazy.
            c += uSunHorizon * exp(-up * 9.0) * sunSide * 0.8 * uHaze;
            // Opposite the sun at dawn/dusk: the dark shadow of the earth low on the horizon,
            // with a faint pink belt just above it.
            float belt = smoothstep(0.04, 0.1, up) * smoothstep(0.32, 0.12, up);
            c += vec3(0.5, 0.28, 0.38) * belt * (1.0 - sunSide) * uHaze * 0.3;
            // Below the horizon (distant fog) fade to a slightly darker horizon.
            c = mix(c, horizon * 0.85, clamp(-d.y * 4.0, 0.0, 1.0));

            float sd = max(dot(d, uSunDir), 0.0);
            c += uSunGlow * (pow(sd, 4.0) * (0.2 + 0.6 * uHaze) + pow(sd, 32.0) * 0.5 + pow(sd, 300.0) * 0.8);
            float md = max(dot(d, uMoonDir), 0.0);
            c += vec3(0.35, 0.45, 0.8) * (pow(md, 12.0) * 0.25 + pow(md, 80.0) * 0.3) * uNight;

            if (!bodies) return c;

            float aboveHorizon = smoothstep(-0.02, 0.03, d.y);

            // Stars show wherever the sky is dark enough, so at dusk they come out
            // on the side opposite the sun first.
            float starVisibility = smoothstep(0.3, 0.08, dot(c, vec3(0.3, 0.5, 0.2)));
            if (starVisibility > 0.0)
            {
                // Stars turn with the sky, around the axis the sun travels on.
                float ca = cos(uSkyAngle), sa = sin(uSkyAngle);
                vec3 s = vec3(ca * d.x + sa * d.y, -sa * d.x + ca * d.y, d.z);
                vec3 p = s * 220.0;
                vec3 cell = floor(p);
                if (hash13(cell) > 0.993)
                {
                    float r = length(fract(p) - 0.5);
                    // A few bright stars, many faint ones.
                    float magnitude = 0.3 + 0.7 * pow(hash13(cell + 17.0), 3.0);
                    // Irregular scintillation (three waves at unrelated speeds), stronger
                    // near the horizon where the light crosses more air.
                    float phase = hash13(cell + 41.0) * 6.283;
                    float wave = 0.45 * sin(uTime * 2.1 + phase) + 0.35 * sin(uTime * 4.7 + phase * 3.1)
                               + 0.2 * sin(uTime * 9.3 + phase * 7.3);
                    float twinkle = max(1.0 + wave * mix(0.7, 0.35, up), 0.0);
                    float fade = smoothstep(0.0, 0.25, d.y); // extinction near the horizon
                    c += vec3(1.1, 1.1, 1.25) * smoothstep(0.45, 0.0, r) * magnitude * twinkle * fade * starVisibility;
                }
            }

            // Slightly tilted squares: a small sun and a big, faint and hazy moon.
            vec2 sq = bodyCoords(d, uSunDir, 0.045, 0.3);
            c += vec3(1.5, 1.35, 1.1) * squareMask(sq, d, uSunDir, 0.0) * aboveHorizon * (1.0 - uNight);
            vec2 mq = bodyCoords(d, uMoonDir, 0.14, -0.35);
            float moonAlpha = 0.95 * smoothstep(-0.02, 0.3, d.y) * uNight;
            c = mix(c, c * 0.5 + moonSurface(mq), squareMask(mq, d, uMoonDir, 0.1) * moonAlpha);
            return c;
        }
        """;

    private const string VertexSource = """
        #version 330 core
        out vec2 vNdc;

        void main()
        {
            // Full-screen triangle from the vertex index, no buffers needed.
            vNdc = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2) * 2.0 - 1.0;
            gl_Position = vec4(vNdc, 0.0, 1.0);
        }
        """;

    private const string FragmentSource = "#version 330 core\n" + Glsl + """

        in vec2 vNdc;
        uniform mat4 uInvViewProj; // rotation-only view, so the camera sits at the origin
        uniform vec3 uCameraPos;
        uniform float uCloudTime;  // game seconds, so clouds speed up with the clock
        out vec4 FragColor;

        float hash12(vec2 p)
        {
            vec3 p3 = fract(vec3(p.xyx) * 0.1031);
            p3 += dot(p3, p3.yzx + 33.33);
            return fract((p3.x + p3.y) * p3.z);
        }

        float valueNoise(vec2 p)
        {
            vec2 i = floor(p), f = fract(p);
            vec2 u = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash12(i), hash12(i + vec2(1.0, 0.0)), u.x),
                       mix(hash12(i + vec2(0.0, 1.0)), hash12(i + vec2(1.0, 1.0)), u.x), u.y);
        }

        float fbm(vec2 p)
        {
            float value = 0.0, amplitude = 0.5;
            for (int i = 0; i < 5; i++)
            {
                value += amplitude * valueNoise(p);
                p = p * 2.03 + vec2(17.1, 3.7);
                amplitude *= 0.5;
            }
            return value;
        }

        // A flat layer of soft clouds high above the world, drifting with the wind.
        vec3 clouds(vec3 d, vec3 sky)
        {
            if (d.y < 0.01) return sky;
            const float height = 220.0;
            float dist = (height - uCameraPos.y) / d.y;
            vec2 p = uCameraPos.xz + d.xz * dist + vec2(1.0, 0.35) * uCloudTime * 2.0;
            float density = smoothstep(0.5, 0.8, fbm(p / 140.0));
            if (density <= 0.0) return sky;

            // White by day, dim blue at night, coloured by the sun at dawn and dusk,
            // with a bright rim when seen toward the sun.
            float sd = max(dot(d, uSunDir), 0.0);
            vec3 light = mix(vec3(0.95, 0.95, 0.98), vec3(0.07, 0.08, 0.15), uNight)
                       + uSunGlow * (0.5 + 1.5 * pow(sd, 4.0))
                       + uSunHorizon * uHaze * 0.4;
            light *= 1.0 - 0.3 * density; // thicker parts are a bit darker

            float alpha = density * 0.7 * smoothstep(0.01, 0.2, d.y) * (1.0 - smoothstep(2500.0, 8000.0, dist));
            return mix(sky, light, alpha);
        }

        void main()
        {
            vec4 far = uInvViewProj * vec4(vNdc, 1.0, 1.0);
            vec3 d = normalize(far.xyz / far.w);
            FragColor = vec4(toneMap(clouds(d, skyColor(d, true))), 1.0);
        }
        """;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao; // core profile needs a bound VAO even with no attributes

    public SkyRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _vao = gl.GenVertexArray();
    }

    /// <summary>Sets the uniforms declared in <see cref="Glsl"/>.</summary>
    public static void SetUniforms(Shader shader, in Atmosphere atmosphere, float time)
    {
        shader.Set("uZenith", atmosphere.Zenith);
        shader.Set("uHorizon", atmosphere.Horizon);
        shader.Set("uSunHorizon", atmosphere.SunHorizon);
        shader.Set("uHaze", atmosphere.Haze);
        shader.Set("uSunDir", atmosphere.SunDirection);
        shader.Set("uMoonDir", atmosphere.MoonDirection);
        shader.Set("uSunGlow", atmosphere.SunGlow);
        shader.Set("uNight", atmosphere.Night);
        shader.Set("uSkyAngle", atmosphere.SkyAngle);
        shader.Set("uTime", time);
    }

    public void Draw(Matrix4x4 inverseViewProjection, Vector3 cameraPosition, float cloudTime, in Atmosphere atmosphere, float time)
    {
        _shader.Use();
        _shader.Set("uInvViewProj", inverseViewProjection);
        _shader.Set("uCameraPos", cameraPosition);
        _shader.Set("uCloudTime", cloudTime);
        SetUniforms(_shader, atmosphere, time);

        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }
}
