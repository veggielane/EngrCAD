using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Ecad;

/// <summary>
/// Routing and verification of nets on a <see cref="MidBoard"/>'s moulded surface.
///
/// <para>Two ways to lay copper. <see cref="Connect(MidBoard, MidPad, MidPad, double)"/> places ONE
/// trace between two given pads as a GEODESIC on the mesh (the place-and-verify path), and
/// <see cref="Route"/> AUTO-ROUTES a whole intrinsic board — the geodesic analogue of the flat
/// autorouter (<c>PcbRouter</c>): a DRC-aware maze search over the mesh vertex graph, MST decomposition
/// of each net from the ratsnest, and rip-up-and-reroute for congestion. Either way a trace between two
/// pads is a geodesic path ON the mesh (correct length and lift on any surface — a straight (u, v) line
/// would cut through a curved shell), and either way <see cref="Verify"/> runs the same 3D DRC.</para>
///
/// <para><b>The exact 3D DRC (<see cref="Mid3dDrc"/>) is the source of truth; the vertex graph only
/// accelerates.</b> The auto-router commits a candidate geodesic only after the exact DRC certifies it
/// adds NO violation (<see cref="Mid3dDrc.RouteCandidateClears"/>), so a graph-resolution error can
/// never produce a violating route — a net that cannot be routed cleanly is reported UNROUTABLE by name
/// (never a silent clearance violation), and a partial result is still DRC-clean.</para>
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
    /// AUTO-ROUTES an INTRINSIC (<see cref="MidBoard.OnMesh"/>) moulded board — the geodesic analogue of
    /// the flat autorouter. Each unrouted net (its pads not yet all joined) is decomposed into 2-pin
    /// connections over an MST and routed as a DRC-aware geodesic on the mesh vertex graph, straightened,
    /// and committed only if the exact 3D DRC certifies it clean; a net boxed in by other-net copper is
    /// reported UNROUTABLE by name. The board is MUTATED in place with the routed traces (so
    /// <see cref="Verify"/> then reports the routed nets connected and clean); a net already connected has
    /// nothing to route and the board is left unchanged.
    /// </summary>
    /// <param name="board">The intrinsic board to route (its pads carry the nets). Mutated with the
    /// routed traces.</param>
    /// <param name="rules">The design rules to route to and certify against (null = the defaults). The
    /// "nets" to route are the board's own unrouted nets (its ratsnest), exactly the flat router.</param>
    /// <param name="options">The router options (null = the defaults).</param>
    /// <exception cref="ArgumentException">The board is a GLOBAL-chart board
    /// (<see cref="MidBoard.OnSurface"/>): auto-routing runs on an intrinsic board, so build it with
    /// <see cref="MidBoard.OnMesh"/>.</exception>
    public static SurfaceRouteResult Route(
        MidBoard board, DrcRuleSet? rules = null, SurfaceRouteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (!board.IsIntrinsic)
            throw new ArgumentException(
                "Auto-routing runs on an INTRINSIC MID board — build the routing surface with "
                + "MidBoard.OnMesh(mesh), where a geodesic maze search over the mesh vertex graph works on "
                + "any geometry. A global-chart board (MidBoard.OnSurface) parameterizes a developable "
                + "patch by one exp map, kept for the exact developable DRC oracle; route the same mesh "
                + "with OnMesh instead.", nameof(board));
        return new SurfaceRouter(board, rules ?? DrcRuleSet.Default, options ?? new SurfaceRouteOptions()).Run();
    }

    /// <summary>
    /// AUTO-ROUTES a MULTI-SHELL MID board — the surface analogue of the flat PCB router's LAYER-CHANGING
    /// via. A net whose pads are on DIFFERENT shells, or that must hop to the other shell to pass an
    /// obstacle, is routed over the UNION of both shells' vertex graphs plus "via edges" tying corresponding
    /// vertices across the dielectric; where the chosen path uses a via edge, a through-shell
    /// <see cref="SurfaceVia"/> is PLACED at that vertex and the route splits into per-shell surface traces.
    ///
    /// <para><b>The exact multi-shell DRC is the source of truth.</b> A candidate route + via is committed
    /// only after each per-shell trace and each new via pad CLEARS the existing copper on its shell
    /// (<see cref="Mid3dDrc"/>) AND the via-to-via web is met — so a graph-resolution error can never ship
    /// a clearance-violating trace or via. A net that cannot be routed even by hopping is reported
    /// UNROUTABLE by name, and a partial result is still clean. Rip-up-and-reroute handles congestion
    /// exactly as the single-shell router does.</para>
    /// </summary>
    /// <param name="stack">The two-shell stack to route (mutated with the committed traces AND vias). The
    /// nets to route are its cross-shell ratsnest (<see cref="MidStack.Connectivity"/>).</param>
    /// <param name="rules">The design rules to route to and certify against (null = the defaults).</param>
    /// <param name="options">The router options (null = the defaults); the via knobs
    /// (<see cref="SurfaceRouteOptions.ViaPadDiameter"/>, <see cref="SurfaceRouteOptions.ViaPenalty"/>)
    /// apply here.</param>
    /// <exception cref="ArgumentException">The stack has ONE shell (there is nothing to hop to — use the
    /// single-shell <see cref="Route(MidBoard, DrcRuleSet?, SurfaceRouteOptions?)"/> on
    /// <see cref="MidStack.Outer"/>), or MORE THAN TWO shells (v1 routes a two-shell stack; a &gt; 2 shell
    /// stack needs partial-span vias, filed).</exception>
    public static StackRouteResult Route(
        MidStack stack, DrcRuleSet? rules = null, SurfaceRouteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stack);
        if (stack.ShellCount < 2)
            throw new ArgumentException(
                "Cross-shell auto-routing needs at least two shells — a one-shell stack has nothing to hop "
                + "to. Route its single surface with MidRouting.Route(stack.Outer, rules, options), the "
                + "single-shell path.", nameof(stack));
        if (stack.ShellCount > 2)
            throw new ArgumentException(
                $"Cross-shell auto-routing v1 routes a TWO-shell stack; this stack has {stack.ShellCount} "
                + "shells. A > 2 shell stack needs PARTIAL-SPAN vias (a via between two adjacent shells "
                + "rather than the full-stack barrel AddVia places), which is filed as a follow-on. Route "
                + "each shell with MidRouting.Route(stack.Shell(k)) and place the vias explicitly.",
                nameof(stack));
        return new CrossShellRouter(stack, rules ?? DrcRuleSet.Default, options ?? new SurfaceRouteOptions()).Run();
    }

    /// <summary>
    /// Straightens a raw surface polyline toward the geodesic, endpoints PINNED — the auto-router's
    /// trace-quality pass, the same curve-shortening <see cref="Geodesic"/> uses (densify then relax to
    /// the midpoint of neighbours, snapped back onto the surface). Unconstrained, so a straightened path
    /// may drift across an obstacle; the router validates it with the exact DRC and falls back to the raw
    /// (edge-graph, obstacle-avoiding) path when it does.
    /// </summary>
    internal static List<SurfacePoint> Straighten(MidSurface surface, IReadOnlyList<SurfacePoint> raw, in SurfacePoint end)
    {
        var path = Dedup(raw, end);
        if (path.Count < 3)
            return path;
        Densify(surface, path);
        Smooth(surface, path, iterations: 16);
        return Dedup(path, end);
    }
}

/// <summary>
/// Options for <see cref="MidRouting.Route"/>. Every field has a rule-derived default (a zero means
/// "derive it"), so <c>new SurfaceRouteOptions()</c> is a sensible auto-route. The vertex graph is an
/// ACCELERATOR — the exact 3D DRC decides every commit — so these only steer the search, never
/// correctness.
/// </summary>
public sealed record SurfaceRouteOptions
{
    /// <summary>The trace width laid (mm); 0 = <see cref="DrcRuleSet.MinTraceWidth"/>.</summary>
    public double TraceWidth { get; init; }

    /// <summary>The copper-to-copper surface clearance the router keeps (mm); 0 =
    /// <see cref="DrcRuleSet.MinCopperClearance"/>. The exact DRC is certified against the SAME rule set,
    /// so this only steers the search.</summary>
    public double Clearance { get; init; }

    /// <summary>The maximum number of times any one net may trigger a rip-up (default 8). When a net has
    /// no clean geodesic, the router routes it ACROSS the committed traces that block it, rips those up,
    /// re-routes it cleanly without them, and re-queues them — negotiated congestion. This bounds the
    /// rip-ups so a truly boxed-in net terminates and is reported UNROUTABLE by name. 0 disables rip-up
    /// (a pure greedy pass).</summary>
    public int MaxRipUpIterations { get; init; } = 8;

    /// <summary>An explicit net routing order (net names); nets not listed follow in ordinal order.
    /// null = ordinal order. A fixed order + a fixed mesh gives deterministic routes.</summary>
    public IReadOnlyList<string>? NetOrder { get; init; }

    /// <summary>Whether to straighten each committed geodesic toward the true geodesic (default true).
    /// A straightened trace that fails the exact DRC falls back to the raw obstacle-avoiding edge
    /// path.</summary>
    public bool Straighten { get; init; } = true;

    /// <summary>The land diameter (mm) of a through-shell via the CROSS-SHELL router drops where a route
    /// hops between shells (default 0.5, matching <see cref="MidStack.AddVia(string?, in Vector3d,
    /// double)"/>). Ignored by the single-shell <see cref="MidRouting.Route(MidBoard, DrcRuleSet?,
    /// SurfaceRouteOptions?)"/>, which places no vias.</summary>
    public double ViaPadDiameter { get; init; } = 0.5;

    /// <summary>The cost the cross-shell search charges for a via edge — the reluctance that makes the
    /// router prefer staying on one shell unless a hop pays (0 = derive a modest default from the
    /// dielectric span and the mesh scale). It must be at least the barrel's 3D chord for the
    /// straight-line heuristic to stay admissible, which the derived default guarantees. The exact 3D DRC
    /// certifies every commit regardless, so this only steers the search. Ignored by the single-shell
    /// router.</summary>
    public double ViaPenalty { get; init; }
}

/// <summary>
/// The result of <see cref="MidRouting.Route"/>: which nets routed and which did NOT, and the counts.
/// The board is guaranteed 3D-DRC-clean against the rule set it was routed with (the router commits a
/// geodesic only after the exact DRC certifies it adds no violation), so a PARTIAL result — one that
/// could not route every net — is still clean, with the unroutable nets NAMED rather than faked.
/// </summary>
/// <param name="Board">The routed board (mutated in place with the committed traces).</param>
/// <param name="RoutedNets">The nets that were routed (had ≥ 2 pads to join and got connected), in
/// routing order.</param>
/// <param name="UnroutedNets">The nets that could NOT be routed, in ordinal order — reported, not
/// faked.</param>
/// <param name="Traces">The committed surface traces (one or more per routed net).</param>
/// <param name="TracesAdded">The number of trace segments added.</param>
/// <param name="RipUps">How many times a net was routed across another and the blocking net ripped up
/// and re-routed (0 = every net found a clean geodesic the first time).</param>
public sealed record SurfaceRouteResult(
    MidBoard Board,
    IReadOnlyList<string> RoutedNets,
    IReadOnlyList<string> UnroutedNets,
    IReadOnlyList<SurfaceTrace> Traces,
    int TracesAdded,
    int RipUps)
{
    /// <summary>True when every net that needed routing was routed.</summary>
    public bool FullyRouted => UnroutedNets.Count == 0;

    /// <summary>A human-readable summary.</summary>
    public override string ToString() =>
        FullyRouted
            ? $"routed {RoutedNets.Count} nets ({TracesAdded} traces)"
              + (RipUps > 0 ? $" after {RipUps} rip-up(s)" : "")
            : $"routed {RoutedNets.Count} nets ({TracesAdded} traces); "
              + $"UNROUTED: {string.Join(", ", UnroutedNets)}";
}

/// <summary>
/// The result of <see cref="MidRouting.Route(MidStack, DrcRuleSet?, SurfaceRouteOptions?)"/>: which nets
/// routed and which did NOT, the committed per-shell traces and the through-shell vias the router dropped
/// to hop between shells, and the counts. The stack is guaranteed 3D-DRC-clean against the rule set it
/// was routed with (every commit is certified by the exact multi-shell DRC), so a PARTIAL result — one
/// that could not route every net — is still clean, with the unroutable nets NAMED rather than faked.
/// </summary>
/// <param name="Stack">The routed stack (mutated in place with the committed traces and vias).</param>
/// <param name="RoutedNets">The nets that were routed (had ≥ 2 terminal pads to join and got connected),
/// in routing order.</param>
/// <param name="UnroutedNets">The nets that could NOT be routed even by hopping shells, in ordinal order
/// — reported, not faked.</param>
/// <param name="Traces">The committed surface traces (one or more per routed net, across both
/// shells).</param>
/// <param name="Vias">The through-shell vias the router placed where a route changes shell.</param>
/// <param name="TracesAdded">The number of trace segments added.</param>
/// <param name="ViasAdded">The number of through-shell vias added.</param>
/// <param name="RipUps">How many times a net was routed across another and the blocking net ripped up and
/// re-routed (0 = every net found a clean route the first time).</param>
public sealed record StackRouteResult(
    MidStack Stack,
    IReadOnlyList<string> RoutedNets,
    IReadOnlyList<string> UnroutedNets,
    IReadOnlyList<SurfaceTrace> Traces,
    IReadOnlyList<SurfaceVia> Vias,
    int TracesAdded,
    int ViasAdded,
    int RipUps)
{
    /// <summary>True when every net that needed routing was routed.</summary>
    public bool FullyRouted => UnroutedNets.Count == 0;

    /// <summary>A human-readable summary.</summary>
    public override string ToString() =>
        FullyRouted
            ? $"routed {RoutedNets.Count} nets ({TracesAdded} traces, {ViasAdded} vias)"
              + (RipUps > 0 ? $" after {RipUps} rip-up(s)" : "")
            : $"routed {RoutedNets.Count} nets ({TracesAdded} traces, {ViasAdded} vias); "
              + $"UNROUTED: {string.Join(", ", UnroutedNets)}";
}
