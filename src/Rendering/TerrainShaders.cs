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

        // shiver 0 drops the leaves' quick trembling (the shadow pass: it made shadows flicker).
        vec3 treeWorld(vec3 pos, float sway, vec4 instance, float scale, float time, float shiver)
        {
            vec3 world = instance.xyz + treeRotate(pos * scale, instance.w);
            world += windOffset(instance.xyz, time) * 0.1 * sway * scale;
            float leaf = step(0.99, sway) * shiver;
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

        // Biomes (indigo, pink and turquoise woods, desert), weights summing to 1; the desert's share alone.
        // Mirrored in C# by GroundMaterials.Biome/Desert: keep the two in sync.
        float desertWeight(vec2 xz)
        {
            return smoothstep(0.70, 0.76, noise2(xz * 0.0008, 23.0) * 0.75 + noise2(xz * 0.003, 24.0) * 0.25);
        }

        vec4 biomeWeights(vec2 xz)
        {
            float desert = desertWeight(xz);
            float flavour = noise2(xz * 0.0011, 21.0) * 0.7 + noise2(xz * 0.004, 22.0) * 0.3;
            float pink = smoothstep(0.58, 0.64, flavour), teal = smoothstep(0.47, 0.41, flavour);
            float wet = 1.0 - desert;
            return vec4((1.0 - pink - teal) * wet, pink * wet, teal * wet, desert);
        }

        // Cosmic grass in patches of one hue each: teal, violet, magenta, lilac gold, sky blue.
        // A slow noise picks the hue (with soft borders between patches), a faster one varies it.
        // Mirrored in C# by GroundMaterials.GrassColor: keep the two in sync.
        vec3 grassColor(vec2 xz)
        {
            float patch = noise2(xz * 0.012, 11.0) * 0.75 + noise2(xz * 0.04, 12.0) * 0.25;
            float shade = noise2(xz * 0.09, 13.0);
            vec3 c = vec3(0.14, 0.34, 0.38);                                   // teal
            c = mix(c, vec3(0.34, 0.22, 0.54), smoothstep(0.30, 0.36, patch)); // violet
            c = mix(c, vec3(0.56, 0.20, 0.46), smoothstep(0.44, 0.50, patch)); // magenta
            c = mix(c, vec3(0.58, 0.46, 0.42), smoothstep(0.56, 0.62, patch)); // lilac gold
            c = mix(c, vec3(0.16, 0.32, 0.58), smoothstep(0.68, 0.74, patch)); // sky blue
            // Each biome pulls the patches toward its own hue (indigo, pink, teal, desert straw).
            vec4 b = biomeWeights(xz);
            vec3 tint = vec3(0.20, 0.24, 0.56) * b.x + vec3(0.62, 0.24, 0.50) * b.y + vec3(0.10, 0.44, 0.48) * b.z + vec3(0.60, 0.46, 0.36) * b.w;
            c = mix(c, tint, 0.4);
            return c * (0.85 + 0.3 * shade);
        }

        // How much of the ground is rock (x, steep slopes) and sand (y, lowlands); the rest is grass.
        vec2 rockSand(vec3 p, float normalY)
        {
            float mid = noise2(p.xz * 0.045, 2.0);
            float rockW = smoothstep(0.30, 0.46, 1.0 - normalY + (mid - 0.5) * 0.12);
            // Sand on the shores: from a few metres above the water (TerrainField.WaterLevel = 15) down.
            // ...and over the deserts, frayed at their edges.
            float desertSand = smoothstep(0.25, 0.7, desertWeight(p.xz) + (mid - 0.5) * 0.3);
            float sandW = max(smoothstep(19.5, 14.0, p.y + (mid - 0.5) * 4.0), desertSand) * (1.0 - rockW);
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
        uniform float uUnderwater;  // 1 while the camera is below the water surface

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
                float d2 = dot(toLight, toLight), r2 = uPointRadius[i] * uPointRadius[i];
                if (d2 >= r2) continue; // out of reach: no light at all
                float d = sqrt(d2);
                float edge = 1.0 - d2 / r2;
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
                vec3 missed = toLight - rd * t;
                float miss2 = dot(missed, missed);
                // Past 8 m the glow is a few ten-thousandths: skipped, and lowered by its value
                // there so it still fades smoothly to nothing.
                if (miss2 >= 64.0) continue;
                float glow = 0.05 / (1.0 + miss2 * 1.2) + 0.06 / (1.0 + miss2 * 30.0) - 0.00067;
                float fade = 1.0 / (1.0 + length(toLight) * 0.04);
                sum += uPointColor[i] * glow * fade;
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
            color = mix(color, sky, edge);

            // Under water: a deep indigo murk that thickens quickly with distance, lit faintly
            // from above; glowing things still shine through it.
            if (uUnderwater > 0.5)
            {
                vec3 murk = vec3(0.03, 0.06, 0.2) + uAmbient * 0.15 + vec3(0.02, 0.08, 0.12) * (1.0 - uNight);
                color = mix(color * vec3(0.7, 0.9, 1.1), murk, (1.0 - exp(-dist * 0.07)) * (1.0 - 0.5 * (1.0 - cutThrough)));
            }
            return vec4(color, 1.0);
        }
        """;

    private const string FragmentHeader = "#version 330 core\n" + SkyRenderer.Glsl + Materials + Lighting;

    // ---- Terrain -------------------------------------------------------------------------

    public const string TerrainVertex = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in float aSlope; // normal.y of the smooth land: picks the material
        layout(location = 3) in float aAo;    // darker at the foot of the walls

        uniform mat4 uViewProj;

        out vec3 vWorldPos;
        out vec3 vNormal;
        out float vSlope;
        out float vAo;

        void main()
        {
            vWorldPos = aPos;
            vNormal = aNormal;
            vSlope = aSlope;
            vAo = aAo;
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
        in float vSlope;
        in float vAo;

        out vec4 FragColor;

        // The land is made of tiles: a faint groove along their edges, fading with distance.
        float tileEdges(vec3 p, vec3 n, float dist)
        {
            if (n.y < 0.5 || dist > 60.0) return 1.0;
            vec2 f = fract(p.xz / 2.0); // TerrainField.TileSize
            float edge = min(min(f.x, 1.0 - f.x), min(f.y, 1.0 - f.y)) * 2.0;
            return mix(1.0, 0.86 + 0.14 * smoothstep(0.0, 0.03, edge), smoothstep(60.0, 20.0, dist));
        }

        vec3 terrainAlbedo(vec3 p, float slope, float dist)
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
            // Desert sand is warmer, peach and gold under the violet sky, with faint wind ripples.
            float desert = desertWeight(xz);
            float ripples = 0.5 + 0.5 * sin(dot(xz, vec2(0.9, 0.45)) + mid * 9.0);
            sand = mix(sand, vec3(0.78, 0.56, 0.52) * (0.93 + 0.07 * ripples * smoothstep(80.0, 20.0, dist)), desert);

            vec2 w = rockSand(p, slope);
            vec3 albedo = mix(mix(grassColor(xz), sand, w.y), rock, w.x);
            return albedo * (0.75 + 0.5 * fine);
        }

        // Glitter: tiny grains that catch the light and twinkle, thickest on sand, brightest at night.
        vec3 glitter(vec3 p, vec3 n, float slope, float dist)
        {
            if (dist > 70.0 || n.y < 0.5) return vec3(0.0);
            vec3 cell = floor(p * 5.0);
            float h = hash13(cell);
            float sandy = rockSand(p, slope).y;
            if (h < 0.994 - sandy * 0.006) return vec3(0.0);
            float r = length(fract(p * 5.0) - 0.5);
            float twinkle = pow(max(0.5 + 0.5 * sin(uTime * (2.0 + h * 4.0) + h * 80.0), 0.0), 4.0);
            vec3 tint = mix(vec3(0.5, 0.9, 1.0), vec3(0.9, 0.6, 1.0), hash13(cell + 5.0));
            return tint * smoothstep(0.4, 0.0, r) * twinkle * mix(1.5, 4.0, uNight) * smoothstep(70.0, 30.0, dist);
        }

        void main()
        {
            vec3 n = normalize(vNormal);
            float dist = length(vWorldPos - uCameraPos);
            vec3 albedo = terrainAlbedo(vWorldPos, vSlope, dist) * vAo * tileEdges(vWorldPos, n, dist);
            vec3 color = litColor(albedo, vWorldPos, n, 0.0) + glitter(vWorldPos, n, vSlope, dist);
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
            // The grass is dense: thin it out soon, the survivors growing wider to keep the ground covered.
            // GrassRenderer.Draw uses the same formula to skip the blades no tile could keep.
            float keep = mix(1.0, 0.07, smoothstep(8.0, uGrassRadius * 0.85, dist));

            vWorldPos = root;
            vNormal = vec3(0.0, 1.0, 0.0);
            vColor = vec3(0.0);
            vTip = 0.0;
            if (aShape.z > keep || dist > uGrassRadius)
            {
                gl_Position = vec4(2.0, 2.0, 2.0, 1.0); // outside the clip volume: dropped
                return;
            }

            // Full height up to the last stretch before the edge of the grass, then fading out.
            float height = aShape.y * smoothstep(uGrassRadius, uGrassRadius * 0.92, dist);
            float t = aBlade.y;
            vec3 facing = vec3(cos(aShape.x), 0.0, sin(aShape.x));
            vec3 across = vec3(-facing.z, 0.0, facing.x);
            // Taller blades are also broader, so tall grass reads as thick stalks, not threads.
            float width = 0.03 * (1.0 - t * 0.85) * min(inversesqrt(keep), 3.2) * clamp(aShape.y / 0.5, 1.0, 2.6);
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
            vec3 world = treeWorld(aPos, aSway, aInstance, aScale, uTime, 1.0);
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
        uniform sampler2D uSeaFloor;   // smooth land height around the camera (SeaFloorMap)
        uniform vec2 uSeaFloorOrigin;
        uniform float uSeaFloorExtent;
        uniform float uWaterLevel;
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

        // Rain rings: in each cell of a fine grid a drop lands now and then, and a ring spreads and
        // fades. Returns the slope the rings add (xy) and how bright their crests glow (z).
        vec3 rainRings(vec2 p)
        {
            // One layer of cells (a denser grid instead of two layers: a quarter of the work).
            vec3 sum = vec3(0.0);
            vec2 q = p * 2.2;
            vec2 cell = floor(q);
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
            {
                vec2 id = cell + vec2(x, y);
                float h = hash13(vec3(id, 31.0));
                float age = fract(uTime * (0.7 + 0.5 * h) + h * 7.0);
                vec2 center = id + vec2(fract(h * 17.31), fract(h * 91.7));
                vec2 off = q - center;
                float r = length(off);
                float ringR = age * 0.6;
                float k = (r - ringR) * 22.0; // (no pow of a negative base: NaN on some GPUs)
                float wave = exp(-k * k) * (1.0 - age) * (1.0 - age);
                sum.xy += off / max(r, 1e-3) * wave;
                sum.z += wave;
            }
            return sum;
        }

        vec3 waterNormal(vec2 p, float dist, vec3 rings)
        {
            float e = 0.4 + dist * 0.01;
            float amplitude = 0.35 / (1.0 + dist * 0.015); // calmer far away, where ripples would only shimmer
            float sx = (waves(p + vec2(e, 0.0)) - waves(p - vec2(e, 0.0))) * amplitude / (2.0 * e);
            float sz = (waves(p + vec2(0.0, e)) - waves(p - vec2(0.0, e))) * amplitude / (2.0 * e);
            vec3 n = vec3(-sx * 6.0, 1.0, -sz * 6.0);
            n.xz += rings.xy * 0.25 * uRain * smoothstep(60.0, 20.0, dist);
            return normalize(n);
        }

        void main()
        {
            vec2 uv = gl_FragCoord.xy / uScreenSize;
            vec3 toFragment = vWorldPos - uCameraPos;
            float dist = length(toFragment);
            vec3 rd = toFragment / dist;
            // Rain rings, computed once for both the ripples and their glow.
            vec3 rings = uRain > 0.01 && dist < 60.0 ? rainRings(vWorldPos.xz) : vec3(0.0);
            vec3 n = waterNormal(vWorldPos.xz, dist, rings);

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
            // Capped, so the galaxy core or the moon reflected in every ripple do not wash out into white.
            vec3 color = mix(refracted, min(skyColor(r, true), vec3(1.6)), fresnel);
            if (uUnderwater > 0.5)
            {
                // Seen from below: the bright, rippled sky through the surface, fading at grazing
                // angles into the silvery mirror of total internal reflection.
                vec3 up = skyColor(normalize(vec3(rd.x, abs(rd.y) * 1.5, rd.z)), true);
                color = mix(vec3(0.08, 0.12, 0.3), min(up, vec3(1.5)) * 0.8, smoothstep(0.05, 0.4, abs(rd.y)));
            }

            // Rain rings glow faintly, as if each drop woke the bioluminescence.
            if (uRain > 0.01 && dist < 60.0)
                color += mix(vec3(0.3, 0.8, 1.0), vec3(0.8, 0.45, 1.0), 0.5 + 0.5 * sin(vWorldPos.x * 0.3 + vWorldPos.z * 0.2))
                    * rings.z * 0.12 * uRain * mix(0.6, 1.5, uNight) * smoothstep(60.0, 20.0, dist);

            // A glittering path toward the sun or the moon.
            float toLight = max(dot(r, uLightDir), 0.0);
            // (Softer under the moon, whose glints would otherwise flood the rippled water.)
            color += uLightColor * (pow(toLight, 400.0) * mix(8.0, 2.0, uNight) + pow(toLight, 40.0) * mix(0.35, 0.12, uNight));

            // Bioluminescence: thin veins of light in the shallows, pulsing in waves toward the shore.
            float shallow = exp(-depth * 0.5);
            float v = texture(uCloudNoise, vec3(vWorldPos.xz * 0.04 + vec2(uTime * 0.008, 0.0), 0.9)).r;
            float vein = pow(max(1.0 - abs(v - 0.5) * 2.0, 0.0), 18.0);
            float pulse = 0.5 + 0.5 * sin(uTime * 1.4 - depth * 3.0 + v * 14.0);
            vec3 glow = vec3(0.3, 0.85, 1.0) * vein * (0.35 + 0.65 * pulse) * shallow * 3.0;
            // A soft line of light where the water laps the sand.
            float shore = exp(-depth * 14.0) * (0.6 + 0.4 * sin(uTime * 2.0 + vWorldPos.x * 0.3 + vWorldPos.z * 0.2));
            glow += vec3(0.25, 0.65, 1.0) * shore * 0.4;
            // Sparkles on the surface, like stars fallen in the water.
            vec2 cell = floor(vWorldPos.xz * 3.0);
            float h = hash13(vec3(cell, 11.0));
            if (h > 0.992 && dist < 90.0)
            {
                float sparkle = smoothstep(0.35, 0.0, length(fract(vWorldPos.xz * 3.0) - 0.5));
                glow += mix(vec3(0.6, 0.9, 1.0), vec3(1.0, 0.7, 1.0), hash13(vec3(cell, 3.0)))
                      * sparkle * pow(max(0.5 + 0.5 * sin(uTime * 3.0 + h * 70.0), 0.0), 4.0) * 3.0 * smoothstep(90.0, 40.0, dist);
            }
            // Breaking waves: bands rolling in to the shore, laid out along the depth contours of the
            // smooth sea floor (so they follow every coastline in soft curves, not the layered tiles),
            // rearing up as the water gets shallow and breaking into glowing pink-white foam that
            // trails behind each crest, a cyan glow on each rising face. Each wave is stronger or
            // weaker along its length, and broken up where it is weak.
            vec2 mapUv = (vWorldPos.xz - uSeaFloorOrigin) / uSeaFloorExtent;
            float inMap = smoothstep(0.0, 0.08, min(min(mapUv.x, mapUv.y), min(1.0 - mapUv.x, 1.0 - mapUv.y)));
            float seaDepth = uWaterLevel - texture(uSeaFloor, mapUv).r;
            if (inMap > 0.0 && seaDepth < 7.0 && uUnderwater < 0.5)
            {
                float depth = max(seaDepth, 0.0);
                float wobble = texture(uCloudNoise, vec3(vWorldPos.xz * 0.012, 0.15)).r;
                float s = depth / 1.6 + uTime * 0.28 + wobble * 1.5;
                float f = fract(s), id = floor(s);
                float strength = smoothstep(0.3, 0.6, texture(uCloudNoise, vec3(vWorldPos.xz * 0.02 + id * 0.37, 0.55)).r);
                float along = texture(uCloudNoise, vec3(vWorldPos.xz * 0.09 + vec2(id * 0.21, uTime * 0.01), 0.75)).r;
                float rising = smoothstep(7.0, 2.5, depth);
                float breaking = smoothstep(2.8, 0.6, depth);
                float crest = pow(f, mix(9.0, 3.5, breaking));
                float trail = exp(-f * mix(9.0, 3.0, breaking)) * breaking;
                // Churned foam: bubbly, not a flat band.
                float bubbles = texture(uCloudNoise, vec3(vWorldPos.xz * 0.35 + vec2(0.0, uTime * 0.05), 0.35)).r;
                trail *= 0.55 + 0.9 * bubbles;
                float broken = smoothstep(0.25, 0.6, along + breaking * 0.3);
                float surf = min((crest + trail * 0.7) * rising * strength * broken, 1.0) * inMap;
                float face = smoothstep(0.45, 0.93, f) * (1.0 - crest) * rising * strength * inMap;
                vec3 foam = mix(vec3(1.0, 0.82, 1.0), vec3(0.95, 0.42, 1.0), 0.4 + 0.3 * sin(vWorldPos.x * 0.05 + vWorldPos.z * 0.04));
                color = mix(color, foam * mix(1.1, 0.35, uNight), surf * 0.85);
                glow += foam * surf * 2.4 + vec3(0.25, 0.7, 1.0) * face * 0.8;
            }

            color += glow * mix(0.45, 1.0, uNight);

            FragColor = finishColor(min(color, vec3(8.0)), vWorldPos, 1.0);
        }
        """;

    // ---- Creatures -----------------------------------------------------------------------

    /// <summary>
    /// Butterflies (kind 0) flap both wings up together, birds (1) beat them up and down, fish (2)
    /// and deep fish (3) swing their tail. Butterflies and fish glow in a hue picked by their phase; birds are dark
    /// silhouettes by day that light up pale blue at night.
    /// </summary>
    public const string CreatureVertex = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aColor;
        layout(location = 2) in float aEmissive;
        layout(location = 3) in float aAnim;
        layout(location = 4) in vec4 aInstance; // position, heading
        layout(location = 5) in vec2 aExtra;    // scale, phase

        uniform mat4 uViewProj;
        uniform float uTime;
        uniform float uNight;
        uniform int uKind;

        out vec3 vWorldPos;
        out vec3 vColor;
        out float vEmissive;

        void main()
        {
            vec3 p = aPos;
            float t = uTime + aExtra.y;
            if (uKind == 0 && aAnim != 0.0)
            {
                float fold = 0.15 + 1.15 * abs(sin(t * 13.0));
                p = vec3(p.x, abs(p.z) * sin(fold), p.z * cos(fold));
            }
            else if (uKind == 1 && aAnim != 0.0)
            {
                float beat = sin(t * 4.5) * 0.6 + 0.1;
                p = vec3(p.x, abs(p.z) * sin(beat), p.z * cos(beat));
            }
            else if (uKind == 2)
            {
                p.z += sin(t * 9.0 - p.x * 9.0) * 0.08 * aAnim;
            }
            else if (uKind == 3)
            {
                p.z += sin(t * 5.0 - p.x * 7.0) * 0.1 * aAnim; // slower, deeper strokes
            }

            float c = cos(aInstance.w), s = sin(aInstance.w);
            p *= aExtra.x;
            vec3 world = aInstance.xyz + vec3(c * p.x - s * p.z, p.y, s * p.x + c * p.z);

            vec3 hue = mix(vec3(0.35, 0.9, 1.0), vec3(0.95, 0.5, 1.0), fract(aExtra.y * 0.37));
            if (uKind == 3)
            {
                // Deep fish: one of four glows per school (Creatures gives a school phases in one band of 20).
                float k = fract(floor(aExtra.y / 20.0) * 0.618);
                hue = k < 0.25 ? vec3(0.3, 1.0, 0.9) : k < 0.5 ? vec3(0.55, 0.45, 1.0) : k < 0.75 ? vec3(1.0, 0.4, 0.8) : vec3(1.0, 0.8, 0.4);
            }
            if (uKind == 1)
            {
                vColor = mix(vec3(0.06, 0.04, 0.12), vec3(0.55, 0.75, 1.0), uNight);
                vEmissive = aEmissive * uNight;
            }
            else
            {
                vColor = aColor * hue;
                vEmissive = aEmissive;
            }
            vWorldPos = world;
            gl_Position = uViewProj * vec4(world, 1.0);
        }
        """;

    public const string CreatureFragment = FragmentHeader + """

        in vec3 vWorldPos;
        in vec3 vColor;
        in float vEmissive;

        out vec4 FragColor;

        void main()
        {
            vec3 color = litColor(vColor, vWorldPos, vec3(0.0, 1.0, 0.0), 0.8);
            color = mix(color, vColor * mix(1.4, 2.4, uNight), vEmissive);
            FragColor = finishColor(color, vWorldPos, 1.0 - 0.6 * vEmissive);
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
