using System.Globalization;
using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Modeling;

/// <summary>One DXF entity, carrying its layer name. The six kinds here are the 2D
/// drawing vocabulary (LINE / ARC / CIRCLE / LWPOLYLINE / SPLINE / TEXT); everything else
/// a file contains is skipped with a diagnostic at load.</summary>
public abstract record DxfEntity(string Layer);

/// <summary>
/// The drawing unit a file declares in its <c>$INSUNITS</c> header variable. The values
/// are AutoCAD's own codes, so the enum IS the file's number.
/// <para><see cref="Unitless"/> is the honest "no claim" value and is the one a great many
/// files carry; it is never scaled. Everything else is scaled to millimetres on load
/// (<see cref="ModelUnits"/>' convention), because <b>a file that imports cleanly at the
/// wrong scale is the failure mode a unit declaration exists to prevent</b> — the same
/// rule <c>IgesReader</c> follows for its unit flag.</para>
/// </summary>
public enum DxfUnits
{
    /// <summary>No unit stated. Coordinates are taken as they are.</summary>
    Unitless = 0,
    Inches = 1,
    Feet = 2,
    Miles = 3,

    /// <summary>Millimetres — what this library writes, and what it scales imports to.</summary>
    Millimetres = 4,
    Centimetres = 5,
    Metres = 6,
    Kilometres = 7,
    Microinches = 8,
    Mils = 9,
    Yards = 10,
    Angstroms = 11,
    Nanometres = 12,
    Microns = 13,
    Decimetres = 14,
}

/// <summary>How cubic Bézier sketch segments are written.</summary>
/// <remarks>
/// A sketch carrying no cubics writes IDENTICALLY under either mode — the choice only ever
/// affects Béziers — which is what makes <see cref="Spline"/> safe to reach for.
/// </remarks>
public enum DxfCurveMode
{
    /// <summary>
    /// Flatten cubics to polyline vertices within the stated chord tolerance (the
    /// default). Every loop is ONE closed LWPOLYLINE, which every reader and every CAM
    /// post understands, at the cost of the one lossy mapping in the writer.
    /// </summary>
    Flatten,

    /// <summary>
    /// Write cubics as exact SPLINE entities, so nothing is approximated. The cost is
    /// structural rather than numerical: a loop containing a cubic arrives as a CHAIN of
    /// entities (LWPOLYLINE runs of lines and arcs, SPLINE per cubic) instead of one
    /// closed polyline, because DXF's polyline vocabulary has no cubic vertex.
    /// <see cref="DxfDocument.ToSketches(out IReadOnlyList{string}, string, double)"/>
    /// chains it back into a closed sketch, so the round trip is exact.
    /// </summary>
    Spline,
}

/// <summary>
/// A DXF line type: a name and its dash pattern in drawing units, positive for a mark
/// and negative for a gap (the DXF convention; 0 is a dot).
/// </summary>
/// <param name="Name">Table name, upper case by convention.</param>
/// <param name="Description">Human-readable pattern, shown by editors.</param>
/// <param name="Dashes">Alternating mark/gap lengths.</param>
public sealed record DxfLineType(string Name, string Description, IReadOnlyList<double> Dashes);

/// <summary>
/// The ISO 128 line types a drawing needs, as DXF patterns. Lengths are in drawing
/// units (millimetres here), sized for a sheet rather than for a model — a hidden line
/// dashed at 0.5 mm reads correctly on paper whatever the part measures, which is why
/// they are absolute and not scaled by anything.
/// </summary>
public static class DxfLineTypes
{
    /// <summary>Unbroken (ISO 128 type A/B): visible outlines and drawing furniture.</summary>
    public static DxfLineType Continuous { get; } = new("CONTINUOUS", "Solid line", []);

    /// <summary>Narrow dashed (type E): hidden detail.</summary>
    public static DxfLineType Hidden { get; } = new("HIDDEN", "Hidden __ __ __ __", [2.5, -1.5]);

    /// <summary>Long-dash dotted (type G/H): centre lines and cutting planes.</summary>
    public static DxfLineType Center { get; } = new("CENTER", "Center ____ _ ____ _", [8, -1.5, 1.5, -1.5]);

    /// <summary>Long-dash double-dotted (type K): adjacent parts, alternate positions.</summary>
    public static DxfLineType Phantom { get; } =
        new("PHANTOM", "Phantom ____ _ _ ____", [10, -1.5, 1.5, -1.5, 1.5, -1.5]);

    /// <summary>Every pattern, in discovery order.</summary>
    public static IReadOnlyList<DxfLineType> All { get; } = [Continuous, Hidden, Center, Phantom];

    /// <summary>Look one up by name (case-insensitive), or null.</summary>
    public static DxfLineType? ByName(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A straight segment (DXF LINE).</summary>
public sealed record DxfLine(Vector2d Start, Vector2d End, string Layer = "0") : DxfEntity(Layer);

/// <summary>A circular arc (DXF ARC) — always counter-clockwise from
/// <paramref name="StartDegrees"/> to <paramref name="EndDegrees"/>, the DXF
/// convention.</summary>
public sealed record DxfArc(
    Vector2d Center, double Radius, double StartDegrees, double EndDegrees, string Layer = "0")
    : DxfEntity(Layer);

/// <summary>A full circle (DXF CIRCLE).</summary>
public sealed record DxfCircle(Vector2d Center, double Radius, string Layer = "0") : DxfEntity(Layer);

/// <summary>
/// A single-line text entity (DXF TEXT): the insertion point, the CAP height in
/// drawing units, and the string. <paramref name="Anchor"/> maps to DXF group 72
/// (0 left, 1 centred, 2 right); a non-left justification also needs the alignment
/// point (11/21), which the writer emits.
/// </summary>
public sealed record DxfText(
    Vector2d Position, string Value, double Height,
    SheetTextAnchor Anchor = SheetTextAnchor.Left, string Layer = "0") : DxfEntity(Layer);

/// <summary>A multi-line note (DXF MTEXT): one entity whose <paramref name="Value"/>
/// carries its own <c>'\n'</c> breaks (written as the format's <c>\P</c> separators),
/// inserted at <paramref name="Position"/> with middle-centre attachment — what a
/// stacked run of single-line TEXTs loses in transit, a reader seeing N unrelated
/// strings instead of one note.</summary>
public sealed record DxfMText(
    Vector2d Position, string Value, double Height, int Attachment = 5, string Layer = "0")
    : DxfEntity(Layer);

/// <summary>A lightweight polyline (DXF LWPOLYLINE): vertices plus a per-vertex
/// <paramref name="Bulges"/> value — tan(sweep/4) of the arc from that vertex to the
/// next, 0 for a straight segment, positive counter-clockwise. Exact for line+arc
/// profiles, which is why it is the entity sketches export to.</summary>
public sealed record DxfPolyline(
    IReadOnlyList<Vector2d> Points, IReadOnlyList<double> Bulges, bool Closed, string Layer = "0")
    : DxfEntity(Layer)
{
    public DxfPolyline(IReadOnlyList<Vector2d> points, bool closed, string layer = "0")
        : this(points, new double[points.Count], closed, layer) { }
}

/// <summary>
/// A B-spline curve (DXF SPLINE): control points, degree and a knot vector, optionally
/// rational via <paramref name="Weights"/>. This is the exact home for a sketch's cubic
/// Bézier segments — a cubic Bézier IS a degree-3 clamped B-spline with four control
/// points, so nothing is approximated in either direction.
/// </summary>
/// <remarks>
/// The reader accepts any SPLINE the file contains and keeps it verbatim; what it can
/// turn into a <see cref="Sketch"/> is narrower and is decided in
/// <see cref="DxfDocument.ToSketches(out IReadOnlyList{string}, string, double)"/>,
/// which reports by name what it declined rather than sampling it. That split is
/// deliberate: the entity list is what the FILE says, and the sketch list is what this
/// kernel can carry exactly.
/// </remarks>
public sealed record DxfSpline(
    IReadOnlyList<Vector2d> ControlPoints, int Degree, IReadOnlyList<double> Knots,
    IReadOnlyList<double>? Weights = null, bool Closed = false, string Layer = "0")
    : DxfEntity(Layer)
{
    /// <summary>A single cubic Bézier as a clamped degree-3 spline — the form this
    /// library writes, and an exact encoding of a <c>CubicSeg</c>.</summary>
    public static DxfSpline CubicBezier(
        in Vector2d start, in Vector2d control1, in Vector2d control2, in Vector2d end,
        string layer = "0") =>
        new([start, control1, control2, end], 3, [0, 0, 0, 0, 1, 1, 1, 1], null, false, layer);

    /// <summary>True when no weight differs from 1 — the case with an exact polynomial
    /// Bézier form.</summary>
    public bool IsRational => Weights is not null && Weights.Any(w => w != 1);
}

/// <summary>
/// Minimal DXF read/write for 2D profiles — the interchange seam with drafting
/// packages and laser/plasma/router CAM. A document is a flat entity list with
/// layers; <see cref="Add(Sketch, string, double, DxfCurveMode)"/> converts sketches
/// losslessly where DXF can express them (lines and arcs become LWPOLYLINE bulge
/// vertices — tan(sweep/4) is EXACT — full circles become CIRCLE; cubic béziers either
/// flatten at a stated chord tolerance or, under <see cref="DxfCurveMode.Spline"/>,
/// become exact SPLINE entities), and
/// <see cref="ToSketches(out IReadOnlyList{string}, string, double)"/> comes back the
/// other way (closed polylines and circles directly; loose LINE/ARC/SPLINE entities
/// chained by endpoint at the 1e-9 weld tier).
/// <para>The writer emits AC1015 with a HEADER stating <c>$INSUNITS</c>, a LTYPE and
/// LAYER table, and an ENTITIES section; the reader accepts any file and reads the six
/// entity kinds above from its ENTITIES section (or from a raw entity list), reporting
/// skipped entity types in <see cref="Diagnostics"/> rather than throwing — the
/// <c>MeshReadResult</c> convention.</para>
/// <para><b>Two things the file must SAY rather than leave to a reader's guess</b>, and
/// both were learned the same way: a LTYPE table (a layer naming a pattern the file never
/// defines shows solid everywhere, losing the hidden/section classification in transit)
/// and <see cref="Units"/> (a file whose numbers mean nothing in particular imports
/// cleanly at the wrong scale — the worst kind of failure, because nothing complains).</para>
/// </summary>
public sealed class DxfDocument
{
    private readonly List<DxfEntity> _entities = [];
    private readonly List<string> _diagnostics = [];

    public IReadOnlyList<DxfEntity> Entities => _entities;

    /// <summary>What the reader skipped or could not chain — never an exception.</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>Distinct layer names in entity order.</summary>
    public IReadOnlyList<string> Layers => [.. _entities.Select(e => e.Layer).Distinct()];

    /// <summary>
    /// Line type per layer (a name from <see cref="DxfLineTypes"/>). A layer with no
    /// entry is CONTINUOUS.
    ///
    /// <para>This is the DXF half of the same edge-CLASSIFICATION story
    /// <see cref="SvgLineClass"/> tells: a drawing is only usable if hidden detail comes
    /// out dashed and a cutting plane comes out chain-dashed, and in DXF that is a
    /// property of the LAYER, not of the entity. The writer emits an LTYPE table for
    /// every pattern a layer here names, so the file is self-contained — a viewer that
    /// had to guess would show everything solid.</para>
    /// </summary>
    public Dictionary<string, string> LayerLineTypes { get; } = [];

    /// <summary>
    /// The drawing unit written to (and read from) the <c>$INSUNITS</c> header variable,
    /// millimetres by default — <see cref="ModelUnits"/>' convention, and what every
    /// coordinate in this document means.
    ///
    /// <para><b>A file that states no unit is a file every reader guesses about</b>, which
    /// is the same defect as the LTYPE table this writer emits: a downstream package has
    /// to decide whether "10" is ten millimetres or ten inches, and a laser cutter that
    /// guesses wrong ruins a sheet. So the header always carries one.</para>
    ///
    /// <para>On load this reads what the file declared; coordinates are SCALED to
    /// millimetres and the property comes back <see cref="DxfUnits.Millimetres"/>, with a
    /// diagnostic naming the original unit and the factor — so a loaded document is
    /// internally consistent and re-saves correctly. <see cref="DxfUnits.Unitless"/> is
    /// never scaled: it is the file's honest "no claim", and inventing a factor for it
    /// would be exactly the silent mis-scaling this exists to prevent.</para>
    /// </summary>
    public DxfUnits Units { get; set; } = DxfUnits.Millimetres;

    /// <summary>Millimetres per one unit of <paramref name="units"/>, or null when the
    /// code names no length (<see cref="DxfUnits.Unitless"/>, or a value outside the
    /// enum).</summary>
    internal static double? MillimetresPer(DxfUnits units) => units switch
    {
        DxfUnits.Inches => 25.4,
        DxfUnits.Feet => 304.8,
        DxfUnits.Miles => 1609344.0,
        DxfUnits.Millimetres => 1.0,
        DxfUnits.Centimetres => 10.0,
        DxfUnits.Metres => 1000.0,
        DxfUnits.Kilometres => 1e6,
        DxfUnits.Microinches => 25.4e-6,
        DxfUnits.Mils => 25.4e-3,
        DxfUnits.Yards => 914.4,
        DxfUnits.Angstroms => 1e-7,
        DxfUnits.Nanometres => 1e-6,
        DxfUnits.Microns => 1e-3,
        DxfUnits.Decimetres => 100.0,
        _ => null,
    };

    public void Add(DxfEntity entity) => _entities.Add(ValidateEntity(entity));

    /// <summary>
    /// Adds a sketch's loops as DXF entities on <paramref name="layer"/>: the outer
    /// loop and each hole become one closed LWPOLYLINE (lines and arcs exact via
    /// bulges) or a CIRCLE when the loop is a single full circle.
    /// <para>Cubic bézier segments follow <paramref name="curves"/>:
    /// <see cref="DxfCurveMode.Flatten"/> (the default) approximates them within
    /// <paramref name="chordTolerance"/>, keeping one closed polyline per loop, while
    /// <see cref="DxfCurveMode.Spline"/> writes them as exact SPLINE entities and the
    /// loop becomes a chain. A sketch with no cubics writes identically either way.</para>
    /// </summary>
    public void Add(
        Sketch sketch, string layer = "0", double chordTolerance = Sketch.DefaultChordTolerance,
        DxfCurveMode curves = DxfCurveMode.Flatten)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        AddLoop(sketch, layer, chordTolerance, curves);
        foreach (var hole in sketch.Holes)
            AddLoop(hole, layer, chordTolerance, curves);
    }

    /// <summary>Adds a polygonal region: outer loop and holes as closed LWPOLYLINEs.</summary>
    public void Add(Region2d region, string layer = "0")
    {
        ArgumentNullException.ThrowIfNull(region);
        _entities.Add(new DxfPolyline([.. region.Outer], closed: true, layer));
        foreach (var hole in region.Holes)
            _entities.Add(new DxfPolyline([.. hole], closed: true, layer));
    }

    private void AddLoop(Sketch sketch, string layer, double chordTolerance, DxfCurveMode curves)
    {
        var segments = sketch.Segments;
        if (segments.Count == 1 && segments[0] is ArcSeg { IsFullCircle: true } circle)
        {
            _entities.Add(new DxfCircle(circle.Center, circle.Radius, layer));
            return;
        }

        if (curves == DxfCurveMode.Spline && segments.Any(s => s is CubicSeg))
        {
            AddLoopWithSplines(segments, layer);
            return;
        }

        var points = new List<Vector2d>();
        var bulges = new List<double>();
        foreach (var segment in segments)
        {
            switch (segment)
            {
                case LineSeg line:
                    points.Add(line.Start);
                    bulges.Add(0);
                    break;
                case ArcSeg arc:
                    points.Add(arc.Start);
                    bulges.Add(Math.Tan(arc.Sweep / 4));
                    break;
                default:
                    // Cubic (or any future) segment: flatten. Start inclusive, end
                    // exclusive — exactly the chaining convention Flatten guarantees.
                    int before = points.Count;
                    var flat = new List<Vector2d>();
                    segment.Flatten(chordTolerance, flat);
                    points.AddRange(flat);
                    for (int i = before; i < points.Count; i++)
                        bulges.Add(0);
                    break;
            }
        }
        _entities.Add(new DxfPolyline(points, bulges, Closed: true, layer));
    }

    /// <summary>
    /// A loop containing cubics, written exactly: each cubic becomes its own SPLINE and
    /// the runs of lines and arcs between them become OPEN LWPOLYLINEs (still exact,
    /// bulges and all). The loop therefore arrives as a chain, which
    /// <see cref="ToSketches(out IReadOnlyList{string}, string, double)"/> re-closes by
    /// endpoint — every entity carries its own two endpoints, so the chain is complete by
    /// construction rather than by tolerance.
    /// </summary>
    private void AddLoopWithSplines(IReadOnlyList<SketchSegment> segments, string layer)
    {
        var points = new List<Vector2d>();
        var bulges = new List<double>();

        void FlushRun(in Vector2d end)
        {
            if (points.Count == 0)
                return;
            // An OPEN polyline needs its final vertex stated: there is no wrap to supply it.
            points.Add(end);
            bulges.Add(0);
            _entities.Add(new DxfPolyline(points, bulges, Closed: false, layer));
            points = [];
            bulges = [];
        }

        foreach (var segment in segments)
        {
            switch (segment)
            {
                case CubicSeg cubic:
                    FlushRun(cubic.Start);
                    _entities.Add(DxfSpline.CubicBezier(
                        cubic.Start, cubic.Control1, cubic.Control2, cubic.End, layer));
                    break;
                case LineSeg line:
                    points.Add(line.Start);
                    bulges.Add(0);
                    break;
                case ArcSeg arc:
                    points.Add(arc.Start);
                    bulges.Add(Math.Tan(arc.Sweep / 4));
                    break;
                default:
                    points.Add(segment.Start);
                    bulges.Add(0);
                    break;
            }
        }
        FlushRun(segments[^1].End);
    }

    // ------------------------------------------------------------------- to sketches

    /// <summary>
    /// The document's 2D profiles as sketches: CIRCLEs and closed LWPOLYLINEs map
    /// directly; loose LINE/ARC entities (and open polylines) are chained end-to-end
    /// at the weld tier into closed loops. Entities that chain into nothing closed are
    /// reported in <paramref name="diagnostics"/> and skipped — never invented.
    /// Nesting (which loop is a hole of which) is NOT re-derived here — pass the
    /// sketches to <c>Sketch.ToRegions(sketches)</c> or place them explicitly;
    /// <c>Shape.Extrude</c> callers usually want <c>WithHole</c> decisions to be
    /// theirs.
    /// </summary>
    /// <param name="layer">Only entities on this layer (null = all).</param>
    /// <param name="diagnostics">What could not become a sketch, by name.</param>
    /// <param name="tolerance">Endpoint chaining distance — the 1e-9 absolute weld
    /// tier by default, matching the sketch constructor's closure validation (a chain
    /// that only closes at a looser tolerance would fail there anyway).</param>
    public IReadOnlyList<Sketch> ToSketches(
        out IReadOnlyList<string> diagnostics, string? layer = null, double tolerance = 1e-9)
    {
        var sketches = new List<Sketch>();
        var report = new List<string>();
        var loose = new List<Curve2d>();

        foreach (var entity in _entities)
        {
            if (layer is not null && entity.Layer != layer)
                continue;
            switch (entity)
            {
                case DxfCircle circle:
                    sketches.Add(Sketch.Circle(circle.Center, circle.Radius));
                    break;
                case DxfPolyline { Closed: true } polyline:
                    TryAddPolylineSketch(polyline, sketches, report);
                    break;
                case DxfPolyline open:
                    loose.AddRange(PolylineCurves(open));
                    break;
                case DxfLine line:
                    loose.Add(new Line2d(line.Start, line.End));
                    break;
                case DxfArc arc:
                    loose.Add(ArcCurve(arc));
                    break;
                case DxfSpline spline:
                    loose.AddRange(SplineCurves(spline, report));
                    break;
            }
        }

        ChainLooseCurves(loose, tolerance, sketches, report);
        diagnostics = report;
        return sketches;
    }

    /// <summary>
    /// A SPLINE as exact sketch-vocabulary curves, or nothing plus a diagnostic.
    /// <para>The tier is deliberately narrow and stated rather than widened by sampling:
    /// a degree-1 spline is a polyline, and a NON-rational clamped cubic whose interior
    /// knots all have multiplicity 3 is ALREADY a chain of Bézier segments, so its control
    /// points split four-at-a-time with nothing computed. That covers what this writer
    /// emits (one cubic Bézier per SPLINE) and what a polybezier exporter emits. A general
    /// B-spline needs knot-insertion Bézier decomposition, which belongs in the NURBS
    /// layer rather than in a file reader — so it is REPORTED, naming why, instead of
    /// being flattened into a lie about exactness.</para>
    /// </summary>
    private static IEnumerable<Curve2d> SplineCurves(DxfSpline spline, List<string> report)
    {
        if (spline.IsRational)
        {
            report.Add(
                $"Skipped a rational degree-{spline.Degree} SPLINE on layer '{spline.Layer}': a sketch "
                + "carries polynomial cubic Beziers, and a weighted curve has no exact one.");
            yield break;
        }

        if (spline.Degree == 1)
        {
            for (int i = 0; i + 1 < spline.ControlPoints.Count; i++)
                yield return new Line2d(spline.ControlPoints[i], spline.ControlPoints[i + 1]);
            yield break;
        }

        if (spline.Degree != 3 || !IsBezierForm(spline))
        {
            report.Add(
                $"Skipped a degree-{spline.Degree} SPLINE on layer '{spline.Layer}' with "
                + $"{spline.ControlPoints.Count} control points: only degree 1, and degree 3 already in "
                + "Bezier form (clamped ends, interior knots of multiplicity 3), convert exactly. "
                + "Decomposing a general B-spline needs knot insertion.");
            yield break;
        }

        for (int i = 0; i + 3 < spline.ControlPoints.Count; i += 3)
        {
            yield return new BezierCurve2d(
                spline.ControlPoints[i], spline.ControlPoints[i + 1],
                spline.ControlPoints[i + 2], spline.ControlPoints[i + 3]);
        }
    }

    /// <summary>Is this clamped cubic already a chain of Béziers? Control point count
    /// 3k + 1, ends clamped (multiplicity 4) and every interior knot value repeated
    /// exactly 3 times. Knot equality is EXACT: a knot vector is a list of parameters a
    /// writer either repeated or did not, so a tolerance here would accept a curve that is
    /// merely nearly in Bézier form and then split it at the wrong places.</summary>
    private static bool IsBezierForm(DxfSpline spline)
    {
        int n = spline.ControlPoints.Count;
        if (n < 4 || (n - 1) % 3 != 0)
            return false;

        var knots = spline.Knots;
        for (int i = 1; i < 4; i++)
        {
            if (knots[i] != knots[0] || knots[^(i + 1)] != knots[^1])
                return false;
        }
        // Interior knots run in triples.
        for (int i = 4; i < knots.Count - 4; i += 3)
        {
            if (knots[i] != knots[i + 1] || knots[i] != knots[i + 2])
                return false;
        }
        return true;
    }

    /// <summary>Convenience overload discarding diagnostics.</summary>
    public IReadOnlyList<Sketch> ToSketches(string? layer = null, double tolerance = 1e-9) =>
        ToSketches(out _, layer, tolerance);

    private static void TryAddPolylineSketch(DxfPolyline polyline, List<Sketch> sketches, List<string> report)
    {
        try
        {
            sketches.Add(Sketch.FromCurves([.. ClosedPolylineCurves(polyline)]));
        }
        catch (ArgumentException exception)
        {
            report.Add($"Closed LWPOLYLINE on layer '{polyline.Layer}' did not form a valid sketch: {exception.Message}");
        }
    }

    private static IEnumerable<Curve2d> ClosedPolylineCurves(DxfPolyline polyline)
    {
        int count = polyline.Points.Count;
        for (int i = 0; i < count; i++)
        {
            var from = polyline.Points[i];
            var to = polyline.Points[(i + 1) % count];
            // Exact-zero guard: a duplicated closing vertex contributes no segment.
            if (from == to && BulgeOf(polyline, i) == 0)
                continue;
            yield return SegmentCurve(from, to, BulgeOf(polyline, i));
        }
    }

    private static IEnumerable<Curve2d> PolylineCurves(DxfPolyline polyline)
    {
        for (int i = 0; i + 1 < polyline.Points.Count; i++)
            yield return SegmentCurve(polyline.Points[i], polyline.Points[i + 1], BulgeOf(polyline, i));
    }

    private static double BulgeOf(DxfPolyline polyline, int index) =>
        index < polyline.Bulges.Count ? polyline.Bulges[index] : 0;

    /// <summary>One polyline segment as a curve: straight for bulge 0, else the arc the
    /// bulge encodes — sweep = 4·atan(bulge), radius and center from the chord.</summary>
    private static Curve2d SegmentCurve(Vector2d from, Vector2d to, double bulge)
    {
        if (bulge == 0)
            return new Line2d(from, to);

        double sweep = 4 * Math.Atan(bulge);
        var chord = to - from;
        double chordLength = chord.Length;
        double radius = Math.Abs(chordLength / (2 * Math.Sin(sweep / 2)));
        // Center sits on the chord's perpendicular bisector, on the side the sweep
        // sign selects: apothem = r·cos(sweep/2), signed toward the arc's center.
        var midpoint = from + chord * 0.5;
        var perpendicular = new Vector2d(-chord.Y, chord.X) / chordLength;   // CCW-left of the chord
        double apothem = radius * Math.Cos(sweep / 2) * Math.Sign(sweep);
        var center = midpoint + perpendicular * apothem;
        double startAngle = Math.Atan2(from.Y - center.Y, from.X - center.X);
        return new Arc2d(center, radius, startAngle, sweep);
    }

    private static Arc2d ArcCurve(DxfArc arc)
    {
        double start = arc.StartDegrees * Math.PI / 180;
        double end = arc.EndDegrees * Math.PI / 180;
        double sweep = end - start;
        if (sweep <= 0)
            sweep += 2 * Math.PI;   // DXF arcs are CCW start -> end
        return new Arc2d(arc.Center, arc.Radius, start, sweep);
    }

    /// <summary>Greedy endpoint chaining of loose curves into closed loops.</summary>
    private static void ChainLooseCurves(
        List<Curve2d> loose, double tolerance, List<Sketch> sketches, List<string> report)
    {
        var remaining = new List<Curve2d>(loose);
        while (remaining.Count > 0)
        {
            var chain = new List<Curve2d> { remaining[0] };
            remaining.RemoveAt(0);
            var start = StartOf(chain[0]);
            var end = EndOf(chain[0]);

            bool extended = true;
            while (extended && start.DistanceTo(end) > tolerance)
            {
                extended = false;
                for (int i = 0; i < remaining.Count; i++)
                {
                    var candidate = remaining[i];
                    if (StartOf(candidate).DistanceTo(end) <= tolerance)
                    {
                        chain.Add(candidate);
                    }
                    else if (EndOf(candidate).DistanceTo(end) <= tolerance)
                    {
                        chain.Add(Reverse(candidate));
                    }
                    else
                    {
                        continue;
                    }
                    remaining.RemoveAt(i);
                    end = EndOf(chain[^1]);
                    extended = true;
                    break;
                }
            }

            if (start.DistanceTo(end) <= tolerance)
            {
                try
                {
                    sketches.Add(Sketch.FromCurves(chain));
                }
                catch (ArgumentException exception)
                {
                    report.Add($"A chained loop of {chain.Count} entities did not form a valid sketch: {exception.Message}");
                }
            }
            else
            {
                report.Add(
                    $"{chain.Count} entit{(chain.Count == 1 ? "y" : "ies")} starting near "
                    + $"({start.X:g6}, {start.Y:g6}) do not close into a loop (gap {start.DistanceTo(end):g3}); skipped.");
            }
        }
    }

    private static Vector2d StartOf(Curve2d curve) => curve.PointAt(curve.Domain.Start);
    private static Vector2d EndOf(Curve2d curve) => curve.PointAt(curve.Domain.End);

    private static Curve2d Reverse(Curve2d curve) => curve switch
    {
        Line2d line => new Line2d(line.End, line.Start),
        Arc2d arc => new Arc2d(arc.Center, arc.Radius, arc.StartAngle + arc.SweepAngle, -arc.SweepAngle),
        _ => throw new NotSupportedException($"Cannot reverse a {curve.GetType().Name}."),
    };

    private static DxfEntity ValidateEntity(DxfEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity is DxfPolyline polyline && polyline.Bulges.Count != polyline.Points.Count)
            throw new ArgumentException(
                $"A polyline needs one bulge per vertex ({polyline.Points.Count} points, "
                + $"{polyline.Bulges.Count} bulges).", nameof(entity));
        if (entity is DxfSpline spline)
        {
            if (spline.Degree < 1)
                throw new ArgumentException($"A spline's degree must be at least 1 (got {spline.Degree}).", nameof(entity));
            if (spline.ControlPoints.Count < spline.Degree + 1)
                throw new ArgumentException(
                    $"A degree-{spline.Degree} spline needs at least {spline.Degree + 1} control points "
                    + $"(got {spline.ControlPoints.Count}).", nameof(entity));
            if (spline.Knots.Count != spline.ControlPoints.Count + spline.Degree + 1)
                throw new ArgumentException(
                    $"A degree-{spline.Degree} spline over {spline.ControlPoints.Count} control points needs "
                    + $"{spline.ControlPoints.Count + spline.Degree + 1} knots (got {spline.Knots.Count}).",
                    nameof(entity));
            if (spline.Weights is { } weights && weights.Count != spline.ControlPoints.Count)
                throw new ArgumentException(
                    $"A spline's weight count must match its control point count "
                    + $"({spline.ControlPoints.Count} points, {weights.Count} weights).", nameof(entity));
        }
        return entity;
    }

    // ------------------------------------------------------------------------ write

    public void Save(TextWriter writer)
    {
        var culture = CultureInfo.InvariantCulture;
        void Pair(int code, string value)
        {
            writer.WriteLine(code.ToString(culture));
            writer.WriteLine(value);
        }
        void Real(int code, double value) => Pair(code, value.ToString("R", culture));

        Pair(0, "SECTION"); Pair(2, "HEADER");
        Pair(9, "$ACADVER"); Pair(1, "AC1015");
        // $INSUNITS is the same duty the LTYPE table has: a file that does not SAY what
        // its numbers mean leaves every reader to guess, and a laser cutter that guesses
        // inches for millimetres ruins a sheet.
        Pair(9, "$INSUNITS"); Pair(70, ((int)Units).ToString(culture));
        Pair(0, "ENDSEC");

        IReadOnlyList<string> layers = Layers;
        if (layers.Count == 0)
            layers = ["0"];
        Pair(0, "SECTION"); Pair(2, "TABLES");

        // LTYPE first: a LAYER record referencing a pattern the file never defines is
        // what makes a reader fall back to solid lines.
        var used = new List<DxfLineType> { DxfLineTypes.Continuous };
        foreach (var layer in layers)
        {
            if (LayerLineTypes.TryGetValue(layer, out string? name)
                && DxfLineTypes.ByName(name) is { } pattern
                && !used.Any(t => t.Name == pattern.Name))
                used.Add(pattern);
        }
        Pair(0, "TABLE"); Pair(2, "LTYPE"); Pair(70, used.Count.ToString(culture));
        foreach (var pattern in used)
        {
            Pair(0, "LTYPE"); Pair(2, pattern.Name);
            Pair(70, "0"); Pair(3, pattern.Description);
            Pair(72, "65");   // 'A' alignment, the only one DXF defines
            Pair(73, pattern.Dashes.Count.ToString(culture));
            Real(40, pattern.Dashes.Sum(Math.Abs));
            foreach (double dash in pattern.Dashes)
                Real(49, dash);
        }
        Pair(0, "ENDTAB");

        Pair(0, "TABLE"); Pair(2, "LAYER"); Pair(70, layers.Count.ToString(culture));
        foreach (var layer in layers)
        {
            Pair(0, "LAYER"); Pair(2, layer);
            Pair(70, "0"); Pair(62, "7");
            Pair(6, LayerLineTypes.TryGetValue(layer, out string? name)
                    && DxfLineTypes.ByName(name) is not null
                ? name
                : DxfLineTypes.Continuous.Name);
        }
        Pair(0, "ENDTAB"); Pair(0, "ENDSEC");

        Pair(0, "SECTION"); Pair(2, "ENTITIES");
        foreach (var entity in _entities)
        {
            switch (entity)
            {
                case DxfLine line:
                    Pair(0, "LINE"); Pair(8, line.Layer);
                    Real(10, line.Start.X); Real(20, line.Start.Y); Real(30, 0);
                    Real(11, line.End.X); Real(21, line.End.Y); Real(31, 0);
                    break;
                case DxfArc arc:
                    Pair(0, "ARC"); Pair(8, arc.Layer);
                    Real(10, arc.Center.X); Real(20, arc.Center.Y); Real(30, 0);
                    Real(40, arc.Radius);
                    Real(50, arc.StartDegrees); Real(51, arc.EndDegrees);
                    break;
                case DxfCircle circle:
                    Pair(0, "CIRCLE"); Pair(8, circle.Layer);
                    Real(10, circle.Center.X); Real(20, circle.Center.Y); Real(30, 0);
                    Real(40, circle.Radius);
                    break;
                case DxfMText mtext:
                    Pair(0, "MTEXT"); Pair(8, mtext.Layer);
                    Real(10, mtext.Position.X); Real(20, mtext.Position.Y); Real(30, 0);
                    Real(40, mtext.Height);
                    Pair(71, mtext.Attachment.ToString(culture));
                    Pair(1, mtext.Value.Replace("\n", "\\P"));
                    break;
                case DxfText text:
                    Pair(0, "TEXT"); Pair(8, text.Layer);
                    Real(10, text.Position.X); Real(20, text.Position.Y); Real(30, 0);
                    Real(40, text.Height);
                    Pair(1, text.Value);
                    if (text.Anchor != SheetTextAnchor.Left)
                    {
                        Pair(72, text.Anchor == SheetTextAnchor.Center ? "1" : "2");
                        // Group 72 is only honoured alongside the alignment point, and
                        // for a centred or right-aligned string that point IS the
                        // insertion point.
                        Real(11, text.Position.X); Real(21, text.Position.Y); Real(31, 0);
                    }
                    break;
                case DxfPolyline polyline:
                    Pair(0, "LWPOLYLINE"); Pair(8, polyline.Layer);
                    Pair(90, polyline.Points.Count.ToString(culture));
                    Pair(70, polyline.Closed ? "1" : "0");
                    for (int i = 0; i < polyline.Points.Count; i++)
                    {
                        Real(10, polyline.Points[i].X);
                        Real(20, polyline.Points[i].Y);
                        double bulge = BulgeOf(polyline, i);
                        if (bulge != 0)
                            Real(42, bulge);
                    }
                    break;
                case DxfSpline spline:
                    Pair(0, "SPLINE"); Pair(8, spline.Layer);
                    // Group 70 bit flags: 1 closed, 2 periodic, 4 rational, 8 planar.
                    int flags = 8
                              | (spline.Closed ? 1 : 0)
                              | (spline.IsRational ? 4 : 0);
                    Pair(70, flags.ToString(culture));
                    Pair(71, spline.Degree.ToString(culture));
                    Pair(72, spline.Knots.Count.ToString(culture));
                    Pair(73, spline.ControlPoints.Count.ToString(culture));
                    Pair(74, "0");                                  // no fit points
                    foreach (double knot in spline.Knots)
                        Real(40, knot);
                    if (spline.IsRational)
                    {
                        foreach (double weight in spline.Weights!)
                            Real(41, weight);
                    }
                    foreach (var point in spline.ControlPoints)
                    {
                        Real(10, point.X); Real(20, point.Y); Real(30, 0);
                    }
                    break;
            }
        }
        Pair(0, "ENDSEC");
        Pair(0, "EOF");
    }

    public void SaveFile(string path)
    {
        using var writer = new StreamWriter(path);
        Save(writer);
    }

    // ------------------------------------------------------------------------- read

    public static DxfDocument LoadFile(string path)
    {
        using var reader = new StreamReader(path);
        return Load(reader);
    }

    /// <summary>Parses the ENTITIES section (or a raw entity list). Unknown entity
    /// types are counted into <see cref="Diagnostics"/>; malformed pairs end the
    /// parse with a diagnostic rather than an exception.</summary>
    public static DxfDocument Load(TextReader reader)
    {
        var culture = CultureInfo.InvariantCulture;
        var document = new DxfDocument();
        var skipped = new Dictionary<string, int>();

        // Read all (code, value) pairs first.
        var pairs = new List<(int Code, string Value)>();
        while (reader.ReadLine() is { } codeLine)
        {
            var valueLine = reader.ReadLine();
            if (valueLine is null)
            {
                document._diagnostics.Add("Trailing group code without a value; parse stopped there.");
                break;
            }
            if (!int.TryParse(codeLine.Trim(), NumberStyles.Integer, culture, out int code))
            {
                document._diagnostics.Add($"Malformed group code '{codeLine.Trim()}'; parse stopped there.");
                break;
            }
            pairs.Add((code, valueLine.Trim()));
        }

        document.ReadUnits(pairs, culture);

        // Scope to the ENTITIES section when sections exist at all.
        int begin = 0, endExclusive = pairs.Count;
        for (int i = 0; i + 1 < pairs.Count; i++)
        {
            if (pairs[i] is (0, "SECTION") && pairs[i + 1] is (2, "ENTITIES"))
            {
                begin = i + 2;
                endExclusive = pairs.Count;
                for (int j = begin; j < pairs.Count; j++)
                {
                    if (pairs[j] is (0, "ENDSEC"))
                    {
                        endExclusive = j;
                        break;
                    }
                }
                break;
            }
        }

        // Walk entities: each starts at a 0 code and owns the pairs until the next 0.
        int position = begin;
        while (position < endExclusive)
        {
            if (pairs[position].Code != 0)
            {
                position++;
                continue;
            }
            string type = pairs[position].Value;
            int bodyStart = position + 1;
            int bodyEnd = bodyStart;
            while (bodyEnd < endExclusive && pairs[bodyEnd].Code != 0)
                bodyEnd++;
            ParseEntity(document, type, pairs, bodyStart, bodyEnd, skipped, culture);
            position = bodyEnd;
        }

        foreach (var (type, count) in skipped)
            document._diagnostics.Add($"Skipped {count} '{type}' entit{(count == 1 ? "y" : "ies")} (not a 2D profile entity).");
        document.ScaleToMillimetres();
        return document;
    }

    /// <summary>Reads <c>$INSUNITS</c> — the header variable is the pair AFTER the
    /// <c>9 $INSUNITS</c> marker, so it is located by the marker rather than by scanning
    /// for a group 70 (of which a file has hundreds).</summary>
    private void ReadUnits(List<(int Code, string Value)> pairs, CultureInfo culture)
    {
        for (int i = 0; i + 1 < pairs.Count; i++)
        {
            if (pairs[i] is not (9, "$INSUNITS"))
                continue;
            if (!int.TryParse(pairs[i + 1].Value, NumberStyles.Integer, culture, out int code))
            {
                _diagnostics.Add($"Unreadable $INSUNITS value '{pairs[i + 1].Value}'; units taken as unstated.");
                Units = DxfUnits.Unitless;
                return;
            }
            var units = (DxfUnits)code;
            if (MillimetresPer(units) is null && units != DxfUnits.Unitless)
            {
                _diagnostics.Add(
                    $"$INSUNITS is {code}, which is not a drafting length unit; coordinates were "
                    + "left as they are.");
                Units = DxfUnits.Unitless;
                return;
            }
            Units = units;
            return;
        }
        // No declaration at all is the file saying nothing, not the file saying millimetres.
        Units = DxfUnits.Unitless;
    }

    /// <summary>
    /// Rescales every coordinate a foreign unit into millimetres and re-labels the
    /// document, so what comes back is internally consistent and re-saves correctly. The
    /// original unit and the factor go into <see cref="Diagnostics"/> — which is where
    /// "what the reader did" belongs; it is not a property that could round-trip, since a
    /// re-save would then declare inches over millimetre coordinates.
    /// </summary>
    private void ScaleToMillimetres()
    {
        if (MillimetresPer(Units) is not { } factor || factor == 1)
            return;                                  // unitless, unknown, or already mm

        for (int i = 0; i < _entities.Count; i++)
            _entities[i] = Scaled(_entities[i], factor);
        _diagnostics.Add(
            $"$INSUNITS declared {Units}; every coordinate was scaled by {factor.ToString("R", CultureInfo.InvariantCulture)} "
            + "into millimetres.");
        Units = DxfUnits.Millimetres;
    }

    private static DxfEntity Scaled(DxfEntity entity, double factor) => entity switch
    {
        DxfLine line => line with { Start = line.Start * factor, End = line.End * factor },
        DxfArc arc => arc with { Center = arc.Center * factor, Radius = arc.Radius * factor },
        DxfCircle circle => circle with { Center = circle.Center * factor, Radius = circle.Radius * factor },
        DxfText text => text with { Position = text.Position * factor, Height = text.Height * factor },
        // A bulge is tan(sweep/4) — an ANGLE, invariant under a uniform scale — so only
        // the vertices move. Scaling it too would reshape every arc.
        DxfPolyline polyline => polyline with { Points = [.. polyline.Points.Select(p => p * factor)] },
        // Knots and weights are parameters, not lengths; only the control points scale.
        DxfSpline spline => spline with { ControlPoints = [.. spline.ControlPoints.Select(p => p * factor)] },
        _ => entity,
    };

    private static void ParseEntity(
        DxfDocument document, string type, List<(int Code, string Value)> pairs,
        int start, int end, Dictionary<string, int> skipped, CultureInfo culture)
    {
        double Value(int code, double fallback = 0)
        {
            for (int i = start; i < end; i++)
            {
                if (pairs[i].Code == code
                    && double.TryParse(pairs[i].Value, NumberStyles.Float, culture, out double parsed))
                    return parsed;
            }
            return fallback;
        }
        string Layer()
        {
            for (int i = start; i < end; i++)
            {
                if (pairs[i].Code == 8)
                    return pairs[i].Value;
            }
            return "0";
        }

        switch (type)
        {
            case "LINE":
                document._entities.Add(new DxfLine(
                    (Value(10), Value(20)), (Value(11), Value(21)), Layer()));
                break;
            case "ARC":
                document._entities.Add(new DxfArc(
                    (Value(10), Value(20)), Value(40), Value(50), Value(51), Layer()));
                break;
            case "CIRCLE":
                document._entities.Add(new DxfCircle((Value(10), Value(20)), Value(40), Layer()));
                break;
            case "MTEXT":
            {
                // Contents arrive as code-3 chunks (250-char continuation) followed by
                // the final code-1; concatenated IN ORDER, with the format's \P
                // paragraph separators restored to '\n'.
                var contents = new System.Text.StringBuilder();
                for (int i = start; i < end; i++)
                {
                    if (pairs[i].Code is 3 or 1)
                        contents.Append(pairs[i].Value);
                }
                document._entities.Add(new DxfMText(
                    (Value(10), Value(20)), contents.Replace("\\P", "\n").ToString(),
                    Value(40, 1), (int)Value(71, 5), Layer()));
                break;
            }
            case "TEXT":
            {
                string? value = null;
                for (int i = start; i < end && value is null; i++)
                {
                    if (pairs[i].Code == 1)
                        value = pairs[i].Value;
                }
                var anchor = (int)Value(72) switch
                {
                    1 => SheetTextAnchor.Center,
                    2 => SheetTextAnchor.Right,
                    _ => SheetTextAnchor.Left,
                };
                document._entities.Add(new DxfText(
                    (Value(10), Value(20)), value ?? "", Value(40, 1), anchor, Layer()));
                break;
            }
            case "LWPOLYLINE":
            {
                bool closed = ((int)Value(70)) % 2 == 1;   // bit 0 = closed
                var points = new List<Vector2d>();
                var bulges = new List<double>();
                double? x = null;
                for (int i = start; i < end; i++)
                {
                    var (code, raw) = pairs[i];
                    if (!double.TryParse(raw, NumberStyles.Float, culture, out double value))
                        continue;
                    switch (code)
                    {
                        case 10:
                            x = value;
                            break;
                        case 20 when x is { } pendingX:
                            points.Add(new Vector2d(pendingX, value));
                            bulges.Add(0);
                            x = null;
                            break;
                        case 42 when points.Count > 0:
                            bulges[^1] = value;
                            break;
                    }
                }
                if (points.Count >= 2)
                    document._entities.Add(new DxfPolyline(points, bulges, closed, Layer()));
                else
                    document._diagnostics.Add("Skipped an LWPOLYLINE with fewer than 2 vertices.");
                break;
            }
            case "SPLINE":
            {
                int flags = (int)Value(70);
                int degree = (int)Value(71, 3);
                var controls = new List<Vector2d>();
                var knots = new List<double>();
                var weights = new List<double>();
                double? x = null;
                for (int i = start; i < end; i++)
                {
                    var (code, raw) = pairs[i];
                    if (!double.TryParse(raw, NumberStyles.Float, culture, out double value))
                        continue;
                    switch (code)
                    {
                        case 40:
                            knots.Add(value);
                            break;
                        case 41:
                            weights.Add(value);
                            break;
                        case 10:
                            x = value;
                            break;
                        case 20 when x is { } pendingX:
                            controls.Add(new Vector2d(pendingX, value));
                            x = null;
                            break;
                    }
                }
                // A degree-d curve over n control points needs exactly n + d + 1 knots;
                // anything else is a malformed (or fit-point-only) spline this cannot
                // reconstruct, so it is named rather than guessed at.
                if (controls.Count >= degree + 1 && knots.Count == controls.Count + degree + 1)
                {
                    document._entities.Add(new DxfSpline(
                        controls, degree, knots,
                        weights.Count == controls.Count ? weights : null,
                        (flags & 1) != 0, Layer()));
                }
                else
                {
                    document._diagnostics.Add(
                        $"Skipped a degree-{degree} SPLINE with {controls.Count} control point(s) and "
                        + $"{knots.Count} knot(s) (a degree-{degree} curve over {controls.Count} control "
                        + $"points needs {controls.Count + degree + 1}); fit-point-only splines are not "
                        + "reconstructed.");
                }
                break;
            }
            case "SECTION" or "ENDSEC" or "EOF" or "TABLE" or "ENDTAB" or "LAYER":
                break;   // structure, not entities
            default:
                skipped[type] = skipped.GetValueOrDefault(type) + 1;
                break;
        }
    }
}
