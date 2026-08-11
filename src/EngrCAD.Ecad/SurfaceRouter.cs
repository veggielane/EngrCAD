using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Ecad;

/// <summary>
/// The surface auto-routing engine — the geodesic analogue of the flat <c>RouterEngine</c>. It searches
/// the mesh VERTEX graph (edge weight = geodesic edge length) with A*, decomposes each net into 2-pin
/// connections over an MST, straightens the geodesic, and rips up and re-routes when a net is boxed in by
/// congestion.
///
/// <para><b>The vertex graph is an accelerator; the exact 3D DRC is the source of truth.</b> A candidate
/// geodesic is committed only after <see cref="Mid3dDrc.RouteCandidateClears"/> certifies it adds NO
/// violation, so a graph-resolution error can never ship a clearance-violating trace. A net that cannot
/// be routed cleanly is reported UNROUTABLE by name, and the partial board is still clean.</para>
///
/// <para><b>The obstacle model.</b> A vertex is HARD-blocked for a net when laying that net's copper
/// there would come within <c>clearance + width/2 + otherHalf + margin</c> of an other-net PAD (measured
/// as the 3D chord, a LOWER bound on the geodesic, so the search over-blocks — the safe direction for an
/// accelerator, since the exact DRC certifies every commit) or when it is on the mesh BOUNDARY; a
/// committed other-net TRACE is SOFT (a per-net bitmask), crossable at a high cost in a rip-up route and
/// then ripped up. The margin is one longest edge, which is exactly what makes the raw edge-graph path
/// DRC-clean by construction (a mid-edge point is within half an edge of a free vertex).</para>
/// </summary>
internal sealed class SurfaceRouter
{
    private readonly MidBoard _board;
    private readonly MidSurface _surface;
    private readonly HalfEdgeMesh _mesh;
    private readonly DrcRuleSet _rules;
    private readonly SurfaceRouteOptions _opt;

    private readonly double _traceWidth;
    private readonly double _clearance;
    private readonly double _margin;
    private readonly double _diameter;
    private readonly double _softPenalty;

    private readonly Vector3d[] _pos;
    private readonly bool[] _boundary;
    private readonly IReadOnlyList<MidSurfaceFeature> _baseFeatures;
    private readonly Dictionary<string, List<Terminal>> _terminals;

    // A* scratch, generation-stamped so it need not be re-zeroed per search.
    private readonly double[] _g;
    private readonly int[] _from;
    private readonly int[] _seen;
    private int _generation;

    internal SurfaceRouter(MidBoard board, DrcRuleSet rules, SurfaceRouteOptions opt)
    {
        _board = board;
        _surface = board.Surface;
        _mesh = board.Mesh;
        _rules = rules;
        _opt = opt;

        _traceWidth = opt.TraceWidth > 0 ? opt.TraceWidth : rules.MinTraceWidth;
        _clearance = opt.Clearance > 0 ? opt.Clearance : rules.MinCopperClearance;
        // Half a longest edge covers a mid-edge point of a routed segment (which is within half an edge of
        // the nearer free vertex), so the raw edge-graph path is DRC-clean by construction.
        _margin = board.LongestEdge() * 0.5;

        int v = _mesh.VertexCount;
        _pos = new Vector3d[v];
        _boundary = new bool[v];
        var box = Aabb.Empty;
        for (int i = 0; i < v; i++)
        {
            _pos[i] = _mesh.GetPosition(i);
            _boundary[i] = _mesh.GetVertex(i).IsBoundary;
            box = box.Union(_pos[i]);
        }
        // A soft crossing must cost more than any clean detour (rip-up is only reached when no clean route
        // exists, so this just steers a rip-up route toward the fewest victims): the mesh diameter × 100
        // dominates any edge-length path and stays scale-invariant.
        _diameter = Math.Max(box.Size.Length, 1e-30);
        _softPenalty = 100 * _diameter;
        _g = new double[v];
        _from = new int[v];
        _seen = new int[v];

        _baseFeatures = board.SurfaceFeatures();
        _terminals = BuildTerminals();
    }

    // ---- the top-level rip-up-and-reroute loop -------------------------------

    internal SurfaceRouteResult Run()
    {
        var unrouted = new HashSet<string>(Mid3dDrc.Check(_board, _rules).Ratsnest, StringComparer.Ordinal);
        var routable = _terminals.Keys
            .Where(net => unrouted.Contains(net) && _terminals[net].Count >= 2)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var order = InitialOrder(routable);

        var committed = new Dictionary<string, List<RoutedSegment>>(StringComparer.Ordinal);
        if (order.Count == 0)
            return Finish(order, committed, ripUps: 0);

        var queue = new Queue<string>(order);
        var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        int ripUps = 0;
        int budget = (order.Count + 1) * (_opt.MaxRipUpIterations + 1);

        for (int step = 0; step < budget && queue.Count > 0; step++)
        {
            string net = queue.Dequeue();
            if (committed.ContainsKey(net))
                continue;

            // 1) A clean route (committed other-net copper is impassable).
            var seg = RouteNet(net, committed, allowRipUp: false, out _);
            if (seg is not null)
            {
                committed[net] = seg;
                continue;
            }

            // 2) A rip-up route: cross the committed traces that block it, rip THOSE up, re-route the net
            //    cleanly without them, and re-queue the ripped nets.
            if (_opt.MaxRipUpIterations > 0 && attempts.GetValueOrDefault(net) < _opt.MaxRipUpIterations)
            {
                _ = RouteNet(net, committed, allowRipUp: true, out var victims);
                if (victims.Count > 0)
                {
                    var saved = victims.ToDictionary(x => x, x => committed[x], StringComparer.Ordinal);
                    foreach (var x in victims)
                        committed.Remove(x);

                    var rerouted = RouteNet(net, committed, allowRipUp: false, out _);
                    if (rerouted is not null)
                    {
                        committed[net] = rerouted;
                        attempts[net] = attempts.GetValueOrDefault(net) + 1;
                        ripUps++;
                        foreach (var x in victims)
                            queue.Enqueue(x);
                        continue;
                    }
                    foreach (var x in victims)   // re-route failed — restore the ripped nets
                        committed[x] = saved[x];
                }
            }
            // 3) Could not route even with rip-up — leave it unrouted (reported by name).
        }

        return Finish(order, committed, ripUps);
    }

    private List<string> InitialOrder(List<string> routable)
    {
        if (_opt.NetOrder is null)
            return routable;
        var set = new HashSet<string>(routable, StringComparer.Ordinal);
        var ordered = _opt.NetOrder.Where(set.Contains).ToList();
        ordered.AddRange(routable.Where(n => !_opt.NetOrder.Contains(n)));
        return ordered;
    }

    // ---- routing one net (MST over its terminals) ----------------------------

    private List<RoutedSegment>? RouteNet(
        string net, Dictionary<string, List<RoutedSegment>> committed, bool allowRipUp, out HashSet<string> victims)
    {
        victims = new HashSet<string>(StringComparer.Ordinal);
        var terms = _terminals[net];
        if (terms.Count < 2)
            return [];   // a single pad — nothing to route

        var (hard, soft, committedNets) = BuildBlockage(net, committed);
        var existing = BuildExisting(net, committed);
        var segments = new List<RoutedSegment>();
        var inTree = new bool[terms.Count];
        inTree[0] = true;

        for (int added = 1; added < terms.Count; added++)
        {
            var (srcIdx, nextIdx) = NearestPair(terms, inTree);
            var path = Search(terms[srcIdx].Seed, terms[nextIdx].Seed, hard, soft, allowRipUp);
            if (path is null)
                return null;   // this connection has no route (boxed in) — the whole net fails

            if (allowRipUp)
            {
                CollectVictims(path, soft, committedNets, victims);
                inTree[nextIdx] = true;
                continue;   // rip-up mode only harvests victims; the reroute commits the geometry
            }

            var seg = BuildSegment(net, terms[srcIdx], terms[nextIdx], path, existing);
            if (seg is null)
                return null;   // the exact DRC rejected the clean route — never ship a violating trace
            segments.Add(seg);
            existing.Add(seg.Feature());   // subsequent connections of this net see it
            inTree[nextIdx] = true;
        }
        return segments;
    }

    private static (int Src, int Next) NearestPair(List<Terminal> terms, bool[] inTree)
    {
        int bestSrc = 0, bestNext = -1;
        double best = double.PositiveInfinity;
        for (int j = 0; j < terms.Count; j++)
        {
            if (inTree[j])
                continue;
            for (int i = 0; i < terms.Count; i++)
            {
                if (!inTree[i])
                    continue;
                double d = terms[i].Point.Position.DistanceTo(terms[j].Point.Position);
                if (d < best)
                {
                    best = d;
                    bestSrc = i;
                    bestNext = j;
                }
            }
        }
        return (bestSrc, bestNext);
    }

    private static void CollectVictims(List<int> path, int[] soft, List<string> committedNets, HashSet<string> victims)
    {
        foreach (int v in path)
        {
            int mask = soft[v];
            for (int b = 0; b < committedNets.Count; b++)
                if ((mask & (1 << b)) != 0)
                    victims.Add(committedNets[b]);
        }
    }

    // ---- A* over mesh vertices ------------------------------------------------

    /// <summary>The obstacle-aware geodesic search from <paramref name="src"/> to <paramref name="target"/>
    /// over the mesh vertex graph. Skips hard-blocked vertices; in a clean route soft vertices are
    /// impassable, in a rip-up route they are passable at a penalty. The heuristic is the 3D straight-line
    /// distance to the target, admissible because a chord never exceeds a geodesic.</summary>
    private List<int>? Search(int src, int target, bool[] hard, int[] soft, bool allowRipUp)
    {
        if (hard[src] || hard[target])
            return null;   // a pad boxed in by other-net copper (or on the mesh boundary)
        if (src == target)
            return [src];

        _generation++;
        int gen = _generation;
        var open = new PriorityQueue<int, (double F, long Seq)>();
        long seq = 0;
        _g[src] = 0;
        _from[src] = -1;
        _seen[src] = gen;
        open.Enqueue(src, (Heuristic(src, target), seq++));

        while (open.TryDequeue(out int cur, out _))
        {
            if (cur == target)
                return Reconstruct(cur, gen, src);
            double gc = _g[cur];
            foreach (var h in _mesh.GetVertex(cur).OutgoingHalfEdges())
            {
                int nb = h.Destination.Index;
                if (hard[nb])
                    continue;
                int sm = soft[nb];
                if (!allowRipUp && sm != 0)
                    continue;
                double step = h.Length;
                if (sm != 0)
                    step += _softPenalty;   // crossing a committed trace — allowed but costly
                double newG = gc + step;
                if (_seen[nb] == gen && _g[nb] <= newG)
                    continue;
                _g[nb] = newG;
                _from[nb] = cur;
                _seen[nb] = gen;
                open.Enqueue(nb, (newG + Heuristic(nb, target), seq++));
            }
        }
        return null;
    }

    private double Heuristic(int v, int target) => _pos[v].DistanceTo(_pos[target]);

    private List<int> Reconstruct(int target, int gen, int src)
    {
        var path = new List<int>();
        int c = target;
        while (c != -1 && _seen[c] == gen)
        {
            path.Add(c);
            if (c == src)
                break;
            c = _from[c];
        }
        path.Reverse();
        return path;   // src .. target
    }

    // ---- turning a vertex path into a validated segment ----------------------

    /// <summary>Builds the centre-line for a connection (exact pad endpoints, the interior vertex surface
    /// points between), straightens it toward the geodesic, and CERTIFIES it with the exact 3D DRC. Falls
    /// back to the raw obstacle-avoiding edge path when straightening drifts across an obstacle; returns
    /// null when even the raw path cannot be certified (which the over-blocking makes essentially
    /// impossible, so it is a defensive gate rather than a common outcome).</summary>
    private RoutedSegment? BuildSegment(
        string net, Terminal src, Terminal target, List<int> path, List<MidSurfaceFeature> existing)
    {
        var raw = new List<SurfacePoint>(path.Count) { src.Point };
        for (int i = 1; i < path.Count - 1; i++)
            raw.Add(_surface.Locate(_pos[path[i]]));
        raw.Add(target.Point);

        var deduped = Dedup(raw);
        if (deduped.Count < 2)
            return null;
        string source = $"{src.Source}->{target.Source}";

        if (_opt.Straighten)
        {
            var straight = MidRouting.Straighten(_surface, deduped, target.Point);
            if (straight.Count >= 2)
            {
                var f = new MidSurfaceFeature(net, source, _traceWidth, straight);
                if (Mid3dDrc.RouteCandidateClears(_board, f, existing, _rules))
                    return new RoutedSegment(net, source, _traceWidth, straight);
            }
        }

        var rawFeature = new MidSurfaceFeature(net, source, _traceWidth, deduped);
        return Mid3dDrc.RouteCandidateClears(_board, rawFeature, existing, _rules)
            ? new RoutedSegment(net, source, _traceWidth, deduped)
            : null;
    }

    private List<SurfacePoint> Dedup(List<SurfacePoint> path)
    {
        double weld = 1e-9 * _diameter;
        var outp = new List<SurfacePoint>(path.Count) { path[0] };
        for (int i = 1; i < path.Count; i++)
            if (path[i].Position.DistanceTo(outp[^1].Position) > weld)
                outp.Add(path[i]);
        return outp;
    }

    // ---- terminals -----------------------------------------------------------

    private Dictionary<string, List<Terminal>> BuildTerminals()
    {
        var byNet = new Dictionary<string, List<Terminal>>(StringComparer.Ordinal);
        foreach (var pad in _board.Pads)
        {
            if (pad.Net is null)
                continue;   // an unconnected pad is an obstacle, not a routed terminal
            if (!byNet.TryGetValue(pad.Net, out var list))
                byNet[pad.Net] = list = [];
            list.Add(new Terminal(_surface.SeedVertex(pad.Located), pad.Located, pad.Source));
        }
        return byNet;
    }

    // ---- blockage ------------------------------------------------------------

    private (bool[] Hard, int[] Soft, List<string> CommittedNets) BuildBlockage(
        string net, Dictionary<string, List<RoutedSegment>> committed)
    {
        var committedNets = committed.Keys.Where(n => n != net)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        var netBit = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int b = 0; b < committedNets.Count; b++)
            netBit[committedNets[b]] = b;

        int v = _mesh.VertexCount;
        var hard = new bool[v];
        var soft = new int[v];

        // The mesh boundary is a hard obstacle (copper stays on the surface).
        for (int i = 0; i < v; i++)
            if (_boundary[i])
                hard[i] = true;

        // Base copper of OTHER nets (pads, and any caller-placed traces) — HARD, never rippable.
        foreach (var f in _baseFeatures)
            if (!SameNet(net, f.Net))
                BlockFeature(f.Polyline, Keepout(f.Width), i => hard[i] = true);

        // Committed traces of OTHER nets — SOFT (rippable), one mask bit each. A committed net past the
        // 31-bit mask capacity is treated as HARD (non-rippable): the router cannot pick it as a rip-up
        // victim, which is safe (it stays an obstacle) rather than misattributing a shifted bit.
        foreach (var m in committedNets)
        {
            bool rippable = netBit[m] < 31;
            int bit = rippable ? 1 << netBit[m] : 0;
            foreach (var seg in committed[m])
            {
                var poly = seg.Positions;
                if (rippable)
                    BlockFeature(poly, Keepout(seg.Width), i => soft[i] |= bit);
                else
                    BlockFeature(poly, Keepout(seg.Width), i => hard[i] = true);
            }
        }
        return (hard, soft, committedNets);
    }

    /// <summary>The features the exact-DRC gate certifies a candidate against — the base copper (pads and
    /// any caller-placed traces) plus every committed segment. The gate skips same-net pairs, so passing
    /// all of them (rather than only other-net copper) is correct and lets a later commit certify a pair
    /// its earlier partner did not yet see.</summary>
    private List<MidSurfaceFeature> BuildExisting(string net, Dictionary<string, List<RoutedSegment>> committed)
    {
        var existing = new List<MidSurfaceFeature>(_baseFeatures);
        foreach (var (m, segs) in committed)
        {
            if (m == net)
                continue;
            foreach (var seg in segs)
                existing.Add(seg.Feature());
        }
        return existing;
    }

    /// <summary>The surface keep-out from a vertex to another-net centre-line of the given width — the
    /// clearance, both half-widths, plus one longest edge so a mid-edge point of the routed trace is
    /// covered by the block on a vertex.</summary>
    private double Keepout(double otherWidth) =>
        _clearance + _traceWidth / 2 + otherWidth / 2 + _margin;

    private void BlockFeature(IReadOnlyList<Vector3d> poly, double keepout, Action<int> mark)
    {
        var box = Bounds(poly).Expanded(keepout);
        double k2 = keepout * keepout;
        for (int i = 0; i < _pos.Length; i++)
        {
            if (!box.Contains(_pos[i]))
                continue;
            if (SquaredDistToPolyline(_pos[i], poly) < k2)
                mark(i);
        }
    }

    private static Aabb Bounds(IReadOnlyList<Vector3d> poly)
    {
        var box = Aabb.Empty;
        foreach (var p in poly)
            box = box.Union(p);
        return box;
    }

    /// <summary>The squared 3D distance from a point to a centre-line polyline (a lower bound on the
    /// geodesic distance to that copper), one point handled directly.</summary>
    private static double SquaredDistToPolyline(in Vector3d p, IReadOnlyList<Vector3d> poly)
    {
        if (poly.Count == 1)
            return (p - poly[0]).LengthSquared;
        double best = double.PositiveInfinity;
        for (int i = 0; i + 1 < poly.Count; i++)
        {
            var a = poly[i];
            var ab = poly[i + 1] - a;
            double len2 = ab.LengthSquared;
            double t = len2 > 0 ? Math.Clamp((p - a).Dot(ab) / len2, 0, 1) : 0;
            best = Math.Min(best, (p - (a + ab * t)).LengthSquared);
        }
        return best;
    }

    private static bool SameNet(string? a, string? b) => a is not null && b is not null && a == b;

    // ---- committing to the board ---------------------------------------------

    private SurfaceRouteResult Finish(List<string> order, Dictionary<string, List<RoutedSegment>> committed, int ripUps)
    {
        var routedNets = order.Where(committed.ContainsKey).ToList();
        var unroutedNets = order.Where(n => !committed.ContainsKey(n))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();

        var placed = new List<SurfaceTrace>();
        foreach (var net in order)
        {
            if (!committed.TryGetValue(net, out var segs))
                continue;
            foreach (var seg in segs)
                placed.Add(_board.PlaceSurfaceTrace(seg.Net, seg.CentreLine, seg.Width, seg.Source));
        }
        return new SurfaceRouteResult(_board, routedNets, unroutedNets, placed, placed.Count, ripUps);
    }

    /// <summary>One pad the router routes to/from: its nearest mesh vertex (the graph seed), its exact
    /// surface point (so a trace endpoint lands ON the pad), and its source name.</summary>
    private readonly record struct Terminal(int Seed, SurfacePoint Point, string Source);

    /// <summary>One committed 2-pin connection: a net, a source name, a width and a centre-line of surface
    /// points, with its cached 3D polyline and DRC feature.</summary>
    private sealed class RoutedSegment
    {
        private Vector3d[]? _positions;
        private MidSurfaceFeature? _feature;

        public RoutedSegment(string? net, string source, double width, IReadOnlyList<SurfacePoint> centreLine)
        {
            Net = net;
            Source = source;
            Width = width;
            CentreLine = centreLine;
        }

        public string? Net { get; }
        public string Source { get; }
        public double Width { get; }
        public IReadOnlyList<SurfacePoint> CentreLine { get; }

        public Vector3d[] Positions => _positions ??= [.. CentreLine.Select(p => p.Position)];
        public MidSurfaceFeature Feature() => _feature ??= new MidSurfaceFeature(Net, Source, Width, CentreLine);
    }
}
