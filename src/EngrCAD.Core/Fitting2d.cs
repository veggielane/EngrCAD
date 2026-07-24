namespace EngrCAD.Core;

/// <summary>
/// Oriented 2D rectangle: center, unit axes (AxisY is AxisX rotated +90°), and half
/// extents. Produced by <see cref="Fitting2d.MinAreaBox"/>.
/// </summary>
public readonly struct OrientedBox2d
{
    public Vector2d Center { get; }
    /// <summary>Unit direction of the first box side.</summary>
    public Vector2d AxisX { get; }
    /// <summary>Half sizes along (AxisX, AxisY).</summary>
    public Vector2d HalfExtents { get; }

    public OrientedBox2d(in Vector2d center, in Vector2d axisX, in Vector2d halfExtents)
    {
        Center = center;
        AxisX = axisX;
        HalfExtents = halfExtents;
    }

    /// <summary>AxisX rotated +90° (counter-clockwise).</summary>
    public Vector2d AxisY => AxisX.Perpendicular;

    public double Area => 4 * HalfExtents.X * HalfExtents.Y;

    /// <summary>Corner i ∈ [0, 4), counter-clockwise starting at (+X, +Y).</summary>
    public Vector2d Corner(int i)
    {
        double sx = i is 0 or 3 ? 1 : -1;
        double sy = i is 0 or 1 ? 1 : -1;
        return Center + AxisX * (sx * HalfExtents.X) + AxisY * (sy * HalfExtents.Y);
    }

    public bool Contains(in Vector2d point, in Tolerance tolerance)
    {
        var d = point - Center;
        return Math.Abs(d.Dot(AxisX)) <= HalfExtents.X + tolerance.Linear &&
               Math.Abs(d.Dot(AxisY)) <= HalfExtents.Y + tolerance.Linear;
    }
}

/// <summary>Circle produced by <see cref="Fitting2d.MinCircle"/>.</summary>
public readonly struct BoundingCircle2d
{
    public Vector2d Center { get; }
    public double Radius { get; }

    public BoundingCircle2d(in Vector2d center, double radius)
    {
        Center = center;
        Radius = radius;
    }

    public bool Contains(in Vector2d point, in Tolerance tolerance) =>
        point.DistanceTo(Center) <= Radius + tolerance.Linear;
}

/// <summary>
/// Minimal bounding fits in 2D: min-area oriented box (rotating-calipers family over the
/// convex hull) and min enclosing circle (Welzl). geometry3Sharp counterparts:
/// ContMinBox2, ContMinCircle2.
/// </summary>
public static class Fitting2d
{
    /// <summary>
    /// Minimum-area oriented bounding box of a point set. Uses the calipers theorem —
    /// some side of the optimal box is collinear with a convex hull edge — and evaluates
    /// every hull-edge-aligned box over the hull vertices (O(h²) in the hull size h,
    /// which is small for CAD data; simpler and more robust than the classic
    /// four-pointer caliper rotation). Degenerate inputs yield degenerate boxes
    /// (coincident → zero extents, collinear → zero width).
    /// </summary>
    public static OrientedBox2d MinAreaBox(IReadOnlyList<Vector2d> points)
    {
        var hull = ConvexHull2.Compute(points);
        if (hull.Length == 1)
            return new OrientedBox2d(hull[0], Vector2d.UnitX, Vector2d.Zero);
        if (hull.Length == 2)
        {
            var axis = (hull[1] - hull[0]).Normalized();
            return new OrientedBox2d(
                (hull[0] + hull[1]) * 0.5, axis, (hull[0].DistanceTo(hull[1]) * 0.5, 0));
        }

        double bestArea = double.PositiveInfinity;
        OrientedBox2d best = default;
        for (int e = 0; e < hull.Length; e++)
        {
            var edge = hull[(e + 1) % hull.Length] - hull[e];
            if (!edge.TryNormalize(Tolerance.Default, out var u))
                continue;
            var v = u.Perpendicular;

            double minU = double.PositiveInfinity, maxU = double.NegativeInfinity;
            double minV = double.PositiveInfinity, maxV = double.NegativeInfinity;
            foreach (var p in hull)
            {
                double pu = p.Dot(u), pv = p.Dot(v);
                minU = Math.Min(minU, pu);
                maxU = Math.Max(maxU, pu);
                minV = Math.Min(minV, pv);
                maxV = Math.Max(maxV, pv);
            }

            double area = (maxU - minU) * (maxV - minV);
            if (area < bestArea)
            {
                bestArea = area;
                var center = u * ((minU + maxU) * 0.5) + v * ((minV + maxV) * 0.5);
                best = new OrientedBox2d(center, u, ((maxU - minU) * 0.5, (maxV - minV) * 0.5));
            }
        }
        return best;
    }

    /// <summary>
    /// Minimum enclosing circle of a point set — Welzl's algorithm in the iterative
    /// move-to-boundary form (expected O(n) after a deterministic seeded shuffle, no
    /// deep recursion). The result is determined by at most 3 support points; every
    /// input point is inside within a relative 1e-9 slack.
    /// </summary>
    public static BoundingCircle2d MinCircle(IReadOnlyList<Vector2d> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
            throw new ArgumentException("Minimum circle of an empty point set.", nameof(points));

        // Deterministic shuffle: same input, same result, expected-linear behavior.
        var p = new Vector2d[points.Count];
        for (int i = 0; i < p.Length; i++)
            p[i] = points[i];
        var random = new Random(0x5EED2D);
        for (int i = p.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }

        var circle = new BoundingCircle2d(p[0], 0);
        for (int i = 1; i < p.Length; i++)
        {
            if (Inside(circle, p[i]))
                continue;
            // p[i] must lie on the boundary of the minimal circle of p[0..i].
            circle = new BoundingCircle2d(p[i], 0);
            for (int j = 0; j < i; j++)
            {
                if (Inside(circle, p[j]))
                    continue;
                // p[i] and p[j] both on the boundary.
                circle = Diameter(p[i], p[j]);
                for (int k = 0; k < j; k++)
                {
                    if (!Inside(circle, p[k]))
                        circle = Circumcircle(p[i], p[j], p[k]);
                }
            }
        }
        return circle;
    }

    private static bool Inside(in BoundingCircle2d circle, in Vector2d point)
    {
        double r = circle.Radius;
        return (point - circle.Center).LengthSquared <= r * r + 1e-9 * Math.Max(1e-9, r * r);
    }

    private static BoundingCircle2d Diameter(in Vector2d a, in Vector2d b)
    {
        var center = (a + b) * 0.5;
        return new BoundingCircle2d(center, center.DistanceTo(a));
    }

    private static BoundingCircle2d Circumcircle(in Vector2d a, in Vector2d b, in Vector2d c)
    {
        var ab = b - a;
        var ac = c - a;
        double det = 2 * ab.Cross(ac);
        // Collinear support: fall back to the widest two-point circle.
        double scale = Math.Max(ab.LengthSquared, ac.LengthSquared);
        if (Math.Abs(det) <= 1e-14 * scale)
        {
            var wide = Diameter(a, b);
            var candidate = Diameter(a, c);
            if (candidate.Radius > wide.Radius)
                wide = candidate;
            candidate = Diameter(b, c);
            return candidate.Radius > wide.Radius ? candidate : wide;
        }

        double abLen2 = ab.LengthSquared;
        double acLen2 = ac.LengthSquared;
        var offset = new Vector2d(
            (ac.Y * abLen2 - ab.Y * acLen2) / det,
            (ab.X * acLen2 - ac.X * abLen2) / det);
        return new BoundingCircle2d(a + offset, offset.Length);
    }
}
