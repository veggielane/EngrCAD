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

    /// <summary>Full ellipse centred at the origin, semi-axes along +x and +y.</summary>
    public static Sketch Ellipse(double semiX, double semiY) => Ellipse(default, semiX, semiY, 0);

    /// <summary>
    /// Full ellipse: semi-axes <paramref name="semiX"/> and <paramref name="semiY"/>,
    /// rotated by <paramref name="rotationDegrees"/> about <paramref name="center"/>.
    /// Exact in all three representations (the segment carries an <see cref="Ellipse3d"/>,
    /// not a flattened polyline) — a circle when the two semi-axes are equal, though it
    /// stays an ellipse rather than collapsing to a circular arc, so
    /// <c>BrepQueries.IsCircular</c> and cylinder promotion will not claim it. Use
    /// <see cref="Circle(double)"/> when the shape really is a circle.
    /// </summary>
    public static Sketch Ellipse(Vector2d center, double semiX, double semiY, double rotationDegrees = 0)
    {
        if (!(semiX > 0))
            throw new ArgumentOutOfRangeException(nameof(semiX), "Ellipse semi-axes must be positive.");
        if (!(semiY > 0))
            throw new ArgumentOutOfRangeException(nameof(semiY), "Ellipse semi-axes must be positive.");
        double radians = rotationDegrees * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        return new Sketch(
            [new EllipseSeg(
                center, new Vector2d(cos, sin) * semiX, new Vector2d(-sin, cos) * semiY, 0, 2 * Math.PI)],
            []);
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

    /// <summary>
    /// The outer loop as an exact chain of <see cref="Curve2d"/>s — lines, arcs with their
    /// SIGNED sweep, and cubic Béziers. Nothing is flattened (contrast
    /// <see cref="ToRegions(double)"/>), so this is the lossless way out of the sketch
    /// vocabulary into the 2D curve family: fit a biarc chain, measure an arc length, offset
    /// one segment, hand a chain to <c>Profile.FromCurves</c>.
    /// </summary>
    /// <remarks>
    /// Hole loops are reached by their own sketches: a hole is a <see cref="Sketch"/>, and
    /// <see cref="WithHole"/> puts one back. That is the whole bridge — deliberately the
    /// smallest API that lets the two vocabularies meet, with no second copy of closure,
    /// winding or degeneracy validation on the 2D-curve side.
    /// </remarks>
    public IReadOnlyList<Curve2d> ToCurves() => [.. Segments.Select(s => s.ToCurve2d())];

    /// <summary>
    /// A sketch from a closed chain of exact 2D curves — the inverse of
    /// <see cref="ToCurves"/>. Lines, arcs and quadratic/cubic Béziers map to sketch
    /// segments exactly (a quadratic is elevated to the equivalent cubic, as
    /// <see cref="SketchBuilder.QuadraticTo"/> does); anything else is REFUSED by name,
    /// because a sketch segment vocabulary that quietly sampled a general NURBS would make
    /// every downstream "exact" claim false.
    /// </summary>
    /// <remarks>
    /// Validation is the ordinary <see cref="Sketch"/> constructor's — closure at the weld
    /// tier, enclosed area relative to the extent, winding normalization — so there is
    /// exactly one place those rules live.
    /// </remarks>
    public static Sketch FromCurves(IReadOnlyList<Curve2d> curves)
    {
        ArgumentNullException.ThrowIfNull(curves);
        if (curves.Count == 0)
            throw new ArgumentException("A sketch needs at least one curve.", nameof(curves));
        var segments = new List<SketchSegment>(curves.Count);
        for (int i = 0; i < curves.Count; i++)
            segments.Add(ToSegment(curves[i], i));
        return new Sketch(segments, []);
    }

    private static SketchSegment ToSegment(Curve2d curve, int index) => curve switch
    {
        Line2d line => new LineSeg(line.Start, line.End),
        Arc2d arc => new ArcSeg(arc.Center, arc.Radius, arc.StartAngle, arc.SweepAngle),
        Ellipse2d ellipse => new EllipseSeg(
            ellipse.Center, ellipse.SemiAxisX, ellipse.SemiAxisY, ellipse.StartAngle, ellipse.SweepAngle),
        BezierCurve2d { Degree: 3 } cubic => new CubicSeg(
            cubic.ControlPoints[0], cubic.ControlPoints[1], cubic.ControlPoints[2], cubic.ControlPoints[3]),
        BezierCurve2d { Degree: 2 } quadratic => Elevate(quadratic),
        _ => throw new ArgumentException(
            $"Curve {index} is a {curve.GetType().Name}, which has no exact sketch segment. "
            + "Sketches carry lines, circular and elliptical arcs, and cubic Beziers; convert or "
            + "approximate it deliberately before building a sketch from it."),
    };

    /// <summary>A quadratic Bézier as the EXACTLY equivalent cubic (degree elevation is a
    /// closed form, not an approximation) — the same arithmetic
    /// <see cref="SketchBuilder.QuadraticTo"/> uses.</summary>
    private static CubicSeg Elevate(BezierCurve2d quadratic)
    {
        var start = quadratic.ControlPoints[0];
        var control = quadratic.ControlPoints[1];
        var end = quadratic.ControlPoints[2];
        return new CubicSeg(
            start, start + (control - start) * (2.0 / 3.0), end + (control - end) * (2.0 / 3.0), end);
    }

    /// <summary>
    /// This sketch placed on a rigid 2D frame: local <c>(x, y)</c> becomes
    /// <c>origin + x·xAxis + y·xAxis.Perpendicular</c>. A rotation and a translation, so
    /// every segment moves EXACTLY — an arc keeps its radius and its signed sweep, an
    /// ellipse both semi-axes, a Bézier its control polygon — and holes ride with it.
    ///
    /// <para>Deliberately RIGID rather than affine. It exists because a sheet-metal
    /// flange's frame in the blank and its frame on the folded wall are the SAME rigid
    /// frame for the same local coordinates, which is what makes an unfold bookkeeping;
    /// an affine map would turn an arc into an ellipse and put a re-fit inside a path
    /// whose whole claim is that nothing is fitted.</para>
    /// </summary>
    /// <param name="origin">Where local (0, 0) lands.</param>
    /// <param name="xAxis">Where local (1, 0) points; normalized, so a zero-length axis is
    /// refused rather than silently collapsing the sketch to a point.</param>
    public Sketch Placed(Vector2d origin, Vector2d xAxis)
    {
        if (!(xAxis.LengthSquared > 0))
            throw new ArgumentException("A placement frame's X axis must be non-zero.", nameof(xAxis));
        var unit = xAxis.Normalized();
        return new Sketch(
            [.. Segments.Select(s => s.Placed(origin, unit))],
            [.. Holes.Select(h => h.Placed(origin, unit))]);
    }

    /// <summary>
    /// This sketch reflected in the y axis (<c>x → −x</c>), traversal sense RESTORED.
    ///
    /// <para>A reflection reverses a loop's winding, so each loop's segments are mirrored,
    /// listed in reverse order and individually reversed — one rule in one place, rather
    /// than each segment kind half-repairing its own sense. That also means a segment at
    /// index <c>i</c> of an <c>n</c>-segment loop lands at index <c>n − 1 − i</c>, which is
    /// what anything naming a segment by index has to remap (a sheet-metal flange does).</para>
    /// </summary>
    public Sketch Mirrored()
    {
        static IReadOnlyList<SketchSegment> Flip(IReadOnlyList<SketchSegment> segments) =>
            [.. segments.Select(s => s.MirroredInY()).Reverse().Select(s => s.Reversed())];
        return new Sketch(Flip(Segments), [.. Holes.Select(h => h.Mirrored())]);
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

    // ---- constraints ----

    /// <summary>
    /// Begins constraining this sketch — the variational constraint layer
    /// (<see cref="ConstrainedSketch"/>): coincident/horizontal/vertical/parallel/
    /// perpendicular/tangent/equal/concentric plus distance/angle/radius dimensions,
    /// solved by Levenberg–Marquardt with the DRAWN geometry as seed and branch
    /// selector. Solving returns a NEW solved <see cref="Sketch"/>; this one is never
    /// modified.
    /// </summary>
    public ConstrainedSketch Constrain() => new(this);

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

    /// <summary>
    /// This sketch grown (positive <paramref name="delta"/>) or shrunk (negative) by a
    /// constant distance, as regions — clearance fits, wall shells, pocket stock, cutter
    /// compensation. Corners are closed by <paramref name="join"/>; see
    /// <see cref="Region2dOffset"/> for the join geometry and the miter-limit rule.
    ///
    /// <para>An inward offset may split the sketch into several regions or consume it
    /// entirely (a 2 mm rib shrunk by 1.5 mm is nothing), which is why this returns a list.
    /// Flattening follows <see cref="ToRegions(double)"/>: arcs and béziers become polylines
    /// first, and round joins are inscribed arcs, so the result sits just inside the true
    /// offset.</para>
    /// </summary>
    public IReadOnlyList<Region2d> Offset(
        double delta, OffsetJoin join = OffsetJoin.Round,
        double miterLimit = Region2dOffset.DefaultMiterLimit,
        double chordTolerance = DefaultChordTolerance) =>
        Region2dOffset.Offset(ToRegions(chordTolerance), delta, join, miterLimit, chordTolerance);

    // ---- curved regions + EXACT 2D booleans ----

    /// <summary>
    /// The sketch as <see cref="CurvedRegion2d"/>s — the currency of the EXACT 2D booleans
    /// and offset, and the way to keep a sketch's arcs through a boolean.
    ///
    /// <para><b>Fidelity contract.</b> Lines and circular arcs cross UNCHANGED: a bore stays
    /// a circle, a slot end stays a semicircle, and a boolean of two such sketches has an
    /// exact closed-form area. Béziers are the one thing still flattened, at
    /// <paramref name="chordTolerance"/>, and deliberately so — the curved arrangement's
    /// tangential tie-break is complete for lines and circles and would need an unbounded
    /// jet for a third shape (see <c>CurvedArrangement2d</c>). A sketch with no Béziers goes
    /// through this door losslessly; one with Béziers is exact except along them.</para>
    ///
    /// <para>Nesting is re-derived by <see cref="CurvedRegion2d.FromLoops"/>, so hole loops
    /// are detected rather than declared, exactly as in <see cref="ToRegions(double)"/>.</para>
    /// </summary>
    public IReadOnlyList<CurvedRegion2d> ToCurvedRegions(double chordTolerance = DefaultChordTolerance)
    {
        var loops = new List<IReadOnlyList<CurvedEdge2d>>();
        CollectCurvedLoops(this, chordTolerance, loops);
        return CurvedRegion2d.FromLoops(loops);
    }

    /// <summary>Several sketches read as ONE bag of curved loops, sorted into regions by
    /// containment — the curved twin of <see cref="ToRegions(IEnumerable{Sketch}, double)"/>.</summary>
    public static IReadOnlyList<CurvedRegion2d> ToCurvedRegions(
        IEnumerable<Sketch> loops, double chordTolerance = DefaultChordTolerance)
    {
        ArgumentNullException.ThrowIfNull(loops);
        var collected = new List<IReadOnlyList<CurvedEdge2d>>();
        foreach (var sketch in loops)
            CollectCurvedLoops(sketch, chordTolerance, collected);
        return CurvedRegion2d.FromLoops(collected);
    }

    /// <summary>
    /// A sketch from a curved region — the way BACK from an exact 2D boolean or offset into
    /// the modelling vocabulary, so the result can be extruded, revolved or swept with its
    /// arcs intact (B-Rep then gets exact NURBS arc profiles rather than a prism of chords).
    /// Holes become hole sketches.
    /// </summary>
    /// <remarks>
    /// Built on <see cref="FromCurves"/>, so closure, winding and degeneracy are validated
    /// in exactly one place — the same "smallest bridge that works" rule as
    /// <see cref="ToCurves"/>.
    /// </remarks>
    public static Sketch FromCurvedRegion(CurvedRegion2d region)
    {
        ArgumentNullException.ThrowIfNull(region);
        var sketch = FromCurves([.. region.Outer.Select(edge => Curve2d.FromCurvedEdge(edge))]);
        foreach (var hole in region.Holes)
            sketch = sketch.WithHole(FromCurves([.. hole.Select(edge => Curve2d.FromCurvedEdge(edge))]));
        return sketch;
    }

    /// <summary>Everything covered by this sketch or <paramref name="other"/>, with arcs
    /// KEPT — see <see cref="ToCurvedRegions(double)"/> for the exact fidelity contract, and
    /// <see cref="FromCurvedRegion"/> for the way back to a sketch.</summary>
    public IReadOnlyList<CurvedRegion2d> UnionExact(Sketch other, double chordTolerance = DefaultChordTolerance) =>
        CurvedRegion2dBoolean.Union(ToCurvedRegions(chordTolerance), Requires(other).ToCurvedRegions(chordTolerance));

    /// <summary>Everything covered by both sketches, with arcs kept.</summary>
    public IReadOnlyList<CurvedRegion2d> IntersectExact(Sketch other, double chordTolerance = DefaultChordTolerance) =>
        CurvedRegion2dBoolean.Intersection(ToCurvedRegions(chordTolerance), Requires(other).ToCurvedRegions(chordTolerance));

    /// <summary>This sketch with <paramref name="other"/> cut away, with arcs kept.</summary>
    public IReadOnlyList<CurvedRegion2d> SubtractExact(Sketch other, double chordTolerance = DefaultChordTolerance) =>
        CurvedRegion2dBoolean.Difference(ToCurvedRegions(chordTolerance), Requires(other).ToCurvedRegions(chordTolerance));

    /// <summary>
    /// This sketch grown or shrunk by a constant distance, with arcs kept — and with
    /// <see cref="OffsetJoin.Round"/> corners as EXACT arcs rather than the inscribed
    /// polygonal fans <see cref="Offset"/> produces.
    /// </summary>
    public IReadOnlyList<CurvedRegion2d> OffsetExact(
        double delta, OffsetJoin join = OffsetJoin.Round,
        double miterLimit = Region2dOffset.DefaultMiterLimit,
        double chordTolerance = DefaultChordTolerance) =>
        CurvedRegion2dOffset.Offset(ToCurvedRegions(chordTolerance), delta, join, miterLimit);

    private static Sketch Requires(Sketch other) =>
        other ?? throw new ArgumentNullException(nameof(other));

    private static void CollectCurvedLoops(
        Sketch sketch, double chordTolerance, List<IReadOnlyList<CurvedEdge2d>> into)
    {
        if (!(chordTolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(chordTolerance), "Chord tolerance must be positive.");
        into.Add(CurvedLoop(sketch.Segments, chordTolerance));
        foreach (var hole in sketch.Holes)
            into.Add(CurvedLoop(hole.Segments, chordTolerance));
    }

    /// <summary>One sketch loop as arrangement edges: lines and arcs verbatim, anything else
    /// (a Bézier) flattened to inscribed chords at <paramref name="chordTolerance"/>.</summary>
    private static IReadOnlyList<CurvedEdge2d> CurvedLoop(
        IReadOnlyList<SketchSegment> segments, double chordTolerance)
    {
        var edges = new List<CurvedEdge2d>(segments.Count);
        var scratch = new List<Vector2d>();
        foreach (var segment in segments)
        {
            if (segment.ToCurve2d().TryToCurvedEdge(out var edge))
            {
                edges.Add(edge);
                continue;
            }
            scratch.Clear();
            segment.Flatten(chordTolerance, scratch);   // start inclusive, end EXCLUSIVE
            scratch.Add(segment.End);
            for (int k = 0; k + 1 < scratch.Count; k++)
                edges.Add(CurvedEdge2d.Line(scratch[k], scratch[k + 1]));
        }
        return edges;
    }

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

    /// <summary>
    /// Elliptical arc from the current point to <paramref name="end"/>, on an ellipse with
    /// the given semi-axis lengths and rotation — SVG's <c>A rx ry rot largeArc sweep</c>
    /// command, with the same two flags and the same meaning, because that is the only
    /// widely-shared spelling of this curve and matching it means a path can cross either
    /// way with nothing re-derived.
    /// </summary>
    /// <remarks>
    /// <para><b>The semi-axes are scaled up when they cannot reach</b>, which is SVG's own
    /// out-of-range rule (F.6.6) and is what stops a caller having to solve for the
    /// minimum ellipse before drawing: both are multiplied by the common factor that makes
    /// the arc exactly reach, so the ellipse's ASPECT and rotation are preserved and the
    /// result is the unique arc of that shape through both points.</para>
    /// <para>Exact in all three representations, and note what that costs nowhere else:
    /// the arc lands on the sketch as an <c>EllipseSeg</c> carrying the centre and both
    /// semi-axis VECTORS, so a rotated ellipse needs no third parameter downstream.</para>
    /// </remarks>
    /// <param name="semiX">Semi-axis before rotation, along the ellipse's own x.</param>
    /// <param name="semiY">Semi-axis before rotation, along the ellipse's own y.</param>
    /// <param name="rotationDegrees">Rotation of the ellipse's axes from the sketch's.</param>
    /// <param name="largeArc">Take the arc of more than half a turn.</param>
    /// <param name="clockwise">Traverse clockwise (SVG's sweep flag = 0).</param>
    public SketchBuilder EllipticalArcTo(
        Vector2d end, double semiX, double semiY, double rotationDegrees = 0,
        bool largeArc = false, bool clockwise = false)
    {
        if (!(semiX > 0) || !(semiY > 0))
            throw new ArgumentOutOfRangeException(nameof(semiX), "Ellipse semi-axes must be positive.");
        var start = _current;
        // Degenerate-chord guard at the weld tier, matching Close(): a zero-length arc has
        // no defined sweep, and the joint would not be a distinct vertex downstream.
        if (start.DistanceTo(end) <= 1e-9)
            throw new ArgumentException("An elliptical arc needs distinct endpoints.", nameof(end));

        double radians = rotationDegrees * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);

        // Work in the ellipse's own frame, then scale y so the ellipse becomes the UNIT
        // CIRCLE: the arc problem is then the circular one, and the answer maps back
        // exactly because the map is linear (it takes centres to centres and the arc's
        // parameter across verbatim).
        Vector2d ToUnit(in Vector2d p)
        {
            var local = new Vector2d(p.X * cos + p.Y * sin, -p.X * sin + p.Y * cos);
            return new Vector2d(local.X / semiX, local.Y / semiY);
        }

        var u0 = ToUnit(start);
        var u1 = ToUnit(end);
        double half = (u1 - u0).Length / 2;
        if (half > 1)
        {
            // SVG F.6.6: scale both semi-axes by the common factor that just reaches, so
            // the aspect and rotation survive and the arc is the unique one of that shape.
            semiX *= half;
            semiY *= half;
            u0 = ToUnit(start);
            u1 = ToUnit(end);
            half = 1;   // by construction, up to rounding
        }

        var chordMid = (u0 + u1) / 2;
        var chord = u1 - u0;
        // Exact-zero guard: distinct endpoints were checked above, and the unit map is
        // invertible, so the chord cannot vanish here.
        var left = new Vector2d(-chord.Y, chord.X).Normalized();
        double offset = Math.Sqrt(Math.Max(0, 1 - half * half));
        // Same rule as ArcTo: a counter-clockwise small arc curves left of the chord, and
        // each of the two flags flips the side.
        var unitCenter = chordMid + left * ((clockwise ^ largeArc) ? -offset : offset);

        double startAngle = Math.Atan2(u0.Y - unitCenter.Y, u0.X - unitCenter.X);
        double endAngle = Math.Atan2(u1.Y - unitCenter.Y, u1.X - unitCenter.X);
        double sweep = endAngle - startAngle;
        // Angles are dimensionless, so 1e-12 rad is legitimately absolute (the ArcTo rule).
        if (!clockwise)
            sweep = sweep <= 1e-12 ? sweep + 2 * Math.PI : sweep;
        else
            sweep = sweep >= -1e-12 ? sweep - 2 * Math.PI : sweep;

        // Map the unit-circle answer back: the centre through the inverse of ToUnit, and
        // the axes as the images of the unit circle's own.
        var center = new Vector2d(
            unitCenter.X * semiX * cos - unitCenter.Y * semiY * sin,
            unitCenter.X * semiX * sin + unitCenter.Y * semiY * cos);
        var a = new Vector2d(cos, sin) * semiX;
        var b = new Vector2d(-sin, cos) * semiY;

        _segments.Add(new EllipseSeg(center, a, b, startAngle, sweep));
        _current = end;
        return this;
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
