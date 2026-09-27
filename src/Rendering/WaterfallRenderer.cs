using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// The islands' waterfalls, of glowing water: a stream across the island's top, a sheet of water
/// arcing over the edge and falling straight to the ground, foam and mist where it lands, and the
/// pond it fills, lit by ripples spreading from the fall. All translucent and self-lit, blended
/// with premultiplied alpha (so they can both darken what is behind, like water, and glow), drawn
/// after the opaque scene and the sea without writing depth. The mesh is rebuilt on the CPU when
/// the set of islands changes; it is small (a few falls at a time).
/// </summary>
public sealed unsafe class WaterfallRenderer : IDisposable
{
    // Vertex: position (3), u, v, kind, extra (see the fragment shader for what each kind uses them for).
    private const int FloatsPerVertex = 7;
    private const float Stream = 0, Sheet = 1, PondSurface = 2, Mist = 3;

    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUv;
        layout(location = 2) in vec2 aKind; // kind, extra

        uniform mat4 uViewProj;

        out vec3 vWorldPos;
        out vec2 vUv;
        flat out int vKind;
        out float vExtra;

        void main()
        {
            vWorldPos = aPos;
            vUv = aUv;
            vKind = int(aKind.x + 0.5);
            vExtra = aKind.y;
            gl_Position = uViewProj * vec4(aPos, 1.0);
        }
        """;

    private const string FragmentSource = "#version 330 core\n" + SkyRenderer.Hash + """
        in vec3 vWorldPos;
        in vec2 vUv;
        flat in int vKind;
        in float vExtra;

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

        const vec3 Cyan = vec3(0.25, 0.95, 1.0);
        const vec3 Lilac = vec3(0.7, 0.55, 1.0);
        const vec3 Deep = vec3(0.02, 0.10, 0.18);

        void main()
        {
            float t = uTime;
            vec3 glow;
            float alpha;
            if (vKind == 0)
            {
                // The stream on the island: u across (0..1), v metres downstream.
                float flow = vnoise(vec2(vUv.x * 4.0, vUv.y * 0.5 - t * 1.8)) * 0.6 + vnoise(vec2(vUv.x * 9.0, vUv.y * 1.3 - t * 2.6)) * 0.4;
                float banks = smoothstep(0.0, 0.25, vUv.x) * smoothstep(1.0, 0.75, vUv.x);
                alpha = 0.75 * banks;
                glow = mix(Cyan, vec3(1.0), flow * flow) * (0.15 + 0.7 * flow * flow) * banks;
            }
            else if (vKind == 1)
            {
                // The falling sheet: u around it (0..1), v metres fallen, extra = the whole drop.
                // Streaks stretched along the fall, racing down, brighter strands here and there.
                float s1 = vnoise(vec2(vUv.x * 26.0, vUv.y * 0.05 - t * 1.6));
                float s2 = vnoise(vec2(vUv.x * 70.0, vUv.y * 0.14 - t * 3.2));
                float streak = s1 * 0.6 + s2 * 0.4;
                float strands = smoothstep(0.6, 0.9, s2);
                float ends = smoothstep(0.0, 2.0, vUv.y) * (0.6 + 0.4 * smoothstep(vExtra, vExtra - 25.0, vUv.y));
                alpha = (0.3 + 0.45 * streak) * ends;
                glow = (mix(Cyan, Lilac, s1 * 0.5) * (0.08 + 0.75 * streak * streak) + vec3(0.8, 1.0, 1.0) * strands * 0.45) * ends;
            }
            else if (vKind == 2)
            {
                // The pond: uv = offset from where the water lands (metres), extra = radius.
                float r = length(vUv);
                float rf = r / vExtra;
                float rings = pow(0.5 + 0.5 * sin(r * 2.4 - t * 3.0), 6.0) * exp(-rf * 2.2);
                float veins = smoothstep(0.55, 0.8, vnoise(vUv * 0.6 + vec2(t * 0.15, -t * 0.1)));
                float foam = smoothstep(2.5, 0.0, r) * (0.6 + 0.4 * vnoise(vUv * 3.0 + t * 2.0));
                float shore = smoothstep(0.7, 1.0, rf) * (0.6 + 0.4 * sin(t * 1.3 + atan(vUv.y, vUv.x) * 5.0));
                alpha = 0.85;
                glow = Cyan * (0.3 + 1.1 * rings + 0.6 * veins * (1.0 - rf * 0.5) + 0.8 * shore) + vec3(0.9, 1.0, 1.0) * foam;
            }
            else
            {
                // Mist and spray where the water lands: u around, v = 0 at the water up to 1.
                float swirl = vnoise(vec2(vUv.x * 12.0 + t * 0.4, vUv.y * 3.0 - t * 0.9));
                float fade = (1.0 - vUv.y) * (1.0 - vUv.y);
                alpha = 0.3 * swirl * fade;
                glow = mix(Cyan, vec3(1.0), 0.5) * swirl * fade * 0.45;
            }

            float dist = length(vWorldPos - uCameraPos);
            float fade = 1.0 - smoothstep(uFadeEnd * 0.6, uFadeEnd, dist);
            glow *= mix(0.9, 1.15, uNight);
            // Premultiplied: the water's own colour covers what is behind by alpha, the glow adds.
            FragColor = vec4((Deep * alpha + glow) * fade, alpha * fade);
        }
        """;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao, _vbo;
    private readonly List<float> _mesh = new();
    private readonly List<PointLight> _lights = new();
    private readonly List<Waterfall> _falls = new();
    private int _vertexCount;
    private int _version = -1;

    public WaterfallRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
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
            _lights.Add(new PointLight(foot + new Vector3(0, 2f, 0), new Vector3(0.35f, 0.9f, 1.0f) * 2.2f, fall.PondRadius * 1.6f + 10f));
        }
        return _lights;
    }

    public void Draw(Matrix4x4 viewProjection, Vector3 camera, float night, float time)
    {
        if (_vertexCount == 0) return;
        _shader.Use();
        _shader.Set("uViewProj", viewProjection);
        _shader.Set("uCameraPos", camera);
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
            width = fall.Width * (0.35f + 0.35f * f);
            return p;
        }
        float v = 0;
        for (int k = 0; k < Steps; k++)
        {
            var a = Point(k, out float wa);
            var b = Point(k + 1, out float wb);
            float vb = v + Vector3.Distance(a, b);
            Quad(a - across * wa * 0.5f, a + across * wa * 0.5f, b + across * wb * 0.5f, b - across * wb * 0.5f,
                new(0, v), new(1, v), new(1, vb), new(0, vb), Stream, 0);
            v = vb;
        }
    }

    private void BuildSheet(Waterfall fall)
    {
        // A flattened tube (wide across, thin along the direction of the fall) following the arc.
        const int Rings = 40, Sides = 12;
        var across = new Vector3(-fall.Direction.Z, 0, fall.Direction.X);
        float total = fall.Lip.Y - fall.Bottom;
        if (total <= 1f) return;
        var rings = new Vector3[Rings + 1, Sides + 1];
        var drops = new float[Rings + 1];
        for (int k = 0; k <= Rings; k++)
        {
            // Rings crowd near the top, where the arc bends.
            float drop = total * MathF.Pow(k / (float)Rings, 1.8f);
            drops[k] = drop;
            var center = fall.Lip + fall.Direction * Reach(drop) - new Vector3(0, drop, 0);
            float half = fall.Width * 0.5f * (1f + drop * 0.003f);
            float depth = fall.Width * 0.14f * MathF.Min(1f, 0.3f + drop / 6f);
            for (int s = 0; s <= Sides; s++)
            {
                float a = s * MathF.Tau / Sides;
                rings[k, s] = center + across * MathF.Cos(a) * half + fall.Direction * MathF.Sin(a) * depth;
            }
        }
        for (int k = 0; k < Rings; k++)
        for (int s = 0; s < Sides; s++)
        {
            float u0 = s / (float)Sides, u1 = (s + 1) / (float)Sides;
            Quad(rings[k, s], rings[k, s + 1], rings[k + 1, s + 1], rings[k + 1, s],
                new(u0, drops[k]), new(u1, drops[k]), new(u1, drops[k + 1]), new(u0, drops[k + 1]), Sheet, total);
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
        float bottom = fall.Width * 0.9f, top = fall.Width * 1.6f, height = 5f + fall.Width;
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

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, float kind, float extra)
    {
        Vertex(a, ua, kind, extra); Vertex(b, ub, kind, extra); Vertex(c, uc, kind, extra);
        Vertex(a, ua, kind, extra); Vertex(c, uc, kind, extra); Vertex(d, ud, kind, extra);
    }

    private void Vertex(Vector3 p, Vector2 uv, float kind, float extra)
    {
        _mesh.Add(p.X); _mesh.Add(p.Y); _mesh.Add(p.Z);
        _mesh.Add(uv.X); _mesh.Add(uv.Y);
        _mesh.Add(kind); _mesh.Add(extra);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }
}
