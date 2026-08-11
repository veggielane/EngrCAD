using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>
/// Routing and verification of nets on a <see cref="MidBoard"/>'s moulded surface — the exp-map
/// parameter-space consumer of the connectivity model.
///
/// <para><b>v1 PLACES traces and VERIFIES them; it does NOT auto-route.</b> Auto-routing on a surface
/// is a research problem: the flat autorouter (<c>PcbRouter</c>) is a grid/maze A* over a plane, and
/// the surface analogue is a GEODESIC maze search whose metric is the exp map's distorted (u, v) space
/// — a genuinely harder problem, and one whose result would still have to be certified by the same 3D
/// DRC. So <see cref="Route"/> refuses by name and points at the manual place-and-verify API; a caller
/// draws the centre-lines in (u, v) (<see cref="MidBoard.PlaceTrace"/> / <see cref="Connect"/>) and
/// <see cref="Verify"/> runs the DRC, which is where the surface's honesty lives.</para>
/// </summary>
public static class MidRouting
{
    /// <summary>
    /// Routes a straight surface trace between two parameter points — the convenience the manual
    /// place-and-verify workflow leans on, connecting two pads of one net with a two-point centre-line.
    /// A geodesic (shortest-on-surface) route is the auto-router's job, filed; this lays the straight
    /// line in (u, v), which on a developable surface IS the geodesic and elsewhere is a stated
    /// approximation the DRC's distortion fold covers.
    /// </summary>
    public static SurfaceTrace Connect(
        MidBoard board, string? net, in Vector2d from, in Vector2d to, double width, string source)
    {
        ArgumentNullException.ThrowIfNull(board);
        return board.PlaceTrace(net, [from, to], width, source);
    }

    /// <summary>
    /// Routes a surface trace between two PADS of the same net — the endpoints land exactly on the pad
    /// centres, so the lifted trace joins them on the surface with no gap.
    /// </summary>
    public static SurfaceTrace Connect(MidBoard board, MidPad from, MidPad to, double width)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        string? net = from.Net ?? to.Net;
        return board.PlaceTrace(net, [from.Parameter, to.Parameter], width, $"{from.Source}->{to.Source}");
    }

    /// <summary>Verifies the board's routing — runs the 3D DRC over the placed pads and traces. The
    /// verify IS the DRC (<see cref="Mid3dDrc.Check"/>); a board that verifies clean has been certified
    /// on the surface, distortion folded in, or its un-certifiable pairs are named.</summary>
    public static Mid3dDrcReport Verify(MidBoard board, DrcRuleSet? rules = null) =>
        Mid3dDrc.Check(board, rules);

    /// <summary>
    /// Auto-routing on the surface — refused by name in v1.
    /// </summary>
    /// <exception cref="NotSupportedException">Always. See the class remarks: a geodesic maze search is
    /// a later stage; place traces manually and <see cref="Verify"/> them.</exception>
    public static Mid3dDrcReport Route(MidBoard board, DrcRuleSet? rules = null) =>
        throw new NotSupportedException(
            "Auto-routing on a moulded surface is not offered in v1. Routing on a doubly-curved surface "
            + "is a GEODESIC maze search — the flat grid autorouter (PcbRouter) does not lift, since the "
            + "surface metric is the exp map's distorted (u, v) space — and it is filed as a later stage. "
            + "Place surface traces manually (MidBoard.PlaceTrace / MidRouting.Connect) and verify them "
            + "with MidRouting.Verify, which folds the distortion into the DRC.");
}
