using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Draws the cosmic sky (gradient, a giant spiral galaxy, nebulae, dense stars, shooting stars,
/// the sun, a giant crescent planet with two small moons, auroras, volumetric clouds) as a
/// full-screen triangle.
/// Output is HDR (bright things above 1, for the bloom); tone mapping happens in the post-process pass.
/// It is drawn after the opaque geometry, at the far plane with a less-or-equal depth test, so it
/// only shades the pixels where the sky is actually visible. The expensive cloud ray march runs
/// before it, at half resolution and only near those pixels (<see cref="DrawClouds"/>).
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
    /// The sky's colour in a direction without the sky bodies (<c>skyGradient(dir)</c>, what
    /// <c>skyColor(dir, false)</c> returns) with the uniforms it reads. Usable in any shader stage:
    /// the grass computes it per vertex. Part of <see cref="Glsl"/>.
    /// </summary>
    public const string SkyGradient = """
        uniform float uRain;      // 0 = clear .. 1 = full rain: the clouds close in
        uniform float uSnow;      // 0 = clear .. 1 = full snowfall: overcast and cold
        uniform float uStorm;     // 0..1: how much of the rain is a storm
        uniform float uBlizzard;  // 0..1: how much of the snow is a blizzard
        uniform float uLightning; // brightness of the current lightning flash
        uniform vec3 uZenith;
        uniform vec3 uHorizon;
        uniform vec3 uSunHorizon;
        uniform vec3 uSunDir;
        uniform vec3 uMoonDir;
        uniform vec3 uSunGlow;
        uniform float uHaze;
        uniform float uNight;

        vec3 skyGradient(vec3 d)
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
            // Rain dims the sky toward a deep, glowing indigo (never a dull grey).
            c = mix(c, c * 0.55 + vec3(0.05, 0.035, 0.12), uRain * 0.6);
            // Snow turns the sky a cold, pale blue-grey (a deep slate at night).
            c = mix(c, vec3(0.58, 0.63, 0.75) * mix(1.0, 0.16, uNight) + c * 0.15, uSnow * 0.6);
            // A storm darkens it further; a blizzard fills it with blowing white.
            c *= 1.0 - 0.3 * uStorm;
            c = mix(c, vec3(0.74, 0.78, 0.86) * mix(1.0, 0.18, uNight), uBlizzard * 0.7);
            // Lightning lights up the whole sky.
            c += vec3(0.75, 0.8, 1.0) * uLightning * (0.3 + 0.4 * up);
            // Below the horizon (distant fog) fade to a slightly darker horizon.
            c = mix(c, horizon * 0.85, clamp(-d.y * 4.0, 0.0, 1.0));

            float sd = max(dot(d, uSunDir), 0.0);
            c += uSunGlow * (pow(sd, 4.0) * (0.12 + 0.3 * uHaze) + pow(sd, 32.0) * 0.4 + pow(sd, 300.0) * 0.6);
            float md = max(dot(d, uMoonDir), 0.0);
            c += vec3(0.35, 0.45, 0.8) * (pow(md, 12.0) * 0.25 + pow(md, 80.0) * 0.3) * uNight;

            return c;
        }

        """;

    /// <summary>
    /// Uniforms, <see cref="Hash"/>, <see cref="SkyGradient"/>, <c>skyColor(dir, bodies)</c> and <c>cloudDensity(p, detail)</c>,
    /// to paste after <c>#version</c> in fragment shaders (it uses <c>fwidth</c>). The cloud noise
    /// texture must be bound to unit <see cref="CloudNoiseUnit"/>.
    /// </summary>
    public const string Glsl = Hash + SkyGradient + """
        uniform sampler3D uCloudNoise;
        uniform float uCloudTime; // game seconds, so clouds speed up with the clock
        uniform float uSnowCover; // snow lying on the world: 0 = none .. 1 = everything white
        uniform float uClearSky;  // 0 = the usual clouds .. 1 = hardly any (WorldPreset.ClearSky)
        uniform float uPlainNight;
        uniform float uPlanetRim; // 1: the planet's shadow keeps a glowing rim (WorldPreset.PlanetRim) // 1: the planet only as a shadow, no auroras, galaxy or nebulae
        uniform float uMagic;     // 1 = the full magical night and bioluminescence, lower = more modest (WorldPreset.Magic)
        uniform vec2 uLightningBolt; // azimuth of the bolt (radians), seed of its shape

        const vec3 SnowColor = vec3(0.9, 0.93, 1.0);

        // How much snow lies at a point on a surface facing up by `up` (its normal's y): it settles
        // first on the heights and creeps down to the shores as the cover grows, only on surfaces
        // that face up, in soft drifts.
        float snowOn(vec3 pos, float up)
        {
            if (uSnowCover <= 0.001) return 0.0;
            float line = mix(95.0, 12.0, pow(uSnowCover, 0.6));
            float drift = texture(uCloudNoise, pos * 0.02).r;
            return smoothstep(line, line + 8.0, pos.y + drift * 10.0) * smoothstep(0.45, 0.8, up) * smoothstep(0.0, 0.25, uSnowCover);
        }
        const float CloudBottom = 260.0;
        const float CloudTop = 760.0;

        // Cloud density at a point: broad shapes from low-frequency noise, rounded at the base and
        // thinning toward the top of the layer; detail erodes the edges into wisps. lod blurs the
        // noise (mip level), for long ray-march steps and soft cloud shadows.
        vec3 cloudCoords(vec3 p) { return (p + vec3(1.0, 0.0, 0.35) * uCloudTime * 3.0) * 0.0011; }

        // The coverage threshold at a point: a very broad noise opens clear skies between banks of
        // big towering clouds, and rain lowers it until the sky is overcast. It changes over
        // kilometres, so the ray march reads it once per step and reuses it for the light samples.
        float cloudThreshold(vec3 p, float lod)
        {
            float macro = textureLod(uCloudNoise, cloudCoords(p) * 0.23 + 0.11, lod + 1.0).r;
            return 0.6 + (0.5 - macro) * 0.32 + 0.22 * uClearSky - 0.3 * max(uRain, uSnow * 0.85) - 0.1 * max(uStorm, uBlizzard);
        }

        // Density before clamping to 0..1 (negative: clear air, the more so the farther from a cloud).
        float cloudDensityRaw(vec3 p, bool detail, float lod, float threshold)
        {
            float h = (p.y - CloudBottom) / (CloudTop - CloudBottom);
            if (h < 0.0 || h > 1.0) return -1.0;
            vec3 q = cloudCoords(p);
            float shape = textureLod(uCloudNoise, q * vec3(1.0, 1.8, 1.0), lod).r * 0.7 + textureLod(uCloudNoise, q * 2.7 + 0.37, lod + 1.4).r * 0.3;
            float profile = smoothstep(0.0, 0.12, h) * smoothstep(1.0, 0.4, h);
            float d = (shape * profile - threshold) * 4.2;
            if (detail && d > 0.0) d -= (1.0 - textureLod(uCloudNoise, q * 5.0 + 0.71, lod + 2.3).r) * 0.2;
            return d;
        }

        float cloudDensity(vec3 p, bool detail, float lod)
        {
            return clamp(cloudDensityRaw(p, detail, lod, cloudThreshold(p, lod)), 0.0, 1.0);
        }

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

        // Craters on the moon at one scale: a few random ones per cell of a grid over the disk,
        // each a darker bowl shaded from the light side with a bright rim. Returns a shade factor.
        float moonCraters(vec2 uv, vec2 light, float density)
        {
            float shade = 1.0;
            vec2 cell = floor(uv);
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
            {
                vec2 id = cell + vec2(x, y);
                float h = hash13(vec3(id, 21.0));
                if (h > density) continue;
                vec2 center = id + vec2(hash13(vec3(id, 5.0)), hash13(vec3(id, 8.0)));
                float radius = 0.18 + 0.3 * hash13(vec3(id, 13.0));
                vec2 off = (uv - center) / radius;
                float r = length(off);
                if (r > 1.4) continue;
                // Inside the bowl the wall facing the light is lit, the other in shadow.
                float facing = dot(off, light);
                float bowl = smoothstep(1.0, 0.75, r);
                shade *= mix(1.0, 0.82 + 0.25 * facing, bowl);
                shade *= 1.0 + 0.25 * exp(-(r - 1.0) * (r - 1.0) * 40.0) * max(-facing, 0.3); // raised rim
            }
            return shade;
        }

        // The moon is a giant planet, seen as a crescent: lit from behind and to one side, so most
        // of its face is in shadow (deep violet, cratered, with faint glowing cracks) and only a
        // thin crescent catches the light, while its limb glows lilac-magenta all round, brightest
        // on the lit side. Two small moons of the same kind hang beside it.
        const float PlanetSize = 0.36; // tangent of its angular radius (about 20 degrees)
        const vec3 PlanetLight = vec3(-0.55, -0.4, -0.75); // mostly from behind: a thin crescent

        vec3 planetSurface(vec2 q)
        {
            float rr = min(dot(q, q), 1.0);
            vec3 n = vec3(q, sqrt(1.0 - rr));
            vec3 light = normalize(PlanetLight);
            vec2 flatLight = normalize(light.xy);
            float grain = texture(uCloudNoise, n * 1.4 + vec3(0.2)).r * 0.6 + texture(uCloudNoise, n * 3.5 + vec3(0.5)).r * 0.4;
            float v = texture(uCloudNoise, n * 0.9 + vec3(0.7, 0.2, 0.4)).r;
            float cracks = pow(max(1.0 - abs(v - 0.5) * 2.0, 0.0), 14.0);
            float craters = moonCraters(q * 4.0 + 7.0, flatLight, 0.3) * moonCraters(q * 9.0 + 3.0, flatLight, 0.16);
            vec3 shadowed = vec3(0.09, 0.05, 0.2) * (0.7 + 0.5 * grain) * craters + vec3(0.6, 0.3, 0.95) * cracks * 0.22;
            float lit = smoothstep(-0.05, 0.35, dot(n, light));
            vec3 crescent = vec3(0.78, 0.62, 1.0) * (0.75 + 0.35 * grain) * craters * lit * 0.5;
            float limb = pow(1.0 - n.z, 3.0);
            vec3 rim = mix(vec3(0.55, 0.38, 1.0), vec3(1.0, 0.5, 0.95), lit)
                     * limb * (0.2 + 0.9 * smoothstep(-0.4, 0.5, dot(normalize(q + vec2(1e-4)), flatLight)));
            return shadowed + crescent + rim;
        }

        // The planet's glow: a lilac-magenta corona hugging the disk, stronger on the lit side,
        // and a wide soft halo.
        vec3 moonHalo(vec3 d, vec2 q)
        {
            float a = acos(clamp(dot(d, uMoonDir), -1.0, 1.0));
            float edge = max(a - 0.346, 0.0); // the disk's angular radius, atan(PlanetSize)
            float side = 0.5 + 0.5 * dot(normalize(q + vec2(1e-4)), normalize(PlanetLight.xy));
            return mix(vec3(0.5, 0.4, 1.0), vec3(0.95, 0.4, 0.95), side)
                 * (exp(-edge * 22.0) * (0.15 + 0.35 * side) + exp(-edge * 5.0) * 0.12);
        }

        // A lightning bolt from the clouds down to the horizon at uLightningBolt.x: a jagged path
        // (zigzags at four scales, from the seed) with a blazing core and a soft glow.
        vec3 lightningBolt(vec3 d)
        {
            float da = atan(d.z, d.x) - uLightningBolt.x;
            da = mod(da + 3.14159, 6.28318) - 3.14159;
            if (abs(da) > 0.4 || d.y < 0.0 || d.y > 0.5) return vec3(0.0);
            float path = 0.0, amp = 0.05;
            for (int i = 0; i < 4; i++)
            {
                float k = d.y * 12.0 * exp2(float(i));
                float a0 = hash13(vec3(floor(k), uLightningBolt.y, float(i))) - 0.5;
                float a1 = hash13(vec3(floor(k) + 1.0, uLightningBolt.y, float(i))) - 0.5;
                path += mix(a0, a1, fract(k)) * amp;
                amp *= 0.5;
            }
            float w = abs(da - path);
            return vec3(0.85, 0.85, 1.0) * (exp(-w * 700.0) * 5.0 + exp(-w * 50.0) * 0.35) * uLightning * smoothstep(0.5, 0.38, d.y);
        }

        // Auroras: curtains of light hanging over a few stretches of the horizon at night, their
        // rays rising from a wavering hem, cyan at the foot, then blue, with magenta tips, slowly
        // drifting and flickering.
        vec3 aurora(vec3 d)
        {
            if (uNight < 0.05 || d.y < 0.0) return vec3(0.0);
            float az = atan(d.z, d.x);
            vec2 ring = vec2(cos(az), sin(az)); // noise sampled on a circle: no seam
            float t = uTime * 0.015;
            float sector = smoothstep(0.42, 0.6, texture(uCloudNoise, vec3(ring * 0.6, 0.2 + t * 0.3)).r);
            if (sector <= 0.0) return vec3(0.0);
            float hem = 0.02 + 0.1 * texture(uCloudNoise, vec3(ring * 1.5, 0.6 + t)).r;
            float top = hem + 0.25 + 0.4 * texture(uCloudNoise, vec3(ring * 2.5, 0.8 + t * 0.5)).r;
            float h = clamp((d.y - hem) / (top - hem), 0.0, 1.0);
            if (d.y < hem - 0.02) return vec3(0.0);
            // Broad, soft bands (low frequency, blurred noise) with a gentler flicker over them.
            float rays = smoothstep(0.2, 0.85, textureLod(uCloudNoise, vec3(ring * 4.0, 0.4 + t * 2.0 + h * 0.1), 1.0).r)
                       * (0.7 + 0.3 * textureLod(uCloudNoise, vec3(ring * 10.0, 0.1 + t * 4.0), 1.0).r);
            float profile = smoothstep(-0.05, 0.12, h) * pow(1.0 - h, 1.3);
            vec3 color = mix(vec3(0.05, 1.0, 0.8), vec3(0.25, 0.35, 1.0), smoothstep(0.1, 0.5, h));
            color = mix(color, vec3(1.0, 0.2, 0.85), smoothstep(0.5, 0.95, h));
            return color * rays * profile * sector * uNight * 1.9;
        }

        // The galaxy: a huge spiral seen face-on, like a swirling planet. Five logarithmic arms,
        // wavy and broken into clumps by noise, spiral out of a blazing core and its bulge,
        // with dark lanes of dust between them, cyan clouds among the pink and violet, star dust,
        // a soft glowing spherical rim, and fainter spiral wisps trailing outside it. Everything
        // is blurred (coarse noise mips, broad arms, wide falloffs): dramatic, but never crisp.
        vec3 galaxy(vec3 d)
        {
            // Measured by angle from its centre, so it can span most of the sky: the rim sits
            // 68 degrees out, wider than the view, so it never fits on screen whole.
            float facing = clamp(dot(d, uGalaxyDir), -1.0, 1.0);
            float r = acos(facing) / 1.19;
            if (r > 1.8) return vec3(0.0);
            vec3 t1 = normalize(cross(uGalaxyDir, vec3(0.0, 1.0, 0.0)));
            vec3 t2 = cross(t1, uGalaxyDir);
            vec2 flat2 = vec2(dot(d, t1), dot(d, t2));
            vec2 q = flat2 / max(length(flat2), 1e-5) * r;
            float angle = atan(q.y, q.x);
            float swirl = angle - log(r + 0.12) * 3.0 + uTime * 0.02; // wound looser near the core
            // Noise twisted along the arms breaks them into clumps and streaks and bends them.
            // (Sampled through cos/sin of the swirl, so there is no seam where the polar angle wraps;
            // the arm count is a whole number for the same reason.)
            float streaks = textureLod(uCloudNoise, vec3(cos(swirl) * r * 0.9, sin(swirl) * r * 0.9, 0.37 + r * 0.4), 3.0).r;
            float clouds = textureLod(uCloudNoise, vec3(cos(swirl) * r * 0.6 + 0.5, sin(swirl) * r * 0.6, 0.71 + r * 0.3), 3.5).r;
            // Broad noise over the sky itself (not along the spiral) bends the arms unevenly, so the
            // spiral loses its symmetry.
            float bend = textureLod(uCloudNoise, vec3(q * 0.55 + 0.3, 0.13), 2.5).r;
            float wavy = swirl + (streaks - 0.5) * 2.6 + (bend - 0.5) * 3.0;
            // Each arm has its own strength: the index changes in the dark lanes, where it cannot be
            // seen, and wraps with the angle (five arms, five indices).
            float armIndex = mod(floor((wavy * 5.0 + 3.14159) / 6.28318), 5.0);
            float armStrength = 0.45 + 0.8 * hash13(vec3(armIndex, 11.0, 5.0));
            // The arms are broken into bright clumps, and emerge from the bulge rather than reaching the centre.
            float clump = smoothstep(0.25, 0.75, streaks);
            float arms = pow(max(0.5 + 0.5 * cos(wavy * 5.0), 0.0), 0.9) * (0.1 + 1.35 * clump) * armStrength * smoothstep(0.08, 0.35, r);
            // Fainter spurs branching between the main arms.
            float spurs = pow(max(0.5 + 0.5 * cos(wavy * 5.0 + 2.6 + clouds * 2.0), 0.0), 2.0) * 0.35 * clouds;
            float inside = smoothstep(1.2, 0.6, r);

            vec3 armColor = mix(vec3(0.22, 0.12, 0.8), vec3(1.0, 0.45, 0.95), arms * exp(-r * 1.0));
            armColor = mix(armColor, vec3(0.3, 0.8, 1.0), smoothstep(0.45, 0.8, clouds) * smoothstep(0.2, 0.7, r) * 0.6);
            float glow = arms * 1.7 + spurs;
            // Dust lanes: the gaps between the arms fall dark, softly.
            float lanes = mix(0.2, 1.0, smoothstep(0.0, 0.45, arms + spurs + smoothstep(0.3, 0.05, r)));
            vec3 c = armColor * (0.25 + glow) * exp(-r * 1.5) * inside * lanes;
            c += vec3(1.0, 0.82, 1.0) * (exp(-r * 12.0) * 0.55 + exp(-r * r * 18.0) * 0.3 + exp(-r * 3.5) * 0.12); // core, bulge, halo
            c += vec3(0.55, 0.4, 1.0) * exp(-((r - 1.0) * 3.5) * ((r - 1.0) * 3.5)) * 0.7; // soft rim
            vec2 dust = floor(q * 420.0);
            float sparkle = step(0.97, hash13(vec3(dust, 3.0))) * (0.5 + 0.5 * sin(uTime * 3.0 + hash13(vec3(dust, 9.0)) * 40.0));
            c += vec3(1.0, 0.9, 1.0) * sparkle * (0.2 + arms) * inside * 0.5;
            // Outer wisps continue the arms beyond the rim, fading out.
            float outer = smoothstep(0.9, 1.25, r) * exp(-(r - 1.0) * 1.6);
            c += vec3(0.4, 0.3, 1.0) * pow(max(0.5 + 0.5 * cos(wavy * 5.0 + 0.6), 0.0), 1.5) * (0.5 + streaks) * outer * 0.6;
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

        uniform float uManualStar;   // game time a shooting star was launched by hand (debug), or far in the past
        uniform float uManualSeed;
        uniform vec3 uManualStart;   // where it starts: ahead of the camera, up in the sky

        // How thin a shooting star's trail is (exp(-across^2 * this)); the water's reflection widens it.
        float gStreakSharpness = 3e5;

        // One shooting star, `age` seconds old: it glides slowly along the great circle from `start`
        // around `axis` (40-90 degrees in 3-4.5 s) with a long tail, brightening quickly and fading
        // out slowly over the second half of its flight.
        vec3 streak(vec3 d, float seed, float age, vec3 start, vec3 axis)
        {
            float duration = 3.0 + 1.5 * hash13(vec3(seed, 3.0, 8.0));
            float progress = age / duration;
            if (progress < 0.0 || progress > 1.0) return vec3(0.0);
            vec3 velocity = normalize(cross(start, axis));
            vec3 normal = cross(start, velocity);
            float sweep = (0.7 + 0.9 * hash13(vec3(seed, 5.0, 2.0))) * progress;
            // Measured along the circle itself (a straight tangent would drift off the path).
            float angle = atan(dot(d, velocity), dot(d, start));
            float behind = sweep - angle;
            float across = dot(d, normal);
            float tailLength = 0.3 + 0.25 * hash13(vec3(seed, 1.0, 9.0));
            float tail = pow(smoothstep(tailLength, 0.0, behind), 1.5) * step(-0.002, behind);
            float life = smoothstep(0.0, 0.08, progress) * (1.0 - smoothstep(0.45, 1.0, progress));
            // (A wider trail is a little dimmer.)
            return vec3(1.0, 0.9, 1.0) * tail * exp(-across * across * gStreakSharpness) * life * 4.0 * pow(gStreakSharpness / 3e5, 0.25);
        }

        // Now and then (one slot in two, every 9 s) a shooting star crosses a good part of the sky,
        // plus any launched by hand (kept visible by day too).
        vec3 shootingStar(vec3 d, float visibility)
        {
            const float period = 9.0;
            float slot = floor(uTime / period);
            vec3 c = vec3(0.0);
            if (hash13(vec3(slot, 7.0, 1.0)) > 0.5)
            {
                float a = hash13(vec3(slot, 2.0, 5.0)) * 6.283;
                // From low to high in the sky (low ones are the ones seen mirrored in the lakes).
                vec3 start = normalize(vec3(cos(a), 0.12 + 0.7 * hash13(vec3(slot, 4.0, 4.0)), sin(a)));
                vec3 axis = normalize(vec3(hash13(vec3(slot, 6.0, 1.0)) - 0.5, 1.0, hash13(vec3(slot, 9.0, 3.0)) - 0.5));
                c += streak(d, slot, uTime - slot * period, start, axis) * visibility;
            }
            // Around the vertical: it sweeps sideways across the view (see LaunchShootingStar).
            c += streak(d, uManualSeed, uTime - uManualStar, uManualStart, vec3(0.0, 1.0, 0.0)) * max(visibility, 0.6);
            return c;
        }

        // Stars show wherever the sky is dark enough (c: skyGradient), so at dusk they come out on
        // the side opposite the sun first.
        float starVisibilityOf(vec3 c) { return smoothstep(0.45, 0.08, dot(c, vec3(0.3, 0.5, 0.2))); }

        // Stars, nebulae and the galaxy turn slowly with the sky.
        vec3 skyTurned(vec3 d)
        {
            float ca = cos(uSkyAngle * 0.25), sa = sin(uSkyAngle * 0.25); // uSkyAngle is continuous
            return vec3(ca * d.x + sa * d.z, d.y, -sa * d.x + ca * d.z);
        }

        // The smooth, costly layers of the sky in direction d: the nebulae, the galaxy and the halo
        // round the planet, before its disk and the horizon hide them (c: skyGradient(d)). The
        // water reads them from a small map of the sky (SkyRenderer.DrawLayers) instead of
        // computing them for every pixel it mirrors the sky in.
        vec3 skyLayers(vec3 d, vec3 c)
        {
            vec3 layers = nebula(skyTurned(d)) * starVisibilityOf(c) * uMagic * (1.0 - uPlainNight)
                        + galaxy(d) * mix(0.2, 1.0, uMagic) * (1.0 - uPlainNight);
            float moonAlpha = smoothstep(-0.02, 0.3, d.y) * uNight;
            if (moonAlpha > 0.0)
                layers += moonHalo(d, bodyCoords(d, uMoonDir, PlanetSize, -0.35)) * moonAlpha * mix(0.3, 1.0, uMagic) * (1.0 - uPlainNight);
            return layers;
        }

        vec3 skyWithLayers(vec3 d, vec3 c, vec3 layers);

        // bodies = false leaves out sun, moon and stars (used for fog and sky light).
        vec3 skyColor(vec3 d, bool bodies)
        {
            vec3 c = skyGradient(d);
            if (!bodies) return c;
            return skyWithLayers(d, c, skyLayers(d, c));
        }

        // The sky in direction d with its smooth layers already known (skyLayers): the gradient c,
        // those layers where neither the planet nor the horizon hides them, the stars, the sun,
        // the planet and its small moons, auroras, lightning and shooting stars.
        vec3 skyWithLayers(vec3 d, vec3 c, vec3 layers)
        {
            float up = clamp(d.y, 0.0, 1.0);
            float aboveHorizon = smoothstep(-0.02, 0.03, d.y);
            float starVisibility = starVisibilityOf(c);
            vec3 s = skyTurned(d);
            // The moon is solid: nothing beyond it (nebulae, galaxy, stars) shows through its disk.
            vec2 mq = bodyCoords(d, uMoonDir, PlanetSize, -0.35);
            float moonAlpha = smoothstep(-0.02, 0.3, d.y) * uNight;
            float moon = diskMask(mq, d, uMoonDir, 0.02) * moonAlpha;
            float behind = aboveHorizon * (1.0 - moon);
            c += layers * behind;
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
                    c += vec3(1.8, 1.8, 2.1) * mix(0.5, 1.0, uMagic) * smoothstep(0.45, 0.0, r) * magnitude * twinkle * fade * starVisibility * (1.0 - moon);
                }
            }

            // A small square sun, and the round moon with its halo.
            vec2 sq = bodyCoords(d, uSunDir, 0.045, 0.3);
            c += vec3(5.0, 4.4, 3.6) * diskMask(sq, d, uSunDir, 0.0) * aboveHorizon * (1.0 - uNight);
            if (moon > 0.0 && uPlainNight > 0.5)
            {
                // Only its shadow: a dark disk hiding the stars (with a softly glowing rim, if asked).
                float limb = pow(clamp(dot(mq, mq), 0.0, 1.0), 6.0);
                c = mix(c, c * 0.25 + vec3(0.55, 0.4, 0.9) * limb * 0.45 * uPlanetRim, moon);
            }
            else if (moon > 0.0)
            {
                // A faint veil of the night air over the planet, tinged with the sky.
                vec3 veiled = mix(planetSurface(mq), c * 0.8 + vec3(0.1, 0.08, 0.2), 0.15);
                c = mix(c, c * 0.2 + veiled, moon);
            }
            if (moonAlpha > 0.0 && dot(d, uMoonDir) > 0.5 && uPlainNight < 0.5)
            {
                // Its two small moons, beside it.
                for (int i = 0; i < 2; i++)
                {
                    vec2 center = i == 0 ? vec2(1.3, 0.95) : vec2(0.62, -1.3);
                    float size = i == 0 ? 0.09 : 0.065;
                    vec2 lq = (mq - center) / size;
                    float w = min(fwidth(lq.x) + fwidth(lq.y), 0.5);
                    float m = (1.0 - smoothstep(1.0 - w, 1.0 + w, length(lq))) * moonAlpha;
                    if (m > 0.0) c = mix(c, planetSurface(lq), m);
                }
            }
            // Auroras hang in the air, in front of everything in the sky.
            c += aurora(d) * aboveHorizon * (1.0 - max(uStorm, uBlizzard)) * uMagic * uMagic * (1.0 - uPlainNight);
            if (uLightning > 0.01) c += lightningBolt(d);
            // Shooting stars burn up in the air, far nearer than anything in the sky: they cross
            // in front of the moon, the galaxy and the nebulae (the clouds still hide them).
            c += shootingStar(d, starVisibility) * aboveHorizon;
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

    // The cloud ray march, drawn at half resolution into its own texture (see DrawClouds): the
    // clouds are soft, so a quarter of the samples look the same at a quarter of the cost.
    private const string CloudFragmentSource = "#version 330 core\n" + Glsl + """

        in vec2 vNdc;
        uniform mat4 uInvViewProj; // rotation-only view, so the camera sits at the origin
        uniform vec3 uCameraPos;
        uniform int uCloudSteps;      // ray-march samples (quality)
        uniform int uCloudLightSteps; // samples toward the light per ray-march sample
        uniform sampler2D uSceneDepth; // full-resolution depth of the opaque scene
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
            // At night the clouds are dark masses with magenta-lit edges, from the giant planet.
            vec3 lightColor = moonlit ? vec3(0.62, 0.34, 0.85) * uNight * 1.2 : uSunGlow * 2.1;
            vec3 ambient = (mix(uHorizon, uZenith, 0.55) * 0.75 + uSunHorizon * uHaze * 0.35) * mix(1.0, 0.55, uNight);

            float cosAngle = dot(rd, lightDir);
            float phase = mix(henyeyGreenstein(cosAngle, 0.7), henyeyGreenstein(cosAngle, -0.2), 0.4) * 5.0;

            float transmittance = 1.0;
            vec3 light = vec3(0.0);
            int skip = 0;
            for (int i = 0; i < 64; i++)
            {
                if (i >= uCloudSteps) break;
                // Well clear of any cloud at the last step: step over this one (empty sky costs half).
                if (skip > 0) { skip = 0; t += stepLength; continue; }
                vec3 p = ro + rd * t;
                float threshold = cloudThreshold(p, lod);
                float raw = cloudDensityRaw(p, true, lod, threshold);
                float density = clamp(raw, 0.0, 1.0);
                if (raw < -1.2) skip = 1;
                if (density > 0.001)
                {
                    float depth = 0.0;
                    float lightStep = 135.0 / float(uCloudLightSteps);
                    for (int j = 1; j <= 4; j++)
                    {
                        if (j > uCloudLightSteps) break;
                        depth += clamp(cloudDensityRaw(p + lightDir * (float(j) * lightStep), false, lod + 1.0, threshold), 0.0, 1.0) * lightStep;
                    }
                    float toLight = exp(-depth * 0.04); // deep, dark cores; bright sunlit rims
                    float powder = 1.0 - exp(-density * 5.0);
                    float height = (p.y - CloudBottom) / (CloudTop - CloudBottom);
                    vec3 scattered = lightColor * toLight * phase * powder + ambient * (0.28 + 0.72 * height); // dark bellies
                    // The galaxy's glow tinges the cloud tops at night.
                    scattered += vec3(0.3, 0.18, 0.6) * uGalaxyGlow * uNight * 0.35 * height;
                    // Lightning flashes inside the clouds.
                    scattered += vec3(0.7, 0.75, 1.0) * uLightning * (0.6 + 0.8 * powder);
                    float stepTransmittance = exp(-density * 0.025 * stepLength);
                    light += transmittance * scattered * (1.0 - stepTransmittance);
                    transmittance *= stepTransmittance;
                    if (transmittance < 0.02) break;
                }
                t += stepLength;
            }

            // A blizzard hides the clouds in pale blowing snow.
            light = mix(light, vec3(0.74, 0.78, 0.86) * mix(1.0, 0.18, uNight) * (1.0 - transmittance), uBlizzard * 0.7);
            // Far clouds melt into the sky.
            float fade = exp(-t0 * 0.00016);
            return vec4(light * fade, mix(1.0, transmittance, fade));
        }

        // True when a full-resolution pixel whose bilinear lookup reaches this texel shows the sky
        // (a 4x4 footprint of the scene depth; the sky is where the depth buffer is still clear).
        bool skyNearby()
        {
            ivec2 size = textureSize(uSceneDepth, 0) - 1;
            ivec2 base = ivec2(gl_FragCoord.xy) * 2;
            for (int y = -1; y <= 2; y++)
            for (int x = -1; x <= 2; x++)
                if (texelFetch(uSceneDepth, clamp(base + ivec2(x, y), ivec2(0), size), 0).r >= 1.0) return true;
            return false;
        }

        void main()
        {
            if (!skyNearby()) { FragColor = vec4(0.0, 0.0, 0.0, 1.0); return; }
            vec4 far = uInvViewProj * vec4(vNdc, 1.0, 1.0);
            FragColor = marchClouds(uCameraPos, normalize(far.xyz / far.w));
        }
        """;

    private const string FragmentSource = "#version 330 core\n" + Glsl + """

        in vec2 vNdc;
        uniform mat4 uInvViewProj; // rotation-only view, so the camera sits at the origin
        uniform float uUnderwater;
        uniform vec3 uAmbient;
        uniform sampler2D uClouds; // light scattered by the clouds (rgb) and transmittance (a), half resolution
        out vec4 FragColor;

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
            vec4 clouds = texture(uClouds, vNdc * 0.5 + 0.5);
            FragColor = vec4(skyColor(d, true) * clouds.a + clouds.rgb, 1.0);
        }
        """;

    // The sky's smooth layers (skyLayers: nebulae, galaxy, the planet's halo) over the upper half
    // of the sky, for the water to mirror (LayersGlsl): azimuth across, elevation up, a texel
    // about a third of a degree. 260 thousand sky directions a frame instead of one for every
    // pixel of water (millions, looking out over a lake): the galaxy cost more than the rest of
    // the water together. The layers are soft, so the map loses nothing seen rippled in the water.
    private const string LayersFragmentSource = "#version 330 core\n" + Glsl + """

        uniform vec2 uLayersSize;
        out vec4 FragColor;

        void main()
        {
            vec2 uv = gl_FragCoord.xy / uLayersSize;
            float azimuth = uv.x * 6.2831853 - 3.1415927, elevation = uv.y * 1.5707963;
            vec3 d = vec3(cos(elevation) * cos(azimuth), sin(elevation), cos(elevation) * sin(azimuth));
            FragColor = vec4(skyLayers(d, skyGradient(d)), 1.0);
        }
        """;

    /// <summary>
    /// <c>mirroredSky(dir)</c>: <c>skyColor(dir, true)</c> for the upper half of the sky, its smooth
    /// layers read from the map <see cref="DrawLayers"/> drew (bound to <see cref="LayersUnit"/>,
    /// <c>uSkyLayers</c>). Paste after <see cref="Glsl"/>.
    /// </summary>
    public const string LayersGlsl = """
        uniform sampler2D uSkyLayers;

        vec3 mirroredSky(vec3 d)
        {
            vec2 uv = vec2(atan(d.z, d.x) / 6.2831853 + 0.5, asin(clamp(d.y, 0.0, 1.0)) / 1.5707963);
            return skyWithLayers(d, skyGradient(d), textureLod(uSkyLayers, uv, 0.0).rgb);
        }
        """;

    /// <summary>Texture unit the sky's layers (<see cref="DrawLayers"/>) are bound to.</summary>
    public const int LayersUnit = 9;
    private const int LayersWidth = 1024, LayersHeight = 256;

    private readonly GL _gl;
    private readonly Shader _shader, _cloudShader, _layersShader;
    private readonly uint _vao; // core profile needs a bound VAO even with no attributes
    private uint _cloudFbo, _cloudTexture, _layersFbo, _layersTexture;
    private int _cloudWidth, _cloudHeight;

    public SkyRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _cloudShader = new Shader(gl, VertexSource, CloudFragmentSource);
        _layersShader = new Shader(gl, VertexSource, LayersFragmentSource);
        _vao = gl.GenVertexArray();
    }

    /// <summary>
    /// Draws the map of the sky's smooth layers the water mirrors (<see cref="LayersGlsl"/>) and
    /// binds it to <see cref="LayersUnit"/>. Leaves its framebuffer bound: the caller binds the scene again.
    /// </summary>
    public unsafe void DrawLayers(in Atmosphere atmosphere, float time, float cloudTime)
    {
        if (_layersFbo == 0)
        {
            _layersTexture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _layersTexture);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, LayersWidth, LayersHeight, 0, PixelFormat.Rgba, PixelType.HalfFloat, (void*)0);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat); // round the horizon
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _layersFbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _layersFbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _layersTexture, 0);
        }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _layersFbo);
        _gl.Viewport(0, 0, LayersWidth, LayersHeight);
        _layersShader.Use();
        SetUniforms(_layersShader, atmosphere, time, cloudTime);
        _layersShader.Set("uLayersSize", new Vector2(LayersWidth, LayersHeight));
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(false);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
        _gl.ActiveTexture(TextureUnit.Texture0 + LayersUnit);
        _gl.BindTexture(TextureTarget.Texture2D, _layersTexture);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    /// <summary>Texture unit the half-resolution clouds are bound to while drawing the sky.</summary>
    public const int CloudsUnit = 5;

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
        shader.Set("uRain", Rain);
        shader.Set("uSnow", Snow);
        shader.Set("uSnowCover", SnowCover);
        shader.Set("uMagic", WorldPreset.Current.Magic);
        shader.Set("uClearSky", WorldPreset.Current.ClearSky);
        shader.Set("uPlainNight", WorldPreset.Current.PlainNight ? 1f : 0f);
        shader.Set("uPlanetRim", WorldPreset.Current.PlanetRim ? 1f : 0f);
        shader.Set("uStorm", Storm);
        shader.Set("uBlizzard", Blizzard);
        shader.Set("uLightning", Lightning);
        shader.Set("uLightningBolt", LightningBolt);
        shader.Set("uManualStar", _manualStar.Time);
        shader.Set("uManualSeed", _manualStar.Seed);
        shader.Set("uManualStart", _manualStar.Start);
    }

    /// <summary>How hard it is raining (0..1, see <see cref="Weather"/>), sent with the sky uniforms.</summary>
    public static float Rain { get; set; }

    /// <summary>How hard it is snowing (0..1) and how much snow lies on the world (0..1), see <see cref="Weather"/>.</summary>
    public static float Snow { get; set; }

    public static float SnowCover { get; set; }

    /// <summary>Storm and blizzard strength, the lightning flash and its bolt (azimuth, seed), see <see cref="Weather"/>.</summary>
    public static float Storm { get; set; }

    public static float Blizzard { get; set; }

    public static float Lightning { get; set; }

    public static Vector2 LightningBolt { get; set; }

    private static (float Time, float Seed, Vector3 Start) _manualStar = (-1e4f, 0f, Vector3.UnitY);

    /// <summary>
    /// Launches a shooting star now (debug), starting up in the sky off to one side of
    /// <paramref name="forward"/> and sweeping around the vertical, so it crosses the view.
    /// </summary>
    public static void LaunchShootingStar(float time, Vector3 forward)
    {
        var flat = new Vector2(forward.X, forward.Z);
        flat = flat.LengthSquared() > 1e-6f ? Vector2.Normalize(flat) : Vector2.UnitX;
        float a = MathF.Atan2(flat.Y, flat.X) - 0.5f;
        var start = Vector3.Normalize(new Vector3(MathF.Cos(a), 0.6f, MathF.Sin(a)));
        _manualStar = (time, _manualStar.Seed + 1f, start);
    }

    /// <summary>
    /// Ray-marches the clouds at half the scene resolution into their own texture, only near the
    /// pixels where the sky shows (<paramref name="depthUnit"/> holds the opaque scene's depth).
    /// Leaves the cloud framebuffer bound: the caller binds the scene again.
    /// </summary>
    public unsafe void DrawClouds(Matrix4x4 inverseViewProjection, Vector3 cameraPosition, float cloudTime, in Atmosphere atmosphere,
        float time, int depthUnit, int sceneWidth, int sceneHeight)
    {
        int width = Math.Max(1, (sceneWidth + 1) / 2), height = Math.Max(1, (sceneHeight + 1) / 2);
        if (width != _cloudWidth || height != _cloudHeight)
        {
            DeleteCloudTarget();
            (_cloudWidth, _cloudHeight) = (width, height);
            _cloudTexture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _cloudTexture);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.HalfFloat, (void*)0);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _cloudFbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _cloudFbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _cloudTexture, 0);
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _cloudFbo);
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _cloudShader.Use();
        _cloudShader.Set("uCloudSteps", 32);
        _cloudShader.Set("uCloudLightSteps", 3);
        _cloudShader.Set("uInvViewProj", inverseViewProjection);
        _cloudShader.Set("uCameraPos", cameraPosition);
        _cloudShader.Set("uSceneDepth", depthUnit);
        SetUniforms(_cloudShader, atmosphere, time, cloudTime);
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
    }

    private void DeleteCloudTarget()
    {
        if (_cloudFbo == 0) return;
        _gl.DeleteFramebuffer(_cloudFbo);
        _gl.DeleteTexture(_cloudTexture);
        _cloudFbo = _cloudTexture = 0;
    }

    /// <summary>Draws the sky over the pixels nothing covers, with the clouds from <see cref="DrawClouds"/>.</summary>
    public void Draw(Matrix4x4 inverseViewProjection, Vector3 cameraPosition, float cloudTime, in Atmosphere atmosphere, float time)
    {
        _shader.Use();
        _shader.Set("uInvViewProj", inverseViewProjection);
        _shader.Set("uUnderwater", cameraPosition.Y < Mine.World.TerrainField.WaterLevel ? 1f : 0f);
        _shader.Set("uAmbient", atmosphere.Ambient);
        _shader.Set("uClouds", CloudsUnit);
        _gl.ActiveTexture(TextureUnit.Texture0 + CloudsUnit);
        _gl.BindTexture(TextureTarget.Texture2D, _cloudTexture);
        _gl.ActiveTexture(TextureUnit.Texture0);
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
        DeleteCloudTarget();
        if (_layersFbo != 0)
        {
            _gl.DeleteFramebuffer(_layersFbo);
            _gl.DeleteTexture(_layersTexture);
        }
        _layersShader.Dispose();
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
        _cloudShader.Dispose();
    }
}
