using System.Numerics;

namespace Mine.Rendering;

/// <summary>
/// Builds meshes in the object vertex layout: position (3), normal (3), colour (3), emissive (1).
/// Triangles are wound counter-clockwise seen from outside.
/// </summary>
internal sealed class MeshBuilder
{
    public readonly List<float> Vertices = new();

    public void Box(Vector3 min, Vector3 max, Vector3 color, float emissive = 0f)
    {
        Vector3 C(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
        Quad(C(1, 0, 0), C(1, 1, 0), C(1, 1, 1), C(1, 0, 1), color, emissive); // +X
        Quad(C(0, 0, 1), C(0, 1, 1), C(0, 1, 0), C(0, 0, 0), color, emissive); // -X
        Quad(C(0, 1, 0), C(0, 1, 1), C(1, 1, 1), C(1, 1, 0), color, emissive); // +Y
        Quad(C(0, 0, 0), C(1, 0, 0), C(1, 0, 1), C(0, 0, 1), color, emissive); // -Y
        Quad(C(1, 0, 1), C(1, 1, 1), C(0, 1, 1), C(0, 0, 1), color, emissive); // +Z
        Quad(C(0, 0, 0), C(0, 1, 0), C(1, 1, 0), C(1, 0, 0), color, emissive); // -Z
    }

    /// <summary>One face of the box [min, max]: 0 +X, 1 -X, 2 +Y, 3 -Y, 4 +Z, 5 -Z.</summary>
    public void BoxFace(Vector3 min, Vector3 max, int face, Vector3 color, float emissive = 0f)
    {
        Vector3 C(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
        switch (face)
        {
            case 0: Quad(C(1, 0, 0), C(1, 1, 0), C(1, 1, 1), C(1, 0, 1), color, emissive); break;
            case 1: Quad(C(0, 0, 1), C(0, 1, 1), C(0, 1, 0), C(0, 0, 0), color, emissive); break;
            case 2: Quad(C(0, 1, 0), C(0, 1, 1), C(1, 1, 1), C(1, 1, 0), color, emissive); break;
            case 3: Quad(C(0, 0, 0), C(1, 0, 0), C(1, 0, 1), C(0, 0, 1), color, emissive); break;
            case 4: Quad(C(1, 0, 1), C(1, 1, 1), C(0, 1, 1), C(0, 0, 1), color, emissive); break;
            default: Quad(C(0, 0, 0), C(0, 1, 0), C(1, 1, 0), C(1, 0, 0), color, emissive); break;
        }
    }

    /// <summary>
    /// An elongated eight-faced gem standing on <paramref name="baseCenter"/>; its tip leans by
    /// <paramref name="tilt"/> per metre of height. A negative height makes it hang downward.
    /// </summary>
    public void Octahedron(Vector3 baseCenter, float radius, float height, Vector3 color, float emissive, Vector3 tilt = default)
    {
        bool hanging = height < 0;
        float middle = height * 0.35f;
        var bottom = baseCenter;
        var top = baseCenter + new Vector3(0, height, 0) + tilt * height;
        var ring = new Vector3[4];
        for (int i = 0; i < 4; i++)
        {
            float a = i * MathF.PI / 2 + MathF.PI / 4;
            ring[i] = baseCenter + new Vector3(MathF.Cos(a) * radius, middle, MathF.Sin(a) * radius) + tilt * middle;
        }
        for (int i = 0; i < 4; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % 4];
            if (hanging) (a, b) = (b, a); // mirrored vertically: keep the faces wound outward
            Triangle(top, b, a, color, emissive);
            Triangle(bottom, a, b, color, emissive);
        }
    }

    /// <summary>A flat quad a-b-c-d, wound counter-clockwise seen from the side it faces.</summary>
    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 color, float emissive)
    {
        Triangle(a, b, c, color, emissive);
        Triangle(a, c, d, color, emissive);
    }

    /// <summary>One vertex with its own (smooth) normal; add them three by three.</summary>
    public void Vertex(Vector3 p, Vector3 normal, Vector3 color, float emissive)
    {
        Vertices.Add(p.X); Vertices.Add(p.Y); Vertices.Add(p.Z);
        Vertices.Add(normal.X); Vertices.Add(normal.Y); Vertices.Add(normal.Z);
        Vertices.Add(color.X); Vertices.Add(color.Y); Vertices.Add(color.Z);
        Vertices.Add(emissive);
    }

    public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 color, float emissive)
    {
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        foreach (var p in (ReadOnlySpan<Vector3>)[a, b, c])
        {
            Vertices.Add(p.X); Vertices.Add(p.Y); Vertices.Add(p.Z);
            Vertices.Add(normal.X); Vertices.Add(normal.Y); Vertices.Add(normal.Z);
            Vertices.Add(color.X); Vertices.Add(color.Y); Vertices.Add(color.Z);
            Vertices.Add(emissive);
        }
    }
}
