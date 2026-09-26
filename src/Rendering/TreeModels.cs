using System.Numerics;

namespace Mine.Rendering;

/// <summary>
/// Procedural tree meshes in a soft, storybook style: a tapering, slightly curved trunk, a few
/// branches, and a crown of noise-displaced blobs of foliage. Each variant is built at several
/// levels of detail from the same random structure, so switching level never changes the shape;
/// the last level, for far away, merges the crown into a single ellipsoid on a three-sided trunk.
/// Vertex layout: position (3), normal (3), colour (3), emissive (1), sway (1).
/// </summary>
public static class TreeModels
{
    public const int FloatsPerVertex = 11;
    public const int LodCount = 4;

    private sealed record Style(
        string Name,
        float Height,
        float TrunkRadius,
        int Branches,
        float CrownRadius,
        Vector3 CrownScale, // blob ellipsoid proportions
        Vector3 LeafLow,    // foliage colour at the bottom of a blob
        Vector3 LeafHigh,   // ...and at the top
        Vector3 Bark,
        Vector3? Orbs = null, // colour of glowing orbs hanging from the crown, if any
        bool Stacked = false, // cypress: blobs stacked up the trunk
        bool Flat = false);   // umbrella: wide, flat crown on long, low branches

    private static readonly Style[] Styles =
    [
        new("Indaco", 7f, 0.36f, 4, 2.6f, new(1f, 0.85f, 1f), new(0.10f, 0.12f, 0.28f), new(0.30f, 0.36f, 0.68f), new(0.22f, 0.16f, 0.24f)),
        new("Cipresso", 10f, 0.28f, 0, 1.5f, new(1f, 1.5f, 1f), new(0.06f, 0.08f, 0.18f), new(0.20f, 0.24f, 0.46f), new(0.18f, 0.13f, 0.20f), Stacked: true),
        new("Rosa", 8f, 0.38f, 5, 2.8f, new(1f, 0.8f, 1f), new(0.42f, 0.12f, 0.34f), new(1.0f, 0.56f, 0.80f), new(0.24f, 0.16f, 0.24f)),
        new("Turchese", 9f, 0.32f, 4, 2.5f, new(1f, 0.9f, 1f), new(0.05f, 0.24f, 0.30f), new(0.40f, 0.86f, 0.86f), new(0.20f, 0.18f, 0.26f)),
        new("Fiori lilla", 6.5f, 0.30f, 5, 2.4f, new(1f, 0.8f, 1f), new(0.40f, 0.22f, 0.50f), new(0.96f, 0.70f, 0.96f), new(0.24f, 0.18f, 0.26f)),
        new("Lucciole", 7.5f, 0.36f, 4, 2.6f, new(1f, 0.85f, 1f), new(0.08f, 0.10f, 0.24f), new(0.28f, 0.28f, 0.62f), new(0.20f, 0.15f, 0.24f), Orbs: new(1.0f, 0.82f, 0.42f)),
        new("Sfere azzurre", 8.5f, 0.32f, 4, 2.5f, new(1f, 0.9f, 1f), new(0.24f, 0.16f, 0.46f), new(0.64f, 0.54f, 0.98f), new(0.22f, 0.18f, 0.28f), Orbs: new(0.45f, 0.92f, 1.0f)),
        new("Ombrello", 8f, 0.30f, 5, 2.8f, new(1.3f, 0.42f, 1.3f), new(0.18f, 0.10f, 0.32f), new(0.56f, 0.36f, 0.80f), new(0.20f, 0.14f, 0.24f), Flat: true),
    ];

    /// <summary>Glowing decorations that share the tree pipeline (instancing, wind, shadows), after the tree styles.</summary>
    public enum Decoration
    {
        VioletCrystals,
        CyanCrystals,
        Lotus,
        GlowBells,
        ShoreRock,
    }

    public static int TreeStyleCount => Styles.Length;

    public static int VariantCount => Styles.Length + Enum.GetValues<Decoration>().Length;

    /// <summary>The variant index of a decoration.</summary>
    public static int VariantOf(Decoration decoration) => Styles.Length + (int)decoration;

    /// <summary>How far away a variant is still drawn: small plants vanish sooner than trees and crystals.</summary>
    public static float MaxDistance(int variant) => variant < Styles.Length ? float.MaxValue : (Decoration)(variant - Styles.Length) switch
    {
        Decoration.Lotus => 160f,
        Decoration.GlowBells => 140f,
        _ => 700f,
    };

    /// <summary>Radius of a variant's trunk at the foot, before instance scaling (for collisions).</summary>
    public static float TrunkRadius(int variant) => variant < Styles.Length ? Styles[variant].TrunkRadius : (Decoration)(variant - Styles.Length) switch
    {
        Decoration.VioletCrystals or Decoration.CyanCrystals => 0.7f,
        Decoration.ShoreRock => 1.1f,
        _ => 0f, // flowers do not block the way
    };

    private readonly record struct Segment(Vector3 A, Vector3 B, float RadiusA, float RadiusB, float SwayA, float SwayB);
    private readonly record struct Blob(Vector3 Center, float Radius, Vector3 Scale, float Seed, float Shade);
    private readonly record struct Orb(Vector3 Center, float Radius);

    public static float[] Build(int variant, int lod)
    {
        if (variant >= Styles.Length) return BuildDecoration((Decoration)(variant - Styles.Length), lod);
        var style = Styles[variant];
        var random = new Random(variant * 7919 + 11);
        var segments = new List<Segment>();
        var blobs = new List<Blob>();
        var orbs = new List<Orb>();
        float h = style.Height;
        float Sway(float y) => MathF.Pow(Math.Clamp(y / h, 0, 1), 2) * 0.6f;

        // Trunk: a gentle curve, tapering toward the top.
        var lean = new Vector3(random.NextSingle() - 0.5f, 0, random.NextSingle() - 0.5f) * 0.6f;
        var trunk = new Vector3[6];
        for (int i = 0; i < trunk.Length; i++)
        {
            float t = i / (float)(trunk.Length - 1);
            trunk[i] = new Vector3(0, t * h, 0) + lean * t * t + new Vector3(MathF.Sin(t * 5 + variant), 0, MathF.Cos(t * 4 + variant)) * 0.12f * t;
        }
        for (int i = 0; i < trunk.Length - 1; i++)
        {
            float t0 = i / (float)(trunk.Length - 1), t1 = (i + 1) / (float)(trunk.Length - 1);
            segments.Add(new(trunk[i], trunk[i + 1], style.TrunkRadius * (1 - 0.65f * t0), style.TrunkRadius * (1 - 0.65f * t1), Sway(trunk[i].Y), Sway(trunk[i + 1].Y)));
        }
        var top = trunk[^1];
        var crownCenter = top;

        if (style.Stacked)
        {
            // Cypress: overlapping blobs climbing the trunk, narrowing toward the tip.
            for (int i = 0; i < 6; i++)
            {
                float t = 0.3f + 0.7f * i / 5f;
                var along = trunk[(int)MathF.Round(t * (trunk.Length - 1))];
                float r = style.CrownRadius * (1.15f - 0.55f * t);
                blobs.Add(new(along + new Vector3(0, 0.5f, 0), r, style.CrownScale, random.NextSingle() * 10, 0.9f + 0.2f * random.NextSingle()));
            }
            crownCenter = trunk[3];
        }
        else
        {
            blobs.Add(new(top + new Vector3(0, style.CrownRadius * 0.35f, 0), style.CrownRadius, style.CrownScale, random.NextSingle() * 10, 1f));
            for (int b = 0; b < style.Branches; b++)
            {
                float angle = b * MathF.Tau / style.Branches + random.NextSingle() * 0.8f;
                float startT = style.Flat ? 0.75f + 0.15f * random.NextSingle() : 0.5f + 0.3f * random.NextSingle();
                var start = trunk[(int)MathF.Round(startT * (trunk.Length - 1))];
                float rise = style.Flat ? 0.25f + 0.2f * random.NextSingle() : 0.7f + 0.4f * random.NextSingle();
                var direction = Vector3.Normalize(new Vector3(MathF.Cos(angle), rise, MathF.Sin(angle)));
                float length = h * (style.Flat ? 0.45f : 0.28f + 0.12f * random.NextSingle());
                var middle = start + direction * length * 0.55f + new Vector3(0, length * 0.1f, 0);
                var end = start + direction * length;
                float r0 = style.TrunkRadius * 0.35f;
                segments.Add(new(start, middle, r0, r0 * 0.6f, 0.7f, 0.8f));
                segments.Add(new(middle, end, r0 * 0.6f, r0 * 0.3f, 0.8f, 0.9f));
                float blobRadius = style.CrownRadius * (0.65f + 0.2f * random.NextSingle());
                blobs.Add(new(end + new Vector3(0, blobRadius * 0.25f, 0), blobRadius, style.CrownScale, random.NextSingle() * 10, 0.85f + 0.25f * random.NextSingle()));
            }
            // A couple of extra blobs to round the crown out.
            for (int i = 0; i < 2 && !style.Flat; i++)
            {
                var offset = new Vector3(random.NextSingle() - 0.5f, 0.3f + 0.4f * random.NextSingle(), random.NextSingle() - 0.5f) * style.CrownRadius * 1.3f;
                blobs.Add(new(top + offset, style.CrownRadius * 0.7f, style.CrownScale, random.NextSingle() * 10, 1f));
            }
        }

        if (style.Orbs is { } _)
        {
            // Glowing orbs hanging just under the foliage.
            foreach (var blob in blobs)
                for (int i = 0; i < 2; i++)
                {
                    float a = random.NextSingle() * MathF.Tau;
                    var dir = Vector3.Normalize(new Vector3(MathF.Cos(a), -0.5f - 0.4f * random.NextSingle(), MathF.Sin(a)));
                    orbs.Add(new(blob.Center + dir * blob.Radius * blob.Scale * 0.95f, 0.11f + 0.06f * random.NextSingle()));
                }
        }

        var mesh = new List<float>();
        if (lod == LodCount - 1)
        {
            BuildSilhouette(mesh, style, trunk, blobs);
            return mesh.ToArray();
        }
        int sides = lod switch { 0 => 8, 1 => 6, _ => 4 };
        foreach (var s in segments)
            Cylinder(mesh, s, sides, style.Bark);
        var sphere = Icosphere(lod switch { 0 => 2, 1 => 1, _ => 0 });
        foreach (var blob in blobs)
            Foliage(mesh, sphere, blob, crownCenter, style);
        if (style.Orbs is { } orbColor)
        {
            var small = Icosphere(lod == 0 ? 1 : 0);
            foreach (var orb in orbs)
                foreach (var d in small)
                    Vertex(mesh, orb.Center + d * orb.Radius, d, orbColor, 1f, 1f);
        }
        return mesh.ToArray();
    }

    /// <summary>Far-away version: one straight trunk and one ellipsoid enclosing every foliage blob.</summary>
    private static void BuildSilhouette(List<float> mesh, Style style, Vector3[] trunk, List<Blob> blobs)
    {
        var center = Vector3.Zero;
        foreach (var blob in blobs) center += blob.Center;
        center /= blobs.Count;
        var radii = Vector3.Zero;
        foreach (var blob in blobs)
        {
            var extent = Vector3.Abs(blob.Center - center) * 0.8f + blob.Scale * blob.Radius;
            radii = Vector3.Max(radii, extent);
        }
        Cylinder(mesh, new Segment(trunk[0], center, style.TrunkRadius, style.TrunkRadius * 0.5f, 0, 0.5f), 3, style.Bark);
        Foliage(mesh, Icosphere(1), new Blob(center, 1f, radii, 1.7f, 1f), center, style);
    }

    private static void Cylinder(List<float> mesh, Segment s, int sides, Vector3 bark)
    {
        var axis = Vector3.Normalize(s.B - s.A);
        var side = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var other = Vector3.Cross(axis, side);
        for (int i = 0; i < sides; i++)
        {
            float a0 = i * MathF.Tau / sides, a1 = (i + 1) * MathF.Tau / sides;
            var n0 = side * MathF.Cos(a0) + other * MathF.Sin(a0);
            var n1 = side * MathF.Cos(a1) + other * MathF.Sin(a1);
            var p00 = s.A + n0 * s.RadiusA; var p01 = s.A + n1 * s.RadiusA;
            var p10 = s.B + n0 * s.RadiusB; var p11 = s.B + n1 * s.RadiusB;
            // Bark darkens toward the ground.
            var cA = bark * (0.7f + 0.3f * Math.Clamp(s.A.Y / 3f, 0, 1));
            var cB = bark * (0.7f + 0.3f * Math.Clamp(s.B.Y / 3f, 0, 1));
            Vertex(mesh, p00, n0, cA, 0, s.SwayA); Vertex(mesh, p11, n1, cB, 0, s.SwayB); Vertex(mesh, p10, n0, cB, 0, s.SwayB);
            Vertex(mesh, p00, n0, cA, 0, s.SwayA); Vertex(mesh, p01, n1, cA, 0, s.SwayA); Vertex(mesh, p11, n1, cB, 0, s.SwayB);
        }
    }

    /// <summary>A lumpy ellipsoid of leaves: lighter on top, darker underneath and toward the inside of the crown.</summary>
    private static void Foliage(List<float> mesh, List<Vector3> sphere, Blob blob, Vector3 crownCenter, Style style)
    {
        var outward = blob.Center - crownCenter;
        outward = outward.LengthSquared() > 0.01f ? Vector3.Normalize(outward) : Vector3.UnitY;
        foreach (var d in sphere)
        {
            float lump = MathF.Sin(d.X * 3.1f + blob.Seed) * MathF.Sin(d.Y * 3.7f + blob.Seed * 1.3f) * MathF.Sin(d.Z * 2.9f + blob.Seed * 0.7f);
            var p = blob.Center + d * (1 + 0.22f * lump) * blob.Radius * blob.Scale;
            var n = Vector3.Normalize(d / blob.Scale);
            float height = d.Y * 0.5f + 0.5f;
            float inside = 0.65f + 0.35f * (Vector3.Dot(d, outward) * 0.5f + 0.5f);
            var color = Vector3.Lerp(style.LeafLow, style.LeafHigh, height * 0.85f + lump * 0.15f) * inside * blob.Shade;
            Vertex(mesh, p, n, color, 0, 1);
        }
    }

    private static float[] BuildDecoration(Decoration decoration, int lod)
    {
        var mesh = new List<float>();
        var random = new Random(9000 + (int)decoration);
        switch (decoration)
        {
            case Decoration.VioletCrystals:
            case Decoration.CyanCrystals:
            {
                var tint = decoration == Decoration.VioletCrystals ? new Vector3(0.66f, 0.42f, 1.0f) : new Vector3(0.40f, 0.85f, 1.0f);
                Rock(mesh, Vector3.Zero, new Vector3(1.0f, 0.35f, 0.9f), new Vector3(0.10f, 0.08f, 0.16f), lod, 3.1f);
                int count = 7;
                for (int i = 0; i < count; i++)
                {
                    // The tallest in the middle, the others leaning outward around it.
                    float a = i * MathF.Tau / count + random.NextSingle() * 0.6f;
                    float r = i == 0 ? 0f : 0.25f + 0.45f * random.NextSingle();
                    var foot = new Vector3(MathF.Cos(a) * r, 0.1f, MathF.Sin(a) * r);
                    var dir = Vector3.Normalize(new Vector3(MathF.Cos(a) * r * 1.2f, 1f, MathF.Sin(a) * r * 1.2f));
                    float length = i == 0 ? 3.2f : 1.0f + 1.8f * random.NextSingle();
                    float radius = i == 0 ? 0.38f : 0.16f + 0.16f * random.NextSingle();
                    var shade = tint * (0.85f + 0.3f * random.NextSingle());
                    Prism(mesh, foot, dir, length, radius, shade, lod >= 2 ? 4 : 6);
                }
                break;
            }
            case Decoration.Lotus:
            {
                // A dark floating pad, two rings of petals glowing at the heart, and a bright core.
                var pad = new Vector3(0.10f, 0.14f, 0.20f);
                int padSides = lod >= 2 ? 8 : 16;
                for (int i = 1; i < padSides; i++) // segment 0 left out: the notch of the leaf
                {
                    float a0 = i * MathF.Tau / padSides, a1 = (i + 1) * MathF.Tau / padSides;
                    var p0 = new Vector3(MathF.Cos(a0) * 0.75f, 0f, MathF.Sin(a0) * 0.75f);
                    var p1 = new Vector3(MathF.Cos(a1) * 0.75f, 0f, MathF.Sin(a1) * 0.75f);
                    Vertex(mesh, Vector3.Zero, Vector3.UnitY, pad, 0, 0.15f);
                    Vertex(mesh, p1, Vector3.UnitY, pad, 0, 0.15f);
                    Vertex(mesh, p0, Vector3.UnitY, pad, 0, 0.15f);
                }
                Petals(mesh, 8, 0.42f, 0.35f, 0.9f, new Vector3(0.95f, 0.55f, 1.0f), 0.0f);
                Petals(mesh, 6, 0.30f, 0.38f, 0.45f, new Vector3(1.0f, 0.70f, 1.0f), 0.5f);
                foreach (var d in Icosphere(lod >= 2 ? 0 : 1))
                    Vertex(mesh, new Vector3(0, 0.14f, 0) + d * 0.09f, d, new Vector3(1.0f, 0.9f, 0.8f), 1f, 0.15f);
                break;
            }
            case Decoration.GlowBells:
            {
                int stems = 6;
                for (int i = 0; i < stems; i++)
                {
                    float a = random.NextSingle() * MathF.Tau;
                    float r = 0.1f + 0.25f * random.NextSingle();
                    float height = 0.5f + 0.6f * random.NextSingle();
                    var foot = new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
                    var bend = new Vector3(MathF.Cos(a) * 0.25f, 0, MathF.Sin(a) * 0.25f);
                    var top = foot + new Vector3(0, height, 0) + bend;
                    Cylinder(mesh, new Segment(foot, top, 0.018f, 0.012f, 0, 0.8f), 3, new Vector3(0.10f, 0.20f, 0.26f));
                    // A drooping bell: a small cone hanging from the tip.
                    var color = i % 2 == 0 ? new Vector3(0.45f, 0.9f, 1.0f) : new Vector3(0.8f, 0.55f, 1.0f);
                    Bell(mesh, top + bend * 0.2f, 0.07f + 0.04f * random.NextSingle(), color, lod >= 2 ? 5 : 8);
                }
                break;
            }
            case Decoration.ShoreRock:
            {
                Rock(mesh, Vector3.Zero, new Vector3(1.4f, 0.9f, 1.1f), new Vector3(0.12f, 0.09f, 0.18f), lod, 5.3f);
                for (int i = 0; i < 3; i++)
                {
                    float a = random.NextSingle() * MathF.Tau;
                    var foot = new Vector3(MathF.Cos(a) * 0.9f, 0.5f, MathF.Sin(a) * 0.6f);
                    var dir = Vector3.Normalize(new Vector3(MathF.Cos(a), 0.9f, MathF.Sin(a)));
                    Prism(mesh, foot, dir, 0.5f + 0.4f * random.NextSingle(), 0.1f, new Vector3(0.55f, 0.45f, 1.0f), 5);
                }
                break;
            }
        }
        return mesh.ToArray();
    }

    /// <summary>A lumpy, dark stone half sunk in the ground.</summary>
    private static void Rock(List<float> mesh, Vector3 center, Vector3 size, Vector3 color, int lod, float seed)
    {
        foreach (var d in Icosphere(lod >= 2 ? 1 : 2))
        {
            float lump = 1f + 0.25f * MathF.Sin(d.X * 4.1f + seed) * MathF.Sin(d.Z * 3.3f - seed) + 0.1f * MathF.Sin(d.Y * 7f + seed);
            var p = center + d * lump * size;
            p.Y = MathF.Max(p.Y, -0.3f);
            var shade = color * (0.8f + 0.35f * (d.Y * 0.5f + 0.5f));
            Vertex(mesh, p, Vector3.Normalize(d / size), shade, 0, 0);
        }
    }

    /// <summary>
    /// A crystal: a hexagonal (or square) prism with a pointed tip. It glows from within: dim at the
    /// foot, brighter toward the tip, the facets alternating in brightness.
    /// </summary>
    private static void Prism(List<float> mesh, Vector3 foot, Vector3 axis, float length, float radius, Vector3 color, int sides)
    {
        var side = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var other = Vector3.Cross(axis, side);
        var shoulder = foot + axis * length * 0.78f;
        var tip = foot + axis * length;
        for (int i = 0; i < sides; i++)
        {
            float a0 = i * MathF.Tau / sides, a1 = (i + 1) * MathF.Tau / sides;
            var o0 = side * MathF.Cos(a0) + other * MathF.Sin(a0);
            var o1 = side * MathF.Cos(a1) + other * MathF.Sin(a1);
            var b0 = foot + o0 * radius; var b1 = foot + o1 * radius;
            var s0 = shoulder + o0 * radius; var s1 = shoulder + o1 * radius;
            var facet = color * (i % 2 == 0 ? 1f : 0.8f);
            var n = Vector3.Normalize(o0 + o1);
            Vertex(mesh, b0, n, facet * 0.7f, 0.3f, 0); Vertex(mesh, s1, n, facet, 0.75f, 0); Vertex(mesh, s0, n, facet, 0.75f, 0);
            Vertex(mesh, b0, n, facet * 0.7f, 0.3f, 0); Vertex(mesh, b1, n, facet * 0.7f, 0.3f, 0); Vertex(mesh, s1, n, facet, 0.75f, 0);
            var tn = Vector3.Normalize(Vector3.Cross(s1 - s0, tip - s0));
            Vertex(mesh, s0, tn, facet, 0.8f, 0); Vertex(mesh, s1, tn, facet, 0.8f, 0); Vertex(mesh, tip, tn, color * 1.2f, 1f, 0);
        }
    }

    /// <summary>A ring of cupped petals, both sides visible, glowing more toward the heart of the flower.</summary>
    private static void Petals(List<float> mesh, int count, float length, float width, float lift, Vector3 color, float twist)
    {
        for (int i = 0; i < count; i++)
        {
            float a = i * MathF.Tau / count + twist;
            var outward = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
            var across = new Vector3(-outward.Z, 0, outward.X);
            var root = new Vector3(0, 0.05f, 0);
            var mid = root + outward * length * 0.55f + new Vector3(0, lift * length * 0.5f, 0);
            var tip = root + outward * length + new Vector3(0, lift * length, 0);
            var l = mid - across * width * 0.5f;
            var r = mid + across * width * 0.5f;
            var n = Vector3.Normalize(Vector3.Cross(r - root, tip - root));
            // Front and back faces, so the petals are seen from both sides with culling on.
            Vertex(mesh, root, n, color, 0.9f, 0.3f); Vertex(mesh, l, n, color, 0.55f, 0.3f); Vertex(mesh, tip, n, color * 1.1f, 0.35f, 0.3f);
            Vertex(mesh, root, n, color, 0.9f, 0.3f); Vertex(mesh, tip, n, color * 1.1f, 0.35f, 0.3f); Vertex(mesh, r, n, color, 0.55f, 0.3f);
            Vertex(mesh, root, -n, color, 0.9f, 0.3f); Vertex(mesh, tip, -n, color * 1.1f, 0.35f, 0.3f); Vertex(mesh, l, -n, color, 0.55f, 0.3f);
            Vertex(mesh, root, -n, color, 0.9f, 0.3f); Vertex(mesh, r, -n, color, 0.55f, 0.3f); Vertex(mesh, tip, -n, color * 1.1f, 0.35f, 0.3f);
        }
    }

    /// <summary>A small glowing bell hanging down from <paramref name="top"/>.</summary>
    private static void Bell(List<float> mesh, Vector3 top, float radius, Vector3 color, int sides)
    {
        var mouth = top - new Vector3(0, radius * 1.6f, 0);
        for (int i = 0; i < sides; i++)
        {
            float a0 = i * MathF.Tau / sides, a1 = (i + 1) * MathF.Tau / sides;
            var o0 = new Vector3(MathF.Cos(a0), 0, MathF.Sin(a0));
            var o1 = new Vector3(MathF.Cos(a1), 0, MathF.Sin(a1));
            var p0 = mouth + o0 * radius; var p1 = mouth + o1 * radius;
            var n = Vector3.Normalize(o0 + o1 + new Vector3(0, 0.5f, 0));
            Vertex(mesh, top, n, color, 1f, 1f); Vertex(mesh, p1, n, color, 0.8f, 1f); Vertex(mesh, p0, n, color, 0.8f, 1f);
            Vertex(mesh, top, -n, color, 1f, 1f); Vertex(mesh, p0, -n, color, 0.8f, 1f); Vertex(mesh, p1, -n, color, 0.8f, 1f);
        }
    }

    private static void Vertex(List<float> mesh, Vector3 p, Vector3 n, Vector3 c, float emissive, float sway)
    {
        mesh.Add(p.X); mesh.Add(p.Y); mesh.Add(p.Z);
        mesh.Add(n.X); mesh.Add(n.Y); mesh.Add(n.Z);
        mesh.Add(c.X); mesh.Add(c.Y); mesh.Add(c.Z);
        mesh.Add(emissive);
        mesh.Add(sway);
    }

    private static readonly Dictionary<int, List<Vector3>> SphereCache = new();

    /// <summary>Unit icosphere as a flat list of triangle corners (counter-clockwise from outside).</summary>
    private static List<Vector3> Icosphere(int subdivisions)
    {
        lock (SphereCache)
        {
            if (SphereCache.TryGetValue(subdivisions, out var cached)) return cached;

            float t = (1 + MathF.Sqrt(5)) / 2;
            Vector3[] v =
            [
                new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
                new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
                new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
            ];
            int[] f =
            [
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
            ];
            var triangles = new List<Vector3>();
            for (int i = 0; i < f.Length; i++) triangles.Add(Vector3.Normalize(v[f[i]]));

            for (int s = 0; s < subdivisions; s++)
            {
                var next = new List<Vector3>(triangles.Count * 4);
                for (int i = 0; i < triangles.Count; i += 3)
                {
                    var a = triangles[i]; var b = triangles[i + 1]; var c = triangles[i + 2];
                    var ab = Vector3.Normalize(a + b); var bc = Vector3.Normalize(b + c); var ca = Vector3.Normalize(c + a);
                    next.AddRange([a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca]);
                }
                triangles = next;
            }
            SphereCache[subdivisions] = triangles;
            return triangles;
        }
    }
}
