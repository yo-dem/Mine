using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Draws the cosmic sky (gradient, a giant spiral galaxy, nebulae, dense stars, shooting stars,
/// the sun, a big moon, volumetric clouds) as a full-screen triangle.
/// Output is HDR (bright things above 1, for the bloom); tone mapping happens in the post-process pass.
/// It is drawn after the opaque geometry, at the far plane with a less-or-equal depth test, so the
/// expensive cloud ray march only runs on the pixels where the sky is actually visible.
/// The sky GLSL is shared with the terrain shader so the fog matches the sky exactly.
/// </summary>
public sealed class SkyRenderer : IDisposable
{
    /// <summary><c>hash13</c>: a cheap pseudo-random value in [0, 1) for a 3D point. Usable in any shader stage.</summary>
    public const string Hash = """
        float hash13(vec3 p)
        {
            p = fract(p * 0.1031);
            p += dot(p, p.zyx + 31.32);
            return fract((p.x + p.y) * p.z);
        }

        """;

    /// <summary>
    /// Uniforms, <see cref="Hash"/>, <c>skyColor(dir, bodies)</c> and <c>cloudDensity(p, detail)</c>,
    /// to paste after <c>#version</c> in fragment shaders (it uses <c>fwidth</c>). The cloud noise
    /// texture must be bound to unit <see cref="CloudNoiseUnit"/>.
    /// </summary>
    public const string Glsl = Hash + """
        uniform sampler3D uCloudNoise;
        uniform float uCloudTime; // game seconds, so clouds speed up with the clock
        const float CloudBottom = 260.0;
        const float CloudTop = 580.0;

        // Cloud density at a point: broad shapes from low-frequency noise, rounded at the base and
        // thinning toward the top of the layer; detail erodes the edges into wisps. lod blurs the
        // noise (mip level), for long ray-march steps and soft cloud shadows.
        float cloudDensity(vec3 p, bool detail, float lod)
        {
            float h = (p.y - CloudBottom) / (CloudTop - CloudBottom);
            if (h < 0.0 || h > 1.0) return 0.0;
            vec3 wind = vec3(1.0, 0.0, 0.35) * uCloudTime * 3.0;
            vec3 q = (p + wind) * 0.0011;
            float shape = textureLod(uCloudNoise, q * vec3(1.0, 1.8, 1.0), lod).r * 0.7 + textureLod(uCloudNoise, q * 2.7 + 0.37, lod + 1.4).r * 0.3;
            float profile = smoothstep(0.0, 0.12, h) * smoothstep(1.0, 0.4, h);
            float d = (shape * profile - 0.6) * 3.0;
            if (detail && d > 0.0) d -= (1.0 - textureLod(uCloudNoise, q * 5.0 + 0.71, lod + 2.3).r) * 0.2;
            return clamp(d, 0.0, 1.0);
        }

        uniform vec3 uZenith;
        uniform vec3 uHorizon;
        uniform vec3 uSunHorizon;
        uniform vec3 uSunDir;
        uniform vec3 uMoonDir;
        uniform vec3 uSunGlow;
        uniform float uHaze;
        uniform float uNight;
        uniform float uSkyAngle;
        uniform vec3 uGalaxyDir;
        uniform float uGalaxyGlow;
        uniform float uTime;

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
        float diskMask(vec2 q, vec3 d, vec3 center, float soft)
        {
            float e = length(q);
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

        // The galaxy: a huge spiral seen face-on, like a swirling planet. Logarithmic arms wind
        // slowly around a bright core, sprinkled with star dust, inside a glowing spherical rim,
        // with fainter spiral wisps trailing outside it.
        vec3 galaxy(vec3 d)
        {
            // Measured by angle from its centre, so it can span most of the sky: the rim sits
            // 68 degrees out, wider than the view, so it never fits on screen whole.
            float facing = clamp(dot(d, uGalaxyDir), -1.0, 1.0);
            float r = acos(facing) / 1.19;
            if (r > 1.7) return vec3(0.0);
            vec3 t1 = normalize(cross(uGalaxyDir, vec3(0.0, 1.0, 0.0)));
            vec3 t2 = cross(t1, uGalaxyDir);
            vec2 flat2 = vec2(dot(d, t1), dot(d, t2));
            vec2 q = flat2 / max(length(flat2), 1e-5) * r;
            float angle = atan(q.y, q.x);
            float swirl = angle - log(r + 0.03) * 2.8 + uTime * 0.02;
            // Noise twisted along the arms breaks them into clumps and streaks, like brush strokes.
            // (Sampled through cos/sin of the swirl, so there is no seam where the polar angle wraps.)
            float streaks = texture(uCloudNoise, vec3(cos(swirl) * r * 0.9, sin(swirl) * r * 0.9, 0.37 + r * 0.4)).r;
            float arms = pow(max(0.5 + 0.5 * cos(swirl * 2.0), 0.0), 2.5) * (0.35 + 1.3 * streaks);
            float inside = smoothstep(1.02, 0.9, r);

            vec3 armColor = mix(vec3(0.25, 0.15, 0.85), vec3(0.95, 0.5, 1.0), arms * exp(-r * 1.2));
            vec3 c = armColor * (0.25 + arms * 1.6) * exp(-r * 1.6) * inside;
            c += vec3(1.0, 0.8, 1.0) * (exp(-r * 22.0) * 2.2 + exp(-r * 6.0) * 0.35); // core
            c += vec3(0.55, 0.4, 1.0) * exp(-((r - 1.0) * 12.0) * ((r - 1.0) * 12.0)) * 1.2;       // rim
            vec2 dust = floor(q * 420.0);
            float sparkle = step(0.965, hash13(vec3(dust, 3.0))) * (0.5 + 0.5 * sin(uTime * 3.0 + hash13(vec3(dust, 9.0)) * 40.0));
            c += vec3(1.0, 0.9, 1.0) * sparkle * (0.3 + arms) * inside * 1.5;
            // Outer wisps continue the arms beyond the rim, fading out.
            float outer = smoothstep(1.0, 1.2, r) * exp(-(r - 1.0) * 1.8);
            c += vec3(0.4, 0.3, 1.0) * pow(max(0.5 + 0.5 * cos(swirl * 2.0 + 0.6), 0.0), 6.0) * outer * 0.8;
            return c * uGalaxyGlow;
        }

        // Nebulae: broad veils of violet and blue drifting across the dark sky.
        vec3 nebula(vec3 s)
        {
            float a = texture(uCloudNoise, s * 0.9 + vec3(0.2, 0.5, 0.1)).r;
            float b = texture(uCloudNoise, s * 2.3 + vec3(a * 0.4)).r;
            float veil = smoothstep(0.45, 0.8, a * 0.7 + b * 0.3);
            return mix(vec3(0.35, 0.1, 0.6), vec3(0.1, 0.25, 0.7), b) * veil * 0.55;
        }

        // A shooting star every few seconds: a short bright streak with a fading tail.
        vec3 shootingStar(vec3 d)
        {
            const float period = 5.0;
            float slot = floor(uTime / period);
            float h = hash13(vec3(slot, 7.0, 1.0));
            if (h < 0.35) return vec3(0.0);
            float progress = fract(uTime / period) * period / 0.9; // lasts 0.9 s
            if (progress > 1.0) return vec3(0.0);
            float a = hash13(vec3(slot, 2.0, 5.0)) * 6.283;
            vec3 start = normalize(vec3(cos(a), 0.45 + 0.4 * hash13(vec3(slot, 4.0, 4.0)), sin(a)));
            vec3 velocity = normalize(cross(start, vec3(0.3, 1.0, 0.2)));
            vec3 head = normalize(start + velocity * progress * 0.45);
            vec3 q = d - head * dot(d, head);
            float along = dot(q, velocity);
            float across = length(q - velocity * along);
            float tail = smoothstep(-0.12, 0.0, along) * step(along, 0.002);
            return vec3(1.0, 0.9, 1.0) * tail * exp(-across * across * 4e5) * (1.0 - progress) * 4.0;
        }

        // bodies = false leaves out sun, moon and stars (used for fog and sky light).
        vec3 skyColor(vec3 d, bool bodies)
        {
            float up = clamp(d.y, 0.0, 1.0);

            // The horizon is warmer toward the sun and cooler on the opposite side.
            vec2 flatDir = normalize(d.xz + vec2(1e-5, 0.0));
            vec2 flatSun = normalize(uSunDir.xz + vec2(1e-5, 0.0));
            float sunSide = pow(clamp(0.5 + 0.5 * dot(flatDir, flatSun), 0.0, 1.0), 2.5);
            vec3 horizon = mix(uHorizon, uSunHorizon, sunSide);
            vec3 c = mix(horizon, uZenith, pow(up, 0.45));

            // A warm band hugging the horizon on the sun side, thicker when the air is hazy.
            c += uSunHorizon * exp(-up * 9.0) * sunSide * 0.5 * uHaze;
            // Opposite the sun at dawn/dusk: the dark shadow of the earth low on the horizon,
            // with a faint pink belt just above it.
            float belt = smoothstep(0.04, 0.1, up) * smoothstep(0.32, 0.12, up);
            c += vec3(0.5, 0.28, 0.38) * belt * (1.0 - sunSide) * uHaze * 0.3;
            // Below the horizon (distant fog) fade to a slightly darker horizon.
            c = mix(c, horizon * 0.85, clamp(-d.y * 4.0, 0.0, 1.0));

            float sd = max(dot(d, uSunDir), 0.0);
            c += uSunGlow * (pow(sd, 4.0) * (0.12 + 0.3 * uHaze) + pow(sd, 32.0) * 0.4 + pow(sd, 300.0) * 0.6);
            float md = max(dot(d, uMoonDir), 0.0);
            c += vec3(0.35, 0.45, 0.8) * (pow(md, 12.0) * 0.25 + pow(md, 80.0) * 0.3) * uNight;

            if (!bodies) return c;

            float aboveHorizon = smoothstep(-0.02, 0.03, d.y);

            // Stars show wherever the sky is dark enough, so at dusk they come out
            // on the side opposite the sun first.
            float starVisibility = smoothstep(0.45, 0.08, dot(c, vec3(0.3, 0.5, 0.2)));
            // Stars, nebulae and the galaxy turn slowly with the sky.
            float ca = cos(uSkyAngle * 0.25), sa = sin(uSkyAngle * 0.25); // uSkyAngle is continuous
            vec3 s = vec3(ca * d.x + sa * d.z, d.y, -sa * d.x + ca * d.z);
            c += nebula(s) * starVisibility * aboveHorizon;
            c += galaxy(d) * aboveHorizon;
            c += shootingStar(d) * starVisibility * aboveHorizon;
            if (starVisibility > 0.0)
            {
                vec3 p = s * 220.0;
                vec3 cell = floor(p);
                if (hash13(cell) > 0.988)
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
                    c += vec3(1.8, 1.8, 2.1) * smoothstep(0.45, 0.0, r) * magnitude * twinkle * fade * starVisibility;
                }
            }

            // Slightly tilted squares: a small sun and a big, faint and hazy moon.
            vec2 sq = bodyCoords(d, uSunDir, 0.045, 0.3);
            c += vec3(5.0, 4.4, 3.6) * diskMask(sq, d, uSunDir, 0.0) * aboveHorizon * (1.0 - uNight);
            vec2 mq = bodyCoords(d, uMoonDir, 0.14, -0.35);
            float moonAlpha = 0.95 * smoothstep(-0.02, 0.3, d.y) * uNight;
            c = mix(c, c * 0.5 + moonSurface(mq), diskMask(mq, d, uMoonDir, 0.1) * moonAlpha);
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
            gl_Position = vec4(vNdc, 1.0, 1.0); // on the far plane: only where nothing else was drawn
        }
        """;

    private const string FragmentSource = "#version 330 core\n" + Glsl + """

        in vec2 vNdc;
        uniform mat4 uInvViewProj; // rotation-only view, so the camera sits at the origin
        uniform vec3 uCameraPos;
        uniform float uUnderwater;
        uniform vec3 uAmbient;
        uniform int uCloudSteps;      // ray-march samples (quality)
        uniform int uCloudLightSteps; // samples toward the light per ray-march sample
        out vec4 FragColor;

        float henyeyGreenstein(float cosAngle, float g)
        {
            float g2 = g * g;
            return (1.0 - g2) / (4.0 * 3.14159 * pow(max(1.0 + g2 - 2.0 * g * cosAngle, 1e-4), 1.5));
        }

        // Ray-marches the cloud layer. Returns the light scattered toward the camera (rgb) and the
        // transmittance (a). Each sample also marches a few steps toward the sun (or the moon) to
        // shade itself: thick cores go dark, thin edges glow, and seen against the light they get
        // a silver lining (forward scattering).
        vec4 marchClouds(vec3 ro, vec3 rd)
        {
            if (abs(rd.y) < 1e-4) return vec4(0.0, 0.0, 0.0, 1.0);
            float tb = (CloudBottom - ro.y) / rd.y, tt = (CloudTop - ro.y) / rd.y;
            float t0 = max(min(tb, tt), 0.0), t1 = max(tb, tt);
            if (t1 <= 0.0 || t0 > 12000.0) return vec4(0.0, 0.0, 0.0, 1.0);
            t1 = min(t1, t0 + 3500.0);

            float stepLength = (t1 - t0) / float(uCloudSteps);
            // Interleaved gradient noise: an even, fine-grained jitter that hides the step banding.
            float jitter = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
            float t = t0 + stepLength * jitter;
            // One noise texel spans about 14 m: blur the noise as the steps grow longer than that.
            float lod = max(log2(stepLength / 14.0), 0.0);

            bool moonlit = uNight > 0.5;
            vec3 lightDir = moonlit ? uMoonDir : uSunDir;
            vec3 lightColor = moonlit ? vec3(0.22, 0.26, 0.45) * uNight : uSunGlow * 1.7;
            vec3 ambient = mix(uHorizon, uZenith, 0.55) * 0.75 + uSunHorizon * uHaze * 0.25;
            float cosAngle = dot(rd, lightDir);
            float phase = mix(henyeyGreenstein(cosAngle, 0.7), henyeyGreenstein(cosAngle, -0.2), 0.4) * 4.0;

            float transmittance = 1.0;
            vec3 light = vec3(0.0);
            for (int i = 0; i < 64; i++)
            {
                if (i >= uCloudSteps) break;
                vec3 p = ro + rd * t;
                float density = cloudDensity(p, true, lod);
                if (density > 0.001)
                {
                    float depth = 0.0;
                    float lightStep = 135.0 / float(uCloudLightSteps);
                    for (int j = 1; j <= 4; j++)
                    {
                        if (j > uCloudLightSteps) break;
                        depth += cloudDensity(p + lightDir * (float(j) * lightStep), false, lod + 1.0) * lightStep;
                    }
                    float toLight = exp(-depth * 0.03);
                    float powder = 1.0 - exp(-density * 5.0);
                    float height = (p.y - CloudBottom) / (CloudTop - CloudBottom);
                    vec3 scattered = lightColor * toLight * phase * powder + ambient * (0.45 + 0.55 * height);
                    float stepTransmittance = exp(-density * 0.025 * stepLength);
                    light += transmittance * scattered * (1.0 - stepTransmittance);
                    transmittance *= stepTransmittance;
                    if (transmittance < 0.02) break;
                }
                t += stepLength;
            }

            // Far clouds melt into the sky.
            float fade = exp(-t0 * 0.00016);
            return vec4(light * fade, mix(1.0, transmittance, fade));
        }

        void main()
        {
            vec4 far = uInvViewProj * vec4(vNdc, 1.0, 1.0);
            vec3 d = normalize(far.xyz / far.w);
            if (uUnderwater > 0.5)
            {
                // Nothing reaches this far under water: only the murk (the world shaders' colour).
                FragColor = vec4(vec3(0.03, 0.06, 0.2) + uAmbient * 0.15 + vec3(0.02, 0.08, 0.12) * (1.0 - uNight), 1.0);
                return;
            }
            vec4 clouds = marchClouds(uCameraPos, d);
            FragColor = vec4(skyColor(d, true) * clouds.a + clouds.rgb, 1.0);
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

    /// <summary>Texture unit the cloud noise is bound to while drawing the sky and the world.</summary>
    public const int CloudNoiseUnit = 2;

    /// <summary>Sets the uniforms declared in <see cref="Glsl"/>.</summary>
    public static void SetUniforms(Shader shader, in Atmosphere atmosphere, float time, float cloudTime)
    {
        shader.Set("uCloudNoise", CloudNoiseUnit);
        shader.Set("uCloudTime", cloudTime);
        shader.Set("uZenith", atmosphere.Zenith);
        shader.Set("uHorizon", atmosphere.Horizon);
        shader.Set("uSunHorizon", atmosphere.SunHorizon);
        shader.Set("uHaze", atmosphere.Haze);
        shader.Set("uSunDir", atmosphere.SunDirection);
        shader.Set("uMoonDir", atmosphere.MoonDirection);
        shader.Set("uSunGlow", atmosphere.SunGlow);
        shader.Set("uNight", atmosphere.Night);
        shader.Set("uSkyAngle", atmosphere.SkyAngle);
        shader.Set("uGalaxyDir", atmosphere.GalaxyDirection);
        shader.Set("uGalaxyGlow", atmosphere.GalaxyGlow);
        shader.Set("uTime", time);
    }

    /// <summary>Low quality (integrated GPUs): fewer cloud samples.</summary>
    public bool LowQuality;

    public void Draw(Matrix4x4 inverseViewProjection, Vector3 cameraPosition, float cloudTime, in Atmosphere atmosphere, float time)
    {
        _shader.Use();
        _shader.Set("uCloudSteps", LowQuality ? 16 : 40);
        _shader.Set("uCloudLightSteps", LowQuality ? 2 : 3);
        _shader.Set("uInvViewProj", inverseViewProjection);
        _shader.Set("uCameraPos", cameraPosition);
        _shader.Set("uUnderwater", cameraPosition.Y < Mine.World.TerrainField.WaterLevel ? 1f : 0f);
        _shader.Set("uAmbient", atmosphere.Ambient);
        SetUniforms(_shader, atmosphere, time, cloudTime);

        _gl.DepthFunc(DepthFunction.Lequal); // the far plane still passes where the buffer is clear
        _gl.DepthMask(false);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.DepthMask(true);
        _gl.DepthFunc(DepthFunction.Less);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }
}
