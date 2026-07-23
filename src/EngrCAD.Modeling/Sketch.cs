using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>A placement for 2D sketches: origin plus orthonormal in-plane axes.</summary>
public readonly struct SketchPlane
{
    public Vector3d Origin { get; }
    public Vector3d XAxis { get; }
    public Vector3d YAxis { get; }
    public Vector3d Normal => XAxis.Cross(YAxis);

    private SketchPlane(in Vector3d origin, in Vector3d xAxis, in Vector3d yAxis)
    {
        Origin = origin;
        XAxis = xAxis;
        YAxis = yAxis;
    }

    public static readonly SketchPlane XY = new(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY);
    public static readonly SketchPlane XZ = new(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ);
    public static readonly SketchPlane YZ = new(Vector3d.Zero, Vector3d.UnitY, Vector3d.UnitZ);

    public static SketchPlane At(in Vector3d origin, in Vector3d xAxis, in Vector3d yAxis)
    {
        var x = xAxis.Normalized();
        var y = (yAxis - x * yAxis.Dot(x)).Normalized(); // re-orthogonalized
        return new SketchPlane(origin, x, y);
    }

    public Vector3d ToWorld(in Vector2d point) => Origin + XAxis * point.X + YAxis * point.Y;

    /// <summary>Rigid map from sketch-local (x, y, 0) coordinates to world.</summary>
    internal Matrix4d ToMatrix() => ToMatrixAt(default);

    /// <summary>Rigid map of the plane frame re-originated at a 2D point (hole placement).</summary>
    internal Matrix4d ToMatrixAt(in Vector2d point)
    {
        var n = Normal;
        var origin = ToWorld(point);
        return new Matrix4d(
            XAxis.X, YAxis.X, n.X, origin.X,
            XAxis.Y, YAxis.Y, n.Y, origin.Y,
            XAxis.Z, YAxis.Z, n.Z, origin.Z,
            0, 0, 0, 1);
    }
}

/// <summary>
/// A closed 2D region drawn from lines, circular arcs, and (cubic/quadratic) Bézier
/// curves — one outer loop plus optional holes. Sketches are pure 2D; consuming
/// operations (<c>Shape.Extrude/Revolve/Sweep</c>) place them with a
/// <see cref="SketchPlane"/>. Every representation honors them: B-Rep via exact curve
/// profiles, implicit via an exact 2D signed distance, mesh via tessellation.
/// </summary>
public sealed class Sketch
{
    internal IReadOnlyList<SketchSegment> Segments { get; }   // outer loop, normalized CCW
    internal IReadOnlyList<Sketch> Holes { get; }

    internal Sketch(IReadOnlyList<SketchSegment> segments, IReadOnlyList<Sketch> holes)
    {
        if (segments.Count == 0)
            throw new ArgumentException("A sketch needs at least one segment.");
        for (int i = 0; i < segments.Count; i++)
        {
            var next = segments[(i + 1) % segments.Count];
            if (segments[i].End.DistanceTo(next.Start) > 1e-9)
                throw new ArgumentException(
                    $"Sketch is not a closed chain: segment {i} ends at {segments[i].End} but the next starts at {next.Start}.");
        }

        double signed = segments.Sum(s => s.SignedAreaContribution());
        if (Math.Abs(signed) < 1e-12)
            throw new ArgumentException("Sketch encloses no area.");
        Segments = signed < 0 ? [.. segments.Reverse().Select(s => s.Reversed())] : segments;
        Holes = holes;
    }

    // ---- construction ----

    public static SketchBuilder Start(double x, double y) => new(new Vector2d(x, y));

    /// <summary>Axis-aligned rectangle centered at the origin.</summary>
    public static Sketch Rectangle(double width, double height) => Polygon(
    [
        new(-width / 2, -height / 2), new(width / 2, -height / 2),
        new(width / 2, height / 2), new(-width / 2, height / 2),
    ]);

    public static Sketch Polygon(IReadOnlyList<Vector2d> corners)
    {
        if (corners.Count < 3)
            throw new ArgumentException("A polygon sketch needs at least 3 corners.");
        var segments = new List<SketchSegment>(corners.Count);
        for (int i = 0; i < corners.Count; i++)
            segments.Add(new LineSeg(corners[i], corners[(i + 1) % corners.Count]));
        return new Sketch(segments, []);
    }

    public static Sketch Circle(double radius) => Circle(default, radius);

    public static Sketch Circle(Vector2d center, double radius)
    {
        if (radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        return new Sketch([new ArcSeg(center, radius, 0, 2 * Math.PI)], []);
    }

    /// <summary>Rectangle centered at the origin with quarter-circle corners.</summary>
    public static Sketch RoundedRectangle(double width, double height, double cornerRadius)
    {
        double w = width / 2, h = height / 2, r = cornerRadius;
        if (r <= 0 || r > Math.Min(w, h))
            throw new ArgumentOutOfRangeException(nameof(cornerRadius));
        return Start(w - r, -h)
            .ArcTo(new(w, -h + r), r, clockwise: false)
            .LineTo(w, h - r)
            .ArcTo(new(w - r, h), r, clockwise: false)
            .LineTo(-w + r, h)
            .ArcTo(new(-w, h - r), r, clockwise: false)
            .LineTo(-w, -h + r)
            .ArcTo(new(-w + r, -h), r, clockwise: false)
            .Close();
    }

    /// <summary>Stadium: a length × width slot centered at the origin (semicircle ends).</summary>
    public static Sketch Slot(double length, double width)
    {
        double r = width / 2, half = length / 2 - r;
        if (r <= 0 || half < 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        return Start(-half, -r)
            .LineTo(half, -r)
            .ArcTo(new(half, r), r, clockwise: false)
            .LineTo(-half, r)
            .ArcTo(new(-half, -r), r, clockwise: false)
            .Close();
    }

    /// <summary>The sketch with an inner region removed (parity handles the rest).</summary>
    public Sketch WithHole(Sketch inner)
    {
        if (inner.Holes.Count > 0)
            throw new ArgumentException("Hole sketches may not have holes of their own.", nameof(inner));
        return new Sketch(Segments, [.. Holes, inner]);
    }

    // ---- measures ----

    /// <summary>Enclosed area (outer minus holes). Exact: analytic for lines and arcs,
    /// Gauss quadrature (exact for cubics) for Bézier segments.</summary>
    public double Area() =>
        Segments.Sum(s => s.SignedAreaContribution()) - Holes.Sum(h => h.Area());

    /// <summary>2D bounds of the outer loop (z = 0).</summary>
    public Aabb Bounds
    {
        get
        {
            var bounds = Aabb.Empty;
            foreach (var segment in Segments)
                bounds = bounds.Union(segment.Bounds());
            return bounds;
        }
    }

    // ---- lowering ----

    /// <summary>The sketch as an exact 2D signed distance field — compose it with
    /// <c>Sdf.ExtrudedRegion</c>/<c>Sdf.RevolvedRegion</c> or your own fields.</summary>
    public Implicit.IPlanarRegion ToRegion() => new SketchRegion(this);

    /// <summary>B-Rep profiles in sketch-local coordinates (the XY plane, z = 0);
    /// consumers place them with a transform.</summary>
    internal (Profile Outer, IReadOnlyList<Profile>? Holes) ToProfiles()
    {
        var outer = new Profile([.. Segments.Select(s => s.ToCurve())]);
        if (Holes.Count == 0)
            return (outer, null);
        return (outer, [.. Holes.Select(h => new Profile([.. h.Segments.Select(s => s.ToCurve())]))]);
    }
}

/// <summary>Fluent path builder: chain segments from a start point, then
/// <see cref="Close"/> (a closing line is added automatically if needed).</summary>
public sealed class SketchBuilder
{
    private readonly List<SketchSegment> _segments = [];
    private readonly Vector2d _start;
    private Vector2d _current;

    internal SketchBuilder(Vector2d start)
    {
        _start = start;
        _current = start;
    }

    public SketchBuilder LineTo(double x, double y) => LineTo(new Vector2d(x, y));

    public SketchBuilder LineTo(Vector2d end)
    {
        _segments.Add(new LineSeg(_current, end));
        _current = end;
        return this;
    }

    /// <summary>Circular arc to <paramref name="end"/> with the given radius —
    /// SVG-style: <paramref name="clockwise"/> picks the sweep direction,
    /// <paramref name="largeArc"/> the long way around.</summary>
    public SketchBuilder ArcTo(Vector2d end, double radius, bool clockwise, bool largeArc = false)
    {
        var chord = end - _current;
        double length = chord.Length;
        if (length < 1e-12)
            throw new ArgumentException("Arc endpoints coincide; use Circle for full circles.");
        if (radius < length / 2 - 1e-12)
            throw new ArgumentException($"Radius {radius} is too small for a chord of length {length}.");

        double h = Math.Sqrt(Math.Max(0, radius * radius - length * length / 4));
        var mid = (_current + end) * 0.5;
        var left = chord.Perpendicular.Normalized();          // +90° from the chord
        // CCW small arcs curve left of the chord ⇒ center on the left; each of the
        // clockwise/largeArc flags flips the side.
        var center = mid + left * ((clockwise ^ largeArc) ? -h : h);

        double startAngle = Math.Atan2(_current.Y - center.Y, _current.X - center.X);
        double endAngle = Math.Atan2(end.Y - center.Y, end.X - center.X);
        double sweep = endAngle - startAngle;
        if (!clockwise)
            sweep = sweep <= 1e-12 ? sweep + 2 * Math.PI : sweep;      // positive
        else
            sweep = sweep >= -1e-12 ? sweep - 2 * Math.PI : sweep;     // negative

        _segments.Add(new ArcSeg(center, radius, startAngle, sweep));
        _current = end;
        return this;
    }

    /// <summary>Circular arc through an interior point to <paramref name="end"/>.</summary>
    public SketchBuilder ArcThrough(Vector2d via, Vector2d end)
    {
        // Circumcenter of (current, via, end).
        var a = _current;
        double d = 2 * (a.X * (via.Y - end.Y) + via.X * (end.Y - a.Y) + end.X * (a.Y - via.Y));
        if (Math.Abs(d) < 1e-12)
            throw new ArgumentException("Arc points are collinear.");
        double a2 = a.LengthSquared, b2 = via.LengthSquared, c2 = end.LengthSquared;
        var center = new Vector2d(
            (a2 * (via.Y - end.Y) + b2 * (end.Y - a.Y) + c2 * (a.Y - via.Y)) / d,
            (a2 * (end.X - via.X) + b2 * (a.X - end.X) + c2 * (via.X - a.X)) / d);
        double radius = a.DistanceTo(center);

        double startAngle = Math.Atan2(a.Y - center.Y, a.X - center.X);
        double viaAngle = Math.Atan2(via.Y - center.Y, via.X - center.X);
        double endAngle = Math.Atan2(end.Y - center.Y, end.X - center.X);
        double ccwToVia = Wrap(viaAngle - startAngle);
        double ccwToEnd = Wrap(endAngle - startAngle);
        double sweep = ccwToVia <= ccwToEnd ? ccwToEnd : ccwToEnd - 2 * Math.PI;

        _segments.Add(new ArcSeg(center, radius, startAngle, sweep));
        _current = end;
        return this;

        static double Wrap(double angle) => angle - 2 * Math.PI * Math.Floor(angle / (2 * Math.PI));
    }

    /// <summary>Cubic Bézier with control points <paramref name="control1"/>/<paramref name="control2"/>.</summary>
    public SketchBuilder BezierTo(Vector2d control1, Vector2d control2, Vector2d end)
    {
        _segments.Add(new CubicSeg(_current, control1, control2, end));
        _current = end;
        return this;
    }

    /// <summary>Quadratic Bézier (stored as the exactly equivalent elevated cubic).</summary>
    public SketchBuilder QuadraticTo(Vector2d control, Vector2d end)
    {
        var c1 = _current + (control - _current) * (2.0 / 3.0);
        var c2 = end + (control - end) * (2.0 / 3.0);
        return BezierTo(c1, c2, end);
    }

    public Sketch Close()
    {
        if (_current.DistanceTo(_start) > 1e-9)
            _segments.Add(new LineSeg(_current, _start));
        return new Sketch([.. _segments], []);
    }
}
