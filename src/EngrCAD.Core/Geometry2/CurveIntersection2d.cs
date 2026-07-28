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

        if (!a.IsArc && !b.IsArc)
            LineLine(a, b, tolerance, contacts, before);
        else if (a.IsArc && !b.IsArc)
            LineArc(b, a, tolerance, contacts, before, swap: true);
        else if (!a.IsArc && b.IsArc)
            LineArc(a, b, tolerance, contacts, before, swap: false);
        else
            ArcArc(a, b, tolerance, contacts, before);
    }

    /// <summary>
    /// Do the two edges lie on the SAME carrier — one line, or one circle? Collinearity is
    /// decided by the exact <see cref="Predicates2d.Orient2dSign"/>; cocircularity by centre
    /// and radius agreeing within <paramref name="tolerance"/>, the same length threshold
    /// vertices merge at.
    /// </summary>
    public static bool SameCarrier(in CurvedEdge2d a, in CurvedEdge2d b, double tolerance)
    {
        if (a.IsArc != b.IsArc)
            return false;
        if (a.IsArc)
            return a.Center.DistanceTo(b.Center) <= tolerance && Math.Abs(a.Radius - b.Radius) <= tolerance;
        return Predicates2d.Orient2dSign(a.Start, a.End, b.Start) == 0
            && Predicates2d.Orient2dSign(a.Start, a.End, b.End) == 0;
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
