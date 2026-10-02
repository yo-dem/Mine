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

        // The climate (GroundMaterials: TemperatureAt, Desert, Snow): hot in the deserts, cold in the
        // snowy lands, mild in between. Keep in sync.
        uniform float uColorVariety;
        const float HotLow = 0.61, HotHigh = 0.69, ColdHigh = 0.39, ColdLow = 0.31;

        float temperature(vec2 xz) { return noise2(xz * 0.0004, 41.0) * 0.75 + noise2(xz * 0.0016, 42.0) * 0.25; }
        float desertWeight(vec2 xz) { return smoothstep(HotLow, HotHigh, temperature(xz)); }
        float snowWeight(vec2 xz) { return smoothstep(ColdHigh, ColdLow, temperature(xz)); }

        // The colour families (indigo, pink and turquoise woods, desert), weights summing to 1, given
        // the desert's and the snowy lands' shares; the snowy lands count as indigo.
        // Mirrored in C# by GroundMaterials.Biome: keep the two in sync.
        vec4 biomeWeightsFor(vec2 xz, float desert, float snow)
        {
            float flavour = noise2(xz * 0.0011, 21.0) * 0.7 + noise2(xz * 0.004, 22.0) * 0.3;
            float pink = smoothstep(0.58, 0.64, flavour), teal = smoothstep(0.47, 0.41, flavour);
            float mild = 1.0 - desert - snow;
            return vec4((1.0 - pink - teal) * mild + snow, pink * mild, teal * mild, desert);
        }

        vec4 biomeWeights(vec2 xz)
        {
            float t = temperature(xz);
            return biomeWeightsFor(xz, smoothstep(HotLow, HotHigh, t), smoothstep(ColdHigh, ColdLow, t));
        }

        // Cosmic grass in patches of one hue each: teal, violet, magenta, lilac gold, sky blue.
        // A slow noise picks the hue (with soft borders between patches), a faster one varies it.
        // Mirrored in C# by GroundMaterials.GrassColor: keep the two in sync.
        // b: biomeWeights(xz), when already known.
        vec3 grassColorFor(vec2 xz, vec4 b)
        {
            float hue = noise2(xz * 0.012, 11.0) * 0.75 + noise2(xz * 0.04, 12.0) * 0.25;
            float shade = noise2(xz * 0.09, 13.0);
            vec3 c = vec3(0.14, 0.34, 0.38);                                 // teal
            c = mix(c, vec3(0.34, 0.22, 0.54), smoothstep(0.30, 0.36, hue)); // violet
            c = mix(c, vec3(0.56, 0.20, 0.46), smoothstep(0.44, 0.50, hue)); // magenta
            c = mix(c, vec3(0.58, 0.46, 0.42), smoothstep(0.56, 0.62, hue)); // lilac gold
            c = mix(c, vec3(0.16, 0.32, 0.58), smoothstep(0.68, 0.74, hue)); // sky blue
            // Each biome pulls the patches toward its own hue (indigo, pink, teal, desert straw).
            vec3 tint = vec3(0.20, 0.24, 0.56) * b.x + vec3(0.62, 0.24, 0.50) * b.y + vec3(0.10, 0.44, 0.48) * b.z + vec3(0.60, 0.46, 0.36) * b.w;
            c = mix(c, tint, 0.4);
            c = mix(vec3(0.36, 0.32, 0.44), c, uColorVariety); // WorldPreset.ColorVariety (GroundMaterials.MutedGrass)
            return c * (0.85 + 0.3 * shade);
        }

        vec3 grassColor(vec2 xz) { return grassColorFor(xz, biomeWeights(xz)); }

        // How much of the ground is rock (x, steep slopes) and sand (y, lowlands); the rest is grass.
        // mid: noise2(p.xz * 0.045, 2.0) and desert: desertWeight(p.xz), when already known.
        vec2 rockSandFor(vec3 p, float normalY, float mid, float desert)
        {
            float rockW = smoothstep(0.30, 0.46, 1.0 - normalY + (mid - 0.5) * 0.12);
            // Sand on the shores: from a few metres above the water (TerrainField.WaterLevel = 15) down.
            // ...and over the deserts, frayed at their edges.
            float desertSand = smoothstep(0.25, 0.7, desert + (mid - 0.5) * 0.3);
            float sandW = max(smoothstep(19.5, 14.0, p.y + (mid - 0.5) * 4.0), desertSand) * (1.0 - rockW);
            return vec2(rockW, sandW);
        }

        vec2 rockSand(vec3 p, float normalY) { return rockSandFor(p, normalY, noise2(p.xz * 0.045, 2.0), desertWeight(p.xz)); }

        // The ground map (GroundMap): grass colour (rgb) and the desert's share minus the snowy
        // lands' (a: they never meet), drawn per 2 m around the camera; computed past its edge.
        uniform sampler2D uGroundMap;
        uniform vec2 uGroundMapOrigin;
        uniform float uGroundMapExtent;

        bool inGroundMap(vec2 xz, out vec2 uv)
        {
            uv = (xz - uGroundMapOrigin) / uGroundMapExtent;
            return all(greaterThan(uv, vec2(0.001))) && all(lessThan(uv, vec2(0.999)));
        }

        """;

    /// <summary>
    /// Scene lighting shared by every world fragment shader; paste after <see cref="Materials"/>.
    /// <c>litColor</c> lights an albedo; <c>finishColor</c> adds haze, halos and fog and tone-maps.
    /// </summary>
    private const string Lighting = """
        // The snowy lands' share at xz (from the ground map where it reaches).
        float groundSnow(vec2 xz)
        {
            vec2 uv;
            if (inGroundMap(xz, uv)) return max(-texture(uGroundMap, uv).a, 0.0);
            return snowWeight(xz);
        }

        // How much snow lies on a surface facing `up`: what snowfall has laid (snowOn), and in the
        // snowy lands always, in drifts, on everything facing up.
        float snowCover(vec3 pos, float up)
        {
            float lands = groundSnow(pos.xz);
            float fallen = snowOn(pos, up);
            if (lands <= 0.001) return fallen;
            float drift = texture(uCloudNoise, pos * 0.02).r;
            return max(fallen, smoothstep(0.3, 0.7, lands + (drift - 0.5) * 0.35) * smoothstep(0.45, 0.8, up));
        }

        uniform vec3 uCameraPos;
        uniform vec3 uAmbient;
        uniform vec3 uFlash;      // the light of a lightning bolt (none reaches indoors)
        uniform vec3 uLightColor;
        uniform vec3 uLightDir;
        uniform sampler2DShadow uShadowMap;
        uniform mat4 uLightViewProj;
        uniform float uShadowTexel;
        uniform float uShadowBias;
        uniform float uFogStart;
        uniform float uFogEnd;
        uniform float uMistDensity; // soft aerial haze, per metre of distance
        uniform float uVeil;        // far veil toward the sky colour: strength at the view's end (0 = none)
        uniform float uVeilStart;   // where it begins (metres)
        uniform float uVeilHeight;  // > 0: it lies low, thinning by e over this many metres above the water
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

        // The sky light a surface facing n picks up (see litColorSky).
        vec3 skyFill(vec3 n) { return skyGradient(normalize(n + vec3(0.0, 0.6, 0.0))); }

        // wrap > 0 lets light bend around soft shapes (foliage, grass) instead of cutting off at 90 degrees.
        // skyLight is skyFill(n) and shadow shadowAt(pos, n), passed in (the grass computes the
        // sky light per vertex, and reuses the shadow).
        vec3 litColorSky(vec3 albedo, vec3 pos, vec3 n, float wrap, vec3 skyLight, float shadow)
        {
            float diffuse = max((dot(n, uLightDir) + wrap) / (1.0 + wrap), 0.0);
            // Sky light: surfaces pick up the colour of the sky they face; generous, so slopes
            // turned away from the sun stay readable and colourful.
            // By day, warm the sky's fill: the bright blue-violet overhead would cool everything down.
            skyLight = mix(skyLight, dot(skyLight, vec3(0.3, 0.59, 0.11)) * vec3(1.15, 0.95, 0.8), 0.5 * (1.0 - uNight));
            vec3 ambient = mix(uAmbient + uFlash * (1.0 - indoors(pos)), skyLight, 0.35) * (0.9 + 0.2 * n.y) * 1.25;
            vec3 direct = uLightColor * diffuse * shadow * cloudShadow(pos);
            return albedo * (ambient + direct + pointLighting(pos, n));
        }

        vec3 litColor(vec3 albedo, vec3 pos, vec3 n, float wrap)
        {
            return litColorSky(albedo, pos, n, wrap, skyFill(n), shadowAt(pos, n));
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
        // tone mapping happens in the post-process pass. sky is skyGradient toward the point,
        // passed in (the grass computes it per vertex).
        vec4 finishColorSky(vec3 color, vec3 pos, float cutThrough, vec3 sky)
        {
            vec3 toFragment = pos - uCameraPos;
            float dist = length(toFragment);
            vec3 rd = toFragment / dist;

            // Soft haze: tints distance with the hue of the air but keeps brightness.
            const vec3 luma = vec3(0.3, 0.59, 0.11);
            vec3 airHue = sky / max(dot(sky, luma), 1e-3);
            float brightness = dot(color, luma);
            vec3 hazeTarget = mix(airHue * brightness, vec3(brightness), 0.4);
            color = mix(color, hazeTarget, mist(uCameraPos, rd, dist) * cutThrough);
        #ifndef CHEAP
            color += lightHalos(uCameraPos, rd, dist);
        #endif

            // The far veil: distance melts into the sky's colour (lying low, if uVeilHeight > 0).
            if (uVeil > 0.0)
            {
                float lying = uVeilHeight > 0.0 ? exp(-max(pos.y - 15.0, 0.0) / uVeilHeight) : 1.0; // TerrainField.WaterLevel
                color = mix(color, sky, uVeil * smoothstep(uVeilStart, uFogEnd, dist) * lying * cutThrough);
            }

            // Edge fog: only the last stretch before the end of the view fades into the sky.
            float edge = smoothstep(uFogStart, uFogEnd, dist) * cutThrough;
            color = mix(color, sky, edge);

            // A blizzard: blowing snow whites out the distance within a few tens of metres.
            if (uBlizzard > 0.001)
            {
                vec3 whiteout = vec3(0.74, 0.78, 0.86) * mix(1.0, 0.18, uNight);
                color = mix(color, whiteout, (1.0 - exp(-dist * 0.018 * uBlizzard)) * (0.4 + 0.6 * cutThrough));
            }

            // Under water: a deep indigo murk that thickens quickly with distance, lit faintly
            // from above; glowing things still shine through it.
            if (uUnderwater > 0.5)
            {
                vec3 murk = vec3(0.03, 0.06, 0.2) + uAmbient * 0.15 + vec3(0.02, 0.08, 0.12) * (1.0 - uNight);
                color = mix(color * vec3(0.7, 0.9, 1.1), murk, (1.0 - exp(-dist * 0.07)) * (1.0 - 0.5 * (1.0 - cutThrough)));
            }
            return vec4(color, 1.0);
        }

        vec4 finishColor(vec3 color, vec3 pos, float cutThrough)
        {
            return finishColorSky(color, pos, cutThrough, skyGradient(normalize(pos - uCameraPos)));
        }

        // Clear glass (blocks of glass, the seed jar, translucent crystals), premultiplied alpha:
        // a faint tint lit like anything else, the sky mirrored more strongly toward the edges
        // (Fresnel), a sharp glint of the sun or moon; `glow` makes it shine and more opaque (the
        // edges of a block, streaks of light, a crystal's tip).
        vec4 glassColor(vec3 pos, vec3 n, vec3 tint, float glow)
        {
            vec3 v = normalize(uCameraPos - pos);
            if (dot(n, v) < 0.0) n = -n; // the far side, seen through the near one
            float facing = clamp(dot(n, v), 0.0, 1.0);
            float fresnel = pow(1.0 - facing, 3.0);
            vec3 mirrored = skyColor(reflect(-v, n), false);
            float glint = pow(max(dot(n, normalize(uLightDir + v)), 0.0), 90.0);
            float alpha = clamp(mix(0.12, 0.7, fresnel) + 0.5 * glow, 0.0, 1.0);
            vec3 body = litColor(tint, pos, n, 0.0) * 0.35;
            vec3 color = body * alpha + mirrored * (0.15 + 0.6 * fresnel) + uLightColor * glint * 2.0
                + tint * glow * mix(0.6, 1.2, uNight);
            // Far away it fades into the fog like everything else.
            float fog = smoothstep(uFogStart, uFogEnd, length(pos - uCameraPos));
            return vec4(color, alpha) * (1.0 - fog);
        }
        """;

    private const string FragmentHeader = "#version 330 core\n" + SkyRenderer.Glsl + IndoorMap.Glsl + Materials + Lighting;

    // ---- Terrain -------------------------------------------------------------------------

    /// <summary>Draws <see cref="GroundMap"/>: per texel, the ground's grass colour (rgb) and the desert's share minus the snowy lands' (a).</summary>
    public const string GroundMapFragment = "#version 330 core\n" + SkyRenderer.Hash + Materials + """

        uniform vec2 uOrigin; // world xz of the map's corner
        uniform float uStep;  // metres per texel

        out vec4 FragColor;

        void main()
        {
            vec2 xz = uOrigin + gl_FragCoord.xy * uStep; // gl_FragCoord is at the texel's centre
            float t = temperature(xz);
            float desert = smoothstep(HotLow, HotHigh, t), snow = smoothstep(ColdHigh, ColdLow, t);
            FragColor = vec4(grassColorFor(xz, biomeWeightsFor(xz, desert, snow)), desert - snow);
        }
        """;

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

        uniform float uWaterLevel;

        out vec4 FragColor;

        // Light from the rocks under the water: from the edges of some of the submerged rocks
        // (the floor's 2 m tiles), light seeps out, as if from cracks: along the rim of the top
        // where the rock drops to a lower one (the neighbours' heights from the SeaFloorMap, as
        // TerrainField.TileHeight: its texels sit on the tiles' centres) and down the walls of the
        // step, brightest at the top edge; pink, blue or gold, one colour to each cluster of
        // rocks, pulsing slowly, broken here and there. Seen from the surface, even through the
        // mirrored sky at night (its light passes through the water: the water's glowBelow), it
        // gives the water depth. Rocks light up in clusters where a broad noise says.
        uniform sampler2D uSeaFloor;
        uniform vec2 uSeaFloorOrigin;

        // The layered height of a tile (TerrainField.TileHeight), or -1e4 off the map.
        float tileHeight(vec2 tile)
        {
            ivec2 t = ivec2(tile - floor(uSeaFloorOrigin / 2.0 + 0.5));
            if (any(lessThan(t, ivec2(0))) || any(greaterThanEqual(t, textureSize(uSeaFloor, 0)))) return -1e4;
            return round(texelFetch(uSeaFloor, t, 0).r / 0.5) * 0.5;
        }

        vec3 rockLight(vec3 p, vec3 n, float dist)
        {
            if (uWaterLevel - p.y < 0.4 || dist > 180.0) return vec3(0.0);
            bool wall = abs(n.y) < 0.4;
            if (!wall && n.y < 0.6) return vec3(0.0);
            // Which rock: on a wall, the tile behind it (the higher one: nudged against the normal).
            vec2 tile = floor((p.xz - (wall ? n.xz * 0.05 : vec2(0.0))) / 2.0);
            float chance = smoothstep(0.5, 0.75, noise2(tile * 0.11, 12.0)) * 0.8;
            if (hash13(vec3(tile, 91.0)) > chance) return vec3(0.0);

            float light;
            if (wall)
            {
                // Down the wall from the top edge of each half-metre layer.
                float up = fract(p.y / 0.5);
                light = pow(up, 6.0) * 1.4 + pow(up, 2.0) * 0.3;
            }
            else
            {
                // Along the rim of the top, on each side where the rock drops.
                float own = tileHeight(tile);
                if (own < -1e3) return vec3(0.0); // off the map
                vec2 local = p.xz - tile * 2.0;
                light = 0.0;
                vec4 edges = vec4(local.x, 2.0 - local.x, local.y, 2.0 - local.y);
                vec2 sides[4] = vec2[4](vec2(-1.0, 0.0), vec2(1.0, 0.0), vec2(0.0, -1.0), vec2(0.0, 1.0));
                for (int k = 0; k < 4; k++)
                {
                    if (edges[k] > 0.8) continue;
                    float below = tileHeight(tile + sides[k]);
                    if (below < -1e3 || own - below < 0.25) continue;
                    light = max(light, exp(-edges[k] / 0.1) * 1.3 + exp(-edges[k] / 0.35) * 0.3);
                }
                if (light <= 0.0) return vec3(0.0);
            }
            float along = dot(p.xz, wall ? vec2(-n.z, n.x) : vec2(0.7, 0.7));
            float seep = 0.35 + 0.65 * smoothstep(0.3, 0.7, noise2(vec2(along * 1.4, p.y * 2.0 + p.x * 0.3), 13.0));
            vec3 pink = vec3(1.0, 0.35, 0.75), blue = vec3(0.2, 0.5, 1.0), gold = vec3(1.0, 0.75, 0.25);
            float hue = noise2(floor(tile / 6.0) * 0.7, 14.0);
            vec3 tint = hue < 0.4 ? pink : hue < 0.62 ? blue : gold;
            float pulse = 0.75 + 0.25 * sin(uTime * 0.35 + hash13(vec3(tile, 92.0)) * 30.0);
            return tint * light * seep * pulse * 1.8 * smoothstep(180.0, 110.0, dist) * mix(0.35, 1.0, uNight);
        }

        // The land is made of tiles: a faint groove along their edges, fading with distance.
        float tileEdges(vec3 p, vec3 n, float dist)
        {
            if (n.y < 0.5 || dist > 60.0) return 1.0;
            vec2 f = fract(p.xz / 2.0); // TerrainField.TileSize
            float edge = min(min(f.x, 1.0 - f.x), min(f.y, 1.0 - f.y)) * 2.0;
            return mix(1.0, 0.86 + 0.14 * smoothstep(0.0, 0.03, edge), smoothstep(60.0, 20.0, dist));
        }

        // Each noise is computed once and handed to the material functions (they were computed up
        // to four times a pixel: the terrain's shading was mostly noise), and the slow ones come
        // from the ground map where it reaches. sandy: rockSand's y, for glitter.
        vec3 terrainAlbedo(vec3 p, float slope, float dist, out float sandy)
        {
            vec2 xz = p.xz;
            vec2 uv;
            float desert;
            vec3 grass;
            if (inGroundMap(xz, uv))
            {
                vec4 ground = texture(uGroundMap, uv);
                grass = ground.rgb;
                desert = max(ground.a, 0.0);
            }
            else
            {
                float t = temperature(xz);
                desert = smoothstep(HotLow, HotHigh, t);
                grass = grassColorFor(xz, biomeWeightsFor(xz, desert, smoothstep(ColdHigh, ColdLow, t)));
            }
            float broad = noise2(xz * 0.0035, 0.0);
            float mid = noise2(xz * 0.045, 2.0);
            // Fine grain in three octaves, fading out with distance where it would only shimmer
            // (and not computed at all past that).
            float fine = 0.5;
            if (dist < 140.0)
            {
                float grain = noise2(xz * 0.7, 3.0) * 0.5 + noise2(xz * 2.9, 4.0) * 0.3 + noise2(xz * 9.0, 5.0) * 0.2;
                fine = mix(grain, 0.5, smoothstep(30.0, 140.0, dist));
            }

            float strata = 0.5 + 0.5 * sin(p.y * 0.45 + mid * 6.0 + broad * 4.0);
            vec3 rock = mix(vec3(0.30, 0.23, 0.36), vec3(0.46, 0.35, 0.50), 0.5 + (strata - 0.5) * 0.45);
            vec3 sand = vec3(0.62, 0.48, 0.68);
            // Desert sand is warmer, peach and gold under the violet sky, with faint wind ripples.
            float ripples = 0.5 + 0.5 * sin(dot(xz, vec2(0.9, 0.45)) + mid * 9.0);
            sand = mix(sand, vec3(0.78, 0.56, 0.52) * (0.93 + 0.07 * ripples * smoothstep(80.0, 20.0, dist)), desert);

            vec2 w = rockSandFor(p, slope, mid, desert);
            sandy = w.y;
            vec3 albedo = mix(mix(grass, sand, w.y), rock, w.x);
            return albedo * (0.75 + 0.5 * fine);
        }

        // Glitter: a few tiny grains that catch the light and twinkle, thickest on sand, brightest at
        // night. Which cells hold a grain, and each grain's own pace and phase, come from different
        // hashes: taken from the same one, the grains (all with nearly the same hash) blinked together.
        vec3 glitter(vec3 p, vec3 n, float sandy, float dist)
        {
            if (dist > 70.0 || n.y < 0.5) return vec3(0.0);
            vec3 cell = floor(p * 5.0);
            float h = hash13(cell);
            if (h < 0.997 - sandy * 0.003) return vec3(0.0);
            float r = length(fract(p * 5.0) - 0.5);
            float pace = 0.7 + 1.8 * hash13(cell + 11.7), phase = 6.2832 * hash13(cell - 23.1);
            float twinkle = pow(max(0.5 + 0.5 * sin(uTime * pace + phase), 0.0), 6.0);
            vec3 tint = mix(vec3(0.5, 0.9, 1.0), vec3(0.9, 0.6, 1.0), hash13(cell + 5.0));
            return tint * smoothstep(0.4, 0.0, r) * twinkle * mix(1.0, 2.5, uNight) * smoothstep(70.0, 30.0, dist);
        }

        void main()
        {
            vec3 n = normalize(vNormal);
            float dist = length(vWorldPos - uCameraPos);
            // Seen from above through more than DeepEnough metres of water, the land is not worth
            // shading: the water fades what lies under it to its own deep colour (exp(-0.14 t):
            // under 2% of it is left), and the deep lake floors cost as much as the water itself.
            const float WaterSurface = 14.8, DeepEnough = 30.0; // TerrainField.WaterLevel
            if (uUnderwater < 0.5 && vWorldPos.y < WaterSurface)
            {
                float through = dist * (WaterSurface - vWorldPos.y) / max(uCameraPos.y - vWorldPos.y, 1e-3);
                if (through > DeepEnough)
                {
                    FragColor = vec4(vec3(0.03, 0.02, 0.08) + uAmbient * 0.1, 1.0);
                    return;
                }
            }
            float sandy;
            vec3 albedo = terrainAlbedo(vWorldPos, vSlope, dist, sandy);
            albedo = mix(albedo, SnowColor, snowCover(vWorldPos, n.y)) * vAo * tileEdges(vWorldPos, n, dist);
            // Sown ground looks watered: darker and a touch cooler on the tile's top, drying as the grass grows.
            albedo *= mix(vec3(1.0), vec3(0.42, 0.40, 0.48), wetGround(vWorldPos) * step(0.7, n.y));
            vec3 color = litColor(albedo, vWorldPos, n, 0.0) + glitter(vWorldPos, n, sandy, dist) * uMagic;
            color += rockLight(vWorldPos, n, dist);
            FragColor = finishColor(color, vWorldPos, 1.0);
        }
        """;

    // ---- Grass ---------------------------------------------------------------------------

    /// <summary>
    /// One instance per blade; colour and grass coverage are worked out on the CPU (GroundMaterials).
    /// Blades thin out with distance (each survivor gets wider) and vanish past <c>uGrassRadius</c>.
    /// </summary>
    public const string GrassVertex = "#version 330 core\n" + Wind + SkyRenderer.SkyGradient + """

        layout(location = 1) in vec4 aBase;  // root position, bend
        layout(location = 2) in vec4 aShape; // facing angle, height, thinning key, colour variation
        layout(location = 3) in vec3 aColor; // ground grass colour at the root

        uniform mat4 uViewProj;
        uniform vec3 uCameraPos;
        uniform float uGrassRadius;
        uniform float uTime;
        uniform int uBladeVertices; // 7, 5 or 3: fewer segments for far blades (GrassRenderer.Draw)

        out vec3 vWorldPos;
        out vec3 vNormal;
        out vec3 vColor;
        out float vTip;
        out vec3 vSkyFill; // the sky light of the blade's face (turned toward the camera) and
        out vec3 vFogSky;  // the sky toward the vertex, for the fog: per vertex, as they vary slowly

        void main()
        {
            // The blade as a triangle strip: pairs of vertices across it (x -1 and 1) climbing in
            // equal steps, and the tip (x 0, y 1) last.
            int segments = (uBladeVertices - 1) / 2;
            vec2 blade = gl_VertexID == uBladeVertices - 1
                ? vec2(0.0, 1.0)
                : vec2((gl_VertexID & 1) == 0 ? -1.0 : 1.0, float(gl_VertexID / 2) / float(segments));
            vec3 root = aBase.xyz;
            float dist = distance(root.xz, uCameraPos.xz);
            // The grass is dense: thin it out soon, the survivors growing wider to keep the ground covered.
            // GrassRenderer.Draw uses the same formula to skip the blades no tile could keep.
            float keep = mix(1.0, 0.07, smoothstep(8.0, uGrassRadius * 0.85, dist));

            vWorldPos = root;
            vNormal = vec3(0.0, 1.0, 0.0);
            vColor = vec3(0.0);
            vTip = 0.0;
            vSkyFill = vFogSky = vec3(0.0);
            if (aShape.z > keep || dist > uGrassRadius)
            {
                gl_Position = vec4(2.0, 2.0, 2.0, 1.0); // outside the clip volume: dropped
                return;
            }

            // Full height up to the last stretch before the edge of the grass, then fading out.
            float height = aShape.y * smoothstep(uGrassRadius, uGrassRadius * 0.92, dist);
            float t = blade.y;
            vec3 facing = vec3(cos(aShape.x), 0.0, sin(aShape.x));
            vec3 across = vec3(-facing.z, 0.0, facing.x);
            // Taller blades are also broader, so tall grass reads as thick stalks, not threads.
            float width = 0.03 * (1.0 - t * 0.85) * min(inversesqrt(keep), 3.2) * clamp(aShape.y / 0.5, 1.0, 2.6);
            vec3 lean = facing * (0.15 + aBase.w * 0.45) * height + windOffset(root, uTime) * 0.25 * height;
            vec3 pos = root + across * blade.x * width + vec3(0.0, height * t, 0.0) + lean * t * t;

            // Roots match the ground, tips are lighter and sometimes sun-bleached.
            vec3 tip = mix(aColor * 1.3, aColor * vec3(1.35, 1.25, 0.8), aShape.w);
            vColor = mix(aColor * 0.9, tip, t);
            vNormal = normalize(facing * 0.6 + vec3(0.0, 0.8, 0.0));
            vWorldPos = pos;
            vTip = t;
            // The same turn toward the camera as the fragment shader's, for the sky light.
            vec3 n = vNormal;
            if (dot(n.xz, uCameraPos.xz - pos.xz) < 0.0) n.xz = -n.xz;
            vSkyFill = skyGradient(normalize(n + vec3(0.0, 0.6, 0.0)));
            vFogSky = skyGradient(normalize(pos - uCameraPos));
            gl_Position = uViewProj * vec4(pos, 1.0);
        }
        """;

    /// <summary>Grass uses the cheap lighting path: one shadow tap, no haze noise, no halos (see Lighting).</summary>
    public const string GrassFragment = "#version 330 core\n#define CHEAP\n" + SkyRenderer.Glsl + IndoorMap.Glsl + Materials + Lighting + """

        in vec3 vWorldPos;
        in vec3 vNormal;
        in vec3 vColor;
        in float vTip;
        in vec3 vSkyFill;
        in vec3 vFogSky;

        out vec4 FragColor;

        void main()
        {
            vec3 toCamera = uCameraPos - vWorldPos;
            vec3 n = normalize(vNormal);
            // Blades are seen from both sides: turn the normal toward the camera, but keep it pointing up.
            if (dot(n.xz, toCamera.xz) < 0.0) n.xz = -n.xz;
            vec3 albedo = mix(vColor, SnowColor, snowCover(vWorldPos, 1.0) * 0.85);
            float shadow = shadowAt(vWorldPos, n);
            vec3 color = litColorSky(albedo, vWorldPos, n, 0.6, vSkyFill, shadow);
            // Sunlight shining through the blades when looking toward the sun.
            vec3 rd = normalize(-toCamera);
            float through = pow(max(dot(rd, uLightDir), 0.0), 3.0) * vTip;
            color += vColor * uLightColor * through * 0.6 * shadow;
            FragColor = finishColorSky(color, vWorldPos, 1.0, vFogSky);
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
        uniform float uGlass; // 1 in the see-through pass: only the glass parts are drawn, 0: all but them
        uniform float uFloat; // 1 for what floats on the water (lotus): it rides the swell
        uniform vec3 uCameraPos;
        uniform float uWaterLevel;
        uniform sampler2D uSeaFloor;
        uniform vec2 uSeaFloorOrigin;
        uniform float uSeaFloorExtent;

        out vec3 vWorldPos;
        out vec3 vNormal;
        out vec3 vColor;
        out float vEmissive;
        out float vFoliage;

        """ + WaterSwell + """

        void main()
        {
            vec3 world = treeWorld(aPos, aSway, aInstance, aScale, uTime, 1.0);
            if (uFloat > 0.5)
            {
                // Rise and fall with the water under it, tilted with its slope (as WaterVertex lifts the water).
                vec3 s = swell(aInstance.xz) * swellFade(aInstance.xz - uCameraPos.xz);
                vec2 local = world.xz - aInstance.xz;
                world.y += s.z + dot(local, s.xy);
            }
            vWorldPos = world;
            vNormal = treeRotate(aNormal, aInstance.w);
            vColor = aColor;
            // Glass parts (translucent crystals) carry their glow as -1 - glow (TreeModels.Prism).
            bool glass = aEmissive < -0.5;
            vEmissive = glass ? -aEmissive - 1.0 : aEmissive;
            vFoliage = step(0.99, aSway);
            gl_Position = uViewProj * vec4(world, 1.0);
            // Parts of the other pass are dropped (outside the clip volume, so no discard is needed).
            if (glass != (uGlass > 0.5)) gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        }
        """;

    public const string TreeFragment = FragmentHeader + """

        in vec3 vWorldPos;
        in vec3 vNormal;
        in vec3 vColor;
        in float vEmissive;
        in float vFoliage;

        uniform float uGlass; // the see-through pass (see TreeVertex)

        out vec4 FragColor;

        void main()
        {
            vec3 n = normalize(vNormal);
            if (uGlass > 0.5)
            {
                FragColor = glassColor(vWorldPos, n, vColor, vEmissive);
                return;
            }
            vec3 rd = normalize(vWorldPos - uCameraPos);
            vec3 albedo = mix(vColor, SnowColor, snowCover(vWorldPos, n.y) * (1.0 - vEmissive));
            vec3 color = litColor(albedo, vWorldPos, n, vFoliage * 0.5);
            // Foliage glows at the edges against the sun, and takes a soft rim of sky colour.
            color += vColor * uLightColor * pow(max(dot(rd, uLightDir), 0.0), 4.0) * 0.5 * vFoliage;
            color += skyColor(n, false) * pow(1.0 - max(dot(n, -rd), 0.0), 3.0) * 0.18 * vFoliage;
            // Glowing orbs shine with their own colour, brighter at night.
            color = mix(color, vColor * mix(1.5, 2.4, uNight), vEmissive);
            FragColor = finishColor(color, vWorldPos, 1.0 - 0.8 * vEmissive);
        }
        """;

    // ---- Water ---------------------------------------------------------------------------

    /// <summary>
    /// The swell, shared by the water's vertex shader (which lifts the surface with it) and its
    /// fragment shader (whose normal follows it): SwellCount long waves from SwellLongest metres
    /// down, fanned a little around the wind, at the deep-water speed for their length, with sharp
    /// crests and broad troughs (exp(sin - 1)). Lower in the shallows (from the SeaFloorMap), though
    /// never flat, so the water laps up and down the shores. Needs uTime, uWaterLevel,
    /// uCameraPos and the uSeaFloor uniforms declared before it.
    /// </summary>
    private const string WaterSwell = """
        // The wind's direction over the water, the same as the grass's (windOffset).
        const vec2 WindDir = vec2(0.848, 0.530);

        const int SwellCount = 4;
        const float SwellLongest = 11.0, SwellSteep = 0.22;

        float swellScale(vec2 p)
        {
            vec2 uv = (p - uSeaFloorOrigin) / uSeaFloorExtent;
            float inMap = smoothstep(0.0, 0.08, min(min(uv.x, uv.y), min(1.0 - uv.x, 1.0 - uv.y)));
            float depth = uWaterLevel - textureLod(uSeaFloor, uv, 0.0).r;
            return mix(1.0, mix(0.35, 1.0, smoothstep(0.0, 2.5, depth)), inMap);
        }

        // The swell's slope (xy) and height above the water level (z).
        vec3 swell(vec2 p)
        {
            vec3 sum = vec3(0.0);
            float wavelength = SwellLongest;
            for (int i = 0; i < SwellCount; i++)
            {
                float h = fract(sin(float(i) * 41.17 + 3.1) * 43758.547);
                float angle = (h - 0.5) * 1.0;
                vec2 dir = mat2(cos(angle), sin(angle), -sin(angle), cos(angle)) * WindDir;
                float k = 6.2832 / wavelength;
                float theta = dot(dir, p) * k - sqrt(9.81 * k) * uTime + h * 40.0;
                float e = exp(sin(theta) - 1.0);
                // (0.47 centres it: exp(sin - 1) averages about that.)
                sum.z += SwellSteep / k * (e - 0.47);
                sum.xy += dir * SwellSteep * cos(theta) * e;
                wavelength *= 0.68;
            }
            return sum * swellScale(p);
        }

        // How much of the swell the drawn water shows at an offset from the camera: none far away,
        // where the grid is coarse and the waves too small to see, nor right round the camera
        // when it is at the water level (swimming), so crests never sweep across the eye.
        float swellFade(vec2 offset)
        {
            float d = length(offset);
            return smoothstep(240.0, 120.0, d)
                 * mix(1.0, smoothstep(0.5, 3.0, d), smoothstep(0.8, 0.2, abs(uCameraPos.y - uWaterLevel)));
        }
        """;

    /// <summary>
    /// The water's surface: a grid of rings round the camera (<c>aOffset</c> in metres, see
    /// <see cref="WaterRenderer"/>) lifted by the swell, which fades out far away, where the grid
    /// is coarse and the waves too small to see, and right round the camera when it is at the
    /// water level (swimming), so crests never sweep across the eye.
    /// </summary>
    public const string WaterVertex = """
        #version 330 core
        layout(location = 0) in vec2 aOffset;

        uniform mat4 uViewProj;
        uniform vec3 uCameraPos;
        uniform float uWaterLevel;
        uniform float uTime;
        uniform sampler2D uSeaFloor;
        uniform vec2 uSeaFloorOrigin;
        uniform float uSeaFloorExtent;

        out vec3 vWorldPos;
        out float vSwell;

        """ + WaterSwell + """

        void main()
        {
            vec2 xz = uCameraPos.xz + aOffset;
            float height = swell(xz).z * swellFade(aOffset);
            vec3 p = vec3(xz.x, uWaterLevel + height, xz.y);
            vWorldPos = p;
            vSwell = height;
            gl_Position = uViewProj * vec4(p, 1.0);
        }
        """;

    /// <summary>
    /// Water: normals from the swell and the wind waves, the sky (galaxy and stars included) reflected
    /// with Fresnel, the scene beneath seen through it (from a snapshot) and absorbed with depth,
    /// a glittering path toward the sun or moon, and bioluminescence: soft drifting clouds of
    /// plankton light breathing toward the shore, a gentle line along the shore and the odd sparkle.
    /// </summary>
    public const string WaterFragment = FragmentHeader + SkyRenderer.LayersGlsl + """

        in vec3 vWorldPos;
        in float vSwell;

        uniform sampler2D uUnderColor; // the scene before the water was drawn
        uniform sampler2D uSeaFloor;   // smooth land height around the camera (SeaFloorMap)
        uniform vec2 uSeaFloorOrigin;
        uniform float uSeaFloorExtent;
        uniform float uWaterLevel;
        uniform sampler2D uUnderDepth;
        uniform vec2 uScreenSize;
        uniform float uNear;
        uniform float uFar;
        // Rings on the water round what stands in it (reed clumps, lotus, blocks: x, z, its radius,
        // a phase), and the path the player has swum or waded, oldest first, up to where they are
        // now (x, z, when; w 0 for an unused point), see Game.UpdateRipples.
        const int MaxRipplers = 40, MaxWake = 40;
        uniform vec4 uRippler[MaxRipplers];
        uniform int uRipplerCount;
        uniform vec4 uWakeStamp[MaxWake];
        uniform vec4 uWakeBounds; // a circle round the wake still alive (xy, radius z; 0: none) and its first stamp (w)

        out vec4 FragColor;

        """ + WaterSwell + """

        // Window depth to view-space distance (System.Numerics projection: NDC depth in [0, 1]).
        float viewDepth(float windowDepth)
        {
            float z = windowDepth * 2.0 - 1.0;
            return uFar * uNear / (uFar - z * (uFar - uNear));
        }

        // Gusts: patches of rougher water drifting downwind (~3 m/s) over calmer, glassier water.
        // Returns how hard the wind blows there, 0.25 (calm) to ~1.4.
        float gusts(vec2 p)
        {
            float a = texture(uCloudNoise, vec3(p * 0.012 - WindDir * uTime * 0.036, 0.42)).r;
            float b = texture(uCloudNoise, vec3(p * 0.03 - WindDir * uTime * 0.07, 0.58)).r;
            return mix(0.25, 1.4, smoothstep(0.38, 0.68, a * 0.7 + b * 0.3));
        }

        const int WaveCount = 10;
        // Each wave's direction, wave number (2 pi / length: from 3.5 m, each 0.68 times the last),
        // deep-water speed (sqrt(g k)), phase, steepness, and how much it reacts to the gusts; and
        // for each, the roughness the waves from it on add once too fine to draw (the sums of
        // steep^2, steep^2 gust, steep^2 gust^2 from it to the last, so a gust's roughness is
        // 0.5 (x + 2 (g - 1) y + (g - 1)^2 z)). Worked out once (they were computed with sin, cos
        // and sqrt for every wave of every pixel).
        const vec2 WaveDir[WaveCount] = vec2[WaveCount](vec2(0.464553, 0.885547), vec2(0.405694, 0.914011), vec2(0.578350, 0.815791), vec2(0.860445, 0.509548), vec2(0.988651, 0.150241), vec2(0.290677, 0.956823), vec2(0.348289, 0.937390), vec2(0.953517, 0.301347), vec2(0.592995, 0.805208), vec2(0.495537, 0.868589));
        const float WaveK[WaveCount] = float[WaveCount](1.795196, 2.639994, 3.882344, 5.709329, 8.396072, 12.347165, 18.157596, 26.702347, 39.268157, 57.747290);
        const float WaveOmega[WaveCount] = float[WaveCount](4.196531, 5.089041, 6.171369, 7.483884, 9.075542, 11.005712, 13.346386, 16.184870, 19.627038, 23.801280);
        const float WavePhase[WaveCount] = float[WaveCount](32.448657, 33.987296, 29.305742, 19.436682, 10.404997, 36.876778, 35.445954, 14.058940, 28.880583, 31.617523);
        const float WaveSteep[WaveCount] = float[WaveCount](0.045000, 0.049444, 0.053889, 0.058333, 0.062778, 0.067222, 0.071667, 0.076111, 0.080556, 0.085000);
        const float WaveGust[WaveCount] = float[WaveCount](0.500000, 0.555556, 0.611111, 0.666667, 0.722222, 0.777778, 0.833333, 0.888889, 0.944444, 1.000000);
        const vec3 WaveRoughRest[WaveCount] = vec3[WaveCount](vec3(0.043880, 0.035558, 0.029804), vec3(0.041855, 0.034545, 0.029298), vec3(0.039410, 0.033187, 0.028543), vec3(0.036506, 0.031413, 0.027459), vec3(0.033103, 0.029144, 0.025946), vec3(0.029162, 0.026298, 0.023891), vec3(0.024643, 0.022783, 0.021157), vec3(0.019507, 0.018503, 0.017590), vec3(0.013714, 0.013354, 0.013013), vec3(0.007225, 0.007225, 0.007225));

        // Wind waves: WaveCount travelling waves from 3.5 metres down to a few centimetres,
        // fanned out around the wind's direction, each running at the deep-water speed for its
        // length with sharp crests and broad troughs (exp(sin)). Returns the surface slope (xy)
        // and, in z, the variance of the slope of the waves too fine for the pixel, which are left
        // out (they would only shimmer) and turned into roughness instead. The waves run from the
        // longest down, so once one is under two pixels the rest are all roughness, added at once
        // from WaveRoughRest: far water computes only its few long waves.
        vec3 windWaves(vec2 p, float footprint, float gust)
        {
            vec3 sum = vec3(0.0);
            float g = gust - 1.0;
            float perPixel = 1.0 / max(footprint, 1e-4);
            for (int i = 0; i < WaveCount; i++)
            {
                float pixels = 6.2831853 / WaveK[i] * perPixel; // the wave's length in pixels
                if (pixels < 2.0)
                {
                    vec3 rest = WaveRoughRest[i];
                    sum.z += 0.5 * (rest.x + 2.0 * g * rest.y + g * g * rest.z);
                    break;
                }
                // Steeper short waves, and those react most to the gusts (long ones only half).
                float steep = WaveSteep[i] * (1.0 + g * WaveGust[i]);
                float theta = dot(WaveDir[i], p) * WaveK[i] - WaveOmega[i] * uTime + WavePhase[i];
                // exp(sin - 1): sharp crests; its slope is steep * cos * exp(sin - 1).
                float slope = steep * cos(theta) * exp(sin(theta) - 1.0);
                // Fade a wave out as it gets shorter than ~4 pixels, its slope going into roughness.
                float keep = smoothstep(2.0, 4.0, pixels);
                sum.xy += WaveDir[i] * slope * keep;
                sum.z += steep * steep * 0.5 * (1.0 - keep);
            }
            return sum;
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

        // The surface normal, and in w its roughness (0 glassy .. 1): the slope of the waves too
        // fine to draw, so far water turns satin and sheeny instead of a flat mirror.
        vec4 waterNormal(vec2 p, float dist, vec3 rings)
        {
            vec2 fw = fwidth(p);
            float footprint = max(fw.x, fw.y);
            float gust = gusts(p);
            vec3 w = windWaves(p, footprint, gust);
            w.xy += swell(p).xy;
            vec3 n = vec3(-w.x, 1.0, -w.y);
            n.xz += rings.xy * 0.25 * uRain * smoothstep(60.0, 20.0, dist);
            // Rain roughens the whole surface.
            float rough = clamp(sqrt(w.z) * 4.0 + uRain * 0.3, 0.0, 1.0);
            return vec4(normalize(n), rough);
        }

        // The slope the rings on the water add. From every lotus, block and reed stem standing in
        // the water (a reed clump has RingStems stems scattered over it) rings keep spreading out:
        // a train of fine waves (~RingWavelength apart) leaving at RingSpeed, gathered in groups
        // that travel outward too, dying away within a few metres. The waves of neighbouring stems
        // are summed, so where they meet they cross and interfere. Finer than a few pixels they
        // fade out (`footprint`: metres per pixel), as they would only shimmer.
        const float RingSpeed = 0.35, RingWavelength = 0.26, RingReach = 3.5;
        const int RingStems = 3;
        vec2 ripples(vec2 p, float footprint)
        {
            float visible = smoothstep(3.0, 6.0, RingWavelength / max(footprint, 1e-4));
            if (visible <= 0.0) return vec2(0.0);
            const float k = 6.2832 / RingWavelength, kGroup = 6.2832 / 1.3;
            vec2 sum = vec2(0.0);
            for (int i = 0; i < MaxRipplers; i++)
            {
                if (i >= uRipplerCount) break;
                vec4 s = uRippler[i];
                if (distance(p, s.xy) > RingReach + s.z) continue;
                bool reeds = s.w < 0.0;
                float phase = abs(s.w);
                int stems = reeds ? RingStems : 1;
                for (int j = 0; j < RingStems; j++)
                {
                    if (j >= stems) break;
                    // A stem somewhere in the clump (or the lotus or block itself, at its centre).
                    float h = fract(phase * 7.13 + float(j) * 0.618);
                    vec2 stem = s.xy + (reeds ? vec2(cos(h * 6.2832), sin(h * 6.2832)) * s.z * (0.3 + 0.6 * fract(h * 13.7)) : vec2(0.0));
                    float start = reeds ? 0.03 : s.z;
                    vec2 off = p - stem;
                    float d = length(off);
                    float x = d - start;
                    if (x < 0.0 || x > RingReach) continue;
                    float own = phase * 3.7 + float(j) * 2.1;
                    float wave = sin(k * (x - uTime * RingSpeed) + own);
                    float groups = 0.5 + 0.5 * sin(kGroup * (x - uTime * RingSpeed * 0.8) + own * 1.3);
                    float envelope = exp(-x * 0.9) / sqrt(1.0 + x * 3.0) * smoothstep(0.0, 0.06, x);
                    sum += off / max(d, 1e-3) * wave * groups * envelope * (reeds ? 0.22 : 0.3);
                }
            }
            return sum * visible;
        }

        // The player's wake, along the path they swam or waded (uWakeStamp). How far a point is
        // from the path, and how long ago the player passed there, are blended over the nearby
        // segments (a soft minimum), so the wake bends smoothly round the turns instead of
        // breaking into straight pieces with corners. Behind the player its waves fan out: crests
        // slanting back from the path (a feathered V), spreading to WakeSpread m/s, broken here
        // and there, dying away over WakeLife seconds. Returns the slope.
        const float WakeLife = 6.0, WakeSpread = 0.45, WakeBlend = 0.35;
        vec2 wake(vec2 p)
        {
            if (uWakeBounds.z <= 0.0 || distance(p, uWakeBounds.xy) > uWakeBounds.z) return vec2(0.0);
            float weights = 0.0, ageSum = 0.0;
            vec2 awaySum = vec2(0.0);
            for (int i = int(uWakeBounds.w); i < MaxWake - 1; i++)
            {
                vec4 a = uWakeStamp[i], b = uWakeStamp[i + 1];
                if (a.w <= 0.0 || b.w <= 0.0) continue;
                vec2 ab = b.xy - a.xy;
                float len2 = dot(ab, ab);
                if (len2 > 4.0) continue; // a jump, not a path
                float t = len2 > 1e-6 ? clamp(dot(p - a.xy, ab) / len2, 0.0, 1.0) : 0.0;
                vec2 off = p - (a.xy + ab * t);
                float d = length(off);
                if (d > 5.0) continue;
                float w = exp(-d / WakeBlend);
                weights += w;
                ageSum += w * (uTime - mix(a.z, b.z, t));
                awaySum += w * off / max(d, 1e-3);
            }
            if (weights < 1e-6) return vec2(0.0);
            float dist = -WakeBlend * log(weights);
            float age = ageSum / weights;
            if (age > WakeLife || dist > 4.0) return vec2(0.0);
            vec2 away = awaySum / max(length(awaySum), 1e-4);
            float life = 1.0 - age / WakeLife;
            // The front of the spreading wake, and behind it (toward the path) the feathered crests.
            float front = 0.2 + age * WakeSpread;
            float inside = smoothstep(0.0, 0.25, dist) * smoothstep(front + 0.25, front - 0.15, dist);
            float crests = sin(dist * 22.0 - age * 7.0);
            float edge = exp(-(dist - front) * (dist - front) * 30.0) * sin((dist - front) * 26.0);
            float broken = 0.4 + 1.2 * texture(uCloudNoise, vec3(p * 0.5, 0.61)).r;
            return away * (crests * inside * 0.6 + edge) * life * life * broken * 0.4;
        }

        // The primordial soup: broad patches of light in the water, pink, indigo and red, that
        // slowly flow, swirl, stretch, merge and part (domain-warped noise, its warp drifting),
        // grainy like glowing plankton, brighter at night.
        vec3 soupPalette(float hue)
        {
            const vec3 hues[6] = vec3[6](vec3(1.0, 0.3, 0.8), vec3(1.0, 0.15, 0.25), vec3(1.0, 0.65, 0.15),
                                         vec3(0.15, 0.9, 1.0), vec3(0.3, 0.25, 1.0), vec3(0.75, 0.25, 1.0));
            float x = hue * 6.0;
            int i = int(floor(x));
            float f = smoothstep(0.0, 1.0, x - floor(x));
            return mix(hues[i % 6], hues[(i + 1) % 6], f);
        }

        vec3 soup(vec2 p)
        {
            // (Read from the noise's blurred mip levels: broad soft masses, no fine marbling.)
            vec2 q = p * 0.009;
            vec2 warp = vec2(textureLod(uCloudNoise, vec3(q * 0.8 + vec2(uTime * 0.004, 0.0), 0.13), 3.0).r,
                             textureLod(uCloudNoise, vec3(q * 0.8 + vec2(0.0, -uTime * 0.0035), 0.47), 3.0).r) - 0.5;
            vec2 flow = q + warp * 2.4 + vec2(uTime * 0.0025, -uTime * 0.0018);
            float body = textureLod(uCloudNoise, vec3(flow, 0.71), 2.5).r * 0.8 + textureLod(uCloudNoise, vec3(flow * 2.3 - warp, 0.29), 2.0).r * 0.2;
            float patches = smoothstep(0.44, 0.6, body);
            if (patches <= 0.0) return vec3(0.0);
            // Colour: its own slow warped noise, run round a ring of vivid hues (pink, red, gold,
            // cyan, indigo, violet), drifting, so every patch has its own and they blend where they
            // meet. (The blurred noise varies little: stretched so the whole ring comes out.)
            float hue = textureLod(uCloudNoise, vec3(flow * 0.7 + warp * 0.8 + 3.1, 0.88), 2.5).r;
            hue = fract(hue * 4.0 + uTime * 0.004);
            vec3 tint = soupPalette(hue);
            float grain = 0.5 + 1.0 * texture(uCloudNoise, vec3(p * 0.6 + warp * 2.0, 0.55 + uTime * 0.002)).r;
            // Soft and even over the patches; the strong light only in narrow streaks winding
            // through them (where the noise crosses a level), never in whole bright areas.
            float streak = exp(-(body - 0.62) * (body - 0.62) / (0.012 * 0.012)) * smoothstep(0.44, 0.52, body);
            return tint * (patches * 0.55 * grain + streak * 1.4);
        }

        void main()
        {
            vec2 uv = gl_FragCoord.xy / uScreenSize;
            vec3 toFragment = vWorldPos - uCameraPos;
            float dist = length(toFragment);
            vec3 rd = toFragment / dist;
            // Rain rings, computed once for both the ripples and their glow.
            vec3 rings = uRain > 0.01 && dist < 60.0 ? rainRings(vWorldPos.xz) : vec3(0.0);
            vec4 surfaceNormal = waterNormal(vWorldPos.xz, dist, rings);
            // The rings (near only) bend the surface fully, also in the night mirror below.
            vec2 fwRipple = fwidth(vWorldPos.xz);
            vec2 rip = dist < 60.0 && uUnderwater < 0.5 ? (ripples(vWorldPos.xz, max(fwRipple.x, fwRipple.y)) + wake(vWorldPos.xz)) * smoothstep(60.0, 30.0, dist) : vec2(0.0);
            vec3 waves = surfaceNormal.xyz;
            vec3 n = normalize(waves - vec3(rip.x, 0.0, rip.y));
            float rough = surfaceNormal.w;

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
            // (Clear enough that the floor shows through the mirror.)
            vec3 refracted = mix(under, deep, 1.0 - exp(-thickness * 0.14));

            // Reflection of the whole sky, galaxy and stars included. At night (mirror) the water is
            // a mirror of the sky: the galaxy, the stars and the moon are seen in it, whole. The
            // reflection then follows the waves only a 25th as much (they barely ripple the image
            // instead of breaking it up), and at least MirrorReflectance of the sky is reflected
            // even looking straight down (dark water, really: the bottom is unlit at night). By day
            // the water is a softer mirror (DayReflectance, the waves followed 30%), so the sky is
            // seen in it and the rings round things and the wake show by day too.
            const float MirrorReflectance = 0.8, DayReflectance = 0.35;
            float mirror = smoothstep(0.2, 0.8, uNight);
            float follow = mix(0.3, 0.04, mirror);
            vec3 nr = normalize(vec3(waves.x * follow - rip.x, 1.0, waves.z * follow - rip.y));
            vec3 r = reflect(rd, nr);
            // Rough water, on average, mirrors the sky a little higher than glassy water would.
            r.y = max(abs(r.y), rough * 0.12 * (1.0 - mirror));
            r = normalize(r);
            // (and less of it at grazing angles, where the tiny waves' backs face the viewer).
            float fresnel = 0.02 + 0.98 * pow(1.0 - max(dot(nr, -rd), 0.0), 5.0) * (1.0 - 0.35 * rough * (1.0 - mirror));
            float physicalFresnel = fresnel;
            fresnel = mix(mix(DayReflectance, MirrorReflectance, mirror), 1.0, fresnel);
            // Capped by day, so the sun-lit sky reflected in every ripple does not wash out into
            // white; the night mirror keeps the moon and the galaxy's core bright. Shooting stars
            // are drawn wider in it, or the least ripple would break their hair-thin trail away.
            gStreakSharpness = 4e4;
            vec3 color = mix(refracted, min(mirroredSky(r), vec3(mix(1.6, 6.0, mirror))), fresnel);
            gStreakSharpness = 3e5;
            // What glows under the water (the crystal outcrops on the floor, their light on it)
            // shines through the mirror as real water would let it, dimmed by the water it crosses,
            // so the lake has depths under the mirrored sky.
            vec3 glowBelow = max(under - vec3(0.45), vec3(0.0)) * exp(-thickness * 0.06);
            color += glowBelow * (1.0 - physicalFresnel) * mix(0.6, 1.3, mirror);
            if (uUnderwater > 0.5)
            {
                // Seen from below: the bright, rippled sky through the surface, fading at grazing
                // angles into the silvery mirror of total internal reflection.
                vec3 up = mirroredSky(normalize(vec3(rd.x, abs(rd.y) * 1.5, rd.z)));
                color = mix(vec3(0.08, 0.12, 0.3), min(up, vec3(1.5)) * 0.8, smoothstep(0.05, 0.4, abs(rd.y)));
            }

            // Rain rings glow faintly, as if each drop woke the bioluminescence.
            if (uRain > 0.01 && dist < 60.0)
                color += mix(vec3(0.3, 0.8, 1.0), vec3(0.8, 0.45, 1.0), 0.5 + 0.5 * sin(vWorldPos.x * 0.3 + vWorldPos.z * 0.2))
                    * rings.z * 0.12 * uRain * mix(0.6, 1.5, uNight) * smoothstep(60.0, 20.0, dist);

            // Light through the crests: looking toward the sun or moon, the thin water at the top of
            // each swell glows turquoise, lit from behind; at night the crests glow faintly by
            // themselves, as if the swell stirred the bioluminescence.
            float crest = clamp(vSwell / 0.12, 0.0, 1.0);
            float against = pow(clamp(dot(normalize(rd.xz + 1e-5), normalize(uLightDir.xz + 1e-5)), 0.0, 1.0), 3.0);
            // (Faint under the moon: it lit the night lake in bands.)
            color += vec3(0.25, 0.8, 0.95) * uLightColor * crest * crest * against * (1.0 - fresnel) * 0.6 * mix(1.0, 0.3, uNight);

            // A glittering path toward the sun or the moon.
            // (By day from the full waves, so the sun still glitters in a broad path; at night from
            // the mirror's calmer surface.)
            float toLight = max(dot(reflect(rd, normalize(mix(n, nr, mirror))), uLightDir), 0.0);
            // (Softer under the moon, whose glints would otherwise flood the rippled water.)
            // On rough water the sharp glints widen into a broad, dimmer sheen.
            float sharpness = mix(400.0, 60.0, rough);
            // (Not in the night mirror: the moon is mirrored whole, and its glint was a bright dot
            // in the middle of it.)
            color += uLightColor * (pow(toLight, sharpness) * mix(8.0, 2.0, uNight) * mix(1.0, 0.35, rough) * (1.0 - mirror)
                                  + pow(toLight, mix(40.0, 12.0, rough)) * mix(0.35, 0.12, uNight) * mix(1.0, 0.4, mirror));

            // The real depth of the water here (from the sea floor map; past its edge, the depth the
            // view ray measures).
            vec2 mapUv = (vWorldPos.xz - uSeaFloorOrigin) / uSeaFloorExtent;
            float inMap = smoothstep(0.0, 0.08, min(min(mapUv.x, mapUv.y), min(1.0 - mapUv.x, 1.0 - mapUv.y)));
            float seaDepth = uWaterLevel - texture(uSeaFloor, mapUv).r;
            float waterDepth = mix(depth, max(seaDepth, 0.0), inMap);

            // Bioluminescence, meant to be watched for a long time: soft clouds of glowing plankton,
            // sparse, drifting very slowly, out in the open water, away from the shores (offshore:
            // by the real depth), and never right in front of the viewer, so someone standing on
            // the shore to watch the lake is not dazzled. They breathe in slow waves of light rolling toward the shore (~14 s), and the
            // swell carries the light, brighter on its crests. Within the clouds, faint filaments.
            float cloudA = texture(uCloudNoise, vec3(vWorldPos.xz * 0.015 + vec2(uTime * 0.0025, uTime * 0.0012), 0.9)).r;
            float cloudB = texture(uCloudNoise, vec3(vWorldPos.xz * 0.05 - vec2(uTime * 0.004, -uTime * 0.003), 0.62)).r;
            float plankton = smoothstep(0.45, 0.8, cloudA * 0.65 + cloudB * 0.35);
            float offshore = smoothstep(1.0, 4.0, waterDepth) * smoothstep(6.0, 18.0, dist);
            float breathe = 0.6 + 0.4 * sin(uTime * 0.45 - waterDepth * 1.2 + cloudA * 3.0);
            float carried = 0.7 + 0.6 * crest;
            float filament = pow(max(1.0 - abs(cloudB - 0.5) * 2.0, 0.0), 14.0) * plankton;
            vec3 planktonColor = mix(vec3(0.2, 0.75, 1.0), vec3(0.45, 0.5, 1.0), smoothstep(0.3, 0.7, cloudB));
            vec3 glow = planktonColor * (plankton * 0.55 + filament * 0.3) * breathe * carried * offshore;
            // A soft line of light where the water laps the sand, slowly coming and going.
            float shore = exp(-waterDepth * 8.0) * (0.65 + 0.35 * sin(uTime * 0.7 + vWorldPos.x * 0.15 + vWorldPos.z * 0.1));
            glow += vec3(0.25, 0.65, 1.0) * shore * 0.07;
            glow += vec3(0.3, 0.85, 1.0) * crest * crest * crest * 0.25 * uNight;
            // Now and then a sparkle, like a star fallen in the water, slowly waxing and waning.
            vec2 cell = floor(vWorldPos.xz * 3.0);
            float h = hash13(vec3(cell, 11.0));
            if (h > 0.996 && dist < 90.0)
            {
                float sparkle = smoothstep(0.35, 0.0, length(fract(vWorldPos.xz * 3.0) - 0.5));
                float wax = smoothstep(0.55, 1.0, 0.5 + 0.5 * sin(uTime * 0.9 + h * 70.0));
                glow += mix(vec3(0.6, 0.9, 1.0), vec3(1.0, 0.7, 1.0), hash13(vec3(cell, 3.0)))
                      * sparkle * wax * 1.2 * smoothstep(90.0, 40.0, dist);
            }
            // Breaking waves: bands rolling in to the shore, laid out along the depth contours of the
            // smooth sea floor (so they follow every coastline in soft curves, not the layered tiles),
            // rearing up as the water gets shallow and breaking into glowing pink-white foam that
            // trails behind each crest, a cyan glow on each rising face. Each wave is stronger or
            // weaker along its length, and broken up where it is weak. Off for now (BreakingWaves),
            // on trial: their lines moving over the surface broke up the mirrored sky.
            const bool BreakingWaves = false;
            if (BreakingWaves && inMap > 0.0 && seaDepth < 7.0 && uUnderwater < 0.5)
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
                // (At night the foam's glow is gentler: it is the brightest thing on the water.)
                glow += foam * surf * mix(2.4, 0.9, uNight) + vec3(0.25, 0.7, 1.0) * face * mix(0.8, 0.3, uNight);
            }

            // (At night, softer: the mirrored sky is the show.)
            color += glow * mix(0.45, 1.0, uNight) * mix(0.15, 1.0, uMagic) * mix(1.0, 0.5, mirror);
            // The primordial soup of light, brightest at night, fading far away (where it would
            // only be a haze).
            if (uUnderwater < 0.5 && dist < 400.0)
            {
                // Never brighter than SoupCap (its hue kept), so it cannot dazzle.
                const float SoupCap = 0.9;
                // (Away from the shores and from the viewer, like the plankton: offshore.)
                vec3 light = soup(vWorldPos.xz) * mix(0.4, 1.2, uNight) * mix(0.15, 1.0, uMagic) * smoothstep(400.0, 200.0, dist) * offshore;
                float peak = max(max(light.r, light.g), light.b);
                color += light * min(1.0, SoupCap / max(peak, 1e-4));
            }

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
                // The side fins flutter (fins carry aColor.g = 1, and how far out toward the tip in b).
                if (aColor.g > 0.5 && abs(aPos.z) > 0.06) p.y += sin(t * 14.0 + aPos.x * 20.0) * 0.025 * aColor.b;
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
            else if (uKind == 2)
            {
                // Surface fish: a silvery body with a touch of the glow hue, and fins of one colour
                // per fish (red, violet, yellow, orange, cyan or magenta), paler toward their tips.
                float k = fract(aExtra.y * 0.618);
                vec3 fin = k < 0.17 ? vec3(1.0, 0.18, 0.22) : k < 0.33 ? vec3(0.6, 0.25, 1.0) : k < 0.5 ? vec3(1.0, 0.85, 0.2)
                         : k < 0.67 ? vec3(1.0, 0.5, 0.12) : k < 0.83 ? vec3(0.2, 0.9, 1.0) : vec3(1.0, 0.3, 0.8);
                vec3 body = mix(vec3(0.75, 0.85, 1.0), hue, 0.35);
                vColor = mix(body, fin * (0.75 + 0.5 * aColor.b), aColor.g);
                vEmissive = aEmissive;
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

    /// <summary>
    /// Small instanced cubes (chips and material cubes, see Debris): one shared model placed, sized,
    /// turned (a quaternion) and tinted per instance; drawn with <see cref="ObjectFragment"/>.
    /// </summary>
    public const string CubeVertex = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec3 aColor;
        layout(location = 3) in float aEmissive;
        layout(location = 4) in vec4 aPlace; // position, size
        layout(location = 5) in vec4 aTurn;  // rotation (quaternion)
        layout(location = 6) in vec3 aTint;

        uniform mat4 uViewProj;

        out vec3 vWorldPos;
        out vec3 vNormal;
        out vec3 vColor;
        out float vEmissive;

        vec3 turn(vec4 q, vec3 v) { return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v); }

        void main()
        {
            vec3 world = aPlace.xyz + turn(aTurn, aPos * aPlace.w);
            vWorldPos = world;
            vNormal = turn(aTurn, aNormal);
            vColor = aColor * aTint;
            vEmissive = aEmissive;
            gl_Position = uViewProj * vec4(world, 1.0);
        }
        """;

    public const string ObjectFragment = FragmentHeader + """

        in vec3 vWorldPos;
        in vec3 vNormal;
        in vec3 vColor;
        in float vEmissive;

        uniform float uGlow;      // flicker of the object's own light
        uniform float uHighlight; // 1 while the player aims at the object
        uniform float uGlass;     // 1 for glass: see-through, premultiplied alpha (glassColor)
        uniform float uSnowless;  // 1 for what the player holds: no snow settles on it

        out vec4 FragColor;

        void main()
        {
            vec3 n = normalize(vNormal);
            if (uGlass > 0.5)
            {
                FragColor = glassColor(vWorldPos, n, vColor, vEmissive);
                return;
            }
            vec3 albedo = mix(vColor, SnowColor, snowCover(vWorldPos, n.y) * (1.0 - vEmissive) * (1.0 - uSnowless));
            vec3 color = litColor(albedo, vWorldPos, n, 0.0);
            // Glowing parts shine with their own colour, brighter at night.
            color = mix(color, vColor * uGlow * mix(1.4, 2.2, uNight), vEmissive);
            color += vColor * 0.35 * uHighlight;
            FragColor = finishColor(color, vWorldPos, 1.0 - 0.8 * vEmissive);
        }
        """;
}
