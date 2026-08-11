using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

/// <summary>Named line-work layers a <see cref="SchematicDrawing"/> writes to, so an SVG or
/// DXF consumer can toggle whole classes (symbol bodies, wires, junctions, text). Fixed
/// strings rather than an enum because they cross into files, where a renamed enum member
/// would silently change a layer name — the same rule <c>SheetLayers</c> follows.</summary>
public static class SchematicLayers
{
    /// <summary>Symbol body graphics (the drawn shape of each part).</summary>
    public const string Symbols = "symbols";

    /// <summary>Pin stub lines (from a pin's anchor to where it meets the body).</summary>
    public const string Pins = "pins";

    /// <summary>Wires between connected pins.</summary>
    public const string Wires = "wires";

    /// <summary>Bus wires (a thick bundle line), their diagonal entry stubs and the bus-vector
    /// label. Drawing SUGAR — bus line-work is never part of the wire graph, so it cannot merge
    /// two signal nets (see <see cref="SchematicBus"/>).</summary>
    public const string Bus = "bus";

    /// <summary>Junction dots where three or more wires meet.</summary>
    public const string Junctions = "junctions";

    /// <summary>Net labels (the name of a net drawn as a label rather than a long wire).</summary>
    public const string Labels = "labels";

    /// <summary>Reference designators (<c>R1</c>, <c>U3</c>).</summary>
    public const string References = "references";

    /// <summary>Component values (<c>330</c>, <c>100nF</c>).</summary>
    public const string Values = "values";

    /// <summary>Deliberate no-connect markers (an X on an intentionally unconnected pin).</summary>
    public const string NoConnects = "noconnects";

    /// <summary>The sheet border.</summary>
    public const string Border = "border";

    /// <summary>The title block frame and its fields.</summary>
    public const string TitleBlock = "titleblock";
}

/// <summary>
/// One terminal as it was drawn on the sheet: the <see cref="PinRef"/> it realises, the
/// world <see cref="Anchor"/> where its wire lands, and how it is connected — either by wire
/// (<see cref="Label"/> is null) or by a shared net <see cref="Label"/> (a rail, drawn as
/// labels rather than one long wire). The anchor is the load-bearing value: a wire routed to
/// this pin ends exactly here, so a symbol's pin anchor coincides with its wire endpoint to
/// the bit.
/// </summary>
/// <param name="Pin">The netlist terminal this draws.</param>
/// <param name="Anchor">The connection point on the sheet (mm), where a wire lands.</param>
/// <param name="Label">The net label if this pin is drawn as a label, else null (wired).</param>
public readonly record struct DrawnPin(PinRef Pin, Vector2d Anchor, string? Label);

/// <summary>
/// A bus as it was drawn on the sheet: the base <see cref="Name"/> and expanded
/// <see cref="Members"/> (<c>NAME</c>+i), the thick bus-wire polyline <see cref="Path"/>, the
/// diagonal <see cref="Entries"/> ripping members off it, and the vector <see cref="Label"/>
/// (<c>NAME[m..n]</c>). It is DRAWING SUGAR: none of this geometry is in the wire graph
/// (<see cref="SchematicDrawing.WireSegments"/>), so a bus can never join two signal nets — which
/// is why <see cref="SchematicDrawing.Verify"/> is unaffected by it.
/// </summary>
/// <param name="Name">The bus base name (<c>"DATA"</c>).</param>
/// <param name="Members">The expanded member net names (<c>NAME</c>+i), in the drawn direction.</param>
/// <param name="Path">The bus wire polyline, sheet mm, rendered THICK.</param>
/// <param name="Entries">The diagonal entry stubs (each a segment from the bus to a signal wire).</param>
/// <param name="Label">The bus-vector label as drawn.</param>
public readonly record struct DrawnBus(
    string Name,
    IReadOnlyList<string> Members,
    IReadOnlyList<Vector2d> Path,
    IReadOnlyList<(Vector2d A, Vector2d B)> Entries,
    SheetText Label);

/// <summary>
/// The drawn schematic SHEET: placed symbols, wires, junction dots, net labels, reference
/// designators, values, a border and a title block — the human-readable VIEW of a
/// <see cref="Schematic"/> that replaces <see cref="Netlist.ToText"/>.
///
/// <para><b>It is a deterministic FUNCTION of the graph and the placement, never a second
/// editable thing</b> (the one-declaration rule): a <see cref="SchematicSheet"/> computes it,
/// so it cannot disagree with the netlist — the same schematic and placement produce
/// byte-identical SVG/DXF. Everything here is already in SHEET COORDINATES (millimetres,
/// origin at the bottom-left of the paper, y up — the same convention <see cref="SvgDrawing"/>
/// and the drawing-sheet machinery use), ready to write via <see cref="ToSvg"/>,
/// <see cref="ToDxf"/> and <see cref="ToPdf"/>.</para>
///
/// <para><b>The drawing does not lie about connectivity.</b> <see cref="Connectivity"/> is
/// reconstructed from the drawn primitives (the wire segments, the pin anchors and the net
/// labels), independently of how they were routed, so <see cref="Verify"/> can assert the
/// drawn sheet joins exactly the pins the netlist connects — no connection omitted, none
/// invented.</para>
/// </summary>
public sealed class SchematicDrawing
{
    private readonly List<(Vector2d A, Vector2d B, string Layer)> _segments;
    private readonly List<(Vector2d A, Vector2d B)> _wireSegments;
    private readonly List<SheetText> _texts;
    private readonly List<DrawnPin> _pins;
    private readonly List<DrawnBus> _buses;
    private readonly double _junctionDotDiameter;
    private readonly double _busWireWidth;

    internal SchematicDrawing(
        Schematic schematic,
        SheetFormat format,
        List<(Vector2d A, Vector2d B, string Layer)> segments,
        List<(Vector2d A, Vector2d B)> wireSegments,
        List<SheetText> texts,
        List<DrawnPin> pins,
        List<DrawnBus> buses,
        double junctionDotDiameter,
        double busWireWidth)
    {
        Schematic = schematic;
        Format = format;
        _segments = segments;
        _wireSegments = wireSegments;
        _texts = texts;
        _pins = pins;
        _buses = buses;
        _junctionDotDiameter = junctionDotDiameter;
        _busWireWidth = busWireWidth;
        Junctions = FindJunctions(wireSegments);
        Connectivity = new DrawnConnectivity(pins, wireSegments);
    }

    /// <summary>The schematic this draws.</summary>
    public Schematic Schematic { get; }

    /// <summary>The paper the sheet is sized to.</summary>
    public SheetFormat Format { get; }

    /// <summary>Every drawn line segment with the layer it belongs to (symbol bodies, pins,
    /// wires, label stubs, border, title-block rules), in draw order.</summary>
    public IReadOnlyList<(Vector2d A, Vector2d B, string Layer)> Segments => _segments;

    /// <summary>Just the net-wire segments (the routing that connects wired pins) — the subset
    /// <see cref="Junctions"/> and <see cref="Connectivity"/> are computed from.</summary>
    public IReadOnlyList<(Vector2d A, Vector2d B)> WireSegments => _wireSegments;

    /// <summary>Every piece of drawn text (net labels, reference designators, values, title
    /// block), each carrying its layer.</summary>
    public IReadOnlyList<SheetText> Texts => _texts;

    /// <summary>The pins as drawn — their world anchors and whether each is wired or
    /// labelled.</summary>
    public IReadOnlyList<DrawnPin> Pins => _pins;

    /// <summary>The buses as drawn — each a thick bundle wire, its diagonal entry stubs and its
    /// vector label. DRAWING SUGAR: this line-work is deliberately kept OUT of
    /// <see cref="WireSegments"/>, so a bus can never join two signal nets.</summary>
    public IReadOnlyList<DrawnBus> Buses => _buses;

    /// <summary>Points where three or more wire endpoints meet — drawn as junction dots. A
    /// wire CROSSING (two nets passing over one another mid-segment) is not a junction, which
    /// is the schematic convention: wires join only where a dot says so.</summary>
    public IReadOnlyList<Vector2d> Junctions { get; }

    /// <summary>The connectivity the drawn sheet actually expresses, reconstructed from the
    /// primitives.</summary>
    public DrawnConnectivity Connectivity { get; }

    /// <summary>
    /// Asserts the drawn sheet joins exactly the pins the schematic connects, BOTH ways: every
    /// signal/stub net's pins are joined (by wire path or by a shared label), and no two pins
    /// on different nets are joined. A drawing that omitted a connection the netlist has, or
    /// invented one it does not, fails this — which is what makes the sheet a faithful view of
    /// the graph rather than a picture that might lie.
    /// </summary>
    public DrawnConnectivityReport Verify() => DrawnConnectivityReport.Of(this);

    // ------------------------------------------------------------------- writers

    /// <summary>The sheet as an SVG document sized to its paper.</summary>
    public string ToSvg()
    {
        var svg = new SvgDrawing { Sheet = (Format.Width, Format.Height) };
        foreach (var group in GroupByLayer())
            svg.AddSegments(group.Select(s => (s.A, s.B)), LineClassOf(group.Key), group.Key);
        // A junction dot is a FILLED disc; a round-capped zero-length stroke of the dot's
        // diameter is exactly that in SVG (and PDF), which is how a stroke-only writer draws a
        // filled mark without a fill of its own.
        if (Junctions.Count > 0)
        {
            var pen = new SvgDrawing.SvgPen("#111111", _junctionDotDiameter, null);
            foreach (var dot in Junctions)
                svg.AddPolyline([dot, dot], closed: false, SvgLineClass.Visible, SchematicLayers.Junctions, pen);
        }
        // Buses on top of the wires they bundle: the bus wire as a THICK line, its entries as
        // ordinary-width diagonal stubs. Both on the bus layer; the labels ride the text path.
        if (_buses.Count > 0)
        {
            var busPen = new SvgDrawing.SvgPen("#111111", _busWireWidth, null);
            foreach (var bus in _buses)
            {
                svg.AddPolyline(bus.Path, closed: false, SvgLineClass.Visible, SchematicLayers.Bus, busPen);
                svg.AddSegments(bus.Entries, SvgLineClass.Visible, SchematicLayers.Bus);
            }
        }
        foreach (var text in _texts)
            svg.AddText(text.Position, text.Text, text.Height, text.Anchor, text.Layer);
        return svg.ToSvg();
    }

    /// <summary>Writes the sheet's SVG to a file.</summary>
    public void SaveSvg(string path) => File.WriteAllText(path, ToSvg());

    /// <summary>
    /// The sheet as a DXF document (millimetres, y up — the same coordinates the SVG carries).
    /// A junction is a small CIRCLE marker rather than a filled dot: the 2D DXF writer carries
    /// no line width and no fill, so the marker is an outline — a per-format spelling
    /// difference, exactly the kind the drawing writers already have.
    /// </summary>
    public DxfDocument ToDxf()
    {
        var dxf = new DxfDocument();
        foreach (var (a, b, layer) in _segments)
            dxf.Add(new DxfLine(a, b, layer));
        foreach (var dot in Junctions)
            dxf.Add(new DxfCircle(dot, _junctionDotDiameter / 2, SchematicLayers.Junctions));
        // Bus wires and entries as ordinary lines on the bus layer: the 2D DXF writer carries no
        // line width, so a bus's thickness is a lineweight the layer confers on read-back — the
        // same per-format spelling difference the junction dot has (a filled disc vs an outline
        // circle).
        foreach (var bus in _buses)
        {
            for (int i = 0; i + 1 < bus.Path.Count; i++)
                dxf.Add(new DxfLine(bus.Path[i], bus.Path[i + 1], SchematicLayers.Bus));
            foreach (var (a, b) in bus.Entries)
                dxf.Add(new DxfLine(a, b, SchematicLayers.Bus));
        }
        foreach (var text in _texts)
            dxf.Add(new DxfText(text.Position, text.Text, text.Height, text.Anchor, text.Layer));
        return dxf;
    }

    /// <summary>Writes the sheet's DXF to a file.</summary>
    public void SaveDxf(string path) => ToDxf().SaveFile(path);

    /// <summary>The sheet as a PDF file — over the same primitives the SVG writer consumes, so
    /// the two cannot disagree about what the sheet looks like. PDF has no layers; the line
    /// classes keep their pens and junction dots are filled discs (PDF's round line cap is the
    /// SVG one).</summary>
    public byte[] ToPdf()
    {
        var pdf = new PdfDrawing { Sheet = (Format.Width, Format.Height) };
        foreach (var group in GroupByLayer())
            pdf.AddSegments(group.Select(s => (s.A, s.B)), LineClassOf(group.Key));
        if (Junctions.Count > 0)
        {
            var pen = new SvgDrawing.SvgPen("#111111", _junctionDotDiameter, null);
            foreach (var dot in Junctions)
                pdf.AddPolyline([dot, dot], closed: false, SvgLineClass.Visible, pen);
        }
        // Buses: the thick bundle wire plus its ordinary-width entry stubs (PDF has no layers;
        // the pen carries the distinction). The labels ride the text path.
        if (_buses.Count > 0)
        {
            var busPen = new SvgDrawing.SvgPen("#111111", _busWireWidth, null);
            foreach (var bus in _buses)
            {
                pdf.AddPolyline(bus.Path, closed: false, SvgLineClass.Visible, busPen);
                pdf.AddSegments(bus.Entries, SvgLineClass.Visible);
            }
        }
        foreach (var text in _texts)
            pdf.AddText(text.Position, text.Text, text.Height, text.Anchor);
        return pdf.ToPdf();
    }

    /// <summary>Writes the sheet's PDF to a file.</summary>
    public void SavePdf(string path) => File.WriteAllBytes(path, ToPdf());

    /// <summary>The line class (pen) each layer draws with: symbols, pins and wires solid; the
    /// border, title block and labels thin. Read by all three writers so they cannot drift.</summary>
    private static SvgLineClass LineClassOf(string layer) => layer switch
    {
        SchematicLayers.Symbols or SchematicLayers.Pins or SchematicLayers.Wires
            or SchematicLayers.NoConnects => SvgLineClass.Visible,
        _ => SvgLineClass.Thin,
    };

    // Groups preserve first-occurrence order (deterministic), so the writers' output is a
    // function of the drawn primitives and nothing else.
    private IEnumerable<IGrouping<string, (Vector2d A, Vector2d B, string Layer)>> GroupByLayer() =>
        _segments.GroupBy(s => s.Layer);

    // ------------------------------------------------------------------- junctions

    /// <summary>Junction dots are the points where at least three wire endpoints coincide — a
    /// T or a cross of wires. Computed from the segments alone (not from the router's own
    /// bookkeeping), so the dots agree with what is actually drawn. Coordinates are exact
    /// (orthogonal projections of exact anchors), so a snap at a scale-free grid welds only
    /// what should weld.</summary>
    private static List<Vector2d> FindJunctions(List<(Vector2d A, Vector2d B)> wires)
    {
        var count = new Dictionary<(long, long), (Vector2d Point, int Count)>();
        void Bump(in Vector2d p)
        {
            var key = WeldKey(p);
            count[key] = count.TryGetValue(key, out var e) ? (e.Point, e.Count + 1) : (p, 1);
        }
        foreach (var (a, b) in wires)
        {
            Bump(a);
            Bump(b);
        }
        var dots = new List<Vector2d>();
        foreach (var (_, entry) in count)
            if (entry.Count >= 3)
                dots.Add(entry.Point);
        // Deterministic order (a dictionary walk is not), so the SVG/DXF are byte-identical.
        dots.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));
        return dots;
    }

    /// <summary>Snap key for the connectivity graph. A scale-free 1e-6 mm grid — far below any
    /// real pin spacing and far above the exact-zero difference of coordinates that should
    /// coincide (both are orthogonal projections of the same exact anchor), so it welds
    /// exactly the points that are the same point.</summary>
    internal static (long, long) WeldKey(in Vector2d p)
    {
        const double grid = 1e-6;
        return ((long)Math.Round(p.X / grid), (long)Math.Round(p.Y / grid));
    }
}

/// <summary>
/// The connectivity the drawn sheet expresses, reconstructed from its primitives — the wire
/// segments, the pin anchors and the net labels — so it is what the drawing SAYS rather than
/// what the router meant. Two pins are joined when a wire path connects their anchors, or when
/// they carry the same net label.
/// </summary>
public sealed class DrawnConnectivity
{
    private readonly Dictionary<PinRef, DrawnPin> _byPin = new();
    private readonly int[] _parent;
    private readonly Dictionary<(long, long), int> _node = new();

    internal DrawnConnectivity(IReadOnlyList<DrawnPin> pins, IReadOnlyList<(Vector2d A, Vector2d B)> wires)
    {
        foreach (var pin in pins)
            _byPin[pin.Pin] = pin;

        // Union-find over the wire graph: every distinct endpoint is a node, every segment
        // unions its two ends. A wired pin's anchor is an endpoint of its net's wires, so it
        // lands in the graph; a labelled pin's anchor touches no net-wire and stays isolated
        // (it joins only by label).
        foreach (var (a, b) in wires)
        {
            Intern(a);
            Intern(b);
        }
        _parent = [.. Enumerable.Range(0, _node.Count)];
        foreach (var (a, b) in wires)
            Union(_node[SchematicDrawing.WeldKey(a)], _node[SchematicDrawing.WeldKey(b)]);
    }

    private int Intern(in Vector2d p)
    {
        var key = SchematicDrawing.WeldKey(p);
        if (!_node.TryGetValue(key, out int id))
        {
            id = _node.Count;
            _node[key] = id;
        }
        return id;
    }

    private int Find(int x)
    {
        while (_parent[x] != x)
            x = _parent[x] = _parent[_parent[x]];
        return x;
    }

    private void Union(int a, int b) => _parent[Find(a)] = Find(b);

    private int? ComponentOf(in Vector2d p)
    {
        var key = SchematicDrawing.WeldKey(p);
        return _node.TryGetValue(key, out int id) ? Find(id) : null;
    }

    /// <summary>The net label a pin is drawn with, or null when it is wired (or not drawn).</summary>
    public string? LabelOf(PinRef pin) => _byPin.TryGetValue(pin, out var drawn) ? drawn.Label : null;

    /// <summary>Whether this pin was drawn on the sheet at all (it is on a signal or stub net).</summary>
    public bool IsDrawn(PinRef pin) => _byPin.ContainsKey(pin);

    /// <summary>
    /// Whether the two pins are joined by the drawing: either both carry the SAME net label, or
    /// both are wired and a wire path connects their anchors. A labelled pin is never
    /// wire-joined (it carries a label, so it takes the label branch), which is what keeps
    /// labels and wires from cross-joining two different nets.
    /// </summary>
    public bool AreJoined(PinRef a, PinRef b)
    {
        if (!_byPin.TryGetValue(a, out var pa) || !_byPin.TryGetValue(b, out var pb))
            return false;
        if (pa.Label is not null || pb.Label is not null)
            return pa.Label is not null && pa.Label == pb.Label;
        var ca = ComponentOf(pa.Anchor);
        var cb = ComponentOf(pb.Anchor);
        return ca is not null && ca == cb;
    }
}

/// <summary>
/// The result of checking a drawn sheet against its schematic (see
/// <see cref="SchematicDrawing.Verify"/>). Every offender is NAMED — a report that only said
/// "the drawing is wrong" would be useless, the ECAD house style.
/// </summary>
public sealed class DrawnConnectivityReport
{
    private DrawnConnectivityReport(
        IReadOnlyList<string> disconnected, IReadOnlyList<string> falseJoins)
    {
        DisconnectedNets = disconnected;
        FalseJoins = falseJoins;
    }

    /// <summary>Nets whose connected pins the drawing did NOT join — a connection omitted.</summary>
    public IReadOnlyList<string> DisconnectedNets { get; }

    /// <summary>Pairs of pins on DIFFERENT nets the drawing joined — a connection invented.</summary>
    public IReadOnlyList<string> FalseJoins { get; }

    /// <summary>True when the drawing joins exactly the pins the schematic connects.</summary>
    public bool Ok => DisconnectedNets.Count == 0 && FalseJoins.Count == 0;

    internal static DrawnConnectivityReport Of(SchematicDrawing drawing)
    {
        var connectivity = drawing.Connectivity;
        var nets = drawing.Schematic.Nets
            .Where(n => n.Kind is NetKind.Signal or NetKind.Stub)
            .ToList();

        var disconnected = new List<string>();
        foreach (var net in nets)
        {
            var drawn = net.Pins.Where(connectivity.IsDrawn).ToList();
            for (int i = 1; i < drawn.Count; i++)
            {
                if (!connectivity.AreJoined(drawn[0], drawn[i]))
                {
                    disconnected.Add(
                        $"Net '{net.Name}' connects {string.Join(", ", net.Pins)}, but the drawing does not "
                        + $"join {drawn[0]} and {drawn[i]}.");
                    break;
                }
            }
        }

        // A false join can only arise between two DIFFERENT nets; comparing each net's pins
        // against every later net's pins is exhaustive over the drawn set.
        var falseJoins = new List<string>();
        for (int i = 0; i < nets.Count; i++)
        {
            for (int j = i + 1; j < nets.Count; j++)
            {
                foreach (var pa in nets[i].Pins)
                {
                    foreach (var pb in nets[j].Pins)
                    {
                        if (connectivity.AreJoined(pa, pb))
                            falseJoins.Add(
                                $"The drawing joins {pa} (net '{nets[i].Name}') and {pb} "
                                + $"(net '{nets[j].Name}'), which the schematic does not connect.");
                    }
                }
            }
        }

        return new DrawnConnectivityReport(disconnected, falseJoins);
    }

    /// <summary>A human-readable summary — <c>OK</c>, or the named offenders.</summary>
    public override string ToString()
    {
        if (Ok)
            return "Drawn connectivity OK: the sheet joins exactly the pins the schematic connects.";
        return string.Join(
            "\n",
            DisconnectedNets.Concat(FalseJoins).Prepend("Drawn connectivity mismatch:"));
    }
}
