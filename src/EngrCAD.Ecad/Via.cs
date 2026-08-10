using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Ecad;

/// <summary>
/// What KIND of cross-layer connection a <see cref="Via"/> is — DERIVED from the copper layers it
/// spans, never stored twice. The type is a pure function of the span against the board's stackup
/// (see <see cref="ViaGeometry.Resolve"/>): a via that reaches both outer faces is
/// <see cref="Through"/>, one that reaches a single outer face is <see cref="Blind"/>, one buried
/// between inner layers is <see cref="Buried"/>, and a via across a single dielectric (adjacent
/// copper layers) is a <see cref="Microvia"/> — the laser-drilled single-hop kind.
/// </summary>
public enum ViaType
{
    /// <summary>Outer face to outer face — the plated through hole that reaches every copper layer.</summary>
    Through,

    /// <summary>An outer copper layer to an inner one, spanning more than one dielectric (a single
    /// dielectric hop from a face is a <see cref="Microvia"/>).</summary>
    Blind,

    /// <summary>Inner copper to inner copper, reaching neither face (spanning more than one
    /// dielectric; a single inner hop is a <see cref="Microvia"/>).</summary>
    Buried,

    /// <summary>Adjacent copper layers — a single dielectric hop (a laser-drilled microvia). The
    /// adjacency is the DEFINITION, and it takes precedence over blind/buried once the span is
    /// exactly one dielectric.</summary>
    Microvia,
}

/// <summary>
/// A net-carrying cross-layer connection at <c>(x, y)</c> spanning the copper layers <c>[From, To]</c>
/// of the board's stackup, with a drill diameter and an annular copper PAD diameter. A signal via
/// transitions a routed trace between layers; a stitching via ties a plane — either way it belongs
/// to a <see cref="Net"/>, so a via with no net is refused by name at <see cref="PcbLayout.AddVia"/>.
///
/// <para><b>The via TYPE is DERIVED, not stored twice.</b> <see cref="From"/> and <see cref="To"/>
/// name copper layers of the stackup, and the span against the stackup decides
/// <see cref="ViaType"/> (see <see cref="ViaGeometry.Resolve"/>) — so a via cannot claim one kind
/// and mean another.</para>
///
/// <para>The via places an annular pad (a disc of <see cref="PadDiameter"/> with the drill removed)
/// on EVERY copper layer it touches, tagged with its <see cref="Net"/> — the copper the DRC reads
/// and the connectivity engine walks. The drill diameter must be positive and strictly smaller than
/// the pad (a via needs a positive annular ring).</para>
/// </summary>
/// <param name="Net">The net this via belongs to. A via ties copper of one net across layers, so an
/// empty net is refused by name.</param>
/// <param name="X">The via centre X (mm, board coordinates).</param>
/// <param name="Y">The via centre Y (mm, board coordinates).</param>
/// <param name="FromLayer">One end of the span — a copper layer name of the board's stackup.</param>
/// <param name="ToLayer">The other end — a copper layer name of the board's stackup. Order does not
/// matter; the span is the contiguous copper range between the two.</param>
/// <param name="DrillDiameter">The finished drilled hole diameter (mm), positive.</param>
/// <param name="PadDiameter">The annular pad diameter (mm), strictly greater than the drill.</param>
public readonly record struct Via(
    string Net, double X, double Y, string FromLayer, string ToLayer,
    double DrillDiameter, double PadDiameter)
{
    /// <summary>The via centre as a 2D point.</summary>
    public Vector2d Center => new(X, Y);
}

/// <summary>
/// The resolved copper of one placed via — its centre, net, the ordered copper layers it touches,
/// the derived <see cref="ViaType"/>, and the annular pad region it places on each touched layer.
/// Derived from a <see cref="Via"/> against the board's stackup (the one-declaration rule), so
/// nothing is stated twice. It is what <see cref="PcbCopperModel"/> carries for the connectivity
/// engine and the via-to-via DRC, and the source it tags its copper features with (<see cref="Source"/>).
/// </summary>
/// <param name="Via">The declaring via.</param>
/// <param name="Source">A unique feature id for reports and connectivity (<c>"via1"</c>).</param>
/// <param name="Type">The derived kind (through/blind/buried/microvia).</param>
/// <param name="Layers">The copper layer names the via touches, top-most first.</param>
public readonly record struct PlacedVia(
    Via Via, string Source, ViaType Type, IReadOnlyList<string> Layers)
{
    /// <summary>The via centre (mm, board coordinates).</summary>
    public Vector2d Center => Via.Center;

    /// <summary>The via's net.</summary>
    public string Net => Via.Net;

    /// <summary>The finished drill diameter (mm).</summary>
    public double DrillDiameter => Via.DrillDiameter;

    /// <summary>The annular pad diameter (mm).</summary>
    public double PadDiameter => Via.PadDiameter;

    /// <summary>The exact annular ring left around the drill — <c>(pad − drill) / 2</c>.</summary>
    public double AnnularRing => (Via.PadDiameter - Via.DrillDiameter) / 2;

    /// <summary>The exact annular pad area on ONE touched layer — <c>π(pad² − drill²) / 4</c>, the
    /// area of a disc of the pad diameter with the drill removed. This is what a touched layer's pad
    /// region measures (its <see cref="CurvedRegion2d.Area"/>), so the two cannot disagree.</summary>
    public double AnnularPadArea =>
        Math.PI * (Via.PadDiameter * Via.PadDiameter - Via.DrillDiameter * Via.DrillDiameter) / 4;

    /// <summary>The annular pad copper region (a disc of the pad diameter with the drill removed).
    /// The SAME region every touched layer places, tagged with the via's net.</summary>
    public CurvedRegion2d PadRegion => ViaGeometry.AnnularPad(Center, PadDiameter, DrillDiameter);
}

/// <summary>Small helpers turning a <see cref="Via"/> into its resolved geometry — the span
/// classification (one source of truth for the derived <see cref="ViaType"/>) and the annular pad
/// region. Kept in one place so the layout, the copper model and the DRC cannot classify a via two
/// ways.</summary>
internal static class ViaGeometry
{
    /// <summary>
    /// Resolves a via against a copper stackup: which copper layers it touches (top-most first) and
    /// its derived <see cref="ViaType"/>. THROUGH (both outer faces) is decided first, then MICROVIA
    /// (a single dielectric hop — adjacent copper layers) takes precedence over BLIND/BURIED, since a
    /// microvia is a physically distinct single-layer via however its ends fall.
    /// </summary>
    /// <exception cref="ArgumentException">A named layer is not in the stackup, or the two ends are
    /// the same layer (a via must span at least two copper layers) — both refused by name.</exception>
    internal static (ViaType Type, IReadOnlyList<string> Layers) Resolve(PcbStackup stackup, in Via via)
    {
        var coppers = stackup.Coppers;                         // top-most first
        int from = IndexOf(coppers, via.FromLayer);
        int to = IndexOf(coppers, via.ToLayer);
        if (from < 0)
            throw new ArgumentException(
                $"Via layer '{via.FromLayer}' is not a copper layer of the stackup "
                + $"(layers: {string.Join(", ", coppers.Select(c => c.Name))}).", nameof(via));
        if (to < 0)
            throw new ArgumentException(
                $"Via layer '{via.ToLayer}' is not a copper layer of the stackup "
                + $"(layers: {string.Join(", ", coppers.Select(c => c.Name))}).", nameof(via));
        if (from == to)
            throw new ArgumentException(
                $"A via must span at least two copper layers, but both ends name '{via.FromLayer}'.",
                nameof(via));

        int lo = Math.Min(from, to), hi = Math.Max(from, to);
        var layers = new List<string>(hi - lo + 1);
        for (int i = lo; i <= hi; i++)
            layers.Add(coppers[i].Name);

        int n = coppers.Count;
        bool topOuter = lo == 0, bottomOuter = hi == n - 1;
        ViaType type =
            topOuter && bottomOuter ? ViaType.Through :        // outer to outer — the whole stack
            hi - lo == 1 ? ViaType.Microvia :                  // a single dielectric hop
            topOuter || bottomOuter ? ViaType.Blind :          // one outer face
            ViaType.Buried;                                    // neither face
        return (type, layers);
    }

    private static int IndexOf(IReadOnlyList<CopperLayerSpec> coppers, string name)
    {
        for (int i = 0; i < coppers.Count; i++)
            if (coppers[i].Name == name)
                return i;
        return -1;
    }

    /// <summary>The annular pad region: a disc of <paramref name="padDiameter"/> with the drill
    /// removed — a curved region whose outer loop is the pad circle and whose one hole is the drill
    /// circle. Its <see cref="CurvedRegion2d.Area"/> is exactly <c>π(pad² − drill²) / 4</c>.</summary>
    internal static CurvedRegion2d AnnularPad(in Vector2d center, double padDiameter, double drillDiameter) =>
        new(
            [CurvedEdge2d.Circle(center, padDiameter / 2)],
            [[CurvedEdge2d.Circle(center, drillDiameter / 2)]]);
}
