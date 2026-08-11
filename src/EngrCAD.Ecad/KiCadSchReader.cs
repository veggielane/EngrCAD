using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>
/// A whole KiCad schematic read from a <c>.kicad_sch</c> file: the reconstructed
/// <see cref="Schematic"/> (components + nets — the connectivity) and the diagnostics naming
/// anything the reader approximated or could not carry.
///
/// <para>The board twin already exists (<see cref="KiCadPcbReader"/>); this is the SCHEMATIC
/// member of the KiCad interchange set. Unlike a board, a schematic's connectivity is not
/// listed anywhere — it is IMPLICIT IN THE GEOMETRY (which wire ends touch which pins, which
/// junction joins which crossing) — so the reader RECONSTRUCTS it. That is what makes "the
/// imported nets match what the schematic draws" a checkable claim: a mis-read wire would show
/// up as a net joining the wrong pins.</para>
/// </summary>
/// <param name="Schematic">The reconstructed schematic — <see cref="Component"/>s from the embedded
/// <c>lib_symbols</c>, connected into <see cref="Net"/>s reconstructed from the wire geometry.</param>
/// <param name="SheetName">The sheet title (the title block's title, or <c>"schematic"</c>).</param>
/// <param name="Diagnostics">What the reader approximated or could not carry — the
/// <c>StepReader.Diagnostics</c> / <c>IdfReader</c> convention.</param>
public sealed record KiCadSchematic(
    Schematic Schematic, string SheetName, IReadOnlyList<string> Diagnostics)
{
    /// <summary>The number of placed components (power symbols are net-name markers, not
    /// components, and are not counted).</summary>
    public int ComponentCount => Schematic.Components.Count;

    /// <summary>The number of nets (signal, stub and no-connect).</summary>
    public int NetCount => Schematic.Nets.Count;

    /// <summary>The reconstructed schematic (parity with <c>KiCadPcb.ToLayout</c>).</summary>
    public Schematic ToSchematic() => Schematic;
}

/// <summary>
/// Reads a whole KiCad schematic (<c>.kicad_sch</c>) into a <see cref="KiCadSchematic"/> — the
/// schematic twin of the KiCad board reader (<see cref="KiCadPcbReader"/>), reusing the same
/// hand-rolled dependency-free S-expression parser (<see cref="SExpr"/>), the same symbol-parsing
/// core (<see cref="KiCadSymbolReader.ParseSymbolList"/> over the embedded <c>lib_symbols</c>), and
/// the same covered-subset / refuse-by-name discipline (the <c>StepReader</c>/<c>IgesReader</c>
/// ethos).
///
/// <para><b>The crux is that connectivity is GEOMETRY, not a list.</b> A schematic never states
/// its netlist; it draws it. So the reader reconstructs the nets with a UNION-FIND over the
/// connection POINTS, the same "two things are one net iff they touch" rule
/// <see cref="PcbConnectivity"/> uses on copper:</para>
/// <list type="bullet">
///   <item>a WIRE joins its two endpoints;</item>
///   <item>a component PIN anchor, a LABEL, a POWER-symbol pin or a JUNCTION lying ON a wire
///   joins that wire (a junction at a crossing therefore joins BOTH crossing wires);</item>
///   <item>two points carrying the same net LABEL are one net (label equivalence);</item>
///   <item>a <c>no_connect</c> marks an isolated pin as deliberately unconnected.</item>
/// </list>
/// <para>Points coincide at a weld tolerance (see the class constant), because KiCad coordinates
/// are exact grid decimals and a pin anchor is an exact isometry of them. Crucially, two wires
/// CROSSING mid-segment with NO junction are NOT joined (the junction dot is the schematic
/// convention) — the reader inverts exactly the rule <c>SchematicDrawing.Verify</c> asserts.</para>
///
/// <para><b>Covered:</b> a single sheet's embedded <c>lib_symbols</c> (mapped to
/// <see cref="PartDefinition"/>s), placed <c>(symbol …)</c> instances (Reference → refdes, Value →
/// value, <c>lib_id</c> → definition), power symbols (their <c>Value</c> is the net name),
/// <c>wire</c>, <c>junction</c>, local <c>label</c>, <c>global_label</c>, and <c>no_connect</c>.</para>
///
/// <para><b>Refused BY NAME</b> (out of v1 scope, filed as follow-ups): buses (<c>bus</c>,
/// <c>bus_entry</c>, and bus-vector labels like <c>D[7..0]</c>), hierarchical sheets (<c>sheet</c>
/// subsheets, <c>hierarchical_label</c>). A netless wire, an instance referencing an unknown
/// symbol, or a dangling pin is REPORTED (a diagnostic), not thrown (the readers-never-throw
/// culture). A non-<c>(kicad_sch …)</c> root — including a <c>.kicad_pcb</c> board or a
/// <c>.kicad_sym</c> handed here — or a malformed S-expression is refused by name.</para>
/// </summary>
public static class KiCadSchReader
{
    /// <summary>The point-coincidence tolerance (mm). KiCad schematic coordinates are exact grid
    /// decimals (the placement grid is never finer than ~0.0254 mm) and a computed pin anchor is
    /// an exact isometry of such decimals, so points that should coincide differ only by IEEE
    /// round-off (~1e-13 mm). <c>1e-4</c> mm is far above that round-off and far below the coarsest
    /// real connection spacing, so it welds exactly the points that are the same point.</summary>
    private const double WeldMm = 1e-4;

    /// <summary>Reads a <c>.kicad_sch</c> file's text into a <see cref="KiCadSchematic"/>.</summary>
    /// <param name="text">The <c>.kicad_sch</c> file text.</param>
    /// <exception cref="FormatException">The file is not a KiCad schematic, its S-expression is
    /// malformed, or it uses a construct out of v1 scope (buses / hierarchical sheets) — refused
    /// by name.</exception>
    public static KiCadSchematic Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var root = SExpr.Parse(text);
        if (!string.Equals(root.Head, "kicad_sch", StringComparison.Ordinal))
            throw new FormatException(
                $"Not a KiCad schematic: the top S-expression is '{root.Head ?? "?"}', expected "
                + "'kicad_sch'. (A '.kicad_pcb' board is read by KiCadPcbReader; a '.kicad_sym' symbol "
                + "library or '.kicad_mod' footprint by KiCadSymbolReader / KiCadFootprintReader.)");

        RefuseOutOfScope(root);

        var diagnostics = new List<string>();
        var once = new HashSet<string>(StringComparer.Ordinal);
        void Note(string message) { if (once.Add(message)) diagnostics.Add(message); }

        Note("KiCad stores schematic coordinates Y-downward; wire/junction/label points are "
            + "imported verbatim, and library-local symbol pins (Y-up) are transformed into that "
            + "same sheet frame.");

        var libSymbols = ReadLibSymbols(root, Note);
        string sheetName = ReadSheetName(root);
        var schematic = new Schematic(sheetName);

        // ---- placed instances -> components + pin anchors + power/net-name points ----
        var partDefs = new Dictionary<string, PartDefinition>(StringComparer.Ordinal);
        var pinPoints = new List<(PinRef Pin, Vector2d Anchor)>();
        var labelPoints = new List<LabelPoint>();
        var usedRefs = new HashSet<string>(StringComparer.Ordinal);
        int generated = 0;

        foreach (var instance in root.Lists("symbol"))
        {
            string? libId = instance.List("lib_id")?.Arg(0);
            if (string.IsNullOrEmpty(libId))
            {
                Note("A placed (symbol ...) has no (lib_id ...) and was skipped.");
                continue;
            }
            if (!libSymbols.TryGetValue(libId, out var parsed))
            {
                Note($"A placed symbol references an unknown lib_symbol '{libId}' and was skipped "
                    + "(no matching entry in (lib_symbols ...)).");
                continue;
            }

            var placement = ReadPlacement(instance);

            if (parsed.IsPower)
            {
                // A power symbol names a net: its Value is the net name, placed at its pin anchor.
                string powerNet = InstanceValue(instance) ?? parsed.Value ?? BareName(libId);
                foreach (var symPin in parsed.Symbol.Pins)
                    labelPoints.Add(new LabelPoint(placement.Place(symPin.Anchor), powerNet, LabelPriority.Power));
                continue;
            }

            // An ordinary component.
            string refDes = InstanceReference(instance);
            if (refDes.Length == 0 || refDes == "?")
            {
                do { refDes = $"U{++generated}"; } while (!usedRefs.Add(refDes));
            }
            else if (!usedRefs.Add(refDes))
            {
                string renamed;
                do { renamed = $"{refDes}_{++generated}"; } while (!usedRefs.Add(renamed));
                Note($"Duplicate reference '{refDes}' (a multi-unit symbol?); imported as a "
                    + $"separate component '{renamed}'. Multi-unit symbols are a filed follow-up.");
                refDes = renamed;
            }

            if (!partDefs.TryGetValue(libId, out var definition))
            {
                definition = new PartDefinition(
                    BareName(libId), parsed.ReferencePrefix, parsed.Pins,
                    footprint: null, body: null, symbol: parsed.Symbol);
                partDefs[libId] = definition;
            }

            var component = schematic.Add(refDes, definition, InstanceValue(instance) ?? "");
            foreach (var pin in definition.Pins)
            {
                var symPin = parsed.Symbol.PinNumbered(pin.Number);
                pinPoints.Add((component.Pin(pin.Number), placement.Place(symPin.Anchor)));
            }
        }

        // ---- wires, junctions, labels, no-connects ----------------------------
        var wires = new List<(Vector2d A, Vector2d B)>();
        foreach (var wire in root.Lists("wire"))
            if (TryWire(wire, out var a, out var b))
                wires.Add((a, b));

        var junctions = new List<Vector2d>();
        foreach (var junction in root.Lists("junction"))
            if (TryAt(junction, out var at))
                junctions.Add(at);

        foreach (var label in root.Lists("label"))
            if (TryLabel(label, out var at, out var name))
                labelPoints.Add(new LabelPoint(at, name, LabelPriority.Local));

        foreach (var label in root.Lists("global_label"))
            if (TryLabel(label, out var at, out var name))
                labelPoints.Add(new LabelPoint(at, name, LabelPriority.Global));

        var noConnects = new List<Vector2d>();
        foreach (var nc in root.Lists("no_connect"))
            if (TryAt(nc, out var at))
                noConnects.Add(at);

        // ---- reconstruct the nets (union-find over connection points) ---------
        BuildNets(schematic, pinPoints, labelPoints, wires, junctions, noConnects, Note);

        return new KiCadSchematic(schematic, sheetName, diagnostics);
    }

    /// <summary>Reads a <c>.kicad_sch</c> file from disk.</summary>
    public static KiCadSchematic ReadFile(string path) => Read(File.ReadAllText(path));

    // ======================================================================
    // Net reconstruction — the crux
    // ======================================================================

    private static void BuildNets(
        Schematic schematic,
        List<(PinRef Pin, Vector2d Anchor)> pinPoints,
        List<LabelPoint> labelPoints,
        List<(Vector2d A, Vector2d B)> wires,
        List<Vector2d> junctions,
        List<Vector2d> noConnects,
        Action<string> note)
    {
        var graph = new Graph();

        // Phase 1: every connection point is a node (coincident points share a node by weld key,
        // so two wire ends / a pin on a wire end / a power pin on a component pin all connect
        // even with no wire drawn between them).
        foreach (var (a, b) in wires) { graph.Intern(a); graph.Intern(b); }
        foreach (var j in junctions) graph.Intern(j);
        foreach (var (_, anchor) in pinPoints) graph.Intern(anchor);
        foreach (var lp in labelPoints) graph.Intern(lp.At);
        foreach (var nc in noConnects) graph.Intern(nc);

        // Phase 2: unions.
        // (a) a wire joins its two endpoints.
        foreach (var (a, b) in wires) graph.Union(graph.Intern(a), graph.Intern(b));

        // (b) a junction / pin anchor / label / power pin lying ON a wire joins that wire. A
        // junction at an X-crossing lies mid-segment on BOTH wires, so it joins them — while a
        // plain crossing with no junction stays two nets (no attachment point there). NoConnect
        // points are deliberately NOT attachment points.
        foreach (var p in junctions.Concat(pinPoints.Select(pp => pp.Anchor)).Concat(labelPoints.Select(lp => lp.At)))
            foreach (var (a, b) in wires)
                if (OnSegment(p, a, b))
                    graph.Union(graph.Intern(p), graph.Intern(a));

        // (c) label equivalence — points sharing a net name are one net.
        foreach (var byName in labelPoints.GroupBy(lp => lp.Name, StringComparer.Ordinal))
        {
            using var e = byName.GetEnumerator();
            if (!e.MoveNext()) continue;
            int first = graph.Intern(e.Current.At);
            while (e.MoveNext())
                graph.Union(first, graph.Intern(e.Current.At));
        }

        // ---- resolve nets ----------------------------------------------------
        var anchorOf = new Dictionary<PinRef, Vector2d>();
        var pinsByRoot = new Dictionary<int, List<PinRef>>();
        foreach (var (pin, anchor) in pinPoints)
        {
            anchorOf[pin] = anchor;
            int root = graph.Find(graph.Intern(anchor));
            (pinsByRoot.TryGetValue(root, out var list) ? list : pinsByRoot[root] = []).Add(pin);
        }

        var namesByRoot = new Dictionary<int, List<LabelPoint>>();
        foreach (var lp in labelPoints)
        {
            int root = graph.Find(graph.Intern(lp.At));
            (namesByRoot.TryGetValue(root, out var list) ? list : namesByRoot[root] = []).Add(lp);
        }

        var noConnectKeys = noConnects.Select(Graph.Key).ToHashSet();

        // Deterministic order: by the lexicographically smallest pin in each net.
        var roots = pinsByRoot.Keys.ToList();
        string MinPin(int root) => pinsByRoot[root].Select(p => p.ToString()).Min(StringComparer.Ordinal)!;
        roots.Sort((a, b) => string.CompareOrdinal(MinPin(a), MinPin(b)));

        var used = new HashSet<string>(StringComparer.Ordinal);
        var deferredNoConnects = new List<PinRef>();
        foreach (int root in roots)
        {
            var pins = pinsByRoot[root];
            bool named = namesByRoot.TryGetValue(root, out var names) && names.Count > 0;
            var ncPins = pins.Where(p => noConnectKeys.Contains(Graph.Key(anchorOf[p]))).ToList();

            // An isolated pin marked no_connect -> its own NoConnect state.
            if (pins.Count == 1 && ncPins.Count == 1 && !named)
            {
                deferredNoConnects.Add(pins[0]);
                continue;
            }

            string name = named
                ? ResolveName(names!, note)
                : $"Net-({MinPin(root)})";
            name = MakeUnique(name, used);

            if (pins.Count >= 2)
                schematic.Connect(name, pins);
            else
                schematic.Stub(name, pins[0]);
            used.Add(name);

            if (ncPins.Count > 0)
                note($"A no_connect at {ncPins[0]} lies on a connected net ('{name}'); the net was "
                    + "kept (KiCad would flag this).");
            else if (pins.Count == 1 && !named)
                note($"Pin {pins[0]} is not connected to anything (a dangling pin); it was recorded "
                    + "as its own single-terminal net.");
        }

        // NoConnect nets last, so the auto NCn names avoid any user net name.
        deferredNoConnects.Sort((a, b) => string.CompareOrdinal(a.ToString(), b.ToString()));
        foreach (var pin in deferredNoConnects)
            schematic.NoConnect(pin);
    }

    /// <summary>Picks a net name for a class carrying several labels: a power name beats a global
    /// label beats a local label; ties break alphabetically. A genuine conflict (two DIFFERENT
    /// names on one net) is noted.</summary>
    private static string ResolveName(List<LabelPoint> names, Action<string> note)
    {
        var distinct = names.Select(n => n.Name).Distinct(StringComparer.Ordinal).ToList();
        var best = names
            .OrderBy(n => (int)n.Priority)
            .ThenBy(n => n.Name, StringComparer.Ordinal)
            .First().Name;
        if (distinct.Count > 1)
            note($"A net carries more than one label ({string.Join(", ", distinct.OrderBy(x => x, StringComparer.Ordinal))}); "
                + $"used '{best}'.");
        return best;
    }

    private static string MakeUnique(string name, HashSet<string> used)
    {
        if (!used.Contains(name))
            return name;
        int suffix = 2;
        string candidate;
        do { candidate = $"{name}_{suffix++}"; } while (used.Contains(candidate));
        return candidate;
    }

    /// <summary>Whether point <paramref name="p"/> lies on segment <c>[a, b]</c> (an endpoint or
    /// the interior) within the weld tolerance.</summary>
    private static bool OnSegment(Vector2d p, Vector2d a, Vector2d b)
    {
        if (p.DistanceTo(a) < WeldMm || p.DistanceTo(b) < WeldMm)
            return true;
        var ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 <= WeldMm * WeldMm)
            return false;
        double t = (p - a).Dot(ab) / len2;
        if (t < 0.0 || t > 1.0)
            return false;
        return p.DistanceTo(a + t * ab) < WeldMm;
    }

    // ======================================================================
    // Parsing helpers
    // ======================================================================

    private static void RefuseOutOfScope(SList root)
    {
        // A subsheet reference (not (sheet_instances …), which every flat sheet carries).
        if (root.List("sheet") is not null)
            throw new FormatException(
                "This KiCad schematic uses hierarchical sheets (a 'sheet' subsheet), which are out "
                + "of v1 scope (single-sheet import only). Flatten the design to one sheet, or file "
                + "hierarchical import as a follow-up.");
        if (root.List("hierarchical_label") is not null)
            throw new FormatException(
                "This KiCad schematic uses a 'hierarchical_label', part of hierarchical sheets, "
                + "which are out of v1 scope (single-sheet import only).");
        if (root.List("bus") is not null || root.List("bus_entry") is not null)
            throw new FormatException(
                "This KiCad schematic uses buses ('bus' / 'bus_entry'), which are out of v1 scope. "
                + "Route the signals as individual wires, or file bus import as a follow-up.");
        foreach (var label in root.Lists("label").Concat(root.Lists("global_label")))
            if (LooksLikeBus(label.Arg(0)))
                throw new FormatException(
                    $"This KiCad schematic uses a bus label ('{label.Arg(0)}'), which is out of v1 "
                    + "scope. Name the signals individually, or file bus import as a follow-up.");
    }

    /// <summary>Whether a label name is a bus vector (<c>D[7..0]</c>) or bus group (<c>{…}</c>).</summary>
    private static bool LooksLikeBus(string? name)
    {
        if (name is null)
            return false;
        if (name.Contains('{') || name.Contains('}'))
            return true;
        int lb = name.IndexOf('[');
        int rb = name.IndexOf(']');
        return lb >= 0 && rb > lb && name.AsSpan(lb, rb - lb).Contains("..", StringComparison.Ordinal);
    }

    private static Dictionary<string, KiCadSymbolReader.ParsedSymbol> ReadLibSymbols(
        SList root, Action<string> note)
    {
        var map = new Dictionary<string, KiCadSymbolReader.ParsedSymbol>(StringComparer.Ordinal);
        var libSymbols = root.List("lib_symbols");
        if (libSymbols is null)
            return map;
        foreach (var symbol in libSymbols.Lists("symbol"))
        {
            string? id = symbol.Arg(0);
            if (string.IsNullOrEmpty(id))
                continue;
            var symbolDiagnostics = new List<string>();
            map[id] = KiCadSymbolReader.ParseSymbolList(symbol, symbolDiagnostics);
            foreach (var message in symbolDiagnostics)
                note(message);
        }
        return map;
    }

    private static string ReadSheetName(SList root)
    {
        string? title = root.List("title_block")?.List("title")?.Arg(0);
        return string.IsNullOrEmpty(title) ? "schematic" : title;
    }

    private static string InstanceReference(SList instance)
    {
        foreach (var property in instance.Lists("property"))
            if (string.Equals(property.Arg(0), "Reference", StringComparison.Ordinal))
                return property.Arg(1) ?? "";
        return "";
    }

    private static string? InstanceValue(SList instance)
    {
        foreach (var property in instance.Lists("property"))
            if (string.Equals(property.Arg(0), "Value", StringComparison.Ordinal))
                return property.Arg(1);
        return null;
    }

    private static string BareName(string libId) =>
        libId.Contains(':') ? libId[(libId.IndexOf(':') + 1)..] : libId;

    private static bool TryWire(SList wire, out Vector2d a, out Vector2d b)
    {
        a = default;
        b = default;
        var points = wire.List("pts");
        if (points is null)
            return false;
        var xy = points.Lists("xy").ToList();
        if (xy.Count < 2)
            return false;
        var p0 = xy[0].Numbers();
        var p1 = xy[1].Numbers();
        if (p0.Count < 2 || p1.Count < 2)
            return false;
        a = new Vector2d(p0[0], p0[1]);
        b = new Vector2d(p1[0], p1[1]);
        return true;
    }

    private static bool TryAt(SList list, out Vector2d at)
    {
        at = default;
        var numbers = list.ChildNumbers("at");
        if (numbers.Count < 2)
            return false;
        at = new Vector2d(numbers[0], numbers[1]);
        return true;
    }

    private static bool TryLabel(SList label, out Vector2d at, out string name)
    {
        at = default;
        name = label.Arg(0) ?? "";
        if (name.Length == 0)
            return false;
        return TryAt(label, out at);
    }

    // ======================================================================
    // Placement transform (library Y-up -> sheet Y-down, plus rotation / mirror)
    // ======================================================================

    private enum MirrorAxis { None, X, Y }

    private readonly record struct Placement(double X, double Y, int Quadrant, MirrorAxis Mirror)
    {
        /// <summary>Maps a library-local point (Y-up) to sheet coordinates (Y-down): the base
        /// transform negates Y, and a screen rotation (0/90/180/270°) composes on top, with an
        /// optional mirror applied in the library frame first (best-effort — a mirror is rare and
        /// documented).</summary>
        public Vector2d Place(Vector2d local)
        {
            double lx = local.X, ly = local.Y;
            if (Mirror == MirrorAxis.X) ly = -ly;          // (mirror x): flip about X -> negate Y
            else if (Mirror == MirrorAxis.Y) lx = -lx;     // (mirror y): flip about Y -> negate X
            double sx, sy;
            switch (Quadrant)
            {
                case 1: sx = -ly; sy = -lx; break;         // 90
                case 2: sx = -lx; sy = ly; break;          // 180
                case 3: sx = ly; sy = lx; break;           // 270
                default: sx = lx; sy = -ly; break;         // 0
            }
            return new Vector2d(X + sx, Y + sy);
        }
    }

    private static Placement ReadPlacement(SList instance)
    {
        var at = instance.ChildNumbers("at");
        double x = at.Count >= 1 ? at[0] : 0;
        double y = at.Count >= 2 ? at[1] : 0;
        double angle = at.Count >= 3 ? at[2] : 0;
        int quadrant = ((int)Math.Round(angle / 90.0) % 4 + 4) % 4;

        var mirror = instance.List("mirror")?.Arg(0) switch
        {
            "x" => MirrorAxis.X,
            "y" => MirrorAxis.Y,
            _ => MirrorAxis.None,
        };
        return new Placement(x, y, quadrant, mirror);
    }

    // ======================================================================
    // Union-find over connection points
    // ======================================================================

    private enum LabelPriority { Power = 0, Global = 1, Local = 2 }

    private readonly record struct LabelPoint(Vector2d At, string Name, LabelPriority Priority);

    private sealed class Graph
    {
        private readonly List<int> _parent = [];
        private readonly Dictionary<(long, long), int> _node = [];

        public int Intern(Vector2d p)
        {
            var key = Key(p);
            if (!_node.TryGetValue(key, out int id))
            {
                id = _parent.Count;
                _node[key] = id;
                _parent.Add(id);
            }
            return id;
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
                x = _parent[x] = _parent[_parent[x]];
            return x;
        }

        public void Union(int a, int b) => _parent[Find(a)] = Find(b);

        /// <summary>The weld cell of a point. The grid center of every 4/6-decimal KiCad
        /// coordinate is a cell center, so a ~1e-13 mm round-off never crosses a cell boundary
        /// and coincident points share a key.</summary>
        public static (long, long) Key(Vector2d p) =>
            ((long)Math.Round(p.X / WeldMm), (long)Math.Round(p.Y / WeldMm));
    }
}
