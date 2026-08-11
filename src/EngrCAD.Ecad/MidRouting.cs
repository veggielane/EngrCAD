using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Ecad;

/// <summary>
/// Routing and verification of nets on a <see cref="MidBoard"/>'s moulded surface.
///
/// <para><b>v1 PLACES traces and VERIFIES them; it does NOT auto-route.</b> On an intrinsic board a trace
/// between two pads is laid as a GEODESIC path ON the mesh (<see cref="Connect(MidBoard, MidPad, MidPad,
/// double)"/> uses <see cref="DijkstraGraphDistance"/>'s edge-graph geodesic), so the lift and the length
/// are correct on any surface — a straight (u, v) line would cut through a curved shell. Auto-routing on
/// a surface is a research problem: the flat autorouter (<c>PcbRouter</c>) is a grid/maze A* over a
/// plane, and the surface analogue is a GEODESIC maze search whose metric is the surface's distorted
/// geometry — a genuinely harder problem, and one whose result would still have to be certified by the
/// same 3D DRC. So <see cref="Route"/> refuses by name; a caller places traces and <see cref="Verify"/>
/// runs the DRC, which is where the surface's honesty lives.</para>
/// </summary>
public static class MidRouting
{
    /// <summary>
    /// Routes a surface trace between two PADS of the same net. On an INTRINSIC board it lays a GEODESIC
    /// path on the mesh between the pads (correct length on any surface); on a GLOBAL-chart board it lays
    /// the straight (u, v) line, which on a developable patch IS the geodesic. Either way the endpoints
    /// land exactly on the pad centres.
    /// </summary>
    public static SurfaceTrace Connect(MidBoard board, MidPad from, MidPad to, double width)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        string? net = from.Net ?? to.Net;
        string source = $"{from.Source}->{to.Source}";
        if (from.HasParameter && to.HasParameter)
            return board.PlaceTrace(net, [from.Parameter, to.Parameter], width, source);
        var centreLine = Geodesic(board, from.Located, to.Located);
        return board.PlaceSurfaceTrace(net, centreLine, width, source);
    }

    /// <summary>
    /// Routes a straight surface trace between two GLOBAL-chart parameter points.
    /// </summary>
    public static SurfaceTrace Connect(
        MidBoard board, string? net, in Vector2d from, in Vector2d to, double width, string source)
    {
        ArgumentNullException.ThrowIfNull(board);
        return board.PlaceTrace(net, [from, to], width, source);
    }

    /// <summary>
    /// Routes a GEODESIC surface trace between two WORLD points snapped to the surface — the intrinsic
    /// path, laying the shortest edge-graph path on the mesh so the trace follows the moulded shell.
    /// </summary>
    public static SurfaceTrace Connect(
        MidBoard board, string? net, in Vector3d from, in Vector3d to, double width, string source)
    {
        ArgumentNullException.ThrowIfNull(board);
        var centreLine = Geodesic(board, board.Locate(from), board.Locate(to));
        return board.PlaceSurfaceTrace(net, centreLine, width, source);
    }

    /// <summary>
    /// The GEODESIC surface path between two located points — the polyline of surface points a trace
    /// between them follows on the mesh. The endpoints are the exact input points (so a trace lands
    /// exactly on its pads); the interior starts as mesh vertices along
    /// <see cref="DijkstraGraphDistance"/>'s shortest EDGE path (which stays ON the surface), then a
    /// STRAIGHTEST-GEODESIC smoothing pulls the interior toward the true geodesic — a curve-shortening
    /// relaxation, each interior point drawn to the midpoint of its neighbours and snapped back onto the
    /// surface, endpoints pinned. This removes the edge path's staircase (up to ~8% long, restricted to
    /// mesh edges) and leaves the whole path on the surface. Coincident consecutive points are dropped.
    /// <para>An auto-router that CHOOSES the route is a later stage; this lays and straightens a route
    /// between two given pads.</para>
    /// </summary>
    public static IReadOnlyList<SurfacePoint> Geodesic(MidBoard board, in SurfacePoint from, in SurfacePoint to)
    {
        ArgumentNullException.ThrowIfNull(board);
        var surface = board.Surface;
        int seed = surface.SeedVertex(from);
        int target = surface.SeedVertex(to);

        var raw = new List<SurfacePoint> { from };
        if (seed != target)
        {
            var dijkstra = DijkstraGraphDistance.Compute(surface.Mesh, seed);
            if (dijkstra.IsReached(target))
            {
                var verts = dijkstra.PathToSeed(target);   // [target, ..., seed]
                for (int i = verts.Count - 1; i >= 0; i--)  // seed -> ... -> target
                    raw.Add(surface.Locate(surface.Mesh.GetPosition(verts[i])));
            }
        }
        raw.Add(to);

        var path = Dedup(raw, to);
        if (path.Count < 3)
            return path;

        Densify(surface, path);
        Smooth(surface, path, iterations: 16);
        return Dedup(path, to);
    }

    /// <summary>Drops coincident consecutive points (the pad sits inside a face touching its seed
    /// vertex), keeping the last point as the exact target.</summary>
    private static List<SurfacePoint> Dedup(IReadOnlyList<SurfacePoint> path, in SurfacePoint to)
    {
        var deduped = new List<SurfacePoint>(path.Count) { path[0] };
        for (int i = 1; i < path.Count; i++)
            if (path[i].Position.DistanceTo(deduped[^1].Position) > 1e-9 * Math.Max(1, path[i].Position.Length))
                deduped.Add(path[i]);
        if (deduped.Count < 2)
            deduped.Add(to);
        return deduped;
    }

    /// <summary>Splits any segment longer than the interior's mean, so the smoothing has interior points
    /// to straighten (a coarse edge path can otherwise be nearly straight already).</summary>
    private static void Densify(MidSurface surface, List<SurfacePoint> path)
    {
        double total = 0;
        for (int i = 1; i < path.Count; i++)
            total += path[i].Position.DistanceTo(path[i - 1].Position);
        double step = total / Math.Max(1, path.Count - 1) * 0.6;
        if (!(step > 0))
            return;
        for (int i = path.Count - 1; i >= 1; i--)
        {
            double len = path[i].Position.DistanceTo(path[i - 1].Position);
            int splits = (int)(len / step);
            for (int s = splits - 1; s >= 1; s--)
            {
                double t = (double)s / splits;
                var mid = path[i - 1].Position * (1 - t) + path[i].Position * t;
                path.Insert(i, surface.Locate(mid));
            }
        }
    }

    /// <summary>Curve-shortening relaxation toward the geodesic: each interior point is drawn halfway to
    /// the midpoint of its neighbours and snapped back onto the surface; the two endpoints (the pads)
    /// stay pinned.</summary>
    private static void Smooth(MidSurface surface, List<SurfacePoint> path, int iterations)
    {
        for (int it = 0; it < iterations; it++)
            for (int i = 1; i < path.Count - 1; i++)
            {
                var mid = (path[i - 1].Position + path[i + 1].Position) * 0.5;
                var blended = path[i].Position + (mid - path[i].Position) * 0.5;
                path[i] = surface.Locate(blended);
            }
    }

    /// <summary>Verifies the board's routing — runs the 3D DRC over the placed pads and traces.</summary>
    public static Mid3dDrcReport Verify(MidBoard board, DrcRuleSet? rules = null) =>
        Mid3dDrc.Check(board, rules);

    /// <summary>
    /// Auto-routing on the surface — refused by name in v1.
    /// </summary>
    /// <exception cref="NotSupportedException">Always. See the class remarks: a geodesic maze search is a
    /// later stage; place traces (as geodesics) manually and <see cref="Verify"/> them.</exception>
    public static Mid3dDrcReport Route(MidBoard board, DrcRuleSet? rules = null) =>
        throw new NotSupportedException(
            "Auto-routing on a moulded surface is not offered in v1. Routing on a doubly-curved surface is "
            + "a GEODESIC maze search — the flat grid autorouter (PcbRouter) does not lift, since the "
            + "surface metric is the surface's own distorted geometry — and it is filed as a later stage. "
            + "Place surface traces (MidRouting.Connect lays them as geodesics on the mesh) and verify them "
            + "with MidRouting.Verify, which folds the distortion into the DRC.");
}
