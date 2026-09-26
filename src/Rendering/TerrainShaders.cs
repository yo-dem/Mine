namespace Mine.Rendering;

/// <summary>
/// GLSL for the world: terrain, grass, trees and objects. They share <see cref="Materials"/>
/// (ground colours, so grass grows exactly where the ground is painted green) and
/// <see cref="Lighting"/> (sun, shadows, point lights, halos, haze and fog), so everything
/// sits in the same light and air, and <see cref="Wind"/>, so plants sway in the same gusts.
/// </summary>
public static class TerrainShaders
{
    /// <summary>Most lights the shaders take at once (the nearest ones are sent).</summary>
    public const int MaxPointLights = 16;

    /// <summary>
    /// Wind displacement for something rooted at <c>p</c>: a few travelling waves plus slow gusts
    /// sweeping across the land. Horizontal only.
    /// </summary>
    public const string Wind = """
        vec3 windOffset(vec3 p, float t)
        {
            float gust = 0.55 + 0.45 * sin(t * 0.35 + p.x * 0.045 + p.z * 0.03);
            float wave = sin(t * 1.6 + p.x * 0.35 + p.z * 0.22)
                       + 0.5 * sin(t * 2.7 + p.x * 0.8 - p.z * 0.6)
                       + 0.25 * sin(t * 4.3 - p.x * 1.3 + p.z * 1.1);
            return vec3(0.8, 0.0, 0.5) * wave * gust;
        }
        """;

    /// <summary>
    /// Places a tree vertex: scaled, turned by the instance yaw and moved to the instance position,
    /// then swayed by the wind (more the higher <c>sway</c> is); leaves (sway 1) also shiver.
    /// Shared by the tree shader and the shadow pass, so shadows move with the trees.
    /// </summary>
    public const string TreeTransform = Wind + """
        vec3 treeRotate(vec3 v, float yaw)
        {
            float c = cos(yaw), s = sin(yaw);
            return vec3(c * v.x - s * v.z, v.y, s * v.x + c * v.z);
        }

        vec3 treeWorld(vec3 pos, float sway, vec4 instance, float scale, float time)
        {
            vec3 world = instance.xyz + treeRotate(pos * scale, instance.w);
            world += windOffset(instance.xyz, time) * 0.1 * sway * scale;
            float leaf = step(0.99, sway);
            world += vec3(sin(time * 5.3 + dot(world, vec3(1.7, 2.1, 1.3))), 0.0,
                          cos(time * 4.7 + dot(world, vec3(1.1, 1.9, 2.3)))) * 0.03 * leaf;
            return world;
        }
        """;

    /// <summary>Noise and ground materials; paste after <see cref="SkyRenderer.Hash"/> (or the full sky GLSL, which includes it).</summary>
    private const string Materials = """
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

        // Cosmic grass drifting between teal-blue and violet, with patches of magenta.
        vec3 grassColor(vec2 xz)
        {
            float broad = noise2(xz * 0.0035, 0.0);
            float tint = noise2(xz * 0.0012 + 7.0, 1.0);
            vec3 grass = mix(vec3(0.16, 0.30, 0.36), vec3(0.34, 0.24, 0.48), broad);
            return mix(grass, vec3(0.52, 0.26, 0.48), smoothstep(0.65, 0.9, tint) * 0.45);
        }

        // How much of the ground is rock (x, steep slopes) and sand (y, lowlands); the rest is grass.
        vec2 rockSand(vec3 p, float normalY)
        {
            float mid = noise2(p.xz * 0.045, 2.0);
            float rockW = smoothstep(0.30, 0.46, 1.0 - normalY + (mid - 0.5) * 0.12);
            // Sand on the shores: from a few metres above the water (TerrainField.WaterLevel = 15) down.
            float sandW = smoothstep(19.5, 14.0, p.y + (mid - 0.5) * 4.0) * (1.0 - rockW);
            return vec2(rockW, sandW);
        }
        """;

    /// <summary>
    /// Scene lighting shared by every world fragment shader; paste after <see cref="Materials"/>.
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
        #ifdef CHEAP
            return mix(texture(uShadowMap, vec3(p.xy, p.z - uShadowBias)), 1.0, smoothstep(0.8, 1.0, border));
        #endif

            float sum = 0.0;
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
                sum += texture(uShadowMap, vec3(p.xy + vec2(x, y) * uShadowTexel * 1.5, p.z - uShadowBias));
            return mix(sum / 9.0, 1.0, smoothstep(0.8, 1.0, border));
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
                sum += uPointColor[i] * (0.05 / (1.0 + miss * miss * 1.2) + 0.06 / (1.0 + miss * miss * 30.0)) * fade;
            }
            return sum * mix(0.25, 1.0, uNight);
        }

        // Shade of the clouds drifting between the surface and the sun (or the moon).
        float cloudShadow(vec3 pos)
        {
            float t = ((CloudBottom + CloudTop) * 0.5 - pos.y) / max(uLightDir.y, 0.15);
            float d = cloudDensity(pos + uLightDir * t, false, 1.5);
            return 1.0 - 0.7 * smoothstep(0.0, 0.6, d);
        }

        // wrap > 0 lets light bend around soft shapes (foliage, grass) instead of cutting off at 90 degrees.
        vec3 litColor(vec3 albedo, vec3 pos, vec3 n, float wrap)
        {
            float diffuse = max((dot(n, uLightDir) + wrap) / (1.0 + wrap), 0.0);
            // Sky light: surfaces pick up the colour of the sky they face; generous, so slopes
            // turned away from the sun stay readable and colourful.
            vec3 skyLight = skyColor(normalize(n + vec3(0.0, 0.6, 0.0)), false);
            vec3 ambient = mix(uAmbient, skyLight, 0.35) * (0.9 + 0.2 * n.y) * 1.25;
            vec3 direct = uLightColor * diffuse * shadowAt(pos, n) * cloudShadow(pos);
            return albedo * (ambient + direct + pointLighting(pos, n));
        }

        // Gentle aerial haze with a faint, slowly drifting variation.
        float mist(vec3 ro, vec3 rd, float dist)
        {
        #ifdef CHEAP
            return 1.0 - exp(-uMistDensity * dist);
        #endif
            vec3 p = ro + rd * dist * 0.5;
            float drift = 0.85 + 0.3 * valueNoise3(p * 0.01 + vec3(uTime * 0.02, 0.0, uTime * 0.015));
            return 1.0 - exp(-uMistDensity * dist * drift);
        }

        // cutThrough < 1 lets glowing things shine through the haze and fog. The result is HDR:
        // tone mapping happens in the post-process pass.
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
        #ifndef CHEAP
            color += lightHalos(uCameraPos, rd, dist);
        #endif

            // Edge fog: only the last stretch before the end of the view fades into the sky.
            float edge = smoothstep(uFogStart, uFogEnd, dist) * cutThrough;
            return vec4(mix(color, sky, edge), 1.0);
        }
        """;

    private const string FragmentHeader = "#version 330 core\n" + SkyRenderer.Glsl + Materials + Lighting;

    // ---- Terrain -------------------------------------------------------------------------

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
    /// Materials are painted from slope and height: grass on gentle ground, warm sandstone with
    /// faint strata on steep flanks, pale sand in the lowlands, and fine grain that fades with distance.
    /// </summary>
    public const string TerrainFragment = FragmentHeader + """

        in vec3 vWorldPos;
        in vec3 vNormal;

        out vec4 FragColor;

        vec3 terrainAlbedo(vec3 p, vec3 n, float dist)
        {
            vec2 xz = p.xz;
            float broad = noise2(xz * 0.0035, 0.0);
            float mid = noise2(xz * 0.045, 2.0);
            // Fine grain in three octaves, fading out with distance where it would only shimmer.
            float grain = noise2(xz * 0.7, 3.0) * 0.5 + noise2(xz * 2.9, 4.0) * 0.3 + noise2(xz * 9.0, 5.0) * 0.2;
            float fine = mix(grain, 0.5, smoothstep(30.0, 140.0, dist));

            float strata = 0.5 + 0.5 * sin(p.y * 0.45 + mid * 6.0 + broad * 4.0);
            vec3 rock = mix(vec3(0.30, 0.23, 0.36), vec3(0.46, 0.35, 0.50), 0.5 + (strata - 0.5) * 0.45);
            vec3 sand = vec3(0.62, 0.48, 0.68);

            vec2 w = rockSand(p, n.y);
            vec3 albedo = mix(mix(grassColor(xz), sand, w.y), rock, w.x);
            return albedo * (0.75 + 0.5 * fine);
        }

        // Glitter: tiny grains that catch the light and twinkle, thickest on sand, brightest at night.
        vec3 glitter(vec3 p, vec3 n, float dist)
        {
            if (dist > 70.0) return vec3(0.0);
            vec3 cell = floor(p * 5.0);
            float h = hash13(cell);
            float sandy = rockSand(p, n.y).y;
            if (h < 0.994 - sandy * 0.006) return vec3(0.0);
            float r = length(fract(p * 5.0) - 0.5);
            float twinkle = pow(0.5 + 0.5 * sin(uTime * (2.0 + h * 4.0) + h * 80.0), 4.0);
            vec3 tint = mix(vec3(0.5, 0.9, 1.0), vec3(0.9, 0.6, 1.0), hash13(cell + 5.0));
            return tint * smoothstep(0.4, 0.0, r) * twinkle * mix(1.5, 4.0, uNight) * smoothstep(70.0, 30.0, dist);
        }

        void main()
        {
            vec3 n = normalize(vNormal);
            float dist = length(vWorldPos - uCameraPos);
            vec3 albedo = terrainAlbedo(vWorldPos, n, dist);
            vec3 color = litColor(albedo, vWorldPos, n, 0.0) + glitter(vWorldPos, n, dist);
            FragColor = finishColor(color, vWorldPos, 1.0);
        }
        """;

    // ---- Grass ---------------------------------------------------------------------------

    /// <summary>
    /// One instance per blade; colour and grass coverage are worked out on the CPU (GroundMaterials).
    /// Blades thin out with distance (each survivor gets wider) and vanish past <c>uGrassRadius</c>.
    /// </summary>
    public const string GrassVertex = "#version 330 core\n" + Wind + """

        layout(location = 0) in vec2 aBlade; // x: -1..1 across the blade, y: 0 at the root .. 1 at the tip
        layout(location = 1) in vec4 aBase;  // root position, bend
        layout(location = 2) in vec4 aShape; // facing angle, height, thinning key, colour variation
        layout(location = 3) in vec3 aColor; // ground grass colour at the root

        uniform mat4 uViewProj;
        uniform vec3 uCameraPos;
        uniform float uGrassRadius;
        uniform float uTime;

        out vec3 vWorldPos;
        out vec3 vNormal;
        out vec3 vColor;
        out float vTip;

        void main()
        {
            vec3 root = aBase.xyz;
            float dist = distance(root.xz, uCameraPos.xz);
            float keep = mix(1.0, 0.12, smoothstep(10.0, uGrassRadius, dist));

            vWorldPos = root;
            vNormal = vec3(0.0, 1.0, 0.0);
            vColor = vec3(0.0);
            vTip = 0.0;
            if (aShape.z > keep || dist > uGrassRadius)
            {
                gl_Position = vec4(2.0, 2.0, 2.0, 1.0); // outside the clip volume: dropped
                return;
            }

            float height = aShape.y * smoothstep(uGrassRadius, uGrassRadius * 0.75, dist);
            float t = aBlade.y;
            vec3 facing = vec3(cos(aShape.x), 0.0, sin(aShape.x));
            vec3 across = vec3(-facing.z, 0.0, facing.x);
            float width = 0.03 * (1.0 - t * 0.85) * min(inversesqrt(keep), 2.8);
            vec3 lean = facing * (0.15 + aBase.w * 0.45) * height + windOffset(root, uTime) * 0.25 * height;
            vec3 pos = root + across * aBlade.x * width + vec3(0.0, height * t, 0.0) + lean * t * t;

            // Roots match the ground, tips are lighter and sometimes sun-bleached.
            vec3 tip = mix(aColor * 1.3, aColor * vec3(1.35, 1.25, 0.8), aShape.w);
            vColor = mix(aColor * 0.9, tip, t);
            vNormal = normalize(facing * 0.6 + vec3(0.0, 0.8, 0.0));
            vWorldPos = pos;
            vTip = t;
            gl_Position = uViewProj * vec4(pos, 1.0);
        }
        """;

    /// <summary>Grass uses the cheap lighting path: one shadow tap, no haze noise, no halos (see Lighting).</summary>
    public const string GrassFragment = "#version 330 core\n#define CHEAP\n" + SkyRenderer.Glsl + Materials + Lighting + """

        in vec3 vWorldPos;
        in vec3 vNormal;
        in vec3 vColor;
        in float vTip;

        out vec4 FragColor;

        void main()
        {
            vec3 toCamera = uCameraPos - vWorldPos;
            vec3 n = normalize(vNormal);
            // Blades are seen from both sides: turn the normal toward the camera, but keep it pointing up.
            if (dot(n.xz, toCamera.xz) < 0.0) n.xz = -n.xz;
            vec3 color = litColor(vColor, vWorldPos, n, 0.6);
            // Sunlight shining through the blades when looking toward the sun.
            vec3 rd = normalize(-toCamera);
            float through = pow(max(dot(rd, uLightDir), 0.0), 3.0) * vTip;
            color += vColor * uLightColor * through * 0.6 * shadowAt(vWorldPos, n);
            FragColor = finishColor(color, vWorldPos, 1.0);
        }
        """;

    // ---- Trees ---------------------------------------------------------------------------

    public const string TreeVertex = "#version 330 core\n" + TreeTransform + """

        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec3 aColor;
        layout(location = 3) in float aEmissive;
        layout(location = 4) in float aSway;     // 0 at the foot of the trunk .. 1 on the leaves
        layout(location = 5) in vec4 aInstance;  // position, yaw
        layout(location = 6) in float aScale;

        uniform mat4 uViewProj;
        uniform float uTime;

        out vec3 vWorldPos;
        out vec3 vNormal;
        out vec3 vColor;
        out float vEmissive;
        out float vFoliage;

        void main()
        {
            vec3 world = treeWorld(aPos, aSway, aInstance, aScale, uTime);
            vWorldPos = world;
            vNormal = treeRotate(aNormal, aInstance.w);
            vColor = aColor;
            vEmissive = aEmissive;
            vFoliage = step(0.99, aSway);
            gl_Position = uViewProj * vec4(world, 1.0);
        }
        """;

    public const string TreeFragment = FragmentHeader + """

        in vec3 vWorldPos;
        in vec3 vNormal;
        in vec3 vColor;
        in float vEmissive;
        in float vFoliage;

        out vec4 FragColor;

        void main()
        {
            vec3 n = normalize(vNormal);
            vec3 rd = normalize(vWorldPos - uCameraPos);
            vec3 color = litColor(vColor, vWorldPos, n, vFoliage * 0.5);
            // Foliage glows at the edges against the sun, and takes a soft rim of sky colour.
            color += vColor * uLightColor * pow(max(dot(rd, uLightDir), 0.0), 4.0) * 0.5 * vFoliage;
            color += skyColor(n, false) * pow(1.0 - max(dot(n, -rd), 0.0), 3.0) * 0.18 * vFoliage;
            // Glowing orbs shine with their own colour, brighter at night.
            color = mix(color, vColor * mix(1.5, 2.4, uNight), vEmissive);
            FragColor = finishColor(color, vWorldPos, 1.0 - 0.8 * vEmissive);
        }
        """;

    // ---- Water ---------------------------------------------------------------------------

    /// <summary>A flat sheet at the water level following the camera; <c>aCorner</c> is -1..1.</summary>
    public const string WaterVertex = """
        #version 330 core
        layout(location = 0) in vec2 aCorner;

        uniform mat4 uViewProj;
        uniform vec3 uCameraPos;
        uniform float uWaterLevel;
        uniform float uWaterExtent;

        out vec3 vWorldPos;

        void main()
        {
            vec3 p = vec3(uCameraPos.x + aCorner.x * uWaterExtent, uWaterLevel, uCameraPos.z + aCorner.y * uWaterExtent);
            vWorldPos = p;
            gl_Position = uViewProj * vec4(p, 1.0);
        }
        """;

    /// <summary>
    /// Water: rippled normals from the cloud noise, the sky (galaxy and stars included) reflected
    /// with Fresnel, the scene beneath seen through it (from a snapshot) and absorbed with depth,
    /// a glittering path toward the sun or moon, and bioluminescence: glowing veins in the shallows,
    /// a pulsing line along the shore and sparkles like stars on the surface.
    /// </summary>
    public const string WaterFragment = FragmentHeader + """

        in vec3 vWorldPos;

        uniform sampler2D uUnderColor; // the scene before the water was drawn
        uniform sampler2D uUnderDepth;
        uniform vec2 uScreenSize;
        uniform float uNear;
        uniform float uFar;

        out vec4 FragColor;

        // Window depth to view-space distance (System.Numerics projection: NDC depth in [0, 1]).
        float viewDepth(float windowDepth)
        {
            float z = windowDepth * 2.0 - 1.0;
            return uFar * uNear / (uFar - z * (uFar - uNear));
        }

        float waves(vec2 p)
        {
            float a = texture(uCloudNoise, vec3(p * 0.035 + vec2(uTime * 0.02, uTime * 0.013), 0.3)).r;
            float b = texture(uCloudNoise, vec3(p * 0.11 - vec2(uTime * 0.03, -uTime * 0.021), 0.7)).r;
            return a * 0.6 + b * 0.4;
        }

        vec3 waterNormal(vec2 p, float dist)
        {
            float e = 0.4 + dist * 0.01;
            float amplitude = 0.35 / (1.0 + dist * 0.015); // calmer far away, where ripples would only shimmer
            float sx = (waves(p + vec2(e, 0.0)) - waves(p - vec2(e, 0.0))) * amplitude / (2.0 * e);
            float sz = (waves(p + vec2(0.0, e)) - waves(p - vec2(0.0, e))) * amplitude / (2.0 * e);
            return normalize(vec3(-sx * 6.0, 1.0, -sz * 6.0));
        }

        void main()
        {
            vec2 uv = gl_FragCoord.xy / uScreenSize;
            vec3 toFragment = vWorldPos - uCameraPos;
            float dist = length(toFragment);
            vec3 rd = toFragment / dist;
            vec3 n = waterNormal(vWorldPos.xz, dist);

            // How much water the view ray crosses before hitting the bottom, and how deep it is there.
            float surface = viewDepth(gl_FragCoord.z);
            float thickness = max(viewDepth(texture(uUnderDepth, uv).r) - surface, 0.0);
            float depth = thickness * max(-rd.y, 0.05);

            // Refraction: the scene beneath, bent by the ripples (unless that would pick up something
            // in front of the water) and fading into deep indigo.
            vec2 bentUv = uv + n.xz * 0.035 * clamp(thickness * 0.3, 0.0, 1.0);
            if (viewDepth(texture(uUnderDepth, bentUv).r) < surface) bentUv = uv;
            vec3 under = texture(uUnderColor, bentUv).rgb;
            vec3 deep = vec3(0.03, 0.015, 0.1) + uAmbient * 0.12;
            vec3 refracted = mix(under, deep, 1.0 - exp(-thickness * 0.22));

            // Reflection of the whole sky, galaxy and stars included.
            vec3 r = reflect(rd, n);
            r.y = abs(r.y);
            float fresnel = 0.02 + 0.98 * pow(1.0 - max(dot(n, -rd), 0.0), 5.0);
            vec3 color = mix(refracted, skyColor(r, true), fresnel);

            // A glittering path toward the sun or the moon.
            float toLight = max(dot(r, uLightDir), 0.0);
            color += uLightColor * (pow(toLight, 400.0) * 8.0 + pow(toLight, 40.0) * 0.35);

            // Bioluminescence: thin veins of light in the shallows, pulsing in waves toward the shore.
            float shallow = exp(-depth * 0.5);
            float v = texture(uCloudNoise, vec3(vWorldPos.xz * 0.04 + vec2(uTime * 0.008, 0.0), 0.9)).r;
            float vein = pow(1.0 - abs(v - 0.5) * 2.0, 18.0);
            float pulse = 0.5 + 0.5 * sin(uTime * 1.4 - depth * 3.0 + v * 14.0);
            vec3 glow = vec3(0.3, 0.85, 1.0) * vein * (0.35 + 0.65 * pulse) * shallow * 3.0;
            // A soft line of light where the water laps the sand.
            float shore = exp(-depth * 10.0) * (0.6 + 0.4 * sin(uTime * 2.0 + vWorldPos.x * 0.3 + vWorldPos.z * 0.2));
            glow += vec3(0.55, 0.75, 1.0) * shore * 0.9;
            // Sparkles on the surface, like stars fallen in the water.
            vec2 cell = floor(vWorldPos.xz * 3.0);
            float h = hash13(vec3(cell, 11.0));
            if (h > 0.992 && dist < 90.0)
            {
                float sparkle = smoothstep(0.35, 0.0, length(fract(vWorldPos.xz * 3.0) - 0.5));
                glow += mix(vec3(0.6, 0.9, 1.0), vec3(1.0, 0.7, 1.0), hash13(vec3(cell, 3.0)))
                      * sparkle * pow(0.5 + 0.5 * sin(uTime * 3.0 + h * 70.0), 4.0) * 3.0 * smoothstep(90.0, 40.0, dist);
            }
            color += glow * mix(0.45, 1.0, uNight);

            FragColor = finishColor(color, vWorldPos, 1.0);
        }
        """;

    // ---- Objects -------------------------------------------------------------------------

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

    public const string ObjectFragment = FragmentHeader + """

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
            vec3 color = litColor(vColor, vWorldPos, n, 0.0);
            // Glowing parts shine with their own colour, brighter at night.
            color = mix(color, vColor * uGlow * mix(1.4, 2.2, uNight), vEmissive);
            color += vColor * 0.35 * uHighlight;
            FragColor = finishColor(color, vWorldPos, 1.0 - 0.8 * vEmissive);
        }
        """;
}
