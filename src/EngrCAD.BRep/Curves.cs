using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>Parametric 3D curve, evaluated over <see cref="Domain"/>.</summary>
public abstract class Curve3d
{
    public abstract Interval Domain { get; }
    public abstract bool IsClosed { get; }
    public abstract Vector3d PointAt(double t);

    /// <summary>
    /// Unit tangent by finite differences: central in the interior, second-order one-sided
    /// at the domain ends (a clamped central difference would be only first-order there,
    /// and sweep frames are sensitive to start-tangent error). Subclasses may override
    /// with exact derivatives.
    /// </summary>
    public virtual Vector3d TangentAt(double t)
    {
        var d = Domain;
        double h = double.IsFinite(d.Length) ? Math.Max(1e-7, d.Length * 1e-7) : 1e-7;

        Vector3d derivative;
        if (t - h < d.Start)
            derivative = PointAt(d.Start) * -3 + PointAt(d.Start + h) * 4 - PointAt(d.Start + 2 * h);
        else if (t + h > d.End)
            derivative = PointAt(d.End) * 3 - PointAt(d.End - h) * 4 + PointAt(d.End - 2 * h);
        else
            derivative = PointAt(t + h) - PointAt(t - h);
        return derivative.Normalized();
    }

    /// <summary>
    /// First derivative dC/dt. The default is a finite difference (central in the
    /// interior, second-order one-sided at domain ends) and is APPROXIMATE — analytic
    /// curves override with the exact derivative, and weld-critical consumers
    /// (offset curves, sweep frames) rely on those exact overrides.
    /// </summary>
    public virtual Vector3d DerivativeAt(double t)
    {
        var d = Domain;
        double h = double.IsFinite(d.Length) ? Math.Max(1e-7, d.Length * 1e-7) : 1e-7;
        if (t - h < d.Start)
            return (PointAt(d.Start) * -3 + PointAt(d.Start + h) * 4 - PointAt(d.Start + 2 * h)) / (2 * h);
        if (t + h > d.End)
            return (PointAt(d.End) * 3 - PointAt(d.End - h) * 4 + PointAt(d.End - 2 * h)) / (2 * h);
        return (PointAt(t + h) - PointAt(t - h)) / (2 * h);
    }

    /// <summary>
    /// Second derivative d²C/dt². The default central difference is a rough fallback
    /// (h ~ 1e-5 balances truncation against round-off; near a domain end it evaluates
    /// at the nearest interior point instead) — analytic curves override exactly.
    /// </summary>
    public virtual Vector3d SecondDerivativeAt(double t)
    {
        var d = Domain;
        double h = double.IsFinite(d.Length) ? Math.Max(1e-5, d.Length * 1e-5) : 1e-5;
        if (double.IsFinite(d.Start))
            t = Math.Max(t, d.Start + h);
        if (double.IsFinite(d.End))
            t = Math.Min(t, d.End - h);
        return (PointAt(t + h) - PointAt(t) * 2 + PointAt(t - h)) / (h * h);
    }

    /// <summary>
    /// The innermost curve beneath wrapper types (<see cref="ReversedCurve"/>,
    /// <see cref="TransformedCurve"/>); consumers use it to pick sampling strategies.
    /// </summary>
    public virtual Curve3d Underlying => this;

    public Curve3d Reversed() => this is ReversedCurve r ? r.Base : new ReversedCurve(this);

    public Curve3d Transformed(in Matrix4d transform) => new TransformedCurve(this, transform);

    // ---- arc length (mirrors the Curve2d family) ----

    /// <summary>Arc length over the whole <see cref="Domain"/>.</summary>
    public double ArcLength(double relativeTolerance = 1e-12) =>
        ArcLength(Domain.Start, Domain.End, relativeTolerance);

    /// <summary>
    /// Arc length between two parameters (a negative direction returns a negative length),
    /// by adaptive Simpson quadrature of the exact speed |C′(t)| with Richardson
    /// extrapolation. <paramref name="relativeTolerance"/> is relative to the CHORD, so the
    /// test is scale-free — an absolute quadrature epsilon would be meaningless at micron
    /// or kilometre scale.
    /// </summary>
    /// <remarks>
    /// Curves with a closed form override this and ignore the tolerance: straight segments,
    /// circles, helices, parabolas and chord-length-parameterized polylines are all EXACT.
    /// Everything else — NURBS above all — integrates, and its accuracy is therefore the
    /// accuracy of <see cref="DerivativeAt"/>: on a curve that has not overridden the
    /// finite-difference default the quadrature is honest about a derivative that is not.
    /// </remarks>
    public virtual double ArcLength(double from, double to, double relativeTolerance = 1e-12)
    {
        if (to < from)
            return -ArcLength(to, from, relativeTolerance);
        if (to <= from)
            return 0;
        double scale = Math.Max((PointAt(to) - PointAt(from)).Length, 1e-300);
        return AdaptiveQuadrature.Integrate(
            t => DerivativeAt(t).Length, from, to, scale * relativeTolerance, depth: 24);
    }

    /// <summary>
    /// The parameter at arc length <paramref name="length"/> measured from
    /// <see cref="Interval.Start"/> — the inverse of <see cref="ArcLength(double, double, double)"/>.
    /// Lengths outside [0, total] clamp to the domain.
    /// </summary>
    /// <remarks>
    /// A ROOT SOLVE (safeguarded Newton on L(t) − s = 0, whose derivative is the exact speed
    /// |C′(t)|) with a bisection bracket, never a minimization: the STEP reader learned the
    /// hard way that minimizing a squared residual stalls near √ε ≈ 1e-8, which is past the
    /// weld tolerance. For repeated queries build an <see cref="ArcLengthTable3d"/> — this
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
            double speed = DerivativeAt(t).Length;
            // Newton where the speed is usable, bisection otherwise or when Newton would
            // leave the bracket — the bracket is what guarantees convergence.
            double next = speed > 0 ? t - f / speed : (lo + hi) * 0.5;
            if (!(next > lo && next < hi))
                next = (lo + hi) * 0.5;
            if (Math.Abs(next - t) <= domain.Length * 1e-15)
                return next;
            t = next;
        }
        return t;
    }
}

/// <summary>The same geometry traversed backwards over the same domain.</summary>
public sealed class ReversedCurve(Curve3d baseCurve) : Curve3d
{
    public Curve3d Base => baseCurve;

    public override Interval Domain => baseCurve.Domain;
    public override bool IsClosed => baseCurve.IsClosed;
    public override Curve3d Underlying => baseCurve.Underlying;

    private double Map(double t) => baseCurve.Domain.Start + baseCurve.Domain.End - t;

    public override Vector3d PointAt(double t) => baseCurve.PointAt(Map(t));
    public override Vector3d TangentAt(double t) => -baseCurve.TangentAt(Map(t));

    // Chain rule with map′ = −1: odd derivatives flip sign, even ones don't.
    public override Vector3d DerivativeAt(double t) => -baseCurve.DerivativeAt(Map(t));
    public override Vector3d SecondDerivativeAt(double t) => baseCurve.SecondDerivativeAt(Map(t));

    /// <summary>Forwarded to the base curve over the mirrored range, so a reversed exact
    /// curve keeps its exact length rather than falling back to quadrature.</summary>
    public override double ArcLength(double from, double to, double relativeTolerance = 1e-12) =>
        -baseCurve.ArcLength(Map(from), Map(to), relativeTolerance);
}

/// <summary>A curve mapped through a rigid (or affine) transform.</summary>
public sealed class TransformedCurve(Curve3d baseCurve, Matrix4d transform) : Curve3d
{
    public Curve3d Base => baseCurve;
    public Matrix4d Transform => transform;

    public override Interval Domain => baseCurve.Domain;
    public override bool IsClosed => baseCurve.IsClosed;
    public override Curve3d Underlying => baseCurve.Underlying;

    public override Vector3d PointAt(double t) => transform.TransformPoint(baseCurve.PointAt(t));

    public override Vector3d TangentAt(double t)
    {
        var v = transform.TransformVector(baseCurve.TangentAt(t));
        return v.Normalized();
    }

    // The transform is affine in t-independent coefficients, so derivatives map exactly.
    public override Vector3d DerivativeAt(double t) => transform.TransformVector(baseCurve.DerivativeAt(t));
    public override Vector3d SecondDerivativeAt(double t) => transform.TransformVector(baseCurve.SecondDerivativeAt(t));
}

/// <summary>Straight segment; t ∈ [0, 1] from <see cref="Start"/> to <see cref="End"/>.</summary>
public sealed class Line3d(Vector3d start, Vector3d end) : Curve3d
{
    public Vector3d Start => start;
    public Vector3d End => end;

    public override Interval Domain => Interval.Unit;
    public override bool IsClosed => false;
    public override Vector3d PointAt(double t) => Vector3d.Lerp(start, end, t);
    public override Vector3d TangentAt(double t) => (end - start).Normalized();
    public override Vector3d DerivativeAt(double t) => end - start;
    public override Vector3d SecondDerivativeAt(double t) => Vector3d.Zero;

    /// <summary>Exact: the speed is the constant |end − start| over the unit domain.</summary>
    public override double ArcLength(double from, double to, double relativeTolerance = 1e-12) =>
        (to - from) * (end - start).Length;
}

/// <summary>
/// Full circle in the plane spanned by <paramref name="xDirection"/>/<paramref name="yDirection"/>
/// (unit, orthogonal); t is the angle in [0, 2π].
/// </summary>
public sealed class Circle3d(Vector3d center, Vector3d xDirection, Vector3d yDirection, double radius) : Curve3d
{
    /// <summary>Circle of <paramref name="radius"/> in the frame's X/Y plane, centered
    /// at its origin (axis = frame Z, t = 0 along frame X).</summary>
    public Circle3d(in Frame3d frame, double radius) : this(frame.Origin, frame.X, frame.Y, radius) { }

    public Vector3d Center => center;
    public Vector3d XDirection => xDirection;
    public Vector3d YDirection => yDirection;
    public Vector3d Axis => xDirection.Cross(yDirection);
    public double Radius => radius;

    public override Interval Domain => new(0, 2 * Math.PI);
    public override bool IsClosed => true;

    public override Vector3d PointAt(double t) =>
        center + xDirection * (radius * Math.Cos(t)) + yDirection * (radius * Math.Sin(t));

    public override Vector3d TangentAt(double t) =>
        (-xDirection * Math.Sin(t) + yDirection * Math.Cos(t)).Normalized();

    public override Vector3d DerivativeAt(double t) =>
        -xDirection * (radius * Math.Sin(t)) + yDirection * (radius * Math.Cos(t));

    public override Vector3d SecondDerivativeAt(double t) =>
        -xDirection * (radius * Math.Cos(t)) - yDirection * (radius * Math.Sin(t));

    /// <summary>Exact: t is the angle and the x/y directions are unit, so the speed is the
    /// constant radius and the arc length is r·Δt.</summary>
    public override double ArcLength(double from, double to, double relativeTolerance = 1e-12) =>
        (to - from) * radius;
}

/// <summary>
/// Rational B-spline curve (NURBS). Weights of all 1 give a plain B-spline; rational
/// weights represent conics exactly. Evaluation is Cox–de Boor over the knot span.
/// </summary>
public sealed class NurbsCurve : Curve3d
{
    public int Degree { get; }
    public IReadOnlyList<Vector3d> ControlPoints { get; }
    public IReadOnlyList<double> Weights { get; }
    public IReadOnlyList<double> Knots { get; }

    public NurbsCurve(int degree, IReadOnlyList<Vector3d> controlPoints, IReadOnlyList<double>? weights, IReadOnlyList<double> knots)
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
    /// <paramref name="endAngle"/> (radians, counter-clockwise in the (x, y) frame;
    /// the sweep must be positive and less than 2π). Built from quadratic rational
    /// Bézier segments of at most 90° joined with interior double knots.
    /// </summary>
    public static NurbsCurve Arc(
        in Vector3d center, in Vector3d xDirection, in Vector3d yDirection,
        double radius, double startAngle, double endAngle)
    {
        double sweep = endAngle - startAngle;
        // 1e-12 angular round-off slack: admits sweeps that are 2π up to rounding, and
        // keeps exact multiples of 90° from ceiling into an extra segment (the
        // epsilon-guard-the-Ceiling lesson; ulp-different equal spans must agree).
        if (sweep <= 0 || sweep >= 2 * Math.PI + 1e-12)
            throw new ArgumentOutOfRangeException(nameof(endAngle),
                "Arc sweep must be positive and less than a full turn; use Circle3d for full circles.");

        int segmentCount = Math.Max(1, (int)Math.Ceiling(sweep / (Math.PI / 2) - 1e-12));
        double delta = sweep / segmentCount;
        double w = Math.Cos(delta / 2);

        Vector3d c = center, x = xDirection, y = yDirection;
        Vector3d OnArc(double angle) =>
            c + x * (radius * Math.Cos(angle)) + y * (radius * Math.Sin(angle));

        var controlPoints = new List<Vector3d>(2 * segmentCount + 1);
        var weights = new List<double>(2 * segmentCount + 1);
        controlPoints.Add(OnArc(startAngle));
        weights.Add(1);
        for (int i = 0; i < segmentCount; i++)
        {
            double mid = startAngle + (i + 0.5) * delta;
            // Tangent-intersection control point: on the bisector, at radius r / cos(δ/2).
            controlPoints.Add(c + x * (radius / w * Math.Cos(mid)) + y * (radius / w * Math.Sin(mid)));
            weights.Add(w);
            controlPoints.Add(OnArc(startAngle + (i + 1) * delta));
            weights.Add(1);
        }

        var knots = new List<double>(2 * segmentCount + 4) { 0, 0, 0 };
        for (int i = 1; i < segmentCount; i++)
        {
            knots.Add(i);
            knots.Add(i);
        }
        knots.Add(segmentCount);
        knots.Add(segmentCount);
        knots.Add(segmentCount);

        return new NurbsCurve(2, controlPoints, weights, knots);
    }

    /// <summary>
    /// Cubic B-spline that passes exactly through the given points
    /// (<c>GeomAPI_PointsToBSpline</c>-style interpolation).
    /// </summary>
    /// <remarks>
    /// Open (default): chord-length parameterization normalized to [0, 1], clamped end
    /// knots, natural end conditions (zero second derivative at both ends); the control
    /// points come from a tridiagonal collocation solve. Exactly two points produce a
    /// degree-1 straight segment (an elevated cubic would add nothing but wasted control
    /// points). Closed: periodic C2 interpolation — wrapped control points over a
    /// periodically extended knot vector make position, tangent, and curvature match at
    /// the seam; the cyclic-tridiagonal collocation system is solved densely with partial
    /// pivoting (point counts are small). Do not repeat the seam point in the input.
    /// </remarks>
    public static NurbsCurve InterpolatePoints(IReadOnlyList<Vector3d> points, bool closed = false)
    {
        ArgumentNullException.ThrowIfNull(points);
        int n = points.Count;
        if (n < 2)
            throw new ArgumentException("Interpolation needs at least 2 points.", nameof(points));
        for (int i = 1; i < n; i++)
        {
            if (points[i].AreEqual(points[i - 1], Tolerance.Default))
                throw new ArgumentException(
                    $"Points {i - 1} and {i} coincide; remove duplicate consecutive points before interpolating.",
                    nameof(points));
        }
        if (!closed)
        {
            return n == 2
                ? new NurbsCurve(1, [points[0], points[1]], null, [0, 0, 1, 1])
                : InterpolateOpen(points);
        }

        if (n < 3)
            throw new ArgumentException("Closed interpolation needs at least 3 points.", nameof(points));
        if (points[0].AreEqual(points[^1], Tolerance.Default))
            throw new ArgumentException(
                "For closed interpolation do not repeat the first point at the end; the curve closes back to it implicitly.",
                nameof(points));
        return InterpolateClosed(points);
    }

    /// <summary>
    /// Chord-length parameters normalized to [0, 1]; for a closed curve the array has one
    /// extra entry for the seam (the closing chord back to the first point).
    /// </summary>
    private static double[] ChordParameters(IReadOnlyList<Vector3d> points, bool closed)
    {
        int n = points.Count;
        var parameters = new double[closed ? n + 1 : n];
        double total = 0;
        for (int i = 1; i < n; i++)
        {
            total += points[i].DistanceTo(points[i - 1]);
            parameters[i] = total;
        }
        if (closed)
        {
            total += points[0].DistanceTo(points[n - 1]);
            parameters[n] = total;
        }
        for (int i = 1; i < parameters.Length; i++)
            parameters[i] /= total;
        parameters[^1] = 1.0; // exact, despite the division round-off
        return parameters;
    }

    private static NurbsCurve InterpolateOpen(IReadOnlyList<Vector3d> points)
    {
        int n = points.Count; // ≥ 3 here
        double[] parameters = ChordParameters(points, closed: false);

        // Clamped cubic knot vector whose interior knots are the interior parameters:
        // n + 2 control points, so C(t̄_i) = Q_i (n equations) plus the two natural end
        // conditions close the square system.
        var knots = new double[n + 6];
        for (int i = 0; i < 4; i++)
        {
            knots[i] = 0;
            knots[n + 2 + i] = 1;
        }
        for (int j = 1; j <= n - 2; j++)
            knots[j + 3] = parameters[j];

        // P_0 = Q_0 and P_{n+1} = Q_{n-1} are known; the remaining unknowns y_k = P_{k+1}
        // (k = 0..n-1) form a tridiagonal system:
        //  - row 0: natural start, N″_1(0) P_1 + N″_2(0) P_2 = −N″_0(0) Q_0
        //  - row j (1..n-2): collocation at t̄_j, which is knot u_{j+3} of multiplicity 1,
        //    so exactly N_j, N_{j+1}, N_{j+2} are nonzero there → couples y_{j−1}, y_j, y_{j+1}
        //  - row n-1: natural end, N″_{n-1}(1) P_{n-1} + N″_n(1) P_n = −N″_{n+1}(1) Q_{n-1}
        var sub = new double[n];
        var diag = new double[n];
        var sup = new double[n];
        var rhs = new Vector3d[n];

        const int stride = 4;
        Span<double> ders = stackalloc double[3 * stride];
        BSplineBasis.EvaluateDerivatives(3, 0.0, 3, knots, 2, ders);
        diag[0] = ders[2 * stride + 1];
        sup[0] = ders[2 * stride + 2];
        rhs[0] = points[0] * -ders[2 * stride + 0];

        Span<double> basis = stackalloc double[4];
        for (int j = 1; j <= n - 2; j++)
        {
            int span = BSplineBasis.FindSpan(parameters[j], 3, n + 2, knots);
            BSplineBasis.Evaluate(span, parameters[j], 3, knots, basis);
            sub[j] = CoefficientOf(basis, span, j);
            diag[j] = CoefficientOf(basis, span, j + 1);
            sup[j] = CoefficientOf(basis, span, j + 2);
            rhs[j] = points[j];
        }

        BSplineBasis.EvaluateDerivatives(n + 1, 1.0, 3, knots, 2, ders);
        sub[n - 1] = ders[2 * stride + 1];
        diag[n - 1] = ders[2 * stride + 2];
        rhs[n - 1] = points[n - 1] * -ders[2 * stride + 3];

        SolveTridiagonal(sub, diag, sup, rhs);

        var controlPoints = new Vector3d[n + 2];
        controlPoints[0] = points[0];
        for (int i = 0; i < n; i++)
            controlPoints[i + 1] = rhs[i];
        controlPoints[n + 1] = points[n - 1];
        return new NurbsCurve(3, controlPoints, null, knots);
    }

    private static NurbsCurve InterpolateClosed(IReadOnlyList<Vector3d> points)
    {
        int n = points.Count; // ≥ 3 here
        double[] parameters = ChordParameters(points, closed: true); // length n + 1; seam at 1

        // Periodic (unclamped) cubic knot vector: u_{j+3} = t̄_j for j = 0..n, extended on
        // both sides by wrapping the end interval lengths so the basis repeats with the
        // curve's period. Control points wrap: P_n = P_0, P_{n+1} = P_1, P_{n+2} = P_2,
        // which makes the spline C2-periodic — position, tangent, and curvature match at
        // the seam by construction.
        var knots = new double[n + 7];
        for (int j = 0; j <= n; j++)
            knots[j + 3] = parameters[j];
        for (int i = 0; i < 3; i++)
        {
            knots[2 - i] = knots[3 - i] - (parameters[n - i] - parameters[n - 1 - i]);
            knots[n + 4 + i] = knots[n + 3 + i] + (parameters[i + 1] - parameters[i]);
        }

        // Collocation at t̄_j (knot u_{j+3}, multiplicity 1): exactly N_j, N_{j+1}, N_{j+2}
        // are nonzero, coupling P_j, P_{j+1}, P_{j+2} with indices wrapped mod n — a cyclic
        // tridiagonal system. Solved densely with partial pivoting; interpolation point
        // counts are small, so O(n³) at construction time is irrelevant.
        var matrix = new double[n][];
        var rhs = new Vector3d[n];
        Span<double> basis = stackalloc double[4];
        for (int j = 0; j < n; j++)
        {
            matrix[j] = new double[n];
            int span = BSplineBasis.FindSpan(parameters[j], 3, n + 3, knots);
            BSplineBasis.Evaluate(span, parameters[j], 3, knots, basis);
            for (int k = 0; k <= 3; k++)
                matrix[j][(span - 3 + k) % n] += basis[k]; // basis[3] is an exact 0 at its own knot
            rhs[j] = points[j];
        }
        SolveDense(matrix, rhs);

        var controlPoints = new Vector3d[n + 3];
        for (int i = 0; i < n; i++)
            controlPoints[i] = rhs[i];
        controlPoints[n] = rhs[0];
        controlPoints[n + 1] = rhs[1];
        controlPoints[n + 2] = rhs[2];
        return new NurbsCurve(3, controlPoints, null, knots);
    }

    /// <summary>Basis value of control point <paramref name="controlIndex"/> given the nonzero window.</summary>
    private static double CoefficientOf(ReadOnlySpan<double> basis, int span, int controlIndex)
    {
        int offset = controlIndex - (span - 3);
        return offset is >= 0 and <= 3 ? basis[offset] : 0.0;
    }

    /// <summary>Thomas algorithm; overwrites <paramref name="rhs"/> with the solution.</summary>
    private static void SolveTridiagonal(double[] sub, double[] diag, double[] sup, Vector3d[] rhs)
    {
        int n = diag.Length;
        for (int i = 1; i < n; i++)
        {
            double m = sub[i] / diag[i - 1];
            diag[i] -= m * sup[i - 1];
            rhs[i] -= rhs[i - 1] * m;
        }
        rhs[n - 1] /= diag[n - 1];
        for (int i = n - 2; i >= 0; i--)
            rhs[i] = (rhs[i] - rhs[i + 1] * sup[i]) / diag[i];
    }

    /// <summary>Gaussian elimination with partial pivoting; overwrites inputs, solution in <paramref name="rhs"/>.</summary>
    private static void SolveDense(double[][] matrix, Vector3d[] rhs)
    {
        int n = rhs.Length;
        for (int column = 0; column < n; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < n; row++)
            {
                if (Math.Abs(matrix[row][column]) > Math.Abs(matrix[pivot][column]))
                    pivot = row;
            }
            if (Tolerance.Default.IsZero(matrix[pivot][column]))
                throw new InvalidOperationException("Singular interpolation system; the input points are degenerate.");
            (matrix[column], matrix[pivot]) = (matrix[pivot], matrix[column]);
            (rhs[column], rhs[pivot]) = (rhs[pivot], rhs[column]);
            for (int row = column + 1; row < n; row++)
            {
                double m = matrix[row][column] / matrix[column][column];
                for (int k = column; k < n; k++)
                    matrix[row][k] -= m * matrix[column][k];
                rhs[row] -= rhs[column] * m;
            }
        }
        for (int row = n - 1; row >= 0; row--)
        {
            var sum = rhs[row];
            for (int k = row + 1; k < n; k++)
                sum -= rhs[k] * matrix[row][k];
            rhs[row] = sum / matrix[row][row];
        }
    }

    public override Interval Domain => new(Knots[Degree], Knots[ControlPoints.Count]);

    public override bool IsClosed =>
        PointAt(Domain.Start).AreEqual(PointAt(Domain.End), Tolerance.Default);

    public override Vector3d PointAt(double t)
    {
        t = Domain.Clamp(t);
        int span = BSplineBasis.FindSpan(t, Degree, ControlPoints.Count, Knots);
        Span<double> basis = stackalloc double[Degree + 1];
        BSplineBasis.Evaluate(span, t, Degree, Knots, basis);

        var numerator = Vector3d.Zero;
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

    /// <summary>Exact first derivative dC/dt (rational quotient rule over the homogeneous form).</summary>
    public override Vector3d DerivativeAt(double t)
    {
        Span<Vector3d> derivatives = stackalloc Vector3d[2];
        EvaluateDerivatives(t, 1, derivatives);
        return derivatives[1];
    }

    /// <summary>Exact second derivative d²C/dt² (right-sided value at knots of reduced continuity).</summary>
    public override Vector3d SecondDerivativeAt(double t)
    {
        Span<Vector3d> derivatives = stackalloc Vector3d[3];
        EvaluateDerivatives(t, 2, derivatives);
        return derivatives[2];
    }

    /// <summary>
    /// Exact unit tangent from the analytic derivative — never finite differences, which
    /// carry ~1e-9 angular error that sweep frames and welded tessellation seams cannot
    /// tolerate (see the numerical notes in CLAUDE.md).
    /// </summary>
    public override Vector3d TangentAt(double t)
    {
        var derivative = DerivativeAt(t);
        // Fall back to the base finite differences only at (rare) stationary points.
        return derivative.TryNormalize(new Tolerance(1e-14, 1e-14), out var unit)
            ? unit
            : base.TangentAt(t);
    }

    /// <summary>
    /// Curve derivatives C, C′, …, C⁽ᵏ⁾ into <paramref name="result"/> (length order + 1).
    /// Rational curves use the generalized quotient rule (The NURBS Book, eq. 4.8):
    /// C⁽ᵏ⁾ = (A⁽ᵏ⁾ − Σᵢ₌₁..ₖ C(k,i) w⁽ⁱ⁾ C⁽ᵏ⁻ⁱ⁾) / w, where A is the weighted numerator.
    /// </summary>
    private void EvaluateDerivatives(double t, int order, Span<Vector3d> result)
    {
        t = Domain.Clamp(t);
        int span = BSplineBasis.FindSpan(t, Degree, ControlPoints.Count, Knots);
        int stride = Degree + 1;
        Span<double> ders = stackalloc double[(order + 1) * stride];
        BSplineBasis.EvaluateDerivatives(span, t, Degree, Knots, order, ders);

        Span<Vector3d> numerator = stackalloc Vector3d[order + 1];
        Span<double> weight = stackalloc double[order + 1];
        for (int k = 0; k <= order; k++)
        {
            var a = Vector3d.Zero;
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

/// <summary>
/// Ellipse: center + A·cos t + B·sin t for orthogonal semi-axis vectors A and B;
/// t ∈ [0, 2π]. A circle when |A| = |B|.
/// </summary>
public sealed class Ellipse3d(Vector3d center, Vector3d semiAxisX, Vector3d semiAxisY) : Curve3d
{
    /// <summary>Ellipse in the frame's X/Y plane centered at its origin, with semi-axis
    /// lengths <paramref name="semiMajor"/> along frame X and <paramref name="semiMinor"/>
    /// along frame Y.</summary>
    public Ellipse3d(in Frame3d frame, double semiMajor, double semiMinor)
        : this(frame.Origin, frame.X * semiMajor, frame.Y * semiMinor) { }

    public Vector3d Center => center;
    public Vector3d SemiAxisX => semiAxisX;
    public Vector3d SemiAxisY => semiAxisY;

    public override Interval Domain => new(0, 2 * Math.PI);
    public override bool IsClosed => true;

    public override Vector3d PointAt(double t) =>
        center + semiAxisX * Math.Cos(t) + semiAxisY * Math.Sin(t);

    public override Vector3d TangentAt(double t) =>
        (-semiAxisX * Math.Sin(t) + semiAxisY * Math.Cos(t)).Normalized();

    public override Vector3d DerivativeAt(double t) =>
        -semiAxisX * Math.Sin(t) + semiAxisY * Math.Cos(t);

    public override Vector3d SecondDerivativeAt(double t) =>
        -semiAxisX * Math.Cos(t) - semiAxisY * Math.Sin(t);
}

/// <summary>
/// Piecewise-linear curve through a point sequence, chord-length parameterized over
/// [0, total length]. Used for numerically traced curves (e.g. surface–surface
/// intersections); refine or fit downstream if smoothness is needed.
/// </summary>
public sealed class PolylineCurve3d : Curve3d
{
    private readonly Vector3d[] _points;
    private readonly double[] _cumulative;
    private readonly bool _isClosed;

    public IReadOnlyList<Vector3d> Points => _points;

    /// <summary>
    /// The curve parameter of each vertex (cumulative chord length, one per
    /// <see cref="Points"/> entry). The polyline is exact at these parameters and
    /// chordal between them — consumers that need on-curve-and-on-surface samples
    /// (pullback, tessellation) must sample here, not at uniform parameters.
    /// </summary>
    public IReadOnlyList<double> VertexParameters => _cumulative;

    public PolylineCurve3d(IReadOnlyList<Vector3d> points, bool isClosed = false)
    {
        if (points.Count < 2)
            throw new ArgumentException("A polyline needs at least 2 points.");
        // A closed polyline repeats its first point at the end for seamless evaluation.
        _points = isClosed && !points[0].AreEqual(points[^1], Tolerance.Default)
            ? [.. points, points[0]]
            : [.. points];
        _isClosed = isClosed;
        _cumulative = new double[_points.Length];
        for (int i = 1; i < _points.Length; i++)
            _cumulative[i] = _cumulative[i - 1] + _points[i].DistanceTo(_points[i - 1]);
    }

    public override Interval Domain => new(0, _cumulative[^1]);
    public override bool IsClosed => _isClosed;

    /// <summary>
    /// Exact, and exactly the identity: a polyline IS parameterized by cumulative chord
    /// length, so its parameter and its arc length are the same number. Quadrature would
    /// integrate a piecewise-constant speed of 1 and return the same answer more slowly and
    /// less accurately (the finite-difference speed straddles every vertex).
    /// </summary>
    public override double ArcLength(double from, double to, double relativeTolerance = 1e-12) =>
        Domain.Clamp(to) - Domain.Clamp(from);

    /// <summary>
    /// The same curve through a subset of its own vertices, dropping those within
    /// <paramref name="tolerance"/> of the chord that replaces them
    /// (<see cref="PolylineSimplify"/>, Douglas–Peucker). The marching tracer emits samples
    /// at its march step, which says more about the step than about the curve, so a traced
    /// arc typically keeps a handful of its hundreds of points.
    /// </summary>
    /// <remarks>
    /// Retained points are bit-for-bit the originals, but the PARAMETERIZATION changes: a
    /// polyline is chord-length parameterized, so dropping a vertex shortens the domain.
    /// Anything holding parameters into this curve — a <c>CurveSegment</c>, a face's pulled
    /// loop, a boolean's mandatory break — must be rebuilt, which is why nothing in the
    /// pipeline simplifies implicitly.
    /// </remarks>
    public PolylineCurve3d Simplified(double tolerance)
    {
        var input = _isClosed ? _points[..^1] : _points;
        var points = _isClosed
            ? PolylineSimplify.SimplifyLoop(input, tolerance)
            : PolylineSimplify.Simplify(input, tolerance);
        return points.Count == input.Length ? this : new PolylineCurve3d(points, _isClosed);
    }

    public override Vector3d PointAt(double t)
    {
        t = Domain.Clamp(t);
        int index = Array.BinarySearch(_cumulative, t);
        if (index >= 0)
            return _points[index];
        index = ~index; // first element greater than t
        double segment = _cumulative[index] - _cumulative[index - 1];
        double f = segment > 0 ? (t - _cumulative[index - 1]) / segment : 0;
        return Vector3d.Lerp(_points[index - 1], _points[index], f);
    }
}
