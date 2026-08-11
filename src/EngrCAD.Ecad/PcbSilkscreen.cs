using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Ecad;

/// <summary>What a piece of silkscreen line-work is — so a caller (and the overlap check) can tell a
/// reference designator from a body outline.</summary>
public enum SilkKind
{
    /// <summary>A component reference designator (<c>R1</c>, <c>U3</c>).</summary>
    Reference,

    /// <summary>A component value (<c>330</c>, <c>10K</c>).</summary>
    Value,

    /// <summary>A component body / courtyard outline.</summary>
    Courtyard,

    /// <summary>A board-level mark (a text label placed by the designer).</summary>
    Mark,
}

/// <summary>One board-level silkscreen mark — a text label placed at a point on a side. The optional
/// <see cref="Height"/> overrides the layer's default text height.</summary>
/// <param name="Text">The label text.</param>
/// <param name="Position">The centre of the label in board-local 2D coordinates (mm).</param>
/// <param name="Side">Which side the mark is on.</param>
/// <param name="Height">The text height (mm), or null for the layer default.</param>
public readonly record struct SilkMark(
    string Text, Vector2d Position, CopperSide Side = CopperSide.Top, double? Height = null);

/// <summary>
/// How a <see cref="PcbSilkscreen"/> is derived: the reference / value text height, the pen
/// <see cref="LineWidth"/> the strokes are drawn with, the <see cref="CourtyardMargin"/> a body outline
/// is grown past the pads, which items to draw, and any board-level <see cref="Marks"/>. A value (equal
/// settings compare equal), so it rides in the layout file as LAYOUT TRUTH the same way a pour's does —
/// write-only-when-stated, so a layout that states none saves byte-identically.
/// </summary>
/// <param name="TextHeight">The reference / value text height (mm), positive.</param>
/// <param name="LineWidth">The silkscreen pen width (mm), positive.</param>
/// <param name="CourtyardMargin">How far a component body outline is grown past its pads (mm),
/// non-negative.</param>
/// <param name="ShowReferences">Whether to draw reference designators (the default; the primary silk
/// content).</param>
/// <param name="ShowValues">Whether to draw component values (off by default — silk real-estate is
/// tight, and a refdes is what a board is assembled from).</param>
/// <param name="ShowCourtyards">Whether to draw component body outlines.</param>
/// <param name="Marks">Board-level text marks (empty by default).</param>
public sealed record PcbSilkscreenSettings(
    double TextHeight = 1.0,
    double LineWidth = 0.15,
    double CourtyardMargin = 0.25,
    bool ShowReferences = true,
    bool ShowValues = false,
    bool ShowCourtyards = true,
    IReadOnlyList<SilkMark>? Marks = null)
{
    /// <summary>The default settings.</summary>
    public static PcbSilkscreenSettings Default { get; } = new();

    /// <summary>The board-level marks (never null).</summary>
    public IReadOnlyList<SilkMark> BoardMarks => Marks ?? [];

    internal void Validate()
    {
        if (!(TextHeight > 0) || !double.IsFinite(TextHeight))
            throw new ArgumentException($"A silkscreen text height must be positive (got {TextHeight:g6}).");
        if (!(LineWidth > 0) || !double.IsFinite(LineWidth))
            throw new ArgumentException($"A silkscreen line width must be positive (got {LineWidth:g6}).");
        if (!(CourtyardMargin >= 0) || !double.IsFinite(CourtyardMargin))
            throw new ArgumentException(
                $"A silkscreen courtyard margin cannot be negative (got {CourtyardMargin:g6}).");
    }
}

/// <summary>One stroke of silkscreen line-work — a polyline (a courtyard side, a glyph stroke) tagged
/// with what it is and which component / mark it belongs to.</summary>
/// <param name="Source">The component reference or mark this stroke draws (<c>"R1"</c>).</param>
/// <param name="Kind">Reference / value / courtyard / mark.</param>
/// <param name="Points">The polyline vertices in board-local 2D coordinates (mm).</param>
public readonly record struct SilkStroke(string Source, SilkKind Kind, IReadOnlyList<Vector2d> Points);

/// <summary>One side's silkscreen — the outer copper <see cref="Layer"/> it draws over, the
/// <see cref="Side"/>, the line-work <see cref="Strokes"/>, and the pen <see cref="LineWidth"/> they are
/// drawn with.</summary>
/// <param name="Layer">The outer copper layer name (<c>"Top"</c>, <c>"Bottom"</c>).</param>
/// <param name="Side">The board face.</param>
/// <param name="Strokes">The line-work, in placement then element order.</param>
/// <param name="LineWidth">The pen width (mm) the strokes are drawn with.</param>
public sealed record SilkLayerContent(
    string Layer, CopperSide Side, IReadOnlyList<SilkStroke> Strokes, double LineWidth);

/// <summary>A silkscreen stroke that overlaps exposed copper (a mask window) — a real defect (silk on a
/// solderable pad prints onto the land). Reported, not thrown, since the fix is a placement move the
/// caller makes.</summary>
/// <param name="SilkSource">The silk element (<c>"R1"</c>).</param>
/// <param name="Kind">What the silk element is.</param>
/// <param name="OpeningSource">The pad / via window it overlaps.</param>
/// <param name="OverlapArea">The overlapping area (mm²).</param>
public readonly record struct SilkOverCopper(
    string SilkSource, SilkKind Kind, string OpeningSource, double OverlapArea)
{
    /// <summary>A human-readable message.</summary>
    public override string ToString() =>
        $"silk {Kind} '{SilkSource}' overlaps exposed copper '{OpeningSource}' by {OverlapArea:g4} mm^2";
}

/// <summary>
/// The silkscreen model, derived from a placed <see cref="PcbLayout"/>: for each surface component a
/// body / courtyard OUTLINE (the pads' bounding box grown by the margin) and its reference designator
/// (and optionally its value) as stroke-font TEXT, plus any board-level marks. It exists on the two
/// OUTER copper layers only (top components → top silk, bottom → bottom silk), the same layers the
/// solder mask does.
///
/// <para><b>Text is line-work.</b> A Gerber has no text primitive, so each glyph is a set of polylines
/// (<see cref="SilkFont"/>) the Gerber draws with a round aperture (exactly as a trace draws) and the
/// reader strokes back — so silk round-trips through the same twin-decoder oracle the copper does.</para>
///
/// <para><b>Silk must not overlap exposed copper.</b> Silk printed onto a solderable pad is a real
/// assembly defect, so <see cref="OverExposedCopper"/> intersects the silk line-work (stroked to its
/// pen width) with the solder-mask windows and reports every overlap by name — a check the caller runs,
/// like the DRC, rather than a throw.</para>
/// </summary>
public sealed class PcbSilkscreen
{
    /// <summary>A small gap (in text-height units) between a courtyard edge and the text baseline.</summary>
    private const double TextGap = 0.25;

    private PcbSilkscreen(SilkLayerContent top, SilkLayerContent bottom, PcbSilkscreenSettings settings)
    {
        Top = top;
        Bottom = bottom;
        Settings = settings;
    }

    /// <summary>The top-side silkscreen.</summary>
    public SilkLayerContent Top { get; }

    /// <summary>The bottom-side silkscreen.</summary>
    public SilkLayerContent Bottom { get; }

    /// <summary>The settings the silkscreen was derived with.</summary>
    public PcbSilkscreenSettings Settings { get; }

    /// <summary>The two silkscreen layers (top then bottom).</summary>
    public IReadOnlyList<SilkLayerContent> Layers => [Top, Bottom];

    /// <summary>
    /// Derives the silkscreen from a placed layout — a body outline and a reference designator (and
    /// optionally the value) per surface component, plus board-level marks. An embedded component gets
    /// no silk (its face is inside the board).
    /// </summary>
    public static PcbSilkscreen For(PcbLayout layout, PcbSilkscreenSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        settings ??= PcbSilkscreenSettings.Default;
        settings.Validate();

        string topName = layout.Board.Stackup.Top.Name;
        string bottomName = layout.Board.Stackup.Bottom.Name;
        var top = new List<SilkStroke>();
        var bottom = new List<SilkStroke>();

        foreach (var placement in layout.Placements)
        {
            if (placement.IsEmbedded)
                continue;   // an embedded component has no exposed face to silkscreen
            var into = placement.Side == CopperSide.Top ? top : bottom;
            AppendComponent(layout, placement, settings, into);
        }

        foreach (var mark in settings.BoardMarks)
        {
            var into = mark.Side == CopperSide.Top ? top : bottom;
            double height = mark.Height ?? settings.TextHeight;
            AppendText(mark.Text, mark.Position, height, centred: true, "board", SilkKind.Mark, into);
        }

        return new PcbSilkscreen(
            new SilkLayerContent(topName, CopperSide.Top, top, settings.LineWidth),
            new SilkLayerContent(bottomName, CopperSide.Bottom, bottom, settings.LineWidth),
            settings);
    }

    /// <summary>The silkscreen layer for an OUTER copper layer name, refused by name for any other
    /// layer — the silkscreen exists on the outer copper only.</summary>
    /// <exception cref="ArgumentException"><paramref name="layer"/> is not an outer copper layer.</exception>
    public SilkLayerContent LayerFor(string layer)
    {
        if (layer == Top.Layer)
            return Top;
        if (layer == Bottom.Layer)
            return Bottom;
        throw new ArgumentException(
            $"'{layer}' is not an outer copper layer; the silkscreen exists on the outer copper layers "
            + $"only ('{Top.Layer}', '{Bottom.Layer}').", nameof(layer));
    }

    /// <summary>Every silk stroke that overlaps an exposed-copper window of the matching side, across
    /// both sides — silk on a solderable pad. <paramref name="clearance"/> (mm) grows the silk footprint
    /// before the test, so a stroke merely closer than the clearance is caught too.</summary>
    public IReadOnlyList<SilkOverCopper> OverExposedCopper(PcbMask mask, double clearance = 0)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var all = new List<SilkOverCopper>();
        all.AddRange(CheckOverCopper(Top, mask.LayerFor(Top.Layer).Openings, clearance));
        all.AddRange(CheckOverCopper(Bottom, mask.LayerFor(Bottom.Layer).Openings, clearance));
        return all;
    }

    /// <summary>Every silk stroke of <paramref name="silk"/> that overlaps one of the exposed-copper
    /// <paramref name="openings"/> (the mask windows on that side). Overlaps are grouped per (silk
    /// source, opening) so a multi-stroke refdes over one pad is one violation naming both by name.</summary>
    public static IReadOnlyList<SilkOverCopper> CheckOverCopper(
        SilkLayerContent silk, IReadOnlyList<MaskOpening> openings, double clearance = 0)
    {
        ArgumentNullException.ThrowIfNull(silk);
        ArgumentNullException.ThrowIfNull(openings);
        if (openings.Count == 0)
            return [];

        double width = silk.LineWidth + 2 * Math.Max(0, clearance);
        var overlaps = new Dictionary<(string, SilkKind, string), double>();
        foreach (var stroke in silk.Strokes)
        {
            foreach (var footprint in StrokeFootprint(stroke.Points, width))
            {
                var fb = footprint.Bounds;
                foreach (var opening in openings)
                {
                    if (Disjoint(fb, opening.Region.Bounds))
                        continue;
                    double area = CurvedRegion2dBoolean.Intersection([footprint], [opening.Region]).Sum(r => r.Area);
                    if (area <= 0)
                        continue;
                    var key = (stroke.Source, stroke.Kind, opening.Source);
                    overlaps[key] = overlaps.GetValueOrDefault(key) + area;
                }
            }
        }

        return [.. overlaps.Select(kv => new SilkOverCopper(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Value))];
    }

    // ---- building one component's silk ----

    private static void AppendComponent(
        PcbLayout layout, PcbPlacement placement, PcbSilkscreenSettings settings, List<SilkStroke> into)
    {
        var component = layout.Schematic.Find(placement.Reference)!;
        var footprint = component.Definition.Footprint;
        if (footprint is null || footprint.Pads.Count == 0)
        {
            // No pads to size a courtyard from — still label the placement point.
            if (settings.ShowReferences)
                AppendText(placement.Reference, new Vector2d(placement.X, placement.Y), settings.TextHeight,
                    centred: true, placement.Reference, SilkKind.Reference, into);
            return;
        }

        var pose = layout.PlacementPose(placement);
        var box = Aabb.Empty;
        foreach (var pad in footprint.Pads)
            box = box.Union(PadGeometry.Region(pad, pose).Bounds);

        double m = settings.CourtyardMargin;
        double minX = box.Min.X - m, minY = box.Min.Y - m, maxX = box.Max.X + m, maxY = box.Max.Y + m;
        double cx = (minX + maxX) / 2;

        if (settings.ShowCourtyards)
            into.Add(new SilkStroke(placement.Reference, SilkKind.Courtyard,
            [
                new Vector2d(minX, minY), new Vector2d(maxX, minY),
                new Vector2d(maxX, maxY), new Vector2d(minX, maxY), new Vector2d(minX, minY),
            ]));

        // The reference sits ABOVE the courtyard, the value BELOW it — clear of the pads, so a clean
        // layout has no silk-over-copper.
        if (settings.ShowReferences)
        {
            double baseY = maxY + TextGap * settings.TextHeight;
            AppendText(placement.Reference, new Vector2d(cx, baseY), settings.TextHeight,
                centred: true, placement.Reference, SilkKind.Reference, into);
        }
        if (settings.ShowValues && component.Value.Length > 0)
        {
            double baseY = minY - (TextGap + 1) * settings.TextHeight;
            AppendText(component.Value, new Vector2d(cx, baseY), settings.TextHeight,
                centred: true, placement.Reference, SilkKind.Value, into);
        }
    }

    private static void AppendText(
        string text, in Vector2d at, double height, bool centred, string source, SilkKind kind,
        List<SilkStroke> into)
    {
        var xAxis = new Vector2d(1, 0);
        var origin = centred
            ? at - xAxis * (SilkFont.TextWidth(text) * height / 2)
            : at;
        foreach (var polyline in SilkFont.Layout(text, origin, xAxis, height))
            into.Add(new SilkStroke(source, kind, polyline));
    }

    // ---- overlap geometry ----

    private static IReadOnlyList<CurvedRegion2d> StrokeFootprint(IReadOnlyList<Vector2d> points, double width)
    {
        var edges = new List<CurvedEdge2d>();
        for (int i = 1; i < points.Count; i++)
            if (points[i - 1].DistanceTo(points[i]) > CurvedRegion2d.BoundaryTolerance)
                edges.Add(CurvedEdge2d.Line(points[i - 1], points[i]));
        if (edges.Count == 0)
            return [];
        return CurvedRegion2dOffset.Stroke(edges, width, StrokeCap.Round, OffsetJoin.Round);
    }

    private static bool Disjoint(in Aabb a, in Aabb b) =>
        a.Max.X < b.Min.X || a.Min.X > b.Max.X || a.Max.Y < b.Min.Y || a.Min.Y > b.Max.Y;
}
