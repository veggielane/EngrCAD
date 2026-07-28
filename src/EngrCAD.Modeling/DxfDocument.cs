using System.Globalization;
using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Modeling;

/// <summary>One DXF entity, carrying its layer name. The five kinds here are the 2D
/// drawing vocabulary (LINE / ARC / CIRCLE / LWPOLYLINE / TEXT); everything else a file
/// contains is skipped with a diagnostic at load.</summary>
public abstract record DxfEntity(string Layer);

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
/// Minimal DXF read/write for 2D profiles — the interchange seam with drafting
/// packages and laser/plasma/router CAM. A document is a flat entity list with
/// layers; <see cref="Add(Sketch, string, double)"/> converts sketches losslessly
/// where DXF can express them (lines and arcs become LWPOLYLINE bulge vertices —
/// tan(sweep/4) is EXACT — full circles become CIRCLE; cubic béziers are flattened at
/// a stated chord tolerance, DXF's polyline vocabulary having no cubic form), and
/// <see cref="ToSketches"/> comes back the other way (closed polylines and circles
/// directly; loose LINE/ARC entities chained by endpoint at the 1e-9 weld tier).
/// <para>The writer emits AC1015 with a LAYER table and an ENTITIES section; the
/// reader accepts any file and reads the four entity kinds above from its ENTITIES
/// section (or from a raw entity list), reporting skipped entity types in
/// <see cref="Diagnostics"/> rather than throwing — the <c>MeshReadResult</c>
/// convention.</para>
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

    public void Add(DxfEntity entity) => _entities.Add(ValidateEntity(entity));

    /// <summary>
    /// Adds a sketch's loops as DXF entities on <paramref name="layer"/>: the outer
    /// loop and each hole become one closed LWPOLYLINE (lines and arcs exact via
    /// bulges) or a CIRCLE when the loop is a single full circle. Cubic bézier
    /// segments are flattened within <paramref name="chordTolerance"/> — the one lossy
    /// mapping, chosen over silently writing nothing.
    /// </summary>
    public void Add(Sketch sketch, string layer = "0", double chordTolerance = Sketch.DefaultChordTolerance)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        AddLoop(sketch, layer, chordTolerance);
        foreach (var hole in sketch.Holes)
            AddLoop(hole, layer, chordTolerance);
    }

    /// <summary>Adds a polygonal region: outer loop and holes as closed LWPOLYLINEs.</summary>
    public void Add(Region2d region, string layer = "0")
    {
        ArgumentNullException.ThrowIfNull(region);
        _entities.Add(new DxfPolyline([.. region.Outer], closed: true, layer));
        foreach (var hole in region.Holes)
            _entities.Add(new DxfPolyline([.. hole], closed: true, layer));
    }

    private void AddLoop(Sketch sketch, string layer, double chordTolerance)
    {
        var segments = sketch.Segments;
        if (segments.Count == 1 && segments[0] is ArcSeg { IsFullCircle: true } circle)
        {
            _entities.Add(new DxfCircle(circle.Center, circle.Radius, layer));
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
            }
        }

        ChainLooseCurves(loose, tolerance, sketches, report);
        diagnostics = report;
        return sketches;
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
        return document;
    }

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
            case "SECTION" or "ENDSEC" or "EOF" or "TABLE" or "ENDTAB" or "LAYER":
                break;   // structure, not entities
            default:
                skipped[type] = skipped.GetValueOrDefault(type) + 1;
                break;
        }
    }
}
