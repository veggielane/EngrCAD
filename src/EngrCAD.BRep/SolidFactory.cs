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

    /// <summary>
    /// Sphere: two revolved hemispherical faces (exact rational quarter-circle
    /// generators) sharing the equator circle edge. V=1, E=1, F=2. The generators touch
    /// the revolve axis at the poles, so this is built directly rather than through
    /// <see cref="Revolve"/> (whose profiles must stay off-axis).
    /// </summary>
    public static BrepSolid MakeSphere(double radius, in Vector3d center = default)
    {
        if (radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));

        var equator = new Circle3d(center, Vector3d.UnitX, Vector3d.UnitY, radius);
        var seam = new BrepVertex(equator.PointAt(0));
        var equatorEdge = new BrepEdge(equator, new Interval(0, 2 * Math.PI), seam, seam);

        // Generators live in the XZ plane; revolving about +Z sweeps the sphere. Both
        // run with +Z tangent at the equator so ∂u × ∂v points outward.
        var northArc = NurbsCurve.Arc(center, Vector3d.UnitX, Vector3d.UnitZ, radius, 0, Math.PI / 2);
        var southArc = NurbsCurve.Arc(center, Vector3d.UnitX, Vector3d.UnitZ, radius, -Math.PI / 2, 0);

        // Equator at the north generator's start ⇒ loop follows the circle (like the
        // cylinder side's bottom loop); at the south generator's end ⇒ opposes it.
        var north = new BrepFace(
            new RevolvedSurface(northArc, center, Vector3d.UnitZ),
            [new BrepLoop([new BrepCoedge(equatorEdge, sameSense: true)])]);
        var south = new BrepFace(
            new RevolvedSurface(southArc, center, Vector3d.UnitZ),
            [new BrepLoop([new BrepCoedge(equatorEdge, sameSense: false)])]);

        return new BrepSolid([new BrepShell([north, south])]);
    }

    /// <summary>
    /// Torus about +Z centered at the origin: the minor circle (radius
    /// <paramref name="minorRadius"/> at distance <paramref name="majorRadius"/> from the
    /// axis) split into two exact arcs and fully revolved — outer and inner bands sharing
    /// the top and bottom junction circles. V=2, E=2, F=2, genus 1.
    /// </summary>
    public static BrepSolid MakeTorus(double majorRadius, double minorRadius, in Vector3d center = default, Vector3d? axis = null)
    {
        if (minorRadius <= 0) throw new ArgumentOutOfRangeException(nameof(minorRadius));
        if (majorRadius <= minorRadius)
            throw new ArgumentOutOfRangeException(nameof(majorRadius),
                "The major radius must exceed the minor radius (the tube may not touch the axis).");

        var a = (axis ?? Vector3d.UnitZ).Normalized();
        var radial = a.ArbitraryPerpendicular(Tolerance.Default);
        var tubeCenter = center + radial * majorRadius;
        var outerArc = NurbsCurve.Arc(tubeCenter, radial, a, minorRadius, -Math.PI / 2, Math.PI / 2);
        var innerArc = NurbsCurve.Arc(tubeCenter, radial, a, minorRadius, Math.PI / 2, 3 * Math.PI / 2);
        return Revolve(new Profile([outerArc, innerArc]), center, a);
    }

    /// <summary>
    /// Cone frustum: radius <paramref name="bottomRadius"/> at the base growing linearly
    /// to <paramref name="topRadius"/> at <paramref name="height"/> along the axis
    /// (default +Z from the origin). The side is an exact <see cref="RevolvedSurface"/>
    /// of the slanted line generator — reusing the proven revolved-band machinery
    /// (domain-driven tessellation with pole fans, analytic plane⊥revolve intersection
    /// circles, wrap splitting, STEP SURFACE_OF_REVOLUTION export) rather than adding a
    /// dedicated cone surface type every consumer would need to learn. Rim circles are
    /// phase-aligned with the surface's u = 0 (both built on the same radial frame), so
    /// tessellation samples coincide exactly. A zero radius makes that end an apex pole
    /// (like sphere poles): no rim edge, no cap, and the side face keeps its single
    /// remaining rim loop. Frustum: V=2, E=2, F=3; apex cone: V=1, E=1, F=2.
    /// </summary>
    public static BrepSolid MakeCone(
        double bottomRadius, double topRadius, double height,
        in Vector3d baseCenter = default, Vector3d? axis = null)
    {
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (bottomRadius < 0) throw new ArgumentOutOfRangeException(nameof(bottomRadius));
        if (topRadius < 0) throw new ArgumentOutOfRangeException(nameof(topRadius));
        double poleTolerance = Tolerance.Default.Linear;
        bool bottomIsApex = bottomRadius <= poleTolerance;
        bool topIsApex = topRadius <= poleTolerance;
        if (bottomIsApex && topIsApex)
            throw new ArgumentException("At least one of the two radii must be positive.", nameof(bottomRadius));

        var a = (axis ?? Vector3d.UnitZ).Normalized();
        var x = a.ArbitraryPerpendicular(Tolerance.Default);
        var y = a.Cross(x); // rim circles share this frame so they stay phase-aligned with u = 0
        var topCenter = baseCenter + a * height;
        var full = new Interval(0, 2 * Math.PI);

        // Generator runs bottom→top — counter-clockwise in (radius, height) — so
        // ∂u × ∂v points outward; a zero-radius end sits exactly on the axis (a pole).
        var generator = new Line3d(baseCenter + x * bottomRadius, topCenter + x * topRadius);

        // Side loops follow the cylinder convention: the bottom rim follows +u, the top
        // rim opposes it; an apex end contributes no loop (the pole ring is degenerate).
        var sideLoops = new List<BrepLoop>(2);
        BrepEdge? bottomEdge = null, topEdge = null;
        if (!bottomIsApex)
        {
            var circle = new Circle3d(baseCenter, x, y, bottomRadius);
            var seam = new BrepVertex(circle.PointAt(0));
            bottomEdge = new BrepEdge(circle, full, seam, seam);
            sideLoops.Add(new BrepLoop([new BrepCoedge(bottomEdge, sameSense: true)]));
        }
        if (!topIsApex)
        {
            var circle = new Circle3d(topCenter, x, y, topRadius);
            var seam = new BrepVertex(circle.PointAt(0));
            topEdge = new BrepEdge(circle, full, seam, seam);
            sideLoops.Add(new BrepLoop([new BrepCoedge(topEdge, sameSense: false)]));
        }

        var faces = new List<BrepFace>(3)
        {
            new(new RevolvedSurface(generator, baseCenter, a), sideLoops),
        };
        if (topEdge is not null)
        {
            // Top cap: normal +axis (basis x, y), loop follows the circle.
            faces.Add(new BrepFace(
                new PlaneSurface(topCenter, x, y),
                [new BrepLoop([new BrepCoedge(topEdge, sameSense: true)])]));
        }
        if (bottomEdge is not null)
        {
            // Bottom cap: normal −axis (basis y, x), loop opposes the circle.
            faces.Add(new BrepFace(
                new PlaneSurface(baseCenter, y, x),
                [new BrepLoop([new BrepCoedge(bottomEdge, sameSense: false)])]));
        }

        return new BrepSolid([new BrepShell(faces)]);
    }
}
