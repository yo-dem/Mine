using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// The islands' waterfalls, thin rivulets of glowing silvery water: a trickle across the island's
/// top, a few threads of water arcing over the edge and falling straight to the ground (drawn at
/// least about a pixel wide however far away, so they never flicker out), a little spray where they
/// land, and the pond they fill, lit by ripples spreading from the fall. The water visibly runs:
/// bright pulses race down the threads over darker water between them. Where the falls near the
/// player land, splashes of droplets leap and fall back (point sprites with no vertex data, each
/// deriving its arc from its index and the time, like the motes). All translucent and self-lit, blended
/// with premultiplied alpha (so they can both darken what is behind, like water, and glow), drawn
/// after the opaque scene and the sea without writing depth. The mesh is rebuilt on the CPU when
/// the set of islands changes; it is small (a few falls at a time).
/// </summary>
public sealed unsafe class WaterfallRenderer : IDisposable
{
    // Vertex: position (3), u, v, kind, extra (see the fragment shader for what each kind uses them
    // for), then the centre of the thread's cross-section (3) and its half-width (0 = never widened).
    private const int FloatsPerVertex = 11;
    private const float Stream = 0, Sheet = 1, PondSurface = 2, Mist = 3;

    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUv;
        layout(location = 2) in vec2 aKind; // kind, extra
        layout(location = 3) in vec4 aAxis; // centre of the cross-section, half-width

        uniform mat4 uViewProj;
        uniform vec3 uCameraPos;
        uniform float uPixelAngle; // radians per pixel

        out vec3 vWorldPos;
        out vec2 vUv;
        flat out int vKind;
        out float vExtra;
        out float vThin; // 1, or less where a far thread was widened (and dimmed to match)

        void main()
        {
            vec3 p = aPos;
            vThin = 1.0;
            if (aAxis.w > 0.0)
            {
                // Keep threads at least ~1.5 pixels wide: widen them far away, spreading their light.
                float minHalf = length(aAxis.xyz - uCameraPos) * uPixelAngle * 0.75;
                float s = max(1.0, minHalf / aAxis.w);
                p = aAxis.xyz + (aPos - aAxis.xyz) * s;
                vThin = 1.0 / s;
            }
            vWorldPos = p;
            vUv = aUv;
            vKind = int(aKind.x + 0.5);
            vExtra = aKind.y;
            gl_Position = uViewProj * vec4(p, 1.0);
        }
        """;

    private const string FragmentSource = "#version 330 core\n" + SkyRenderer.Hash + """
        in vec3 vWorldPos;
        in vec2 vUv;
        flat in int vKind;
        in float vExtra;
        in float vThin;

        uniform vec3 uCameraPos;
        uniform float uTime;
        uniform float uNight;
        uniform float uFadeEnd; // fully faded out at this distance (the edge of the view)

        out vec4 FragColor;

        float vnoise(vec2 p)
        {
            vec2 i = floor(p), f = fract(p);
            f = f * f * (3.0 - 2.0 * f);
            float a = hash13(vec3(i, 1.0)), b = hash13(vec3(i + vec2(1.0, 0.0), 1.0));
            float c = hash13(vec3(i + vec2(0.0, 1.0), 1.0)), d = hash13(vec3(i + vec2(1.0, 1.0), 1.0));
            return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
        }

        const vec3 Silver = vec3(0.82, 0.9, 1.0);
        const vec3 Cyan = vec3(0.45, 0.9, 1.0);
        const vec3 Deep = vec3(0.03, 0.08, 0.14);

        void main()
        {
            float t = uTime;
            vec3 glow;
            float alpha;
            if (vKind == 0)
            {
                // The stream on the island: u across (0..1), v metres downstream.
                // Ripples racing downstream: bright crests over darker water.
                float wob = vnoise(vec2(vUv.x * 3.0, vUv.y * 0.3));
                float crest = pow(fract(vUv.y * 0.9 - t * 1.6 + wob * 1.5), 4.0);
                float fine = smoothstep(0.55, 0.85, vnoise(vec2(vUv.x * 7.0, vUv.y * 2.0 - t * 4.0)));
                float flow = clamp(crest * 0.9 + fine * 0.6, 0.0, 1.0);
                float banks = smoothstep(0.0, 0.3, vUv.x) * smoothstep(1.0, 0.7, vUv.x);
                alpha = (0.55 + 0.3 * flow) * banks;
                glow = mix(Cyan, Silver, 0.5 + 0.5 * flow) * (0.08 + 1.2 * flow) * banks * mix(2.0, 1.0, vThin);
            }
            else if (vKind == 1)
            {
                // A falling thread: u around it (0..1), v metres fallen, extra = the whole drop.
                // Silvery, with brighter beads of light racing down it.
                // Pulses of light racing down (sawtooth bands bent by noise) over darker water, plus
                // fine fast streaks: the eye reads it as liquid pouring. Far away, where a thread is
                // only a pixel or two wide, the pattern evens out into a steady line (no flicker).
                float wob = vnoise(vec2(vUv.x * 4.0, vUv.y * 0.04));
                float pulse = pow(fract(vUv.y * 0.11 - t * 2.2 + wob * 2.0), 5.0);
                float fine = smoothstep(0.55, 0.85, vnoise(vec2(vUv.x * 12.0, vUv.y * 0.5 - t * 7.0)));
                float flow = clamp(pulse + fine * 0.55, 0.0, 1.0);
                flow = mix(0.45, flow, smoothstep(0.3, 0.8, vThin));
                float ends = smoothstep(0.0, 1.5, vUv.y) * (0.5 + 0.5 * smoothstep(vExtra, vExtra - 20.0, vUv.y));
                alpha = (0.5 + 0.35 * flow) * ends;
                glow = Silver * (0.05 + 1.0 * flow) * ends * mix(2.2, 1.0, vThin);
            }
            else if (vKind == 2)
            {
                // The pond: uv = offset from where the water lands (metres), extra = radius.
                float r = length(vUv);
                float rf = r / vExtra;
                float rings = pow(0.5 + 0.5 * sin(r * 2.4 - t * 3.0), 6.0) * exp(-rf * 2.2);
                float veins = smoothstep(0.55, 0.8, vnoise(vUv * 0.6 + vec2(t * 0.15, -t * 0.1)));
                float foam = smoothstep(1.4, 0.0, r) * (0.3 + 0.7 * vnoise(vUv * 4.0 + t * 3.0)) * 0.6;
                float shore = smoothstep(0.7, 1.0, rf) * (0.6 + 0.4 * sin(t * 1.3 + atan(vUv.y, vUv.x) * 5.0));
                alpha = 0.85;
                glow = mix(Cyan, Silver, 0.5) * (0.25 + 1.1 * rings + 0.5 * veins * (1.0 - rf * 0.5) + 0.7 * shore) + Silver * foam;
            }
            else
            {
                // Mist and spray where the water lands: u around, v = 0 at the water up to 1.
                float swirl = vnoise(vec2(vUv.x * 12.0 + t * 0.4, vUv.y * 3.0 - t * 0.9));
                float fade = (1.0 - vUv.y) * (1.0 - vUv.y);
                alpha = 0.2 * swirl * fade;
                glow = Silver * swirl * fade * 0.4;
            }

            float dist = length(vWorldPos - uCameraPos);
            float fade = 1.0 - smoothstep(uFadeEnd * 0.6, uFadeEnd, dist);
            glow *= mix(0.9, 1.15, uNight);
            // Premultiplied: the water's own colour covers what is behind by alpha, the glow adds.
            FragColor = vec4((Deep * alpha + glow) * fade, alpha * fade);
        }
        """;

    // Splashes: droplets per fall, for the MaxSplashes falls nearest the player within SplashRange.
    private const int DropsPerFall = 360, MaxSplashes = 8;
    private const float SplashRange = 260f;

    private const string SplashVertexSource = "#version 330 core\n" + SkyRenderer.Hash + """
        uniform mat4 uViewProj;
        uniform vec3 uCameraPos;
        uniform float uTime;
        uniform float uPointScale; // viewport height / (2 tan(fov / 2))
        uniform vec4 uFeet[8];     // where each fall lands (xyz) and how wide it is (w)

        const int DropsPerFall = 360;

        out float vAlpha;
        out float vSoft;

        void main()
        {
            int fall = gl_VertexID / DropsPerFall, i = gl_VertexID % DropsPerFall;
            vec4 foot = uFeet[fall];
            float h1 = hash13(vec3(i, fall, 1.0)), h2 = hash13(vec3(i, fall, 2.0)), h3 = hash13(vec3(i, fall, 3.0));
            float h4 = hash13(vec3(i, fall, 4.0)), h5 = hash13(vec3(i, fall, 5.0));
            float strength = 0.6 + foot.w;
            // One in five is a slow puff of spray; the rest are droplets thrown up and out.
            bool puff = h5 < 0.2;
            float life = puff ? 1.2 + 1.0 * h1 : 0.45 + 0.6 * h1;
            float age = fract(uTime / life + h2) * life;
            float angle = h3 * 6.2832;
            vec3 out3 = vec3(cos(angle), 0.0, sin(angle));
            float speed = (puff ? 0.4 + 0.6 * h4 : 0.8 + 3.2 * h4 * h4) * strength;
            float rise = (puff ? 0.8 + 0.8 * h1 : 3.0 + 5.0 * h4) * strength;
            float gravity = puff ? 0.6 : 9.8;
            vec3 start = foot.xyz + out3 * foot.w * 0.6 * h2 + vec3(0.0, 0.05, 0.0);
            vec3 p = start + out3 * speed * age + vec3(0.0, rise * age - 0.5 * gravity * age * age, 0.0);

            float fade = 1.0 - age / life;
            vAlpha = (puff ? 0.05 * fade : 1.6 * fade * fade) * step(foot.y, p.y);
            vSoft = puff ? 1.0 : 0.0;
            float dist = length(p - uCameraPos);
            float size = (puff ? 0.7 + 0.8 * age : 0.12 + 0.12 * h1) * uPointScale / max(dist, 0.1);
            gl_PointSize = clamp(size, 1.0, puff ? 50.0 : 14.0);
            vAlpha *= min(size, 1.0) * (1.0 - smoothstep(150.0, 260.0, dist)); // sub-pixel: fainter
            gl_Position = uViewProj * vec4(p, 1.0);
        }
        """;

    private const string SplashFragmentSource = """
        #version 330 core
        in float vAlpha;
        in float vSoft;
        uniform float uNight;
        out vec4 FragColor;
        void main()
        {
            float d = length(gl_PointCoord - 0.5) * 2.0;
            float shape = vSoft > 0.5 ? (1.0 - d) * (1.0 - d) : smoothstep(1.0, 0.4, d);
            if (shape <= 0.0) discard;
            vec3 color = vec3(0.85, 0.93, 1.0) * mix(1.2, 1.8, uNight);
            FragColor = vec4(color * shape * vAlpha, 0.0);
        }
        """;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly Shader _splashShader;
    private readonly uint _vao, _vbo, _splashVao;
    private readonly List<Vector4> _feet = new();
    private readonly List<float> _mesh = new();
    private readonly List<PointLight> _lights = new();
    private readonly List<Waterfall> _falls = new();
    private int _vertexCount;
    private int _version = -1;

    public WaterfallRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _splashShader = new Shader(gl, SplashVertexSource, SplashFragmentSource);
        _splashVao = gl.GenVertexArray(); // core profile needs a bound VAO even with no attributes
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        uint stride = FloatsPerVertex * sizeof(float);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(5 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, (void*)(7 * sizeof(float)));
        gl.EnableVertexAttribArray(3);
        gl.BindVertexArray(0);
    }

    /// <summary>Rebuilds the mesh when the islands have changed.</summary>
    public void Update(IslandField islands)
    {
        if (islands.Version == _version) return;
        _version = islands.Version;
        _mesh.Clear();
        _falls.Clear();
        foreach (var island in islands.All)
        foreach (var fall in island.Falls)
        {
            _falls.Add(fall);
            BuildStream(island, fall);
            BuildSheet(fall);
            if (fall.PondRadius > 0) BuildPond(fall);
            BuildMist(fall);
        }

        _vertexCount = _mesh.Count / FloatsPerVertex;
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        var data = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_mesh);
        fixed (float* p = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
    }

    /// <summary>A soft light over each pond (or where the water lands) near <paramref name="center"/>.</summary>
    public List<PointLight> Lights(Vector3 center, float radius = 120f)
    {
        _lights.Clear();
        foreach (var fall in _falls)
        {
            var foot = FootOf(fall);
            if (Vector3.DistanceSquared(foot, center) > radius * radius) continue;
            _lights.Add(new PointLight(foot + new Vector3(0, 1.5f, 0), new Vector3(0.6f, 0.85f, 1.0f) * 1.6f, fall.PondRadius * 1.5f + 6f));
        }
        return _lights;
    }

    /// <param name="pixelAngle">The angle one pixel of the scene covers, in radians.</param>
    public void Draw(Matrix4x4 viewProjection, Vector3 camera, float night, float time, float pixelAngle)
    {
        DrawFalls(viewProjection, camera, night, time, pixelAngle);
        DrawSplashes(viewProjection, camera, night, time, 1f / pixelAngle);
    }

    private void DrawSplashes(Matrix4x4 viewProjection, Vector3 camera, float night, float time, float pointScale)
    {
        _feet.Clear();
        foreach (var fall in _falls)
        {
            var foot = FootOf(fall);
            if (Vector3.DistanceSquared(foot, camera) < SplashRange * SplashRange) _feet.Add(new Vector4(foot, fall.Width));
        }
        if (_feet.Count == 0) return;
        _feet.Sort((a, b) => Vector3.DistanceSquared(new(a.X, a.Y, a.Z), camera).CompareTo(Vector3.DistanceSquared(new(b.X, b.Y, b.Z), camera)));
        int count = Math.Min(_feet.Count, MaxSplashes);

        _splashShader.Use();
        _splashShader.Set("uViewProj", viewProjection);
        _splashShader.Set("uCameraPos", camera);
        _splashShader.Set("uTime", time);
        _splashShader.Set("uNight", night);
        _splashShader.Set("uPointScale", pointScale);
        for (int i = 0; i < count; i++)
            _splashShader.Set($"uFeet[{i}]", _feet[i]);

        _gl.Enable(EnableCap.ProgramPointSize);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        _gl.DepthMask(false);
        _gl.BindVertexArray(_splashVao);
        _gl.DrawArrays(PrimitiveType.Points, 0, (uint)(count * DropsPerFall));
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.ProgramPointSize);
    }

    private void DrawFalls(Matrix4x4 viewProjection, Vector3 camera, float night, float time, float pixelAngle)
    {
        if (_vertexCount == 0) return;
        _shader.Use();
        _shader.Set("uViewProj", viewProjection);
        _shader.Set("uCameraPos", camera);
        _shader.Set("uPixelAngle", pixelAngle);
        _shader.Set("uTime", time);
        _shader.Set("uNight", night);
        _shader.Set("uFadeEnd", IslandField.Radius);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_vertexCount);
        _gl.Enable(EnableCap.CullFace);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
    }

    /// <summary>
    /// How far out from the lip the water is after falling <paramref name="drop"/> metres: it leaves
    /// the edge moving outward and soon falls straight, <see cref="IslandField.FallReach"/> out.
    /// </summary>
    private static float Reach(float drop) => 0.3f + (IslandField.FallReach - 0.3f) * (1f - MathF.Exp(-drop / 5f));

    private static Vector3 FootOf(Waterfall fall)
    {
        var foot = fall.Lip + fall.Direction * IslandField.FallReach;
        foot.Y = fall.Bottom;
        return foot;
    }

    private void BuildStream(Island island, Waterfall fall)
    {
        // From a spring part way to the centre out to the lip, lying on the dome.
        const int Steps = 14;
        var across = new Vector3(-fall.Direction.Z, 0, fall.Direction.X);
        float angle = MathF.Atan2(fall.Direction.Z, fall.Direction.X);
        float edge = IslandField.EdgeRadius(island, angle);
        Vector3 Point(int k, out float width)
        {
            float f = 0.3f + 0.7f * k / Steps;
            var p = island.Center + fall.Direction * edge * f;
            p.Y = IslandField.TopHeight(island, f) + 0.15f;
            width = fall.Width * (0.8f + 0.6f * f);
            return p;
        }
        float v = 0;
        for (int k = 0; k < Steps; k++)
        {
            var a = Point(k, out float wa);
            var b = Point(k + 1, out float wb);
            float vb = v + Vector3.Distance(a, b);
            Quad(a - across * wa * 0.5f, a + across * wa * 0.5f, b + across * wb * 0.5f, b - across * wb * 0.5f,
                new(0, v), new(1, v), new(1, vb), new(0, vb), Stream, 0, a, wa * 0.5f, b, wb * 0.5f);
            v = vb;
        }
    }

    private void BuildSheet(Waterfall fall)
    {
        // A main thread and two finer ones that part from it as they fall.
        float total = fall.Lip.Y - fall.Bottom;
        if (total <= 1f) return;
        var across = new Vector3(-fall.Direction.Z, 0, fall.Direction.X);
        BuildThread(fall, total, across, 0f, fall.Width * 0.5f);
        BuildThread(fall, total, across, 1f, fall.Width * 0.25f);
        BuildThread(fall, total, across, -0.7f, fall.Width * 0.2f);
    }

    /// <summary>One thin round thread following the arc, <paramref name="side"/> metres across at the lip, spreading out.</summary>
    private void BuildThread(Waterfall fall, float total, Vector3 across, float side, float radius)
    {
        const int Rings = 40, Sides = 6;
        var rings = new Vector3[Rings + 1, Sides + 1];
        var centers = new Vector3[Rings + 1];
        var drops = new float[Rings + 1];
        for (int k = 0; k <= Rings; k++)
        {
            // Rings crowd near the top, where the arc bends.
            float drop = total * MathF.Pow(k / (float)Rings, 1.8f);
            drops[k] = drop;
            float spread = side * fall.Width * (0.6f + MathF.Min(drop, 60f) * 0.02f);
            var center = fall.Lip + fall.Direction * Reach(drop) + across * spread - new Vector3(0, drop, 0);
            centers[k] = center;
            for (int s = 0; s <= Sides; s++)
            {
                float a = s * MathF.Tau / Sides;
                rings[k, s] = center + (across * MathF.Cos(a) + fall.Direction * MathF.Sin(a)) * radius;
            }
        }
        for (int k = 0; k < Rings; k++)
        for (int s = 0; s < Sides; s++)
        {
            float u0 = s / (float)Sides, u1 = (s + 1) / (float)Sides;
            Quad(rings[k, s], rings[k, s + 1], rings[k + 1, s + 1], rings[k + 1, s],
                new(u0, drops[k]), new(u1, drops[k]), new(u1, drops[k + 1]), new(u0, drops[k + 1]), Sheet, total,
                centers[k], radius, centers[k + 1], radius);
        }
    }

    private void BuildPond(Waterfall fall)
    {
        // A disc a little wider than the basin: the bank, higher than the water, hides the rest.
        const int Segments = 36;
        var center = FootOf(fall);
        float radius = fall.PondRadius + 1.5f;
        for (int s = 0; s < Segments; s++)
        {
            float a0 = s * MathF.Tau / Segments, a1 = (s + 1) * MathF.Tau / Segments;
            var o0 = new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * radius;
            var o1 = new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius;
            Vertex(center, Vector2.Zero, PondSurface, fall.PondRadius);
            Vertex(center + new Vector3(o0.X, 0, o0.Y), o0, PondSurface, fall.PondRadius);
            Vertex(center + new Vector3(o1.X, 0, o1.Y), o1, PondSurface, fall.PondRadius);
        }
    }

    private void BuildMist(Waterfall fall)
    {
        // A wide, low column of spray round the foot of the fall.
        const int Sides = 16;
        var center = FootOf(fall) - new Vector3(0, 0.3f, 0);
        float bottom = 0.6f + fall.Width, top = 1.2f + 2f * fall.Width, height = 2f + 3f * fall.Width;
        for (int s = 0; s < Sides; s++)
        {
            float a0 = s * MathF.Tau / Sides, a1 = (s + 1) * MathF.Tau / Sides;
            var d0 = new Vector3(MathF.Cos(a0), 0, MathF.Sin(a0));
            var d1 = new Vector3(MathF.Cos(a1), 0, MathF.Sin(a1));
            float u0 = s / (float)Sides, u1 = (s + 1) / (float)Sides;
            Quad(center + d0 * bottom, center + d1 * bottom, center + d1 * top + new Vector3(0, height, 0), center + d0 * top + new Vector3(0, height, 0),
                new(u0, 0), new(u1, 0), new(u1, 1), new(u0, 1), Mist, 0);
        }
    }

    /// <summary>
    /// Two triangles. For threads, a and b lie on the cross-section centred at <paramref name="axisAB"/>,
    /// c and d on the one centred at <paramref name="axisCD"/> (half-widths given; 0 = never widened).
    /// </summary>
    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, float kind, float extra,
        Vector3 axisAB = default, float halfAB = 0, Vector3 axisCD = default, float halfCD = 0)
    {
        Vertex(a, ua, kind, extra, axisAB, halfAB); Vertex(b, ub, kind, extra, axisAB, halfAB); Vertex(c, uc, kind, extra, axisCD, halfCD);
        Vertex(a, ua, kind, extra, axisAB, halfAB); Vertex(c, uc, kind, extra, axisCD, halfCD); Vertex(d, ud, kind, extra, axisCD, halfCD);
    }

    private void Vertex(Vector3 p, Vector2 uv, float kind, float extra, Vector3 axis = default, float half = 0)
    {
        _mesh.Add(p.X); _mesh.Add(p.Y); _mesh.Add(p.Z);
        _mesh.Add(uv.X); _mesh.Add(uv.Y);
        _mesh.Add(kind); _mesh.Add(extra);
        _mesh.Add(axis.X); _mesh.Add(axis.Y); _mesh.Add(axis.Z); _mesh.Add(half);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteVertexArray(_splashVao);
        _shader.Dispose();
        _splashShader.Dispose();
    }
}
