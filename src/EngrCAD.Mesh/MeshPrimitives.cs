using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Generators for primitive closed meshes. All primitives are wound counter-clockwise
/// viewed from outside (positive <see cref="HalfEdgeMesh.Volume"/>). Z is up.
/// </summary>
public static class MeshPrimitives
{
    /// <summary>Axis-aligned box: 8 vertices, 6 quad faces.</summary>
    public static HalfEdgeMesh Box(in Aabb bounds)
    {
        if (bounds.IsEmpty)
            throw new ArgumentException("Box bounds must be non-empty.", nameof(bounds));

        var (x0, y0, z0) = bounds.Min;
        var (x1, y1, z1) = bounds.Max;
        Vector3d[] positions =
        [
            (x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0),
            (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1),
        ];
        List<int[]> faces =
        [
            [0, 3, 2, 1], // bottom (−Z)
            [4, 5, 6, 7], // top (+Z)
            [0, 1, 5, 4], // −Y
            [1, 2, 6, 5], // +X
            [2, 3, 7, 6], // +Y
            [3, 0, 4, 7], // −X
        ];
        return HalfEdgeMesh.Build(positions, faces);
    }

    public static HalfEdgeMesh Box(double width, double depth, double height) =>
        Box(new Aabb((-width / 2, -depth / 2, -height / 2), (width / 2, depth / 2, height / 2)));

    /// <summary>
    /// Latitude/longitude sphere centered at the origin: triangle fans at the poles,
    /// quad bands between. <paramref name="segments"/> around Z, <paramref name="rings"/>
    /// latitude divisions pole to pole.
    /// </summary>
    public static HalfEdgeMesh UvSphere(double radius, int segments = 32, int rings = 16)
    {
        if (radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        if (segments < 3) throw new ArgumentOutOfRangeException(nameof(segments));
        if (rings < 2) throw new ArgumentOutOfRangeException(nameof(rings));

        var positions = new List<Vector3d> { (0, 0, radius) };
        for (int i = 1; i < rings; i++)
        {
            double phi = Math.PI * i / rings;
            double z = radius * Math.Cos(phi);
            double r = radius * Math.Sin(phi);
            for (int j = 0; j < segments; j++)
            {
                double theta = 2 * Math.PI * j / segments;
                positions.Add((r * Math.Cos(theta), r * Math.Sin(theta), z));
            }
        }
        positions.Add((0, 0, -radius));

        int Ring(int ring, int segment) => 1 + (ring - 1) * segments + segment % segments;
        int top = 0, bottom = positions.Count - 1;

        var faces = new List<int[]>();
        for (int j = 0; j < segments; j++)
            faces.Add([top, Ring(1, j), Ring(1, j + 1)]);
        for (int i = 1; i < rings - 1; i++)
        {
            for (int j = 0; j < segments; j++)
                faces.Add([Ring(i, j), Ring(i + 1, j), Ring(i + 1, j + 1), Ring(i, j + 1)]);
        }
        for (int j = 0; j < segments; j++)
            faces.Add([bottom, Ring(rings - 1, j + 1), Ring(rings - 1, j)]);

        return HalfEdgeMesh.Build(positions, faces);
    }

    /// <summary>
    /// Cylinder along Z from z = 0 to z = <paramref name="height"/>: quad sides and
    /// single n-gon caps (the half-edge structure supports polygon faces directly).
    /// </summary>
    public static HalfEdgeMesh Cylinder(double radius, double height, int segments = 32)
    {
        if (radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (segments < 3) throw new ArgumentOutOfRangeException(nameof(segments));

        var positions = new List<Vector3d>();
        for (int j = 0; j < segments; j++)
        {
            double theta = 2 * Math.PI * j / segments;
            positions.Add((radius * Math.Cos(theta), radius * Math.Sin(theta), 0));
        }
        for (int j = 0; j < segments; j++)
        {
            double theta = 2 * Math.PI * j / segments;
            positions.Add((radius * Math.Cos(theta), radius * Math.Sin(theta), height));
        }

        var faces = new List<int[]>();
        for (int j = 0; j < segments; j++)
        {
            int j1 = (j + 1) % segments;
            faces.Add([j, j1, segments + j1, segments + j]);
        }
        var topCap = new int[segments];
        var bottomCap = new int[segments];
        for (int j = 0; j < segments; j++)
        {
            topCap[j] = segments + j;
            bottomCap[j] = segments - 1 - j;
        }
        faces.Add(topCap);
        faces.Add(bottomCap);

        return HalfEdgeMesh.Build(positions, faces);
    }
}
