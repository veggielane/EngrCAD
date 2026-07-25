using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Modeling;

/// <summary>A placement for 2D sketches: a rigid <see cref="Frame3d"/> whose X/Y span
/// the sketch plane (the sketch's 2D coordinates) and whose Z is the plane normal.</summary>
public readonly struct SketchPlane
{
    /// <summary>The underlying rigid frame (X/Y in-plane, Z = normal).</summary>
    public Frame3d Frame { get; }

    public Vector3d Origin => Frame.Origin;
    public Vector3d XAxis => Frame.X;
    public Vector3d YAxis => Frame.Y;
    public Vector3d Normal => Frame.Z;

    public SketchPlane(in Frame3d frame) => Frame = frame;

    public static readonly SketchPlane XY = new(Frame3d.WorldXY);
    public static readonly SketchPlane XZ = new(Frame3d.FromOrthonormal(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ));
    public static readonly SketchPlane YZ = new(Frame3d.FromOrthonormal(Vector3d.Zero, Vector3d.UnitY, Vector3d.UnitZ));

    public static SketchPlane At(in Vector3d origin, in Vector3d xAxis, in Vector3d yAxis) =>
        new(Frame3d.FromXY(origin, xAxis, yAxis)); // same Gram-Schmidt order as before (locked by Core test)

    /// <summary>
    /// Sketch placement on a planar face of a lowered body (find one with
    /// <c>BrepQueries</c>, e.g. <c>solid.PlanarFacesWithNormal(Vector3d.UnitZ)</c>).
    /// The plane's X/Y are the face surface's own directions, its normal the face's
    /// outward normal (so <c>Shape.Extrude</c> grows out of the material and
    /// <c>Shape.Drill</c> cuts into it), and its origin the face's outer-loop vertex
    /// centroid on the plane. Throws when the face is not planar.
    /// </summary>
    public static SketchPlane On(BrepFace face) =>
        new(face.Frame() ?? throw new ArgumentException(
            "Sketches need a planar face; this face is not planar.", nameof(face)));

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
    /// <summary>
    /// The epsilon ladder's <b>scale-free degeneracy</b> tier for sketch geometry: a
    /// quantity is degenerate when it is this small RELATIVE to the coordinates that
    /// produced it — the enclosed area against the sketch's extent², a chord against the
    /// endpoints' magnitude, a circumcenter determinant against that magnitude squared.
    /// Absolute floors cannot serve here: a sketch is a user-authored profile whose units
    /// and scale are entirely the caller's choice (a micron-scale seal groove and a
    /// metre-scale weldment both go through this constructor), and an absolute area floor
    /// fails quadratically with that scale in BOTH directions. Deliberately NOT a
    /// <c>Tolerance</c> — this is a degeneracy/round-off test, not a model-unit
    /// coincidence test; sketch closure still uses the 1e-9 absolute weld tier because
    /// those endpoints become exactly-shared vertices downstream.
    /// </summary>
    internal const double RelativeDegeneracy = 1e-12;

    internal IReadOnlyList<SketchSegment> Segments { get; }   // outer loop, normalized CCW
    internal IReadOnlyList<Sketch> Holes { get; }

    internal Sketch(IReadOnlyList<SketchSegment> segments, IReadOnlyList<Sketch> holes)
    {
        if (segments.Count == 0)
            throw new ArgumentException("A sketch needs at least one segment.");
        for (int i = 0; i < segments.Count; i++)
        {
            var next = segments[(i + 1) % segments.Count];
            // Weld-scale (1e-9) closure validation: sketch joints become exact shared
            // vertices downstream, so gaps beyond weld tolerance must be rejected here.
            if (segments[i].End.DistanceTo(next.Start) > 1e-9)
                throw new ArgumentException(
                    $"Sketch is not a closed chain: segment {i} ends at {segments[i].End} but the next starts at {next.Start}.");
        }

        double signed = segments.Sum(s => s.SignedAreaContribution());
        // Degenerate-area guard, RELATIVE to the sketch's own extent. An absolute area
        // floor is a latent scale bug of exactly the kind CLAUDE.md records for BSP's
        // plane epsilon: area is quadratic in scale, so one fixed number is simultaneously
        // too coarse for a small sketch (a legitimate sub-micron profile encloses less
        // than 1e-12 and was rejected) and far too fine for a large one (a metre-scale
        // sliver 1e-10 wide encloses 1e-7 and sailed through). Comparing |area| against
        // extent² is the only scale-free form. Non-strict, so an exactly-zero area is
        // always rejected — including the all-points-coincident case where extent is 0.
        var bounds = Aabb.Empty;
        foreach (var segment in segments)
            bounds = bounds.Union(segment.Bounds());
        double extent = Math.Max(bounds.Size.X, bounds.Size.Y);
        if (Math.Abs(signed) <= extent * extent * RelativeDegeneracy)
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

    // ---- polygonal regions + 2D booleans ----

    /// <summary>
    /// Default flattening tolerance for <see cref="ToRegions(double)"/> and the boolean
    /// sugar: no chord deviates more than 1 µm (model units are millimetres by convention)
    /// from the true arc or bézier.
    /// </summary>
    public const double DefaultChordTolerance = 1e-3;

    /// <summary>
    /// The sketch as polygonal <see cref="Region2d"/>s — the currency of 2D booleans,
    /// <c>Profile.FromRegion</c>, and any consumer that wants explicit loops.
    ///
    /// <para><b>Fidelity contract.</b> Arcs and béziers are FLATTENED to polylines within
    /// <paramref name="chordTolerance"/>; lines are exact. A sketch passed straight to
    /// <c>Shape.Extrude</c>/<c>Revolve</c>/<c>Sweep</c> keeps its exact curves (B-Rep gets
    /// exact NURBS profiles, implicit gets the exact 2D signed distance of
    /// <see cref="ToRegion"/>) — going through a region is a deliberate approximation, and
    /// anything built from the result inherits it. Exact curved 2D booleans are future work.</para>
    ///
    /// <para>Nesting is re-derived by <see cref="Region2d.FromLoops"/>, so hole loops are
    /// detected rather than declared.</para>
    /// </summary>
    public IReadOnlyList<Region2d> ToRegions(double chordTolerance = DefaultChordTolerance)
    {
        var loops = new List<IReadOnlyList<Vector2d>>();
        CollectLoops(this, chordTolerance, loops);
        return Region2d.FromLoops(loops);
    }

    /// <summary>
    /// Several sketches read as ONE bag of loops, sorted into regions by containment —
    /// automatic hole detection without <see cref="WithHole"/>: draw the plate outline and
    /// its bolt holes as separate sketches, pass them all, and the nesting falls out. Same
    /// flattening contract as <see cref="ToRegions(double)"/>.
    /// </summary>
    public static IReadOnlyList<Region2d> ToRegions(
        IEnumerable<Sketch> loops, double chordTolerance = DefaultChordTolerance)
    {
        ArgumentNullException.ThrowIfNull(loops);
        var flattened = new List<IReadOnlyList<Vector2d>>();
        foreach (var sketch in loops)
            CollectLoops(sketch, chordTolerance, flattened);
        return Region2d.FromLoops(flattened);
    }

    /// <summary>Everything covered by this sketch or <paramref name="other"/>, as regions
    /// (flattened — see <see cref="ToRegions(double)"/>'s fidelity contract).</summary>
    public IReadOnlyList<Region2d> Union(Sketch other, double chordTolerance = DefaultChordTolerance) =>
        Region2dBoolean.Union(ToRegions(chordTolerance), Requires(other).ToRegions(chordTolerance));

    /// <summary>Everything covered by both this sketch and <paramref name="other"/>, as regions.</summary>
    public IReadOnlyList<Region2d> Intersect(Sketch other, double chordTolerance = DefaultChordTolerance) =>
        Region2dBoolean.Intersection(ToRegions(chordTolerance), Requires(other).ToRegions(chordTolerance));

    /// <summary>This sketch with <paramref name="other"/> cut away, as regions — the plate
    /// with a pocket, the washer, the slotted bracket.</summary>
    public IReadOnlyList<Region2d> Subtract(Sketch other, double chordTolerance = DefaultChordTolerance) =>
        Region2dBoolean.Difference(ToRegions(chordTolerance), Requires(other).ToRegions(chordTolerance));

    private static Sketch Requires(Sketch other) =>
        other ?? throw new ArgumentNullException(nameof(other));

    private static void CollectLoops(Sketch sketch, double chordTolerance, List<IReadOnlyList<Vector2d>> into)
    {
        if (!(chordTolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(chordTolerance), "Chord tolerance must be positive.");
        into.Add(FlattenLoop(sketch.Segments, chordTolerance));
        foreach (var hole in sketch.Holes)
            into.Add(FlattenLoop(hole.Segments, chordTolerance));
    }

    /// <summary>Each segment contributes its start point only, so the chain's joints appear
    /// exactly once and the loop closes implicitly.</summary>
    private static IReadOnlyList<Vector2d> FlattenLoop(
        IReadOnlyList<SketchSegment> segments, double chordTolerance)
    {
        var points = new List<Vector2d>();
        foreach (var segment in segments)
            segment.Flatten(chordTolerance, points);
        return points;
    }

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
        // Both guards are relative to the coordinate magnitudes involved (see
        // Sketch.RelativeDegeneracy): "coincident" and "radius short of the semicircle by
        // round-off" are statements about precision at this scale, not about millimetres.
        double floor = Sketch.RelativeDegeneracy * Magnitude(_current, end);
        if (length <= floor)
            throw new ArgumentException("Arc endpoints coincide; use Circle for full circles.");
        if (radius < length / 2 - floor)
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
        // Angles are dimensionless, so these two ARE legitimately absolute: 1e-12 rad is
        // the round-off band around a zero sweep at any model scale.
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
        // d is four times the signed triangle area, so it is QUADRATIC in the coordinates:
        // an absolute floor called a perfectly good micron-scale arc collinear while
        // passing genuinely collinear metre-scale points through. Compare against
        // magnitude² instead (see Sketch.RelativeDegeneracy).
        double magnitude = Magnitude(a, via, end);
        if (Math.Abs(d) <= Sketch.RelativeDegeneracy * magnitude * magnitude)
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
        // Weld-scale (1e-9) absolute, deliberately: the closing point becomes an exactly
        // shared vertex downstream, and the weld tier is absolute by policy.
        if (_current.DistanceTo(_start) > 1e-9)
            _segments.Add(new LineSeg(_current, _start));
        return new Sketch([.. _segments], []);
    }

    /// <summary>Largest coordinate magnitude among the given points — the scale a
    /// degeneracy test at this point in the path is relative to.</summary>
    private static double Magnitude(in Vector2d a, in Vector2d b) =>
        Math.Max(Math.Max(Math.Abs(a.X), Math.Abs(a.Y)), Math.Max(Math.Abs(b.X), Math.Abs(b.Y)));

    private static double Magnitude(in Vector2d a, in Vector2d b, in Vector2d c) =>
        Math.Max(Magnitude(a, b), Math.Max(Math.Abs(c.X), Math.Abs(c.Y)));
}
