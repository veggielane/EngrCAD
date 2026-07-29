using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// Where one glyph goes: a rigid placement of the glyph's own (x along the writing
/// direction, y up) frame into sketch coordinates.
/// <para><b>Only the CONTROL POINTS are mapped, and that is exact.</b> A glyph outline is
/// lines and Béziers, and a Bézier is an affine combination of its control points at
/// every parameter — so the curve through mapped control points IS the mapped curve, with
/// nothing sampled or refitted. (The same property makes
/// <c>TransformedCurve(NurbsCurve)</c> a lossless STEP export.) It is also why text on a
/// path <em>rotates</em> each glyph rather than bending it: a warp that followed the
/// curve's curvature is not affine, so no exact Bézier image of a glyph exists under
/// it.</para>
/// <para>A translation-only pose evaluates the ORIGINAL expression
/// <c>origin + point * scale</c> rather than a rotation that happens to be the identity,
/// so every straight run of text is bit-for-bit what it was before poses existed.</para>
/// </summary>
internal readonly struct GlyphPose
{
    private readonly Vector2d _origin;
    private readonly Vector2d _xAxis;
    private readonly bool _rotated;

    private GlyphPose(in Vector2d origin, in Vector2d xAxis, bool rotated)
    {
        _origin = origin;
        _xAxis = xAxis;
        _rotated = rotated;
    }

    /// <summary>Upright placement at <paramref name="origin"/> (x right, y up).</summary>
    public static GlyphPose At(in Vector2d origin) => new(origin, Vector2d.UnitX, rotated: false);

    /// <summary>Placement with the glyph's x axis along <paramref name="xAxis"/> (which
    /// must be a unit vector) and its y axis the left perpendicular — so the map is a
    /// proper rotation and a glyph can never come out mirrored.</summary>
    public static GlyphPose Along(in Vector2d origin, in Vector2d xAxis) => new(origin, xAxis, rotated: true);

    /// <summary>Maps a point already expressed in sketch units relative to the glyph
    /// origin.</summary>
    public Vector2d Map(in Vector2d local) => _rotated
        ? _origin + _xAxis * local.X + new Vector2d(-_xAxis.Y, _xAxis.X) * local.Y
        : _origin + local;
}

/// <summary>
/// Glyph outlines to closed <see cref="Sketch"/>es. Two things happen here, both of
/// them the reason modeled text is exact rather than a polygon soup:
/// <list type="number">
/// <item><description><b>Contours become sketch segments exactly.</b> TrueType outlines
/// are straight lines and <em>quadratic</em> Béziers, and <c>SketchBuilder</c> has
/// <c>LineTo</c>/<c>QuadraticTo</c> — so a glyph maps onto the sketch vocabulary with
/// no flattening. Text then inherits the whole pipeline: exact NURBS profiles in
/// B-Rep, the exact 2D signed distance in implicit, crisp tessellation in mesh. The
/// one subtlety is the format's compression of on-curve points: between two
/// consecutive off-curve points an on-curve point at their midpoint is
/// <em>implied</em> and must be reconstructed, and a contour with no on-curve point at
/// all starts at the midpoint of its last and first points.</description></item>
/// <item><description><b>Counters become holes.</b> The holes in O, A and 8 (and the two
/// in B) are separate contours. TrueType's convention is that outlines are clockwise
/// and counters counter-clockwise under the nonzero winding rule, but real fonts
/// violate it often enough that orientation is not trustworthy — so contours are
/// classified by <em>containment</em> instead: nesting depth from point-in-region
/// tests, even depth = outline, odd depth = counter, attached to its immediate parent
/// with <c>Sketch.WithHole</c>. An island inside a counter (depth 2) becomes its own
/// top-level sketch, which is also what <c>WithHole</c> requires.</description></item>
/// </list>
/// </summary>
internal static class GlyphOutlines
{
    /// <summary>Absolute weld tolerance (1e-9 = <c>Tolerance.Default.Linear</c>): the
    /// same guard <see cref="Sketch"/> applies to its own chain closure, so a segment
    /// this converter keeps is one the sketch will accept.</summary>
    private const double Weld = 1e-9;

    /// <summary>
    /// The glyph as closed sketches with counters attached as holes, scaled from font
    /// units by <paramref name="scale"/> and placed with the glyph origin (on the
    /// baseline, at the pen position) by <paramref name="pose"/>.
    /// Blank glyphs yield an empty list — never an exception.
    /// </summary>
    public static List<Sketch> ToSketches(Glyph glyph, double scale, in GlyphPose pose)
    {
        if (glyph.IsEmpty)
            return [];

        var contours = new List<Contour>(glyph.Contours.Count);
        foreach (var source in glyph.Contours)
        {
            var contour = BuildContour(source, scale, pose);
            if (contour is not null)
                contours.Add(contour);
        }
        return contours.Count switch
        {
            0 => [],
            1 => [contours[0].Sketch],
            _ => Nest(contours),
        };
    }

    // ---- one contour -> one sketch ------------------------------------------

    /// <summary>A built contour plus the points a containment test votes with.</summary>
    private sealed record Contour(Sketch Sketch, Vector2d[] Samples);

    private static Contour? BuildContour(GlyphContour source, double scale, in GlyphPose pose)
    {
        var points = Compact(source.Points, scale, pose, source.IsCubic);
        if (points.Count < 2)
            return null;
        if (source.IsCubic)
            return BuildCubicContour(points);

        // Start at the first on-curve point. A contour made entirely of off-curve
        // points (a smooth oval) has none, and then the format's rule is to start at
        // the midpoint of the last and first control points.
        int first = points.FindIndex(p => p.OnCurve);
        Vector2d start = first >= 0
            ? points[first].Position
            : Vector2d.Lerp(points[^1].Position, points[0].Position, 0.5);
        int begin = first >= 0 ? first + 1 : 0;
        int count = first >= 0 ? points.Count - 1 : points.Count;

        var builder = Sketch.Start(start.X, start.Y);
        var vertices = new List<Vector2d> { start };
        var current = start;
        int segments = 0;
        Vector2d? pending = null;                    // an off-curve control awaiting its end point

        for (int k = 0; k < count; k++)
        {
            var point = points[(begin + k) % points.Count];
            if (point.OnCurve)
            {
                if (pending is { } control)
                    Quadratic(control, point.Position);
                else
                    Line(point.Position);
                pending = null;
            }
            else
            {
                // Two off-curve points in a row: the on-curve point between them is
                // implied at their midpoint.
                if (pending is { } control)
                    Quadratic(control, Vector2d.Lerp(control, point.Position, 0.5));
                pending = point.Position;
            }
        }
        if (pending is { } last)
            Quadratic(last, start);

        if (segments < 2)
            return null;                             // a spike or a single edge: encloses nothing

        Sketch sketch;
        try
        {
            sketch = builder.Close();
        }
        catch (ArgumentException)
        {
            // Real fonts ship zero-area artifact contours (retraced edges, collapsed
            // strokes). They contribute no material, so drop them rather than failing
            // the whole glyph; Sketch is the authority on what encloses area.
            return null;
        }

        var samples = new List<Vector2d>(vertices.Count * 2);
        for (int i = 0; i < vertices.Count; i++)
        {
            samples.Add(vertices[i]);
            samples.Add(Vector2d.Lerp(vertices[i], vertices[(i + 1) % vertices.Count], 0.5));
        }
        return new Contour(sketch, [.. samples]);

        void Line(in Vector2d to)
        {
            if (to.DistanceTo(current) <= Weld)
                return;                              // duplicate point: a zero-length Line3d downstream
            builder.LineTo(to);
            vertices.Add(to);
            current = to;
            segments++;
        }

        void Quadratic(in Vector2d control, in Vector2d to)
        {
            if (to.DistanceTo(current) <= Weld)
                return;                              // out-and-back spike: zero area, degenerate NURBS
            builder.QuadraticTo(control, to);
            vertices.Add(to);
            current = to;
            segments++;
        }
    }

    /// <summary>
    /// A CFF contour: on-curve anchors joined by lines, or by cubic Béziers whose two
    /// control points sit between them as off-curve entries. The contour starts on an
    /// anchor (charstrings always moveto first), and a trailing control pair curves
    /// back to the start — the cubic analogue of TrueType's wrap-around quadratic.
    /// </summary>
    private static Contour? BuildCubicContour(List<GlyphPoint> points)
    {
        if (!points[0].OnCurve)
            return null;                             // no anchor to start from: a malformed artifact contour

        var start = points[0].Position;
        var builder = Sketch.Start(start.X, start.Y);
        var vertices = new List<Vector2d> { start };
        var current = start;
        int segments = 0;
        Vector2d? control1 = null, control2 = null;

        for (int i = 1; i < points.Count; i++)
        {
            var point = points[i];
            if (point.OnCurve)
            {
                Emit(point.Position);
            }
            else if (control1 is null)
            {
                control1 = point.Position;
            }
            else if (control2 is null)
            {
                control2 = point.Position;
            }
            else
            {
                return null;                         // three controls in a row: not a cubic contour
            }
        }
        Emit(start);                                 // close back to the first anchor

        if (segments < 2)
            return null;

        Sketch sketch;
        try
        {
            sketch = builder.Close();
        }
        catch (ArgumentException)
        {
            return null;                             // zero-area artifact contour (see the quadratic path)
        }

        var samples = new List<Vector2d>(vertices.Count * 2);
        for (int i = 0; i < vertices.Count; i++)
        {
            samples.Add(vertices[i]);
            samples.Add(Vector2d.Lerp(vertices[i], vertices[(i + 1) % vertices.Count], 0.5));
        }
        return new Contour(sketch, [.. samples]);

        void Emit(in Vector2d to)
        {
            // Zero-chord segments are dropped — the same out-and-back-spike policy as
            // the quadratic path (zero area, degenerate NURBS downstream).
            if (to.DistanceTo(current) <= Weld)
            {
                control1 = control2 = null;
                return;
            }
            if (control1 is { } c1 && control2 is { } c2)
                builder.BezierTo(c1, c2, to);
            else
                builder.LineTo(to);                  // no controls — or a lone control, collapsed to its chord
            vertices.Add(to);
            current = to;
            segments++;
            control1 = control2 = null;
        }
    }

    /// <summary>Scales and places the font's points, dropping consecutive duplicates
    /// (which would become zero-length segments). For cubic (CFF) contours only
    /// coincident <em>anchors</em> are dropped: a cubic's two control points may
    /// legitimately coincide with each other or with an anchor.</summary>
    private static List<GlyphPoint> Compact(IReadOnlyList<GlyphPoint> points, double scale, in GlyphPose pose, bool isCubic = false)
    {
        var compacted = new List<GlyphPoint>(points.Count);
        foreach (var point in points)
        {
            var mapped = new GlyphPoint(pose.Map(point.Position * scale), point.OnCurve);
            if (compacted.Count > 0 &&
                compacted[^1].OnCurve == mapped.OnCurve &&
                (!isCubic || mapped.OnCurve) &&
                compacted[^1].Position.DistanceTo(mapped.Position) <= Weld)
                continue;
            compacted.Add(mapped);
        }
        // The contour wraps, so the last point may duplicate the first.
        if (compacted.Count > 1 &&
            compacted[0].OnCurve == compacted[^1].OnCurve &&
            (!isCubic || compacted[0].OnCurve) &&
            compacted[0].Position.DistanceTo(compacted[^1].Position) <= Weld)
            compacted.RemoveAt(compacted.Count - 1);
        return compacted;
    }

    // ---- nesting -------------------------------------------------------------

    private static List<Sketch> Nest(List<Contour> contours)
    {
        int n = contours.Count;
        var regions = new SketchRegion[n];
        var bounds = new Aabb[n];
        for (int i = 0; i < n; i++)
        {
            regions[i] = new SketchRegion(contours[i].Sketch);
            bounds[i] = contours[i].Sketch.Bounds;
        }

        var contains = new bool[n, n];               // contains[outer, inner]
        var depth = new int[n];
        for (int outer = 0; outer < n; outer++)
        {
            for (int inner = 0; inner < n; inner++)
            {
                if (outer == inner || !bounds[outer].Intersects(bounds[inner]))
                    continue;                        // disjoint bounds cannot nest
                if (!Contains(regions[outer], contours[inner]))
                    continue;
                contains[outer, inner] = true;
                depth[inner]++;
            }
        }

        // Immediate parent = the deepest contour that contains this one.
        var parent = new int[n];
        for (int inner = 0; inner < n; inner++)
        {
            parent[inner] = -1;
            for (int outer = 0; outer < n; outer++)
                if (contains[outer, inner] && (parent[inner] < 0 || depth[outer] > depth[parent[inner]]))
                    parent[inner] = outer;
        }

        // Even depth draws material, odd depth removes it. Mutually containing
        // contours (duplicated outlines) would leave nothing at even depth; fall back
        // to treating every contour as its own outline rather than losing the glyph.
        bool anyOuter = depth.Any(d => d % 2 == 0);
        if (!anyOuter)
            return [.. contours.Select(c => c.Sketch)];

        var result = new List<Sketch>(n);
        for (int i = 0; i < n; i++)
        {
            if (depth[i] % 2 != 0)
                continue;
            var sketch = contours[i].Sketch;
            for (int hole = 0; hole < n; hole++)
                if (depth[hole] % 2 != 0 && parent[hole] == i)
                    sketch = sketch.WithHole(contours[hole].Sketch);
            result.Add(sketch);
        }
        return result;
    }

    /// <summary>Majority point-in-region vote: robust when contours touch (a single
    /// sample sitting exactly on the boundary cannot decide the classification).</summary>
    private static bool Contains(SketchRegion outer, Contour inner)
    {
        int votes = 0;
        foreach (var sample in inner.Samples)
            if (outer.SignedDistance(sample) < 0)
                votes++;
        return votes * 2 > inner.Samples.Length;
    }
}
