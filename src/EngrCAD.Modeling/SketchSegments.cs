using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// One piece of a sketch loop. Each kind supplies the exact math every lowering needs:
/// signed area (Green's theorem term), bounds, the exact B-Rep curve, point distance,
/// and y-monotone pieces for ray-parity inside tests.
/// </summary>
internal abstract class SketchSegment
{
    public abstract Vector2d Start { get; }
    public abstract Vector2d End { get; }
    public abstract SketchSegment Reversed();

    /// <summary>½∮(x dy − y dx) along the segment (Green's theorem area term).</summary>
    public abstract double SignedAreaContribution();

    public abstract Aabb Bounds();

    /// <summary>Exact curve in sketch-local coordinates (the XY plane, z = 0).</summary>
    public abstract Curve3d ToCurve();

    /// <summary>
    /// The same geometry in the 2D curve vocabulary — exact, never sampled. The two
    /// families describe the same three shapes, so this is a re-expression: a
    /// <c>LineSeg</c> IS a <see cref="Line2d"/>, an <c>ArcSeg</c>'s signed sweep IS an
    /// <see cref="Arc2d"/>'s (both carry orientation intrinsically), and a cubic segment IS
    /// a cubic <see cref="BezierCurve2d"/>.
    /// </summary>
    public abstract Curve2d ToCurve2d();

    public abstract double Distance(in Vector2d point);

    /// <summary>
    /// This segment placed on a rigid 2D frame: the frame's origin plus
    /// <c>x·frame.X + y·frame.Y</c>, with Y the X axis turned a quarter turn
    /// counter-clockwise. A rotation and a translation and nothing else, so every
    /// segment kind moves EXACTLY — a radius stays a radius and an ellipse keeps both
    /// semi-axes, with no similarity to spend and nothing to re-fit.
    /// </summary>
    /// <remarks>
    /// Rigid rather than affine deliberately: the consumer is a sheet-metal flange's own
    /// local coordinates, whose 2D frame in the blank and 3D frame on the wall are the
    /// SAME rigid frame (that is what makes the unfold bookkeeping rather than geometry).
    /// A general affine map would change an arc into an ellipse and put a re-fit in a
    /// path whose whole claim is that nothing is fitted.
    /// </remarks>
    public abstract SketchSegment Placed(in Vector2d origin, in Vector2d xAxis);

    /// <summary>
    /// This segment reflected in the y axis (<c>x → −x</c>). Every kind is built from
    /// POINTS and VECTORS, and a reflection is linear, so mirroring those IS the mirrored
    /// curve — no parameter is re-derived and no angle is recomputed from a vector.
    /// <para>The traversal SENSE is not repaired here: a reflection reverses a loop's
    /// winding, and restoring it is the loop's business (reverse the segment ORDER and each
    /// segment), which is one rule in one place rather than a half-repair per kind.</para>
    /// </summary>
    public abstract SketchSegment MirroredInY();

    /// <summary>The reflection of one point in the y axis.</summary>
    private protected static Vector2d Flip(in Vector2d p) => new(-p.X, p.Y);

    /// <summary>The rigid image of one point — every override's shared arithmetic, so a
    /// segment kind cannot place its own points by a different rule from its neighbours'
    /// joints.</summary>
    private protected static Vector2d Place(in Vector2d origin, in Vector2d xAxis, in Vector2d local) =>
        origin + xAxis * local.X + xAxis.Perpendicular * local.Y;

    /// <summary>The rigid image of a DIRECTION (no translation) — for the stored vectors
    /// an ellipse carries.</summary>
    private protected static Vector2d PlaceVector(in Vector2d xAxis, in Vector2d local) =>
        xAxis * local.X + xAxis.Perpendicular * local.Y;

    /// <summary>Splits into y-monotone pieces for robust even–odd parity.</summary>
    public abstract IEnumerable<MonotonePiece> MonotonePieces();

    /// <summary>
    /// Appends this segment's polyline approximation — <see cref="Start"/> inclusive,
    /// <see cref="End"/> EXCLUSIVE, so chaining a closed loop's segments produces each joint
    /// exactly once — deviating from the true curve by no more than
    /// <paramref name="chordTolerance"/>. Straight segments are exact; curves are not (this
    /// is the lossy step of the region pipeline, see <c>Sketch.ToRegions</c>).
    /// </summary>
    public abstract void Flatten(double chordTolerance, List<Vector2d> into);
}

/// <summary>A y-monotone stretch of a segment: the parity ray test reduces to the
/// classic half-open endpoint rule plus one x-at-y evaluation.</summary>
internal abstract class MonotonePiece
{
    public double Y0 { get; protected init; }
    public double Y1 { get; protected init; }
    public abstract double XAtY(double y);
}

// ---------------------------------------------------------------------------- line

internal sealed class LineSeg(Vector2d start, Vector2d end) : SketchSegment
{
    public override Vector2d Start => start;
    public override Vector2d End => end;

    public override SketchSegment Reversed() => new LineSeg(end, start);

    public override double SignedAreaContribution() => 0.5 * start.Cross(end);

    public override Aabb Bounds() => new(
        (Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), 0),
        (Math.Max(start.X, end.X), Math.Max(start.Y, end.Y), 0));

    public override Curve3d ToCurve() => new Line3d((start.X, start.Y, 0), (end.X, end.Y, 0));

    public override Curve2d ToCurve2d() => new Line2d(start, end);

    public override SketchSegment Placed(in Vector2d origin, in Vector2d xAxis) =>
        new LineSeg(Place(origin, xAxis, start), Place(origin, xAxis, end));

    public override SketchSegment MirroredInY() => new LineSeg(Flip(start), Flip(end));

    public override double Distance(in Vector2d point)
    {
        var direction = end - start;
        double lengthSquared = direction.LengthSquared;
        double t = lengthSquared < 1e-24 ? 0 : Math.Clamp((point - start).Dot(direction) / lengthSquared, 0, 1);
        return point.DistanceTo(start + direction * t);
    }

    public override IEnumerable<MonotonePiece> MonotonePieces()
    {
        if (Math.Abs(start.Y - end.Y) > 0)
            yield return new LinePiece(start, end);
    }

    /// <summary>Exact: a line's polyline approximation is the line.</summary>
    public override void Flatten(double chordTolerance, List<Vector2d> into) => into.Add(start);

    private sealed class LinePiece : MonotonePiece
    {
        private readonly Vector2d _a, _b;

        public LinePiece(Vector2d a, Vector2d b)
        {
            _a = a;
            _b = b;
            Y0 = a.Y;
            Y1 = b.Y;
        }

        public override double XAtY(double y) => _a.X + (_b.X - _a.X) * ((y - _a.Y) / (_b.Y - _a.Y));
    }
}

// ----------------------------------------------------------------------------- arc

/// <summary>Circular arc: signed <paramref name="sweep"/> (positive = CCW) from
/// <paramref name="startAngle"/>; |sweep| = 2π is a full circle.</summary>
internal sealed class ArcSeg(Vector2d center, double radius, double startAngle, double sweep) : SketchSegment
{
    public Vector2d Center => center;
    public double Radius => radius;
    public double StartAngle => startAngle;
    public double Sweep => sweep;

    private Vector2d PointAt(double angle) =>
        center + new Vector2d(Math.Cos(angle), Math.Sin(angle)) * radius;

    public override Vector2d Start => PointAt(startAngle);

    /// <summary>
    /// A full turn's end point <b>IS</b> its start point — an exact substitution, not a
    /// snap. <c>sin(2π)</c> is −2.449e-16, so <c>PointAt(startAngle + sweep)</c> lands
    /// r·2.4e-16 away from <see cref="Start"/>, and a closed curve whose two ends merely
    /// evaluate near each other is not closed: the gap is a band no parity ray can cross
    /// (see <see cref="MonotonePieces"/>), and it also leaves <c>End − Start</c> as noise
    /// in <see cref="SignedAreaContribution"/> where the exact answer is zero.
    /// </summary>
    public override Vector2d End => IsFullCircle ? Start : PointAt(startAngle + sweep);

    // Angular round-off slack: full circles are constructed with sweep = ±2π exactly,
    // so 1e-12 only absorbs arithmetic noise (tighter than Tolerance.Default.Angular).
    public bool IsFullCircle => Math.Abs(Math.Abs(sweep) - 2 * Math.PI) < 1e-12;

    public override SketchSegment Reversed() => new ArcSeg(center, radius, startAngle + sweep, -sweep);

    /// <summary>A rigid frame turns an arc by the frame's own angle and leaves the radius
    /// and the SWEEP untouched — the sweep is a signed angle, and a rotation is what this
    /// map is, so nothing about the arc's shape or handedness can move.</summary>
    public override SketchSegment Placed(in Vector2d origin, in Vector2d xAxis) => new ArcSeg(
        Place(origin, xAxis, center), radius, startAngle + Math.Atan2(xAxis.Y, xAxis.X), sweep);

    /// <summary>A reflection turns <c>(cos θ, sin θ)</c> into <c>(cos(π − θ), sin(π − θ))</c>,
    /// so the start angle reflects about π/2 and the sweep changes SIGN — which is the same
    /// statement as "a reflection reverses handedness", written where it is exact.</summary>
    public override SketchSegment MirroredInY() =>
        new ArcSeg(Flip(center), radius, Math.PI - startAngle, -sweep);

    public override double SignedAreaContribution() =>
        0.5 * (center.Cross(End - Start) + radius * radius * sweep);

    public override Aabb Bounds()
    {
        var bounds = new Aabb((Start.X, Start.Y, 0), (Start.X, Start.Y, 0)).Union((End.X, End.Y, 0));
        foreach (double axis in AxisAngles())
        {
            var p = PointAt(axis);
            bounds = bounds.Union((p.X, p.Y, 0));
        }
        return bounds;
    }

    /// <summary>Axis-extreme angles (k·π/2) that fall inside the sweep.</summary>
    private IEnumerable<double> AxisAngles()
    {
        double a0 = Math.Min(startAngle, startAngle + sweep);
        double a1 = Math.Max(startAngle, startAngle + sweep);
        for (double k = Math.Ceiling(a0 / (Math.PI / 2)); k * Math.PI / 2 <= a1; k++)
            yield return k * Math.PI / 2;
    }

    public override Curve3d ToCurve()
    {
        var center3 = new Vector3d(center.X, center.Y, 0);
        if (IsFullCircle)
        {
            var toStart = (Start - center).Normalized();
            var x = new Vector3d(toStart.X, toStart.Y, 0);
            // yDir chosen so increasing curve parameter follows the sweep direction.
            var y = sweep > 0
                ? new Vector3d(-toStart.Y, toStart.X, 0)
                : new Vector3d(toStart.Y, -toStart.X, 0);
            return new Circle3d(center3, x, y, radius);
        }
        // Partial arcs as trimmed circles (Underlying Circle3d) so downstream code —
        // rim features, promotions — can classify them; the signed sweep encodes the
        // direction (CurveSegment maps decreasing parameters too).
        var circle = new Circle3d(center3, Vector3d.UnitX, Vector3d.UnitY, radius);
        return new CurveSegment(circle, startAngle, startAngle + sweep);
    }

    /// <summary>Direct: <see cref="Arc2d"/> carries the same signed sweep, so orientation
    /// crosses the bridge as data rather than as a flag to be re-derived.</summary>
    public override Curve2d ToCurve2d() => new Arc2d(center, radius, startAngle, sweep);

    public override double Distance(in Vector2d point)
    {
        var offset = point - center;
        double angle = Math.Atan2(offset.Y, offset.X);
        if (AngleInSweep(angle))
            return Math.Abs(offset.Length - radius);
        return Math.Min(point.DistanceTo(Start), point.DistanceTo(End));
    }

    private bool AngleInSweep(double angle)
    {
        if (IsFullCircle)
            return true;
        double from = sweep > 0 ? startAngle : startAngle + sweep;
        double span = Math.Abs(sweep);
        double delta = (angle - from) % (2 * Math.PI);
        if (delta < 0)
            delta += 2 * Math.PI;
        return delta <= span;
    }

    public override IEnumerable<MonotonePiece> MonotonePieces()
    {
        // Split at the y-extreme angles (π/2 + kπ) so each piece is y-monotone.
        double a0 = Math.Min(startAngle, startAngle + sweep);
        double a1 = Math.Max(startAngle, startAngle + sweep);
        var breaks = new List<double> { a0 };
        for (double k = Math.Ceiling((a0 - Math.PI / 2) / Math.PI); ; k++)
        {
            double angle = Math.PI / 2 + k * Math.PI;
            if (angle >= a1)
                break;
            if (angle > a0)
                breaks.Add(angle);
        }
        breaks.Add(a1);

        // The two OUTER ordinates come from the stored endpoints rather than from
        // PointAt, so a closed arc's last piece ends exactly where its first began.
        // Interior breaks need no such care: consecutive pieces call PointAt with the
        // same argument, so they already agree bit for bit. Which stored endpoint is
        // which depends on the sweep's sign, since a0/a1 are ordered by angle.
        var atA0 = sweep >= 0 ? Start : End;
        var atA1 = sweep >= 0 ? End : Start;

        for (int i = 0; i + 1 < breaks.Count; i++)
        {
            var from = i == 0 ? atA0 : PointAt(breaks[i]);
            var to = i + 2 == breaks.Count ? atA1 : PointAt(breaks[i + 1]);
            if (Math.Abs(from.Y - to.Y) <= 0)
                continue;
            // The branch (left/right half of the circle) is fixed within a piece.
            double mid = (breaks[i] + breaks[i + 1]) / 2;
            bool rightBranch = Math.Cos(mid) >= 0;
            yield return new ArcPiece(center, radius, from.Y, to.Y, rightBranch);
        }
    }

    /// <summary>
    /// Uniform angular subdivision sized by the sagitta r·(1 − cos(Δ/2)) ≤ tolerance, so no
    /// chord bulges further than <paramref name="chordTolerance"/> from the true arc.
    /// Capped at 90° per chord so even a very coarse tolerance keeps a circle a circle.
    /// </summary>
    public override void Flatten(double chordTolerance, List<Vector2d> into)
    {
        double maxAngle = chordTolerance >= radius
            ? Math.PI / 2
            : Math.Min(Math.PI / 2, 2 * Math.Acos(1 - chordTolerance / radius));
        int count = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / maxAngle));
        for (int i = 0; i < count; i++)
            into.Add(PointAt(startAngle + sweep * i / count));
    }

    private sealed class ArcPiece : MonotonePiece
    {
        private readonly Vector2d _center;
        private readonly double _radius;
        private readonly bool _rightBranch;

        public ArcPiece(Vector2d center, double radius, double y0, double y1, bool rightBranch)
        {
            _center = center;
            _radius = radius;
            _rightBranch = rightBranch;
            Y0 = y0;
            Y1 = y1;
        }

        public override double XAtY(double y)
        {
            double dy = y - _center.Y;
            double dx = Math.Sqrt(Math.Max(0, _radius * _radius - dy * dy));
            return _center.X + (_rightBranch ? dx : -dx);
        }
    }
}

// ------------------------------------------------------------------------- ellipse

/// <summary>
/// Elliptical arc <c>C + A·cos θ + B·sin θ</c> over a signed sweep — <see cref="ArcSeg"/>
/// generalized, and exact in all three representations because the whole chain down to
/// <see cref="Ellipse3d"/> already existed (STEP write/read, `BrepArchive`, IGES,
/// tessellation density, plane-section chord counts).
/// </summary>
/// <remarks>
/// <para><b>Every quantity here is closed form except the distance.</b> The signed area is
/// the same shape as the circular one — <c>½(C × (End − Start) + (A × B)·sweep)</c>,
/// which reduces to <see cref="ArcSeg.SignedAreaContribution"/> exactly when
/// A = (r, 0) and B = (0, r) — and the y-monotone pieces invert
/// <c>y − C.y = R·sin(θ + φ)</c> analytically, so the ray-parity crossing is as exact as a
/// circle's rather than a bisection like <c>CubicSeg</c>'s.</para>
/// <para><b>The distance is not.</b> A point's distance to an ellipse is a quartic root,
/// so <see cref="Distance"/> delegates to the shared <see cref="Curve2d"/> scan-and-Newton
/// rather than restating it — the segment and the 2D curve family then agree by
/// construction, which is the rule the repo keeps re-learning the hard way. That makes an
/// elliptical sketch's field the most expensive member of the family per sample, a 65-point
/// scan plus a bracketed Newton. <c>SketchRegion</c> now carries its own kernel for it
/// (<c>EllipseData</c>/<c>EllipseRefine</c>): the scan parameters are the same for every
/// query, so it bakes their curve points and hoists the Newton step's cosine/sine pair,
/// which is bit-exact memoization rather than a different method. That kernel transcribes
/// <em>this</em> delegation, so the two are held bit-equal by test — if the shared solve
/// changes, the transcription must follow.</para>
/// </remarks>
internal sealed class EllipseSeg : SketchSegment
{
    private readonly Vector2d _center, _a, _b;
    private readonly double _startAngle, _sweep;
    private readonly Ellipse2d _curve;

    public EllipseSeg(Vector2d center, Vector2d semiAxisX, Vector2d semiAxisY, double startAngle, double sweep)
    {
        _center = center;
        _a = semiAxisX;
        _b = semiAxisY;
        _startAngle = startAngle;
        _sweep = sweep;
        _curve = new Ellipse2d(center, semiAxisX, semiAxisY, startAngle, sweep);
    }

    public Vector2d Center => _center;
    public Vector2d SemiAxisX => _a;
    public Vector2d SemiAxisY => _b;
    public double StartAngle => _startAngle;
    public double Sweep => _sweep;

    private Vector2d PointAt(double angle) =>
        _center + _a * Math.Cos(angle) + _b * Math.Sin(angle);

    public override Vector2d Start => PointAt(_startAngle);
    public override Vector2d End => PointAt(_startAngle + _sweep);

    /// <summary>Angular round-off slack, matching <see cref="ArcSeg.IsFullCircle"/>.</summary>
    public bool IsFullEllipse => Math.Abs(Math.Abs(_sweep) - 2 * Math.PI) < 1e-12;

    public override SketchSegment Reversed() =>
        new EllipseSeg(_center, _a, _b, _startAngle + _sweep, -_sweep);

    /// <summary>The centre and BOTH semi-axis VECTORS ride the frame; the polar parameters
    /// are untouched because they are measured in the ellipse's own axis frame, which the
    /// rotation carries with it — the same reasoning the constrained-sketch rebuild uses,
    /// and the reason a rotated ellipse is the ordinary case here rather than a third
    /// parameter.</summary>
    public override SketchSegment Placed(in Vector2d origin, in Vector2d xAxis) => new EllipseSeg(
        Place(origin, xAxis, _center),
        PlaceVector(xAxis, _a),
        PlaceVector(xAxis, _b),
        _startAngle,
        _sweep);

    /// <summary>The centre and BOTH semi-axis vectors reflect and the polar parameters
    /// stay: <c>P(θ) = C + A cos θ + B sin θ</c> is linear in C, A and B, so a linear map
    /// of those IS the mapped curve. The handedness change rides in the reflected axis
    /// pair rather than in a sign on the sweep, which is why nothing is re-derived.</summary>
    public override SketchSegment MirroredInY() =>
        new EllipseSeg(Flip(_center), Flip(_a), Flip(_b), _startAngle, _sweep);

    /// <summary>
    /// ½∮(x dy − y dx) = ½(C × (End − Start) + (A × B)·sweep), from
    /// P × P′ = −sin θ (C × A) + cos θ (C × B) + (A × B) integrated over the sweep. The
    /// first two terms collapse into the STORED endpoints, which is both shorter and the
    /// closed-curve rule (an endpoint re-derived from its angle is not bit-equal to the
    /// neighbour's stored copy).
    /// </summary>
    public override double SignedAreaContribution() =>
        0.5 * (_center.Cross(End - Start) + _a.Cross(_b) * _sweep);

    public override Aabb Bounds()
    {
        var bounds = new Aabb((Start.X, Start.Y, 0), (Start.X, Start.Y, 0)).Union((End.X, End.Y, 0));
        // dx/dθ = 0 at tan θ = B.x/A.x and dy/dθ = 0 at tan θ = B.y/A.y (each with its
        // +π partner), so the extremes are exact rather than sampled.
        foreach (double angle in ExtremeAngles(_a.X, _b.X))
            bounds = bounds.Union(Lift(PointAt(angle)));
        foreach (double angle in ExtremeAngles(_a.Y, _b.Y))
            bounds = bounds.Union(Lift(PointAt(angle)));
        return bounds;

        static Vector3d Lift(in Vector2d p) => new(p.X, p.Y, 0);
    }

    /// <summary>The angles inside the sweep where <c>−a·sin θ + b·cos θ = 0</c>.</summary>
    private IEnumerable<double> ExtremeAngles(double a, double b)
    {
        // Exact-zero guard: a component that is identically zero in both axes never
        // varies, so it has no extreme to report.
        if (a == 0 && b == 0)
            yield break;
        double baseAngle = Math.Atan2(b, a);
        double lo = Math.Min(_startAngle, _startAngle + _sweep);
        double hi = Math.Max(_startAngle, _startAngle + _sweep);
        for (double k = Math.Ceiling((lo - baseAngle) / Math.PI); ; k++)
        {
            double angle = baseAngle + k * Math.PI;
            if (angle > hi)
                yield break;
            if (angle >= lo)
                yield return angle;
        }
    }

    /// <summary>One rule: the exact 3D form is <see cref="Ellipse2d.ToCurve3d"/>'s, on the
    /// world XY plane the sketch lives in.</summary>
    public override Curve3d ToCurve() => _curve.ToCurve3d();

    public override Curve2d ToCurve2d() => _curve;

    /// <summary>Delegated, deliberately — see the class remarks.</summary>
    public override double Distance(in Vector2d point) => _curve.DistanceTo(point);

    public override IEnumerable<MonotonePiece> MonotonePieces()
    {
        // y(θ) = C.y + R·sin(θ + φ) with R = hypot(A.y, B.y) and φ = atan2(A.y, B.y), so
        // the y extremes are θ + φ = ±π/2 + kπ.
        double r = Math.Sqrt(_a.Y * _a.Y + _b.Y * _b.Y);
        // Exact-zero guard: a degenerate y (both axes horizontal) never crosses a ray.
        if (!(r > 0))
            yield break;
        double phase = Math.Atan2(_a.Y, _b.Y);

        double lo = Math.Min(_startAngle, _startAngle + _sweep);
        double hi = Math.Max(_startAngle, _startAngle + _sweep);
        var breaks = new List<double> { lo };
        for (double k = Math.Ceiling((lo + phase - Math.PI / 2) / Math.PI); ; k++)
        {
            double angle = Math.PI / 2 + k * Math.PI - phase;
            if (angle >= hi)
                break;
            if (angle > lo)
                breaks.Add(angle);
        }
        breaks.Add(hi);

        for (int i = 0; i + 1 < breaks.Count; i++)
        {
            var from = PointAt(breaks[i]);
            var to = PointAt(breaks[i + 1]);
            if (Math.Abs(from.Y - to.Y) <= 0)
                continue;
            // Within a piece θ + φ stays inside ONE half-period of the sine, so the
            // inverse branch is fixed and reading it off the midpoint is exact.
            double mid = (breaks[i] + breaks[i + 1]) / 2;
            yield return new EllipsePiece(
                _center, _a, _b, phase, r, from.Y, to.Y, Math.Cos(mid + phase) >= 0, mid);
        }
    }

    /// <summary>
    /// Uniform angular subdivision. The chord deviation over a step Δ is bounded by
    /// max|P″|·Δ²/8 ≤ (|A| + |B|)·Δ²/8 — conservative rather than the circle's exact
    /// sagitta, because an ellipse's curvature varies along it and a single closed form
    /// would have to be the tightest bound over the whole sweep.
    /// </summary>
    public override void Flatten(double chordTolerance, List<Vector2d> into)
    {
        double bound = _a.Length + _b.Length;
        double maxAngle = chordTolerance <= 0 || bound <= 0
            ? Math.PI / 2
            : Math.Min(Math.PI / 2, Math.Sqrt(8 * chordTolerance / bound));
        int count = Math.Max(1, (int)Math.Ceiling(Math.Abs(_sweep) / maxAngle));
        for (int i = 0; i < count; i++)
            into.Add(PointAt(_startAngle + _sweep * i / count));
    }

    private sealed class EllipsePiece : MonotonePiece
    {
        private readonly Vector2d _center, _a, _b;
        private readonly double _phase, _radius, _mid;
        private readonly bool _rising;

        public EllipsePiece(
            Vector2d center, Vector2d a, Vector2d b, double phase, double radius,
            double y0, double y1, bool rising, double mid)
        {
            _center = center;
            _a = a;
            _b = b;
            _phase = phase;
            _radius = radius;
            _rising = rising;
            _mid = mid;
            Y0 = y0;
            Y1 = y1;
        }

        public override double XAtY(double y)
        {
            // Invert y − C.y = R·sin(θ + φ) on this piece's own half-period, then evaluate
            // x on the ellipse — closed form throughout, no bisection.
            double s = Math.Clamp((y - _center.Y) / _radius, -1, 1);
            double shifted = _rising ? Math.Asin(s) : Math.PI - Math.Asin(s);
            double angle = shifted - _phase;
            // Land in this piece's own turn: the piece spans at most half a period, so
            // exactly one 2π shift is admissible.
            angle += 2 * Math.PI * Math.Round((_mid - angle) / (2 * Math.PI));
            return _center.X + _a.X * Math.Cos(angle) + _b.X * Math.Sin(angle);
        }
    }
}

// -------------------------------------------------------------------------- bézier

internal sealed class CubicSeg(Vector2d p0, Vector2d c1, Vector2d c2, Vector2d p3) : SketchSegment
{
    public override Vector2d Start => p0;
    public override Vector2d End => p3;

    // The control points, for SketchRegion's lane-wise kernel: it re-derives the Bernstein
    // and Newton arithmetic below term for term over SIMD lanes, so it needs the same four
    // points this segment does. Internal, like the whole segment family.
    public Vector2d P0 => p0;
    public Vector2d Control1 => c1;
    public Vector2d Control2 => c2;
    public Vector2d P3 => p3;

    /// <summary>Only the CONTROL POINTS move, which IS the placed curve: a Bézier is an
    /// affine combination of them at every parameter, so mapping the four points maps the
    /// curve exactly (the same property that makes a transformed NURBS a lossless STEP
    /// export and text-on-a-path Native in all three representations).</summary>
    public override SketchSegment Placed(in Vector2d origin, in Vector2d xAxis) => new CubicSeg(
        Place(origin, xAxis, p0),
        Place(origin, xAxis, c1),
        Place(origin, xAxis, c2),
        Place(origin, xAxis, p3));

    public override SketchSegment MirroredInY() =>
        new CubicSeg(Flip(p0), Flip(c1), Flip(c2), Flip(p3));

    private Vector2d PointAt(double t)
    {
        double u = 1 - t;
        return p0 * (u * u * u) + c1 * (3 * u * u * t) + c2 * (3 * u * t * t) + p3 * (t * t * t);
    }

    private Vector2d DerivativeAt(double t)
    {
        double u = 1 - t;
        return (c1 - p0) * (3 * u * u) + (c2 - c1) * (6 * u * t) + (p3 - c2) * (3 * t * t);
    }

    private Vector2d SecondDerivativeAt(double t) =>
        (c2 - c1 * 2 + p0) * (6 * (1 - t)) + (p3 - c2 * 2 + c1) * (6 * t);

    public override SketchSegment Reversed() => new CubicSeg(p3, c2, c1, p0);

    public override double SignedAreaContribution()
    {
        // 3-point Gauss–Legendre on ½·cross(B, B′): the integrand is degree 5, which
        // 3-point quadrature integrates exactly.
        Span<double> nodes = [0.5 - Math.Sqrt(0.6) / 2, 0.5, 0.5 + Math.Sqrt(0.6) / 2];
        Span<double> weights = [5.0 / 18, 8.0 / 18, 5.0 / 18];
        double area = 0;
        for (int i = 0; i < 3; i++)
            area += weights[i] * 0.5 * PointAt(nodes[i]).Cross(DerivativeAt(nodes[i]));
        return area;
    }

    public override Aabb Bounds()
    {
        // Control-hull bounds: conservative, sufficient for framing and SDF regions.
        var min = Vector2d.Min(Vector2d.Min(p0, c1), Vector2d.Min(c2, p3));
        var max = Vector2d.Max(Vector2d.Max(p0, c1), Vector2d.Max(c2, p3));
        return new Aabb((min.X, min.Y, 0), (max.X, max.Y, 0));
    }

    public override Curve3d ToCurve() => new NurbsCurve(3,
        [(p0.X, p0.Y, 0), (c1.X, c1.Y, 0), (c2.X, c2.Y, 0), (p3.X, p3.Y, 0)],
        null, [0, 0, 0, 0, 1, 1, 1, 1]);

    public override Curve2d ToCurve2d() => new BezierCurve2d(p0, c1, c2, p3);

    public override double Distance(in Vector2d point)
    {
        // Coarse sampling picks the basin; Newton on (B−p)·B′ polishes to ~1e-12.
        double bestT = 0, bestDistance = double.PositiveInfinity;
        const int samples = 16;
        for (int i = 0; i <= samples; i++)
        {
            double t = (double)i / samples;
            double d = point.DistanceTo(PointAt(t));
            if (d < bestDistance)
            {
                bestDistance = d;
                bestT = t;
            }
        }
        double refined = bestT;
        for (int iteration = 0; iteration < 8; iteration++)
        {
            var offset = PointAt(refined) - point;
            var d1 = DerivativeAt(refined);
            double g = offset.Dot(d1);
            double gPrime = d1.Dot(d1) + offset.Dot(SecondDerivativeAt(refined));
            if (Math.Abs(gPrime) < 1e-18)
                break;
            refined = Math.Clamp(refined - g / gPrime, 0, 1);
        }
        return Math.Min(bestDistance, point.DistanceTo(PointAt(refined)));
    }

    public override IEnumerable<MonotonePiece> MonotonePieces()
    {
        // Split where y′(t) = 0 (a quadratic) so each piece is y-monotone.
        double a = 3 * (p3.Y - 3 * c2.Y + 3 * c1.Y - p0.Y);
        double b = 6 * (c2.Y - 2 * c1.Y + p0.Y);
        double c = 3 * (c1.Y - p0.Y);
        var breaks = new List<double> { 0, 1 };
        if (Math.Abs(a) < 1e-15)
        {
            if (Math.Abs(b) > 1e-15)
                AddRoot(-c / b);
        }
        else
        {
            double discriminant = b * b - 4 * a * c;
            if (discriminant >= 0)
            {
                double sqrt = Math.Sqrt(discriminant);
                AddRoot((-b + sqrt) / (2 * a));
                AddRoot((-b - sqrt) / (2 * a));
            }
        }
        breaks.Sort();

        for (int i = 0; i + 1 < breaks.Count; i++)
        {
            double y0 = PointAt(breaks[i]).Y;
            double y1 = PointAt(breaks[i + 1]).Y;
            if (Math.Abs(y0 - y1) > 0)
                yield return new CubicPiece(this, breaks[i], breaks[i + 1], y0, y1);
        }

        void AddRoot(double t)
        {
            if (t > 1e-12 && t < 1 - 1e-12)
                breaks.Add(t);
        }
    }

    /// <summary>
    /// Adaptive de Casteljau subdivision: a piece is emitted once both control points sit
    /// within <paramref name="chordTolerance"/> of its chord, which bounds the curve's own
    /// deviation (the curve lies in the control hull). Depth-capped so a cusp cannot spin.
    /// </summary>
    public override void Flatten(double chordTolerance, List<Vector2d> into) =>
        Subdivide(p0, c1, c2, p3, chordTolerance, 0, into);

    private static void Subdivide(
        Vector2d a, Vector2d b, Vector2d c, Vector2d d, double tolerance, int depth, List<Vector2d> into)
    {
        if (depth >= 16 || (ChordDistance(a, d, b) <= tolerance && ChordDistance(a, d, c) <= tolerance))
        {
            into.Add(a);
            return;
        }
        var ab = Vector2d.Lerp(a, b, 0.5);
        var bc = Vector2d.Lerp(b, c, 0.5);
        var cd = Vector2d.Lerp(c, d, 0.5);
        var abc = Vector2d.Lerp(ab, bc, 0.5);
        var bcd = Vector2d.Lerp(bc, cd, 0.5);
        var mid = Vector2d.Lerp(abc, bcd, 0.5);
        Subdivide(a, ab, abc, mid, tolerance, depth + 1, into);
        Subdivide(mid, bcd, cd, d, tolerance, depth + 1, into);
    }

    /// <summary>Distance from a control point to the chord SEGMENT (degenerate chords —
    /// a bézier that returns to its start — measure to the point).</summary>
    private static double ChordDistance(in Vector2d from, in Vector2d to, in Vector2d point)
    {
        var direction = to - from;
        double lengthSquared = direction.LengthSquared;
        if (!(lengthSquared > 0))
            return point.DistanceTo(from); // exact-zero division guard
        double t = Math.Clamp((point - from).Dot(direction) / lengthSquared, 0, 1);
        return point.DistanceTo(from + direction * t);
    }

    private sealed class CubicPiece : MonotonePiece
    {
        private readonly CubicSeg _segment;
        private readonly double _t0, _t1;

        public CubicPiece(CubicSeg segment, double t0, double t1, double y0, double y1)
        {
            _segment = segment;
            _t0 = t0;
            _t1 = t1;
            Y0 = y0;
            Y1 = y1;
        }

        public override double XAtY(double y)
        {
            // Bisection on the monotone piece: guaranteed bracketing, ~1e-15 in 50 steps.
            double lo = _t0, hi = _t1;
            bool increasing = Y1 > Y0;
            for (int i = 0; i < 50; i++)
            {
                double mid = (lo + hi) / 2;
                if (_segment.PointAt(mid).Y < y == increasing)
                    lo = mid;
                else
                    hi = mid;
            }
            return _segment.PointAt((lo + hi) / 2).X;
        }
    }
}
