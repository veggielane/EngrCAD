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
    /// The innermost curve beneath wrapper types (<see cref="ReversedCurve"/>,
    /// <see cref="TransformedCurve"/>); consumers use it to pick sampling strategies.
    /// </summary>
    public virtual Curve3d Underlying => this;

    public Curve3d Reversed() => this is ReversedCurve r ? r.Base : new ReversedCurve(this);

    public Curve3d Transformed(in Matrix4d transform) => new TransformedCurve(this, transform);
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
}

/// <summary>
/// Full circle in the plane spanned by <paramref name="xDirection"/>/<paramref name="yDirection"/>
/// (unit, orthogonal); t is the angle in [0, 2π].
/// </summary>
public sealed class Circle3d(Vector3d center, Vector3d xDirection, Vector3d yDirection, double radius) : Curve3d
{
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

    public override Interval Domain => new(Knots[Degree], Knots[ControlPoints.Count]);

    public override bool IsClosed =>
        PointAt(Domain.Start).AreEqual(PointAt(Domain.End), Tolerance.Default);

    public override Vector3d PointAt(double t)
    {
        t = Domain.Clamp(t);
        int span = NurbsBasis.FindSpan(t, Degree, ControlPoints.Count, Knots);
        Span<double> basis = stackalloc double[Degree + 1];
        NurbsBasis.Evaluate(span, t, Degree, Knots, basis);

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
}

/// <summary>
/// Ellipse: center + A·cos t + B·sin t for orthogonal semi-axis vectors A and B;
/// t ∈ [0, 2π]. A circle when |A| = |B|.
/// </summary>
public sealed class Ellipse3d(Vector3d center, Vector3d semiAxisX, Vector3d semiAxisY) : Curve3d
{
    public Vector3d Center => center;
    public Vector3d SemiAxisX => semiAxisX;
    public Vector3d SemiAxisY => semiAxisY;

    public override Interval Domain => new(0, 2 * Math.PI);
    public override bool IsClosed => true;

    public override Vector3d PointAt(double t) =>
        center + semiAxisX * Math.Cos(t) + semiAxisY * Math.Sin(t);

    public override Vector3d TangentAt(double t) =>
        (-semiAxisX * Math.Sin(t) + semiAxisY * Math.Cos(t)).Normalized();
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

/// <summary>Shared B-spline basis evaluation (The NURBS Book, algorithms A2.1/A2.2).</summary>
internal static class NurbsBasis
{
    public static int FindSpan(double u, int degree, int controlPointCount, IReadOnlyList<double> knots)
    {
        int n = controlPointCount - 1;
        if (u >= knots[n + 1])
            return n;
        if (u <= knots[degree])
            return degree;
        int low = degree, high = n + 1;
        int mid = (low + high) / 2;
        while (u < knots[mid] || u >= knots[mid + 1])
        {
            if (u < knots[mid])
                high = mid;
            else
                low = mid;
            mid = (low + high) / 2;
        }
        return mid;
    }

    public static void Evaluate(int span, double u, int degree, IReadOnlyList<double> knots, Span<double> basis)
    {
        Span<double> left = stackalloc double[degree + 1];
        Span<double> right = stackalloc double[degree + 1];
        basis[0] = 1;
        for (int j = 1; j <= degree; j++)
        {
            left[j] = u - knots[span + 1 - j];
            right[j] = knots[span + j] - u;
            double saved = 0;
            for (int r = 0; r < j; r++)
            {
                double temp = basis[r] / (right[r + 1] + left[j - r]);
                basis[r] = saved + right[r + 1] * temp;
                saved = left[j - r] * temp;
            }
            basis[j] = saved;
        }
    }
}
