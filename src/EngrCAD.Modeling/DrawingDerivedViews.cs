using EngrCAD.Core;

namespace EngrCAD.Modeling;

// Views DERIVED from other views: a section (cut somewhere, and the cut marked on the
// view it was taken from), a detail (a circled region blown up), and a broken view (a
// long part with its middle elided).
//
// The three share one structural idea and it is the reason they landed together: a
// derived view is a CLIP (and, for a break, a monotone REMAP) on top of a projection
// that already exists, plus a MARK drawn on the view it came from. The clip is why a
// detail view needs no second projection — it shares its parent's, so it cannot show
// different geometry — and the mark is why the sheet needs a view-to-view reference at
// all: the marker is drawn in the PARENT's coordinates from the CHILD's own facts.

/// <summary>What kind of derivation a <see cref="ViewOrigin"/> records.</summary>
public enum ViewOriginKind
{
    /// <summary>The child is a section of the parent; the parent gets a cutting line.</summary>
    Section,

    /// <summary>The child is a scaled-up detail of a region; the parent gets a circle.</summary>
    Detail,
}

/// <summary>
/// A view's reference to the view it was derived from — the one piece of structure the
/// sheet model did not have, and what lets a mark appear on the PARENT that states a fact
/// about the CHILD.
/// </summary>
/// <param name="Parent">The view the derivation was taken from.</param>
/// <param name="Letter">The letter labelling the pair on both views ("A", giving
/// "SECTION A-A" and a cutting line marked A at both ends).</param>
/// <param name="Kind">Which derivation this is.</param>
public sealed record ViewOrigin(DrawingView Parent, string Letter, ViewOriginKind Kind);

/// <summary>
/// A detail view's clip: the disc of the parent's projection that the detail shows, in the
/// parent's own projected MODEL coordinates (so the circle means the same thing whatever
/// scale either view is drawn at).
/// </summary>
/// <param name="Centre">Disc centre, view-local model coordinates.</param>
/// <param name="Radius">Disc radius, model units.</param>
public sealed record ViewDetail(Vector2d Centre, double Radius)
{
    /// <summary>Number of chords the boundary circle is drawn with.</summary>
    public const int CircleSegments = 64;

    /// <summary>
    /// The runs clipped to the disc: every piece of every polyline whose points lie inside
    /// it, cut exactly at the circle. A run entirely outside disappears; a run crossing the
    /// boundary is split there, so the detail shows the parent's line work and nothing else.
    /// </summary>
    public IReadOnlyList<HiddenLineRun> Clip(IReadOnlyList<HiddenLineRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var clipped = new List<HiddenLineRun>(runs.Count);
        foreach (var run in runs)
        {
            foreach (var piece in ClipPolyline(run.Points))
                clipped.Add(run with { Points = piece });
        }
        return clipped;
    }

    /// <summary>True when the point is inside (or on) the disc.</summary>
    public bool Contains(in Vector2d p) => (p - Centre).LengthSquared <= Radius * Radius;

    private IEnumerable<IReadOnlyList<Vector2d>> ClipPolyline(IReadOnlyList<Vector2d> points)
    {
        var current = new List<Vector2d>();
        for (int i = 0; i + 1 < points.Count; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            bool inA = Contains(a), inB = Contains(b);
            if (inA && inB)
            {
                if (current.Count == 0)
                    current.Add(a);
                current.Add(b);
                continue;
            }
            // A segment meets the circle where |a + t(b-a) - c| = r: one quadratic, so the
            // crossing is exact rather than searched (a straight chord against a circle is
            // one of the closed forms this kernel already insists on elsewhere).
            var d = b - a;
            var f = a - Centre;
            double qa = d.LengthSquared;
            if (qa <= 0)
                continue;
            double qb = 2 * f.Dot(d);
            double qc = f.LengthSquared - Radius * Radius;
            double disc = qb * qb - 4 * qa * qc;
            if (disc < 0)
            {
                if (current.Count >= 2)
                    yield return current;
                current = [];
                continue;
            }
            double root = Math.Sqrt(disc);
            double t0 = (-qb - root) / (2 * qa);
            double t1 = (-qb + root) / (2 * qa);
            double enter = Math.Max(0, Math.Min(t0, t1));
            double exit = Math.Min(1, Math.Max(t0, t1));
            if (exit <= enter)
            {
                if (current.Count >= 2)
                    yield return current;
                current = [];
                continue;
            }
            var p0 = a + d * enter;
            var p1 = a + d * exit;
            if (current.Count == 0)
                current.Add(p0);
            current.Add(p1);
            if (exit < 1 || !inB)
            {
                if (current.Count >= 2)
                    yield return current;
                current = [];
            }
        }
        if (current.Count >= 2)
            yield return current;
    }
}

/// <summary>Which of the view's own axes a <see cref="ViewBreak"/> removes a band along.</summary>
public enum BreakAxis
{
    /// <summary>Remove a band of X — the usual break for a part that is long left to right.</summary>
    Horizontal,

    /// <summary>Remove a band of Y.</summary>
    Vertical,
}

/// <summary>
/// A broken view: a band of the view's own coordinates is removed and the far side slid in,
/// so a long part fits the paper at a scale that shows its detail.
///
/// <para><b>The dimensions stay TRUE, and that is the substance of the feature.</b> A
/// <see cref="SheetAnnotation"/> reads its VALUE from its anchors in the view's model
/// coordinates and draws its anatomy through the view's map onto the sheet — so a dimension
/// spanning the break measures the part's real length while its arrows land on the drawn,
/// shortened view. Nothing has to remember to state the true value: the value never went
/// through the break at all.</para>
///
/// <para><b>The gap is in MODEL units, deliberately.</b> A paper gap would make the view's
/// size a function of the drawing scale and the scale a function of the view's size, which
/// is circular; stating how much of the part's own length is left standing in place of what
/// was removed has no such loop and is what the map is written in.</para>
/// </summary>
/// <param name="Axis">Which coordinate the band is removed from.</param>
/// <param name="From">Lower edge of the removed band, view-local model coordinates.</param>
/// <param name="To">Upper edge of the removed band.</param>
/// <param name="Gap">What the band is replaced by, in MODEL units.</param>
/// <param name="Teeth">Zig-zag teeth in each break line.</param>
public sealed record ViewBreak(BreakAxis Axis, double From, double To, double Gap, int Teeth = 6)
{
    /// <summary>Builds a break, refusing an inverted band or a non-positive gap by name.</summary>
    public static ViewBreak Between(BreakAxis axis, double from, double to, double gap, int teeth = 6)
    {
        if (!(to > from))
            throw new ArgumentOutOfRangeException(
                nameof(to), $"A break removes the band from {from} to {to}, which is not a band.");
        if (!(gap > 0))
            throw new ArgumentOutOfRangeException(
                nameof(gap), "A break's gap must be positive, or the two halves would touch.");
        if (teeth < 1)
            throw new ArgumentOutOfRangeException(nameof(teeth), "A break line needs at least one tooth.");
        return new ViewBreak(axis, from, to, gap, teeth);
    }

    /// <summary>How much shorter the view becomes.</summary>
    public double Removed => To - From - Gap;

    /// <summary>
    /// The break's map on the view's own coordinates: identity below the band, a shift of
    /// <c>-(To - From) + Gap</c> above it, and a continuous compression of the band itself
    /// onto the gap. Monotone and continuous, so it can be applied to anything the view
    /// draws — line work, cut boundaries and annotation anchors alike — and an anchor
    /// inside the removed band lands visibly in the gap rather than silently somewhere else.
    /// </summary>
    public Vector2d Map(in Vector2d p)
    {
        double c = Axis == BreakAxis.Horizontal ? p.X : p.Y;
        double mapped;
        if (c <= From)
            mapped = c;
        else if (c >= To)
            mapped = c - (To - From) + Gap;
        else
            mapped = From + (c - From) * Gap / (To - From);
        return Axis == BreakAxis.Horizontal ? new Vector2d(mapped, p.Y) : new Vector2d(p.X, mapped);
    }

    /// <summary>The runs with the band's interior removed and the rest mapped — the clip and
    /// the map in one pass, since a piece is cut exactly where the band starts.</summary>
    public IReadOnlyList<HiddenLineRun> Apply(IReadOnlyList<HiddenLineRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var result = new List<HiddenLineRun>(runs.Count);
        foreach (var run in runs)
        {
            foreach (var piece in ClipPolyline(run.Points))
            {
                var mapped = new Vector2d[piece.Count];
                for (int i = 0; i < piece.Count; i++)
                    mapped[i] = Map(piece[i]);
                result.Add(run with { Points = mapped });
            }
        }
        return result;
    }

    /// <summary>The two break lines, as polylines in the view's MAPPED coordinates: a
    /// zig-zag across the view at each cut edge.</summary>
    public IReadOnlyList<IReadOnlyList<Vector2d>> BreakLines(in Aabb mappedBounds)
    {
        double lo = Axis == BreakAxis.Horizontal ? mappedBounds.Min.Y : mappedBounds.Min.X;
        double hi = Axis == BreakAxis.Horizontal ? mappedBounds.Max.Y : mappedBounds.Max.X;
        double over = (hi - lo) * 0.05;
        lo -= over;
        hi += over;
        double amplitude = Gap * 0.35;
        var lines = new List<IReadOnlyList<Vector2d>>(2);
        foreach (double at in new[] { From, From + Gap })
        {
            var points = new List<Vector2d>(Teeth * 2 + 1);
            for (int i = 0; i <= Teeth * 2; i++)
            {
                double t = i / (double)(Teeth * 2);
                double across = lo + (hi - lo) * t;
                // Ends on the line, interior alternating either side of it: a long-break
                // zig-zag whose two ends meet the neighbouring line work squarely.
                double along = at + (i == 0 || i == Teeth * 2 ? 0 : (i % 2 == 1 ? amplitude : -amplitude));
                points.Add(Axis == BreakAxis.Horizontal
                    ? new Vector2d(along, across)
                    : new Vector2d(across, along));
            }
            lines.Add(points);
        }
        return lines;
    }

    /// <summary>
    /// Splits a polyline at the band's two edges and keeps the pieces outside it. The
    /// coordinate is AFFINE along a segment, so each edge contributes at most one crossing
    /// parameter and the sub-intervals between the sorted crossings are each wholly inside
    /// or wholly outside — decided at the sub-interval's own midpoint, which needs no
    /// tolerance because a midpoint is never on an edge it did not cross.
    /// </summary>
    private IEnumerable<IReadOnlyList<Vector2d>> ClipPolyline(IReadOnlyList<Vector2d> points)
    {
        var current = new List<Vector2d>();
        var ts = new List<double>(4);
        for (int i = 0; i + 1 < points.Count; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            double ca = Coordinate(a), cb = Coordinate(b);
            ts.Clear();
            ts.Add(0);
            ts.Add(1);
            if (ca != cb)
            {
                foreach (double edge in new[] { From, To })
                {
                    double t = (edge - ca) / (cb - ca);
                    if (t > 0 && t < 1)
                        ts.Add(t);
                }
            }
            ts.Sort();
            for (int k = 0; k + 1 < ts.Count; k++)
            {
                double t0 = ts[k], t1 = ts[k + 1];
                if (!(t1 > t0))
                    continue;
                double mid = ca + (cb - ca) * (t0 + t1) / 2;
                if (mid <= From || mid >= To)
                {
                    if (current.Count == 0)
                        current.Add(a + (b - a) * t0);
                    current.Add(a + (b - a) * t1);
                }
                else if (current.Count >= 2)
                {
                    yield return current;
                    current = [];
                }
                else
                {
                    current = [];
                }
            }
        }
        if (current.Count >= 2)
            yield return current;
    }

    private double Coordinate(in Vector2d p) => Axis == BreakAxis.Horizontal ? p.X : p.Y;
}

/// <summary>
/// A mark one view draws on ANOTHER view — a section's cutting line, a detail's circle.
/// Deliberately not a <see cref="SheetAnnotation"/>: an annotation is a dimension or a note
/// the caller placed, all of it on the dimensions layer, whereas a marker is a drawing
/// CONVENTION with its own layers (a cutting line is chain-dashed on the section layer, a
/// detail circle continuous on the symbol layer) and is derived from another view rather
/// than stated.
/// </summary>
public abstract class ViewMarker
{
    /// <summary>Emits the marker into the parent view's symbol geometry.</summary>
    /// <param name="toSheet">Maps a parent-view model point onto the sheet.</param>
    /// <param name="style">Paper-millimetre sizes.</param>
    /// <param name="bounds">The parent view's own content bounds, model coordinates.</param>
    /// <param name="symbols">Layered line work is appended here.</param>
    /// <param name="texts">Text is appended here.</param>
    internal abstract void Build(
        Func<Vector2d, Vector2d> toSheet, SheetStyle style, in Aabb bounds,
        List<(Vector2d A, Vector2d B, string Layer)> symbols, List<SheetText> texts);

    /// <summary>An arrowhead with its TIP at <paramref name="tip"/> pointing along
    /// <paramref name="direction"/> — the same V the dimension anatomy uses, on a layered
    /// segment list.</summary>
    private protected static void Arrowhead(
        List<(Vector2d A, Vector2d B, string Layer)> symbols,
        in Vector2d tip, in Vector2d direction, SheetStyle style, string layer)
    {
        var back = tip - direction * style.ArrowLength;
        var wing = new Vector2d(-direction.Y, direction.X) * style.ArrowHalfWidth;
        symbols.Add((tip, back + wing, layer));
        symbols.Add((tip, back - wing, layer));
    }
}

/// <summary>
/// The cutting line a section view draws on the view it was taken from: a chain-dashed line
/// where the plane cuts, an arrow at each end pointing the way the section looks, and the
/// section's letter beyond each arrow.
///
/// <para><b>The plane appears as a LINE exactly when the section's direction is
/// perpendicular to the parent's</b>, which is a statement about orthographic projection
/// rather than a restriction chosen here: projecting a plane along a direction that is not
/// in it covers the whole sheet, so there would be no line to draw. That is why a section
/// is marked on a view square to it and refused by name on any other.</para>
/// </summary>
public sealed class SectionCuttingLine : ViewMarker
{
    private readonly Vector2d _normal;      // unit, in the parent's model coordinates
    private readonly double _offset;        // the line is normal . p == offset
    private readonly Vector2d _sight;       // the direction the section looks, parent 2D
    private readonly string _letter;

    internal SectionCuttingLine(in Vector2d normal, double offset, in Vector2d sight, string letter)
    {
        _normal = normal;
        _offset = offset;
        _sight = sight;
        _letter = letter;
    }

    /// <summary>
    /// Builds the marker for <paramref name="section"/> on <paramref name="parent"/>, or
    /// refuses by name when the section's plane does not project to a line there.
    /// </summary>
    public static SectionCuttingLine For(DrawingView parent, DrawingView section, string letter)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(section);
        if (section.SectionThrough is not { } through)
            throw new InvalidOperationException(
                $"'{section.Label}' has no cutting plane, so nothing can be marked on '{parent.Label}'.");

        var n = section.Direction;
        double alongParent = n.Dot(parent.Direction);
        if (Math.Abs(alongParent) > 1e-9)
            throw new InvalidOperationException(
                $"A section's cutting plane shows as a LINE on a view square to it, and "
                + $"'{section.Label}' is {Math.Acos(Math.Clamp(Math.Abs(alongParent), -1, 1)) * 180 / Math.PI:F1} "
                + $"degrees from square to '{parent.Label}' (their directions' dot product is {alongParent:0.###}). "
                + "Mark the section on a view perpendicular to it, or take the section along the "
                + "oblique normal instead.");

        var frame = parent.Frame;
        var normal = new Vector2d(n.Dot(frame.X), n.Dot(frame.Y));
        if (!normal.TryNormalize(Tolerance.Default, out var unit))
            throw new InvalidOperationException(
                $"'{section.Label}' cuts along '{parent.Label}'s own view direction, so its plane "
                + "covers that view rather than crossing it.");
        double offset = n.Dot(through - frame.Origin);
        // A section keeps what is FURTHER from its own viewer, so the arrows point along
        // the direction of sight — the negative of the frame's toward-the-eye axis.
        return new SectionCuttingLine(unit, offset, -unit, letter);
    }

    internal override void Build(
        Func<Vector2d, Vector2d> toSheet, SheetStyle style, in Aabb bounds,
        List<(Vector2d A, Vector2d B, string Layer)> symbols, List<SheetText> texts)
    {
        if (bounds.IsEmpty)
            return;
        var along = new Vector2d(-_normal.Y, _normal.X);
        // A point on the line, then the line clipped to the view's own extent: the model
        // rectangle is convex, so the two crossings are the min and max of the parameter
        // over its four edges.
        var basePoint = _normal * _offset;
        if (!ClipToBox(basePoint, along, bounds, out double t0, out double t1))
            return;

        var a = toSheet(basePoint + along * t0);
        var b = toSheet(basePoint + along * t1);
        if (!(b - a).TryNormalize(Tolerance.Default, out var direction))
            return;
        double over = style.TextHeight * 2.5;
        a -= direction * over;
        b += direction * over;
        symbols.Add((a, b, SheetLayers.Section));

        // The sight direction on the sheet: the placement is a uniform scale, so a model
        // direction maps to the same sheet direction and needs no re-derivation.
        var sightSheet = toSheet(basePoint + _sight) - toSheet(basePoint);
        if (!sightSheet.TryNormalize(Tolerance.Default, out var sight))
            return;

        double shaft = style.TextHeight * 2;
        foreach (var end in new[] { a, b })
        {
            var tip = end + sight * shaft;
            symbols.Add((end, tip, SheetLayers.Section));
            Arrowhead(symbols, tip, sight, style, SheetLayers.Section);
            texts.Add(new SheetText(
                end - sight * (style.TextGap + style.TextHeight),
                _letter, style.TextHeight, SheetTextAnchor.Center, SheetLayers.Section));
        }
    }

    /// <summary>The parameter range over which <c>p + t*d</c> stays inside the box.</summary>
    private static bool ClipToBox(in Vector2d p, in Vector2d d, in Aabb box, out double t0, out double t1)
    {
        t0 = double.NegativeInfinity;
        t1 = double.PositiveInfinity;
        Span<double> lo = [box.Min.X, box.Min.Y];
        Span<double> hi = [box.Max.X, box.Max.Y];
        Span<double> origin = [p.X, p.Y];
        Span<double> direction = [d.X, d.Y];
        for (int axis = 0; axis < 2; axis++)
        {
            if (Math.Abs(direction[axis]) < 1e-12)
            {
                if (origin[axis] < lo[axis] || origin[axis] > hi[axis])
                    return false;
                continue;
            }
            double a = (lo[axis] - origin[axis]) / direction[axis];
            double b = (hi[axis] - origin[axis]) / direction[axis];
            t0 = Math.Max(t0, Math.Min(a, b));
            t1 = Math.Min(t1, Math.Max(a, b));
        }
        return t1 > t0 && double.IsFinite(t0) && double.IsFinite(t1);
    }
}

/// <summary>
/// The circle a detail view draws on the view it was taken from, with the detail's letter
/// beside it — the other half of the view-to-view reference.
/// </summary>
public sealed class DetailCircle : ViewMarker
{
    private readonly ViewDetail _detail;
    private readonly string _letter;

    internal DetailCircle(ViewDetail detail, string letter)
    {
        _detail = detail;
        _letter = letter;
    }

    /// <summary>Builds the marker for <paramref name="detail"/>, or refuses by name when the
    /// view carries no detail clip.</summary>
    public static DetailCircle For(DrawingView detail, string letter)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (detail.Detail is not { } clip)
            throw new InvalidOperationException(
                $"'{detail.Label}' is not a detail view (it carries no detail clip), so there is "
                + "no region to circle on its parent.");
        return new DetailCircle(clip, letter);
    }

    internal override void Build(
        Func<Vector2d, Vector2d> toSheet, SheetStyle style, in Aabb bounds,
        List<(Vector2d A, Vector2d B, string Layer)> symbols, List<SheetText> texts)
    {
        var previous = toSheet(_detail.Centre + new Vector2d(_detail.Radius, 0));
        var first = previous;
        for (int i = 1; i <= ViewDetail.CircleSegments; i++)
        {
            double angle = 2 * Math.PI * i / ViewDetail.CircleSegments;
            var point = toSheet(
                _detail.Centre + new Vector2d(Math.Cos(angle), Math.Sin(angle)) * _detail.Radius);
            symbols.Add((previous, point, SheetLayers.Symbol));
            previous = point;
        }
        // A short leader out of the circle at 45 degrees, with the letter at its end.
        var centre = toSheet(_detail.Centre);
        double sheetRadius = (first - centre).Length;
        var unit = new Vector2d(1, 1).Normalized(Tolerance.Default);
        var on = centre + unit * sheetRadius;
        var tail = on + unit * (style.TextHeight * 1.5);
        symbols.Add((on, tail, SheetLayers.Symbol));
        texts.Add(new SheetText(
            tail + new Vector2d(style.TextGap, style.TextGap),
            _letter, style.TextHeight, SheetTextAnchor.Left, SheetLayers.Symbol));
    }
}
