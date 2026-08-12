using System.Globalization;
using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>What KIND of conductive access point an IPC-D-356A record describes — the three the
/// bare-board netlist lists. The op code and the presence of a component reference recover it:
/// a <see cref="ThroughHolePad"/> is op <c>317</c> (drilled) with a component, a
/// <see cref="SurfacePad"/> is op <c>327</c> (no hole) with a component, and a <see cref="Via"/>
/// is op <c>317</c> (drilled) with NO component reference.</summary>
public enum Ipc356Feature
{
    /// <summary>A plated through-hole component pad — op <c>317</c>, drilled, reaches every layer
    /// (access <c>00</c>).</summary>
    ThroughHolePad,

    /// <summary>A surface-mount (SMD) component pad — op <c>327</c>, no hole, on one outer/inner
    /// copper layer (access = that layer's number).</summary>
    SurfacePad,

    /// <summary>A net-carrying via — op <c>317</c>, drilled, no component reference; a probeable
    /// cross-layer connection.</summary>
    Via,
}

/// <summary>
/// One IPC-D-356A netlist access point — a conductive test point the fab net-compares against the
/// copper and a flying-probe / bed-of-nails tester programs to. It is a pure PROJECTION of a placed
/// pad or via: the <see cref="Net"/> it belongs to, the component <see cref="RefDes"/> + <see cref="Pin"/>
/// (empty for a via), the <see cref="Feature"/> kind, the IPC <see cref="Access"/> layer code, the
/// board-frame midpoint (<see cref="Xum"/>/<see cref="Yum"/> in MICROMETRES — the file's unit), and the
/// finished <see cref="DrillUm"/> (0 for an SMD pad).
///
/// <para><b>Coordinates are the file's own quantum (micrometres, integer).</b> So the round trip is
/// EXACT — a parse recovers the same integers a write emitted, and a wrong units scale is a 10^n tell
/// against a coordinate-magnitude check. Read <see cref="Xmm"/>/<see cref="Ymm"/>/<see cref="DrillMm"/>
/// for millimetres.</para>
/// </summary>
/// <param name="Net">The net name this access point belongs to. An unconnected / no-connect pad is
/// its OWN single-point net, named <c>N/C-######</c> uniquely (see <see cref="PcbIpc356"/>).</param>
/// <param name="RefDes">The component reference designator (<c>R1</c>), or the empty string for a via.</param>
/// <param name="Pin">The pad / pin number (<c>1</c>, <c>A5</c>), or the empty string for a via.</param>
/// <param name="Feature">The access-point kind (through-hole pad / SMD pad / via).</param>
/// <param name="Access">The IPC access code: <c>0</c> = all layers (a through feature), else the
/// 1-based number of the top-most copper layer it is accessed from (top = 1, bottom = N). A
/// blind/buried via's single top-most-layer code cannot spell the full range it reaches, so it ALSO
/// carries <see cref="FromLayer"/>/<see cref="ToLayer"/> (see <see cref="HasLayerSpan"/>).</param>
/// <param name="Xum">The access-point midpoint X in micrometres, board-frame verbatim (no Y-flip).</param>
/// <param name="Yum">The access-point midpoint Y in micrometres, board-frame verbatim.</param>
/// <param name="DrillUm">The finished drill diameter in micrometres for a drilled feature, else 0.</param>
/// <param name="FromLayer">The top-most copper layer a blind/buried via reaches (1-based, top = 1), or
/// <c>0</c> when no explicit span is stated. A through feature (both outer faces) and a single-layer SMD
/// pad leave this <c>0</c> — their reached set is implicit in <see cref="Access"/>, so no span token is
/// written and their records stay byte-identical to the narrow format.</param>
/// <param name="ToLayer">The bottom-most copper layer a blind/buried via reaches (1-based, top = 1), or
/// <c>0</c> when no explicit span is stated (the twin of <see cref="FromLayer"/>).</param>
public readonly record struct Ipc356AccessPoint(
    string Net, string RefDes, string Pin, Ipc356Feature Feature,
    int Access, long Xum, long Yum, long DrillUm,
    int FromLayer = 0, int ToLayer = 0)
{
    /// <summary>Whether this access point is a via (no component reference).</summary>
    public bool IsVia => Feature == Ipc356Feature.Via;

    /// <summary>Whether this access point states an EXPLICIT copper-layer span (a blind/buried via) — the
    /// per-inner-layer encoding the single <see cref="Access"/> code cannot carry. When true,
    /// <c>[FromLayer, ToLayer]</c> is the 1-based inclusive copper range the feature reaches.</summary>
    public bool HasLayerSpan => FromLayer > 0 && ToLayer > 0;

    /// <summary>Whether this access point has a drilled hole (a through-hole pad or a via — every
    /// op <c>317</c> record).</summary>
    public bool IsDrilled => Feature != Ipc356Feature.SurfacePad;

    /// <summary>The midpoint X in millimetres.</summary>
    public double Xmm => Xum / 1000.0;

    /// <summary>The midpoint Y in millimetres.</summary>
    public double Ymm => Yum / 1000.0;

    /// <summary>The finished drill diameter in millimetres (0 for an SMD pad).</summary>
    public double DrillMm => DrillUm / 1000.0;

    /// <summary>The canonical <c>"R1.1"</c> spelling of a component pad, or the empty string for a
    /// via (which names no component).</summary>
    public string PadName => RefDes.Length == 0 ? "" : $"{RefDes}.{Pin}";
}

/// <summary>One net of an IPC-D-356A netlist — its name and the access points on it, grouped by
/// <see cref="PcbIpc356.Nets"/> so a reader can reconstruct connectivity net by net.</summary>
/// <param name="Name">The net name.</param>
/// <param name="AccessPoints">The access points on this net, in file order.</param>
public readonly record struct Ipc356Net(string Name, IReadOnlyList<Ipc356AccessPoint> AccessPoints);

/// <summary>One point of a conductor path, in the file's micrometre quantum (integer µm, so the round
/// trip is exact). <see cref="Xmm"/>/<see cref="Ymm"/> are the millimetre views.</summary>
/// <param name="Xum">The X coordinate in micrometres.</param>
/// <param name="Yum">The Y coordinate in micrometres.</param>
public readonly record struct Ipc356PathPoint(long Xum, long Yum)
{
    /// <summary>The X coordinate in millimetres.</summary>
    public double Xmm => Xum / 1000.0;

    /// <summary>The Y coordinate in millimetres.</summary>
    public double Ymm => Yum / 1000.0;
}

/// <summary>
/// A CONDUCTOR record (op <c>378</c>) — a net's copper PATH on one layer, the more-thorough net-compare
/// datum an access-point list (pads / vias) does not carry. It names the net, the 1-based copper
/// <see cref="Access"/> layer the conductor is on, its <see cref="WidthUm"/>, and its centre-line
/// <see cref="Path"/> (≥ 2 points). Emitted only when the caller asks for conductors
/// (<see cref="PcbIpc356.Write(PcbLayout, bool)"/>) — the default netlist lists access points only, so
/// it is byte-identical to before.
/// </summary>
/// <param name="Net">The net the conductor carries.</param>
/// <param name="Access">The 1-based copper-layer number the conductor is on (top = 1).</param>
/// <param name="WidthUm">The conductor width in micrometres.</param>
/// <param name="Path">The conductor centre-line, ≥ 2 points, in the file's micrometre quantum.</param>
public readonly record struct Ipc356Conductor(
    string Net, int Access, long WidthUm, IReadOnlyList<Ipc356PathPoint> Path)
{
    /// <summary>The conductor width in millimetres.</summary>
    public double WidthMm => WidthUm / 1000.0;
}

/// <summary>The two halves of a parsed IPC-D-356A file — the ACCESS POINTS (pads / vias) and the
/// CONDUCTORS (op <c>378</c> paths, empty when the file carried none). The twin of what
/// <see cref="PcbIpc356.Write(IReadOnlyList{Ipc356AccessPoint}, IReadOnlyList{Ipc356Conductor}, string)"/>
/// emits.</summary>
/// <param name="AccessPoints">Every access point (pad / via) in the file.</param>
/// <param name="Conductors">Every conductor (op <c>378</c>) in the file (empty when none).</param>
public readonly record struct Ipc356File(
    IReadOnlyList<Ipc356AccessPoint> AccessPoints, IReadOnlyList<Ipc356Conductor> Conductors);

/// <summary>
/// <b>IPC-D-356A bare-board netlist export</b> — the board-house electrical-test / netlist-compare
/// deliverable, the fab-netlist twin of the copper <see cref="PcbGerberExport">Gerber</see> /
/// <see cref="ExcellonWriter">Excellon</see> set and the assembly <see cref="PcbPickAndPlace"/> centroid.
/// It lists, per NET, every conductive ACCESS POINT — every component pad and every net-carrying via —
/// with its net name, reference designator + pin, board-frame midpoint (X, Y), layer/access code, drill
/// (for drilled features) and feature kind, so a fab can net-compare the Gerbers and program a
/// flying-probe / bed-of-nails tester. IPC-D-356<b>A</b> is the netname-carrying revision (the original
/// IPC-D-356 carried none).
///
/// <para><b>The oracle is the twin decoder plus a net reconstruction.</b> <see cref="Parse"/> reads the
/// output back, and the net PARTITION it reconstructs (which access points share a net) equals the
/// board's OWN partition — the same one <see cref="PcbConnectivity"/> / <see cref="Netlist"/> report,
/// resolved through the SAME copper model the DRC reads (the one-declaration identity). A netlist that
/// mislabels an access point is a silent fab failure, so the strong form is asserted: the SET-OF-SETS of
/// component pads grouped by file-net equals the copper model's, and a dropped / relabelled record makes
/// them differ.</para>
///
/// <para><b>Findings (each stated so it cannot rot):</b></para>
/// <list type="bullet">
/// <item><b>Units are metric micrometres</b>, declared <c>P UNITS CUST 2</c>, coordinates written
/// <c>X&lt;sign&gt;&lt;µm&gt;</c>/<c>Y…</c> — the file's own integer quantum, so the round trip is EXACT
/// and a wrong scale (mm-integers instead of µm) is a 1000× coordinate-magnitude tell (the Gerber
/// <c>%FS</c> lesson). The parser refuses any other units code by name (scoped to what the writer emits).</item>
/// <item><b>Coordinates are board-frame VERBATIM</b> (no Y-flip — the repo's coordinate honesty). A
/// bottom-side access point keeps the same board (x, y) as a top one (a plated hole serves both faces);
/// which SIDE it is probed from is the ACCESS code, not a coordinate flip.</item>
/// <item><b>Access convention:</b> <c>A00</c> = all layers (a through-hole pad or a through via reaches
/// both outer faces); otherwise the access is the 1-based number of the top-most copper layer the feature
/// is accessed from — <c>A01</c> for a top pad, <c>A0N</c> for the bottom of an N-layer board, the
/// top-most reached layer for a buried via. This is the general IPC layer convention, and it reduces to
/// the task's <c>top = 1 / bottom = 2 / all = 0</c> on a 2-layer board.</item>
/// <item><b>A blind/buried via carries its full layer SPAN</b>, because the single 2-digit access code
/// cannot: a feature reaching MORE than one copper layer but NOT both outer faces (a blind or buried via)
/// ALSO writes an explicit <c>L&lt;from&gt;-&lt;to&gt;</c> geometry token — the 1-based inclusive copper
/// range it touches (so a buried via between inner layers 2 and 3 is <c>A02 … L02-03</c>, not merely
/// <c>A02</c>) — recovered by <see cref="Parse"/> into <see cref="Ipc356AccessPoint.FromLayer"/>/
/// <see cref="Ipc356AccessPoint.ToLayer"/>. A through feature and an SMD pad write NO span token (their
/// reached set is implicit in the access code), so their records are BYTE-IDENTICAL to the narrow format —
/// the encoding only changes blind/buried-via records.</item>
/// <item><b>An over-width identity rides a continuation record, never a truncation.</b> The fixed fields
/// are 14 / 6 / 4 chars (net / refdes / pin); a longer identity is carried IN FULL by a preceding op
/// <c>379</c> continuation record (letter-tagged <c>N</c>/<c>R</c>/<c>P</c> tokens for the fields that
/// overflow), while the fixed field holds the identity's HEAD so the columns stay valid and a legacy
/// reader still gets a usable — if truncated — name. <see cref="Parse"/> applies the continuation to the
/// record that immediately follows it, recovering the full identity, so the net-reconstruction oracle
/// groups by the full net name. A board whose identities all FIT the fixed fields writes no continuation
/// record and is byte-identical to the narrow format — the mechanism only changes over-width records.</item>
/// <item><b>What is emitted / included:</b> every component pad (op <c>327</c> for SMD, <c>317</c> for
/// through-hole — drilled) and every net-carrying <see cref="Via"/> (op <c>317</c>, no component
/// reference). An unconnected / no-connect pad is each its OWN single-point net (a unique
/// <c>N/C-######</c> name), which is exactly how the copper model treats a null-net feature — so the
/// reconstructed partition matches. Board mounting / legacy holes are EXCLUDED (they carry no net — a
/// netlist lists NET access points). Conductor (trace, op <c>378</c>) records are OPT-IN
/// (<see cref="Write(PcbLayout, bool)"/>): one per routed trace — its net, its 1-based copper layer,
/// its width, and its centre-line path — for a more thorough net-compare (the conductor topology, not
/// just the access points). The default netlist emits access points ONLY, so it is byte-identical to
/// before; <see cref="ParseFile"/> reads both halves back.</item>
/// <item><b>An identity field is REFUSED, never sanitized, when neither the record NOR a continuation can
/// spell it</b> (the topological-naming rule — the net name IS the reconstruction key, so squashing
/// whitespace would misresolve two nets into one). Whitespace in any identity, or a real net colliding
/// with the synthetic <c>N/C-######</c> namespace, refuses by name. An over-WIDTH identity does NOT refuse
/// — it rides a continuation record (above).</item>
/// </list>
///
/// <para><b>Record format (fixed identity columns + a letter-prefixed geometry token stream):</b></para>
/// <code>
/// col 1-3    op code: 317 (drilled — through-hole pad / via) or 327 (SMD pad, no hole)
/// col 5-18   net name              (14, left-justified, space-padded; HEAD only when a 379 carries the full name)
/// col 20-25  reference designator  (6,  left-justified; blank for a via; HEAD only when continued)
/// col 27-30  pin number            (4,  left-justified; blank for a via; HEAD only when continued)
/// col 32+    A&lt;nn&gt; X&lt;±µm&gt; Y&lt;±µm&gt; [D&lt;µm&gt;] [L&lt;from&gt;-&lt;to&gt;]   (access, midpoint, drill on drilled records, layer span on blind/buried vias)
///
/// 378 &lt;net&gt; A&lt;nn&gt; W&lt;µm&gt; X&lt;±µm&gt; Y&lt;±µm&gt; …   (a CONDUCTOR: net + copper layer + width + a ≥2-point centre-line path; opt-in)
/// 379 [N&lt;full net&gt;] [R&lt;full refdes&gt;] [P&lt;full pin&gt;]   (a continuation record, on the line BEFORE the record it applies to; only over-width fields)
/// </code>
/// <para>with a header of <c>C</c> comment and <c>P</c> parameter records (<c>P UNITS CUST 2</c>,
/// <c>P VER IPC-D-356A</c>) and a trailing <c>999</c> end record. The reader is the round-trip oracle
/// SCOPED to what the writer emits (an unknown record code or units is refused by name).</para>
/// </summary>
public static class PcbIpc356
{
    private const int NetFieldWidth = 14;      // IPC-D-356A net-name field
    private const int RefDesFieldWidth = 6;    // reference-designator field
    private const int PinFieldWidth = 4;       // pin-number field
    private const int GeometryStart = 31;      // 0-based index of column 32 (the token stream)
    private const string ContinuationCode = "379";   // wide-identity continuation record
    private const string ConductorCode = "378";       // a conductor (a net's copper path on one layer)

    // ---- the one Compute both the writer and the oracle project --------------

    /// <summary>Projects a layout into IPC-D-356A access points — every component pad and every
    /// net-carrying via, each with its net resolved through <see cref="PcbCopperModel.FromLayout"/>
    /// (the same net tagging the DRC and connectivity read, so the netlist cannot drift). The result
    /// is sorted deterministically (by net, then location), so two emissions are byte-identical.</summary>
    /// <param name="layout">The board layout.</param>
    /// <exception cref="ArgumentException">A net name cannot be spelled in the record (over the field
    /// width or carrying whitespace), or a real net collides with the synthetic <c>N/C-######</c>
    /// namespace — refused by name.</exception>
    public static IReadOnlyList<Ipc356AccessPoint> Compute(PcbLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var model = PcbCopperModel.FromLayout(layout);

        // The component-pad sources' net tagging (a through-hole pad appears on every layer with the
        // same source and net, so one representative per source is enough); vias / traces / pours are
        // not component pads and are excluded here.
        var viaSources = new HashSet<string>(model.Vias.Select(v => v.Source), StringComparer.Ordinal);
        var traceSources = new HashSet<string>(model.TraceSources, StringComparer.Ordinal);
        var pourSources = new HashSet<string>(model.PourSources, StringComparer.Ordinal);
        var netOfPad = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var f in model.Copper)
            if (!viaSources.Contains(f.Source) && !traceSources.Contains(f.Source)
                && !pourSources.Contains(f.Source))
                netOfPad[f.Source] = f.Net;   // last-wins is fine: identical across a pad's layers

        var coppers = layout.Board.Stackup.Coppers;
        int layerCount = coppers.Count;
        var layerIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < layerCount; i++)
            layerIndex[coppers[i].Name] = i;
        var allLayerIndices = Enumerable.Range(0, layerCount).ToList();
        var placementByRef = layout.Placements.ToDictionary(p => p.Reference, StringComparer.Ordinal);

        var points = new List<Ipc356AccessPoint>();
        int syntheticCounter = 0;

        // (1) Component pads — every placed footprint pad, in a deterministic PlacedPads() order (so a
        //     synthetic net name is assigned stably before the final sort).
        foreach (var pad in layout.PlacedPads())
        {
            string source = pad.Name;   // "R1.1"
            string? net = netOfPad.GetValueOrDefault(source);

            IReadOnlyList<int> touched;
            long drillUm;
            Ipc356Feature feature;
            if (pad.Kind == PadKind.ThroughHole)
            {
                touched = allLayerIndices;
                drillUm = ToMicrons(pad.DrillDiameter);
                feature = Ipc356Feature.ThroughHolePad;
            }
            else
            {
                // An SMD pad reaches exactly the outer / inner copper it sits on.
                string layerName = placementByRef.TryGetValue(pad.Reference, out var placement)
                    ? layout.TargetLayerName(placement)
                    : layout.Board.Stackup.Top.Name;
                touched = [layerIndex.GetValueOrDefault(layerName, 0)];
                drillUm = 0;
                feature = Ipc356Feature.SurfacePad;
            }

            string netName = net ?? Synthetic(++syntheticCounter);
            RequireRealNetName(net);   // a real net may not impersonate the synthetic namespace
            var (padFrom, padTo) = LayerSpan(touched, layerCount);   // (0, 0) for a pad — a single or all-layers reach
            points.Add(new Ipc356AccessPoint(
                netName, pad.Reference, pad.PadNumber, feature,
                AccessCode(touched, layerCount), ToMicrons(pad.World.X), ToMicrons(pad.World.Y), drillUm,
                padFrom, padTo));
        }

        // (2) Vias — a net-carrying, drilled, componentless access point. Its net is non-null by
        //     construction (AddVia refuses an empty net).
        foreach (var pv in layout.PlacedVias())
        {
            RequireRealNetName(pv.Net);
            var touched = pv.Layers.Select(n => layerIndex.GetValueOrDefault(n, 0)).ToList();
            var (viaFrom, viaTo) = LayerSpan(touched, layerCount);   // a blind/buried via carries its span; a through via does not
            points.Add(new Ipc356AccessPoint(
                pv.Net, "", "", Ipc356Feature.Via,
                AccessCode(touched, layerCount), ToMicrons(pv.Center.X), ToMicrons(pv.Center.Y),
                ToMicrons(pv.DrillDiameter), viaFrom, viaTo));
        }

        points.Sort(Order);
        return points;
    }

    /// <summary>
    /// The CONDUCTOR records (op <c>378</c>) — one per routed trace, carrying the net, the 1-based
    /// copper-layer it is on, its width, and its centre-line path. A trace is copper on ONE layer, so a
    /// conductor's <see cref="Ipc356Conductor.Access"/> is that layer's number. Sorted deterministically
    /// (by net, then first point), so two emissions are byte-identical. A degenerate trace (fewer than
    /// two points) carries no path and is skipped.
    /// </summary>
    /// <param name="layout">The board layout.</param>
    /// <exception cref="ArgumentException">A trace net collides with the synthetic namespace — refused
    /// by name (the same rule the access points obey).</exception>
    public static IReadOnlyList<Ipc356Conductor> ComputeConductors(PcbLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var coppers = layout.Board.Stackup.Coppers;
        var layerIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < coppers.Count; i++)
            layerIndex[coppers[i].Name] = i;

        var conductors = new List<Ipc356Conductor>();
        foreach (var trace in layout.Traces)
        {
            if (trace.Points.Count < 2)
                continue;   // a conductor needs a path
            RequireRealNetName(trace.Net);
            int access = layerIndex.GetValueOrDefault(trace.Layer, 0) + 1;   // 1-based single layer
            var path = trace.Points
                .Select(p => new Ipc356PathPoint(ToMicrons(p.X), ToMicrons(p.Y)))
                .ToList();
            conductors.Add(new Ipc356Conductor(trace.Net, access, ToMicrons(trace.Width), path));
        }
        conductors.Sort(ConductorOrder);
        return conductors;
    }

    // ---- writing --------------------------------------------------------------

    /// <summary>Writes the IPC-D-356A netlist text for a layout (no disk touched) — access points only,
    /// byte-identical to before. Pass <see cref="Write(PcbLayout, bool)"/> with <c>true</c> to also emit
    /// conductor (op <c>378</c>) records.</summary>
    public static string Write(PcbLayout layout) => Write(layout, includeConductors: false);

    /// <summary>Writes the IPC-D-356A netlist text for a layout, optionally including CONDUCTOR records
    /// (op <c>378</c>, a net's copper path per routed trace) for a more thorough net-compare. With
    /// <paramref name="includeConductors"/> false the output is byte-identical to the access-point-only
    /// netlist.</summary>
    public static string Write(PcbLayout layout, bool includeConductors)
    {
        ArgumentNullException.ThrowIfNull(layout);
        string design = string.IsNullOrWhiteSpace(layout.Schematic.Name) ? "board" : layout.Schematic.Name;
        var conductors = includeConductors
            ? ComputeConductors(layout)
            : (IReadOnlyList<Ipc356Conductor>)[];
        return Write(Compute(layout), conductors, design);
    }

    /// <summary>Writes the IPC-D-356A netlist text from computed access points and a design name.</summary>
    /// <exception cref="ArgumentException">An identity field (net / refdes / pin) is too long or carries
    /// whitespace to be spelled in a record — refused by name (an identity is never sanitized).</exception>
    public static string Write(IReadOnlyList<Ipc356AccessPoint> points, string design = "board") =>
        Write(points, [], design);

    /// <summary>Writes the IPC-D-356A netlist text from computed access points AND conductors. An empty
    /// conductor list emits no <c>378</c> records, so the output is byte-identical to the access-point
    /// overload — which is what keeps a conductor-free file (the default) unchanged.</summary>
    /// <exception cref="ArgumentException">An identity field is too long or carries whitespace — refused
    /// by name.</exception>
    public static string Write(
        IReadOnlyList<Ipc356AccessPoint> points,
        IReadOnlyList<Ipc356Conductor> conductors,
        string design = "board")
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(conductors);

        // Coordinate / drill field widths are DERIVED from the data (min IPC widths) over BOTH the access
        // points and the conductors' path points / widths, so a large board widens the column rather than
        // overflowing; they are cosmetic (the tokens are self-delimiting by their leading letter and
        // sign), so the reader needs no a-priori knowledge of them.
        long maxCoord = 1, maxDrill = 1;
        foreach (var p in points)
        {
            maxCoord = Math.Max(maxCoord, Math.Max(Math.Abs(p.Xum), Math.Abs(p.Yum)));
            if (p.IsDrilled)
                maxDrill = Math.Max(maxDrill, p.DrillUm);
        }
        foreach (var cnd in conductors)
        {
            maxDrill = Math.Max(maxDrill, cnd.WidthUm);
            foreach (var pt in cnd.Path)
                maxCoord = Math.Max(maxCoord, Math.Max(Math.Abs(pt.Xum), Math.Abs(pt.Yum)));
        }
        int coordWidth = Math.Max(6, maxCoord.ToString(CultureInfo.InvariantCulture).Length);
        int drillWidth = Math.Max(4, maxDrill.ToString(CultureInfo.InvariantCulture).Length);

        var sb = new StringBuilder();
        sb.Append("C  IPC-D-356A netlist generated by EngrCAD\n");
        sb.Append("C  Design: ").Append(design).Append('\n');
        sb.Append("P  UNITS CUST 2\n");   // metric — coordinates in micrometres (0.001 mm)
        sb.Append("P  VER IPC-D-356A\n");
        sb.Append("P  IMAGE PRIMARY\n");

        foreach (var p in points)
        {
            // An over-width identity is carried IN FULL by a preceding 379 continuation record; the
            // record's fixed field then holds its head. A record all of whose identities FIT writes no
            // continuation, so it is byte-identical to the narrow format.
            string continuation = Continuation(p);
            if (continuation.Length > 0)
                sb.Append(continuation).Append('\n');
            sb.Append(FormatRecord(p, coordWidth, drillWidth)).Append('\n');
        }

        // Conductor records follow the access points. An over-width net rides the SAME 379 continuation
        // (an N token only — a conductor has no refdes / pin), so the fixed net field holds its head.
        foreach (var cnd in conductors)
        {
            string continuation = ConductorContinuation(cnd.Net);
            if (continuation.Length > 0)
                sb.Append(continuation).Append('\n');
            sb.Append(FormatConductor(cnd, coordWidth, drillWidth)).Append('\n');
        }

        sb.Append("999\n");   // end record
        return sb.ToString();
    }

    /// <summary>Writes the IPC-D-356A netlist for a layout to <c>&lt;directory&gt;/&lt;name&gt;.ipc</c>
    /// (the directory is created if needed) and returns the path written.</summary>
    public static string WriteFile(PcbLayout layout, string directory, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory.CreateDirectory(directory);
        string b = string.IsNullOrWhiteSpace(name)
            ? (layout.Schematic.Name.Length > 0 ? Sanitize(layout.Schematic.Name) : "board")
            : Sanitize(name);
        if (b.Length == 0)
            b = "board";
        string path = Path.Combine(directory, b + ".ipc");
        File.WriteAllText(path, Write(layout));
        return path;
    }

    private static string FormatRecord(in Ipc356AccessPoint p, int coordWidth, int drillWidth)
    {
        string op = p.IsDrilled ? "317" : "327";
        var sb = new StringBuilder(48);
        sb.Append(op);                                     // cols 1-3
        sb.Append(' ');                                    // col 4
        sb.Append(Field(p.Net, NetFieldWidth, "net name"));       // cols 5-18
        sb.Append(' ');                                    // col 19
        sb.Append(Field(p.RefDes, RefDesFieldWidth, "reference designator"));  // cols 20-25
        sb.Append(' ');                                    // col 26
        sb.Append(Field(p.Pin, PinFieldWidth, "pin number"));     // cols 27-30
        sb.Append(' ');                                    // col 31

        // Geometry token stream (col 32+): access, midpoint, and — only for a drilled record — the drill.
        sb.Append('A').Append(p.Access.ToString("D2", CultureInfo.InvariantCulture));
        sb.Append(" X").Append(Coord(p.Xum, coordWidth));
        sb.Append(" Y").Append(Coord(p.Yum, coordWidth));
        if (p.IsDrilled)
        {
            // A drilled feature MUST spell a positive drill (op 317 ⇒ D > 0). A drill that rounds below
            // the file's 1 µm quantum cannot be represented, so it is refused by name rather than emitted
            // as an unparseable D0 (the identity-can't-be-spelled rule, one level down at the geometry).
            if (p.DrillUm <= 0)
                throw new ArgumentException(
                    $"A drilled {p.Feature} at ({p.Xmm:g6}, {p.Ymm:g6}) has a drill below the file's 1 µm "
                    + "resolution, so it cannot be spelled as a positive drill.", nameof(p));
            sb.Append(" D").Append(p.DrillUm.ToString(CultureInfo.InvariantCulture).PadLeft(drillWidth, '0'));
        }

        // A blind/buried via carries its full 1-based inclusive copper-layer span; the single access
        // code cannot spell a range. A through feature and an SMD pad state no span (see LayerSpan), so
        // no token is written and their records stay byte-identical to the narrow format.
        if (p.HasLayerSpan)
            sb.Append(" L")
              .Append(p.FromLayer.ToString("D2", CultureInfo.InvariantCulture))
              .Append('-')
              .Append(p.ToLayer.ToString("D2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // The wide-identity continuation: a preceding op-379 record carrying, IN FULL, each identity (net /
    // refdes / pin) that overflows its fixed field, as letter-tagged N/R/P tokens (the same style as the
    // geometry stream). Returns "" when every identity fits — so a record all of whose fields fit writes
    // no continuation record and stays byte-identical to the narrow format.
    private static string Continuation(in Ipc356AccessPoint p)
    {
        var tokens = new StringBuilder();
        AppendWide(tokens, 'N', p.Net, NetFieldWidth);
        AppendWide(tokens, 'R', p.RefDes, RefDesFieldWidth);
        AppendWide(tokens, 'P', p.Pin, PinFieldWidth);
        return tokens.Length == 0 ? "" : ContinuationCode + " " + tokens;
    }

    private static void AppendWide(StringBuilder tokens, char tag, string? value, int width)
    {
        value ??= "";
        if (value.Length <= width)
            return;   // fits the fixed field — no continuation token needed
        if (tokens.Length > 0)
            tokens.Append(' ');
        tokens.Append(tag).Append(value);   // whitespace is refused by Field on the same point
    }

    // A conductor (op 378) record: op + net(14) + A<layer> W<widthµm> then one X/Y token pair per path
    // point (the same self-delimiting geometry stream the access-point records use). The net rides the
    // same fixed field (over-width → a preceding 379 N-token continuation), so a legacy reader still
    // gets a usable name.
    private static string FormatConductor(in Ipc356Conductor c, int coordWidth, int drillWidth)
    {
        if (c.Path.Count < 2)
            throw new ArgumentException(
                $"A conductor on net '{c.Net}' has fewer than two path points, so it has no path.", nameof(c));
        var sb = new StringBuilder(64);
        sb.Append(ConductorCode);                                 // cols 1-3
        sb.Append(' ');                                           // col 4
        sb.Append(Field(c.Net, NetFieldWidth, "net name"));       // cols 5-18
        sb.Append(' ');                                           // col 19
        sb.Append('A').Append(c.Access.ToString("D2", CultureInfo.InvariantCulture));
        sb.Append(" W").Append(c.WidthUm.ToString(CultureInfo.InvariantCulture).PadLeft(drillWidth, '0'));
        foreach (var pt in c.Path)
        {
            sb.Append(" X").Append(Coord(pt.Xum, coordWidth));
            sb.Append(" Y").Append(Coord(pt.Yum, coordWidth));
        }
        return sb.ToString();
    }

    // A conductor's wide-net continuation — the 379 record carrying the full net when it overflows its
    // fixed field (a conductor has no refdes / pin, so only an N token). "" when the net fits.
    private static string ConductorContinuation(string net)
    {
        var tokens = new StringBuilder();
        AppendWide(tokens, 'N', net, NetFieldWidth);
        return tokens.Length == 0 ? "" : ContinuationCode + " " + tokens;
    }

    // A stable total order for conductors (by net, layer, first point, width, point count), so two
    // emissions are byte-identical.
    private static int ConductorOrder(Ipc356Conductor a, Ipc356Conductor b)
    {
        int c = string.CompareOrdinal(a.Net, b.Net);
        if (c != 0) return c;
        c = a.Access.CompareTo(b.Access);
        if (c != 0) return c;
        var pa = a.Path.Count > 0 ? a.Path[0] : default;
        var pb = b.Path.Count > 0 ? b.Path[0] : default;
        c = pa.Xum.CompareTo(pb.Xum);
        if (c != 0) return c;
        c = pa.Yum.CompareTo(pb.Yum);
        if (c != 0) return c;
        c = a.WidthUm.CompareTo(b.WidthUm);
        if (c != 0) return c;
        return a.Path.Count.CompareTo(b.Path.Count);
    }

    // ---- the twin decoder (the round-trip oracle) ----------------------------

    /// <summary>Reads an IPC-D-356A netlist back into access points — the TWIN of <see cref="Write"/>,
    /// so the round trip is provable rather than assumed. It is scoped to what the writer emits: only
    /// <c>317</c>/<c>327</c> records and <c>P UNITS CUST 2</c> are accepted, everything else refused by
    /// name.</summary>
    /// <exception cref="FormatException">The text is not IPC-D-356A of the writer's shape — a missing /
    /// unsupported units record, an unknown record code, a malformed record (too short, no net, missing
    /// coordinate token, a drilled record with no drill or an SMD record with one) — each refused by name.</exception>
    public static IReadOnlyList<Ipc356AccessPoint> Parse(string text) => ParseFile(text).AccessPoints;

    /// <summary>Reads an IPC-D-356A netlist back into its CONDUCTOR (op <c>378</c>) records — the twin of
    /// <see cref="ComputeConductors"/>. Empty when the file carries none.</summary>
    public static IReadOnlyList<Ipc356Conductor> ParseConductors(string text) => ParseFile(text).Conductors;

    /// <summary>Reads an IPC-D-356A netlist back into BOTH halves — access points (pads / vias) and
    /// conductors (op <c>378</c>) — the twin of the combined <see cref="Write(IReadOnlyList{Ipc356AccessPoint},
    /// IReadOnlyList{Ipc356Conductor}, string)"/>, so the round trip is provable rather than assumed. It
    /// is scoped to what the writer emits: only <c>317</c>/<c>327</c>/<c>378</c> records, a <c>379</c>
    /// continuation, and <c>P UNITS CUST 2</c> are accepted, everything else refused by name.</summary>
    /// <exception cref="FormatException">The text is not IPC-D-356A of the writer's shape — a missing /
    /// unsupported units record, an unknown record code, a malformed record — each refused by name.</exception>
    public static Ipc356File ParseFile(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var points = new List<Ipc356AccessPoint>();
        var conductors = new List<Ipc356Conductor>();
        bool unitsSeen = false;
        int lineNo = 0;
        // A pending wide-identity continuation (379) applies to the NEXT record (a test record OR a
        // conductor) — accumulate then consume. A continuation followed by no record, or by another
        // continuation, is malformed.
        string? contNet = null, contRef = null, contPin = null;
        bool pending = false;

        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
                continue;

            char c0 = line[0];
            if (c0 == 'C')
                continue;   // comment
            if (c0 == 'P')
            {
                var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 2 && t[1] == "UNITS")
                {
                    // P UNITS CUST 2 — metric micrometres. The writer emits only this; refuse others.
                    if (t.Length < 4 || t[2] != "CUST" || t[3] != "2")
                        throw new FormatException(
                            $"Unsupported IPC-D-356A units on line {lineNo} ('{line.Trim()}'); this "
                            + "reader supports 'P UNITS CUST 2' (metric micrometres) only.");
                    unitsSeen = true;
                }
                continue;   // other parameter records (VER, IMAGE, …) are ignored
            }
            if (line == "999")
                break;   // end record

            if (c0 != '3')
                throw new FormatException(
                    $"Unknown IPC-D-356A record on line {lineNo}: '{line.Trim()}'.");

            if (line.Length >= 3 && line[..3] == ContinuationCode)
            {
                // A wide-identity continuation record — stash the full net/refdes/pin for the next record.
                if (pending)
                    throw new FormatException(
                        $"IPC-D-356A continuation record (379) on line {lineNo} follows another continuation "
                        + "with no test record between them.");
                (contNet, contRef, contPin) = ParseContinuation(line, lineNo);
                pending = true;
                continue;
            }

            if (line.Length >= 3 && line[..3] == ConductorCode)
            {
                // A conductor record — its net may ride the pending continuation (an N token); refdes/pin
                // do not apply to a conductor.
                conductors.Add(ParseConductorRecord(line, lineNo, contNet));
                contNet = contRef = contPin = null;
                pending = false;
                continue;
            }

            points.Add(ParseRecord(line, lineNo, contNet, contRef, contPin));
            contNet = contRef = contPin = null;
            pending = false;
        }

        if (pending)
            throw new FormatException(
                "The IPC-D-356A file ends with a continuation record (379) that no record follows.");
        if (!unitsSeen)
            throw new FormatException(
                "The IPC-D-356A file declares no units — a 'P UNITS CUST 2' record is required so the "
                + "coordinate scale is unambiguous (a wrong scale is a 10^n tell).");
        return new Ipc356File(points, conductors);
    }

    // Parses a 379 wide-identity continuation record — the full net / refdes / pin as letter-tagged
    // N/R/P tokens (only the fields that overflowed their fixed columns). At least one token is required.
    private static (string? Net, string? RefDes, string? Pin) ParseContinuation(string line, int lineNo)
    {
        string? net = null, refDes = null, pin = null;
        var tokens = line[ContinuationCode.Length..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var tok in tokens)
        {
            string value = tok[1..];
            switch (tok[0])
            {
                case 'N': net = value; break;
                case 'R': refDes = value; break;
                case 'P': pin = value; break;
                default:
                    throw new FormatException(
                        $"IPC-D-356A continuation record (379) on line {lineNo} has an unknown field token "
                        + $"'{tok}' (expected N/R/P).");
            }
        }
        if (net is null && refDes is null && pin is null)
            throw new FormatException(
                $"IPC-D-356A continuation record (379) on line {lineNo} carries no field tokens.");
        return (net, refDes, pin);
    }

    private static Ipc356AccessPoint ParseRecord(
        string line, int lineNo, string? contNet, string? contRef, string? contPin)
    {
        if (line.Length < GeometryStart)
            throw new FormatException(
                $"IPC-D-356A record on line {lineNo} is too short ('{line.Trim()}').");
        string op = line[..3];
        if (op != "317" && op != "327")
            throw new FormatException(
                $"Unknown IPC-D-356A operation code '{op}' on line {lineNo} (expected 317 or 327).");

        // A preceding 379 continuation carries the FULL identity for any field that overflowed its fixed
        // column (whose head the column still holds); otherwise read the field verbatim.
        string net = contNet ?? line.Substring(4, NetFieldWidth).TrimEnd();
        string refDes = contRef ?? line.Substring(19, RefDesFieldWidth).TrimEnd();
        string pin = contPin ?? line.Substring(26, PinFieldWidth).TrimEnd();
        if (net.Length == 0)
            throw new FormatException($"IPC-D-356A record on line {lineNo} names no net.");

        // Geometry token stream from column 32: A<nn> X<±µm> Y<±µm> [D<µm>] [L<from>-<to>].
        var tokens = line[GeometryStart..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int? access = null;
        long? x = null, y = null, drill = null;
        int fromLayer = 0, toLayer = 0;
        foreach (var tok in tokens)
        {
            char k = tok[0];
            string rest = tok[1..];
            switch (k)
            {
                case 'A': access = ParseInt(rest, lineNo, "access"); break;
                case 'X': x = ParseLong(rest, lineNo, "X"); break;
                case 'Y': y = ParseLong(rest, lineNo, "Y"); break;
                case 'D': drill = ParseLong(rest, lineNo, "drill"); break;
                case 'L': (fromLayer, toLayer) = ParseSpan(rest, lineNo); break;
                default:
                    throw new FormatException(
                        $"IPC-D-356A record on line {lineNo} has an unknown geometry token '{tok}'.");
            }
        }
        if (access is null || x is null || y is null)
            throw new FormatException(
                $"IPC-D-356A record on line {lineNo} is missing an access (A) or coordinate (X/Y) token.");

        bool drilled = op == "317";
        if (drilled && (drill is null || drill <= 0))
            throw new FormatException(
                $"IPC-D-356A drilled record (317) on line {lineNo} carries no positive drill (D) token.");
        if (!drilled && drill is not null)
            throw new FormatException(
                $"IPC-D-356A surface record (327) on line {lineNo} carries a drill (D) token; an SMD pad "
                + "has no hole.");
        if (op == "327" && refDes.Length == 0)
            throw new FormatException(
                $"IPC-D-356A surface record (327) on line {lineNo} names no component.");

        Ipc356Feature feature = op == "327"
            ? Ipc356Feature.SurfacePad
            : refDes.Length == 0 ? Ipc356Feature.Via : Ipc356Feature.ThroughHolePad;

        return new Ipc356AccessPoint(
            net, refDes, pin, feature, access.Value, x.Value, y.Value, drill ?? 0, fromLayer, toLayer);
    }

    // Parses a conductor (378) record: net (fixed field, or a preceding 379 N continuation) + a token
    // stream A<layer> W<widthµm> then one X/Y pair per path point. Refuses a missing access/width, a
    // mismatched X/Y count, or fewer than two points — by name.
    private static Ipc356Conductor ParseConductorRecord(string line, int lineNo, string? contNet)
    {
        if (line.Length < 4 + NetFieldWidth)
            throw new FormatException(
                $"IPC-D-356A conductor record (378) on line {lineNo} is too short ('{line.Trim()}').");
        string net = contNet ?? line.Substring(4, NetFieldWidth).TrimEnd();
        if (net.Length == 0)
            throw new FormatException($"IPC-D-356A conductor record (378) on line {lineNo} names no net.");

        var tokens = line[(4 + NetFieldWidth)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int? access = null;
        long? width = null;
        var xs = new List<long>();
        var ys = new List<long>();
        foreach (var tok in tokens)
        {
            char k = tok[0];
            string rest = tok[1..];
            switch (k)
            {
                case 'A': access = ParseInt(rest, lineNo, "access"); break;
                case 'W': width = ParseLong(rest, lineNo, "width"); break;
                case 'X': xs.Add(ParseLong(rest, lineNo, "X")); break;
                case 'Y': ys.Add(ParseLong(rest, lineNo, "Y")); break;
                default:
                    throw new FormatException(
                        $"IPC-D-356A conductor record (378) on line {lineNo} has an unknown token '{tok}'.");
            }
        }
        if (access is null || width is null)
            throw new FormatException(
                $"IPC-D-356A conductor record (378) on line {lineNo} is missing an access (A) or width (W) token.");
        if (xs.Count != ys.Count)
            throw new FormatException(
                $"IPC-D-356A conductor record (378) on line {lineNo} has mismatched X/Y coordinate tokens "
                + $"({xs.Count} X, {ys.Count} Y).");
        if (xs.Count < 2)
            throw new FormatException(
                $"IPC-D-356A conductor record (378) on line {lineNo} has fewer than two path points.");

        var path = new List<Ipc356PathPoint>(xs.Count);
        for (int i = 0; i < xs.Count; i++)
            path.Add(new Ipc356PathPoint(xs[i], ys[i]));
        return new Ipc356Conductor(net, access.Value, width.Value, path);
    }

    // Parses an L<from>-<to> layer-span token into its two 1-based copper-layer numbers.
    private static (int From, int To) ParseSpan(string s, int lineNo)
    {
        int dash = s.IndexOf('-');
        if (dash > 0 && dash < s.Length - 1
            && int.TryParse(s[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out int a)
            && int.TryParse(s[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int b)
            && a > 0 && b > 0)
            return (a, b);
        throw new FormatException(
            $"IPC-D-356A record on line {lineNo} has a malformed layer span 'L{s}' "
            + "(expected L<from>-<to>, 1-based copper layers).");
    }

    // ---- grouping (for a net-by-net reconstruction) --------------------------

    /// <summary>Groups access points by net name (in name order) — the netlist view a fab
    /// net-compares against the Gerbers, and the reconstruction the round-trip oracle checks.</summary>
    public static IReadOnlyList<Ipc356Net> Nets(IEnumerable<Ipc356AccessPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var byNet = new Dictionary<string, List<Ipc356AccessPoint>>(StringComparer.Ordinal);
        foreach (var p in points)
        {
            if (!byNet.TryGetValue(p.Net, out var list))
                byNet[p.Net] = list = [];
            list.Add(p);
        }
        return byNet
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new Ipc356Net(kv.Key, kv.Value))
            .ToList();
    }

    // ---- access code, ordering, formatting, refusals -------------------------

    /// <summary>The IPC access code from a feature's touched copper layers (stackup indices, top-most =
    /// 0): <c>0</c> when it reaches BOTH outer faces (a through feature), else the 1-based number of the
    /// top-most reached layer (top = 1, bottom = N; an inner layer's own number for a buried via).</summary>
    private static int AccessCode(IReadOnlyList<int> touched, int layerCount)
    {
        bool top = false, bottom = false;
        int min = int.MaxValue;
        foreach (int i in touched)
        {
            if (i == 0) top = true;
            if (i == layerCount - 1) bottom = true;
            if (i < min) min = i;
        }
        return top && bottom ? 0 : min + 1;
    }

    /// <summary>The 1-based inclusive copper-layer span a feature reaches (top-most copper = 1), for the
    /// explicit <c>L&lt;from&gt;-&lt;to&gt;</c> per-inner-layer token — returned ONLY for a blind/buried
    /// via, a feature reaching MORE than one copper layer but NOT both outer faces, whose single
    /// top-most-layer <see cref="AccessCode"/> alone cannot spell the range. A through feature (both
    /// faces) and a single-layer SMD pad return <c>(0, 0)</c>: their reached set is implicit in the
    /// access code, so no token is written and their records stay byte-identical to the narrow
    /// format.</summary>
    private static (int From, int To) LayerSpan(IReadOnlyList<int> touched, int layerCount)
    {
        int min = int.MaxValue, max = int.MinValue;
        foreach (int i in touched)
        {
            if (i < min) min = i;
            if (i > max) max = i;
        }
        bool through = min == 0 && max == layerCount - 1;
        return max > min && !through ? (min + 1, max + 1) : (0, 0);
    }

    // A stable total order: by net, then location (X, Y), then component / pin / feature / access / drill,
    // so ties are fully broken and two emissions are byte-identical.
    private static int Order(Ipc356AccessPoint a, Ipc356AccessPoint b)
    {
        int c = string.CompareOrdinal(a.Net, b.Net);
        if (c != 0) return c;
        c = a.Xum.CompareTo(b.Xum);
        if (c != 0) return c;
        c = a.Yum.CompareTo(b.Yum);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.RefDes, b.RefDes);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Pin, b.Pin);
        if (c != 0) return c;
        c = a.Feature.CompareTo(b.Feature);
        if (c != 0) return c;
        c = a.Access.CompareTo(b.Access);
        if (c != 0) return c;
        c = a.DrillUm.CompareTo(b.DrillUm);
        if (c != 0) return c;
        c = a.FromLayer.CompareTo(b.FromLayer);
        if (c != 0) return c;
        return a.ToLayer.CompareTo(b.ToLayer);
    }

    private static long ToMicrons(double mm) =>
        (long)Math.Round(mm * 1000.0, MidpointRounding.AwayFromZero);

    private static string Coord(long um, int width) =>
        (um < 0 ? "-" : "+") + Math.Abs(um).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

    // A left-justified, space-padded fixed-width identity field. Whitespace is refused by name (it cannot
    // be spelled in a fixed column nor in a continuation token — the net name IS the reconstruction key,
    // and squashing two names into one is the exact silent-misresolve a netlist must not do). An
    // over-width value is NOT refused: its full text rides a preceding 379 continuation (see Continuation)
    // and the fixed field holds its head.
    private static string Field(string value, int width, string what)
    {
        value ??= "";
        foreach (char ch in value)
            if (char.IsWhiteSpace(ch))
                throw new ArgumentException(
                    $"IPC-D-356A {what} '{value}' contains whitespace, which neither a fixed-column record "
                    + "nor a continuation token can spell (an identity is refused, never sanitized).", nameof(value));
        // A value over the fixed width is carried IN FULL by a preceding 379 continuation record (see
        // Continuation); the fixed field then holds its HEAD, so the columns stay valid and a legacy
        // reader still gets a usable — if truncated — identity. A value that fits is written verbatim, so
        // a record all of whose identities fit is byte-identical to the narrow format.
        string head = value.Length > width ? value[..width] : value;
        return head.PadRight(width);
    }

    private const string SyntheticPrefix = "N/C-";

    private static string Synthetic(int index) =>
        SyntheticPrefix + index.ToString("D6", CultureInfo.InvariantCulture);

    private static bool IsSyntheticName(string name) =>
        name.Length == SyntheticPrefix.Length + 6
        && name.StartsWith(SyntheticPrefix, StringComparison.Ordinal)
        && name.AsSpan(SyntheticPrefix.Length).ToString().All(char.IsAsciiDigit);

    // A real (schematic) net name may not impersonate the synthetic unconnected-pad namespace, or an
    // unconnected pad and a real net would silently share a name — the one thing a netlist must not do.
    private static void RequireRealNetName(string? net)
    {
        if (net is not null && IsSyntheticName(net))
            throw new ArgumentException(
                $"Net name '{net}' collides with the synthetic unconnected-pad namespace "
                + $"('{SyntheticPrefix}######'); rename the net.", nameof(net));
    }

    private static int ParseInt(string s, int lineNo, string what)
    {
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            return v;
        throw new FormatException($"IPC-D-356A record on line {lineNo} has a malformed {what} '{s}'.");
    }

    private static long ParseLong(string s, int lineNo, string what)
    {
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
            return v;
        throw new FormatException($"IPC-D-356A record on line {lineNo} has a malformed {what} '{s}'.");
    }

    private static string Sanitize(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_').ToArray();
        return new string(chars);
    }
}
