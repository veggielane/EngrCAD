using System.Globalization;
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
/// value, <c>lib_id</c> → definition, <c>(unit N)</c> → which unit), power symbols (their
/// <c>Value</c> is the net name), <c>wire</c>, <c>junction</c>, local <c>label</c>,
/// <c>global_label</c>, <c>no_connect</c>, and <b>buses</b> — bus wires (<c>bus</c>), bus entries
/// (<c>bus_entry</c>) and bus-VECTOR labels (<c>DATA[0..7]</c>).</para>
///
/// <para><b>Multi-unit symbols merge.</b> A multi-unit part (a dual op-amp drawn as several symbols
/// under one package) is placed as several <c>(symbol …)</c> instances SHARING ONE reference
/// designator, each with a distinct <c>(unit N)</c>. They MERGE into one <see cref="Component"/> whose
/// pins span every unit, and each instance places only its own unit's pins at that unit's location —
/// so a net wired to amp A's output and one wired to amp B's input are distinct nets on the same
/// component. A repeated placement of one unit, or two different symbols under one reference
/// designator, is reported and skipped.</para>
///
/// <para><b>Buses are SUGAR that EXPANDS to member nets; the net of a ripped signal is its own
/// local label.</b> KiCad requires a wire ripped off a bus (via a bus entry) to be LABELLED with a
/// bus member (e.g. <c>DATA3</c>) — that label names the net, and same-named labels are already one
/// net by local-label equivalence, so on a single flat sheet the bus's CONNECTING role is entirely
/// subsumed by that equivalence. The bus model's job here is therefore (a) to declare the member
/// NAMESPACE by expanding a <c>NAME[m..n]</c> vector or an anonymous <c>{A B …}</c> GROUP label into
/// its members (so a bus label is NOT mistaken for a signal net), and (b) to validate that a ripped
/// tap's label is a declared member. The bus's connecting role becomes load-bearing only ACROSS sheets
/// (hierarchical
/// bus pins), which stays out of scope. Buses are supported only in the single-sheet
/// <see cref="Read"/>; the hierarchical entry points still refuse them by name.</para>
///
/// <para><b>Hierarchical / multi-sheet designs</b> (a root that references sub-sheet FILES) are
/// handled by <see cref="ReadProject(string)"/> / <see cref="ReadProjectFrom"/>, which flatten the
/// hierarchy into one <see cref="Schematic"/> and STITCH connectivity across sheets (a parent sheet
/// pin ↔ a sub-sheet <c>hierarchical_label</c> of the same name; global labels and power span every
/// sheet; local labels stay local). The single-sheet <see cref="Read"/> deliberately REFUSES a
/// hierarchical design by name so a flat import cannot silently drop a whole subsheet.</para>
///
/// <para><b>Refused BY NAME</b>: a named bus ALIAS (a <c>(bus_alias …)</c> definition referenced by a
/// bare name — needs an alias table; an anonymous <c>{A B …}</c> group is supported), a NESTED group,
/// and a malformed bus range (<c>DATA[]</c>, a non-integer bound); buses in the HIERARCHICAL entry points
/// (out of scope there); and, in <see cref="Read"/> only, hierarchical sheets (<c>sheet</c>
/// subsheets, <c>hierarchical_label</c>). A netless wire, an instance referencing an unknown symbol,
/// a dangling pin, or a dangling / non-member bus entry is REPORTED (a diagnostic), not thrown
/// (the readers-never-throw culture); a hierarchical project's missing/unreadable sub-sheet file is
/// reported too, and a RECURSIVE sheet reference is refused by name. A non-<c>(kicad_sch …)</c> root
/// — including a <c>.kicad_pcb</c> board or a <c>.kicad_sym</c> handed here — or a malformed
/// S-expression is refused by name.</para>
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
    /// malformed, or it uses a construct out of v1 scope (a bus GROUP label, a malformed bus range,
    /// or a hierarchical sheet) — refused by name.</exception>
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
        // MULTI-UNIT MERGE: a multi-unit component is placed as several (symbol …) instances sharing
        // one reference designator, each carrying a (unit N). They merge into ONE Component here, and
        // each instance places only its own unit's pins (at that unit's location).
        var componentsByRef = new Dictionary<string, Component>(StringComparer.Ordinal);
        var placedUnits = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
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
                do { refDes = $"U{++generated}"; } while (componentsByRef.ContainsKey(refDes));

            if (!partDefs.TryGetValue(libId, out var definition))
                partDefs[libId] = definition = BuildDefinition(libId, parsed);

            PlaceInstance(
                schematic, refDes, InstanceValue(instance) ?? "", parsed, definition,
                InstanceUnit(instance), placement, componentsByRef, placedUnits, pinPoints, Note);
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

        // Labels split two ways: a bus-VECTOR label declares a member namespace (it is NOT a signal
        // net), an ordinary label is a signal net point (the existing rule).
        var busLabels = new List<BusLabel>();
        foreach (var label in root.Lists("label"))
            ReadLabelOrBus(label, LabelPriority.Local, labelPoints, busLabels);
        foreach (var label in root.Lists("global_label"))
            ReadLabelOrBus(label, LabelPriority.Global, labelPoints, busLabels);

        var noConnects = new List<Vector2d>();
        foreach (var nc in root.Lists("no_connect"))
            if (TryAt(nc, out var at))
                noConnects.Add(at);

        // ---- bus geometry: bus wires (the bundle) + bus entries (the diagonal rips) ----------
        var busSegments = new List<(Vector2d A, Vector2d B)>();
        foreach (var bus in root.Lists("bus"))
            ReadBusSegments(bus, busSegments);
        var busEntries = new List<(Vector2d A, Vector2d B)>();
        foreach (var entry in root.Lists("bus_entry"))
            if (TryBusEntry(entry, out var ea, out var eb))
                busEntries.Add((ea, eb));

        // ---- reconstruct the nets (union-find over connection points) ---------
        BuildNets(schematic, pinPoints, labelPoints, wires, junctions, noConnects,
            busSegments, busEntries, busLabels, Note);

        return new KiCadSchematic(schematic, sheetName, diagnostics);
    }

    /// <summary>Reads a <c>.kicad_sch</c> file from disk.</summary>
    public static KiCadSchematic ReadFile(string path) => Read(File.ReadAllText(path));

    // ======================================================================
    // Hierarchical / multi-sheet import (the multi-FILE entry points)
    // ======================================================================

    /// <summary>
    /// Reads a HIERARCHICAL KiCad project — a root <c>.kicad_sch</c> plus the sub-sheet FILES it
    /// references through <c>(sheet … (property "Sheetfile" …))</c> — into ONE flattened
    /// <see cref="KiCadSchematic"/>, resolving Sheetfile references RELATIVE TO THE ROOT'S
    /// DIRECTORY (the KiCad convention: all sheet files share the project directory), recursively.
    /// This is the multi-file twin of <see cref="Read"/>; the single-sheet <see cref="Read"/> is
    /// unchanged and still refuses a hierarchical design by name.
    ///
    /// <para>The flattened schematic uses HIERARCHICAL reference designators — a component in a
    /// sub-sheet placed under sheet "PowerSupply" is named <c>"PowerSupply/U1"</c> (the
    /// <see cref="Modeling.PartInstance"/> occurrence-path convention), and a sheet instanced twice
    /// gives DISTINCT instances (<c>"Amp1/U1"</c>, <c>"Amp2/U1"</c>) with distinct internal nets.
    /// Connectivity is stitched across sheets: a parent SHEET PIN joins the sub-sheet's
    /// <c>hierarchical_label</c> of the same name; <c>global_label</c>s and power symbols span every
    /// sheet; local <c>label</c>s stay local to their sheet.</para>
    /// </summary>
    /// <param name="rootPath">The root <c>.kicad_sch</c> file.</param>
    /// <returns>The flattened whole-project schematic; <see cref="KiCadSchematic.SheetName"/> is the
    /// ROOT sheet's title.</returns>
    /// <exception cref="FormatException">The root is not a KiCad schematic, a sheet uses buses,
    /// or the hierarchy is RECURSIVE (a sheet including itself) — refused by name. A missing or
    /// unreadable sub-sheet file is REPORTED in <see cref="KiCadSchematic.Diagnostics"/>, not thrown
    /// (the readers-never-throw-on-dirty culture).</exception>
    public static KiCadSchematic ReadProject(string rootPath)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        string rootDir = Path.GetDirectoryName(Path.GetFullPath(rootPath)) ?? ".";
        string rootText = File.ReadAllText(rootPath);
        return BuildProject(rootText, Path.GetFileName(rootPath), sheetFile =>
        {
            string p = Path.Combine(rootDir, sheetFile);
            return File.Exists(p) ? File.ReadAllText(p) : null;
        });
    }

    /// <summary>
    /// Reads a HIERARCHICAL KiCad project from an IN-MEMORY map of <c>Sheetfile → text</c> — the
    /// testable core the disk <see cref="ReadProject(string)"/> is a thin wrapper over. A
    /// <c>(sheet … (property "Sheetfile" "sub.kicad_sch"))</c> is resolved by looking up
    /// <c>"sub.kicad_sch"</c> in <paramref name="sheetsByFile"/> (a missing key is a reported,
    /// skipped sub-sheet — never a throw). See <see cref="ReadProject(string)"/> for the flattening
    /// and cross-sheet stitching rules.
    /// </summary>
    /// <param name="rootSheetFile">The map key of the ROOT sheet.</param>
    /// <param name="sheetsByFile">Every sheet file's text keyed by its Sheetfile name.</param>
    /// <exception cref="FormatException">The root key is not in the map, the root is not a KiCad
    /// schematic, a sheet uses buses, or the hierarchy is recursive — refused by name.</exception>
    public static KiCadSchematic ReadProjectFrom(
        string rootSheetFile, IReadOnlyDictionary<string, string> sheetsByFile)
    {
        ArgumentNullException.ThrowIfNull(rootSheetFile);
        ArgumentNullException.ThrowIfNull(sheetsByFile);
        if (!sheetsByFile.TryGetValue(rootSheetFile, out var rootText))
            throw new FormatException(
                $"The root sheet '{rootSheetFile}' is not in the supplied sheet map. It has: "
                + string.Join(", ", sheetsByFile.Keys) + ".");
        return BuildProject(rootText, rootSheetFile, sf => sheetsByFile.GetValueOrDefault(sf));
    }

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
        List<(Vector2d A, Vector2d B)> busSegments,
        List<(Vector2d A, Vector2d B)> busEntries,
        List<BusLabel> busLabels,
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

        // Buses: the signal union-find above already reconstructed every ripped member net from its
        // own local label (a member DATAi = its label DATAi, same-named labels one net). The bus
        // model only VALIDATES the taps and reports dangling / non-member bus entries.
        ValidateBuses(graph, namesByRoot, wires, busSegments, busEntries, busLabels, note);

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

        // Bus VECTORS (NAME[m..n]) are supported and expanded (see ExpandBusVector); a bus GROUP
        // ({…} named group / alias) is out of scope, and a malformed bus range is refused BY NAME —
        // both before any work is done, so a bad file fails the same way a bad geometry file does.
        foreach (var label in root.Lists("label").Concat(root.Lists("global_label")))
        {
            string? name = label.Arg(0);
            // Validate the member expansion up front (a malformed vector range or group is refused by
            // name before any work), the same way a bad geometry file fails early.
            if (IsBusGroup(name))
                ExpandBusGroup(name!);
            else if (IsBusVector(name))
                ExpandBusVector(name!);
        }
    }

    // ======================================================================
    // Buses — sugar that expands to member nets. The signal union-find is
    // UNCHANGED: a ripped tap's net is its own local label (KiCad requires the
    // tap labelled with a member, and same-named labels are already one net by
    // local-label equivalence, so the bus's connecting role is subsumed on a
    // single flat sheet). The bus model DECLARES the member namespace (so a
    // bus-vector label is not mistaken for a signal net) and VALIDATES the taps.
    // ======================================================================

    /// <summary>An expanded bus-vector label: where it sits, its raw text, and the member net names
    /// it declares (<c>DATA[0..7]</c> → DATA0..DATA7).</summary>
    private readonly record struct BusLabel(Vector2d At, string Raw, IReadOnlyList<string> Members);

    /// <summary>Whether a label names a bus — a bus GROUP or a bus VECTOR. The hierarchical path uses
    /// this to refuse buses by name (buses across sheets stay out of scope).</summary>
    private static bool LooksLikeBus(string? name) => IsBusGroup(name) || IsBusVector(name);

    /// <summary>Whether a label is an ANONYMOUS bus GROUP — the <c>{A B …}</c> brace form (supported in
    /// the single-sheet <see cref="Read"/> via <see cref="ExpandBusGroup"/>; still refused ACROSS sheets).
    /// A named bus ALIAS is a BARE name, so it is not caught here — it needs its own alias table and stays
    /// out of scope, treated as a plain signal label.</summary>
    private static bool IsBusGroup(string? name) =>
        name is not null && (name.Contains('{') || name.Contains('}'));

    /// <summary>Whether a label is a bus VECTOR — a <c>NAME[m..n]</c> suffix (a non-empty base name,
    /// then a bracketed range at the very end). Whether the range is well-formed is decided by
    /// <see cref="ExpandBusVector"/>, which refuses a malformed one by name.</summary>
    private static bool IsBusVector(string? name)
    {
        if (name is null || name.Length == 0 || name[^1] != ']')
            return false;
        int lb = name.IndexOf('[');
        return lb >= 1;   // a non-empty base name before the '['
    }

    /// <summary>Expands a bus-vector label <c>NAME[m..n]</c> into its member net names — the KiCad
    /// vector notation, member i named <c>NAME</c> + i (so <c>DATA[0..7]</c> is DATA0..DATA7). The
    /// range is inclusive and honoured in the DRAWN direction, so a reversed <c>DATA[7..0]</c> is
    /// legal and expands to the same eight members. A malformed range — empty, no <c>..</c>, or a
    /// non-integer bound — is refused BY NAME.</summary>
    private static IReadOnlyList<string> ExpandBusVector(string name)
    {
        int lb = name.IndexOf('[');
        string baseName = name[..lb];
        string inner = name[(lb + 1)..^1];   // between the '[' and the final ']'
        var parts = inner.Split("..", StringSplitOptions.None);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int start)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int end))
            throw new FormatException(
                $"The bus label '{name}' has a malformed vector range: a bus vector is NAME[m..n] "
                + "with integer bounds (for example 'DATA[0..7]'; reversed 'DATA[7..0]' is allowed).");
        var members = new List<string>();
        int step = start <= end ? 1 : -1;
        for (int i = start; ; i += step)
        {
            members.Add(baseName + i.ToString(CultureInfo.InvariantCulture));
            if (i == end)
                break;
        }
        return members;
    }

    /// <summary>Expands an ANONYMOUS bus GROUP label <c>{A B DATA[0..3]}</c> into its member net names —
    /// the whitespace-separated tokens, each a bare signal OR itself a bus VECTOR (expanded in turn). A
    /// named bus ALIAS (a bare name referencing a <c>(bus_alias …)</c> definition, not spelled <c>{…}</c>)
    /// is NOT this and stays out of scope. A nested group is refused by name.</summary>
    private static IReadOnlyList<string> ExpandBusGroup(string name)
    {
        int lb = name.IndexOf('{');
        int rb = name.LastIndexOf('}');
        if (lb < 0 || rb <= lb)
            throw new FormatException(
                $"The bus group label '{name}' is malformed (expected a brace group like '{{A B C}}').");
        string inner = name[(lb + 1)..rb].Trim();
        if (inner.Length == 0)
            throw new FormatException($"The bus group label '{name}' declares no members (an empty '{{}}').");
        var members = new List<string>();
        foreach (var token in inner.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Contains('{') || token.Contains('}'))
                throw new FormatException(
                    $"The bus group label '{name}' nests a group ('{token}'); a v1 group contains bare "
                    + "signals and bus vectors only.");
            if (IsBusVector(token))
                members.AddRange(ExpandBusVector(token));
            else
                members.Add(token);
        }
        return members;
    }

    /// <summary>Reads a <c>(label …)</c> / <c>(global_label …)</c>: a bus-VECTOR or anonymous bus-GROUP
    /// label is expanded into a <see cref="BusLabel"/> (it declares members, it is NOT a signal net),
    /// everything else is an ordinary signal-label point.</summary>
    private static void ReadLabelOrBus(
        SList label, LabelPriority priority, List<LabelPoint> labelPoints, List<BusLabel> busLabels)
    {
        if (!TryLabel(label, out var at, out var name))
            return;
        if (IsBusGroup(name))    // an anonymous {A B …} group — validated in RefuseOutOfScope
            busLabels.Add(new BusLabel(at, name, ExpandBusGroup(name)));
        else if (IsBusVector(name))
            busLabels.Add(new BusLabel(at, name, ExpandBusVector(name)));
        else
            labelPoints.Add(new LabelPoint(at, name, priority));
    }

    /// <summary>Reads a bus wire <c>(bus (pts (xy …) …))</c> into its consecutive segments (a bus
    /// polyline can have more than two points).</summary>
    private static void ReadBusSegments(SList bus, List<(Vector2d A, Vector2d B)> segments)
    {
        var pts = bus.List("pts");
        if (pts is null)
            return;
        var xy = pts.Lists("xy")
            .Select(x => x.Numbers())
            .Where(n => n.Count >= 2)
            .Select(n => new Vector2d(n[0], n[1]))
            .ToList();
        for (int i = 0; i + 1 < xy.Count; i++)
            segments.Add((xy[i], xy[i + 1]));
    }

    /// <summary>Reads a bus entry <c>(bus_entry (at x y) (size dx dy))</c> — the diagonal stub that
    /// connects a point on the bus to a signal-wire endpoint. It joins <c>(x, y)</c> to
    /// <c>(x + dx, y + dy)</c>; which end is the bus side and which the wire side is worked out from
    /// the geometry (whichever end lies on a bus segment is the bus side).</summary>
    private static bool TryBusEntry(SList entry, out Vector2d a, out Vector2d b)
    {
        b = default;
        if (!TryAt(entry, out a))
            return false;
        var size = entry.ChildNumbers("size");
        b = size.Count >= 2 ? new Vector2d(a.X + size[0], a.Y + size[1]) : a;
        return true;
    }

    /// <summary>Validates the buses against the reconstructed signal nets and reports what is wrong,
    /// never throwing (the readers-never-throw-on-dirty culture). The signal nets are already built
    /// — this only checks that each ripped tap's own local label is a declared member of the bus it
    /// taps, and that no bus entry is dangling.</summary>
    private static void ValidateBuses(
        Graph graph,
        Dictionary<int, List<LabelPoint>> namesByRoot,
        List<(Vector2d A, Vector2d B)> wires,
        List<(Vector2d A, Vector2d B)> busSegments,
        List<(Vector2d A, Vector2d B)> busEntries,
        List<BusLabel> busLabels,
        Action<string> note)
    {
        if (busSegments.Count == 0 && busEntries.Count == 0 && busLabels.Count == 0)
            return;   // nothing bus-shaped on the sheet — the flat path is untouched

        // A bus bundle: a connected component of bus wires (its own union-find, separate from the
        // signal graph so a bus point is never mistaken for a signal net).
        var busGraph = new Graph();
        foreach (var (a, b) in busSegments)
            busGraph.Union(busGraph.Intern(a), busGraph.Intern(b));

        // The bundle a point sits on (a bus-vector label, or a bus entry's bus-side), or -1.
        int BundleAt(Vector2d p)
        {
            foreach (var (a, b) in busSegments)
                if (OnSegment(p, a, b))
                    return busGraph.Find(busGraph.Intern(a));
            return -1;
        }

        // The members declared for each bundle (union of the bus-vector labels sitting on it).
        var membersByBundle = new Dictionary<int, HashSet<string>>();
        foreach (var bl in busLabels)
        {
            int bundle = BundleAt(bl.At);
            if (bundle < 0)
            {
                note($"Bus label '{bl.Raw}' at {Fmt(bl.At)} does not sit on a bus wire; its members "
                    + "declare no bus.");
                continue;
            }
            var set = membersByBundle.TryGetValue(bundle, out var s)
                ? s : membersByBundle[bundle] = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in bl.Members)
                set.Add(m);
        }

        // Each bus entry rips a signal off the bus: its wire-side label must be a member of the bus.
        foreach (var (a, b) in busEntries)
        {
            int bundle = BundleAt(a);
            Vector2d wireEnd = b;
            if (bundle < 0)
            {
                bundle = BundleAt(b);
                wireEnd = a;
            }
            if (bundle < 0)
            {
                note($"A bus entry at {Fmt(a)} is not connected to a bus; the signal ripped there "
                    + "joins no bus member.");
                continue;
            }

            var wire = FindWire(wires, wireEnd);
            if (wire is null)
            {
                note($"A bus entry at {Fmt(wireEnd)} is not connected to a wire; nothing is ripped "
                    + "off the bus there.");
                continue;
            }

            int root = graph.Find(graph.Intern(wire.Value.A));
            var netLabels = namesByRoot.GetValueOrDefault(root)?
                .Select(l => l.Name).Distinct(StringComparer.Ordinal).ToList() ?? [];
            if (netLabels.Count == 0)
            {
                note($"A wire ripped off a bus at {Fmt(wireEnd)} carries no member label; KiCad "
                    + "wants the ripped wire labelled with a bus member (e.g. 'DATA3').");
                continue;
            }
            var members = membersByBundle.GetValueOrDefault(bundle);
            if (members is { Count: > 0 } && !netLabels.Any(members.Contains))
                note($"A wire ripped off a bus carries the label(s) {string.Join(", ", netLabels.OrderBy(x => x, StringComparer.Ordinal))}, "
                    + $"which is not a member of the bus (members: {string.Join(", ", members.OrderBy(x => x, StringComparer.Ordinal))}).");
        }
    }

    /// <summary>The signal wire a point lies on (endpoint or interior), or null.</summary>
    private static (Vector2d A, Vector2d B)? FindWire(List<(Vector2d A, Vector2d B)> wires, Vector2d p)
    {
        foreach (var w in wires)
            if (OnSegment(p, w.A, w.B))
                return w;
        return null;
    }

    /// <summary>A point formatted for a diagnostic.</summary>
    private static string Fmt(Vector2d p) =>
        $"({p.X.ToString("0.###", CultureInfo.InvariantCulture)}, "
        + $"{p.Y.ToString("0.###", CultureInfo.InvariantCulture)})";

    // ======================================================================
    // Hierarchical engine — instance tree, per-sheet collection, cross-sheet
    // stitching. Reuses every leaf geometry/naming helper (Graph, OnSegment,
    // TryWire/TryAt/TryLabel, ReadPlacement, ReadLibSymbols, MakeUnique, …); only
    // the multi-instance orchestration is new (necessarily — the flat path knows
    // exactly one sheet).
    // ======================================================================

    /// <summary>One placed sheet in the flattened hierarchy. A sheet FILE placed twice gives two
    /// instances with distinct <see cref="Path"/>s. <see cref="Ports"/> are the sheet-pins on the
    /// <c>(sheet …)</c> node that placed this instance — they live in the PARENT's coordinate
    /// space (they are drawn on the parent), which is why they carry the parent's id via
    /// <see cref="ParentId"/>.</summary>
    private sealed record SheetInstance(
        int Id, string Path, int ParentId, IReadOnlyList<(string Name, Vector2d At)> Ports);

    /// <summary>The connection points collected from one sheet instance, tagged by instance so the
    /// global union-find keeps two sheets' identical coordinates apart.</summary>
    private sealed class SheetData
    {
        public required int Instance { get; init; }
        public List<(PinRef Pin, Vector2d Anchor)> PinPoints { get; } = [];
        public List<LabelPoint> LabelPoints { get; } = [];   // local + global + power (by Priority)
        public List<(Vector2d A, Vector2d B)> Wires { get; } = [];
        public List<Vector2d> Junctions { get; } = [];
        public List<Vector2d> NoConnects { get; } = [];
        public List<(Vector2d At, string Name)> HierLabels { get; } = [];
    }

    /// <summary>Builds a flattened schematic from a root sheet plus a resolver for its sub-sheet
    /// files. Walks the instance tree, collects each sheet's connection points, then stitches the
    /// nets across sheets (see <see cref="BuildHierarchicalNets"/>).</summary>
    private static KiCadSchematic BuildProject(
        string rootText, string rootKey, Func<string, string?> resolveChild)
    {
        var diagnostics = new List<string>();
        var once = new HashSet<string>(StringComparer.Ordinal);
        void Note(string message) { if (once.Add(message)) diagnostics.Add(message); }

        Note("KiCad stores schematic coordinates Y-downward; wire/junction/label points are "
            + "imported verbatim, and library-local symbol pins (Y-up) are transformed into that "
            + "same sheet frame.");

        var rootContent = ParseSheet(rootText, rootKey);
        string rootName = ReadSheetName(rootContent);
        var schematic = new Schematic(rootName);

        var instances = new List<SheetInstance>();
        var sheetData = new List<SheetData>();
        // Definitions are interned per lib_id ACROSS the whole project (two Device:R in different
        // sheets share one PartDefinition), the same interning the flat path does within one sheet.
        var partDefs = new Dictionary<string, PartDefinition>(StringComparer.Ordinal);
        // Multi-unit merge across the whole project (keyed by the hierarchical refdes, so a subsheet
        // placed twice keeps its instances apart while a part's units under one path merge).
        var componentsByRef = new Dictionary<string, Component>(StringComparer.Ordinal);
        var placedUnits = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        int[] generated = [0];   // one refdes counter across every instance

        void Walk(SList content, string path, int parentId,
            IReadOnlyList<(string Name, Vector2d At)> ports, IReadOnlyList<string> fileChain)
        {
            int id = instances.Count;
            instances.Add(new SheetInstance(id, path, parentId, ports));
            sheetData.Add(CollectSheet(
                content, id, path, schematic, partDefs, componentsByRef, placedUnits, generated, Note));

            var childNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sheet in content.Lists("sheet"))
            {
                string? childFile = SheetProperty(sheet, "Sheetfile");
                if (string.IsNullOrEmpty(childFile))
                {
                    Note("A (sheet ...) has no Sheetfile property and was skipped.");
                    continue;
                }
                if (fileChain.Contains(childFile, StringComparer.Ordinal))
                    throw new FormatException(
                        "This KiCad project has a RECURSIVE sheet reference: the chain "
                        + string.Join(" -> ", fileChain.Append(childFile))
                        + " includes a sheet inside itself, so the hierarchy cannot be flattened.");

                string? childText = resolveChild(childFile);
                if (childText is null)
                {
                    Note($"The subsheet file '{childFile}' could not be resolved and was skipped "
                        + "(its components and nets are absent from the flattened schematic).");
                    continue;
                }

                SList childContent;
                try { childContent = ParseSheet(childText, childFile); }
                catch (FormatException ex)
                {
                    Note($"The subsheet file '{childFile}' could not be read ({ex.Message}) and was "
                        + "skipped.");
                    continue;
                }

                string sheetName = SheetProperty(sheet, "Sheetname") is { Length: > 0 } n
                    ? n : $"Sheet{childNames.Count + 1}";
                string uniqueName = UniqueChild(sheetName, childNames);
                string childPath = path.Length == 0 ? uniqueName : $"{path}/{uniqueName}";
                Walk(childContent, childPath, id, ReadSheetPins(sheet), [.. fileChain, childFile]);
            }
        }

        Walk(rootContent, "", -1, [], [rootKey]);
        BuildHierarchicalNets(schematic, instances, sheetData, Note);
        return new KiCadSchematic(schematic, rootName, diagnostics);
    }

    /// <summary>Parses one sheet file, validating its head is <c>kicad_sch</c> and refusing buses
    /// (but NOT sheets/hierarchical_labels, which are the whole point here).</summary>
    private static SList ParseSheet(string text, string sourceName)
    {
        var root = SExpr.Parse(text);
        if (!string.Equals(root.Head, "kicad_sch", StringComparison.Ordinal))
            throw new FormatException(
                $"Not a KiCad schematic: the sheet '{sourceName}' has top S-expression "
                + $"'{root.Head ?? "?"}', expected 'kicad_sch'.");
        RefuseBuses(root, sourceName);
        return root;
    }

    /// <summary>Refuses buses in one sheet (the one out-of-scope construct that still applies to the
    /// hierarchical path).</summary>
    private static void RefuseBuses(SList sheet, string sourceName)
    {
        if (sheet.List("bus") is not null || sheet.List("bus_entry") is not null)
            throw new FormatException(
                $"The KiCad sheet '{sourceName}' uses buses ('bus' / 'bus_entry'), which are out of "
                + "scope. Route the signals as individual wires, or file bus import as a follow-up.");
        foreach (var label in sheet.Lists("label")
            .Concat(sheet.Lists("global_label")).Concat(sheet.Lists("hierarchical_label")))
            if (LooksLikeBus(label.Arg(0)))
                throw new FormatException(
                    $"The KiCad sheet '{sourceName}' uses a bus label ('{label.Arg(0)}'), which is "
                    + "out of scope. Name the signals individually, or file bus import as a follow-up.");
    }

    /// <summary>The value of a <c>(property "key" "value" …)</c> on a <c>(sheet …)</c> node.</summary>
    private static string? SheetProperty(SList sheet, string key)
    {
        foreach (var property in sheet.Lists("property"))
            if (string.Equals(property.Arg(0), key, StringComparison.Ordinal))
                return property.Arg(1);
        return null;
    }

    /// <summary>The sheet-pins (ports) on a <c>(sheet …)</c> node: each a name and a position in the
    /// PARENT sheet's coordinate space. A sheet pin ties the parent net at its position to the
    /// sub-sheet's <c>hierarchical_label</c> of the same name.</summary>
    private static IReadOnlyList<(string Name, Vector2d At)> ReadSheetPins(SList sheet)
    {
        var ports = new List<(string, Vector2d)>();
        foreach (var pin in sheet.Lists("pin"))
        {
            string name = pin.Arg(0) ?? "";
            if (name.Length != 0 && TryAt(pin, out var at))
                ports.Add((name, at));
        }
        return ports;
    }

    /// <summary>Makes a child sheet's path segment unique among its siblings (two <c>(sheet …)</c>
    /// with the same Sheetname get <c>name</c> / <c>name.2</c> — the assembly occurrence-name rule).</summary>
    private static string UniqueChild(string name, HashSet<string> used)
    {
        if (used.Add(name))
            return name;
        int suffix = 2;
        string candidate;
        do { candidate = $"{name}.{suffix++}"; } while (!used.Add(candidate));
        return candidate;
    }

    /// <summary>Collects one sheet instance's components (into the flattened schematic with a
    /// hierarchical refdes) and its connection points. Mirrors the flat <see cref="Read"/>'s middle,
    /// prefixing every reference designator by the instance path and reading this sheet's OWN
    /// <c>lib_symbols</c>.</summary>
    private static SheetData CollectSheet(
        SList content, int instance, string path, Schematic schematic,
        Dictionary<string, PartDefinition> partDefs, Dictionary<string, Component> componentsByRef,
        Dictionary<string, HashSet<int>> placedUnits, int[] generated, Action<string> note)
    {
        var data = new SheetData { Instance = instance };
        var libSymbols = ReadLibSymbols(content, note);
        string prefix = path.Length == 0 ? "" : path + "/";

        foreach (var inst in content.Lists("symbol"))
        {
            string? libId = inst.List("lib_id")?.Arg(0);
            if (string.IsNullOrEmpty(libId))
            {
                note("A placed (symbol ...) has no (lib_id ...) and was skipped.");
                continue;
            }
            if (!libSymbols.TryGetValue(libId, out var parsed))
            {
                note($"A placed symbol references an unknown lib_symbol '{libId}' and was skipped "
                    + "(no matching entry in (lib_symbols ...)).");
                continue;
            }

            var placement = ReadPlacement(inst);
            if (parsed.IsPower)
            {
                string powerNet = InstanceValue(inst) ?? parsed.Value ?? BareName(libId);
                foreach (var symPin in parsed.Symbol.Pins)
                    data.LabelPoints.Add(
                        new LabelPoint(placement.Place(symPin.Anchor), powerNet, LabelPriority.Power));
                continue;
            }

            string localRef = InstanceReference(inst);
            string refDes;
            if (localRef.Length == 0 || localRef == "?")
                do { refDes = $"{prefix}U{++generated[0]}"; } while (componentsByRef.ContainsKey(refDes));
            else
                refDes = prefix + localRef;

            if (!partDefs.TryGetValue(libId, out var definition))
                partDefs[libId] = definition = BuildDefinition(libId, parsed);

            // MULTI-UNIT MERGE (the flat path's rule, keyed by the hierarchical refdes): a part's
            // units under one path merge into one component, each placing only its own unit's pins.
            PlaceInstance(
                schematic, refDes, InstanceValue(inst) ?? "", parsed, definition,
                InstanceUnit(inst), placement, componentsByRef, placedUnits, data.PinPoints, note);
        }

        foreach (var wire in content.Lists("wire"))
            if (TryWire(wire, out var a, out var b))
                data.Wires.Add((a, b));
        foreach (var junction in content.Lists("junction"))
            if (TryAt(junction, out var at))
                data.Junctions.Add(at);
        foreach (var label in content.Lists("label"))
            if (TryLabel(label, out var at, out var name))
                data.LabelPoints.Add(new LabelPoint(at, name, LabelPriority.Local));
        foreach (var label in content.Lists("global_label"))
            if (TryLabel(label, out var at, out var name))
                data.LabelPoints.Add(new LabelPoint(at, name, LabelPriority.Global));
        foreach (var hier in content.Lists("hierarchical_label"))
            if (TryLabel(hier, out var at, out var name))
                data.HierLabels.Add((at, name));
        foreach (var nc in content.Lists("no_connect"))
            if (TryAt(nc, out var at))
                data.NoConnects.Add(at);

        return data;
    }

    /// <summary>The global union-find over every sheet instance's connection points, plus the
    /// cross-sheet stitching that makes a parent net and its sub-sheet net ONE net. This is the flat
    /// <see cref="BuildNets"/> rule generalized to many instances with SCOPING: a wire/junction/pin
    /// joins within an instance; a LOCAL label joins within its instance; a GLOBAL label or a POWER
    /// symbol joins ACROSS every instance; and a parent SHEET PIN joins the sub-sheet's
    /// <c>hierarchical_label</c> of the same name.</summary>
    private static void BuildHierarchicalNets(
        Schematic schematic, List<SheetInstance> instances, List<SheetData> data, Action<string> note)
    {
        var graph = new Graph();
        var byInstance = data.ToDictionary(d => d.Instance);

        // Phase 1: intern every connection point in its own instance space.
        foreach (var sd in data)
        {
            foreach (var (a, b) in sd.Wires) { graph.Intern(sd.Instance, a); graph.Intern(sd.Instance, b); }
            foreach (var j in sd.Junctions) graph.Intern(sd.Instance, j);
            foreach (var (_, anchor) in sd.PinPoints) graph.Intern(sd.Instance, anchor);
            foreach (var lp in sd.LabelPoints) graph.Intern(sd.Instance, lp.At);
            foreach (var (at, _) in sd.HierLabels) graph.Intern(sd.Instance, at);
            foreach (var nc in sd.NoConnects) graph.Intern(sd.Instance, nc);
        }
        // A sheet-pin lives in its PARENT instance's geometry.
        foreach (var inst in instances)
            if (inst.ParentId >= 0)
                foreach (var port in inst.Ports)
                    graph.Intern(inst.ParentId, port.At);

        // Phase 2a: a wire joins its endpoints.
        foreach (var sd in data)
            foreach (var (a, b) in sd.Wires)
                graph.Union(graph.Intern(sd.Instance, a), graph.Intern(sd.Instance, b));

        // Phase 2b: a junction / pin / label / hier-label / sheet-pin lying ON a wire joins it.
        foreach (var sd in data)
        {
            var childPorts = instances
                .Where(i => i.ParentId == sd.Instance)
                .SelectMany(i => i.Ports.Select(p => p.At));
            var attach = sd.Junctions
                .Concat(sd.PinPoints.Select(p => p.Anchor))
                .Concat(sd.LabelPoints.Select(l => l.At))
                .Concat(sd.HierLabels.Select(h => h.At))
                .Concat(childPorts);
            foreach (var p in attach)
                foreach (var (a, b) in sd.Wires)
                    if (OnSegment(p, a, b))
                        graph.Union(graph.Intern(sd.Instance, p), graph.Intern(sd.Instance, a));
        }

        // Phase 2c: LOCAL label equivalence WITHIN an instance (the scoping crux — two sheets' "CLK"
        // local labels are TWO nets).
        foreach (var sd in data)
            foreach (var group in sd.LabelPoints
                .Where(l => l.Priority == LabelPriority.Local)
                .GroupBy(l => l.Name, StringComparer.Ordinal))
                UnionAll(graph, sd.Instance, group.Select(l => l.At));

        // Phase 2d: GLOBAL label + POWER symbol equivalence ACROSS every instance.
        var globalPoints = data.SelectMany(sd => sd.LabelPoints
            .Where(l => l.Priority is LabelPriority.Global or LabelPriority.Power)
            .Select(l => (sd.Instance, l.At, l.Name)));
        foreach (var group in globalPoints.GroupBy(x => x.Name, StringComparer.Ordinal))
        {
            using var e = group.GetEnumerator();
            if (!e.MoveNext()) continue;
            int first = graph.Intern(e.Current.Instance, e.Current.At);
            while (e.MoveNext()) graph.Union(first, graph.Intern(e.Current.Instance, e.Current.At));
        }

        // Phase 2e: hierarchical-label equivalence WITHIN an instance (same name = the child's one
        // port of that name).
        foreach (var sd in data)
            foreach (var group in sd.HierLabels.GroupBy(h => h.Name, StringComparer.Ordinal))
                UnionAll(graph, sd.Instance, group.Select(h => h.At));

        // Phase 2f: STITCH — a parent sheet-pin joins the sub-sheet's hierarchical_label of the same
        // name (name-matched, scoped to THIS child instance).
        foreach (var child in instances)
        {
            if (child.ParentId < 0) continue;
            var childHier = byInstance[child.Id].HierLabels;
            var portNames = child.Ports.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var port in child.Ports)
            {
                var matches = childHier
                    .Where(h => string.Equals(h.Name, port.Name, StringComparison.Ordinal)).ToList();
                if (matches.Count == 0)
                {
                    note($"Sheet pin '{port.Name}' on subsheet '{child.Path}' has no matching "
                        + "hierarchical label inside it; the port connects nothing across the sheet "
                        + "boundary.");
                    continue;
                }
                int parentNode = graph.Intern(child.ParentId, port.At);
                foreach (var hl in matches)
                    graph.Union(parentNode, graph.Intern(child.Id, hl.At));
            }
            foreach (var hl in childHier)
                if (!portNames.Contains(hl.Name))
                    note($"Hierarchical label '{hl.Name}' in subsheet '{child.Path}' has no matching "
                        + "sheet pin on its parent (a dangling hierarchical port).");
        }

        ResolveHierNets(schematic, instances, data, graph, note);
    }

    private static void UnionAll(Graph graph, int instance, IEnumerable<Vector2d> points)
    {
        using var e = points.GetEnumerator();
        if (!e.MoveNext()) return;
        int first = graph.Intern(instance, e.Current);
        while (e.MoveNext()) graph.Union(first, graph.Intern(instance, e.Current));
    }

    /// <summary>Groups the pins by their union-find root into the flattened schematic's nets, names
    /// each net (power &gt; global label &gt; sheet-pin/hier-label port &gt; local label &gt;
    /// generated), and defers isolated no-connect pins — the flat <see cref="BuildNets"/> resolution
    /// generalized to instance-tagged points.</summary>
    private static void ResolveHierNets(
        Schematic schematic, List<SheetInstance> instances, List<SheetData> data, Graph graph,
        Action<string> note)
    {
        var pathOf = instances.ToDictionary(i => i.Id, i => i.Path);

        var anchorOf = new Dictionary<PinRef, (int Instance, Vector2d At)>();
        var pinsByRoot = new Dictionary<int, List<PinRef>>();
        foreach (var sd in data)
            foreach (var (pin, anchor) in sd.PinPoints)
            {
                anchorOf[pin] = (sd.Instance, anchor);
                int root = graph.Find(graph.Intern(sd.Instance, anchor));
                (pinsByRoot.TryGetValue(root, out var list) ? list : pinsByRoot[root] = []).Add(pin);
            }

        var labelByRoot = new Dictionary<int, List<(LabelPriority Priority, int Instance, string Name)>>();
        var hierByRoot = new Dictionary<int, List<(int Instance, string Name)>>();
        foreach (var sd in data)
        {
            foreach (var lp in sd.LabelPoints)
            {
                int root = graph.Find(graph.Intern(sd.Instance, lp.At));
                (labelByRoot.TryGetValue(root, out var l) ? l : labelByRoot[root] = [])
                    .Add((lp.Priority, sd.Instance, lp.Name));
            }
            foreach (var (at, name) in sd.HierLabels)
            {
                int root = graph.Find(graph.Intern(sd.Instance, at));
                (hierByRoot.TryGetValue(root, out var l) ? l : hierByRoot[root] = [])
                    .Add((sd.Instance, name));
            }
        }
        var portByRoot = new Dictionary<int, List<string>>();
        foreach (var inst in instances)
            if (inst.ParentId >= 0)
                foreach (var port in inst.Ports)
                {
                    int root = graph.Find(graph.Intern(inst.ParentId, port.At));
                    (portByRoot.TryGetValue(root, out var l) ? l : portByRoot[root] = []).Add(port.Name);
                }

        var noConnectKeys = new HashSet<(int, long, long)>();
        foreach (var sd in data)
            foreach (var nc in sd.NoConnects)
            {
                var (x, y) = Graph.Key(nc);
                noConnectKeys.Add((sd.Instance, x, y));
            }

        string MinPin(int root) => pinsByRoot[root].Select(p => p.ToString()).Min(StringComparer.Ordinal)!;
        var roots = pinsByRoot.Keys.ToList();
        roots.Sort((a, b) => string.CompareOrdinal(MinPin(a), MinPin(b)));

        var used = new HashSet<string>(StringComparer.Ordinal);
        var deferredNoConnects = new List<PinRef>();
        foreach (int root in roots)
        {
            var pins = pinsByRoot[root];
            bool named = labelByRoot.TryGetValue(root, out var labels) && labels.Count > 0
                || hierByRoot.ContainsKey(root) || portByRoot.ContainsKey(root);
            var ncPins = pins.Where(p =>
            {
                var (inst, at) = anchorOf[p];
                var (x, y) = Graph.Key(at);
                return noConnectKeys.Contains((inst, x, y));
            }).ToList();

            if (pins.Count == 1 && ncPins.Count == 1 && !named)
            {
                deferredNoConnects.Add(pins[0]);
                continue;
            }

            string name = ResolveHierName(
                root, labels, portByRoot.GetValueOrDefault(root),
                hierByRoot.GetValueOrDefault(root), pathOf, MinPin, note);
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

        deferredNoConnects.Sort((a, b) => string.CompareOrdinal(a.ToString(), b.ToString()));
        foreach (var pin in deferredNoConnects)
            schematic.NoConnect(pin);
    }

    /// <summary>Names a hierarchical net: a power name beats a global label beats a stitched
    /// sheet-pin/hierarchical-label PORT name beats a hierarchical/local label beats a generated
    /// name. A GLOBAL name is used bare (global names are globally unique); a LOCAL or a dangling
    /// hierarchical name is QUALIFIED by its sheet path (<c>"SubA/CLK"</c>) so two sheets' identical
    /// local names stay two distinct nets.</summary>
    private static string ResolveHierName(
        int root,
        List<(LabelPriority Priority, int Instance, string Name)>? labels,
        List<string>? ports,
        List<(int Instance, string Name)>? hier,
        Dictionary<int, string> pathOf,
        Func<int, string> minPin,
        Action<string> note)
    {
        labels ??= [];
        string? power = labels.Where(n => n.Priority == LabelPriority.Power)
            .Select(n => n.Name).OrderBy(s => s, StringComparer.Ordinal).FirstOrDefault();
        string? global = labels.Where(n => n.Priority == LabelPriority.Global)
            .Select(n => n.Name).OrderBy(s => s, StringComparer.Ordinal).FirstOrDefault();
        string? port = ports?.OrderBy(s => s, StringComparer.Ordinal).FirstOrDefault();

        (int Instance, string Name)? hierPick = null;
        if (hier is { Count: > 0 })
            hierPick = hier.OrderBy(h => h.Name, StringComparer.Ordinal).First();
        (int Instance, string Name)? localPick = null;
        var locals = labels.Where(n => n.Priority == LabelPriority.Local)
            .OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        if (locals.Count > 0)
            localPick = (locals[0].Instance, locals[0].Name);

        string result =
            power ?? global ?? port
            ?? (hierPick is { } h ? Qualify(pathOf[h.Instance], h.Name) : null)
            ?? (localPick is { } l ? Qualify(pathOf[l.Instance], l.Name) : null)
            ?? $"Net-({minPin(root)})";

        var distinct = labels.Select(n => n.Name)
            .Concat(ports ?? [])
            .Concat(hier?.Select(x => x.Name) ?? [])
            .Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > 1)
            note($"A net carries more than one label ({string.Join(", ", distinct.OrderBy(x => x, StringComparer.Ordinal))}); "
                + $"used '{result}'.");
        return result;
    }

    private static string Qualify(string path, string name) =>
        path.Length == 0 ? name : $"{path}/{name}";

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

    /// <summary>The KiCad unit number of a placed instance's <c>(unit N)</c> — which schematic unit
    /// of a multi-unit part it draws (1-based). Absent (a plain single-unit part) means unit 1.</summary>
    private static int InstanceUnit(SList instance) =>
        instance.List("unit")?.Numbers() is { Count: >= 1 } n ? (int)n[0] : 1;

    private static string BareName(string libId) =>
        libId.Contains(':') ? libId[(libId.IndexOf(':') + 1)..] : libId;

    /// <summary>Builds the part definition for a placed lib_symbol — MULTI-UNIT (its per-unit symbols
    /// under <c>Units</c>, pins the union) when the symbol draws several units, else the incumbent
    /// single-symbol definition (byte-identical).</summary>
    private static PartDefinition BuildDefinition(string libId, KiCadSymbolReader.ParsedSymbol parsed) =>
        parsed.Units.Count > 1
            ? new PartDefinition(BareName(libId), parsed.ReferencePrefix, parsed.Pins,
                footprint: null, body: null, symbol: null, units: parsed.Units)
            : new PartDefinition(BareName(libId), parsed.ReferencePrefix, parsed.Pins,
                footprint: null, body: null, symbol: parsed.Symbol);

    /// <summary>The drawn symbol of one unit of a placed part (unit N selects the unit whose number
    /// is N). A single-unit part is always unit 1 whatever the instance's <c>(unit …)</c> says.</summary>
    private static Symbol? UnitSymbolFor(KiCadSymbolReader.ParsedSymbol parsed, int unit)
    {
        if (parsed.Units.Count == 1)
            return parsed.Units[0];
        for (int i = 0; i < parsed.UnitNumbers.Count; i++)
            if (parsed.UnitNumbers[i] == unit)
                return parsed.Units[i];
        return null;
    }

    /// <summary>
    /// Places one <c>(symbol …)</c> instance onto its <see cref="Component"/>, MERGING the several
    /// instances of a multi-unit part (which share one reference designator, each carrying a
    /// <c>(unit N)</c>) into a single component and placing ONLY that instance's unit's pins at that
    /// instance's location. The first instance of a reference designator creates the component; a
    /// later one adds another unit's pins (a single-unit part places its one unit here and is
    /// byte-identical). A second placement of the same unit, or two symbols under one reference
    /// designator, is reported and skipped (no rename — same reference designator is one component).
    /// </summary>
    private static void PlaceInstance(
        Schematic schematic, string refDes, string value, KiCadSymbolReader.ParsedSymbol parsed,
        PartDefinition definition, int unit, Placement placement,
        Dictionary<string, Component> componentsByRef, Dictionary<string, HashSet<int>> placedUnits,
        List<(PinRef Pin, Vector2d Anchor)> pinPoints, Action<string> note)
    {
        if (!componentsByRef.TryGetValue(refDes, out var component))
        {
            component = schematic.Add(refDes, definition, value);
            componentsByRef[refDes] = component;
            placedUnits[refDes] = [];
        }
        else if (!ReferenceEquals(component.Definition, definition))
        {
            note($"Reference '{refDes}' is placed as two different symbols "
                + $"('{component.Definition.Name}' and '{definition.Name}'); the second placement was "
                + "skipped.");
            return;
        }

        var unitSymbol = UnitSymbolFor(parsed, unit);
        if (unitSymbol is null)
        {
            note($"Component '{refDes}' unit {unit} is not defined by symbol '{definition.Name}' "
                + $"(it draws {parsed.Units.Count} unit(s)); the instance's pins were not placed.");
            return;
        }
        if (!placedUnits[refDes].Add(unit))
        {
            note($"Component '{refDes}' unit {unit} is placed more than once; the extra placement was "
                + "skipped.");
            return;
        }
        foreach (var symPin in unitSymbol.Pins)
            pinPoints.Add((component.Pin(symPin.Number), placement.Place(symPin.Anchor)));
    }

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
        private readonly Dictionary<(int, long, long), int> _node = [];

        /// <summary>Interns a point in the SINGLE coordinate space (the flat single-sheet path):
        /// instance 0. Kept so <see cref="BuildNets"/> reads exactly as before.</summary>
        public int Intern(Vector2d p) => Intern(0, p);

        /// <summary>Interns a point in one sheet INSTANCE's coordinate space. Each sheet instance
        /// has its own space, so two sheets with a wire at the same <c>(x, y)</c> do NOT join —
        /// the instance id keeps them apart (the crux of hierarchical scoping). The flat path
        /// stays bit-identical because it interns everything at instance 0, so node ids are
        /// assigned in the same order.</summary>
        public int Intern(int instance, Vector2d p)
        {
            var (x, y) = Key(p);
            var key = (instance, x, y);
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
