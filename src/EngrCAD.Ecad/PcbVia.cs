using EngrCAD.Core.Geometry2;

namespace EngrCAD.Ecad;

public sealed partial class PcbLayout
{
    private readonly List<Via> _vias = [];

    /// <summary>The vias placed on this layout, in declaration order. Vias are LAYOUT TRUTH (a placed
    /// signal or stitching via is part of the design), so they round-trip in the layout file.</summary>
    public IReadOnlyList<Via> Vias => _vias;

    // ---- declaring vias ------------------------------------------------------

    /// <summary>
    /// Adds a net-carrying cross-layer via at <c>(x, y)</c> spanning the copper layers
    /// <paramref name="fromLayer"/>..<paramref name="toLayer"/> of the board's stackup, with a drill
    /// and an annular pad diameter. The via TYPE is DERIVED from the span (see <see cref="ViaType"/>);
    /// pass <paramref name="require"/> to ASSERT a kind and be refused by name if the span disagrees
    /// (e.g. a <see cref="ViaType.Microvia"/> across non-adjacent layers).
    /// </summary>
    /// <param name="net">The via's net (a via ties one net across layers; an empty net is refused).</param>
    /// <param name="x">The via centre X (mm, board coordinates).</param>
    /// <param name="y">The via centre Y (mm, board coordinates).</param>
    /// <param name="fromLayer">One end of the span, a copper layer name of the stackup.</param>
    /// <param name="toLayer">The other end, a copper layer name of the stackup.</param>
    /// <param name="drill">The finished drill diameter (mm), positive.</param>
    /// <param name="pad">The annular pad diameter (mm), strictly greater than the drill.</param>
    /// <param name="require">If set, the DERIVED type must equal this kind or the via is refused by
    /// name — the way a caller states "this is meant to be a microvia" and gets a named refusal when
    /// the layers are not adjacent.</param>
    /// <exception cref="ArgumentException">No net, a non-positive drill, a pad not larger than the
    /// drill, a layer not in the stackup, the two ends the same layer, a centre off the board
    /// outline, or a derived type not matching <paramref name="require"/> — all refused by name.</exception>
    public PcbLayout AddVia(
        string net, double x, double y, string fromLayer, string toLayer,
        double drill, double pad, ViaType? require = null)
    {
        var via = new Via(net, x, y, fromLayer, toLayer, drill, pad);
        ValidateVia(via, require);
        _vias.Add(via);
        return this;
    }

    /// <summary>Adds a fully-formed <see cref="Via"/> (the same validation as <see cref="AddVia"/>).</summary>
    public PcbLayout WithVia(Via via)
    {
        ValidateVia(via, require: null);
        _vias.Add(via);
        return this;
    }

    // Used by the loader to reconstruct a via verbatim (the same validation, so a saved-then-loaded
    // via is refused by name for the same reasons a freshly-declared one is).
    internal void AddLoadedVia(Via via)
    {
        try
        {
            ValidateVia(via, require: null);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"A saved via is invalid: {ex.Message}");
        }
        _vias.Add(via);
    }

    private void ValidateVia(in Via via, ViaType? require)
    {
        if (string.IsNullOrEmpty(via.Net))
            throw new ArgumentException(
                $"A via at ({via.X:g6}, {via.Y:g6}) has no net — a via ties one net across layers "
                + "(a signal via transitions a trace, a stitching via ties a plane), so it must name "
                + "a net.", nameof(via));
        if (!(via.DrillDiameter > 0))
            throw new ArgumentException(
                $"Via on net '{via.Net}' needs a positive drill diameter (got {via.DrillDiameter:g6}).",
                nameof(via));
        if (!(via.PadDiameter > via.DrillDiameter))
            throw new ArgumentException(
                $"Via on net '{via.Net}' needs a pad diameter strictly larger than its drill "
                + $"(pad {via.PadDiameter:g6} ≤ drill {via.DrillDiameter:g6}); a via needs a positive "
                + "annular ring.", nameof(via));
        if (!Board.ContainsInOutline(via.Center))
            throw new ArgumentException(
                $"Via on net '{via.Net}' at ({via.X:g6}, {via.Y:g6}) is off the board outline.",
                nameof(via));

        // A via drills vertically, so its centre must not fall inside an embedded component's milled
        // cavity — that pocket holds the embedded part, and a via there would breach it. Conservative
        // in z (refused wherever the centre lies in a cavity footprint), which is the safe v1 rule.
        foreach (var cavity in Cavities())
            if (cavity.Footprint.Contains(via.Center))
                throw new ArgumentException(
                    $"Via on net '{via.Net}' at ({via.X:g6}, {via.Y:g6}) falls inside the milled "
                    + $"cavity for embedded component '{cavity.Source}'.", nameof(via));

        // Resolve the span (throws by name for an unknown layer / a zero-span via), and — if the
        // caller asked for a specific kind — refuse a span that derives to a different one.
        var (type, layers) = ViaGeometry.Resolve(Board.Stackup, via);
        if (require is { } wanted && type != wanted)
            throw new ArgumentException(
                $"Via on net '{via.Net}' was declared as a {wanted} but its span "
                + $"({via.FromLayer}..{via.ToLayer}, {layers.Count} copper layers) is a {type}"
                + (wanted == ViaType.Microvia
                    ? " — a microvia spans exactly one dielectric (adjacent copper layers)." : "."),
                nameof(require));
    }

    // ---- derived via facts (one source of truth for the type / touched layers) ----

    /// <summary>The DERIVED type of a via — through / blind / buried / microvia — a pure function of
    /// its span against the board's stackup.</summary>
    public ViaType ViaTypeOf(in Via via) => ViaGeometry.Resolve(Board.Stackup, via).Type;

    /// <summary>The copper layers a via touches (top-most first) — the contiguous copper range
    /// between its two ends. It places an annular pad on each of these, tagged with its net.</summary>
    public IReadOnlyList<string> ViaLayers(in Via via) => ViaGeometry.Resolve(Board.Stackup, via).Layers;

    /// <summary>The resolved copper of a via — centre, net, derived type, touched layers and annular
    /// pad — for the connectivity engine and the via DRC. Auto-named <c>"via1"</c>, <c>"via2"</c>, …
    /// in declaration order.</summary>
    public PlacedVia PlacedViaOf(int index)
    {
        var via = _vias[index];
        var (type, layers) = ViaGeometry.Resolve(Board.Stackup, via);
        return new PlacedVia(via, ViaSource(index), type, layers);
    }

    /// <summary>The resolved copper of every via, in declaration order.</summary>
    public IReadOnlyList<PlacedVia> PlacedVias()
    {
        var list = new List<PlacedVia>(_vias.Count);
        for (int i = 0; i < _vias.Count; i++)
            list.Add(PlacedViaOf(i));
        return list;
    }

    /// <summary>The report id a via's copper features are tagged with (<c>"via1"</c>, one-based in
    /// declaration order) — stable so the DRC and connectivity name the same via.</summary>
    internal static string ViaSource(int index) => $"via{index + 1}";

    // ---- connectivity (the routing prerequisite) -----------------------------

    /// <summary>The net-connectivity of this layout — which nets are joined by copper (through
    /// same-layer touch AND vias) and which are still an unrouted ratsnest. The seam an autorouter
    /// reuses; a net whose pads sit on different layers is CONNECTED once a via ties them.</summary>
    public ConnectivityReport Connectivity() => PcbConnectivity.Analyze(this);

    /// <summary>Whether every pad of a named net is joined into one connected copper component.</summary>
    public bool IsNetConnected(string net) => Connectivity().Of(net).IsConnected;
}
