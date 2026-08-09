using System.Linq.Expressions;
using EngrCAD.Core;

namespace EngrCAD.Implicit;

// Plane-bounded convex solids: prisms (regular n-gon and trapezoidal), and the general
// half-space intersection.
//
// Two tiers, and the difference between them is the whole design.
//
// A PRISM is EXACT, because the 3D problem factors: the solid is a convex 2D section times
// an axial interval, so the nearest point is found by solving the 2D problem exactly (the
// nearest point over the section's own edges, which is a closed form per edge) and combining
// it with the axial distance the way CylinderSdf already does — a combination that is exact
// for any convex section, since the section distance and the axial distance are measured
// along orthogonal directions.
//
// A general CONVEX POLYHEDRON does not factor that way, so it is answered in two halves and
// the split is exactly where the arithmetic changes. INSIDE, the max over the face half-spaces
// IS the distance — for a convex body the nearest boundary point lies on the nearest face
// plane — so one dot product per plane is exact and nothing more is needed. OUTSIDE it is only
// a lower bound, understating wherever the nearest feature is an edge or a vertex, so the node
// takes the minimum over its own boundary TRIANGLES instead, which is Sdf.Pyramid's route and
// exact for the same reason. The vertices were already being enumerated for Bounds; grouping
// them per plane and fan-triangulating is what turns them into faces.
//
// The cheap form is still nameable (ConvexDistance.HalfSpaceBound) rather than deleted, since
// the query then costs O(triangles) Voronoi-region tests instead of one dot product per plane
// — 12 against 6 for a cube, and growing quadratically with the plane count for a polyhedron
// with many faces. Two estimators answering one question must both be nameable.

/// <summary>
/// How <see cref="Sdf.ConvexPolyhedron"/> answers OUTSIDE the solid. Inside, both settings
/// give the same exact value by the same arithmetic, so this names a cost/fidelity choice
/// about one half of space rather than two different solids.
/// </summary>
public enum ConvexDistance
{
    /// <summary>The true distance, as the minimum over the solid's own boundary triangles.
    /// Costs a Voronoi-region test per triangle.</summary>
    Exact,

    /// <summary>The maximum over the face half-spaces: a correct-sign <b>lower bound</b>,
    /// short wherever the nearest feature is an edge or a corner, at one dot product per
    /// plane. The engine's standing field contract, and the right choice for a polyhedron
    /// with many faces.</summary>
    HalfSpaceBound,
}

/// <summary>Which coordinate a <see cref="ConvexPrismSdf"/> extrudes along.</summary>
internal enum PrismAxis
{
    /// <summary>Section in the XZ plane, extruded along Y.</summary>
    Y,

    /// <summary>Section in the XY plane, extruded along Z.</summary>
    Z,
}

/// <summary>
/// A convex polygon swept along one axis, with flat caps. <b>Exact distance.</b> The polygon
/// must be convex and wound counter-clockwise in its own (u, v) plane; both are checked at
/// construction, because a concave or clockwise outline would silently invert the sign.
/// </summary>
internal sealed class ConvexPrismSdf : Sdf
{
    private readonly Vector2d[] _polygon;
    private readonly double _halfHeight;
    private readonly PrismAxis _axis;

    public ConvexPrismSdf(Vector2d[] polygon, double halfHeight, PrismAxis axis)
    {
        if (polygon.Length < 3)
            throw new ArgumentException("A prism section needs at least three vertices.", nameof(polygon));
        if (halfHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(halfHeight));
        RequireConvexCounterClockwise(polygon);
        _polygon = polygon;
        _halfHeight = halfHeight;
        _axis = axis;
    }

    /// <summary>
    /// Every turn must be a left turn (or straight). The comparison is against a threshold
    /// RELATIVE to the outline's own extent squared, because a cross product is an AREA and
    /// an absolute epsilon on one is a scale-dependent test — the lesson the decimator's
    /// normal guard cost this project once already.
    /// </summary>
    private static void RequireConvexCounterClockwise(Vector2d[] polygon)
    {
        double extent = 0;
        foreach (var p in polygon)
            extent = Math.Max(extent, Math.Max(Math.Abs(p.X), Math.Abs(p.Y)));
        double floor = -1e-13 * extent * extent;

        double area = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            var c = polygon[(i + 2) % polygon.Length];
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (cross < floor)
                throw new ArgumentException(
                    $"A prism section must be convex and counter-clockwise; the turn at vertex " +
                    $"{(i + 1) % polygon.Length} ({b.X:R}, {b.Y:R}) goes the other way.", nameof(polygon));
            area += a.X * b.Y - b.X * a.Y;
        }
        if (area <= 0)
            throw new ArgumentException(
                "A prism section must be wound counter-clockwise (it encloses no positive area).",
                nameof(polygon));
    }

    /// <summary>
    /// Exact signed distance to a convex polygon: the nearest point over its edges gives the
    /// magnitude, and the sign comes from the half-plane test, which for a convex outline is
    /// exact with no ray parity involved.
    /// </summary>
    private double SectionDistance(double u, double v)
    {
        double best = double.PositiveInfinity;
        bool inside = true;
        for (int i = 0; i < _polygon.Length; i++)
        {
            var a = _polygon[i];
            var b = _polygon[(i + 1) % _polygon.Length];
            double ex = b.X - a.X, ey = b.Y - a.Y;
            double wx = u - a.X, wy = v - a.Y;
            double t = Math.Clamp((wx * ex + wy * ey) / (ex * ex + ey * ey), 0, 1);
            double dx = wx - ex * t, dy = wy - ey * t;
            best = Math.Min(best, dx * dx + dy * dy);
            // Counter-clockwise winding: the interior is to the left of every edge.
            if (ex * wy - ey * wx < 0)
                inside = false;
        }
        return inside ? -Math.Sqrt(best) : Math.Sqrt(best);
    }

    public override double Evaluate(in Vector3d p)
    {
        double u, v, w;
        if (_axis == PrismAxis.Z)
        {
            u = p.X;
            v = p.Y;
            w = p.Z;
        }
        else
        {
            u = p.X;
            v = p.Z;
            w = p.Y;
        }

        double ds = SectionDistance(u, v);
        double da = Math.Abs(w) - _halfHeight;
        double outsideS = Math.Max(ds, 0);
        double outsideA = Math.Max(da, 0);
        return Math.Min(Math.Max(ds, da), 0)
            + Math.Sqrt(outsideS * outsideS + outsideA * outsideA);
    }

    public override Aabb Bounds
    {
        get
        {
            double uMin = double.PositiveInfinity, uMax = double.NegativeInfinity;
            double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;
            foreach (var p in _polygon)
            {
                uMin = Math.Min(uMin, p.X);
                uMax = Math.Max(uMax, p.X);
                vMin = Math.Min(vMin, p.Y);
                vMax = Math.Max(vMax, p.Y);
            }
            return _axis == PrismAxis.Z
                ? new Aabb((uMin, vMin, -_halfHeight), (uMax, vMax, _halfHeight))
                : new Aabb((uMin, -_halfHeight, vMin), (uMax, _halfHeight, vMax));
        }
    }

    /// <summary>
    /// Unrolls the per-edge loop — the whole reason a prism is worth compiling, since the
    /// polygon is fixed at construction and every edge's coefficients are constants. The
    /// running <c>inside</c> flag becomes a running <c>Min</c> of the same cross products:
    /// all of them are non-negative exactly when their minimum is, and each cross product is
    /// computed by the identical expression, so the branch is taken on the identical value.
    /// </summary>
    internal override Expression BuildExpression(SdfExpression e)
    {
        var (u, v, w) = _axis == PrismAxis.Z ? (e.X, e.Y, e.Z) : (e.X, e.Z, e.Y);
        var uu = e.Let(u);
        var vv = e.Let(v);

        Expression? best = null;
        Expression? side = null;
        for (int i = 0; i < _polygon.Length; i++)
        {
            var a = _polygon[i];
            var b = _polygon[(i + 1) % _polygon.Length];
            double ex = b.X - a.X, ey = b.Y - a.Y;
            var wx = e.Let(Expression.Subtract(uu, SdfExpression.Const(a.X)));
            var wy = e.Let(Expression.Subtract(vv, SdfExpression.Const(a.Y)));
            var t = e.Let(SdfExpression.Clamp(
                Expression.Divide(
                    Expression.Add(
                        Expression.Multiply(wx, SdfExpression.Const(ex)),
                        Expression.Multiply(wy, SdfExpression.Const(ey))),
                    SdfExpression.Const(ex * ex + ey * ey)),
                0, 1));
            var dx = Expression.Subtract(wx, Expression.Multiply(SdfExpression.Const(ex), t));
            var dy = Expression.Subtract(wy, Expression.Multiply(SdfExpression.Const(ey), t));
            var squared = Expression.Add(SdfExpression.Square(dx), SdfExpression.Square(dy));
            best = best is null ? squared : SdfExpression.Min(e.Let(best), squared);

            var cross = Expression.Subtract(
                Expression.Multiply(SdfExpression.Const(ex), wy),
                Expression.Multiply(SdfExpression.Const(ey), wx));
            side = side is null ? cross : SdfExpression.Min(e.Let(side), cross);
        }

        var root = e.Let(SdfExpression.Sqrt(best!));
        var section = e.Let(Expression.Condition(
            Expression.LessThan(e.Let(side!), SdfExpression.Const(0)),
            root, Expression.Negate(root)));

        var zero = SdfExpression.Const(0);
        var axial = e.Let(Expression.Subtract(
            SdfExpression.Abs(w), SdfExpression.Const(_halfHeight)));
        return Expression.Add(
            SdfExpression.Min(SdfExpression.Max(section, axial), zero),
            SdfExpression.Sqrt(Expression.Add(
                SdfExpression.Square(SdfExpression.Max(section, zero)),
                SdfExpression.Square(SdfExpression.Max(axial, zero)))));
    }
}

/// <summary>
/// The convex solid bounded by a set of half-spaces. See <see cref="Sdf.ConvexPolyhedron"/>
/// for the fidelity contract and the file remarks for why the outside is answered by the
/// boundary triangles rather than by the planes.
/// </summary>
internal sealed class ConvexPolyhedronSdf : Sdf
{
    private readonly Vector3d[] _normals;
    private readonly double[] _offsets;
    private readonly Aabb _bounds;

    /// <summary>The boundary triangulation, or empty under
    /// <see cref="ConvexDistance.HalfSpaceBound"/>.</summary>
    private readonly (Vector3d A, Vector3d B, Vector3d C)[] _triangles;

    private ConvexPolyhedronSdf(
        Vector3d[] normals, double[] offsets, in Aabb bounds,
        (Vector3d, Vector3d, Vector3d)[] triangles)
    {
        _normals = normals;
        _offsets = offsets;
        _bounds = bounds;
        _triangles = triangles;
    }

    public static Sdf Create(
        IReadOnlyList<(Vector3d Normal, double Offset)> halfSpaces, ConvexDistance distance)
    {
        ArgumentNullException.ThrowIfNull(halfSpaces);
        if (halfSpaces.Count < 4)
            throw new ArgumentException(
                "A bounded convex solid needs at least four half-spaces.", nameof(halfSpaces));

        var normals = new Vector3d[halfSpaces.Count];
        var offsets = new double[halfSpaces.Count];
        for (int i = 0; i < halfSpaces.Count; i++)
        {
            var (n, d) = halfSpaces[i];
            double length = n.Length;
            if (!(length > 0) || !double.IsFinite(d))
                throw new ArgumentException(
                    $"Half-space {i} has a degenerate normal or a non-finite offset.", nameof(halfSpaces));
            // Unit normals, so the max-of-half-spaces value is a distance rather than a
            // scaled one; the offset scales with the normal.
            normals[i] = n / length;
            offsets[i] = d / length;
        }

        var vertices = EnumerateVertices(normals, offsets);
        var bounds = Aabb.Empty;
        foreach (var v in vertices)
            bounds = bounds.Union(v);
        if (bounds.IsEmpty)
            throw new ArgumentException(
                "These half-spaces do not enclose a bounded solid: no point satisfies all of them " +
                "while lying on three of the planes. Add the planes that close it off, or use " +
                "Sdf.Intersection over half-spaces and supply the sampling region explicitly.");

        var triangles = distance == ConvexDistance.Exact
            ? Triangulate(normals, offsets, vertices, bounds)
            : [];
        return new ConvexPolyhedronSdf(normals, offsets, bounds, triangles);
    }

    /// <summary>
    /// The vertices of a bounded half-space intersection are exactly the points where three
    /// of its planes meet and every other plane is satisfied, so enumerating the triples
    /// gives them directly — O(n³) in the plane count, which is nothing at the handful of
    /// planes a primitive carries and is worth paying because it is what makes this node
    /// polygonizable where <c>Sdf.Intersection</c> over the same half-spaces reports infinity.
    /// <para>The containment test admits a point on a plane at the 1e-9 weld tier, since a
    /// vertex lies exactly ON the three planes that produced it and on any further plane
    /// through it (a pyramid's apex meets four).</para>
    /// </summary>
    private static List<Vector3d> EnumerateVertices(Vector3d[] normals, double[] offsets)
    {
        var vertices = new List<Vector3d>();
        int n = normals.Length;
        double slack = Tolerance.Default.Linear;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                for (int k = j + 1; k < n; k++)
                {
                    if (!TrySolve(normals[i], offsets[i], normals[j], offsets[j],
                                  normals[k], offsets[k], out var p))
                        continue;
                    bool inside = true;
                    for (int m = 0; m < n && inside; m++)
                        inside = normals[m].Dot(p) - offsets[m] <= slack;
                    if (inside)
                        vertices.Add(p);
                }
            }
        }
        return vertices;
    }

    /// <summary>
    /// The boundary as triangles: the vertices ON each plane, ordered by angle about their own
    /// centroid in that plane, fanned from the first. Winding is irrelevant — the distance to a
    /// triangle does not read it — so no orientation convention has to be established.
    /// <para>
    /// Every threshold here is RELATIVE to the solid's own extent, because both are areas or
    /// lengths and an absolute epsilon on one is a scale-dependent test (the recorded
    /// decimator lesson). A vertex where four or more planes meet is produced once per triple,
    /// so the list is welded first; a plane carrying fewer than three distinct vertices bounds
    /// nothing and contributes none, and a collinear run inside a plane produces slivers whose
    /// nearest point is a boundary EDGE, so dropping them cannot raise the minimum.
    /// </para>
    /// </summary>
    private static (Vector3d, Vector3d, Vector3d)[] Triangulate(
        Vector3d[] normals, double[] offsets, List<Vector3d> vertices, in Aabb bounds)
    {
        var size = bounds.Max - bounds.Min;
        double extent = Math.Max(size.X, Math.Max(size.Y, size.Z));
        double weld = 1e-9 * extent;
        double sliver = 1e-13 * extent * extent;

        var welded = new List<Vector3d>();
        foreach (var v in vertices)
        {
            bool seen = false;
            foreach (var w in welded)
            {
                if ((w - v).Length <= weld)
                {
                    seen = true;
                    break;
                }
            }
            if (!seen)
                welded.Add(v);
        }

        var triangles = new List<(Vector3d, Vector3d, Vector3d)>();
        var onPlane = new List<Vector3d>();
        for (int m = 0; m < normals.Length; m++)
        {
            onPlane.Clear();
            foreach (var v in welded)
            {
                if (Math.Abs(normals[m].Dot(v) - offsets[m]) <= weld)
                    onPlane.Add(v);
            }
            if (onPlane.Count < 3)
                continue;

            var centroid = Vector3d.Zero;
            foreach (var v in onPlane)
                centroid += v;
            centroid /= onPlane.Count;

            // The one arbitrary-perpendicular convention in the codebase; which direction it
            // picks decides only where the angular sort starts, never the triangulation.
            var u = normals[m].ArbitraryPerpendicular(Tolerance.Default);
            var w2 = normals[m].Cross(u);
            var ordered = onPlane
                .OrderBy(v => Math.Atan2((v - centroid).Dot(w2), (v - centroid).Dot(u)))
                .ToArray();

            for (int t = 1; t + 1 < ordered.Length; t++)
            {
                var (a, b, c) = (ordered[0], ordered[t], ordered[t + 1]);
                if ((b - a).Cross(c - a).LengthSquared > sliver * sliver)
                    triangles.Add((a, b, c));
            }
        }

        if (triangles.Count == 0)
            throw new ArgumentException(
                "These half-spaces enclose no boundary faces: the enumerated vertices do not lie " +
                "three-or-more to a plane, so the solid is degenerate.");
        return [.. triangles];
    }

    /// <summary>Cramer's rule on the three plane equations; a determinant that is not
    /// meaningfully non-zero means the planes share a direction and meet in no point.
    /// The threshold is relative to the rows' own magnitudes (all unit here), so it is the
    /// scale-free tier rather than a length.</summary>
    private static bool TrySolve(
        in Vector3d n0, double d0, in Vector3d n1, double d1, in Vector3d n2, double d2,
        out Vector3d point)
    {
        var c12 = n1.Cross(n2);
        double det = n0.Dot(c12);
        if (Math.Abs(det) < 1e-12)
        {
            point = default;
            return false;
        }
        point = (c12 * d0 + n2.Cross(n0) * d1 + n0.Cross(n1) * d2) / det;
        return true;
    }

    /// <summary>
    /// The half-space max first, because it settles the sign AND is already the exact answer
    /// inside — so the triangle scan is paid only outside, and the interior value is
    /// bit-identical under both <see cref="ConvexDistance"/> settings.
    /// </summary>
    public override double Evaluate(in Vector3d p)
    {
        double outside = double.NegativeInfinity;
        for (int i = 0; i < _normals.Length; i++)
            outside = Math.Max(outside, _normals[i].Dot(p) - _offsets[i]);
        if (_triangles.Length == 0 || outside <= 0)
            return outside;

        double best = double.PositiveInfinity;
        foreach (var (a, b, c) in _triangles)
            best = Math.Min(best, (Distance3d.ClosestPointOnTriangle(p, a, b, c) - p).LengthSquared);
        return Math.Sqrt(best);
    }

    public override Aabb Bounds => _bounds;

    /// <summary>
    /// Only the half-space form compiles. The exact form's outside branch is a minimum over a
    /// Voronoi-region test per triangle, which is a loop rather than an expression, so it takes
    /// the base class's call-back-into-Evaluate fallback — always exact, simply not flattened.
    /// </summary>
    internal override Expression BuildExpression(SdfExpression e)
    {
        if (_triangles.Length > 0)
            return base.BuildExpression(e);

        Expression body = SdfExpression.Const(double.NegativeInfinity);
        for (int i = 0; i < _normals.Length; i++)
        {
            var plane = Expression.Subtract(
                SdfExpression.Add(
                    Expression.Multiply(SdfExpression.Const(_normals[i].X), e.X),
                    Expression.Multiply(SdfExpression.Const(_normals[i].Y), e.Y),
                    Expression.Multiply(SdfExpression.Const(_normals[i].Z), e.Z)),
                SdfExpression.Const(_offsets[i]));
            body = SdfExpression.Max(e.Let(body), plane);
        }
        return body;
    }
}
