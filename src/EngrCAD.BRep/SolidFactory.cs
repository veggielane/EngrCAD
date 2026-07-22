using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Construction of primitive solids with full topology. Faces are built so surface
/// normals point outward and loops run counter-clockwise around them.
/// </summary>
public static partial class SolidFactory
{
    /// <summary>Axis-aligned box: 8 vertices, 12 line edges, 6 planar faces.</summary>
    public static BrepSolid MakeBox(in Aabb bounds)
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
        // Same outward-CCW face table as MeshPrimitives.Box.
        int[][] faceLoops =
        [
            [0, 3, 2, 1], // bottom (−Z)
            [4, 5, 6, 7], // top (+Z)
            [0, 1, 5, 4], // −Y
            [1, 2, 6, 5], // +X
            [2, 3, 7, 6], // +Y
            [3, 0, 4, 7], // −X
        ];

        var vertices = positions.Select(p => new BrepVertex(p)).ToArray();
        var edges = new Dictionary<(int, int), BrepEdge>();

        BrepEdge GetEdge(int a, int b, out bool sameSense)
        {
            var key = a < b ? (a, b) : (b, a);
            if (!edges.TryGetValue(key, out var edge))
            {
                edge = new BrepEdge(
                    new Line3d(vertices[key.Item1].Position, vertices[key.Item2].Position),
                    Interval.Unit, vertices[key.Item1], vertices[key.Item2]);
                edges[key] = edge;
            }
            sameSense = key.Item1 == a;
            return edge;
        }

        var faces = new List<BrepFace>(6);
        foreach (var loopIndices in faceLoops)
        {
            var coedges = new List<BrepCoedge>(4);
            for (int i = 0; i < loopIndices.Length; i++)
            {
                int a = loopIndices[i];
                int b = loopIndices[(i + 1) % loopIndices.Length];
                coedges.Add(new BrepCoedge(GetEdge(a, b, out bool sameSense), sameSense));
            }

            // Outward normal from the CCW loop (Newell over 4 points).
            var p0 = positions[loopIndices[0]];
            var p1 = positions[loopIndices[1]];
            var p2 = positions[loopIndices[2]];
            var normal = (p1 - p0).Cross(p2 - p0).Normalized();
            var xDirection = (p1 - p0).Normalized();
            var surface = new PlaneSurface(p0, xDirection, normal.Cross(xDirection));

            faces.Add(new BrepFace(surface, [new BrepLoop(coedges)]));
        }

        return new BrepSolid([new BrepShell(faces)]);
    }

    /// <summary>
    /// Cylinder along +Z from z = 0 to z = height: a periodic cylindrical face bounded by
    /// two closed circular edges, plus two planar cap faces. V=2, E=2, F=3, L=4.
    /// </summary>
    public static BrepSolid MakeCylinder(double radius, double height)
    {
        if (radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        var x = Vector3d.UnitX;
        var y = Vector3d.UnitY;
        var full = new Interval(0, 2 * Math.PI);

        var bottomCircle = new Circle3d((0, 0, 0), x, y, radius);
        var topCircle = new Circle3d((0, 0, height), x, y, radius);
        var bottomSeam = new BrepVertex(bottomCircle.PointAt(0));
        var topSeam = new BrepVertex(topCircle.PointAt(0));
        var bottomEdge = new BrepEdge(bottomCircle, full, bottomSeam, bottomSeam);
        var topEdge = new BrepEdge(topCircle, full, topSeam, topSeam);

        // Side: cylindrical surface (normal radially outward), bounded below and above.
        // Loop orientation: CCW around the outward normal ⇒ bottom loop follows the circle
        // (+u), the top loop opposes it.
        var side = new BrepFace(
            new CylinderSurface((0, 0, 0), x, y, radius),
            [
                new BrepLoop([new BrepCoedge(bottomEdge, sameSense: true)]),
                new BrepLoop([new BrepCoedge(topEdge, sameSense: false)]),
            ]);

        // Top cap: normal +Z, loop follows the circle CCW seen from above.
        var topCap = new BrepFace(
            new PlaneSurface((0, 0, height), x, y),
            [new BrepLoop([new BrepCoedge(topEdge, sameSense: true)])]);

        // Bottom cap: normal −Z (basis y, x), loop opposes the circle.
        var bottomCap = new BrepFace(
            new PlaneSurface((0, 0, 0), y, x),
            [new BrepLoop([new BrepCoedge(bottomEdge, sameSense: false)])]);

        return new BrepSolid([new BrepShell([side, topCap, bottomCap])]);
    }
}
