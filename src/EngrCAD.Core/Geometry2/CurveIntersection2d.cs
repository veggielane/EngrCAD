namespace EngrCAD.Core.Geometry2;

/// <summary>
/// Closed-form intersection of the two shapes a <see cref="CurvedEdge2d"/> can be —
/// straight segment and circular arc. Line/line, line/arc and arc/arc all have exact
/// algebraic solutions, which is precisely why "lines and arcs" is a complete tier that a
/// curved arrangement can decide without subdivision or a fitting tolerance.
///
/// <para><b>One tolerance, and it is a LENGTH.</b> Every decision this class makes is
/// posed so that its threshold is a distance in model units, and that distance is the
/// arrangement's own vertex snap tolerance — no second epsilon, no dimensionless band.
/// A line is tangent to a circle when the centre's distance from the line differs from
/// the radius by less than the tolerance; two circles are tangent when their centre
/// distance differs from r₀ + r₁ (or |r₀ − r₁|) by less than the tolerance; a point is on
/// an edge when its distance to the edge is under it. That is the same resolution at
/// which the arrangement can tell two vertices apart, so nothing finer could be
/// represented anyway.</para>
///
/// <para><b>Near-tangency SNAPS, it does not refuse.</b> A discriminant inside the band is
/// reported as ONE touch point rather than as two near-coincident crossings or as a miss.
/// Both alternatives are unstable: a pair of crossings a nanometre apart produces a
/// degenerate sliver cell whose classification is decided by rounding (this is exactly the
/// near-tangency pinhole the polygonal path suffers), while dropping the contact loses a
/// node the tracing needs. Snapping is area-neutral to O(τ^1.5) and always yields a valid
/// arrangement, because a tangential contact IS representable here: the two edges leave the
/// node with equal tangents and DIFFERENT curvature, which the fan order can rank.</para>
/// </summary>
public static class CurveIntersection2d
{
    /// <summary>One contact between two edges: the parameter on each, and the point.</summary>
    /// <param name="Ta">Parameter on the first edge.</param>
    /// <param name="Tb">Parameter on the second edge.</param>
    /// <param name="Point">The contact point.</param>
    public readonly record struct Contact(double Ta, double Tb, Vector2d Point);

    /// <summary>
    /// Every contact between <paramref name="a"/> and <paramref name="b"/>: transversal
    /// crossings, tangential touches, and endpoints of either lying on the other. Contacts
    /// closer than <paramref name="tolerance"/> to one another are reported once.
    ///
    /// <para>Edges sharing a carrier (collinear segments, cocircular arcs) contribute only
    /// their endpoint contacts — which is all a planar arrangement needs, because splitting
    /// both at every shared endpoint makes the overlapping pieces span identical vertex
    /// pairs and they then dedupe on the carrier.</para>
    /// </summary>
    public static IReadOnlyList<Contact> Intersect(in CurvedEdge2d a, in CurvedEdge2d b, double tolerance)
    {
        var contacts = new List<Contact>(4);
        Intersect(a, b, tolerance, contacts);
        return contacts;
    }

    /// <summary>Appends the contacts of <paramref name="a"/> and <paramref name="b"/> to a
    /// caller-owned list (the allocation-free entry the arrangement uses).</summary>
    public static void Intersect(
        in CurvedEdge2d a, in CurvedEdge2d b, double tolerance, List<Contact> contacts)
    {
        if (!(tolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        // Bounding-box reject, padded by the tolerance so a snapped contact survives it.
        var boxA = a.Bounds();
        var boxB = b.Bounds();
        if (boxA.Min.X - tolerance > boxB.Max.X || boxB.Min.X - tolerance > boxA.Max.X
            || boxA.Min.Y - tolerance > boxB.Max.Y || boxB.Min.Y - tolerance > boxA.Max.Y)
        {
            return;
        }

        int before = contacts.Count;

        // Endpoint contacts first: they carry the exact stored coordinates, so a shared
        // joint is reported at its authored position rather than at a re-solved one.
        AddIfOnBoth(a, b, a.Start, 0, tolerance, contacts, before, swap: false);
        AddIfOnBoth(a, b, a.End, 1, tolerance, contacts, before, swap: false);
        AddIfOnBoth(b, a, b.Start, 0, tolerance, contacts, before, swap: true);
        AddIfOnBoth(b, a, b.End, 1, tolerance, contacts, before, swap: true);

        // Two edges on one carrier contribute only their endpoint contacts (above): the
        // arrangement splits both at every shared endpoint and the overlapping pieces then
        // dedupe on the carrier. Testing it BEFORE the shape dispatch is what keeps the
        // Bézier subdivision from recursing on a curve against itself.
        if (SameCarrier(a, b, tolerance))
            return;

        switch (a.Kind, b.Kind)
        {
            case (CurvedEdgeKind.Line, CurvedEdgeKind.Line):
                LineLine(a, b, tolerance, contacts, before);
                break;
            case (CurvedEdgeKind.Arc, CurvedEdgeKind.Line):
                LineArc(b, a, tolerance, contacts, before, swap: true);
                break;
            case (CurvedEdgeKind.Line, CurvedEdgeKind.Arc):
                LineArc(a, b, tolerance, contacts, before, swap: false);
                break;
            case (CurvedEdgeKind.Arc, CurvedEdgeKind.Arc):
                ArcArc(a, b, tolerance, contacts, before);
                break;
            case (CurvedEdgeKind.Bezier, CurvedEdgeKind.Bezier):
                BezierBezier(a, b, tolerance, contacts, before);
                break;
            case (CurvedEdgeKind.Bezier, _):
                ConicBezier(b, a, tolerance, contacts, before, swap: true);
                break;
            default:
                ConicBezier(a, b, tolerance, contacts, before, swap: false);
                break;
        }
    }

    /// <summary>
    /// Do the two edges lie on the SAME carrier — one line, one circle, or one cubic?
    /// Collinearity is decided by the exact <see cref="Predicates2d.Orient2dSign"/>;
    /// cocircularity by centre and radius agreeing within <paramref name="tolerance"/>, the
    /// same length threshold vertices merge at.
    ///
    /// <para>For two cubics the question is whether one is a RESTRICTION of the other, which
    /// is decidable in closed form and needs no new epsilon: a cubic is determined by its
    /// endpoints and end derivatives, so <c>a</c> restricted to
    /// <c>[a.ParameterOf(b.Start), a.ParameterOf(b.End)]</c> is the only cubic that could
    /// coincide with <c>b</c>, and comparing the two control polygons within
    /// <paramref name="tolerance"/> settles it. The comparison is of POINTS, so the threshold
    /// is a length like every other in this tier — and a reversed restriction is covered for
    /// free, since the parameters then come back in the other order.</para>
    /// </summary>
    public static bool SameCarrier(in CurvedEdge2d a, in CurvedEdge2d b, double tolerance)
    {
        if (a.Kind != b.Kind)
            return false;
        switch (a.Kind)
        {
            case CurvedEdgeKind.Arc:
                return a.Center.DistanceTo(b.Center) <= tolerance
                    && Math.Abs(a.Radius - b.Radius) <= tolerance;

            case CurvedEdgeKind.Bezier:
            {
                // Both of b's endpoints must be ON a before its parameters mean anything.
                if (a.DistanceTo(b.Start) > tolerance || a.DistanceTo(b.End) > tolerance)
                    return false;
                double t0 = a.ParameterOf(b.Start);
                double t1 = a.ParameterOf(b.End);
                // Exact-zero guard: a zero-span restriction is a point, not a carrier match.
                if (t0 == t1)
                    return false;
                var restricted = a.Sub(t0, t1);
                var (q0, q1, q2, q3) = restricted.ControlPoints;
                var (p0, p1, p2, p3) = b.ControlPoints;
                return q0.DistanceTo(p0) <= tolerance && q1.DistanceTo(p1) <= tolerance
                    && q2.DistanceTo(p2) <= tolerance && q3.DistanceTo(p3) <= tolerance;
            }

            default:
                return Predicates2d.Orient2dSign(a.Start, a.End, b.Start) == 0
                    && Predicates2d.Orient2dSign(a.Start, a.End, b.End) == 0;
        }
    }

    // ---- the three closed forms ----

    private static void LineLine(
        in CurvedEdge2d a, in CurvedEdge2d b, double tolerance, List<Contact> contacts, int before)
    {
        // Proper transversal crossing detected EXACTLY (both pairs strictly straddle); the
        // crossing point is then computed in doubles, as Arrangement2d does.
        int oa0 = Predicates2d.Orient2dSign(a.Start, a.End, b.Start);
        int oa1 = Predicates2d.Orient2dSign(a.Start, a.End, b.End);
        int ob0 = Predicates2d.Orient2dSign(b.Start, b.End, a.Start);
        int ob1 = Predicates2d.Orient2dSign(b.Start, b.End, a.End);
        if (oa0 * oa1 >= 0 || ob0 * ob1 >= 0)
            return;

        var da = a.End - a.Start;
        var db = b.End - b.Start;
        double denom = da.Cross(db);
        // Exact-zero division guard: the straddle test above already rules out parallel.
        if (denom == 0)
            return;
        double t = (b.Start - a.Start).Cross(db) / denom;
        var point = a.Start + da * t;
        Add(contacts, before, tolerance, new Contact(t, b.ParameterOf(point), point));
    }

    private static void LineArc(
        in CurvedEdge2d line, in CurvedEdge2d arc, double tolerance,
        List<Contact> contacts, int before, bool swap)
    {
        var direction = line.End - line.Start;
        double length = direction.Length;
        // Exact-zero guard: a degenerate segment has no direction to solve along.
        if (!(length > 0))
            return;
        var unit = direction / length;

        var toCentre = arc.Center - line.Start;
        double along = toCentre.Dot(unit);
        double perpendicular = Math.Abs(toCentre.Cross(unit));

        // The tangency test in LENGTH units: how far the line's distance from the centre is
        // from the radius. Outside the band the discriminant is safely signed.
        double gap = perpendicular - arc.Radius;
        if (gap > tolerance)
            return;
        if (Math.Abs(gap) <= tolerance)
        {
            AddSolved(line, arc, line.Start + unit * along, along / length, tolerance, contacts, before, swap);
            return;
        }
        double half = Math.Sqrt(Math.Max(arc.Radius * arc.Radius - perpendicular * perpendicular, 0));
        AddSolved(line, arc, line.Start + unit * (along - half), (along - half) / length, tolerance, contacts, before, swap);
        AddSolved(line, arc, line.Start + unit * (along + half), (along + half) / length, tolerance, contacts, before, swap);
    }

    private static void ArcArc(
        in CurvedEdge2d a, in CurvedEdge2d b, double tolerance, List<Contact> contacts, int before)
    {
        var delta = b.Center - a.Center;
        double distance = delta.Length;
        // Concentric: either the same carrier (endpoint contacts already cover it) or
        // disjoint circles. Either way there is no transversal root to find.
        if (distance <= tolerance)
            return;
        var unit = delta / distance;

        double external = distance - (a.Radius + b.Radius);
        double internalGap = distance - Math.Abs(a.Radius - b.Radius);
        if (external > tolerance || internalGap < -tolerance)
            return;   // separate, or one strictly inside the other

        if (Math.Abs(external) <= tolerance)
        {
            AddArcArc(a, b, a.Center + unit * a.Radius, tolerance, contacts, before);
            return;
        }
        if (Math.Abs(internalGap) <= tolerance)
        {
            var towards = a.Radius > b.Radius ? unit : -unit;
            AddArcArc(a, b, a.Center + towards * a.Radius, tolerance, contacts, before);
            return;
        }

        double axial = (distance * distance + a.Radius * a.Radius - b.Radius * b.Radius) / (2 * distance);
        double half = Math.Sqrt(Math.Max(a.Radius * a.Radius - axial * axial, 0));
        var foot = a.Center + unit * axial;
        var offset = unit.Perpendicular * half;
        AddArcArc(a, b, foot + offset, tolerance, contacts, before);
        AddArcArc(a, b, foot - offset, tolerance, contacts, before);
    }

    // ---- the cubic cases ----

    /// <summary>
    /// A cubic against a LINE or an ARC. Both conics have an implicit form that is a
    /// polynomial in the cubic's own parameter — <c>n̂·(C(t) − a)</c> for a line (degree 3,
    /// and numerically a SIGNED DISTANCE because n̂ is a unit normal) and
    /// <c>|C(t) − c|² − r²</c> for a circle (degree 6) — so the contact parameters are the
    /// real roots of one univariate polynomial on [0, 1]. No subdivision and no fitting
    /// tolerance enter.
    ///
    /// <para><b>Tangency needs no second epsilon.</b> A tangential contact is a DOUBLE root,
    /// which a sign change cannot see, so the polynomial's CRITICAL POINTS are offered as
    /// candidates alongside its sign-change roots and the ordinary distance filters decide:
    /// a critical point genuinely on the other edge is within <paramref name="tolerance"/> of
    /// it and is kept, one merely near a turning point is not and is dropped. That keeps the
    /// tier's single length threshold, snaps a near-tangency to ONE touch point (the turning
    /// point) rather than to two crossings a nanometre apart, and needs the caller to know
    /// nothing about multiplicities.</para>
    /// </summary>
    private static void ConicBezier(
        in CurvedEdge2d conic, in CurvedEdge2d bezier, double tolerance,
        List<Contact> contacts, int before, bool swap)
    {
        Span<double> polynomial = stackalloc double[7];
        int degree;
        if (conic.IsArc)
        {
            degree = CircleResidual(conic, bezier, polynomial);
        }
        else
        {
            var direction = conic.End - conic.Start;
            double length = direction.Length;
            // Exact-zero guard: a degenerate segment has no normal to solve against.
            if (!(length > 0))
                return;
            degree = LineResidual(direction / length, conic.Start, bezier, polynomial);
        }

        Span<double> candidates = stackalloc double[16];
        int count = CandidateParameters(polynomial[..(degree + 1)], candidates);
        for (int i = 0; i < count; i++)
        {
            double t = candidates[i];
            var point = bezier.PointAt(t);
            if (conic.DistanceTo(point) > tolerance || bezier.DistanceTo(point) > tolerance)
                continue;
            double tConic = conic.ParameterOf(conic.NearestPoint(point));
            var contact = swap ? new Contact(t, tConic, point) : new Contact(tConic, t, point);
            Add(contacts, before, tolerance, contact);
        }
    }

    /// <summary>Coefficients of <c>n̂·(C(t) − a)</c> — the signed distance from the line
    /// through <paramref name="origin"/> with unit direction <paramref name="unit"/>, as a
    /// cubic in the Bézier's parameter.</summary>
    private static int LineResidual(
        in Vector2d unit, in Vector2d origin, in CurvedEdge2d bezier, Span<double> into)
    {
        var normal = unit.Perpendicular;
        var (p0, p1, p2, p3) = bezier.ControlPoints;
        // Power basis of the cubic, projected onto the normal.
        double c0 = normal.Dot(p0 - origin);
        double c1 = normal.Dot((p1 - p0) * 3);
        double c2 = normal.Dot((p0 - p1 * 2 + p2) * 3);
        double c3 = normal.Dot(p3 - p0 + (p1 - p2) * 3);
        into[0] = c0;
        into[1] = c1;
        into[2] = c2;
        into[3] = c3;
        return 3;
    }

    /// <summary>Coefficients of <c>|C(t) − c|² − r²</c>, a degree-6 polynomial in the
    /// Bézier's parameter.</summary>
    private static int CircleResidual(
        in CurvedEdge2d arc, in CurvedEdge2d bezier, Span<double> into)
    {
        var (p0, p1, p2, p3) = bezier.ControlPoints;
        var c = arc.Center;
        Span<double> x = [p0.X - c.X, ((p1 - p0) * 3).X, ((p0 - p1 * 2 + p2) * 3).X, (p3 - p0 + (p1 - p2) * 3).X];
        Span<double> y = [p0.Y - c.Y, ((p1 - p0) * 3).Y, ((p0 - p1 * 2 + p2) * 3).Y, (p3 - p0 + (p1 - p2) * 3).Y];
        into[..7].Clear();
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
                into[i + j] += x[i] * x[j] + y[i] * y[j];
        }
        into[0] -= arc.Radius * arc.Radius;
        return 6;
    }

    /// <summary>
    /// The parameters worth testing: every sign-change root of the polynomial on [0, 1] plus
    /// every critical point (a double root shows as the latter and not the former — see
    /// <see cref="ConicBezier"/>).
    /// </summary>
    private static int CandidateParameters(ReadOnlySpan<double> polynomial, Span<double> into)
    {
        Span<double> critical = stackalloc double[8];
        int criticalCount = CriticalPoints(polynomial, critical);

        int count = 0;
        double previous = 0;
        double previousValue = Evaluate(polynomial, 0);
        for (int i = 0; i <= criticalCount; i++)
        {
            double next = i == criticalCount ? 1 : critical[i];
            double nextValue = Evaluate(polynomial, next);
            // Exact-zero test: a root sitting exactly on a partition point is taken verbatim.
            if (previousValue == 0)
                Accept(previous, into, ref count);
            else if (nextValue == 0)
                Accept(next, into, ref count);
            else if (previousValue > 0 != nextValue > 0)
                Accept(Bisect(polynomial, previous, next, previousValue), into, ref count);
            previous = next;
            previousValue = nextValue;
        }
        for (int i = 0; i < criticalCount; i++)
            Accept(critical[i], into, ref count);
        return count;

        static void Accept(double t, Span<double> into, ref int count)
        {
            if (t >= 0 && t <= 1 && count < into.Length)
                into[count++] = t;
        }
    }

    /// <summary>Roots of the derivative on (0, 1), ascending — found by recursing on the
    /// derivative, so each level's roots partition [0, 1] into intervals the level above is
    /// MONOTONE on and a bracketed bisection cannot miss a simple root.</summary>
    private static int CriticalPoints(ReadOnlySpan<double> polynomial, Span<double> into)
    {
        int degree = polynomial.Length - 1;
        while (degree > 0 && polynomial[degree] == 0)
            degree--;
        if (degree <= 1)
            return 0;

        Span<double> derivative = stackalloc double[degree];
        for (int i = 1; i <= degree; i++)
            derivative[i - 1] = polynomial[i] * i;
        return RootsInUnit(derivative, into);
    }

    /// <summary>Simple roots of a polynomial strictly inside (0, 1), ascending.</summary>
    private static int RootsInUnit(ReadOnlySpan<double> polynomial, Span<double> into)
    {
        int degree = polynomial.Length - 1;
        while (degree > 0 && polynomial[degree] == 0)
            degree--;
        if (degree <= 0)
            return 0;
        if (degree == 1)
        {
            double root = -polynomial[0] / polynomial[1];
            if (root > 0 && root < 1)
            {
                into[0] = root;
                return 1;
            }
            return 0;
        }

        Span<double> critical = stackalloc double[8];
        int criticalCount = CriticalPoints(polynomial[..(degree + 1)], critical);

        int count = 0;
        double previous = 0;
        double previousValue = Evaluate(polynomial, 0);
        for (int i = 0; i <= criticalCount; i++)
        {
            double next = i == criticalCount ? 1 : critical[i];
            double nextValue = Evaluate(polynomial, next);
            if (previousValue != 0 && nextValue != 0 && previousValue > 0 != nextValue > 0
                && count < into.Length)
            {
                into[count++] = Bisect(polynomial, previous, next, previousValue);
            }
            previous = next;
            previousValue = nextValue;
        }
        return count;
    }

    /// <summary>Bisection over a bracket that already straddles a root.</summary>
    private static double Bisect(ReadOnlySpan<double> polynomial, double lo, double hi, double atLo)
    {
        for (int i = 0; i < 60; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (mid <= lo || mid >= hi)
                break;
            double value = Evaluate(polynomial, mid);
            if (value == 0)
                return mid;
            if (value > 0 == atLo > 0)
            {
                lo = mid;
                atLo = value;
            }
            else
            {
                hi = mid;
            }
        }
        return 0.5 * (lo + hi);
    }

    private static double Evaluate(ReadOnlySpan<double> polynomial, double t)
    {
        double value = 0;
        for (int i = polynomial.Length - 1; i >= 0; i--)
            value = value * t + polynomial[i];
        return value;
    }

    /// <summary>
    /// Two cubics. Implicitizing one gives a degree-9 polynomial whose coefficients are a
    /// 3×3 resultant of quadratics — exact in principle and badly conditioned in practice —
    /// so this takes the standard route instead: recursive de Casteljau subdivision with a
    /// bounding-box rejection, down to boxes no larger than <paramref name="tolerance"/>.
    /// The threshold is therefore a LENGTH, like every other in this tier, and a tangential
    /// contact comes out as one cluster that <see cref="Add"/> reports once.
    ///
    /// <para>The pathological input for a subdivision — two curves that OVERLAP — cannot
    /// reach here: <see cref="SameCarrier"/> is tested first and answers it in closed form.
    /// What remains is bounded by a node budget, and a curve pair that exhausts it has
    /// contributed every contact its boxes could isolate.</para>
    /// </summary>
    private static void BezierBezier(
        in CurvedEdge2d a, in CurvedEdge2d b, double tolerance, List<Contact> contacts, int before)
    {
        const int budget = 4096;
        var stack = new Stack<(double A0, double A1, double B0, double B1, CurvedEdge2d Ea, CurvedEdge2d Eb)>();
        stack.Push((0, 1, 0, 1, a, b));
        int visited = 0;
        while (stack.Count > 0 && visited++ < budget)
        {
            var (a0, a1, b0, b1, ea, eb) = stack.Pop();
            var boxA = ea.Bounds();
            var boxB = eb.Bounds();
            if (boxA.Min.X - tolerance > boxB.Max.X || boxB.Min.X - tolerance > boxA.Max.X
                || boxA.Min.Y - tolerance > boxB.Max.Y || boxB.Min.Y - tolerance > boxA.Max.Y)
            {
                continue;
            }

            double extentA = Math.Max(boxA.Max.X - boxA.Min.X, boxA.Max.Y - boxA.Min.Y);
            double extentB = Math.Max(boxB.Max.X - boxB.Min.X, boxB.Max.Y - boxB.Min.Y);
            // An EIGHTH of the tolerance, not the tolerance: the box rejection is padded by
            // the tolerance, so a tangential contact legitimately keeps several leaves alive
            // and they must all land inside one dedupe radius. A transversal one converges
            // onto a single point through the polish below whatever the leaf size, so this
            // only costs three extra levels on the case that needs them.
            if (extentA <= 0.125 * tolerance && extentB <= 0.125 * tolerance)
            {
                double ta = 0.5 * (a0 + a1);
                double tb = 0.5 * (b0 + b1);
                Polish(a, b, ref ta, ref tb);
                Add(contacts, before, tolerance, new Contact(ta, tb, a.PointAt(ta)));
                continue;
            }

            if (extentA >= extentB)
            {
                double mid = 0.5 * (a0 + a1);
                stack.Push((a0, mid, b0, b1, ea.Sub(0, 0.5), eb));
                stack.Push((mid, a1, b0, b1, ea.Sub(0.5, 1), eb));
            }
            else
            {
                double mid = 0.5 * (b0 + b1);
                stack.Push((a0, a1, b0, mid, ea, eb.Sub(0, 0.5)));
                stack.Push((a0, a1, mid, b1, ea, eb.Sub(0.5, 1)));
            }
        }
    }

    /// <summary>
    /// Newton on <c>C_a(s) − C_b(t) = 0</c> from an already-isolated pair of parameters. It
    /// is what makes every leaf of one transversal cluster converge to the SAME point, so
    /// <see cref="Add"/>'s dedupe reports the crossing once rather than once per leaf — the
    /// subdivision isolates, the polish decides. A singular Jacobian is a TANGENTIAL contact,
    /// where there is no isolated root to converge to; the isolate is kept verbatim and the
    /// leaf size is what bounds the cluster.
    /// </summary>
    private static void Polish(in CurvedEdge2d a, in CurvedEdge2d b, ref double s, ref double t)
    {
        for (int i = 0; i < 12; i++)
        {
            var residual = a.PointAt(s) - b.PointAt(t);
            var da = a.DerivativeAt(s);
            var db = b.DerivativeAt(t);
            double determinant = da.Cross(-db);
            // Exact-zero guard: parallel tangents mean a tangential contact, not a root.
            if (determinant == 0)
                return;
            double ds = (-residual).Cross(-db) / determinant;
            double dt = da.Cross(-residual) / determinant;
            double nextS = Math.Clamp(s + ds, 0, 1);
            double nextT = Math.Clamp(t + dt, 0, 1);
            if (nextS == s && nextT == t)
                return;
            s = nextS;
            t = nextT;
        }
    }

    // ---- bookkeeping ----

    private static void AddSolved(
        in CurvedEdge2d line, in CurvedEdge2d arc, in Vector2d point, double t,
        double tolerance, List<Contact> contacts, int before, bool swap)
    {
        // The algebra solves against the INFINITE line and FULL circle; both trims are then
        // one distance test each, which also absorbs the parametric end cases.
        if (line.DistanceTo(point) > tolerance || arc.DistanceTo(point) > tolerance)
            return;
        double tArc = arc.ParameterOf(point);
        var contact = swap ? new Contact(tArc, t, point) : new Contact(t, tArc, point);
        Add(contacts, before, tolerance, contact);
    }

    private static void AddArcArc(
        in CurvedEdge2d a, in CurvedEdge2d b, in Vector2d point,
        double tolerance, List<Contact> contacts, int before)
    {
        if (a.DistanceTo(point) > tolerance || b.DistanceTo(point) > tolerance)
            return;
        Add(contacts, before, tolerance, new Contact(a.ParameterOf(point), b.ParameterOf(point), point));
    }

    private static void AddIfOnBoth(
        in CurvedEdge2d owner, in CurvedEdge2d other, in Vector2d point, double t,
        double tolerance, List<Contact> contacts, int before, bool swap)
    {
        if (other.DistanceTo(point) > tolerance)
            return;
        double tOther = other.ParameterOf(other.NearestPoint(point));
        var contact = swap ? new Contact(tOther, t, point) : new Contact(t, tOther, point);
        Add(contacts, before, tolerance, contact);
    }

    private static void Add(List<Contact> contacts, int before, double tolerance, in Contact contact)
    {
        for (int i = before; i < contacts.Count; i++)
        {
            if (contacts[i].Point.DistanceTo(contact.Point) <= tolerance)
                return;
        }
        contacts.Add(contact);
    }
}
