namespace EngrCAD.Core.Geometry2;

/// <summary>Which of the three shapes a <see cref="CurvedEdge2d"/> carries.</summary>
public enum CurvedEdgeKind
{
    /// <summary>A straight segment from <see cref="CurvedEdge2d.Start"/> to <see cref="CurvedEdge2d.End"/>.</summary>
    Line,

    /// <summary>A circular arc on <see cref="CurvedEdge2d.Center"/> / <see cref="CurvedEdge2d.Radius"/>.</summary>
    Arc,

    /// <summary>A CUBIC Bézier over the control polygon Start, <see cref="CurvedEdge2d.Control1"/>,
    /// <see cref="CurvedEdge2d.Control2"/>, End.</summary>
    Bezier,
}

/// <summary>
/// One boundary piece of a <see cref="CurvedRegion2d"/>: a straight segment, a circular arc,
/// or a CUBIC Bézier, parameterized over t ∈ [0, 1] from <see cref="Start"/> to
/// <see cref="End"/>.
///
/// <para><b>Why this vocabulary and not <c>Curve2d</c>.</b> The exact 2D curve family
/// (<c>Line2d</c>/<c>Arc2d</c>/<c>BezierCurve2d</c>/<c>NurbsCurve2d</c>) lives in
/// EngrCAD.BRep because <c>Curve2d.ToCurve3d</c> returns a <c>Curve3d</c>, and Core cannot
/// reference BRep. So the curved arrangement carries its own three-shape vocabulary as plain
/// data, and BRep bridges the two (<c>Curve2d.TryToCurvedEdges</c> /
/// <c>Curve2d.FromCurvedEdge</c>).</para>
///
/// <para><b>Why the cubic is the whole polynomial tier.</b> A quadratic Bézier elevates to a
/// cubic EXACTLY (<see cref="FromQuadratic"/>), and a non-rational B-spline of degree ≤ 3
/// splits into cubic Bézier pieces exactly by knot insertion — so "cubic" is the closure of
/// the polynomial sketch vocabulary rather than a cut-off. What stays outside is anything
/// RATIONAL other than a circle (an ellipse, a general rational NURBS): its implicit degree
/// and its area integral are different objects, and the tier refuses it by name rather than
/// approximating it.</para>
///
/// <para><b>Orientation is intrinsic.</b> <see cref="SweepAngle"/> is SIGNED (positive =
/// counter-clockwise), matching <c>Arc2d</c>, so reversing an arc never needs a flag: it
/// negates the sweep and swaps the endpoints. A Bézier reverses by reversing its control
/// polygon.</para>
///
/// <para><b>Endpoints are stored, not re-derived.</b> An arc built by
/// <see cref="Arc(in Vector2d, double, double, double)"/> gets endpoints on its own circle,
/// but an edge coming out of an arrangement keeps the arrangement's stored VERTEX positions
/// so consecutive edges of a loop hand over their shared point bit-for-bit (the same rule
/// the B-Rep section loops follow). The two may therefore disagree at the ulp level, which
/// is below every tolerance in play. For a Bézier <see cref="Start"/> and <see cref="End"/>
/// ARE the outer control points, so substituting them moves the curve by that same amount
/// and no separate carrier can drift away from them.</para>
/// </summary>
public readonly struct CurvedEdge2d : IEquatable<CurvedEdge2d>
{
    /// <summary>Start point (t = 0).</summary>
    public Vector2d Start { get; }

    /// <summary>End point (t = 1).</summary>
    public Vector2d End { get; }

    /// <summary>Circle centre; meaningless for a straight or Bézier edge.</summary>
    public Vector2d Center { get; }

    /// <summary>Circle radius, or exactly 0 for a straight or Bézier edge.</summary>
    public double Radius { get; }

    /// <summary>Polar angle of the arc's start on its circle; 0 otherwise.</summary>
    public double StartAngle { get; }

    /// <summary>Signed angular span in radians (positive = counter-clockwise); 0 otherwise.</summary>
    public double SweepAngle { get; }

    /// <summary>First interior control point of a cubic Bézier; meaningless otherwise.</summary>
    public Vector2d Control1 { get; }

    /// <summary>Second interior control point of a cubic Bézier; meaningless otherwise.</summary>
    public Vector2d Control2 { get; }

    /// <summary>Which shape this edge carries.</summary>
    public CurvedEdgeKind Kind { get; }

    /// <summary>True when this edge is a circular arc.</summary>
    public bool IsArc => Kind == CurvedEdgeKind.Arc;

    /// <summary>True when this edge is a cubic Bézier.</summary>
    public bool IsBezier => Kind == CurvedEdgeKind.Bezier;

    /// <summary>True when the arc closes on itself (a whole circle as one edge).</summary>
    // Angular round-off slack, not a model tolerance: closure is a fact about the
    // parameterization, exactly as Arc2d.IsClosed spells it.
    public bool IsFullCircle => IsArc && Math.Abs(Math.Abs(SweepAngle) - 2 * Math.PI) < 1e-12;

    private CurvedEdge2d(
        in Vector2d start, in Vector2d end, in Vector2d center,
        double radius, double startAngle, double sweepAngle,
        in Vector2d control1, in Vector2d control2, CurvedEdgeKind kind)
    {
        Start = start;
        End = end;
        Center = center;
        Radius = radius;
        StartAngle = startAngle;
        SweepAngle = sweepAngle;
        Control1 = control1;
        Control2 = control2;
        Kind = kind;
    }

    /// <summary>A straight segment.</summary>
    public static CurvedEdge2d Line(in Vector2d start, in Vector2d end) =>
        new(start, end, default, 0, 0, 0, default, default, CurvedEdgeKind.Line);

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
        return new CurvedEdge2d(
            start, end, center, radius, startAngle, sweepAngle, default, default, CurvedEdgeKind.Arc);
    }

    /// <summary>A whole circle as one edge, counter-clockwise from the +x radial.</summary>
    public static CurvedEdge2d Circle(in Vector2d center, double radius) =>
        Arc(center, radius, 0, 2 * Math.PI);

    /// <summary>A cubic Bézier over the four control points.</summary>
    public static CurvedEdge2d Bezier(
        in Vector2d start, in Vector2d control1, in Vector2d control2, in Vector2d end) =>
        new(start, end, default, 0, 0, 0, control1, control2, CurvedEdgeKind.Bezier);

    /// <summary>
    /// A quadratic Bézier as the cubic it exactly is: degree elevation moves the two interior
    /// control points to <c>(P0 + 2C)/3</c> and <c>(2C + P2)/3</c>, which reproduces the same
    /// curve at every parameter — a re-expression, not a fit. It is why the tier can be
    /// "cubic" and still carry every TrueType glyph segment.
    /// </summary>
    public static CurvedEdge2d FromQuadratic(
        in Vector2d start, in Vector2d control, in Vector2d end) =>
        Bezier(start, (start + control * 2) / 3, (control * 2 + end) / 3, end);

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
        return new CurvedEdge2d(
            start, end, center, radius, a0, sweep, default, default, CurvedEdgeKind.Arc);
    }

    /// <summary>The same geometry with explicit endpoints substituted (an arrangement
    /// re-anchoring an edge onto its stored vertices). Straight edges rebuild outright; a
    /// Bézier's outer control points ARE its endpoints, so they move with them.</summary>
    public CurvedEdge2d WithEndpoints(in Vector2d start, in Vector2d end) => Kind switch
    {
        CurvedEdgeKind.Arc => new CurvedEdge2d(
            start, end, Center, Radius, StartAngle, SweepAngle, default, default, CurvedEdgeKind.Arc),
        CurvedEdgeKind.Bezier => Bezier(start, Control1, Control2, end),
        _ => Line(start, end),
    };

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
        return Kind switch
        {
            CurvedEdgeKind.Arc => OnCircle(Center, Radius, AngleAt(t)),
            CurvedEdgeKind.Bezier => DeCasteljau(t),
            _ => Vector2d.Lerp(Start, End, t),
        };
    }

    /// <summary>Midpoint of the edge (on the curve, not on the chord).</summary>
    public Vector2d MidPoint => PointAt(0.5);

    /// <summary>Unit tangent in the direction of travel at parameter <paramref name="t"/>.</summary>
    public Vector2d TangentAt(double t)
    {
        switch (Kind)
        {
            case CurvedEdgeKind.Arc:
            {
                double angle = AngleAt(t);
                double sign = Math.Sign(SweepAngle);
                return new Vector2d(-Math.Sin(angle) * sign, Math.Cos(angle) * sign);
            }
            case CurvedEdgeKind.Bezier:
            {
                var d1 = DerivativeAt(t);
                double length = d1.Length;
                if (length > 0)
                    return d1 / length;
                // A cusp (a coincident control point at an endpoint) has no first-order
                // direction; l'Hopital's rule says the LIMITING tangent is the first
                // non-vanishing derivative, which for a cubic is the second and then the
                // third. Exact-zero guards throughout: only a genuinely stationary
                // derivative takes the next term.
                var d2 = SecondDerivativeAt(t);
                if (d2.LengthSquared > 0)
                    return d2.Normalized();
                var d3 = ThirdDerivative();
                return d3.LengthSquared > 0 ? d3.Normalized() : (End - Start).Normalized();
            }
            default:
                return (End - Start).Normalized();
        }
    }

    /// <summary>First derivative with respect to t (exact for all three shapes).</summary>
    public Vector2d DerivativeAt(double t) => Kind switch
    {
        CurvedEdgeKind.Arc => PerpendicularRadial(AngleAt(t)) * SweepAngle,
        CurvedEdgeKind.Bezier => BezierDerivative(t),
        _ => End - Start,
    };

    /// <summary>Second derivative with respect to t (exact for all three shapes).</summary>
    public Vector2d SecondDerivativeAt(double t) => Kind switch
    {
        CurvedEdgeKind.Arc => -Radial(AngleAt(t)) * (Radius * SweepAngle * SweepAngle),
        CurvedEdgeKind.Bezier => BezierSecondDerivative(t),
        _ => Vector2d.Zero,
    };

    /// <summary>
    /// Signed curvature at t (positive turns counter-clockwise). CONSTANT for a line (0) and
    /// an arc (±1/r), which is exactly what makes the tangential tie-break of
    /// <see cref="CurvedArrangement2d"/> terminate in one step for those two shapes.
    /// </summary>
    public double SignedCurvatureAt(double t)
    {
        if (Kind != CurvedEdgeKind.Bezier)
            return IsArc ? Math.Sign(SweepAngle) / Radius : 0;
        var d1 = BezierDerivative(t);
        double speed = d1.Length;
        // Exact-zero division guard (scale-free): a stationary point has no curvature.
        if (!(speed > 0))
            return 0;
        return d1.Cross(BezierSecondDerivative(t)) / (speed * speed * speed);
    }

    /// <summary>Signed curvature at the START of the edge — the constant value for lines and
    /// arcs, so every incumbent reading is unchanged.</summary>
    public double SignedCurvature => SignedCurvatureAt(0);

    /// <summary>
    /// Radius of curvature at t: <c>+∞</c> for a straight edge, the arc's own radius, and
    /// <c>1/|κ|</c> for a Bézier. This is the ONE rule
    /// <see cref="CurvedRegion2dBoolean"/> caps its interior-sample push by — for an arc it
    /// reduces to <see cref="Radius"/> and for a line to no cap at all, so the shape the
    /// straight proof needs survives unchanged.
    /// </summary>
    public double CurvatureRadiusAt(double t)
    {
        if (Kind == CurvedEdgeKind.Line)
            return double.PositiveInfinity;
        if (Kind == CurvedEdgeKind.Arc)
            return Radius;
        double curvature = Math.Abs(SignedCurvatureAt(t));
        return curvature > 0 ? 1 / curvature : double.PositiveInfinity;
    }

    /// <summary>The same edge traversed the other way.</summary>
    public CurvedEdge2d Reversed() => Kind switch
    {
        CurvedEdgeKind.Arc => new CurvedEdge2d(
            End, Start, Center, Radius, StartAngle + SweepAngle, -SweepAngle,
            default, default, CurvedEdgeKind.Arc),
        CurvedEdgeKind.Bezier => Bezier(End, Control2, Control1, Start),
        _ => Line(End, Start),
    };

    /// <summary>
    /// Arc length. EXACT for a line and an arc; for a Bézier it is a 5-point Gauss–Legendre
    /// quadrature over four sub-intervals, which integrates a cubic's speed to round-off in
    /// practice — a cubic has no closed-form arc length, and the API says so rather than
    /// implying one.
    /// </summary>
    public double Length => Kind switch
    {
        CurvedEdgeKind.Arc => Math.Abs(SweepAngle) * Radius,
        CurvedEdgeKind.Bezier => BezierLength(),
        _ => Start.DistanceTo(End),
    };

    /// <summary>
    /// The sub-edge over [<paramref name="t0"/>, <paramref name="t1"/>], with the given
    /// explicit endpoints (an arrangement supplies its stored vertex positions).
    ///
    /// <para>EXACT for every shape: an arc's sub-arc is an arc of the same circle, and a
    /// cubic's restriction to a sub-interval is again a cubic, whose control points come
    /// from the endpoints and end derivatives — Hermite data a cubic is fully determined
    /// by.</para>
    /// </summary>
    public CurvedEdge2d Sub(double t0, double t1, in Vector2d start, in Vector2d end) => Kind switch
    {
        CurvedEdgeKind.Arc => new CurvedEdge2d(
            start, end, Center, Radius, AngleAt(t0), SweepAngle * (t1 - t0),
            default, default, CurvedEdgeKind.Arc),
        CurvedEdgeKind.Bezier => BezierSub(t0, t1, start, end),
        _ => Line(start, end),
    };

    /// <summary>The sub-edge over [<paramref name="t0"/>, <paramref name="t1"/>].</summary>
    public CurvedEdge2d Sub(double t0, double t1) => Sub(t0, t1, PointAt(t0), PointAt(t1));

    /// <summary>
    /// ½∮(x dy − y dx) along this edge, measured about <paramref name="anchor"/> — the
    /// Green's-theorem term whose loop sum is the enclosed signed area.
    ///
    /// <para>EXACT for all three shapes, which is the whole point of the curved tier: a
    /// circle's loop sums to πr² rather than to an inscribed polygon's area. For an arc the
    /// integral closes to ½[r²Δ + cx(y₁ − y₀) − cy(x₁ − x₀)] with the centre and points taken
    /// relative to the anchor; for a cubic the integrand x·y′ − y·x′ is a QUINTIC polynomial,
    /// so integrating it term by term is exact arithmetic rather than a quadrature.</para>
    /// </summary>
    public double SignedAreaTerm(in Vector2d anchor)
    {
        var p0 = Start - anchor;
        var p1 = End - anchor;
        switch (Kind)
        {
            case CurvedEdgeKind.Arc:
            {
                var c = Center - anchor;
                return 0.5 * (Radius * Radius * SweepAngle + c.X * (p1.Y - p0.Y) - c.Y * (p1.X - p0.X));
            }
            case CurvedEdgeKind.Bezier:
                return BezierAreaTerm(anchor);
            default:
                return 0.5 * p0.Cross(p1);
        }
    }

    /// <summary>Axis-aligned bounds. TIGHT for every shape: an arc's cardinal extremes inside
    /// the sweep are included, and a cubic's stationary points (the roots of a quadratic per
    /// axis) are too — so the box is the curve's and never the control polygon's.</summary>
    public Aabb Bounds()
    {
        var bounds = Aabb.Empty
            .Union(new Vector3d(Start.X, Start.Y, 0))
            .Union(new Vector3d(End.X, End.Y, 0));
        switch (Kind)
        {
            case CurvedEdgeKind.Arc:
                for (int quadrant = 0; quadrant < 4; quadrant++)
                {
                    double angle = quadrant * Math.PI / 2;
                    if (!TryLocalize(angle, out _))
                        continue;
                    var p = OnCircle(Center, Radius, angle);
                    bounds = bounds.Union(new Vector3d(p.X, p.Y, 0));
                }
                return bounds;

            case CurvedEdgeKind.Bezier:
            {
                Span<double> roots = stackalloc double[2];
                var (cx, cy) = PowerBasis();
                for (int axis = 0; axis < 2; axis++)
                {
                    var c = axis == 0 ? cx : cy;
                    // The derivative of a cubic is the quadratic 3c3 t² + 2c2 t + c1.
                    int count = QuadraticRootsInUnit(3 * c.W, 2 * c.Z, c.Y, roots);
                    for (int k = 0; k < count; k++)
                    {
                        var p = DeCasteljau(roots[k]);
                        bounds = bounds.Union(new Vector3d(p.X, p.Y, 0));
                    }
                }
                return bounds;
            }

            default:
                return bounds;
        }
    }

    /// <summary>
    /// Maps a polar <paramref name="angle"/> to a curve parameter in [0, 1] when the angle
    /// lies within the sweep (modulo 2π) — <c>Arc2d.TryLocalize</c>'s rule. Arcs only.
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

    /// <summary>Distance from <paramref name="point"/> to the edge.</summary>
    public double DistanceTo(in Vector2d point) => (NearestPoint(point) - point).Length;

    /// <summary>
    /// The nearest point ON the edge — closed form for a line and an arc. For a Bézier the
    /// footpoint condition <c>(C(t) − p)·C′(t) = 0</c> is a QUINTIC, so it is bracketed on a
    /// fixed 33-point scan and each bracket refined by safeguarded bisection–Newton (the
    /// pattern <c>Curve2d.NearestPoint</c> already uses one layer up). Every candidate is a
    /// real point on the curve, so the reported distance can only ever OVER-estimate the true
    /// one — the safe direction for a fitting error and the unsafe one for a contact test,
    /// which is why the scan is dense enough to bracket every root of a quintic in practice.
    /// </summary>
    public Vector2d NearestPoint(in Vector2d point)
    {
        switch (Kind)
        {
            case CurvedEdgeKind.Arc:
            {
                var offset = point - Center;
                // Exact-zero guard: the centre is equidistant from every point of the arc.
                if (offset.LengthSquared > 0
                    && TryLocalize(Math.Atan2(offset.Y, offset.X), out double t))
                {
                    return PointAt(t);
                }
                return (Start - point).LengthSquared <= (End - point).LengthSquared ? Start : End;
            }

            case CurvedEdgeKind.Bezier:
                return DeCasteljau(NearestParameter(point));

            default:
            {
                var direction = End - Start;
                double lengthSquared = direction.LengthSquared;
                // Exact-zero guard: a degenerate segment is its own start point.
                if (!(lengthSquared > 0))
                    return Start;
                double s = Math.Clamp((point - Start).Dot(direction) / lengthSquared, 0, 1);
                return Start + direction * s;
            }
        }
    }

    /// <summary>
    /// The parameter of <paramref name="point"/> along this edge, assuming the point lies on
    /// it (callers verify separately). Straight edges project; arcs localize the polar angle
    /// with the sweep's own folding, so the answer is monotone in travel order; a Bézier
    /// reports its nearest-point parameter.
    /// </summary>
    public double ParameterOf(in Vector2d point)
    {
        switch (Kind)
        {
            case CurvedEdgeKind.Arc:
            {
                double angle = Math.Atan2(point.Y - Center.Y, point.X - Center.X);
                double folded = (angle - StartAngle) % (2 * Math.PI);
                if (SweepAngle > 0 && folded < 0)
                    folded += 2 * Math.PI;
                else if (SweepAngle < 0 && folded > 0)
                    folded -= 2 * Math.PI;
                return folded / SweepAngle;
            }

            case CurvedEdgeKind.Bezier:
                return NearestParameter(point);

            default:
            {
                var direction = End - Start;
                double lengthSquared = direction.LengthSquared;
                return lengthSquared > 0 ? (point - Start).Dot(direction) / lengthSquared : 0;
            }
        }
    }

    /// <summary>
    /// Crossings of the +x ray from <paramref name="point"/>, under the half-open
    /// upward-crossing rule that counts each shared endpoint exactly once. An arc is split
    /// into its y-monotone pieces first (at the ±y extremes of its circle), and each piece
    /// then sits wholly on one x-side of the centre, so its crossing abscissa is the closed
    /// form cx ± √(r² − (y − cy)²). A cubic is split at the roots of y′(t), which makes each
    /// piece y-monotone too — the crossing is then the unique root of y(t) = point.Y in that
    /// piece, found by safeguarded bisection rather than by a closed form, since a cubic's
    /// inverse has none.
    /// </summary>
    public int RayCrossings(in Vector2d point)
    {
        switch (Kind)
        {
            case CurvedEdgeKind.Arc:
                return ArcRayCrossings(point);

            case CurvedEdgeKind.Bezier:
                return BezierRayCrossings(point);

            default:
            {
                if (Start.Y > point.Y == End.Y > point.Y)
                    return 0;
                // Which side of the edge the point falls on is the EXACT orientation sign, so
                // the crossing abscissa is never computed for a straight edge (Region2d's rule).
                int side = Predicates2d.Orient2dSign(Start, End, point);
                return (End.Y > Start.Y ? side > 0 : side < 0) ? 1 : 0;
            }
        }
    }

    private int ArcRayCrossings(in Vector2d point)
    {
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

    private int BezierRayCrossings(in Vector2d point)
    {
        var (_, cy) = PowerBasis();
        Span<double> roots = stackalloc double[2];
        int cutCount = QuadraticRootsInUnit(3 * cy.W, 2 * cy.Z, cy.Y, roots);

        int crossings = 0;
        double fromT = 0;
        // Same stored-endpoint rule as the arc: the first and last piece take their ordinate
        // from Start/End so a chain's joints cannot let a ray slip between two edges.
        double fromY = Start.Y;
        for (int i = 0; i <= cutCount; i++)
        {
            bool last = i == cutCount;
            double toT = last ? 1 : roots[i];
            double toY = last ? End.Y : DeCasteljau(toT).Y;
            if (fromY > point.Y != toY > point.Y)
            {
                double t = SolveYMonotone(fromT, toT, fromY, toY, point.Y);
                if (DeCasteljau(t).X > point.X)
                    crossings++;
            }
            fromT = toT;
            fromY = toY;
        }
        return crossings;
    }

    /// <summary>The unique parameter in a y-MONOTONE piece at which y reaches
    /// <paramref name="target"/>, by bisection (the bracket is what guarantees convergence;
    /// a cubic's inverse has no closed form).</summary>
    private double SolveYMonotone(double t0, double t1, double y0, double y1, double target)
    {
        bool rising = y1 > y0;
        double lo = t0, hi = t1;
        for (int i = 0; i < 60 && hi - lo > 0; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (mid <= lo || mid >= hi)
                break;
            double value = DeCasteljau(mid).Y;
            if (value > target == rising)
                hi = mid;
            else
                lo = mid;
        }
        return 0.5 * (lo + hi);
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

    // ---- cubic Bézier arithmetic ----

    /// <summary>The four control points, Start and End included.</summary>
    public (Vector2d P0, Vector2d P1, Vector2d P2, Vector2d P3) ControlPoints =>
        (Start, Control1, Control2, End);

    private Vector2d DeCasteljau(double t)
    {
        var a = Vector2d.Lerp(Start, Control1, t);
        var b = Vector2d.Lerp(Control1, Control2, t);
        var c = Vector2d.Lerp(Control2, End, t);
        return Vector2d.Lerp(Vector2d.Lerp(a, b, t), Vector2d.Lerp(b, c, t), t);
    }

    private Vector2d BezierDerivative(double t)
    {
        var q0 = (Control1 - Start) * 3;
        var q1 = (Control2 - Control1) * 3;
        var q2 = (End - Control2) * 3;
        return Vector2d.Lerp(Vector2d.Lerp(q0, q1, t), Vector2d.Lerp(q1, q2, t), t);
    }

    private Vector2d BezierSecondDerivative(double t)
    {
        var q0 = (Control1 - Start) * 3;
        var q1 = (Control2 - Control1) * 3;
        var q2 = (End - Control2) * 3;
        return Vector2d.Lerp((q1 - q0) * 2, (q2 - q1) * 2, t);
    }

    private Vector2d ThirdDerivative() =>
        (End - Start + (Control1 - Control2) * 3) * 6;

    /// <summary>Power-basis coefficients (c0 + c1 t + c2 t² + c3 t³) per axis, packed as
    /// (c0, c1, c2, c3).</summary>
    private (Vector4Coefficients X, Vector4Coefficients Y) PowerBasis()
    {
        var p0 = Start;
        var p1 = Control1;
        var p2 = Control2;
        var p3 = End;
        var c1 = (p1 - p0) * 3;
        var c2 = (p0 - p1 * 2 + p2) * 3;
        var c3 = p3 - p0 + (p1 - p2) * 3;
        return (new Vector4Coefficients(p0.X, c1.X, c2.X, c3.X),
                new Vector4Coefficients(p0.Y, c1.Y, c2.Y, c3.Y));
    }

    /// <summary>Power-basis coefficients of one axis of a cubic: X + Y·t + Z·t² + W·t³.</summary>
    private readonly record struct Vector4Coefficients(double X, double Y, double Z, double W);

    private CurvedEdge2d BezierSub(double t0, double t1, in Vector2d start, in Vector2d end)
    {
        // A cubic restricted to [t0, t1] is exactly the cubic through its endpoints with
        // matching end derivatives, scaled by the interval length — Hermite data determines
        // a cubic completely, so this is exact rather than a fit.
        double span = (t1 - t0) / 3;
        var control1 = PointAt(t0) + BezierDerivative(t0) * span;
        var control2 = PointAt(t1) - BezierDerivative(t1) * span;
        return Bezier(start, control1, control2, end);
    }

    private double BezierAreaTerm(in Vector2d anchor)
    {
        var (cx, cy) = PowerBasis();
        // Relative to the anchor: only the constant term moves.
        double ax0 = cx.X - anchor.X, ay0 = cy.X - anchor.Y;
        Span<double> a = [ax0, cx.Y, cx.Z, cx.W];
        Span<double> b = [ay0, cy.Y, cy.Z, cy.W];
        Span<double> da = [cx.Y, 2 * cx.Z, 3 * cx.W];
        Span<double> db = [cy.Y, 2 * cy.Z, 3 * cy.W];

        // f(t) = x·y′ − y·x′ is a quintic; integrate it term by term (exact arithmetic).
        Span<double> f = stackalloc double[6];
        f.Clear();
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 3; j++)
                f[i + j] += a[i] * db[j] - b[i] * da[j];
        }
        double integral = 0;
        for (int k = 0; k < 6; k++)
            integral += f[k] / (k + 1);
        return 0.5 * integral;
    }

    private double BezierLength()
    {
        // 5-point Gauss-Legendre on four sub-intervals. A cubic's speed is the square root
        // of a quartic, so no closed form exists; this is exact to round-off in practice and
        // the API contract says numerical rather than pretending otherwise.
        ReadOnlySpan<double> nodes =
        [
            -0.9061798459386640, -0.5384693101056831, 0.0,
            0.5384693101056831, 0.9061798459386640,
        ];
        ReadOnlySpan<double> weights =
        [
            0.2369268850561891, 0.4786286704993665, 0.5688888888888889,
            0.4786286704993665, 0.2369268850561891,
        ];
        const int panels = 4;
        double half = 0.5 / panels;
        double total = 0;
        for (int panel = 0; panel < panels; panel++)
        {
            double centre = (panel + 0.5) / panels;
            for (int k = 0; k < nodes.Length; k++)
                total += weights[k] * BezierDerivative(centre + half * nodes[k]).Length;
        }
        return total * half;
    }

    /// <summary>The parameter of the nearest point on a cubic — see
    /// <see cref="NearestPoint"/> for the method and its error direction.</summary>
    private double NearestParameter(in Vector2d point)
    {
        const int samples = 32;
        double best = 0;
        double bestDistance = (Start - point).LengthSquared;
        double endDistance = (End - point).LengthSquared;
        if (endDistance < bestDistance)
        {
            best = 1;
            bestDistance = endDistance;
        }

        double previousT = 0;
        double previous = (Start - point).Dot(BezierDerivative(0));
        for (int i = 1; i <= samples; i++)
        {
            double t = (double)i / samples;
            double value = (DeCasteljau(t) - point).Dot(BezierDerivative(t));
            if (previous == 0 || previous > 0 != value > 0)
            {
                double root = RefineFootpoint(point, previousT, t);
                double distance = (DeCasteljau(root) - point).LengthSquared;
                if (distance < bestDistance)
                {
                    best = root;
                    bestDistance = distance;
                }
            }
            previousT = t;
            previous = value;
        }
        return best;
    }

    /// <summary>Bisection on the footpoint condition over a bracket that already straddles a
    /// root — the bracket is what guarantees convergence, so no seed can send it astray.</summary>
    private double RefineFootpoint(in Vector2d point, double lo, double hi)
    {
        double flo = (PointAt(lo) - point).Dot(BezierDerivative(lo));
        for (int i = 0; i < 60 && hi - lo > 0; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (mid <= lo || mid >= hi)
                break;
            double value = (DeCasteljau(mid) - point).Dot(BezierDerivative(mid));
            if (value > 0 == flo > 0)
            {
                lo = mid;
                flo = value;
            }
            else
            {
                hi = mid;
            }
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>Real roots of a·t² + b·t + c strictly inside (0, 1), in ascending order.</summary>
    private static int QuadraticRootsInUnit(double a, double b, double c, Span<double> roots)
    {
        int count = 0;
        // Exact-zero test, not a tolerance: a genuinely linear derivative has one root.
        if (a == 0)
        {
            if (b != 0)
                Accept(-c / b, roots, ref count);
            return count;
        }
        double discriminant = b * b - 4 * a * c;
        if (discriminant < 0)
            return 0;
        double root = Math.Sqrt(discriminant);
        // Stable quadratic form: the two roots as q/a and c/q with q = −(b ± √D)/2.
        double q = -0.5 * (b + Math.Sign(b == 0 ? 1 : b) * root);
        double r0 = q / a;
        double r1 = q != 0 ? c / q : r0;
        if (r0 > r1)
            (r0, r1) = (r1, r0);
        Accept(r0, roots, ref count);
        Accept(r1, roots, ref count);
        return count;

        static void Accept(double t, Span<double> into, ref int count)
        {
            if (t > 0 && t < 1 && count < into.Length)
                into[count++] = t;
        }
    }

    private static Vector2d OnCircle(in Vector2d center, double radius, double angle) =>
        new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));

    private static Vector2d Radial(double angle) => new(Math.Cos(angle), Math.Sin(angle));

    private Vector2d PerpendicularRadial(double angle) =>
        new(-Radius * Math.Sin(angle), Radius * Math.Cos(angle));

    /// <summary>Value equality on every stored field (used to dedupe identical primitives).</summary>
    public bool Equals(CurvedEdge2d other) =>
        Kind == other.Kind
        && Start == other.Start && End == other.End && Center == other.Center
        && Radius == other.Radius && StartAngle == other.StartAngle && SweepAngle == other.SweepAngle
        && Control1 == other.Control1 && Control2 == other.Control2;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CurvedEdge2d other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(Start, End, Center, Radius, StartAngle, SweepAngle, Control1, Control2);

    /// <inheritdoc/>
    public override string ToString() => Kind switch
    {
        CurvedEdgeKind.Arc => $"Arc({Center}, r={Radius}, {StartAngle} + {SweepAngle})",
        CurvedEdgeKind.Bezier => $"Bezier({Start}, {Control1}, {Control2}, {End})",
        _ => $"Line({Start} -> {End})",
    };
}
