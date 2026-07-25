using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Two tangent-continuous circular arcs (either of which may degenerate to a straight
/// segment) joining a start point+tangent to an end point+tangent — a "biarc".
/// </summary>
/// <remarks>
/// The two data points and both end tangents are reproduced exactly in the ALGEBRA
/// (measured: within ~1e-15 of the radius, i.e. the round-off of one atan2/cos pair);
/// all of the fit's freedom, and therefore all of its approximation error, is
/// concentrated at the interior <see cref="Joint"/>. That placement is deliberate: a
/// chain of biarcs shares only its data points with its neighbours, so consecutive
/// pieces hand over their shared point at round-off rather than at the fit tolerance —
/// the approximation error never lands on a junction that has to weld.
/// </remarks>
public sealed class BiArc2d
{
    /// <summary>First piece, from the start point to <see cref="Joint"/>.</summary>
    public Curve2d First { get; }

    /// <summary>Second piece, from <see cref="Joint"/> to the end point.</summary>
    public Curve2d Second { get; }

    /// <summary>The tangent-continuous junction between the two pieces.</summary>
    public Vector2d Joint { get; }

    /// <summary>Distance along the start tangent to the joint's construction point.</summary>
    public double D1 { get; }

    /// <summary>Distance along the end tangent to the joint's construction point.</summary>
    public double D2 { get; }

    internal BiArc2d(Curve2d first, Curve2d second, in Vector2d joint, double d1, double d2)
    {
        First = first;
        Second = second;
        Joint = joint;
        D1 = d1;
        D2 = d2;
    }

    /// <summary>The two pieces in traversal order.</summary>
    public IReadOnlyList<Curve2d> Curves => [First, Second];

    /// <summary>Distance from a point to the biarc (the nearer of the two pieces).</summary>
    public double DistanceTo(in Vector2d point) => Math.Min(First.DistanceTo(point), Second.DistanceTo(point));

    /// <summary>Total arc length of both pieces.</summary>
    public double Length => PieceLength(First) + PieceLength(Second);

    private static double PieceLength(Curve2d curve) => curve switch
    {
        Arc2d arc => arc.Length,
        Line2d line => (line.End - line.Start).Length,
        _ => curve.ArcLength(),
    };
}

/// <summary>Why a biarc fit could not be produced.</summary>
public enum BiArcFitStatus
{
    /// <summary>A biarc was produced.</summary>
    Success,

    /// <summary>The two points coincide, so no biarc is defined.</summary>
    CoincidentPoints,

    /// <summary>A tangent had zero length.</summary>
    DegenerateTangent,

    /// <summary>
    /// Parallel tangents with the chord pointing away from them: the biarc's free
    /// parameter would have to be infinite and no finite pair of arcs exists.
    /// </summary>
    NoFiniteSolution,

    /// <summary>The sample points are not planar to within the requested tolerance (3D fits only).</summary>
    NotPlanar,
}

/// <summary>
/// Biarc fitting: two tangent-continuous arcs through a point+tangent pair, and
/// tolerance-driven biarc chains through a polyline.
/// </summary>
/// <remarks>
/// <para>
/// Ported in spirit from Ryan Juckett's biarc interpolation (the source of g3's
/// <c>BiArcFit2</c>), with two corrections. First, the free parameter is computed in the
/// numerically stable form <c>d = |v|² / (√disc + v·t)</c> rather than
/// <c>(√disc − v·t)/denom</c>: the two are algebraically identical, but the stable form
/// stays accurate as <c>denom = 2 − 2·t₁·t₂</c> approaches zero and *reduces exactly to
/// the equal-tangent special case at denom = 0*, so the original's epsilon test on a
/// squared quantity (`|t₁+t₂|² ≈ 4`, which silently picks a branch at an arbitrary
/// angular threshold of √ε) disappears entirely. Second, the original's semicircle
/// branch assigns <c>arc1</c> twice, leaving the second arc uninitialized — that
/// configuration is handled explicitly here.
/// </para>
/// <para>
/// NOTHING in the kernel fits biarcs implicitly. Marching-tracer output stays a
/// <see cref="PolylineCurve3d"/> unless a caller opts in through
/// <see cref="TryFitPolyline(IReadOnlyList{Vector3d}, double, out BiArcChain3d, out BiArcFitStatus)"/>,
/// and every fit reports the deviation it actually achieved so the caller — not the
/// kernel — decides whether an approximation is acceptable.
/// </para>
/// </remarks>
public static class BiArcFit
{
    /// <summary>
    /// The standard biarc through (<paramref name="startPoint"/>,
    /// <paramref name="startTangent"/>) and (<paramref name="endPoint"/>,
    /// <paramref name="endTangent"/>), with the free parameter chosen so the two tangent
    /// distances are equal (the "middle" fit).
    /// </summary>
    /// <exception cref="ArgumentException">The configuration admits no finite biarc; use
    /// <see cref="TryFit"/> to test instead of throwing.</exception>
    public static BiArc2d Fit(in Vector2d startPoint, in Vector2d startTangent,
                              in Vector2d endPoint, in Vector2d endTangent)
    {
        var status = TryFit(startPoint, startTangent, endPoint, endTangent, out var result);
        if (status != BiArcFitStatus.Success)
            throw new ArgumentException($"No biarc exists for this configuration ({status}).");
        return result!;
    }

    /// <summary>
    /// Non-throwing <see cref="Fit"/>. Tangents need not be unit length; they are
    /// normalized internally.
    /// </summary>
    public static BiArcFitStatus TryFit(in Vector2d startPoint, in Vector2d startTangent,
                                        in Vector2d endPoint, in Vector2d endTangent,
                                        out BiArc2d? result)
    {
        result = null;
        var chord = endPoint - startPoint;
        double chordLength = chord.Length;
        // Exact-zero guards (scale-free): degenerate input, not a tolerance decision.
        if (!(chordLength > 0))
            return BiArcFitStatus.CoincidentPoints;
        if (!(startTangent.LengthSquared > 0) || !(endTangent.LengthSquared > 0))
            return BiArcFitStatus.DegenerateTangent;

        var t1 = startTangent / startTangent.Length;
        var t2 = endTangent / endTangent.Length;
        var tangentSum = t1 + t2;

        // The biarc's free parameter d solves (2 − 2·t₁·t₂)·d² + 2(v·t)·d − |v|² = 0.
        // Written as d = |v|² / (√disc + v·t) it needs no branch on the leading
        // coefficient: at t₁ = t₂ the expression collapses to |v|²/(4·v·t₁), which is
        // exactly the equal-tangent formula.
        double chordLengthSquared = chord.LengthSquared;
        double leading = 2 - 2 * t1.Dot(t2);
        double chordDotSum = chord.Dot(tangentSum);
        double discriminant = chordDotSum * chordDotSum + leading * chordLengthSquared;
        double denominator = Math.Sqrt(Math.Max(discriminant, 0)) + chordDotSum;

        // Relative guard: `denominator` is a length, compared against the chord length —
        // dimensionally consistent and therefore scale-free (an absolute epsilon on a
        // length term is the defect this repo has hit five times).
        if (denominator <= chordLength * 1e-12)
        {
            // Parallel tangents perpendicular to the chord: the classic two-semicircle
            // U-turn, whose d is infinite. (g3's port of this branch is broken — it
            // assigns its first arc twice and never sets the second.)
            if (Math.Abs(chord.Dot(t1)) <= chordLength * 1e-12 && leading <= 1e-12)
            {
                // Both arcs are semicircles of radius |v|/4 meeting at the chord midpoint.
                return Build(startPoint, t1, endPoint, t2, startPoint + chord * 0.5,
                    double.PositiveInfinity, double.PositiveInfinity, out result);
            }
            return BiArcFitStatus.NoFiniteSolution;
        }

        double d = chordLengthSquared / denominator;
        var junction = (startPoint + endPoint + (t1 - t2) * d) * 0.5;
        return Build(startPoint, t1, endPoint, t2, junction, d, d, out result);
    }

    /// <summary>
    /// Biarc with the first tangent distance pinned to <paramref name="d1"/>. The
    /// equal-distance fit from <see cref="TryFit"/> is the natural default; varying d1
    /// over roughly [0, 2·d] trades length between the two arcs and often fits a sampled
    /// curve better. Negative or wildly large d1 produce nonsense and are rejected.
    /// </summary>
    public static BiArcFitStatus TryFit(in Vector2d startPoint, in Vector2d startTangent,
                                        in Vector2d endPoint, in Vector2d endTangent,
                                        double d1, out BiArc2d? result)
    {
        result = null;
        var chord = endPoint - startPoint;
        double chordLength = chord.Length;
        if (!(chordLength > 0))
            return BiArcFitStatus.CoincidentPoints;
        if (!(startTangent.LengthSquared > 0) || !(endTangent.LengthSquared > 0))
            return BiArcFitStatus.DegenerateTangent;
        if (!(d1 > 0))
            return BiArcFitStatus.NoFiniteSolution;

        var t1 = startTangent / startTangent.Length;
        var t2 = endTangent / endTangent.Length;

        double dot = t1.Dot(t2);
        double denominator = chord.Dot(t2) - d1 * (dot - 1.0);
        // Relative guard again: `denominator` carries a length.
        if (Math.Abs(denominator) <= chordLength * 1e-12)
            return BiArcFitStatus.NoFiniteSolution;

        double d2 = (0.5 * chord.LengthSquared - d1 * chord.Dot(t1)) / denominator;
        if (!(d2 > 0))
            return BiArcFitStatus.NoFiniteSolution;

        var junction = ((t1 - t2) * (d1 * d2) + endPoint * d1 + startPoint * d2) / (d1 + d2);
        return Build(startPoint, t1, endPoint, t2, junction, d1, d2, out result);
    }

    /// <summary>
    /// Builds the two pieces around a computed joint. The second arc is built FROM the
    /// end point (backwards) and then reversed, so the end point and end tangent are
    /// exact and the construction round-off lands on the interior joint — where nothing
    /// has to weld.
    /// </summary>
    private static BiArcFitStatus Build(in Vector2d startPoint, in Vector2d t1,
                                        in Vector2d endPoint, in Vector2d t2,
                                        in Vector2d junction, double d1, double d2,
                                        out BiArc2d? result)
    {
        result = null;
        if (junction.AreEqual(startPoint, Tolerance.Default) || junction.AreEqual(endPoint, Tolerance.Default))
        {
            // A joint sitting on a data point degenerates one arc away; the caller can
            // subdivide instead of receiving a zero-length piece.
            return BiArcFitStatus.NoFiniteSolution;
        }

        var first = Arc2d.FromPointAndTangent(startPoint, t1, junction);
        var backward = Arc2d.FromPointAndTangent(endPoint, -t2, junction);
        var second = backward switch
        {
            Arc2d arc => (Curve2d)arc.Reversed(),
            Line2d line => new Line2d(line.End, line.Start),
            _ => throw new InvalidOperationException("Unexpected biarc piece type."),
        };
        result = new BiArc2d(first, second, junction, d1, d2);
        return BiArcFitStatus.Success;
    }

    // ------------------------------------------------------------------ 2D polylines

    /// <summary>
    /// Fits a chain of biarcs to a polyline so that every input point lies within
    /// <paramref name="tolerance"/> of the result.
    /// </summary>
    /// <remarks>
    /// Tangents at the data points are estimated from the circle through each point and
    /// its two neighbours — EXACT for points sampled off a circle or a line, which is
    /// what marching intersection curves usually are — falling back to the chord
    /// direction when the three points are collinear to within a relative area test.
    /// The chain is then produced by recursive bisection: fit one biarc across a span,
    /// measure every interior sample against it, and split at the worst sample if the
    /// tolerance is missed.
    /// </remarks>
    public static BiArcChain2d FitPolyline(IReadOnlyList<Vector2d> points, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
            throw new ArgumentException("A polyline needs at least 2 points.", nameof(points));
        if (!(tolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(tolerance));

        var tangents = EstimateTangents(points);
        var curves = new List<Curve2d>();
        double worst = 0;
        Recurse(0, points.Count - 1);
        return new BiArcChain2d(curves, worst);

        void Recurse(int from, int to)
        {
            if (to - from >= 2 &&
                TryFit(points[from], tangents[from], points[to], tangents[to], out var biarc) == BiArcFitStatus.Success)
            {
                // The metric covers the span's ENDPOINTS too (so the reported deviation
                // includes each piece's own endpoint reproduction), but only interior
                // samples are eligible split points — otherwise a span could try to
                // subdivide at its own boundary and never terminate.
                double deviation = 0;
                int worstIndex = from + 1;
                double worstInterior = -1;
                for (int i = from; i <= to; i++)
                {
                    double d = biarc!.DistanceTo(points[i]);
                    deviation = Math.Max(deviation, d);
                    if (i > from && i < to && d > worstInterior)
                    {
                        worstInterior = d;
                        worstIndex = i;
                    }
                }
                if (deviation <= tolerance)
                {
                    worst = Math.Max(worst, deviation);
                    curves.Add(biarc!.First);
                    curves.Add(biarc.Second);
                    return;
                }
                Recurse(from, worstIndex);
                Recurse(worstIndex, to);
                return;
            }

            if (to - from == 1)
            {
                // A single chord: exact through both endpoints, nothing to measure.
                curves.Add(new Line2d(points[from], points[to]));
                return;
            }

            // The span admits no biarc (coincident points, anti-parallel tangents).
            // Halve it rather than silently emitting a chord across it.
            int mid = (from + to) / 2;
            Recurse(from, mid);
            Recurse(mid, to);
        }
    }

    /// <summary>
    /// Tangent at every polyline point from the circle through it and its neighbours
    /// (exact for circular and straight data), the end points using the same circle as
    /// their inner neighbour.
    /// </summary>
    private static Vector2d[] EstimateTangents(IReadOnlyList<Vector2d> points)
    {
        int n = points.Count;
        var tangents = new Vector2d[n];
        if (n == 2)
        {
            var direction = (points[1] - points[0]).Normalized();
            tangents[0] = tangents[1] = direction;
            return tangents;
        }

        for (int i = 0; i < n; i++)
        {
            int a = Math.Clamp(i - 1, 0, n - 3);
            int b = a + 1, c = a + 2;
            tangents[i] = CircleTangent(points[a], points[b], points[c], points[i]);
        }
        return tangents;
    }

    /// <summary>
    /// Unit tangent at <paramref name="at"/> of the circle through a, b, c, oriented
    /// a → c. Collinear triples (a RELATIVE area test — twice the triangle area against
    /// the product of the three side lengths, which is dimensionless) fall back to the
    /// a → c chord.
    /// </summary>
    private static Vector2d CircleTangent(in Vector2d a, in Vector2d b, in Vector2d c, in Vector2d at)
    {
        var chord = c - a;
        double chordLength = chord.Length;
        if (!(chordLength > 0))
            return Vector2d.UnitX;

        var ab = b - a;
        var bc = c - b;
        double twiceArea = ab.Cross(bc);
        double lengths = ab.Length * bc.Length * chordLength;
        // Relative degeneracy: |2·area| / (|ab||bc||ca|) is 1/(2R), so this is a
        // curvature test against the triple's own size — scale-free by construction.
        if (lengths <= 0 || Math.Abs(twiceArea) <= lengths * 1e-12)
            return chord / chordLength;

        // Circumcentre by the standard determinant form, then the tangent is the radial
        // rotated a quarter turn in the triple's turn direction.
        double aa = a.LengthSquared, bb = b.LengthSquared, cc = c.LengthSquared;
        double ux = (aa * (b.Y - c.Y) + bb * (c.Y - a.Y) + cc * (a.Y - b.Y)) / (2 * twiceArea);
        double uy = (aa * (c.X - b.X) + bb * (a.X - c.X) + cc * (b.X - a.X)) / (2 * twiceArea);
        var center = new Vector2d(ux, uy);

        var radial = at - center;
        if (!(radial.LengthSquared > 0))
            return chord / chordLength;
        var tangent = radial.Perpendicular / radial.Length;
        return twiceArea > 0 ? tangent : -tangent;
    }

    // ------------------------------------------------------------------ 3D polylines

    /// <summary>
    /// Fits a biarc chain to a PLANAR 3D polyline — the marching tracer's output shape —
    /// returning exact <see cref="Line3d"/> and rational-arc <see cref="NurbsCurve"/>
    /// pieces suitable for STEP export and for lighter B-Rep edges.
    /// </summary>
    /// <remarks>
    /// Planarity is required and CHECKED: the best-fit plane's maximum residual must be
    /// within <paramref name="tolerance"/>, otherwise the fit is refused
    /// (<see cref="BiArcFitStatus.NotPlanar"/>) rather than silently flattening a
    /// space curve. The reported <see cref="BiArcChain3d.MaxDeviation"/> is the true 3D
    /// distance — √(in-plane² + out-of-plane²) — so it accounts for the flattening as
    /// well as the arc fit.
    /// </remarks>
    public static BiArcFitStatus TryFitPolyline(IReadOnlyList<Vector3d> points, double tolerance,
                                                out BiArcChain3d result)
    {
        ArgumentNullException.ThrowIfNull(points);
        result = default!;
        if (points.Count < 2)
            throw new ArgumentException("A polyline needs at least 2 points.", nameof(points));
        if (!(tolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(tolerance));

        var frame = Fitting3d.FitPlane(points);
        var planar = new Vector2d[points.Count];
        var offsets = new double[points.Count];
        double planarity = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var local = frame.ToLocal(points[i]);
            planar[i] = new Vector2d(local.X, local.Y);
            offsets[i] = local.Z;
            planarity = Math.Max(planarity, Math.Abs(local.Z));
        }
        if (planarity > tolerance)
            return BiArcFitStatus.NotPlanar;

        // Fit in-plane against the residual budget left by the flattening: the reported
        // 3D deviation is the hypotenuse of the two, so budgeting them independently
        // would quietly overshoot the caller's tolerance.
        double inPlaneBudget = Math.Sqrt(Math.Max(tolerance * tolerance - planarity * planarity, 0));
        // Exact-zero guard: a polyline exactly at the tolerance leaves no budget, so ask
        // for the tightest fit the recursion can give instead of dividing by zero.
        var chain = FitPolyline(planar, inPlaneBudget > 0 ? inPlaneBudget : tolerance * 1e-3);

        var curves = new List<Curve3d>(chain.Curves.Count);
        foreach (var curve in chain.Curves)
            curves.Add(Lift(curve, frame));

        // Honest metric: measure every ORIGINAL point against the fitted planar chain and
        // add its out-of-plane offset in quadrature.
        double deviation = 0;
        for (int i = 0; i < planar.Length; i++)
        {
            double inPlane = double.PositiveInfinity;
            foreach (var curve in chain.Curves)
                inPlane = Math.Min(inPlane, curve.DistanceTo(planar[i]));
            deviation = Math.Max(deviation, Math.Sqrt(inPlane * inPlane + offsets[i] * offsets[i]));
        }

        result = new BiArcChain3d(curves, deviation, planarity, frame);
        return BiArcFitStatus.Success;
    }

    /// <summary>Places a fitted 2D piece on the fit plane as exact 3D geometry.</summary>
    private static Curve3d Lift(Curve2d curve, in Frame3d frame)
    {
        switch (curve)
        {
            case Line2d line:
                return new Line3d(
                    frame.ToWorld(new Vector3d(line.Start.X, line.Start.Y, 0)),
                    frame.ToWorld(new Vector3d(line.End.X, line.End.Y, 0)));

            case Arc2d arc:
            {
                var center = frame.ToWorld(new Vector3d(arc.Center.X, arc.Center.Y, 0));
                // A clockwise 2D arc becomes a counter-clockwise arc about the FLIPPED
                // in-plane Y axis, which keeps NurbsCurve.Arc's positive-sweep contract
                // without ever reversing (and therefore re-rounding) the geometry.
                bool forward = arc.SweepAngle > 0;
                var x = frame.X;
                var y = forward ? frame.Y : -frame.Y;
                double start = forward ? arc.StartAngle : -arc.StartAngle;
                double sweep = Math.Abs(arc.SweepAngle);
                if (arc.IsClosed)
                    return new Circle3d(center, x, y, arc.Radius);
                return NurbsCurve.Arc(center, x, y, arc.Radius, start, start + sweep);
            }

            default:
                throw new InvalidOperationException($"Unexpected fitted piece {curve.GetType().Name}.");
        }
    }
}

/// <summary>A fitted chain of 2D arcs and segments plus the deviation it achieved.</summary>
/// <param name="Curves">The pieces in traversal order (each is an <see cref="Arc2d"/> or a <see cref="Line2d"/>).</param>
/// <param name="MaxDeviation">
/// The largest distance from an INPUT SAMPLE to the chain. It measures the given points
/// only: it says nothing about how far the fit strays from the true curve between them,
/// which is a property of the sampling, not of the fit.
/// </param>
public sealed record BiArcChain2d(IReadOnlyList<Curve2d> Curves, double MaxDeviation)
{
    /// <summary>How many pieces are genuine arcs.</summary>
    public int ArcCount => Curves.Count(c => c is Arc2d);

    /// <summary>How many pieces degenerated to straight segments.</summary>
    public int SegmentCount => Curves.Count(c => c is Line2d);
}

/// <summary>A fitted chain of planar 3D arcs and segments plus the deviation it achieved.</summary>
/// <param name="Curves">
/// The pieces in traversal order: <see cref="Line3d"/> for straight runs, exact rational
/// <see cref="NurbsCurve"/> arcs (or a <see cref="Circle3d"/> for a full turn) otherwise
/// — all STEP-exportable analytic geometry.
/// </param>
/// <param name="MaxDeviation">
/// The largest 3D distance from an input sample to the chain, INCLUDING the out-of-plane
/// flattening error. Compare this against your own tolerance before adopting the fit.
/// </param>
/// <param name="Planarity">The largest out-of-plane residual of the input against the fit plane.</param>
/// <param name="Plane">The best-fit plane the arcs live on.</param>
public sealed record BiArcChain3d(
    IReadOnlyList<Curve3d> Curves, double MaxDeviation, double Planarity, Frame3d Plane)
{
    /// <summary>How many pieces are arcs rather than straight segments.</summary>
    public int ArcCount => Curves.Count(c => c is not Line3d);

    /// <summary>How many pieces are straight segments.</summary>
    public int SegmentCount => Curves.Count(c => c is Line3d);
}
