using System.Numerics;
using Mine.World;

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
        bool Flat = false,    // umbrella: wide, flat crown on long, low branches
        bool Weeping = false, // willow: long blobs hanging below the branch tips
        bool Bare = false);   // dry tree: no leaves, branches forking into twigs

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
        new("Gigante", 15f, 0.62f, 6, 4.0f, new(1f, 0.8f, 1f), new(0.14f, 0.08f, 0.30f), new(0.52f, 0.36f, 0.86f), new(0.18f, 0.12f, 0.22f)),
        new("Salice", 7.5f, 0.34f, 7, 2.0f, new(0.8f, 1.5f, 0.8f), new(0.04f, 0.20f, 0.26f), new(0.30f, 0.78f, 0.80f), new(0.18f, 0.14f, 0.22f), Weeping: true),
        new("Pino", 12f, 0.30f, 0, 2.7f, new(1f, 0.55f, 1f), new(0.05f, 0.07f, 0.20f), new(0.18f, 0.30f, 0.60f), new(0.16f, 0.12f, 0.20f), Stacked: true),
        new("Alberello", 3.6f, 0.16f, 3, 1.3f, new(1f, 0.85f, 1f), new(0.40f, 0.12f, 0.36f), new(1.0f, 0.55f, 0.85f), new(0.22f, 0.15f, 0.22f)),
        new("Arbusto", 1.4f, 0.10f, 2, 1.0f, new(1.2f, 0.7f, 1.2f), new(0.10f, 0.14f, 0.30f), new(0.32f, 0.42f, 0.72f), new(0.18f, 0.14f, 0.22f)),
        // Dry trees of the deserts: bleached, twisted, bare.
        new("Secco", 6.5f, 0.30f, 5, 0f, Vector3.One, default, default, new(0.50f, 0.42f, 0.50f), Bare: true),
        new("Contorto", 4.2f, 0.36f, 4, 0f, Vector3.One, default, default, new(0.30f, 0.22f, 0.30f), Bare: true),
        new("Cespuglio secco", 1.1f, 0.06f, 7, 0f, Vector3.One, default, default, new(0.46f, 0.36f, 0.40f), Bare: true),
    ];

    /// <summary>Glowing decorations that share the tree pipeline (instancing, wind, shadows), after the tree styles.</summary>
    public enum Decoration
    {
        VioletCrystals,
        CyanCrystals,
        Lotus,
        GlowBells,
        Reeds,
        Palm,
        Daisies,     // flat pastel stars
        Tulips,      // upright cups, strong colours
        Starflowers, // small glowing stars, also under the trees
        Lupins,      // tall spikes of florets with glowing tips
        // Flowers with leaves (the ones above are bare stems), the leaves shaped like grass blades:
        Irises,      // a fan of upright sword leaves, flowers with raised and drooping petals
        Poppies,     // a rosette of broad leaves lying on the ground, cup flowers on thin stems
        Lilies,      // arching strap leaves, leafy stems, glowing trumpets
        TallPalm,    // a very tall, slender palm with a deep curve
        ShortPalm,   // a short, stocky palm with a big crown
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
        Decoration.Daisies or Decoration.Tulips or Decoration.Starflowers => 110f,
        Decoration.Lupins => 140f,
        Decoration.Irises or Decoration.Lilies or Decoration.Poppies => 120f,
        Decoration.Reeds => 400f,
        Decoration.Palm or Decoration.TallPalm or Decoration.ShortPalm => float.MaxValue,
        _ => 700f,
    };

    /// <summary>Whether a variant is drawn into the shadow map: small flowers are not worth it.</summary>
    public static bool CastsShadow(int variant) => variant < Styles.Length || (Decoration)(variant - Styles.Length) is not
        (Decoration.Lotus or Decoration.GlowBells or Decoration.Daisies or Decoration.Tulips or Decoration.Starflowers or Decoration.Lupins
         or Decoration.Irises or Decoration.Poppies or Decoration.Lilies);

    /// <summary>The colour a decoration lights its surroundings with, or null if it gives no light.</summary>
    public static System.Numerics.Vector3? GlowColor(int variant) => variant < Styles.Length ? null : (Decoration)(variant - Styles.Length) switch
    {
        Decoration.VioletCrystals => new Vector3(0.66f, 0.42f, 1.0f),
        Decoration.CyanCrystals => new Vector3(0.40f, 0.85f, 1.0f),
        _ => null,
    };

    /// <summary>How high above its base a glowing decoration's light sits, before scaling.</summary>
    public static float GlowHeight(int variant) => 1.4f;

    /// <summary>Radius of a variant's trunk at the foot, before instance scaling (for collisions).</summary>
    public static float TrunkRadius(int variant) => variant < Styles.Length ? Styles[variant].TrunkRadius : (Decoration)(variant - Styles.Length) switch
    {
        Decoration.VioletCrystals or Decoration.CyanCrystals => 0.7f,
        Decoration.Reeds => 0f,
        Decoration.Palm => 0.24f,
        Decoration.TallPalm => 0.22f,
        Decoration.ShortPalm => 0.3f,
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
        else if (style.Bare)
        {
            // Dry tree: crooked branches from the upper trunk, each forking into two or three
            // twigs that fork once more, all reaching up and out.
            for (int b = 0; b < style.Branches; b++)
            {
                float angle = b * MathF.Tau / style.Branches + random.NextSingle() * 0.9f;
                float startT = 0.35f + 0.5f * random.NextSingle();
                var start = trunk[(int)MathF.Round(startT * (trunk.Length - 1))];
                var direction = Vector3.Normalize(new Vector3(MathF.Cos(angle), 0.5f + 0.7f * random.NextSingle(), MathF.Sin(angle)));
                float length = h * (0.3f + 0.15f * random.NextSingle());
                AddTwigs(segments, random, start, direction, length, style.TrunkRadius * 0.45f, 0.6f, 2);
            }
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
                // Willows hang their foliage below the branch tips; others sit on top of them.
                float lift = style.Weeping ? -blobRadius * style.CrownScale.Y * 0.7f : blobRadius * 0.25f;
                blobs.Add(new(end + new Vector3(0, lift, 0), blobRadius, style.CrownScale, random.NextSingle() * 10, 0.85f + 0.25f * random.NextSingle()));
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
        if (lod == LodCount - 1 && !style.Bare)
        {
            BuildSilhouette(mesh, style, trunk, blobs);
            return mesh.ToArray();
        }
        int sides = lod switch { 0 => 8, 1 => 6, 2 => 4, _ => 3 };
        foreach (var s in segments)
        {
            // Far away, a dry tree keeps only its trunk and thicker branches.
            if (style.Bare && lod >= 2 && s.RadiusA < style.TrunkRadius * (lod == 2 ? 0.15f : 0.3f)) continue;
            Cylinder(mesh, s, sides, style.Bark);
        }
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

    /// <summary>
    /// A crooked branch from <paramref name="start"/> (two segments, bending), which then forks into
    /// two or three thinner ones, <paramref name="depth"/> times.
    /// </summary>
    private static void AddTwigs(List<Segment> segments, Random random, Vector3 start, Vector3 direction, float length,
        float radius, float sway, int depth)
    {
        var bend = new Vector3(random.NextSingle() - 0.5f, random.NextSingle() * 0.3f, random.NextSingle() - 0.5f) * 0.6f;
        var middle = start + direction * length * 0.5f;
        var end = middle + Vector3.Normalize(direction + bend) * length * 0.5f;
        float nextSway = MathF.Min(sway + 0.12f, 0.95f);
        segments.Add(new(start, middle, radius, radius * 0.75f, sway, (sway + nextSway) * 0.5f));
        segments.Add(new(middle, end, radius * 0.75f, radius * 0.5f, (sway + nextSway) * 0.5f, nextSway));
        if (depth == 0) return;
        int forks = 2 + random.Next(2);
        var heading = Vector3.Normalize(end - middle);
        for (int f = 0; f < forks; f++)
        {
            float a = random.NextSingle() * MathF.Tau;
            var spread = new Vector3(MathF.Cos(a), 0.4f + 0.4f * random.NextSingle(), MathF.Sin(a)) * (0.7f + 0.3f * random.NextSingle());
            AddTwigs(segments, random, end, Vector3.Normalize(heading + spread), length * (0.5f + 0.2f * random.NextSingle()),
                radius * 0.5f, nextSway, depth - 1);
        }
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
                    float height = 0.8f + 0.5f * random.NextSingle();
                    var foot = new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
                    var bend = new Vector3(MathF.Cos(a) * 0.25f, 0, MathF.Sin(a) * 0.25f);
                    var top = foot + new Vector3(0, height, 0) + bend;
                    Cylinder(mesh, new Segment(foot, top, 0.018f, 0.012f, 0, BellSway), 3, new Vector3(0.10f, 0.20f, 0.26f));
                    // A drooping bell: a small cone hanging from the tip.
                    var color = i % 2 == 0 ? new Vector3(0.2f, 0.75f, 1.0f) : new Vector3(0.7f, 0.35f, 1.0f);
                    Bell(mesh, top, 0.08f + 0.04f * random.NextSingle(), color, lod >= 2 ? 5 : 8);
                }
                break;
            }
            case Decoration.Daisies:
            {
                Vector3[] colors = [new(1.0f, 0.62f, 0.82f), new(0.92f, 0.86f, 1.0f), new(1.0f, 0.84f, 0.48f), new(0.78f, 0.62f, 1.0f)];
                for (int i = 0; i < (lod >= 1 ? 4 : 7); i++)
                {
                    var top = FlowerStem(mesh, random, 0.75f, 0.4f, 0.3f);
                    Flower(mesh, top, 8, 0.13f + 0.04f * random.NextSingle(), 0.07f, 0.15f, colors[random.Next(colors.Length)], 0f,
                        new Vector3(1.0f, 0.75f, 0.25f), random.NextSingle());
                }
                break;
            }
            case Decoration.Tulips:
            {
                Vector3[] colors = [new(0.95f, 0.22f, 0.55f), new(1.0f, 0.45f, 0.30f), new(0.58f, 0.24f, 0.95f), new(1.0f, 0.75f, 0.30f)];
                for (int i = 0; i < (lod >= 1 ? 3 : 5); i++)
                {
                    var top = FlowerStem(mesh, random, 0.8f, 0.35f, 0.2f);
                    Flower(mesh, top, 5, 0.13f + 0.04f * random.NextSingle(), 0.1f, 2.2f, colors[random.Next(colors.Length)], 0.05f,
                        new Vector3(0.3f, 0.1f, 0.2f), random.NextSingle());
                }
                break;
            }
            case Decoration.Starflowers:
            {
                Vector3[] colors = [new(0.3f, 0.95f, 1.0f), new(1.0f, 0.8f, 0.35f), new(0.5f, 1.0f, 0.75f), new(0.95f, 0.5f, 1.0f)];
                for (int i = 0; i < (lod >= 1 ? 4 : 6); i++)
                {
                    var top = FlowerStem(mesh, random, 0.6f, 0.5f, 0.35f);
                    Flower(mesh, top, 5, 0.1f + 0.04f * random.NextSingle(), 0.05f, 0.3f, colors[random.Next(colors.Length)], 0.85f,
                        new Vector3(1.0f, 1.0f, 0.9f), random.NextSingle());
                }
                break;
            }
            case Decoration.Lupins:
            {
                // Spikes of small florets, darker at the foot and glowing toward the tip.
                Vector3[] foot = [new(0.25f, 0.2f, 0.75f), new(0.6f, 0.2f, 0.7f), new(0.2f, 0.45f, 0.8f)];
                Vector3[] tip = [new(0.95f, 0.55f, 1.0f), new(1.0f, 0.7f, 0.85f), new(0.5f, 0.95f, 1.0f)];
                for (int i = 0; i < (lod >= 1 ? 3 : 4); i++)
                {
                    float a = random.NextSingle() * MathF.Tau;
                    float r = 0.05f + 0.25f * random.NextSingle();
                    var start = new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
                    var end = start + new Vector3(MathF.Cos(a) * 0.1f, 1.1f + 0.6f * random.NextSingle(), MathF.Sin(a) * 0.1f);
                    Cylinder(mesh, new Segment(start, end, 0.02f, 0.012f, 0, BellSway), 3, new Vector3(0.12f, 0.2f, 0.25f));
                    int c = random.Next(foot.Length);
                    int florets = lod >= 1 ? 6 : 12;
                    for (int k = 0; k < florets; k++)
                    {
                        float t = 0.55f + 0.45f * k / (florets - 1);
                        float spin = k * 2.4f;
                        var p = Vector3.Lerp(start, end, t) + new Vector3(MathF.Cos(spin), 0, MathF.Sin(spin)) * 0.035f * (1.2f - t);
                        float u = (t - 0.55f) / 0.45f;
                        Bud(mesh, p, 0.06f * (1.2f - 0.6f * u), Vector3.Lerp(foot[c], tip[c], u), 0.1f + 0.6f * u * u, BellSway * t);
                    }
                }
                break;
            }
            case Decoration.Irises:
            case Decoration.Poppies:
            case Decoration.Lilies:
                LeafyFlowers(mesh, random, decoration, lod);
                break;
            case Decoration.Palm:
            case Decoration.TallPalm:
            case Decoration.ShortPalm:
            {
                // A slender trunk curving toward the water, crowned with long arching fronds: a dark
                // silhouette against the glowing sky, as on a tropical shore at dusk. Three builds
                // (with the instance scale on top) so a grove never looks like copies of one palm.
                var (height, radius, bend, frondLength, fronds0) = decoration switch
                {
                    Decoration.TallPalm => (13f, 0.22f, 1.5f, 3.2f, 11),
                    Decoration.ShortPalm => (4.6f, 0.3f, 0.8f, 3.4f, 12),
                    _ => (8.5f, 0.24f, 1f, 2.8f, 10),
                };
                float lean = (0.9f + 0.6f * random.NextSingle()) * bend;
                int rings = lod >= 2 ? 5 : 9;
                var trunk = new Vector3[rings + 1];
                for (int i = 0; i <= rings; i++)
                {
                    float t = i / (float)rings;
                    trunk[i] = new Vector3(lean * t * t * 2.2f, t * height, 0.3f * MathF.Sin(t * 2.5f));
                }
                var bark = new Vector3(0.10f, 0.07f, 0.13f);
                for (int i = 0; i < rings; i++)
                {
                    float t0 = i / (float)rings, t1 = (i + 1) / (float)rings;
                    Cylinder(mesh, new Segment(trunk[i], trunk[i + 1], radius * (1 - 0.35f * t0), radius * (1 - 0.35f * t1),
                        t0 * t0 * 0.6f, t1 * t1 * 0.6f), lod >= 2 ? 4 : 6, bark * (0.8f + 0.25f * t1));
                }

                var top = trunk[rings];
                int fronds = lod >= 2 ? 7 : fronds0;
                int steps = lod >= 2 ? 4 : 7;
                for (int f = 0; f < fronds; f++)
                {
                    float a = f * MathF.Tau / fronds + random.NextSingle() * 0.4f;
                    var outward = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
                    var across = new Vector3(-outward.Z, 0, outward.X);
                    float length = frondLength + 1.2f * random.NextSingle();
                    float rise = 0.6f + 0.5f * random.NextSingle();
                    var leaf = Vector3.Lerp(new Vector3(0.06f, 0.10f, 0.14f), new Vector3(0.12f, 0.16f, 0.26f), random.NextSingle());
                    Vector3 Point(float t) => top + outward * length * t + new Vector3(0, rise * MathF.Sin(t * 2.2f) - 1.9f * t * t, 0);
                    for (int s = 0; s < steps; s++)
                    {
                        float t0 = s / (float)steps, t1 = (s + 1) / (float)steps;
                        float w0 = 0.55f * MathF.Sin(MathF.PI * MathF.Max(t0, 0.08f)), w1 = 0.55f * MathF.Sin(MathF.PI * t1);
                        var p0 = Point(t0); var p1 = Point(t1);
                        // Leaflets droop on both sides of the rib, so the frond is a shallow V.
                        var l0 = p0 + across * w0 - new Vector3(0, w0 * 0.35f, 0);
                        var r0 = p0 - across * w0 - new Vector3(0, w0 * 0.35f, 0);
                        var l1 = p1 + across * w1 - new Vector3(0, w1 * 0.35f, 0);
                        var r1 = p1 - across * w1 - new Vector3(0, w1 * 0.35f, 0);
                        var n = Vector3.UnitY;
                        var c = leaf * (0.8f + 0.4f * t1);
                        // Both windings, so the fronds are seen from above and below.
                        Vertex(mesh, p0, n, c, 0, 1); Vertex(mesh, l0, n, c, 0, 1); Vertex(mesh, l1, n, c, 0, 1);
                        Vertex(mesh, p0, n, c, 0, 1); Vertex(mesh, l1, n, c, 0, 1); Vertex(mesh, p1, n, c, 0, 1);
                        Vertex(mesh, p0, n, c, 0, 1); Vertex(mesh, r1, n, c, 0, 1); Vertex(mesh, r0, n, c, 0, 1);
                        Vertex(mesh, p0, n, c, 0, 1); Vertex(mesh, p1, n, c, 0, 1); Vertex(mesh, r1, n, c, 0, 1);
                        Vertex(mesh, p0, -n, c, 0, 1); Vertex(mesh, l1, -n, c, 0, 1); Vertex(mesh, l0, -n, c, 0, 1);
                        Vertex(mesh, p0, -n, c, 0, 1); Vertex(mesh, p1, -n, c, 0, 1); Vertex(mesh, l1, -n, c, 0, 1);
                        Vertex(mesh, p0, -n, c, 0, 1); Vertex(mesh, r0, -n, c, 0, 1); Vertex(mesh, r1, -n, c, 0, 1);
                        Vertex(mesh, p0, -n, c, 0, 1); Vertex(mesh, r1, -n, c, 0, 1); Vertex(mesh, p1, -n, c, 0, 1);
                    }
                }
                break;
            }
            case Decoration.Reeds:
            {
                // A clump of tall, round reed stems of pale straw-mauve, most topped by the dark
                // velvet spike of a bulrush; they lean a little outward and sway from the top.
                int stems = lod >= 2 ? 7 : 14;
                int sides = lod >= 2 ? 3 : 5;
                for (int i = 0; i < stems; i++)
                {
                    float a = random.NextSingle() * MathF.Tau;
                    float r = MathF.Sqrt(random.NextSingle()) * 0.9f;
                    var foot = new Vector3(MathF.Cos(a) * r, -0.3f, MathF.Sin(a) * r);
                    float height = 2.0f + 2.5f * random.NextSingle() * random.NextSingle() + 1.2f * random.NextSingle();
                    var lean = new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * (0.15f + 0.3f * random.NextSingle());
                    var top = foot + new Vector3(0, height, 0) + lean;
                    var mid = foot + new Vector3(0, height * 0.55f, 0) + lean * 0.3f;
                    var stem = Vector3.Lerp(new Vector3(0.40f, 0.33f, 0.38f), new Vector3(0.56f, 0.48f, 0.50f), random.NextSingle());
                    float radius = 0.035f + 0.02f * random.NextSingle();
                    Cylinder(mesh, new Segment(foot, mid, radius, radius * 0.85f, 0f, 0.35f), sides, stem * 0.8f);
                    Cylinder(mesh, new Segment(mid, top, radius * 0.85f, radius * 0.6f, 0.35f, 0.9f), sides, stem);
                    if (random.NextSingle() < 0.65f)
                    {
                        // The bulrush head: a thick dark cylinder a little below the tip.
                        var dir = Vector3.Normalize(top - mid);
                        var headTop = top - dir * 0.25f;
                        var headBottom = headTop - dir * (0.35f + 0.2f * random.NextSingle());
                        Cylinder(mesh, new Segment(headBottom, headTop, radius * 2.6f, radius * 2.4f, 0.85f, 0.88f), sides,
                            new Vector3(0.20f, 0.10f, 0.18f));
                    }
                }
                break;
            }
        }
        return mesh.ToArray();
    }

    /// <summary>
    /// What breaking an instance gives, how long it takes, how it is aimed at (a vertical capsule
    /// around its trunk or body) and the colours of the chips that fly off it.
    /// </summary>
    public readonly record struct Yield(string Name, Amount Amount, float Seconds, float PickRadius, float PickHeight, Vector3 Chip, Vector3 Chip2);

    private static readonly Vector3 CrystalChip = new(0.75f, 0.55f, 1.0f), VioletChip = new(0.66f, 0.42f, 1.0f), CyanChip = new(0.45f, 0.85f, 1.0f);

    /// <summary>A small glowing crystal (<see cref="ObjectKind.Crystal"/>): quick to break, it gives only a couple of cubes.</summary>
    public static readonly Yield SmallCrystal = new("Cristallo", new Amount(Resource.Crystal, 2), 0.5f, 0.3f, 0.6f, CrystalChip, VioletChip);

    /// <summary>
    /// What breaking an instance of <paramref name="variant"/> at <paramref name="scale"/> gives
    /// (bigger ones give more and take longer), or null if it cannot be broken: trees (palms too)
    /// give wood and crystal clusters give crystal cubes (stone comes from digging the rock spires).
    /// </summary>
    public static Yield? YieldOf(int variant, float scale)
    {
        int Round(float v) => Math.Max(1, (int)MathF.Round(v));
        if (variant < Styles.Length)
        {
            var style = Styles[variant];
            float size = style.Height * scale;
            string name = style.Height < 2f ? "Arbusto" : style.Bare ? "Albero secco" : "Albero";
            return new Yield(name, new Amount(Resource.Wood, Round(size * (style.Bare ? 0.35f : 0.5f))), 0.8f + 0.1f * size,
                MathF.Max(0.45f, style.TrunkRadius * scale * 2f), MathF.Min(4f, MathF.Max(1.2f, size * 0.5f)),
                style.Bark * 1.3f, style.Bare ? style.Bark * 1.7f : style.LeafHigh);
        }
        return (Decoration)(variant - Styles.Length) switch
        {
            Decoration.Palm or Decoration.TallPalm or Decoration.ShortPalm => new Yield("Palma", new Amount(Resource.Wood, Round(4f * scale)),
                1.6f * scale, 0.5f, 3f, new Vector3(0.30f, 0.22f, 0.26f), new Vector3(0.16f, 0.22f, 0.34f)),
            // Crystal clusters (0.6 to 2.3 in scale) give 3 to 12 crystal cubes.
            Decoration.VioletCrystals or Decoration.CyanCrystals => new Yield("Cristalli giganti", new Amount(Resource.Crystal, Round(5.2f * scale)),
                1.2f * scale, 0.9f * scale, 2.6f * scale, CrystalChip, (Decoration)(variant - Styles.Length) == Decoration.CyanCrystals ? CyanChip : VioletChip),
            _ => null,
        };
    }

    /// <summary>A lumpy, dark stone half sunk in the ground.</summary>
    private static void Rock(List<float> mesh, Vector3 center, Vector3 size, Vector3 color, int lod, float seed, float emissive = 0f)
    {
        foreach (var d in Icosphere(lod >= 2 ? 1 : 2))
        {
            float lump = 1f + 0.25f * MathF.Sin(d.X * 4.1f + seed) * MathF.Sin(d.Z * 3.3f - seed) + 0.1f * MathF.Sin(d.Y * 7f + seed);
            var p = center + d * lump * size;
            p.Y = MathF.Max(p.Y, -0.3f);
            var shade = color * (0.8f + 0.35f * (d.Y * 0.5f + 0.5f));
            Vertex(mesh, p, Vector3.Normalize(d / size), shade, emissive, 0);
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

    // Below 0.99, so neither the stem tips nor the bells get the leaves' extra shiver.
    private const float BellSway = 0.8f;

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
            // Same sway as the tip of the stem, so the bell stays on it in the wind.
            Vertex(mesh, top, n, color, 0.55f, BellSway); Vertex(mesh, p1, n, color, 0.4f, BellSway); Vertex(mesh, p0, n, color, 0.4f, BellSway);
            Vertex(mesh, top, -n, color, 0.55f, BellSway); Vertex(mesh, p0, -n, color, 0.4f, BellSway); Vertex(mesh, p1, -n, color, 0.4f, BellSway);
        }
    }

    /// <summary>
    /// A thin stem from a random foot near the centre, <paramref name="height"/> plus up to
    /// <paramref name="extra"/> tall and leaning out by up to <paramref name="lean"/>; returns its tip.
    /// </summary>
    private static Vector3 FlowerStem(List<float> mesh, Random random, float height, float extra, float lean)
    {
        float a = random.NextSingle() * MathF.Tau;
        float r = 0.05f + 0.3f * random.NextSingle();
        float out_ = lean * random.NextSingle();
        var foot = new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
        var top = foot + new Vector3(MathF.Cos(a) * out_, height + extra * random.NextSingle(), MathF.Sin(a) * out_);
        Cylinder(mesh, new Segment(foot, top, 0.014f, 0.01f, 0, BellSway), 3, new Vector3(0.12f, 0.22f, 0.26f));
        return top;
    }

    /// <summary>A random point on the ground within <paramref name="radius"/> of the centre.</summary>
    private static Vector3 RandomFoot(Random random, float radius)
    {
        float a = random.NextSingle() * MathF.Tau, r = radius * MathF.Sqrt(random.NextSingle());
        return new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
    }

    /// <summary>The colours of a flower born of light: pale glowing stems, tinted leaves with veins of light, neon petals.</summary>
    private sealed record LightPalette(Vector3 Stem, Vector3 LeafBase, Vector3 LeafTip, Vector3[] PetalBase, Vector3[] PetalTip, Vector3 Seed);

    private static LightPalette PaletteOf(Decoration kind) => kind switch
    {
        Decoration.Lilies => new(new(0.80f, 0.92f, 1.0f), new(0.18f, 0.45f, 0.60f), new(0.45f, 0.85f, 0.95f),
            [new(0.15f, 0.85f, 1.0f), new(0.30f, 1.0f, 0.85f), new(0.55f, 0.75f, 1.0f)],
            [new(0.85f, 0.60f, 1.0f), new(0.90f, 1.0f, 1.0f), new(1.0f, 0.65f, 0.90f)], new(0.7f, 1.0f, 1.0f)),
        Decoration.Poppies => new(new(0.95f, 0.82f, 0.95f), new(0.40f, 0.18f, 0.50f), new(0.80f, 0.45f, 0.85f),
            [new(1.0f, 0.15f, 0.55f), new(0.95f, 0.25f, 0.80f), new(1.0f, 0.30f, 0.40f)],
            [new(1.0f, 0.70f, 0.30f), new(1.0f, 0.90f, 1.0f), new(0.70f, 0.40f, 1.0f)], new(1.0f, 0.8f, 0.5f)),
        _ => new(new(0.85f, 0.82f, 1.0f), new(0.20f, 0.20f, 0.55f), new(0.50f, 0.55f, 1.0f),
            [new(0.45f, 0.20f, 1.0f), new(0.30f, 0.35f, 1.0f), new(0.70f, 0.25f, 1.0f)],
            [new(0.40f, 0.85f, 1.0f), new(0.85f, 0.75f, 1.0f), new(1.0f, 0.50f, 0.90f)], new(0.8f, 0.7f, 1.0f)),
    };

    /// <summary>The colour of the light a flower born of light casts around it, or null.</summary>
    public static Vector3? FlowerLight(int variant) => variant < Styles.Length ? null : (Decoration)(variant - Styles.Length) switch
    {
        Decoration.Lilies => new Vector3(0.35f, 0.85f, 1.0f),
        Decoration.Poppies => new Vector3(1.0f, 0.35f, 0.7f),
        Decoration.Irises => new Vector3(0.6f, 0.4f, 1.0f),
        _ => null,
    };

    /// <summary>
    /// Flowers born of light, built like plants: curved pale stems glowing brighter toward the top,
    /// clothed in tinted leaves with veins of light, a calyx under a head of curved, cupped neon
    /// petals glowing toward their tips, stamens with shining tips, and seeds of light floating over
    /// the head. A few stems carry a closed bud. They also cast coloured light (<see cref="FlowerLight"/>).
    /// </summary>
    private static void LeafyFlowers(List<float> mesh, Random random, Decoration kind, int lod)
    {
        var palette = PaletteOf(kind);
        int rows = lod == 0 ? 5 : lod == 1 ? 3 : 2;
        int stems = lod >= 2 ? 1 : kind == Decoration.Poppies ? 2 : 1 + random.Next(2);
        void LeafBlade(Vector3 foot, Vector3 outward, float length, float width, float elevation, float droop, float swayHeight) =>
            Blade(mesh, foot, Vector3.UnitY, outward, length, width, elevation, droop, 0.3f, false, palette.LeafBase, palette.LeafTip,
                0.08f, 0.25f, swayHeight, rows, ribGlow: 0.45f);

        // Leaves from the ground: sword fans for the irises, a low rosette for the poppies.
        int basal = kind switch { Decoration.Irises => 7, Decoration.Poppies => 6, _ => 0 } / (lod == 0 ? 1 : 2);
        for (int i = 0; i < basal; i++)
        {
            float a = (i + 0.3f * random.NextSingle()) * MathF.Tau / basal;
            if (kind == Decoration.Irises)
                LeafBlade(RandomFoot(random, 0.06f), Horizontal(a), 0.45f + 0.3f * random.NextSingle(), 0.06f,
                    1.2f + 0.2f * random.NextSingle(), 0.3f + 0.35f * random.NextSingle(), 1f);
            else
                LeafBlade(RandomFoot(random, 0.04f), Horizontal(a), 0.24f + 0.08f * random.NextSingle(), 0.11f, 0.9f, 0.9f, 1f);
        }

        // A flower head (or a closed bud) at the tip of a stem or branch, facing along its axis.
        void Head(Vector3 top, Vector3 axis, float size, bool bud)
        {
            int hue = random.Next(palette.PetalBase.Length);
            Vector3 petalBase = palette.PetalBase[hue], petalTip = palette.PetalTip[hue];
            if (lod == 0) Calyx(mesh, top, axis, bud, palette);
            if (bud)
            {
                Petals(mesh, top, axis, 5, 0.1f * size, 0.06f * size, 0.12f, -0.1f, 0.4f, true, palette.Stem, petalBase, 0.3f, 0.8f, rows, random.NextSingle());
                return;
            }
            float twist = random.NextSingle() * MathF.Tau;
            switch (kind)
            {
                case Decoration.Poppies:
                    // Four broad cupped petals around a shining heart ringed with stamens.
                    Petals(mesh, top, axis, 4, 0.14f * size, 0.17f * size, 0.75f, 0.35f, 0.4f, true, petalBase, petalTip, 0.35f, 0.9f, rows, twist);
                    Bud(mesh, top + axis * 0.03f * size, 0.03f * size, palette.Seed, 1f, BellSway);
                    if (lod == 0)
                        for (int k = 0; k < 8; k++)
                            Bud(mesh, top + (axis * 0.025f + Horizontal(k * MathF.Tau / 8f) * 0.04f) * size, 0.01f, petalTip, 1f, BellSway);
                    break;
                case Decoration.Lilies:
                    // Six narrow petals curling back around long stamens.
                    Petals(mesh, top, axis, 3, 0.19f * size, 0.075f * size, 0.5f, 1.3f, 0.3f, false, petalBase, petalTip, 0.35f, 0.95f, rows, twist);
                    Petals(mesh, top, axis, 3, 0.18f * size, 0.07f * size, 0.55f, 1.2f, 0.3f, false, petalBase, petalTip, 0.35f, 0.95f, rows,
                        twist + MathF.PI / 3f);
                    if (lod == 0)
                    {
                        var (u, v) = Frame(axis);
                        for (int k = 0; k < 6; k++)
                        {
                            float a = k * MathF.Tau / 6f + twist;
                            var tip = top + (axis * 0.13f + (u * MathF.Cos(a) + v * MathF.Sin(a)) * 0.05f) * size;
                            GlowTube(mesh, top, tip, 0.004f, 0.003f, BellSway, BellSway, palette.Stem, 0.6f, 0.9f, 3);
                            Bud(mesh, tip, 0.012f, palette.Seed, 1f, BellSway);
                        }
                    }
                    break;
                default:
                    // Irises: three petals standing up and curling in, three wide falls drooping out,
                    // gold at their throat.
                    Petals(mesh, top, axis, 3, 0.13f * size, 0.08f * size, 0.25f, -0.35f, 0.35f, true, petalBase, petalTip, 0.35f, 0.9f, rows, twist);
                    Petals(mesh, top, axis, 3, 0.16f * size, 0.11f * size, 1.1f, 1.1f, 0.25f, true, new Vector3(1.0f, 0.85f, 0.4f), petalBase,
                        0.5f, 0.8f, rows, twist + MathF.PI / 3f);
                    break;
            }
            // Seeds of light floating over the head.
            if (lod <= 1)
                for (int k = 0; k < 2; k++)
                {
                    var p = top + axis * (0.1f + 0.2f * random.NextSingle()) + Horizontal(random.NextSingle() * MathF.Tau) * 0.1f * random.NextSingle();
                    Bud(mesh, p, 0.01f + 0.01f * random.NextSingle(), palette.Seed, 1f, BellSway);
                }
        }

        for (int s = 0; s < stems; s++)
        {
            float height = kind switch
            {
                Decoration.Poppies => 0.55f + 0.3f * random.NextSingle(),
                Decoration.Lilies => 0.8f + 0.35f * random.NextSingle(),
                _ => 0.75f + 0.3f * random.NextSingle(),
            };
            float StemSway(Vector3 p) => BellSway * Math.Clamp(p.Y / height, 0f, 1f);
            var foot = RandomFoot(random, 0.12f);
            var lean = Horizontal(random.NextSingle() * MathF.Tau) * (0.05f + 0.1f * random.NextSingle()) * height;
            Vector3 Stem(float t) => foot + new Vector3(0, height * t, 0) + lean * t * t;
            int segments = lod == 0 ? 4 : 2;
            for (int k = 0; k < segments; k++)
            {
                float t0 = k / (float)segments, t1 = (k + 1) / (float)segments;
                GlowTube(mesh, Stem(t0), Stem(t1), 0.018f - 0.007f * t0, 0.018f - 0.007f * t1, BellSway * t0, BellSway * t1,
                    palette.Stem, 0.25f + 0.35f * t0, 0.25f + 0.35f * t1, lod == 0 ? 4 : 3);
            }

            // Leaves along the stem below the branches, each turned on from the last (like real
            // plants), smaller higher up.
            int leaves = kind switch { Decoration.Lilies => 7, Decoration.Poppies => 3, _ => 2 };
            leaves = lod == 0 ? leaves : lod == 1 ? (leaves + 1) / 2 : 1;
            float turn = random.NextSingle() * MathF.Tau;
            for (int k = 0; k < leaves; k++)
            {
                float t = 0.12f + 0.55f * (k + 0.5f) / leaves;
                turn += 2.4f;
                float size = 1.1f - 0.5f * t;
                var (length, width, elevation, droop) = kind switch
                {
                    Decoration.Lilies => (0.25f, 0.065f, 0.75f, 0.9f),
                    Decoration.Poppies => (0.17f, 0.07f, 0.6f, 0.7f),
                    _ => (0.22f, 0.05f, 1.15f, 0.35f), // irises: narrow leaves hugging the stem
                };
                LeafBlade(Stem(t), Horizontal(turn), length * size, width * size, elevation, droop, height);
            }

            // One flower crowns the stem; below it the stem branches into two or three curving side
            // stems, each ending in a smaller flower (now and then a closed bud).
            Head(Stem(1f), Vector3.Normalize(new Vector3(0, height, 0) + lean * 2f), 1f, false);
            int branches = lod >= 2 ? 1 : 2 + random.Next(2);
            float spin = random.NextSingle() * MathF.Tau;
            for (int k = 0; k < branches; k++)
            {
                var start = Stem(0.7f + 0.2f * random.NextSingle());
                var outward = Horizontal(spin + (k + 0.3f * random.NextSingle()) * MathF.Tau / branches);
                float length = (0.16f + 0.1f * random.NextSingle()) * (0.6f + 0.4f * height);
                // A quadratic curve: out first, then turning upward.
                var control = start + outward * length * 0.6f + new Vector3(0, length * 0.2f, 0);
                var end = start + outward * length * 0.8f + new Vector3(0, length * 0.75f, 0);
                Vector3 Branch(float t) => Vector3.Lerp(Vector3.Lerp(start, control, t), Vector3.Lerp(control, end, t), t);
                int pieces = lod == 0 ? 3 : 1;
                for (int q = 0; q < pieces; q++)
                {
                    Vector3 b0 = Branch(q / (float)pieces), b1 = Branch((q + 1) / (float)pieces);
                    GlowTube(mesh, b0, b1, 0.01f - 0.003f * q / pieces, 0.01f - 0.003f * (q + 1) / pieces, StemSway(b0), StemSway(b1),
                        palette.Stem, 0.5f, 0.6f, 3);
                }
                // A little leaf (a bract) where the branch leaves the stem.
                if (lod == 0)
                    LeafBlade(start, Horizontal(spin + (k + 0.5f) * MathF.Tau / branches), 0.08f, 0.03f, 0.7f, 0.6f, height);
                var axis = Vector3.Normalize(Vector3.Normalize(end - control) + new Vector3(0, 0.6f, 0));
                Head(end, axis, 0.75f + 0.15f * random.NextSingle(), random.NextSingle() < 0.2f);
            }
        }
    }

    /// <summary>A tapering tube that glows from within, brighter toward <paramref name="b"/> when <paramref name="glowB"/> is higher.</summary>
    private static void GlowTube(List<float> mesh, Vector3 a, Vector3 b, float radiusA, float radiusB, float swayA, float swayB,
        Vector3 color, float glowA, float glowB, int sides)
    {
        var (u, v) = Frame(Vector3.Normalize(b - a));
        for (int i = 0; i < sides; i++)
        {
            float a0 = i * MathF.Tau / sides, a1 = (i + 1) * MathF.Tau / sides;
            var n0 = u * MathF.Cos(a0) + v * MathF.Sin(a0);
            var n1 = u * MathF.Cos(a1) + v * MathF.Sin(a1);
            var p00 = a + n0 * radiusA; var p01 = a + n1 * radiusA;
            var p10 = b + n0 * radiusB; var p11 = b + n1 * radiusB;
            // Counter-clockwise from outside: check against the outward normal.
            if (Vector3.Dot(Vector3.Cross(p11 - p00, p10 - p00), n0) < 0)
            {
                Vertex(mesh, p00, n0, color, glowA, swayA); Vertex(mesh, p11, n1, color, glowB, swayB); Vertex(mesh, p10, n0, color, glowB, swayB);
                Vertex(mesh, p00, n0, color, glowA, swayA); Vertex(mesh, p01, n1, color, glowA, swayA); Vertex(mesh, p11, n1, color, glowB, swayB);
            }
            else
            {
                Vertex(mesh, p00, n0, color, glowA, swayA); Vertex(mesh, p10, n0, color, glowB, swayB); Vertex(mesh, p11, n1, color, glowB, swayB);
                Vertex(mesh, p00, n0, color, glowA, swayA); Vertex(mesh, p11, n1, color, glowB, swayB); Vertex(mesh, p01, n1, color, glowA, swayA);
            }
        }
    }

    /// <summary>A horizontal unit vector at an angle around Y.</summary>
    private static Vector3 Horizontal(float angle) => new(MathF.Cos(angle), 0, MathF.Sin(angle));

    /// <summary>Two unit vectors perpendicular to <paramref name="axis"/> and to each other.</summary>
    private static (Vector3 U, Vector3 V) Frame(Vector3 axis)
    {
        var u = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ));
        return (u, Vector3.Cross(axis, u));
    }

    /// <summary>
    /// A ring of <paramref name="count"/> petals around <paramref name="axis"/> at <paramref name="center"/>,
    /// opened <paramref name="open"/> radians from the axis and curling by <paramref name="curl"/> (see <see cref="Blade"/>).
    /// </summary>
    private static void Petals(List<float> mesh, Vector3 center, Vector3 axis, int count, float length, float width, float open, float curl,
        float cup, bool rounded, Vector3 baseColor, Vector3 tipColor, float baseGlow, float tipGlow, int rows, float twist)
    {
        var (u, v) = Frame(axis);
        for (int i = 0; i < count; i++)
        {
            float a = twist + i * MathF.Tau / count;
            var outward = u * MathF.Cos(a) + v * MathF.Sin(a);
            Blade(mesh, center, axis, outward, length, width, MathF.PI / 2f - open, curl, cup, rounded,
                baseColor, tipColor, baseGlow, tipGlow, 0f, rows);
        }
    }

    /// <summary>Five small sepals under the flower head, folded back (or wrapped around a bud).</summary>
    private static void Calyx(List<float> mesh, Vector3 center, Vector3 axis, bool bud, LightPalette palette)
    {
        var (u, v) = Frame(axis);
        for (int i = 0; i < 5; i++)
        {
            float a = i * MathF.Tau / 5f;
            var outward = u * MathF.Cos(a) + v * MathF.Sin(a);
            if (bud) Blade(mesh, center - axis * 0.01f, axis, outward, 0.08f, 0.035f, 1.2f, -0.2f, 0.3f, false, palette.LeafBase, palette.LeafTip, 0.2f, 0.4f, 0f, 3, 0.4f);
            else Blade(mesh, center - axis * 0.02f, axis, outward, 0.05f, 0.03f, -0.4f, 0.3f, 0.2f, false, palette.LeafBase, palette.LeafTip, 0.2f, 0.4f, 0f, 3, 0.4f);
        }
    }

    /// <summary>
    /// A curved, folded surface: a leaf or a petal. It starts at <paramref name="foot"/> and grows in
    /// the plane of <paramref name="up"/> and <paramref name="outward"/> (perpendicular unit vectors),
    /// first at <paramref name="elevation"/> radians above <paramref name="outward"/>, then bending
    /// down by <paramref name="droop"/> radians over its length (negative: curling up). Its outline is
    /// narrow at the foot, widest near the middle and pointed (a leaf) or <paramref name="rounded"/> (a
    /// petal); its edges are raised by <paramref name="fold"/> of the half width toward the
    /// <paramref name="up"/> side, so leaves are channelled along the midrib and petals cupped. The
    /// midrib is lighter. Seen from both sides. Sway: constant (heads, <paramref name="swayHeight"/> 0)
    /// or growing with height up to <paramref name="swayHeight"/>, like the stem it grows on.
    /// </summary>
    private static void Blade(List<float> mesh, Vector3 foot, Vector3 up, Vector3 outward, float length, float width, float elevation,
        float droop, float fold, bool rounded, Vector3 baseColor, Vector3 tipColor, float baseGlow, float tipGlow, float swayHeight, int rows,
        float ribGlow = 0f)
    {
        var across = Vector3.Normalize(Vector3.Cross(up, outward));
        // Direction and centre at t (0..1 along the length), the angle measured from "up".
        float theta0 = MathF.PI / 2f - elevation, kappa = droop;
        Vector3 Direction(float t) => up * MathF.Cos(theta0 + kappa * t) + outward * MathF.Sin(theta0 + kappa * t);
        Vector3 Center(float t)
        {
            if (MathF.Abs(kappa) < 1e-3f) return foot + Direction(0) * length * t;
            float a = theta0 + kappa * t;
            return foot + (up * (MathF.Sin(a) - MathF.Sin(theta0)) + outward * (MathF.Cos(theta0) - MathF.Cos(a))) * (length / kappa);
        }
        float Half(float t) => rounded
            ? 0.5f * width * MathF.Max(MathF.Sin(MathF.PI * (0.1f + 0.8f * t)), 0.25f)
            : 0.5f * width * MathF.Pow(MathF.Max(MathF.Sin(MathF.PI * (0.08f + 0.92f * t)), 0f), 0.7f);

        Span<Vector3> left = stackalloc Vector3[8], mid = stackalloc Vector3[8], right = stackalloc Vector3[8];
        Span<float> ts = stackalloc float[8];
        for (int k = 0; k < rows; k++)
        {
            float t = rounded ? k / (float)(rows - 1) : k / (float)rows;
            var c = Center(t);
            var d = Direction(t);
            var inner = up * MathF.Sin(theta0 + kappa * t) - outward * MathF.Cos(theta0 + kappa * t); // perpendicular, toward "up"
            float h = Half(t);
            ts[k] = t;
            mid[k] = c;
            left[k] = c - across * h + inner * h * fold;
            right[k] = c + across * h + inner * h * fold;
        }
        var tip = rounded ? Center(1f) + Direction(1f) * (length / (rows - 1)) * 0.35f : Center(1f);

        Vector3 Color(float t, bool rib) => Vector3.Lerp(baseColor, tipColor, t) * (rib ? 1.15f : 1f);
        float Glow(float t, bool rib) => MathF.Min(float.Lerp(baseGlow, tipGlow, t) + (rib ? ribGlow : 0f), 1f);
        float Sway(Vector3 p) => swayHeight <= 0f ? BellSway : BellSway * Math.Clamp(p.Y / swayHeight, 0f, 1f);
        void Triangle(Vector3 a, float ta, bool ra, Vector3 b, float tb, bool rb, Vector3 c, float tc, bool rc)
        {
            var n = Vector3.Cross(b - a, c - a);
            if (n.LengthSquared() < 1e-14f) return;
            n = Vector3.Normalize(n);
            Vertex(mesh, a, n, Color(ta, ra), Glow(ta, ra), Sway(a)); Vertex(mesh, b, n, Color(tb, rb), Glow(tb, rb), Sway(b)); Vertex(mesh, c, n, Color(tc, rc), Glow(tc, rc), Sway(c));
            Vertex(mesh, a, -n, Color(ta, ra), Glow(ta, ra), Sway(a)); Vertex(mesh, c, -n, Color(tc, rc), Glow(tc, rc), Sway(c)); Vertex(mesh, b, -n, Color(tb, rb), Glow(tb, rb), Sway(b));
        }
        for (int k = 0; k + 1 < rows; k++)
        {
            float t0 = ts[k], t1 = ts[k + 1];
            Triangle(left[k], t0, false, mid[k], t0, true, mid[k + 1], t1, true);
            Triangle(left[k], t0, false, mid[k + 1], t1, true, left[k + 1], t1, false);
            Triangle(mid[k], t0, true, right[k], t0, false, right[k + 1], t1, false);
            Triangle(mid[k], t0, true, right[k + 1], t1, false, mid[k + 1], t1, true);
        }
        int last = rows - 1;
        Triangle(left[last], ts[last], false, mid[last], ts[last], true, tip, 1f, true);
        Triangle(mid[last], ts[last], true, right[last], ts[last], false, tip, 1f, true);
    }

    /// <summary>
    /// A flower head at <paramref name="center"/>: a ring of diamond petals rising by
    /// <paramref name="lift"/> (0 = flat star, 2 = upright cup) around a small heart.
    /// </summary>
    private static void Flower(List<float> mesh, Vector3 center, int petals, float length, float width, float lift,
        Vector3 color, float emissive, Vector3 heart, float twist)
    {
        for (int i = 0; i < petals; i++)
        {
            float a = (i + twist) * MathF.Tau / petals;
            var outward = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
            var across = new Vector3(-outward.Z, 0, outward.X);
            var dir = Vector3.Normalize(outward + new Vector3(0, lift, 0));
            var tip = center + dir * length;
            var mid = center + dir * length * 0.5f;
            var l = mid - across * width * 0.5f;
            var r = mid + across * width * 0.5f;
            var n = Vector3.Normalize(Vector3.Cross(r - center, tip - center));
            var pale = color * 1.15f;
            // Both windings, so the petals are seen from both sides with culling on.
            Vertex(mesh, center, n, color, emissive, BellSway); Vertex(mesh, l, n, color, emissive, BellSway); Vertex(mesh, tip, n, pale, emissive, BellSway);
            Vertex(mesh, center, n, color, emissive, BellSway); Vertex(mesh, tip, n, pale, emissive, BellSway); Vertex(mesh, r, n, color, emissive, BellSway);
            Vertex(mesh, center, -n, color, emissive, BellSway); Vertex(mesh, tip, -n, pale, emissive, BellSway); Vertex(mesh, l, -n, color, emissive, BellSway);
            Vertex(mesh, center, -n, color, emissive, BellSway); Vertex(mesh, r, -n, color, emissive, BellSway); Vertex(mesh, tip, -n, pale, emissive, BellSway);
        }
        Bud(mesh, center + new Vector3(0, 0.01f, 0), length * 0.22f, heart, MathF.Max(emissive, 0.2f), BellSway);
    }

    /// <summary>A tiny octahedron: a floret, or the heart of a flower.</summary>
    private static void Bud(List<float> mesh, Vector3 c, float r, Vector3 color, float emissive, float sway)
    {
        Vector3[] axes = [Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitX, -Vector3.UnitZ];
        for (int half = 0; half < 2; half++)
        {
            float up = half == 0 ? 1f : -1f;
            var pole = c + new Vector3(0, r * up, 0);
            for (int i = 0; i < 4; i++)
            {
                var a = c + axes[i] * r;
                var b = c + axes[(i + 1) % 4] * r;
                var n = Vector3.Normalize(axes[i] + axes[(i + 1) % 4] + new Vector3(0, up, 0));
                // Counter-clockwise from outside: check against the outward normal.
                if (Vector3.Dot(Vector3.Cross(b - a, pole - a), n) < 0) (a, b) = (b, a);
                Vertex(mesh, a, n, color, emissive, sway); Vertex(mesh, b, n, color, emissive, sway); Vertex(mesh, pole, n, color, emissive, sway);
            }
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
