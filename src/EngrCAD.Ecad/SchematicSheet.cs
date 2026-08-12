using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

/// <summary>
/// Where a component's symbol sits on the sheet: a <see cref="Position"/> (the symbol's own
/// origin, in sheet millimetres), an orthogonal rotation in <see cref="QuarterTurns"/> (90°
/// steps — the schematic convention; a symbol is placed upright or turned, never at an
/// arbitrary angle), and an optional <see cref="Mirror"/> across the symbol's local Y axis.
///
/// <para>The transform is EXACT — a quarter turn is a sign swap, never a <c>cos</c> (the
/// glTF/packing rule) — so a pin's world anchor is computed with no round-off and coincides
/// with its wire endpoint to the bit.</para>
/// </summary>
/// <param name="Position">The symbol origin on the sheet, mm.</param>
/// <param name="QuarterTurns">Counter-clockwise rotation in units of 90°.</param>
/// <param name="Mirror">Reflect the symbol across its own vertical (local Y) axis.</param>
public readonly record struct SymbolPose(Vector2d Position, int QuarterTurns = 0, bool Mirror = false)
{
    /// <summary>Maps a point from the symbol's own frame onto the sheet.</summary>
    public Vector2d Apply(in Vector2d p) => Position + Linear(p);

    /// <summary>Maps a direction (no translation) from the symbol's frame onto the sheet.</summary>
    public Vector2d ApplyDirection(in Vector2d v) => Linear(v);

    private Vector2d Linear(in Vector2d p)
    {
        var m = Mirror ? new Vector2d(-p.X, p.Y) : p;   // reflect across local Y
        return (((QuarterTurns % 4) + 4) % 4) switch    // exact quarter turns: sign swaps only
        {
            0 => m,
            1 => new Vector2d(-m.Y, m.X),
            2 => new Vector2d(-m.X, -m.Y),
            _ => new Vector2d(m.Y, -m.X),
        };
    }
}

/// <summary>
/// Where every component's symbol is placed on the sheet, keyed by reference designator — a
/// value passed to a <see cref="SchematicSheet"/>. Placement is HAND-DONE in v1 (a real
/// auto-placer that produces a good layout is its own problem, filed rather than invented);
/// <see cref="Grid"/> is a deterministic grid PLACEHOLDER, clearly labelled as such.
/// </summary>
public sealed class SchematicPlacement
{
    // Poses keyed by (reference designator, 1-based UNIT number). A single-unit component uses unit 1,
    // so Place(refdes, pose) stores (refdes, 1) and PoseOf(refdes) reads it back — the whole existing
    // single-unit API is unchanged. A MULTI-unit part (a dual op-amp) places EACH unit separately, so a
    // net between two amps of one package draws as two symbols wired together.
    private readonly Dictionary<(string Reference, int Unit), SymbolPose> _poses = [];

    /// <summary>Places a single-unit component's symbol — equivalently, unit 1 of any component.</summary>
    /// <returns>This placement, so calls chain.</returns>
    public SchematicPlacement Place(string referenceDesignator, in SymbolPose pose) =>
        Place(referenceDesignator, 1, pose);

    /// <summary>Places UNIT <paramref name="unit"/> (1-based) of a component — the multi-unit form (a
    /// dual op-amp places unit 1 and unit 2 at their own poses).</summary>
    public SchematicPlacement Place(string referenceDesignator, int unit, in SymbolPose pose)
    {
        ArgumentException.ThrowIfNullOrEmpty(referenceDesignator);
        if (unit < 1)
            throw new ArgumentOutOfRangeException(nameof(unit), "A schematic unit number is 1-based.");
        _poses[(referenceDesignator, unit)] = pose;
        return this;
    }

    /// <summary>Places a single-unit component's symbol at a position, optionally turned or mirrored.</summary>
    public SchematicPlacement Place(
        string referenceDesignator, in Vector2d position, int quarterTurns = 0, bool mirror = false) =>
        Place(referenceDesignator, 1, new SymbolPose(position, quarterTurns, mirror));

    /// <summary>Places unit <paramref name="unit"/> (1-based) at a position, optionally turned or mirrored.</summary>
    public SchematicPlacement Place(
        string referenceDesignator, int unit, in Vector2d position, int quarterTurns = 0, bool mirror = false) =>
        Place(referenceDesignator, unit, new SymbolPose(position, quarterTurns, mirror));

    /// <summary>The pose for a component's unit 1 (a single-unit component's symbol), or null.</summary>
    public SymbolPose? PoseOf(string referenceDesignator) => PoseOf(referenceDesignator, 1);

    /// <summary>The pose for UNIT <paramref name="unit"/> (1-based) of a component, or null.</summary>
    public SymbolPose? PoseOf(string referenceDesignator, int unit) =>
        _poses.TryGetValue((referenceDesignator, unit), out var pose) ? pose : null;

    /// <summary>The reference designators this placement covers.</summary>
    public IReadOnlyCollection<string> References =>
        [.. _poses.Keys.Select(k => k.Reference).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// A deterministic GRID auto-placement — a labelled PLACEHOLDER, not a good layout. Symbols
    /// are laid left-to-right, top-to-bottom in the drawing area at <paramref name="spacing"/>
    /// apart. Use it to see a schematic at all; a readable layout is hand-placed (or waits on a
    /// real auto-placer, which is a different problem).
    /// </summary>
    /// <param name="schematic">The schematic to place.</param>
    /// <param name="format">The paper, so the grid fits the drawing area.</param>
    /// <param name="spacing">Centre-to-centre spacing, mm.</param>
    /// <param name="columns">Columns; the default is roughly square.</param>
    public static SchematicPlacement Grid(
        Schematic schematic, SheetFormat? format = null, double spacing = 25.4, int? columns = null)
    {
        ArgumentNullException.ThrowIfNull(schematic);
        var paper = format ?? SchematicSheet.DefaultFormat;

        // One cell per (component, UNIT) — a multi-unit part gets a cell per unit. A schematic of only
        // single-unit components flattens to one cell per component in order, so its grid is unchanged.
        var cells = new List<(string Reference, int Unit)>();
        foreach (var c in schematic.Components)
        {
            int unitCount = Math.Max(1, c.Definition.Units.Count);
            for (int u = 1; u <= unitCount; u++)
                cells.Add((c.ReferenceDesignator, u));
        }

        int n = cells.Count;
        int cols = columns ?? Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n)));
        double margin = SchematicSheet.DefaultMargin;
        double left = margin + spacing / 2;
        double top = paper.Height - margin - spacing / 2;

        var placement = new SchematicPlacement();
        for (int i = 0; i < n; i++)
        {
            int col = i % cols, row = i / cols;
            placement.Place(
                cells[i].Reference, cells[i].Unit,
                new SymbolPose(new Vector2d(left + col * spacing, top - row * spacing)));
        }
        return placement;
    }
}

/// <summary>
/// The rules a <see cref="SchematicSheet"/> draws by — chiefly WHICH nets are drawn as labels
/// rather than wires, plus a few sizes. A record so a settings value can be shared.
/// </summary>
public sealed record SchematicSheetOptions
{
    /// <summary>A net with more pins than this is drawn as LABELS at each pin rather than one
    /// long wire (a high-fanout net crossing the sheet is unreadable as a wire). Default 4.</summary>
    public int FanoutThreshold { get; init; } = 4;

    /// <summary>Net names recognised as power/ground rails and therefore drawn as labels
    /// (case-insensitive). A net is ALSO a rail when any of its pins is <see cref="PinType.Power"/>
    /// or <see cref="PinType.Ground"/>, so an unnamed rail is caught too.</summary>
    public IReadOnlySet<string> PowerNetNames { get; init; } = DefaultPowerNetNames;

    /// <summary>Junction-dot diameter, mm.</summary>
    public double JunctionDotDiameter { get; init; } = 1.0;

    /// <summary>Bus-wire pen width, mm — wider than a signal wire's (0.5) so a declared bus reads
    /// as a bundle. Only affects sheets that declare a <see cref="SchematicBus"/>; a bus-free
    /// sheet is byte-identical whatever this is set to.</summary>
    public double BusWireWidth { get; init; } = 0.8;

    /// <summary>Text cap height for reference designators, values and net labels, mm.</summary>
    public double TextHeight { get; init; } = 2.0;

    /// <summary>Length of the short wire stub a net label leaves its pin on, mm.</summary>
    public double LabelStubLength { get; init; } = 2.54;

    /// <summary>Draw component values under each symbol.</summary>
    public bool ShowValues { get; init; } = true;

    /// <summary>The default power/ground net names, a superset of the common rails. Declared
    /// BEFORE <see cref="Default"/> so the static default captures the initialised set rather
    /// than null (static field initialisers run in textual order).</summary>
    public static IReadOnlySet<string> DefaultPowerNetNames { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GND", "GNDA", "GNDD", "AGND", "DGND", "PGND", "VSS", "VSSA",
            "VCC", "VDD", "VDDA", "VEE", "VBAT", "VBUS", "VIN", "VOUT",
            "3V3", "+3V3", "+3.3V", "5V", "+5V", "+12V", "-12V", "+15V", "-15V",
            "V+", "V-",
        };

    /// <summary>The default rule set.</summary>
    public static SchematicSheetOptions Default { get; } = new();
}

/// <summary>
/// A drawn schematic SHEET: a <see cref="Schematic"/>, a <see cref="SchematicPlacement"/>, a
/// paper size and a title block. Call <see cref="Draw"/> for the rendered
/// <see cref="SchematicDrawing"/> (SVG/DXF/PDF).
///
/// <para><b>This is the human-readable view the ECAD campaign owed</b> — it replaces
/// <see cref="Netlist.ToText"/> as the way to look at a schematic — and it is deliberately a
/// VIEW of the graph, derived so it cannot disagree with the netlist (the one-declaration
/// rule). Everything drawn is a function of the graph and the placement.</para>
///
/// <para><b>Multi-unit parts draw as SEPARATE unit symbols.</b> A dual op-amp (a
/// <see cref="PartDefinition"/> with several <see cref="PartDefinition.Units"/>) places EACH unit at its
/// own sheet location (<see cref="SchematicPlacement.Place(string, int, in SymbolPose)"/>), labelled
/// <c>U1A</c>/<c>U1B</c>/…; each pin is drawn by the unit whose symbol carries it, so a net between two
/// amps of one package draws as two symbols wired together. Because the connectivity reconstruction reads
/// the DRAWN wire geometry, it is unit-agnostic — a single-unit part places and draws exactly as before
/// (byte-identical).</para>
///
/// <para><b>What it refuses, by name.</b> A component with no <see cref="PartDefinition.Symbol"/>
/// cannot be drawn; a net that connects a pin NO unit of the component draws (the symbol and the netlist
/// disagreeing about the part's pins — a <see cref="PinIdentity"/> failure); a component whose every UNIT
/// the placement does not cover. All are refused at construction, naming the offenders.</para>
/// </summary>
public sealed class SchematicSheet
{
    /// <summary>The default paper: A4 landscape.</summary>
    public static SheetFormat DefaultFormat => SheetFormat.A4;

    /// <summary>Distance from the paper edge to the border, mm.</summary>
    public const double DefaultMargin = 10;

    private readonly SchematicPlacement _placement;
    private readonly SchematicSheetOptions _options;
    private readonly IReadOnlyList<SchematicBus> _buses;

    /// <summary>Builds a sheet. A null placement grid-places every component (a labelled
    /// placeholder — see <see cref="SchematicPlacement.Grid"/>); a null title defaults its title
    /// to the schematic's name.</summary>
    /// <param name="schematic">The schematic to draw.</param>
    /// <param name="placement">Where each symbol sits (null grid-places every component).</param>
    /// <param name="format">The paper (null = A4 landscape).</param>
    /// <param name="title">The title block (null defaults its title to the schematic's name).</param>
    /// <param name="options">The drawing rules (null = the defaults).</param>
    /// <param name="standards">Opt-in sheet-standard furniture (null = none).</param>
    /// <param name="buses">Caller-declared <see cref="SchematicBus"/>es to draw ON TOP of the
    /// wires — DRAWING SUGAR that connects nothing. Null (the default) draws no buses and leaves
    /// the sheet's output byte-identical.</param>
    /// <exception cref="ArgumentException">A component has no symbol, a net connects a pin the
    /// symbol does not draw, or the placement does not cover a component — each named.</exception>
    public SchematicSheet(
        Schematic schematic,
        SchematicPlacement? placement = null,
        SheetFormat? format = null,
        TitleBlock? title = null,
        SchematicSheetOptions? options = null,
        FrameStandards? standards = null,
        IReadOnlyList<SchematicBus>? buses = null)
    {
        ArgumentNullException.ThrowIfNull(schematic);
        Schematic = schematic;
        Format = format ?? DefaultFormat;
        _options = options ?? SchematicSheetOptions.Default;
        _placement = placement ?? SchematicPlacement.Grid(schematic, Format);
        Title = title ?? new TitleBlock { Title = schematic.Name };
        Standards = standards ?? FrameStandards.None;
        _buses = buses is null ? [] : [.. buses];
        Validate();
    }

    /// <summary>The schematic this draws.</summary>
    public Schematic Schematic { get; }

    /// <summary>The paper.</summary>
    public SheetFormat Format { get; }

    /// <summary>The title block (its title defaults to the schematic's name).</summary>
    public TitleBlock Title { get; }

    /// <summary>Opt-in sheet-standard furniture (ISO 5457 zone grid and centring marks);
    /// default <see cref="FrameStandards.None"/> leaves the sheet's output byte-identical.</summary>
    public FrameStandards Standards { get; }

    /// <summary>The caller-declared buses drawn on this sheet (drawing sugar; connects nothing).
    /// Empty by default, which leaves the sheet's output byte-identical.</summary>
    public IReadOnlyList<SchematicBus> Buses => _buses;

    /// <summary>
    /// The shared <see cref="DrawingFrame"/> this sheet draws its border and title block from —
    /// the SAME frame value the mechanical <see cref="DrawingSheet"/> uses, so the two cannot
    /// disagree about what a frame looks like. A schematic configures the two-band schematic
    /// title block on the ECAD schematic layers; that is the only thing that differs.
    /// </summary>
    public DrawingFrame Frame() => new()
    {
        Format = Format,
        Margin = DefaultMargin,
        Title = Title,
        BorderLayer = SchematicLayers.Border,
        TitleBlockLayer = SchematicLayers.TitleBlock,
        Layout = new SchematicTitleBlock(_options.TextHeight),
        Standards = Standards,
    };

    /// <summary>Refuses, by name, everything that would make the sheet undrawable.</summary>
    private void Validate()
    {
        var noSymbol = new List<string>();
        var unplaced = new List<string>();
        foreach (var component in Schematic.Components)
        {
            if (component.Definition.Symbol is null)
            {
                noSymbol.Add($"{component.ReferenceDesignator} ({component.Definition.Name})");
                continue;
            }
            // EVERY unit must have a sheet position — a multi-unit part places each unit separately, so
            // "U1" naming just unit A is not enough. A single-unit part checks unit 1, unchanged.
            var units = component.Definition.Units;
            bool multi = units.Count > 1;
            for (int u = 1; u <= units.Count; u++)
                if (_placement.PoseOf(component.ReferenceDesignator, u) is null)
                    unplaced.Add(multi
                        ? $"{component.ReferenceDesignator}{(char)('A' + u - 1)}"
                        : component.ReferenceDesignator);
        }
        if (noSymbol.Count > 0)
            throw new ArgumentException(
                "These components have no schematic symbol and cannot be drawn: "
                + string.Join(", ", noSymbol)
                + ". Attach a Symbol to each PartDefinition (ComponentLibrary imports one from KiCad), "
                + "or remove them from the sheet.");
        if (unplaced.Count > 0)
            throw new ArgumentException(
                "These components have no sheet position: " + string.Join(", ", unplaced)
                + ". Place them (SchematicPlacement.Place), or pass a null placement to grid-place all.");

        // A net that names a pin NO unit of the component draws is the "pin with no anchor" case — the
        // symbol(s) and the netlist disagree about the part's pins. A multi-unit part draws a pin on
        // whichever unit carries it, so the check is over the UNION of the units' pins.
        var noAnchor = new List<string>();
        foreach (var net in Schematic.Nets)
            foreach (var pin in net.Pins)
                if (pin.Component.Definition.Symbol is { } symbol
                    && !pin.Component.Definition.Units.Any(s => s.HasPin(pin.Number)))
                    noAnchor.Add(
                        $"net '{net.Name}' connects {pin}, but {pin.ReferenceDesignator}'s symbol "
                        + $"('{symbol.Name}') draws no pin '{pin.Number}'");
        if (noAnchor.Count > 0)
            throw new ArgumentException(
                "A wire cannot land on a pin the symbol does not draw (a PinIdentity mismatch): "
                + string.Join("; ", noAnchor) + ".");
    }

    /// <summary>Renders the sheet — the deterministic function of the graph and the placement.</summary>
    public SchematicDrawing Draw() => new SheetBuilder(this, _placement, _options).Build();

    // ------------------------------------------------------------------------------------
    // The builder: layout, symbol rendering, wire routing, labels and the title block. It is
    // a separate object so Draw() can hold no mutable state and stay a pure function.
    // ------------------------------------------------------------------------------------
    private sealed class SheetBuilder
    {
        private const int CircleSegments = 48;

        private readonly SchematicSheet _sheet;
        private readonly SchematicPlacement _placement;
        private readonly SchematicSheetOptions _options;

        private readonly List<(Vector2d A, Vector2d B, string Layer)> _segments = [];
        private readonly List<(Vector2d A, Vector2d B)> _wires = [];
        private readonly List<SheetText> _texts = [];
        private readonly List<DrawnPin> _pins = [];
        private readonly List<DrawnBus> _buses = [];

        internal SheetBuilder(SchematicSheet sheet, SchematicPlacement placement, SchematicSheetOptions options)
        {
            _sheet = sheet;
            _placement = placement;
            _options = options;
        }

        internal SchematicDrawing Build()
        {
            // The border and title block come from the shared DrawingFrame, so a schematic and
            // a mechanical drawing of one project draw the same furniture. Merged AHEAD of the
            // symbols and wires, as the frame's own line work always was.
            var frame = _sheet.Frame().Compute();
            _segments.AddRange(frame.Lines);
            _texts.AddRange(frame.Texts);

            foreach (var component in _sheet.Schematic.Components)
                DrawSymbol(component);
            // Nets after symbols so wires overlay bodies where they must.
            foreach (var net in _sheet.Schematic.Nets)
                DrawNet(net);
            // Buses last, drawn ON TOP of the wires they bundle. They are DRAWING SUGAR: their
            // geometry never touches the wire graph (_wires), so a bus wire crossing two member
            // wires cannot merge their nets — which is what leaves Verify() unaffected.
            foreach (var bus in _sheet.Buses)
                DrawBus(bus);
            return new SchematicDrawing(
                _sheet.Schematic, _sheet.Format, _segments, _wires, _texts, _pins, _buses,
                _options.JunctionDotDiameter, _options.BusWireWidth);
        }

        // ---- symbols --------------------------------------------------------

        private void DrawSymbol(Component component)
        {
            // Each UNIT of the part is a separate symbol at its own pose (a dual op-amp draws as two
            // amp symbols wired together). A single-unit part has one unit, drawn at the refdes pose with
            // its bare refdes — so its output is byte-identical.
            var units = component.Definition.Units;
            bool multi = units.Count > 1;
            for (int u = 0; u < units.Count; u++)
            {
                var pose = _placement.PoseOf(component.ReferenceDesignator, u + 1)!.Value;
                // Multi-unit units are labelled "U1A", "U1B", … (the KiCad convention); a single-unit
                // part keeps its bare refdes. The value is drawn once (under the first unit).
                string label = multi ? component.ReferenceDesignator + (char)('A' + u) : component.ReferenceDesignator;
                DrawUnit(component, units[u], pose, label, showValue: u == 0);
            }
        }

        private void DrawUnit(
            Component component, Symbol symbol, in SymbolPose pose, string label, bool showValue)
        {
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            void Grow(Vector2d p)
            {
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
            }

            foreach (var graphic in symbol.Graphics)
                DrawGraphic(graphic, pose, Grow);

            foreach (var pin in symbol.Pins)
            {
                var anchor = pose.Apply(pin.Anchor);
                var inner = pose.Apply(pin.Inner);
                _segments.Add((anchor, inner, SchematicLayers.Pins));
                Grow(anchor);
                Grow(inner);
            }

            // A part with no body graphics still needs a footprint for the labels.
            if (double.IsInfinity(minX))
            {
                var origin = pose.Apply(Vector2d.Zero);
                minX = maxX = origin.X;
                minY = maxY = origin.Y;
            }

            double gap = _options.TextHeight;
            double cx = (minX + maxX) / 2;
            _texts.Add(new SheetText(
                new Vector2d(cx, maxY + gap), label, _options.TextHeight,
                SheetTextAnchor.Center, SchematicLayers.References));
            if (showValue && _options.ShowValues && component.Value.Length > 0)
                _texts.Add(new SheetText(
                    new Vector2d(cx, minY - gap - _options.TextHeight), component.Value, _options.TextHeight,
                    SheetTextAnchor.Center, SchematicLayers.Values));
        }

        /// <summary>The 1-based unit number and symbol that draw a pin — the unit whose symbol carries it.
        /// For a single-unit part this is always (1, the symbol).</summary>
        private static (int Unit, Symbol Symbol) UnitOf(Component component, string pinNumber)
        {
            var units = component.Definition.Units;
            for (int i = 0; i < units.Count; i++)
                if (units[i].HasPin(pinNumber))
                    return (i + 1, units[i]);
            return (1, component.Definition.Symbol!);   // Validate() proved some unit draws it
        }

        private void DrawGraphic(SymbolGraphic graphic, in SymbolPose pose, Action<Vector2d> grow)
        {
            switch (graphic)
            {
                case SymbolPolyline polyline:
                    for (int i = 0; i + 1 < polyline.Points.Count; i++)
                    {
                        var a = pose.Apply(polyline.Points[i]);
                        var b = pose.Apply(polyline.Points[i + 1]);
                        _segments.Add((a, b, SchematicLayers.Symbols));
                        grow(a);
                        grow(b);
                    }
                    break;
                case SymbolRectangle rectangle:
                {
                    var corners = new[]
                    {
                        pose.Apply(rectangle.Min),
                        pose.Apply(new Vector2d(rectangle.Max.X, rectangle.Min.Y)),
                        pose.Apply(rectangle.Max),
                        pose.Apply(new Vector2d(rectangle.Min.X, rectangle.Max.Y)),
                    };
                    for (int i = 0; i < 4; i++)
                    {
                        _segments.Add((corners[i], corners[(i + 1) % 4], SchematicLayers.Symbols));
                        grow(corners[i]);
                    }
                    break;
                }
                case SymbolCircle circle:
                {
                    var previous = pose.Apply(circle.Center + new Vector2d(circle.Radius, 0));
                    grow(previous);
                    for (int i = 1; i <= CircleSegments; i++)
                    {
                        double angle = 2 * Math.PI * i / CircleSegments;
                        var point = pose.Apply(circle.Center
                            + new Vector2d(Math.Cos(angle), Math.Sin(angle)) * circle.Radius);
                        _segments.Add((previous, point, SchematicLayers.Symbols));
                        grow(point);
                        previous = point;
                    }
                    break;
                }
                case SymbolArc arc:
                    foreach (var (a, b) in ArcSegments(arc, pose))
                    {
                        _segments.Add((a, b, SchematicLayers.Symbols));
                        grow(a);
                        grow(b);
                    }
                    break;
                case SymbolText text:
                    _texts.Add(new SheetText(
                        pose.Apply(text.At), text.Value, text.Size, SheetTextAnchor.Center,
                        SchematicLayers.Symbols));
                    grow(pose.Apply(text.At));
                    break;
            }
        }

        /// <summary>An arc-through-three-points as world segments: the circle through the three
        /// points sampled from start through mid to end, or the chord when the three are
        /// collinear.</summary>
        private static IEnumerable<(Vector2d A, Vector2d B)> ArcSegments(SymbolArc arc, SymbolPose pose)
        {
            var start = arc.Start;
            var mid = arc.Mid;
            var end = arc.End;
            double d = 2 * (start.X * (mid.Y - end.Y) + mid.X * (end.Y - start.Y) + end.X * (start.Y - mid.Y));
            if (Math.Abs(d) < 1e-12)
            {
                yield return (pose.Apply(start), pose.Apply(end));
                yield break;
            }
            double sq(in Vector2d p) => p.X * p.X + p.Y * p.Y;
            double ux = (sq(start) * (mid.Y - end.Y) + sq(mid) * (end.Y - start.Y) + sq(end) * (start.Y - mid.Y)) / d;
            double uy = (sq(start) * (end.X - mid.X) + sq(mid) * (start.X - end.X) + sq(end) * (mid.X - start.X)) / d;
            var center = new Vector2d(ux, uy);
            double radius = center.DistanceTo(start);
            double a0 = Math.Atan2(start.Y - center.Y, start.X - center.X);
            double am = Math.Atan2(mid.Y - center.Y, mid.X - center.X);
            double a1 = Math.Atan2(end.Y - center.Y, end.X - center.X);

            double ccw = a1 - a0;
            while (ccw <= 0) ccw += 2 * Math.PI;
            double midOffset = am - a0;
            while (midOffset < 0) midOffset += 2 * Math.PI;
            while (midOffset >= 2 * Math.PI) midOffset -= 2 * Math.PI;
            double sweep = midOffset <= ccw ? ccw : ccw - 2 * Math.PI;

            int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 24)));
            var previous = pose.Apply(start);
            for (int i = 1; i <= steps; i++)
            {
                double angle = a0 + sweep * i / steps;
                var point = pose.Apply(center + new Vector2d(Math.Cos(angle), Math.Sin(angle)) * radius);
                yield return (previous, point);
                previous = point;
            }
        }

        // ---- nets: wires and labels -----------------------------------------

        private void DrawNet(Net net)
        {
            if (net.Kind == NetKind.NoConnect)
            {
                foreach (var pin in net.Pins)
                    DrawNoConnect(pin);
                return;
            }
            if (net.Kind is not (NetKind.Signal or NetKind.Stub))
                return;

            var anchors = net.Pins.Select(AnchorOf).ToList();

            // The net-label rule: a power/ground rail (a pin typed Power/Ground, or a recognised
            // rail name) or a high-fanout net is drawn as LABELS at each pin rather than one long
            // wire across the sheet; a net with fewer than two pins cannot be wired at all.
            if (net.Pins.Count < 2 || IsRail(net) || net.Pins.Count > _options.FanoutThreshold)
            {
                for (int i = 0; i < net.Pins.Count; i++)
                {
                    DrawLabel(net.Pins[i], anchors[i], net.Name);
                    _pins.Add(new DrawnPin(net.Pins[i], anchors[i], net.Name));
                }
                return;
            }

            RouteWires(anchors);
            for (int i = 0; i < net.Pins.Count; i++)
                _pins.Add(new DrawnPin(net.Pins[i], anchors[i], null));
        }

        private bool IsRail(Net net) =>
            _options.PowerNetNames.Contains(net.Name)
            || net.Pins.Any(p => p.Pin.Type is PinType.Power or PinType.Ground);

        /// <summary>The world anchor of a terminal — the point where its wire lands.</summary>
        private Vector2d AnchorOf(PinRef pin)
        {
            // Resolve which UNIT draws the pin, and use THAT unit's pose and symbol — so a pin of a
            // dual op-amp's second amp anchors on the second symbol. Single-unit reduces to unit 1.
            var (unit, symbol) = UnitOf(pin.Component, pin.Number);
            var pose = _placement.PoseOf(pin.ReferenceDesignator, unit)!.Value;
            return pose.Apply(symbol.PinNumbered(pin.Number).Anchor);   // Validate() proved the pin exists
        }

        /// <summary>The direction a wire leaves a pin's anchor — away from the body, opposite the
        /// symbol pin's <see cref="SymbolPinDirection"/> (which points INTO the body).</summary>
        private Vector2d LeaveDirection(PinRef pin)
        {
            var (unit, symbol) = UnitOf(pin.Component, pin.Number);
            var pose = _placement.PoseOf(pin.ReferenceDesignator, unit)!.Value;
            return pose.ApplyDirection(-symbol.PinNumbered(pin.Number).Direction.ToVector());
        }

        /// <summary>
        /// Orthogonal (Manhattan) routing that connects a net's pins so the drawn wire graph
        /// connects exactly them. Two pins take an L (horizontal then vertical); three or more
        /// take a horizontal TRUNK at the pins' median y with a vertical stub from each pin, so
        /// interior stubs make T-junctions the dot renderer marks. It is a small SCHEMATIC
        /// router — no layers, no clearance — and v1 may cross symbols or other nets (a crossing
        /// is not a connection); a good obstacle-avoiding route is a separate problem.
        /// </summary>
        private void RouteWires(IReadOnlyList<Vector2d> anchors)
        {
            if (anchors.Count == 2)
            {
                var a = anchors[0];
                var b = anchors[1];
                var corner = new Vector2d(b.X, a.Y);   // horizontal-first L
                AddWire(a, corner);
                AddWire(corner, b);
                return;
            }

            // Trunk at the median y (an exact copy of a pin's y, so a pin at that level has an
            // exact zero-length stub), with a vertical stub from each pin to it.
            var ys = anchors.Select(p => p.Y).OrderBy(y => y).ToList();
            double trunkY = ys[(ys.Count - 1) / 2];
            foreach (var anchor in anchors)
                AddWire(anchor, new Vector2d(anchor.X, trunkY));

            var xs = anchors.Select(p => p.X).Distinct().OrderBy(x => x).ToList();
            for (int i = 0; i + 1 < xs.Count; i++)
                AddWire(new Vector2d(xs[i], trunkY), new Vector2d(xs[i + 1], trunkY));
        }

        /// <summary>Adds a net wire (dropping a zero-length segment, which would only litter the
        /// junction count) to both the render list and the connectivity list.</summary>
        private void AddWire(in Vector2d a, in Vector2d b)
        {
            if (a == b)
                return;
            _segments.Add((a, b, SchematicLayers.Wires));
            _wires.Add((a, b));
        }

        /// <summary>Draws a net label: a short cosmetic stub leaving the pin along its own
        /// direction, and the net name at the stub's end. The stub is NOT part of the wire graph
        /// — a labelled pin joins its net by the shared label, which is what keeps a rail from
        /// being one wire across the whole sheet.</summary>
        private void DrawLabel(PinRef pin, in Vector2d anchor, string name)
        {
            var direction = LeaveDirection(pin);
            var end = anchor + direction * _options.LabelStubLength;
            _segments.Add((anchor, end, SchematicLayers.Wires));

            double gap = _options.TextHeight * 0.4;
            SheetTextAnchor textAnchor;
            Vector2d position;
            if (Math.Abs(direction.X) >= Math.Abs(direction.Y))
            {
                bool rightward = direction.X >= 0;
                textAnchor = rightward ? SheetTextAnchor.Left : SheetTextAnchor.Right;
                position = end + new Vector2d((rightward ? 1 : -1) * gap, -_options.TextHeight / 2);
            }
            else
            {
                textAnchor = SheetTextAnchor.Center;
                bool upward = direction.Y >= 0;
                position = end + new Vector2d(0, upward ? gap : -gap - _options.TextHeight);
            }
            _texts.Add(new SheetText(position, name, _options.TextHeight, textAnchor, SchematicLayers.Labels));
        }

        /// <summary>Draws a deliberate no-connect marker — an X at the pin's anchor.</summary>
        private void DrawNoConnect(PinRef pin)
        {
            var anchor = AnchorOf(pin);
            double r = _options.TextHeight * 0.6;
            _segments.Add((anchor + new Vector2d(-r, -r), anchor + new Vector2d(r, r), SchematicLayers.NoConnects));
            _segments.Add((anchor + new Vector2d(-r, r), anchor + new Vector2d(r, -r), SchematicLayers.NoConnects));
        }

        /// <summary>Records a caller-declared bus as ONE <see cref="DrawnBus"/> — the bundle wire,
        /// its diagonal entry stubs and its vector label — and puts the label on the text list so
        /// the writers render it. It deliberately touches NEITHER <c>_wires</c> (the wire graph
        /// Verify reads) nor <c>_segments</c> (the writers draw the bus geometry from the
        /// <see cref="DrawnBus"/> directly, with the bus wire's own thick pen), so a bus connects
        /// nothing and cannot be confused with a signal wire.</summary>
        private void DrawBus(SchematicBus bus)
        {
            var entries = bus.Entries.Select(e => (e.OnBus, e.OnWire)).ToList();
            var label = new SheetText(
                bus.LabelPosition, bus.Label, _options.TextHeight, bus.LabelAnchor, SchematicLayers.Bus);
            _texts.Add(label);
            _buses.Add(new DrawnBus(bus.Name, bus.Members, [.. bus.Path], entries, label));
        }

    }
}
