namespace EngrCAD.Core.Geometry2;

/// <summary>
/// One boundary piece of a <see cref="CurvedRegion2d"/>: a straight segment or a circular
/// arc, parameterized over t ∈ [0, 1] from <see cref="Start"/> to <see cref="End"/>.
///
/// <para><b>Why this vocabulary and not <c>Curve2d</c>.</b> The exact 2D curve family
/// (<c>Line2d</c>/<c>Arc2d</c>/<c>BezierCurve2d</c>/<c>NurbsCurve2d</c>) lives in
/// EngrCAD.BRep because <c>Curve2d.ToCurve3d</c> returns a <c>Curve3d</c>, and Core cannot
/// reference BRep. So the curved arrangement carries its own two-shape vocabulary as plain
/// data, and BRep bridges the two (<c>Curve2d.ToCurvedEdge2d</c> /
/// <c>CurvedEdge2d.ToCurve2d</c>). Lines and arcs are also exactly the tier the curved
/// arrangement can decide soundly — see <see cref="CurvedArrangement2d"/>.</para>
///
/// <para><b>Orientation is intrinsic.</b> <see cref="SweepAngle"/> is SIGNED (positive =
/// counter-clockwise), matching <c>Arc2d</c>, so reversing an edge never needs a flag: it
/// negates the sweep and swaps the endpoints.</para>
///
/// <para><b>Endpoints are stored, not re-derived.</b> An arc built by
/// <see cref="Arc(in Vector2d, double, double, double)"/> gets endpoints on its own circle,
/// but an edge coming out of an arrangement keeps the arrangement's stored VERTEX positions
/// so consecutive edges of a loop hand over their shared point bit-for-bit (the same rule
/// the B-Rep section loops follow). The two may therefore disagree at the ulp level, which
/// is below every tolerance in play.</para>
/// </summary>
public readonly struct CurvedEdge2d : IEquatable<CurvedEdge2d>
{
    /// <summary>Start point (t = 0).</summary>
    public Vector2d Start { get; }

    /// <summary>End point (t = 1).</summary>
    public Vector2d End { get; }

    /// <summary>Circle centre; meaningless for a straight edge.</summary>
    public Vector2d Center { get; }

    /// <summary>Circle radius, or exactly 0 for a straight edge.</summary>
    public double Radius { get; }

    /// <summary>Polar angle of the arc's start on its circle; 0 for a straight edge.</summary>
    public double StartAngle { get; }

    /// <summary>Signed angular span in radians (positive = counter-clockwise); 0 for a straight edge.</summary>
    public double SweepAngle { get; }

    /// <summary>True when this edge is a circular arc rather than a straight segment.</summary>
    // Exact-zero semantic test: the radius IS the discriminator, never a near-zero measurement.
    public bool IsArc => Radius > 0;

    /// <summary>True when the arc closes on itself (a whole circle as one edge).</summary>
    // Angular round-off slack, not a model tolerance: closure is a fact about the
    // parameterization, exactly as Arc2d.IsClosed spells it.
    public bool IsFullCircle => IsArc && Math.Abs(Math.Abs(SweepAngle) - 2 * Math.PI) < 1e-12;

    private CurvedEdge2d(
        in Vector2d start, in Vector2d end, in Vector2d center,
        double radius, double startAngle, double sweepAngle)
    {
        Start = start;
        End = end;
        Center = center;
        Radius = radius;
        StartAngle = startAngle;
        SweepAngle = sweepAngle;
    }

    /// <summary>A straight segment.</summary>
    public static CurvedEdge2d Line(in Vector2d start, in Vector2d end) =>
        new(start, end, default, 0, 0, 0);

    /// <summary>A circular arc from <paramref name="startAngle"/> spanning the SIGNED
    /// <paramref name="sweepAngle"/>; endpoints land on the circle.</summary>
    public static CurvedEdge2d Arc(
        in Vector2d center, double radius, double startAngle, double sweepAngle)
    {
        if (!(radius > 0))
            throw new ArgumentOutOfRangeException(nameof(radius), "Arc radius must be positive.");
        if (!double.IsFinite(sweepAngle) || Math.Abs(sweepAngle) > 2 * Math.PI + 1e-12)
            throw new ArgumentOutOfRangeException(nameof(sweepAngle), "Arc sweep cannot exceed a full turn.");
        var start = OnCircle(center, radius, startAngle);
        // A FULL turn closes on its own start point EXACTLY. Evaluating the end angle
        // instead lands ~2e-16 r away (sin(2 pi) is not 0 in doubles), and that gap is not
        // cosmetic: a +x ray whose ordinate falls inside it counts the seam piece's two
        // endpoints on opposite sides and the parity comes out wrong, so a point measurably
        // inside a disc reads as outside.
        var end = Math.Abs(Math.Abs(sweepAngle) - 2 * Math.PI) < 1e-12
            ? start
            : OnCircle(center, radius, startAngle + sweepAngle);
        return new CurvedEdge2d(start, end, center, radius, startAngle, sweepAngle);
    }

    /// <summary>A whole circle as one edge, counter-clockwise from the +x radial.</summary>
    public static CurvedEdge2d Circle(in Vector2d center, double radius) =>
        Arc(center, radius, 0, 2 * Math.PI);

    /// <summary>
    /// An arc with EXPLICIT endpoints — the form an arrangement emits, so a loop's
    /// consecutive edges share their joint bit-for-bit. The endpoints are expected to lie on
    /// the circle to within the caller's snap tolerance; the arc's own angles are derived
    /// from them and the requested turn direction.
    /// </summary>
    /// <param name="turn">+1 for a counter-clockwise arc, −1 for clockwise.</param>
    public static CurvedEdge2d ArcBetween(
        in Vector2d start, in Vector2d end, in Vector2d center, double radius, int turn)
    {
        if (!(radius > 0))
            throw new ArgumentOutOfRangeException(nameof(radius), "Arc radius must be positive.");
        if (turn is not (1 or -1))
            throw new ArgumentOutOfRangeException(nameof(turn), "Turn must be +1 or -1.");
        double a0 = Math.Atan2(start.Y - center.Y, start.X - center.X);
        double a1 = Math.Atan2(end.Y - center.Y, end.X - center.X);
        double sweep = a1 - a0;
        if (turn > 0)
        {
            while (sweep <= 0)
                sweep += 2 * Math.PI;
        }
        else
        {
            while (sweep >= 0)
                sweep -= 2 * Math.PI;
        }
        return new CurvedEdge2d(start, end, center, radius, a0, sweep);
    }

    /// <summary>The same geometry with explicit endpoints substituted (an arrangement
    /// re-anchoring an arc onto its stored vertices). Straight edges rebuild outright.</summary>
    public CurvedEdge2d WithEndpoints(in Vector2d start, in Vector2d end) => IsArc
        ? new CurvedEdge2d(start, end, Center, Radius, StartAngle, SweepAngle)
        : Line(start, end);

    /// <summary>The polar angle on the circle at curve parameter <paramref name="t"/>.</summary>
    public double AngleAt(double t) => StartAngle + SweepAngle * t;

    /// <summary>Point at curve parameter <paramref name="t"/> ∈ [0, 1]. The exact stored
    /// endpoints are returned at t = 0 and t = 1, so a chain's joints never drift.</summary>
    public Vector2d PointAt(double t)
    {
        // Exact-endpoint semantic tests: t = 0 and t = 1 mean "the stored endpoint".
        if (t == 0)
            return Start;
        if (t == 1)
            return End;
        return IsArc ? OnCircle(Center, Radius, AngleAt(t)) : Vector2d.Lerp(Start, End, t);
    }

    /// <summary>Midpoint of the edge (on the arc, not on the chord).</summary>
    public Vector2d MidPoint => PointAt(0.5);

    /// <summary>Unit tangent in the direction of travel at parameter <paramref name="t"/>.</summary>
    public Vector2d TangentAt(double t)
    {
        if (!IsArc)
            return (End - Start).Normalized();
        double angle = AngleAt(t);
        double sign = Math.Sign(SweepAngle);
        return new Vector2d(-Math.Sin(angle) * sign, Math.Cos(angle) * sign);
    }

    /// <summary>Signed curvature (positive turns counter-clockwise) — constant on both
    /// shapes, which is exactly what makes the tangential tie-break of
    /// <see cref="CurvedArrangement2d"/> decidable.</summary>
    public double SignedCurvature => IsArc ? Math.Sign(SweepAngle) / Radius : 0;

    /// <summary>The same edge traversed the other way.</summary>
    public CurvedEdge2d Reversed() => IsArc
        ? new CurvedEdge2d(End, Start, Center, Radius, StartAngle + SweepAngle, -SweepAngle)
        : Line(End, Start);

    /// <summary>Exact arc length.</summary>
    public double Length => IsArc ? Math.Abs(SweepAngle) * Radius : Start.DistanceTo(End);

    /// <summary>
    /// The half-open sub-edge over [<paramref name="t0"/>, <paramref name="t1"/>], with the
    /// given explicit endpoints (an arrangement supplies its stored vertex positions).
    /// </summary>
    public CurvedEdge2d Sub(double t0, double t1, in Vector2d start, in Vector2d end) => IsArc
        ? new CurvedEdge2d(start, end, Center, Radius, AngleAt(t0), SweepAngle * (t1 - t0))
        : Line(start, end);

    /// <summary>The sub-edge over [<paramref name="t0"/>, <paramref name="t1"/>].</summary>
    public CurvedEdge2d Sub(double t0, double t1) => Sub(t0, t1, PointAt(t0), PointAt(t1));

    /// <summary>
    /// ½∮(x dy − y dx) along this edge, measured about <paramref name="anchor"/> — the
    /// Green's-theorem term whose loop sum is the enclosed signed area.
    ///
    /// <para>EXACT for both shapes, which is the whole point of the curved tier: a circle's
    /// loop sums to πr² rather than to an inscribed polygon's area. For an arc the integral
    /// closes to ½[r²Δ + cx(y₁ − y₀) − cy(x₁ − x₀)] with the centre and points taken
    /// relative to the anchor.</para>
    /// </summary>
    public double SignedAreaTerm(in Vector2d anchor)
    {
        var p0 = Start - anchor;
        var p1 = End - anchor;
        if (!IsArc)
            return 0.5 * p0.Cross(p1);
        var c = Center - anchor;
        return 0.5 * (Radius * Radius * SweepAngle + c.X * (p1.Y - p0.Y) - c.Y * (p1.X - p0.X));
    }

    /// <summary>Axis-aligned bounds; an arc's cardinal extremes inside the sweep are
    /// included, so the box is tight rather than the chord's.</summary>
    public Aabb Bounds()
    {
        var bounds = Aabb.Empty
            .Union(new Vector3d(Start.X, Start.Y, 0))
            .Union(new Vector3d(End.X, End.Y, 0));
        if (!IsArc)
            return bounds;
        for (int quadrant = 0; quadrant < 4; quadrant++)
        {
            double angle = quadrant * Math.PI / 2;
            if (!TryLocalize(angle, out _))
                continue;
            var p = OnCircle(Center, Radius, angle);
            bounds = bounds.Union(new Vector3d(p.X, p.Y, 0));
        }
        return bounds;
    }

    /// <summary>
    /// Maps a polar <paramref name="angle"/> to a curve parameter in [0, 1] when the angle
    /// lies within the sweep (modulo 2π) — <c>Arc2d.TryLocalize</c>'s rule.
    /// </summary>
    public bool TryLocalize(double angle, out double t)
    {
        t = 0;
        if (!IsArc)
            return false;
        double delta = angle - StartAngle;
        double folded = delta % (2 * Math.PI);
        if (SweepAngle > 0 && folded < 0)
            folded += 2 * Math.PI;
        else if (SweepAngle < 0 && folded > 0)
            folded -= 2 * Math.PI;
        t = folded / SweepAngle;
        return t >= 0 && t <= 1;
    }

    /// <summary>Exact distance from <paramref name="point"/> to the edge.</summary>
    public double DistanceTo(in Vector2d point) => (NearestPoint(point) - point).Length;

    /// <summary>The nearest point ON the edge — closed form for both shapes.</summary>
    public Vector2d NearestPoint(in Vector2d point)
    {
        if (!IsArc)
        {
            var direction = End - Start;
            double lengthSquared = direction.LengthSquared;
            // Exact-zero guard: a degenerate segment is its own start point.
            if (!(lengthSquared > 0))
                return Start;
            double s = Math.Clamp((point - Start).Dot(direction) / lengthSquared, 0, 1);
            return Start + direction * s;
        }

        var offset = point - Center;
        // Exact-zero guard: the centre is equidistant from every point of the arc.
        if (offset.LengthSquared > 0
            && TryLocalize(Math.Atan2(offset.Y, offset.X), out double t))
        {
            return PointAt(t);
        }
        return (Start - point).LengthSquared <= (End - point).LengthSquared ? Start : End;
    }

    /// <summary>
    /// The parameter of <paramref name="point"/> along this edge, assuming the point lies on
    /// it (callers verify separately). Straight edges project; arcs localize the polar angle
    /// with the sweep's own folding, so the answer is monotone in travel order.
    /// </summary>
    public double ParameterOf(in Vector2d point)
    {
        if (!IsArc)
        {
            var direction = End - Start;
            double lengthSquared = direction.LengthSquared;
            return lengthSquared > 0 ? (point - Start).Dot(direction) / lengthSquared : 0;
        }
        double angle = Math.Atan2(point.Y - Center.Y, point.X - Center.X);
        double folded = (angle - StartAngle) % (2 * Math.PI);
        if (SweepAngle > 0 && folded < 0)
            folded += 2 * Math.PI;
        else if (SweepAngle < 0 && folded > 0)
            folded -= 2 * Math.PI;
        return folded / SweepAngle;
    }

    /// <summary>
    /// Crossings of the +x ray from <paramref name="point"/>, under the half-open
    /// upward-crossing rule that counts each shared endpoint exactly once. An arc is split
    /// into its y-monotone pieces first (at the ±y extremes of its circle), and each piece
    /// then sits wholly on one x-side of the centre, so its crossing abscissa is the closed
    /// form cx ± √(r² − (y − cy)²).
    /// </summary>
    public int RayCrossings(in Vector2d point)
    {
        if (!IsArc)
        {
            if (Start.Y > point.Y == End.Y > point.Y)
                return 0;
            // Which side of the edge the point falls on is the EXACT orientation sign, so
            // the crossing abscissa is never computed for a straight edge (Region2d's rule).
            int side = Predicates2d.Orient2dSign(Start, End, point);
            return (End.Y > Start.Y ? side > 0 : side < 0) ? 1 : 0;
        }

        int crossings = 0;
        Span<double> cuts = stackalloc double[4];
        int cutCount = MonotoneCuts(cuts);
        double from = StartAngle;
        // The FIRST and LAST piece take their ordinate from the STORED endpoints, not from
        // the angle. That is what makes a chain's parity consistent: the next edge's first
        // piece starts at exactly this edge's End, so no ray can slip between them. Deriving
        // both from the angles reopens the sin(2 pi) seam by ~2e-16 r.
        double fromY = Start.Y;
        for (int i = 0; i <= cutCount; i++)
        {
            bool last = i == cutCount;
            double to = last ? StartAngle + SweepAngle : cuts[i];
            double toY = last ? End.Y : Center.Y + Radius * Math.Sin(to);
            if (fromY > point.Y != toY > point.Y)
            {
                double half = 0.5 * (from + to);
                double dy = point.Y - Center.Y;
                double inside = Radius * Radius - dy * dy;
                double dx = inside > 0 ? Math.Sqrt(inside) : 0;
                // A y-monotone piece of a circle sits wholly on one x-side of the centre,
                // so its crossing abscissa is the closed form with that side's sign.
                double x = Center.X + (Math.Cos(half) >= 0 ? dx : -dx);
                if (x > point.X)
                    crossings++;
            }
            from = to;
            fromY = toY;
        }
        return crossings;
    }

    /// <summary>Angles strictly inside the sweep at which dy/dθ changes sign (θ ≡ ±π/2),
    /// in travel order — the y-monotone cut points of <see cref="RayCrossings"/>.</summary>
    private int MonotoneCuts(Span<double> cuts)
    {
        int count = 0;
        double end = StartAngle + SweepAngle;
        // Extremes sit at angles congruent to pi/2 modulo pi; walk them in TRAVEL order
        // from the sweep's own phase, so a sweep of any sign or magnitude is covered
        // without folding (folding would alias the two residues onto duplicate cuts).
        if (SweepAngle > 0)
        {
            double first = Math.PI / 2 + Math.Ceiling((StartAngle - Math.PI / 2) / Math.PI) * Math.PI;
            if (first <= StartAngle)
                first += Math.PI;
            for (double angle = first; angle < end && count < cuts.Length; angle += Math.PI)
                cuts[count++] = angle;
        }
        else
        {
            double first = Math.PI / 2 + Math.Floor((StartAngle - Math.PI / 2) / Math.PI) * Math.PI;
            if (first >= StartAngle)
                first -= Math.PI;
            for (double angle = first; angle > end && count < cuts.Length; angle -= Math.PI)
                cuts[count++] = angle;
        }
        return count;
    }

    private static Vector2d OnCircle(in Vector2d center, double radius, double angle) =>
        new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));

    /// <summary>Value equality on every stored field (used to dedupe identical primitives).</summary>
    public bool Equals(CurvedEdge2d other) =>
        Start == other.Start && End == other.End && Center == other.Center
        && Radius == other.Radius && StartAngle == other.StartAngle && SweepAngle == other.SweepAngle;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CurvedEdge2d other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Start, End, Center, Radius, StartAngle, SweepAngle);

    /// <inheritdoc/>
    public override string ToString() => IsArc
        ? $"Arc({Center}, r={Radius}, {StartAngle} + {SweepAngle})"
        : $"Line({Start} -> {End})";
}
