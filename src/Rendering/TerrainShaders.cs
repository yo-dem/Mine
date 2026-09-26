namespace Mine.Rendering;

/// <summary>
/// GLSL for the terrain surface. Materials are painted procedurally from slope and height:
/// grass on gentle ground, layered rock on steep flanks, pale sand in the lowlands, with
/// broad colour drifts so the land shimmers between golden, green and teal. Lighting,
/// shadows, haze and sky colours are shared with the rest of the scene.
/// </summary>
public static class TerrainShaders
{
    public const string Vertex = """
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

    public const string Fragment = "#version 330 core\n" + SkyRenderer.Glsl + """

        in vec3 vWorldPos;
        in vec3 vNormal;

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

        out vec4 FragColor;

        // 1 = fully lit, 0 = in shadow; 3x3 PCF taps for soft edges, fading out
        // toward the border of the area covered by the shadow map.
        float shadow(vec3 n)
        {
            vec4 lightSpace = uLightViewProj * vec4(vWorldPos + n * 0.08, 1.0);
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

        float noise2(vec2 p, float layer) { return valueNoise3(vec3(p, layer)); }

        // Gentle aerial haze with a faint, slowly drifting variation.
        float mist(vec3 ro, vec3 rd, float dist)
        {
            vec3 p = ro + rd * dist * 0.5;
            float drift = 0.85 + 0.3 * valueNoise3(p * 0.01 + vec3(uTime * 0.02, 0.0, uTime * 0.015));
            return 1.0 - exp(-uMistDensity * dist * drift);
        }

        vec3 terrainAlbedo(vec3 p, vec3 n, float dist)
        {
            vec2 xz = p.xz;
            float broad = noise2(xz * 0.0035, 0.0);
            float tint = noise2(xz * 0.0012 + 7.0, 1.0);
            float mid = noise2(xz * 0.045, 2.0);
            // Fine grain in three octaves, fading out with distance where it would only shimmer.
            float grain = noise2(xz * 0.7, 3.0) * 0.5 + noise2(xz * 2.9, 4.0) * 0.3 + noise2(xz * 9.0, 5.0) * 0.2;
            float fine = mix(grain, 0.5, smoothstep(30.0, 140.0, dist));

            // Grass drifting between fresh green, golden and a dreamy teal.
            vec3 grass = mix(vec3(0.33, 0.50, 0.15), vec3(0.64, 0.60, 0.22), broad);
            grass = mix(grass, vec3(0.20, 0.48, 0.38), smoothstep(0.65, 0.9, tint) * 0.35);

            // Warm sandstone with faint, wavy strata.
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
            vec3 toFragment = vWorldPos - uCameraPos;
            float dist = length(toFragment);
            vec3 rd = toFragment / dist;

            vec3 albedo = terrainAlbedo(vWorldPos, n, dist);

            float diffuse = max(dot(n, uLightDir), 0.0);
            // Sky light: surfaces pick up the colour of the sky they face.
            vec3 skyLight = skyColor(normalize(n + vec3(0.0, 0.6, 0.0)), false);
            // A generous ambient keeps slopes facing away from the sun readable and colourful.
            vec3 ambient = mix(uAmbient, skyLight, 0.35) * (0.9 + 0.2 * n.y) * 1.25;
            vec3 color = albedo * (ambient + uLightColor * diffuse * shadow(n));

            vec3 sky = skyColor(rd, false);

            // Soft haze: tints distance with the hue of the air but keeps brightness.
            const vec3 luma = vec3(0.3, 0.59, 0.11);
            vec3 airHue = sky / max(dot(sky, luma), 1e-3);
            float brightness = dot(color, luma);
            vec3 hazeTarget = mix(airHue * brightness, vec3(brightness), 0.4);
            color = mix(color, hazeTarget, mist(uCameraPos, rd, dist));

            // Edge fog: only the last stretch before the end of the view fades into the sky.
            float edge = smoothstep(uFogStart, uFogEnd, dist);
            FragColor = vec4(toneMap(mix(color, sky, edge)), 1.0);
        }
        """;
}
