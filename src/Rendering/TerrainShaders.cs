namespace Mine.Rendering;

/// <summary>
/// GLSL for the world: the terrain surface and the objects lying on it. Both share
/// <see cref="Lighting"/> (sun, shadows, point lights, glowing halos, haze and edge fog),
/// so everything sits in the same light and air.
/// </summary>
public static class TerrainShaders
{
    /// <summary>Most lights the shaders take at once (the nearest ones are sent).</summary>
    public const int MaxPointLights = 16;

    /// <summary>
    /// Scene lighting shared by every world fragment shader; paste after <see cref="SkyRenderer.Glsl"/>.
    /// <c>litColor</c> lights an albedo; <c>finishColor</c> adds haze, halos and fog and tone-maps.
    /// </summary>
    private const string Lighting = """
        uniform vec3 uCameraPos;
        uniform vec3 uAmbient;
        uniform vec3 uLightColor;
        uniform vec3 uLightDir;
        uniform sampler2DShadow uShadowMap;
        uniform mat4 uLightViewProj;
        uniform float uShadowTexel;
        uniform float uShadowBias;
        uniform float uFogStart;
        uniform float uFogEnd;
        uniform float uMistDensity; // soft aerial haze, per metre of distance

        #define MAX_POINT_LIGHTS 16
        uniform int uPointCount;
        uniform vec3 uPointPos[MAX_POINT_LIGHTS];
        uniform vec3 uPointColor[MAX_POINT_LIGHTS]; // colour times intensity
        uniform float uPointRadius[MAX_POINT_LIGHTS];

        // 1 = fully lit, 0 = in shadow; 3x3 PCF taps for soft edges, fading out
        // toward the border of the area covered by the shadow map.
        float shadowAt(vec3 pos, vec3 n)
        {
            vec4 lightSpace = uLightViewProj * vec4(pos + n * 0.08, 1.0);
            vec3 p = lightSpace.xyz / lightSpace.w * 0.5 + 0.5;
            float border = max(abs(p.x - 0.5), abs(p.y - 0.5)) * 2.0;
            if (border > 1.0 || p.z > 1.0) return 1.0;

            float sum = 0.0;
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
                sum += texture(uShadowMap, vec3(p.xy + vec2(x, y) * uShadowTexel * 1.5, p.z - uShadowBias));
            return mix(sum / 9.0, 1.0, smoothstep(0.8, 1.0, border));
        }

        float valueNoise3(vec3 p)
        {
            vec3 i = floor(p), f = fract(p);
            vec3 u = f * f * (3.0 - 2.0 * f);
            float a = mix(hash13(i), hash13(i + vec3(1, 0, 0)), u.x);
            float b = mix(hash13(i + vec3(0, 1, 0)), hash13(i + vec3(1, 1, 0)), u.x);
            float c = mix(hash13(i + vec3(0, 0, 1)), hash13(i + vec3(1, 0, 1)), u.x);
            float d = mix(hash13(i + vec3(0, 1, 1)), hash13(i + vec3(1, 1, 1)), u.x);
            return mix(mix(a, b, u.y), mix(c, d, u.y), u.z);
        }

        // Point lights are weaker in daylight, where the sun would drown them out anyway.
        float pointLightScale() { return mix(0.35, 1.0, uNight); }

        // Light from lanterns, torches and crystals: smooth falloff to zero at the radius,
        // slightly wrapped so it spills softly over the shapes it touches.
        vec3 pointLighting(vec3 pos, vec3 n)
        {
            vec3 sum = vec3(0.0);
            for (int i = 0; i < uPointCount; i++)
            {
                vec3 toLight = uPointPos[i] - pos;
                float d = length(toLight);
                float edge = clamp(1.0 - (d * d) / (uPointRadius[i] * uPointRadius[i]), 0.0, 1.0);
                float falloff = edge * edge / (1.0 + d * d * 0.15);
                float facing = max(dot(n, toLight / max(d, 1e-3)) * 0.8 + 0.2, 0.0);
                sum += uPointColor[i] * falloff * facing;
            }
            return sum * pointLightScale();
        }

        // Glowing halos in the air around the lights: how close the view ray passes to each one.
        vec3 lightHalos(vec3 ro, vec3 rd, float dist)
        {
            vec3 sum = vec3(0.0);
            for (int i = 0; i < uPointCount; i++)
            {
                vec3 toLight = uPointPos[i] - ro;
                float t = clamp(dot(toLight, rd), 0.0, dist);
                float miss = length(toLight - rd * t);
                float fade = 1.0 / (1.0 + length(toLight) * 0.04);
                sum += uPointColor[i] * (0.05 / (1.0 + miss * miss * 1.2) + 0.12 / (1.0 + miss * miss * 30.0)) * fade;
            }
            return sum * mix(0.25, 1.0, uNight);
        }

        vec3 litColor(vec3 albedo, vec3 pos, vec3 n)
        {
            float diffuse = max(dot(n, uLightDir), 0.0);
            // Sky light: surfaces pick up the colour of the sky they face; generous, so slopes
            // turned away from the sun stay readable and colourful.
            vec3 skyLight = skyColor(normalize(n + vec3(0.0, 0.6, 0.0)), false);
            vec3 ambient = mix(uAmbient, skyLight, 0.35) * (0.9 + 0.2 * n.y) * 1.25;
            return albedo * (ambient + uLightColor * diffuse * shadowAt(pos, n) + pointLighting(pos, n));
        }

        // Gentle aerial haze with a faint, slowly drifting variation.
        float mist(vec3 ro, vec3 rd, float dist)
        {
            vec3 p = ro + rd * dist * 0.5;
            float drift = 0.85 + 0.3 * valueNoise3(p * 0.01 + vec3(uTime * 0.02, 0.0, uTime * 0.015));
            return 1.0 - exp(-uMistDensity * dist * drift);
        }

        // cutThrough < 1 lets glowing things shine through the haze and fog.
        vec4 finishColor(vec3 color, vec3 pos, float cutThrough)
        {
            vec3 toFragment = pos - uCameraPos;
            float dist = length(toFragment);
            vec3 rd = toFragment / dist;
            vec3 sky = skyColor(rd, false);

            // Soft haze: tints distance with the hue of the air but keeps brightness.
            const vec3 luma = vec3(0.3, 0.59, 0.11);
            vec3 airHue = sky / max(dot(sky, luma), 1e-3);
            float brightness = dot(color, luma);
            vec3 hazeTarget = mix(airHue * brightness, vec3(brightness), 0.4);
            color = mix(color, hazeTarget, mist(uCameraPos, rd, dist) * cutThrough);
            color += lightHalos(uCameraPos, rd, dist);

            // Edge fog: only the last stretch before the end of the view fades into the sky.
            float edge = smoothstep(uFogStart, uFogEnd, dist) * cutThrough;
            return vec4(toneMap(mix(color, sky, edge)), 1.0);
        }
        """;

    public const string TerrainVertex = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;

        uniform mat4 uViewProj;

        out vec3 vWorldPos;
        out vec3 vNormal;

        void main()
        {
            vWorldPos = aPos;
            vNormal = aNormal;
            gl_Position = uViewProj * vec4(aPos, 1.0);
        }
        """;

    /// <summary>
    /// Materials are painted from slope and height: grass on gentle ground (drifting between
    /// green, golden and a teal accent), warm sandstone with faint strata on steep flanks,
    /// pale sand in the lowlands, and fine grain that fades with distance.
    /// </summary>
    public const string TerrainFragment = "#version 330 core\n" + SkyRenderer.Glsl + Lighting + """

        in vec3 vWorldPos;
        in vec3 vNormal;

        out vec4 FragColor;

        float noise2(vec2 p, float layer) { return valueNoise3(vec3(p, layer)); }

        vec3 terrainAlbedo(vec3 p, vec3 n, float dist)
        {
            vec2 xz = p.xz;
            float broad = noise2(xz * 0.0035, 0.0);
            float tint = noise2(xz * 0.0012 + 7.0, 1.0);
            float mid = noise2(xz * 0.045, 2.0);
            // Fine grain in three octaves, fading out with distance where it would only shimmer.
            float grain = noise2(xz * 0.7, 3.0) * 0.5 + noise2(xz * 2.9, 4.0) * 0.3 + noise2(xz * 9.0, 5.0) * 0.2;
            float fine = mix(grain, 0.5, smoothstep(30.0, 140.0, dist));

            vec3 grass = mix(vec3(0.33, 0.50, 0.15), vec3(0.64, 0.60, 0.22), broad);
            grass = mix(grass, vec3(0.20, 0.48, 0.38), smoothstep(0.65, 0.9, tint) * 0.35);

            float strata = 0.5 + 0.5 * sin(p.y * 0.45 + mid * 6.0 + broad * 4.0);
            vec3 rock = mix(vec3(0.70, 0.55, 0.45), vec3(0.84, 0.71, 0.58), 0.5 + (strata - 0.5) * 0.35);

            vec3 sand = vec3(0.88, 0.76, 0.58);

            float slope = 1.0 - n.y;
            float rockW = smoothstep(0.30, 0.46, slope + (mid - 0.5) * 0.12);
            float sandW = smoothstep(14.0, 6.0, p.y + (mid - 0.5) * 6.0) * (1.0 - rockW);
            vec3 albedo = mix(mix(grass, sand, sandW), rock, rockW);
            return albedo * (0.75 + 0.5 * fine);
        }

        void main()
        {
            vec3 n = normalize(vNormal);
            vec3 albedo = terrainAlbedo(vWorldPos, n, length(vWorldPos - uCameraPos));
            FragColor = finishColor(litColor(albedo, vWorldPos, n), vWorldPos, 1.0);
        }
        """;

    public const string ObjectVertex = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec3 aColor;
        layout(location = 3) in float aEmissive;

        uniform mat4 uViewProj;
        uniform mat4 uModel;

        out vec3 vWorldPos;
        out vec3 vNormal;
        out vec3 vColor;
        out float vEmissive;

        void main()
        {
            vec4 world = uModel * vec4(aPos, 1.0);
            vWorldPos = world.xyz;
            vNormal = mat3(uModel) * aNormal; // rotation and translation only
            vColor = aColor;
            vEmissive = aEmissive;
            gl_Position = uViewProj * world;
        }
        """;

    public const string ObjectFragment = "#version 330 core\n" + SkyRenderer.Glsl + Lighting + """

        in vec3 vWorldPos;
        in vec3 vNormal;
        in vec3 vColor;
        in float vEmissive;

        uniform float uGlow;      // flicker of the object's own light
        uniform float uHighlight; // 1 while the player aims at the object

        out vec4 FragColor;

        void main()
        {
            vec3 n = normalize(vNormal);
            vec3 color = litColor(vColor, vWorldPos, n);
            // Glowing parts shine with their own colour, brighter at night.
            color = mix(color, vColor * uGlow * mix(1.3, 1.8, uNight), vEmissive);
            color += vColor * 0.35 * uHighlight;
            FragColor = finishColor(color, vWorldPos, 1.0 - 0.8 * vEmissive);
        }
        """;
}
