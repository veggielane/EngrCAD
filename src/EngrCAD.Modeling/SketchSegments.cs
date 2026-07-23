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

    public abstract double Distance(in Vector2d point);

    /// <summary>Splits into y-monotone pieces for robust even–odd parity.</summary>
    public abstract IEnumerable<MonotonePiece> MonotonePieces();
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
    public override Vector2d End => PointAt(startAngle + sweep);

    public bool IsFullCircle => Math.Abs(Math.Abs(sweep) - 2 * Math.PI) < 1e-12;

    public override SketchSegment Reversed() => new ArcSeg(center, radius, startAngle + sweep, -sweep);

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

        for (int i = 0; i + 1 < breaks.Count; i++)
        {
            var from = PointAt(breaks[i]);
            var to = PointAt(breaks[i + 1]);
            if (Math.Abs(from.Y - to.Y) <= 0)
                continue;
            // The branch (left/right half of the circle) is fixed within a piece.
            double mid = (breaks[i] + breaks[i + 1]) / 2;
            bool rightBranch = Math.Cos(mid) >= 0;
            yield return new ArcPiece(center, radius, from.Y, to.Y, rightBranch);
        }
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

// -------------------------------------------------------------------------- bézier

internal sealed class CubicSeg(Vector2d p0, Vector2d c1, Vector2d c2, Vector2d p3) : SketchSegment
{
    public override Vector2d Start => p0;
    public override Vector2d End => p3;

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
