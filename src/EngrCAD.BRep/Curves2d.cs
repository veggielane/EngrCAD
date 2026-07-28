using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Parametric 2D curve, evaluated over <see cref="Domain"/>. The sketch-plane sibling of
/// <see cref="Curve3d"/>: same shape of API, one dimension less.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="Curve3d"/>, whose <see cref="PolylineCurve3d"/> genuinely has no
/// derivative, <see cref="DerivativeAt"/> and <see cref="SecondDerivativeAt"/> are
/// ABSTRACT here: every 2D curve in the kernel is analytic, so there is no
/// finite-difference fallback for a new curve type to inherit by accident. That is the
/// repo's "no finite-difference tangents in weld-critical constructions" rule expressed
/// at the type level.
/// </para>
/// </remarks>
public abstract class Curve2d
{
    public abstract Interval Domain { get; }
    public abstract bool IsClosed { get; }
    public abstract Vector2d PointAt(double t);

    /// <summary>Exact first derivative dC/dt.</summary>
    public abstract Vector2d DerivativeAt(double t);

    /// <summary>Exact second derivative d²C/dt².</summary>
    public abstract Vector2d SecondDerivativeAt(double t);

    /// <summary>
    /// The same curve placed on <paramref name="plane"/> as an EXACT <see cref="Curve3d"/>
    /// (the region's 2D x/y become the frame's X/Y) — the bridge from the sketch-plane
    /// vocabulary into the topology one, used by <see cref="Profile.FromCurves"/>.
    /// </summary>
    /// <remarks>
    /// Abstract for the same reason the derivatives are: every conversion here is exact and
    /// there must be no sampled fallback for a new 2D type to inherit by accident. Nothing
    /// is flattened, so a chain of lines and arcs becomes a chain of <see cref="Line3d"/>
    /// and trimmed <see cref="Circle3d"/> segments, not a polyline — which is the whole
    /// point of having a 2D curve family beside the polygonal <c>Region2d</c> one.
    /// </remarks>
    public abstract Curve3d ToCurve3d(in Frame3d plane);

    /// <summary>The curve on the world XY plane (z = 0).</summary>
    public Curve3d ToCurve3d() => ToCurve3d(Frame3d.WorldXY);

    /// <summary>Lifts a sketch-plane point onto a placement frame.</summary>
    private protected static Vector3d Lift(in Frame3d plane, in Vector2d point) =>
        plane.ToWorld(new Vector3d(point.X, point.Y, 0));

    /// <summary>Unit tangent from the exact derivative.</summary>
    public virtual Vector2d TangentAt(double t)
    {
        var derivative = DerivativeAt(t);
        double length = derivative.Length;
        // Exact-zero guard (scale-free — a fixed epsilon here would reject legitimate
        // micron-scale geometry): only a genuine stationary point has no direction.
        if (!(length > 0))
            throw new InvalidOperationException("Curve is stationary here; no unit tangent exists.");
        return derivative / length;
    }

    /// <summary>
    /// Signed curvature κ = (C′ × C″) / |C′|³ (positive when the curve turns
    /// counter-clockwise). Zero-length derivative returns 0 rather than throwing.
    /// </summary>
    public double CurvatureAt(double t)
    {
        var d1 = DerivativeAt(t);
        double speed = d1.Length;
        // Exact-zero division guard (scale-free): a stationary point has no curvature.
        return speed > 0 ? d1.Cross(SecondDerivativeAt(t)) / (speed * speed * speed) : 0;
    }

    /// <summary>
    /// Distance from <paramref name="point"/> to this curve. The default samples the
    /// curve and refines the best sample with a safeguarded 1D Newton iteration on
    /// d/dt ½|C(t) − p|² = 0; because every candidate is a real point ON the curve, the
    /// result can only ever OVER-estimate the true distance, never under-estimate it —
    /// which is the safe direction for a fitting error metric. <see cref="Line2d"/> and
    /// <see cref="Arc2d"/> override with the exact closed forms.
    /// </summary>
    public virtual double DistanceTo(in Vector2d point) => (NearestPoint(point) - point).Length;

    /// <summary>The nearest point ON this curve (see <see cref="DistanceTo"/> for the method).</summary>
    public virtual Vector2d NearestPoint(in Vector2d point)
    {
        var domain = Domain;
        const int samples = 64;
        double best = double.PositiveInfinity, bestT = domain.Start;
        for (int i = 0; i <= samples; i++)
        {
            double t = domain.ParameterAt((double)i / samples);
            double d = (PointAt(t) - point).LengthSquared;
            if (d < best)
            {
                best = d;
                bestT = t;
            }
        }

        // Newton on f(t) = (C − p)·C′; f′ = |C′|² + (C − p)·C″. Bracketed by the domain,
        // and never allowed to leave the sampled neighbourhood, so a bad step degrades to
        // the sampled answer instead of wandering.
        double window = domain.Length / samples;
        double lo = domain.Clamp(bestT - window), hi = domain.Clamp(bestT + window);
        double parameter = bestT;
        for (int iteration = 0; iteration < 20; iteration++)
        {
            var delta = PointAt(parameter) - point;
            var d1 = DerivativeAt(parameter);
            double f = delta.Dot(d1);
            double df = d1.LengthSquared + delta.Dot(SecondDerivativeAt(parameter));
            // Near-underflow degenerate guard, not a model tolerance.
            if (Math.Abs(df) < 1e-30)
                break;
            double next = Math.Clamp(parameter - f / df, lo, hi);
            if (Math.Abs(next - parameter) <= domain.Length * 1e-15)
            {
                parameter = next;
                break;
            }
            parameter = next;
        }

        var refined = PointAt(parameter);
        return (refined - point).LengthSquared <= best ? refined : PointAt(bestT);
    }

    /// <summary>
    /// Arc length over the whole domain, by adaptive Simpson quadrature of |C′(t)|.
    /// <paramref name="relativeTolerance"/> is relative to the chord length, so the test
    /// is scale-free.
    /// </summary>
    public double ArcLength(double relativeTolerance = 1e-12) =>
        ArcLength(Domain.Start, Domain.End, relativeTolerance);

    /// <summary>Arc length between two parameters (negative direction returns a negative length).</summary>
    public double ArcLength(double from, double to, double relativeTolerance = 1e-12)
    {
        if (to < from)
            return -ArcLength(to, from, relativeTolerance);
        if (to <= from)
            return 0;
        // Scale the tolerance by the chord: an absolute quadrature epsilon would be
        // meaningless at micron or kilometre scale (the relative-guard rule).
        double scale = Math.Max((PointAt(to) - PointAt(from)).Length, 1e-300);
        // One copy of adaptive Simpson serves both curve families (see AdaptiveQuadrature);
        // the arithmetic is unchanged from the hand-rolled version this replaced.
        return AdaptiveQuadrature.Integrate(Speed, from, to, scale * relativeTolerance, depth: 24);
    }

    private double Speed(double t) => DerivativeAt(t).Length;

    /// <summary>
    /// The parameter at arc length <paramref name="length"/> measured from
    /// <see cref="Interval.Start"/> — the inverse of <see cref="ArcLength(double, double, double)"/>.
    /// </summary>
    /// <remarks>
    /// A ROOT SOLVE (safeguarded Newton on L(t) − s = 0, whose derivative is the exact
    /// speed |C′(t)|) with a bisection bracket, never a minimization: the STEP reader
    /// learned the hard way that minimizing a squared residual stalls near √ε ≈ 1e-8,
    /// which is past the weld tolerance. Lengths outside [0, total] clamp to the domain.
    /// For repeated queries build an <see cref="ArcLengthTable2d"/> instead — this
    /// re-integrates from the domain start on every call.
    /// </remarks>
    public double ParameterAtLength(double length, double relativeTolerance = 1e-12)
    {
        var domain = Domain;
        if (length <= 0)
            return domain.Start;
        double total = ArcLength(relativeTolerance);
        if (length >= total)
            return domain.End;

        double lo = domain.Start, hi = domain.End;
        double t = domain.ParameterAt(length / total); // arc-length-proportional seed
        double epsilon = Math.Max(total, 1e-300) * relativeTolerance;
        for (int iteration = 0; iteration < 60; iteration++)
        {
            double f = ArcLength(domain.Start, t, relativeTolerance) - length;
            if (Math.Abs(f) <= epsilon)
                return t;
            if (f > 0)
                hi = t;
            else
                lo = t;
            double speed = Speed(t);
            // Newton where the speed is usable, bisection otherwise or when Newton
            // would leave the bracket — the bracket is what guarantees convergence.
            double next = speed > 0 ? t - f / speed : (lo + hi) * 0.5;
            if (!(next > lo && next < hi))
                next = (lo + hi) * 0.5;
            if (Math.Abs(next - t) <= domain.Length * 1e-15)
                return next;
            t = next;
        }
        return t;
    }

    /// <summary>Samples the curve at <paramref name="count"/> + 1 uniform parameters.</summary>
    public Vector2d[] Sample(int count)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count));
        var points = new Vector2d[count + 1];
        for (int i = 0; i <= count; i++)
            points[i] = PointAt(Domain.ParameterAt((double)i / count));
        return points;
    }
}

/// <summary>
/// Arc-length parameterization table for repeated queries (the g3 <c>ArcLengthParam</c>
/// role). Builds a monotone (length, parameter) table once and answers
/// <see cref="ParameterAtLength"/> by table lookup plus one safeguarded Newton polish
/// against the exact speed, so a resampling loop is O(n) instead of O(n) integrations.
/// </summary>
public sealed class ArcLengthTable2d
{
    private readonly Curve2d _curve;
    private readonly double[] _parameters;
    private readonly double[] _lengths;

    /// <summary>Total arc length of the curve.</summary>
    public double TotalLength => _lengths[^1];

    /// <summary>The curve this table parameterizes.</summary>
    public Curve2d Curve => _curve;

    public ArcLengthTable2d(Curve2d curve, int steps = 128, double relativeTolerance = 1e-12)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (steps < 1)
            throw new ArgumentOutOfRangeException(nameof(steps));
        _curve = curve;
        _parameters = new double[steps + 1];
        _lengths = new double[steps + 1];
        var domain = curve.Domain;
        for (int i = 0; i <= steps; i++)
            _parameters[i] = domain.ParameterAt((double)i / steps);
        // Cumulative sum of per-interval quadrature: each piece is integrated exactly
        // once, so the table costs one full integration, not one per entry.
        for (int i = 1; i <= steps; i++)
            _lengths[i] = _lengths[i - 1] + curve.ArcLength(_parameters[i - 1], _parameters[i], relativeTolerance);
    }

    /// <summary>The curve parameter at arc length <paramref name="length"/> from the domain start.</summary>
    public double ParameterAtLength(double length)
    {
        if (length <= 0)
            return _parameters[0];
        if (length >= TotalLength)
            return _parameters[^1];

        int index = Array.BinarySearch(_lengths, length);
        if (index >= 0)
            return _parameters[index];
        index = ~index; // first entry greater than length
        double lo = _parameters[index - 1], hi = _parameters[index];
        double span = _lengths[index] - _lengths[index - 1];
        double t = span > 0 ? lo + (hi - lo) * (length - _lengths[index - 1]) / span : lo;

        double target = length - _lengths[index - 1];
        for (int iteration = 0; iteration < 12; iteration++)
        {
            double f = _curve.ArcLength(lo, t) - target;
            double speed = _curve.DerivativeAt(t).Length;
            if (speed <= 0)
                break;
            double next = t - f / speed;
            if (!(next > lo && next < hi))
                break;
            if (Math.Abs(next - t) <= (hi - lo) * 1e-15)
            {
                t = next;
                break;
            }
            t = next;
        }
        return t;
    }

    /// <summary>The point at arc length <paramref name="length"/> from the domain start.</summary>
    public Vector2d PointAtLength(double length) => _curve.PointAt(ParameterAtLength(length));
}

/// <summary>Straight 2D segment; t ∈ [0, 1] from <see cref="Start"/> to <see cref="End"/>.</summary>
public sealed class Line2d(Vector2d start, Vector2d end) : Curve2d
{
    public Vector2d Start => start;
    public Vector2d End => end;

    public override Interval Domain => Interval.Unit;
    public override bool IsClosed => false;
    public override Vector2d PointAt(double t) => Vector2d.Lerp(start, end, t);
    public override Vector2d DerivativeAt(double t) => end - start;
    public override Vector2d SecondDerivativeAt(double t) => Vector2d.Zero;

    /// <summary>Exact distance to the segment (clamped projection).</summary>
    public override double DistanceTo(in Vector2d point) => (NearestPoint(point) - point).Length;

    public override Vector2d NearestPoint(in Vector2d point)
    {
        var direction = end - start;
        double lengthSquared = direction.LengthSquared;
        // Exact-zero guard: a degenerate segment is its own start point.
        if (lengthSquared <= 0)
            return start;
        double t = Math.Clamp((point - start).Dot(direction) / lengthSquared, 0, 1);
        return start + direction * t;
    }

    public override Curve3d ToCurve3d(in Frame3d plane) => new Line3d(Lift(plane, start), Lift(plane, end));
}

/// <summary>
/// Circular arc: center + r·(cos θ, sin θ) with θ = <see cref="StartAngle"/> +
/// t·<see cref="SweepAngle"/> over t ∈ [0, 1]. The sweep is SIGNED — positive is
/// counter-clockwise — so orientation is intrinsic to the data and no separate
/// "positive rotation" flag (or reverse-and-hope repair, as in the g3 original) is ever
/// needed.
/// </summary>
public sealed class Arc2d : Curve2d
{
    public Vector2d Center { get; }
    public double Radius { get; }
    public double StartAngle { get; }

    /// <summary>Signed angular span in radians; positive is counter-clockwise.</summary>
    public double SweepAngle { get; }

    public Arc2d(in Vector2d center, double radius, double startAngle, double sweepAngle)
    {
        if (!(radius > 0))
            throw new ArgumentOutOfRangeException(nameof(radius), "Arc radius must be positive.");
        if (Math.Abs(sweepAngle) > 2 * Math.PI + 1e-12)
            throw new ArgumentOutOfRangeException(nameof(sweepAngle), "Arc sweep cannot exceed a full turn.");
        Center = center;
        Radius = radius;
        StartAngle = startAngle;
        SweepAngle = sweepAngle;
    }

    /// <summary>The polar angle at curve parameter <paramref name="t"/>.</summary>
    public double AngleAt(double t) => StartAngle + SweepAngle * t;

    public override Interval Domain => Interval.Unit;

    // Exact-2π semantic test, deliberately with an angular round-off slack rather than a
    // model tolerance: closure here is a topological fact about the parameterization.
    public override bool IsClosed => Math.Abs(Math.Abs(SweepAngle) - 2 * Math.PI) < 1e-12;

    public override Vector2d PointAt(double t)
    {
        double angle = AngleAt(t);
        return new Vector2d(Center.X + Radius * Math.Cos(angle), Center.Y + Radius * Math.Sin(angle));
    }

    public override Vector2d DerivativeAt(double t)
    {
        double angle = AngleAt(t);
        return new Vector2d(-Radius * SweepAngle * Math.Sin(angle), Radius * SweepAngle * Math.Cos(angle));
    }

    public override Vector2d SecondDerivativeAt(double t)
    {
        double angle = AngleAt(t);
        double k = -Radius * SweepAngle * SweepAngle;
        return new Vector2d(k * Math.Cos(angle), k * Math.Sin(angle));
    }

    /// <summary>Exact arc length |sweep|·r (no quadrature needed).</summary>
    public double Length => Math.Abs(SweepAngle) * Radius;

    /// <summary>The same arc traversed the other way (start and end swap, sweep negates).</summary>
    public Arc2d Reversed() => new(Center, Radius, StartAngle + SweepAngle, -SweepAngle);

    /// <summary>
    /// Exact, and built the SAME way a sketch arc is: a full turn becomes a
    /// <see cref="Circle3d"/> whose frame is rotated to the arc's own start radial (so the
    /// curve parameter follows the signed sweep), anything less becomes a
    /// <see cref="CurveSegment"/> over a circle on the placement frame's own axes. Keeping
    /// the trimmed form means downstream classification — `BrepQueries.IsCircular`, rim
    /// features, cylinder promotion — sees the same `Underlying` circle it always has, and a
    /// NEGATIVE sweep is expressed as a decreasing parameter range rather than a reversal
    /// wrapper, which is exactly the intrinsic-orientation property `Arc2d` exists for.
    /// </summary>
    public override Curve3d ToCurve3d(in Frame3d plane)
    {
        var center = Lift(plane, Center);
        if (IsClosed)
        {
            double cos = Math.Cos(StartAngle), sin = Math.Sin(StartAngle);
            var x = plane.ToWorldVector(new Vector3d(cos, sin, 0));
            var perpendicular = plane.ToWorldVector(new Vector3d(-sin, cos, 0));
            return new Circle3d(center, x, SweepAngle > 0 ? perpendicular : -perpendicular, Radius);
        }
        var circle = new Circle3d(center, plane.X, plane.Y, Radius);
        return new CurveSegment(circle, StartAngle, StartAngle + SweepAngle);
    }

    /// <summary>Exact distance to the arc: radial when the foot lies inside the sweep, otherwise to the nearer end.</summary>
    public override double DistanceTo(in Vector2d point) => (NearestPoint(point) - point).Length;

    public override Vector2d NearestPoint(in Vector2d point)
    {
        var offset = point - Center;
        // Exact-zero guard: the arc centre is equidistant from every point of the arc, so
        // the start point is as good an answer as any.
        if (offset.LengthSquared > 0)
        {
            double angle = Math.Atan2(offset.Y, offset.X);
            if (TryLocalize(angle, out double t))
                return PointAt(t);
        }
        var p0 = PointAt(0);
        var p1 = PointAt(1);
        return (p0 - point).LengthSquared <= (p1 - point).LengthSquared ? p0 : p1;
    }

    /// <summary>
    /// Maps a polar <paramref name="angle"/> to a curve parameter in [0, 1] if the angle
    /// lies within the sweep (modulo 2π).
    /// </summary>
    public bool TryLocalize(double angle, out double t)
    {
        double sweep = SweepAngle;
        double delta = angle - StartAngle;
        // Fold into the sweep's half-turn convention: [0, 2π) forwards, (−2π, 0] backwards.
        double folded = delta % (2 * Math.PI);
        if (sweep > 0 && folded < 0)
            folded += 2 * Math.PI;
        else if (sweep < 0 && folded > 0)
            folded -= 2 * Math.PI;
        t = sweep != 0 ? folded / sweep : 0;
        return t >= 0 && t <= 1;
    }

    /// <summary>
    /// The arc leaving <paramref name="start"/> along unit tangent
    /// <paramref name="tangent"/> and ending exactly at <paramref name="end"/> — the
    /// biarc's construction primitive. Returns a <see cref="Line2d"/> when the chord is
    /// parallel to the tangent (an infinite-radius arc); the straightness test is
    /// RELATIVE (the sagitta compared against the chord length), never an absolute
    /// epsilon on a projected length, which would fail quadratically with scale.
    /// </summary>
    public static Curve2d FromPointAndTangent(in Vector2d start, in Vector2d tangent, in Vector2d end,
        double relativeStraightness = 1e-12)
    {
        var chord = end - start;
        double chordLength = chord.Length;
        // Exact-zero guard: no arc is defined by a zero chord.
        if (chordLength <= 0)
            throw new ArgumentException("Arc endpoints coincide.", nameof(end));

        var normal = tangent.Perpendicular; // left of travel
        double height = chord.Dot(normal);
        // Relative straightness: height/chordLength is the sine of the chord-tangent
        // angle, a dimensionless quantity, so this epsilon is scale-free by construction.
        if (Math.Abs(height) <= chordLength * relativeStraightness)
            return new Line2d(start, end);

        double signedRadius = chord.LengthSquared / (2 * height);
        var center = start + normal * signedRadius;
        double startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
        double endAngle = Math.Atan2(end.Y - center.Y, end.X - center.X);

        // The sign of the radius IS the turn direction: centre to the left of travel
        // (positive height) means a counter-clockwise arc.
        double sweep = endAngle - startAngle;
        if (signedRadius > 0)
        {
            while (sweep <= 0)
                sweep += 2 * Math.PI;
        }
        else
        {
            while (sweep >= 0)
                sweep -= 2 * Math.PI;
        }
        return new Arc2d(center, Math.Abs(signedRadius), startAngle, sweep);
    }
}

/// <summary>
/// 2D Bézier curve of any degree over t ∈ [0, 1], evaluated by de Casteljau. Derivatives
/// are exact: the hodograph of a degree-n Bézier is the degree-(n−1) Bézier over the
/// scaled control-point differences.
/// </summary>
public sealed class BezierCurve2d : Curve2d
{
    private readonly Vector2d[] _controlPoints;

    public IReadOnlyList<Vector2d> ControlPoints => _controlPoints;
    public int Degree => _controlPoints.Length - 1;

    public BezierCurve2d(IReadOnlyList<Vector2d> controlPoints)
    {
        ArgumentNullException.ThrowIfNull(controlPoints);
        if (controlPoints.Count < 2)
            throw new ArgumentException("A Bézier curve needs at least 2 control points.", nameof(controlPoints));
        _controlPoints = [.. controlPoints];
    }

    /// <summary>Quadratic Bézier (the TrueType glyph segment form).</summary>
    public BezierCurve2d(in Vector2d start, in Vector2d control, in Vector2d end)
        : this([start, control, end]) { }

    /// <summary>Cubic Bézier.</summary>
    public BezierCurve2d(in Vector2d start, in Vector2d control1, in Vector2d control2, in Vector2d end)
        : this([start, control1, control2, end]) { }

    public override Interval Domain => Interval.Unit;

    public override bool IsClosed => _controlPoints[0].AreEqual(_controlPoints[^1], Tolerance.Default);

    public override Vector2d PointAt(double t) => DeCasteljau(_controlPoints, t);

    public override Vector2d DerivativeAt(double t) => DeCasteljau(Hodograph(_controlPoints), t);

    public override Vector2d SecondDerivativeAt(double t)
    {
        var first = Hodograph(_controlPoints);
        return first.Length < 2 ? Vector2d.Zero : DeCasteljau(Hodograph(first), t);
    }

    /// <summary>
    /// Exact: a degree-n Bézier IS a degree-n B-spline with a Bézier knot vector (n + 1
    /// zeros then n + 1 ones) and unit weights, so this is a re-expression rather than a
    /// conversion — the same control points, the same curve, every parameter unchanged.
    /// </summary>
    public override Curve3d ToCurve3d(in Frame3d plane)
    {
        int degree = Degree;
        var points = new Vector3d[_controlPoints.Length];
        for (int i = 0; i < points.Length; i++)
            points[i] = Lift(plane, _controlPoints[i]);
        var knots = new double[2 * (degree + 1)];
        for (int i = degree + 1; i < knots.Length; i++)
            knots[i] = 1;
        return new NurbsCurve(degree, points, null, knots);
    }

    /// <summary>Control points of the derivative curve: n·(P_{i+1} − P_i).</summary>
    private static Vector2d[] Hodograph(Vector2d[] points)
    {
        int n = points.Length - 1;
        if (n < 1)
            return [Vector2d.Zero];
        var result = new Vector2d[n];
        for (int i = 0; i < n; i++)
            result[i] = (points[i + 1] - points[i]) * n;
        return result;
    }

    private static Vector2d DeCasteljau(Vector2d[] points, double t)
    {
        int n = points.Length;
        if (n == 1)
            return points[0];
        Span<Vector2d> work = n <= 8 ? stackalloc Vector2d[8] : new Vector2d[n];
        for (int i = 0; i < n; i++)
            work[i] = points[i];
        for (int level = n - 1; level > 0; level--)
        {
            for (int i = 0; i < level; i++)
                work[i] = Vector2d.Lerp(work[i], work[i + 1], t);
        }
        return work[0];
    }
}

/// <summary>
/// Rational B-spline curve in 2D (the sketch-plane counterpart of
/// <see cref="NurbsCurve"/>). Weights of all 1 give a plain B-spline; rational weights
/// represent conics exactly.
/// </summary>
/// <remarks>
/// The basis comes from the shared <see cref="BSplineBasis"/> — the same A2.1/A2.2/A2.3
/// code the 3D curve and the tensor-product surface use, because the basis depends only
/// on knots and degree. Only the control-point accumulation is written twice, and that
/// is genuinely dimension-specific arithmetic rather than a fork of the algorithm.
/// </remarks>
public sealed class NurbsCurve2d : Curve2d
{
    public int Degree { get; }
    public IReadOnlyList<Vector2d> ControlPoints { get; }
    public IReadOnlyList<double> Weights { get; }
    public IReadOnlyList<double> Knots { get; }

    public NurbsCurve2d(int degree, IReadOnlyList<Vector2d> controlPoints, IReadOnlyList<double>? weights, IReadOnlyList<double> knots)
    {
        if (degree < 1)
            throw new ArgumentOutOfRangeException(nameof(degree));
        if (controlPoints.Count < degree + 1)
            throw new ArgumentException($"A degree-{degree} curve needs at least {degree + 1} control points.");
        if (knots.Count != controlPoints.Count + degree + 1)
            throw new ArgumentException(
                $"Expected {controlPoints.Count + degree + 1} knots for {controlPoints.Count} control points of degree {degree}, got {knots.Count}.");
        for (int i = 1; i < knots.Count; i++)
        {
            if (knots[i] < knots[i - 1])
                throw new ArgumentException("Knot vector must be non-decreasing.");
        }
        if (weights is not null && weights.Count != controlPoints.Count)
            throw new ArgumentException("Weight count must match control point count.");

        Degree = degree;
        ControlPoints = controlPoints;
        Weights = weights ?? [.. Enumerable.Repeat(1.0, controlPoints.Count)];
        Knots = knots;
    }

    /// <summary>
    /// Exact rational circular arc from <paramref name="startAngle"/> to
    /// <paramref name="endAngle"/> (radians, counter-clockwise; the sweep must be
    /// positive and less than 2π) — the 2D form of <see cref="NurbsCurve.Arc"/>, built
    /// by the same construction so the two agree control point for control point.
    /// </summary>
    public static NurbsCurve2d Arc(in Vector2d center, double radius, double startAngle, double endAngle)
    {
        var lifted = NurbsCurve.Arc(
            new Vector3d(center.X, center.Y, 0), Vector3d.UnitX, Vector3d.UnitY, radius, startAngle, endAngle);
        return Flatten(lifted);
    }

    /// <summary>
    /// Cubic B-spline through the given 2D points — the 2D face of
    /// <see cref="NurbsCurve.InterpolatePoints"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately DELEGATES to the 3D routine on the z = 0 plane rather than forking
    /// the collocation solve. The solve is linear and every operation on the z component
    /// is 0·m, 0 − 0 or 0/d, so the control points come back with z EXACTLY zero (not
    /// approximately) — a second tridiagonal/cyclic solver would add a second place for
    /// the natural end conditions to be wrong and buy nothing.
    /// </remarks>
    public static NurbsCurve2d InterpolatePoints(IReadOnlyList<Vector2d> points, bool closed = false)
    {
        ArgumentNullException.ThrowIfNull(points);
        var lifted = new Vector3d[points.Count];
        for (int i = 0; i < points.Count; i++)
            lifted[i] = new Vector3d(points[i].X, points[i].Y, 0);
        return Flatten(NurbsCurve.InterpolatePoints(lifted, closed));
    }

    /// <summary>Exact: degree, knots and weights carry over untouched and only the control
    /// points are lifted, so the rational arcs this type represents stay exact arcs.</summary>
    public override Curve3d ToCurve3d(in Frame3d plane)
    {
        var points = new Vector3d[ControlPoints.Count];
        for (int i = 0; i < points.Length; i++)
            points[i] = Lift(plane, ControlPoints[i]);
        return new NurbsCurve(Degree, points, Weights, Knots);
    }

    /// <summary>Drops the z coordinate of a planar 3D NURBS curve built on the z = 0 plane.</summary>
    private static NurbsCurve2d Flatten(NurbsCurve curve)
    {
        var points = new Vector2d[curve.ControlPoints.Count];
        for (int i = 0; i < points.Length; i++)
            points[i] = new Vector2d(curve.ControlPoints[i].X, curve.ControlPoints[i].Y);
        return new NurbsCurve2d(curve.Degree, points, curve.Weights, curve.Knots);
    }

    public override Interval Domain => new(Knots[Degree], Knots[ControlPoints.Count]);

    public override bool IsClosed => PointAt(Domain.Start).AreEqual(PointAt(Domain.End), Tolerance.Default);

    public override Vector2d PointAt(double t)
    {
        t = Domain.Clamp(t);
        int span = BSplineBasis.FindSpan(t, Degree, ControlPoints.Count, Knots);
        Span<double> basis = stackalloc double[Degree + 1];
        BSplineBasis.Evaluate(span, t, Degree, Knots, basis);

        var numerator = Vector2d.Zero;
        double denominator = 0;
        for (int i = 0; i <= Degree; i++)
        {
            int index = span - Degree + i;
            double bw = basis[i] * Weights[index];
            numerator += ControlPoints[index] * bw;
            denominator += bw;
        }
        return numerator / denominator;
    }

    /// <summary>Exact first derivative (rational quotient rule over the homogeneous form).</summary>
    public override Vector2d DerivativeAt(double t)
    {
        Span<Vector2d> derivatives = stackalloc Vector2d[2];
        EvaluateDerivatives(t, 1, derivatives);
        return derivatives[1];
    }

    /// <summary>Exact second derivative (right-sided value at knots of reduced continuity).</summary>
    public override Vector2d SecondDerivativeAt(double t)
    {
        Span<Vector2d> derivatives = stackalloc Vector2d[3];
        EvaluateDerivatives(t, 2, derivatives);
        return derivatives[2];
    }

    /// <summary>
    /// C, C′, …, C⁽ᵏ⁾ into <paramref name="result"/>; the generalized rational quotient
    /// rule (The NURBS Book eq. 4.8), identical to <see cref="NurbsCurve"/>'s but in 2D.
    /// </summary>
    private void EvaluateDerivatives(double t, int order, Span<Vector2d> result)
    {
        t = Domain.Clamp(t);
        int span = BSplineBasis.FindSpan(t, Degree, ControlPoints.Count, Knots);
        int stride = Degree + 1;
        Span<double> ders = stackalloc double[(order + 1) * stride];
        BSplineBasis.EvaluateDerivatives(span, t, Degree, Knots, order, ders);

        Span<Vector2d> numerator = stackalloc Vector2d[order + 1];
        Span<double> weight = stackalloc double[order + 1];
        for (int k = 0; k <= order; k++)
        {
            var a = Vector2d.Zero;
            double w = 0;
            for (int j = 0; j <= Degree; j++)
            {
                int index = span - Degree + j;
                double bw = ders[k * stride + j] * Weights[index];
                a += ControlPoints[index] * bw;
                w += bw;
            }
            numerator[k] = a;
            weight[k] = w;
        }

        for (int k = 0; k <= order; k++)
        {
            var v = numerator[k];
            double binomial = 1;
            for (int i = 1; i <= k; i++)
            {
                binomial = binomial * (k - i + 1) / i;
                v -= result[k - i] * (binomial * weight[i]);
            }
            result[k] = v / weight[0];
        }
    }
}
