using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Ecad;

/// <summary>
/// The CROSS-SHELL surface auto-routing engine — the surface analogue of the flat PCB router's
/// LAYER-CHANGING via. It searches the UNION of both shells' vertex graphs plus VIA EDGES tying
/// corresponding vertices across the dielectric, so ONE A* both routes a net between shells and chooses
/// WHERE to change shells; where the chosen path uses a via edge, a through-shell <see cref="SurfaceVia"/>
/// is placed at that vertex and the route splits into per-shell surface traces.
///
/// <para><b>The combined graph.</b> A node is (shell, vertex), encoded <c>shell·V + vertex</c>. A mesh edge
/// on shell k connects two of that shell's vertices at that shell's geodesic edge length; a VIA EDGE
/// connects (k, v) to (k±1, v) — trivial to enumerate because a <see cref="MidStack"/>'s shells share mesh
/// topology, so vertex v corresponds across shells — at a fixed via PENALTY, which biases the router toward
/// staying on one shell unless a hop pays. The heuristic is the 3D straight-line distance to the target,
/// admissible because every edge costs at least the 3D chord it spans (a mesh geodesic edge ≥ its chord;
/// a via edge ≥ its barrel chord, which the derived penalty guarantees).</para>
///
/// <para><b>The exact multi-shell DRC is the source of truth</b> (the flat router's own rule, lifted onto
/// the surface): a candidate route + via is committed only after each per-shell trace CLEARS the existing
/// other-net copper on its shell (<see cref="Mid3dDrc.RouteCandidateClears"/>), each new via pad CLEARS
/// other-net copper on every shell it touches (<see cref="Mid3dDrc.RouteClearanceClears"/>), and the
/// via-to-via web is met — so a graph-resolution error can never ship a clearance-violating trace or via.
/// A net that cannot be routed even by hopping is reported UNROUTABLE by name, and a partial result is
/// still clean. Rip-up-and-reroute is the single-shell router's verbatim, over the combined graph.</para>
/// </summary>
internal sealed class CrossShellRouter
{
    private readonly MidStack _stack;
    private readonly IReadOnlyList<MidBoard> _shells;
    private readonly HalfEdgeMesh[] _meshes;
    private readonly int _shellCount;
    private readonly int _v;
    private readonly DrcRuleSet _rules;
    private readonly SurfaceRouteOptions _opt;

    private readonly double _traceWidth;
    private readonly double _clearance;
    private readonly double _margin;
    private readonly double _diameter;
    private readonly double _softPenalty;
    private readonly double _viaPadDiameter;
    private readonly double _viaPenalty;

    private readonly Vector3d[] _pos;      // per node (shell·V + vertex)
    private readonly bool[] _boundary;     // per node
    private readonly IReadOnlyList<MidSurfaceFeature>[] _baseFeatures;   // per shell
    private readonly List<(IReadOnlyList<Vector3d> Span, double Pad)> _baseVias = [];
    private readonly Dictionary<string, List<Terminal>> _terminals;

    // A* scratch, generation-stamped so it need not be re-zeroed per search.
    private readonly double[] _g;
    private readonly int[] _from;
    private readonly int[] _seen;
    private int _generation;

    internal CrossShellRouter(MidStack stack, DrcRuleSet rules, SurfaceRouteOptions opt)
    {
        _stack = stack;
        _shells = stack.Shells;
        _shellCount = stack.ShellCount;
        _rules = rules;
        _opt = opt;

        _meshes = new HalfEdgeMesh[_shellCount];
        for (int k = 0; k < _shellCount; k++)
            _meshes[k] = _shells[k].Mesh;
        _v = _meshes[0].VertexCount;

        _traceWidth = opt.TraceWidth > 0 ? opt.TraceWidth : rules.MinTraceWidth;
        _clearance = opt.Clearance > 0 ? opt.Clearance : rules.MinCopperClearance;
        _viaPadDiameter = opt.ViaPadDiameter > 0 ? opt.ViaPadDiameter : 0.5;

        double longest = 0;
        for (int k = 0; k < _shellCount; k++)
            longest = Math.Max(longest, _shells[k].LongestEdge());
        // Half a longest edge covers a mid-edge point of a routed segment, so the raw edge-graph path is
        // DRC-clean by construction (the single-shell router's rule).
        _margin = longest * 0.5;

        int nodes = _shellCount * _v;
        _pos = new Vector3d[nodes];
        _boundary = new bool[nodes];
        var box = Aabb.Empty;
        for (int k = 0; k < _shellCount; k++)
        {
            var mesh = _meshes[k];
            for (int vtx = 0; vtx < _v; vtx++)
            {
                int node = k * _v + vtx;
                _pos[node] = mesh.GetPosition(vtx);
                _boundary[node] = mesh.GetVertex(vtx).IsBoundary;
                box = box.Union(_pos[node]);
            }
        }
        _diameter = Math.Max(box.Size.Length, 1e-30);
        // A soft crossing must cost more than any clean detour (the single-shell router's rule): the mesh
        // diameter × 100 dominates any edge-length path and stays scale-invariant.
        _softPenalty = 100 * _diameter;
        // A via edge's cost must be at least its 3D chord (the dielectric barrel) for the heuristic to stay
        // admissible; a few mesh edges on top is the reluctance that keeps a via from being used gratuitously.
        _viaPenalty = opt.ViaPenalty > 0 ? opt.ViaPenalty : stack.Offset(_shellCount - 1) + longest * 4;

        _g = new double[nodes];
        _from = new int[nodes];
        _seen = new int[nodes];

        _baseFeatures = new IReadOnlyList<MidSurfaceFeature>[_shellCount];
        for (int k = 0; k < _shellCount; k++)
            _baseFeatures[k] = _shells[k].SurfaceFeatures();

        var viaPads = new HashSet<MidPad>(ReferenceEqualityComparer.Instance);
        foreach (var via in stack.Vias)
        {
            _baseVias.Add((via.SpanPolyline, via.PadDiameter));
            foreach (var pad in via.Pads)
                viaPads.Add(pad);
        }
        _terminals = BuildTerminals(viaPads);
    }

    // ---- the top-level rip-up-and-reroute loop -------------------------------

    internal StackRouteResult Run()
    {
        var unrouted = new HashSet<string>(_stack.Connectivity().Unrouted, StringComparer.Ordinal);
        var routable = _terminals.Keys
            .Where(net => unrouted.Contains(net) && _terminals[net].Count >= 2)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var order = InitialOrder(routable);

        var committed = new Dictionary<string, NetRoute>(StringComparer.Ordinal);
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
            var route = RouteNet(net, committed, allowRipUp: false, out _);
            if (route is not null)
            {
                committed[net] = route;
                continue;
            }

            // 2) A rip-up route: cross the committed traces/vias that block it, rip THOSE up, re-route the
            //    net cleanly without them, and re-queue the ripped nets.
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

    // ---- routing one net (MST over its terminals, across both shells) --------

    private NetRoute? RouteNet(
        string net, Dictionary<string, NetRoute> committed, bool allowRipUp, out HashSet<string> victims)
    {
        victims = new HashSet<string>(StringComparer.Ordinal);
        var terms = _terminals[net];
        if (terms.Count < 2)
            return new NetRoute();   // a single pad — nothing to route

        var (hard, soft, committedNets) = BuildBlockage(net, committed);
        var existing = BuildExisting(net, committed);
        var otherViaSpans = BuildOtherViaSpans(committed);
        var route = new NetRoute();
        var newViaSpans = new List<(Vector3d[] Span, double Pad)>();

        var inTree = new bool[terms.Count];
        inTree[0] = true;
        for (int added = 1; added < terms.Count; added++)
        {
            var (srcIdx, nextIdx) = NearestPair(terms, inTree);
            var path = Search(Node(terms[srcIdx]), Node(terms[nextIdx]), hard, soft, allowRipUp);
            if (path is null)
                return null;   // this connection has no route (boxed in) — the whole net fails

            if (allowRipUp)
            {
                CollectVictims(path, soft, committedNets, victims);
                inTree[nextIdx] = true;
                continue;   // rip-up mode only harvests victims; the reroute commits the geometry
            }

            if (!CertifyAndBuild(net, path, terms[srcIdx], terms[nextIdx], existing, otherViaSpans, newViaSpans, route))
                return null;   // the exact DRC rejected the clean route — never ship a violating trace/via
            inTree[nextIdx] = true;
        }
        return route;
    }

    private int Node(in Terminal t) => t.Shell * _v + t.Seed;

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

    private void CollectVictims(List<int> path, int[] soft, List<string> committedNets, HashSet<string> victims)
    {
        foreach (int node in path)
        {
            int mask = soft[node];
            for (int b = 0; b < committedNets.Count; b++)
                if ((mask & (1 << b)) != 0)
                    victims.Add(committedNets[b]);
        }
    }

    // ---- A* over the combined (shell, vertex) graph --------------------------

    /// <summary>The obstacle-aware search from <paramref name="src"/> to <paramref name="target"/> over the
    /// combined graph — each shell's mesh edges plus via edges tying corresponding vertices. Skips
    /// hard-blocked nodes; in a clean route soft nodes are impassable, in a rip-up route they are passable
    /// at a penalty. The heuristic is the 3D straight-line distance, admissible because every edge costs at
    /// least its 3D chord.</summary>
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
            int shell = cur / _v, vert = cur % _v;
            double gc = _g[cur];

            void Relax(int nb, double edge)
            {
                if (hard[nb])
                    return;
                int sm = soft[nb];
                if (!allowRipUp && sm != 0)
                    return;
                double step = edge;
                if (sm != 0)
                    step += _softPenalty;   // crossing a committed trace/via — allowed but costly
                double newG = gc + step;
                if (_seen[nb] == gen && _g[nb] <= newG)
                    return;
                _g[nb] = newG;
                _from[nb] = cur;
                _seen[nb] = gen;
                open.Enqueue(nb, (newG + Heuristic(nb, target), seq++));
            }

            foreach (var h in _meshes[shell].GetVertex(vert).OutgoingHalfEdges())
                Relax(shell * _v + h.Destination.Index, h.Length);
            for (int dk = -1; dk <= 1; dk += 2)
            {
                int kk = shell + dk;
                if ((uint)kk < (uint)_shellCount)
                    Relax(kk * _v + vert, _viaPenalty);   // a via edge changes shell at the same vertex
            }
        }
        return null;
    }

    private double Heuristic(int node, int target) => _pos[node].DistanceTo(_pos[target]);

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
        return path;   // src .. target, combined node ids
    }

    // ---- turning a combined path into validated segments + vias --------------

    /// <summary>Decomposes a certified path into per-shell trace runs and the via vertices between them,
    /// certifies each trace on its shell and each via pad on every shell it touches (plus the via-to-via
    /// web), and — if all clear — records them in <paramref name="route"/>. Returns false when the exact
    /// DRC rejects any piece, so the whole connection fails and the net is left for rip-up or reported
    /// unroutable.</summary>
    private bool CertifyAndBuild(
        string net, List<int> path, Terminal srcTerm, Terminal dstTerm,
        List<MidSurfaceFeature>[] existing,
        List<(IReadOnlyList<Vector3d> Span, double Pad)> otherViaSpans,
        List<(Vector3d[] Span, double Pad)> newViaSpans,
        NetRoute route)
    {
        var (runs, viaVerts) = Decompose(path);

        // Certify + build every per-shell trace segment.
        var segments = new List<Segment>();
        for (int r = 0; r < runs.Count; r++)
        {
            var (shell, verts) = runs[r];
            var board = _shells[shell];
            var cl = new List<SurfacePoint>(verts.Count + 2);
            if (r == 0)
                cl.Add(srcTerm.Point);                        // start on the source pad exactly
            foreach (int vtx in verts)
                cl.Add(board.Surface.Locate(board.Mesh.GetPosition(vtx)));
            if (r == runs.Count - 1)
                cl.Add(dstTerm.Point);                        // end on the target pad exactly
            var deduped = Dedup(cl);
            if (deduped.Count < 2)
                continue;   // a pad coincident with its via foot — connected by touch, no trace needed
            string source = $"{srcTerm.Source}->{dstTerm.Source}#s{shell}r{r}";
            var centreLine = Certify(net, shell, deduped, source, existing[shell]);
            if (centreLine is null)
                return false;
            segments.Add(new Segment(shell, source, _traceWidth, centreLine));
        }

        // Certify each new via: its pad clears other-net copper on every shell, and the via-to-via web
        // (net-agnostic) is met against every existing via and every earlier via of this route.
        var vias = new List<int>();
        foreach (int vtx in viaVerts)
        {
            var span = _stack.ViaSpanAtVertex(vtx);
            for (int k = 0; k < _shellCount; k++)
            {
                var padFeat = ViaPadFeature(net, k, span);
                if (!Mid3dDrc.RouteClearanceClears(_shells[k], padFeat, existing[k], _clearance))
                    return false;
            }
            if (!ViaWebClears(span, otherViaSpans, newViaSpans))
                return false;
            vias.Add(vtx);
            newViaSpans.Add((span, _viaPadDiameter));
        }

        route.Segments.AddRange(segments);
        route.ViaVertices.AddRange(vias);
        return true;
    }

    private (List<(int Shell, List<int> Verts)> Runs, List<int> Vias) Decompose(List<int> path)
    {
        var runs = new List<(int Shell, List<int> Verts)>();
        var vias = new List<int>();
        int prevShell = path[0] / _v;
        var cur = new List<int> { path[0] % _v };
        for (int i = 1; i < path.Count; i++)
        {
            int shell = path[i] / _v, vtx = path[i] % _v;
            if (shell == prevShell)
            {
                cur.Add(vtx);
            }
            else
            {
                // A via edge keeps the vertex (cur[^1] == vtx): the run before and after share it.
                runs.Add((prevShell, cur));
                vias.Add(vtx);
                cur = [vtx];
                prevShell = shell;
            }
        }
        runs.Add((prevShell, cur));
        return (runs, vias);
    }

    /// <summary>Straightens the centre-line toward the geodesic and CERTIFIES it with the exact 3D DRC on
    /// its shell, falling back to the raw obstacle-avoiding edge path when straightening drifts across an
    /// obstacle; returns the certified centre-line, or null when even the raw path cannot be certified
    /// (which over-blocking makes essentially impossible, so it is a defensive gate).</summary>
    private IReadOnlyList<SurfacePoint>? Certify(
        string net, int shell, List<SurfacePoint> deduped, string source, IReadOnlyList<MidSurfaceFeature> existing)
    {
        var board = _shells[shell];
        if (_opt.Straighten)
        {
            var straight = MidRouting.Straighten(board.Surface, deduped, deduped[^1]);
            if (straight.Count >= 2)
            {
                var f = new MidSurfaceFeature(net, source, _traceWidth, straight);
                if (Mid3dDrc.RouteCandidateClears(board, f, existing, _rules))
                    return straight;
            }
        }
        var raw = new MidSurfaceFeature(net, source, _traceWidth, deduped);
        return Mid3dDrc.RouteCandidateClears(board, raw, existing, _rules) ? deduped : null;
    }

    private MidSurfaceFeature ViaPadFeature(string? net, int shell, Vector3d[] span)
    {
        var foot = _shells[shell].Surface.Locate(span[shell]);
        return new MidSurfaceFeature(net, "via", _viaPadDiameter, [foot]);
    }

    private bool ViaWebClears(
        Vector3d[] span,
        List<(IReadOnlyList<Vector3d> Span, double Pad)> other,
        List<(Vector3d[] Span, double Pad)> newer)
    {
        double min = _rules.MinViaToVia;
        if (!(min > 0))
            return true;
        foreach (var (s, pad) in other)
            if (SurfaceGeometry.PolylineDistance(span, s) - _viaPadDiameter / 2 - pad / 2 < min)
                return false;
        foreach (var (s, pad) in newer)
            if (SurfaceGeometry.PolylineDistance(span, s) - _viaPadDiameter / 2 - pad / 2 < min)
                return false;
        return true;
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

    // ---- terminals (user pads across both shells) ----------------------------

    private Dictionary<string, List<Terminal>> BuildTerminals(HashSet<MidPad> viaPads)
    {
        var byNet = new Dictionary<string, List<Terminal>>(StringComparer.Ordinal);
        for (int k = 0; k < _shellCount; k++)
        {
            var board = _shells[k];
            foreach (var pad in board.Pads)
            {
                if (pad.Net is null || viaPads.Contains(pad))
                    continue;   // an unconnected pad is an obstacle; a via pad is a connector, not a terminal
                if (!byNet.TryGetValue(pad.Net, out var list))
                    byNet[pad.Net] = list = [];
                list.Add(new Terminal(k, board.Surface.SeedVertex(pad.Located), pad.Located, pad.Source));
            }
        }
        return byNet;
    }

    // ---- blockage ------------------------------------------------------------

    private (bool[] Hard, int[] Soft, List<string> CommittedNets) BuildBlockage(
        string net, Dictionary<string, NetRoute> committed)
    {
        var committedNets = committed.Keys.Where(n => n != net)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        var netBit = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int b = 0; b < committedNets.Count; b++)
            netBit[committedNets[b]] = b;

        int nodes = _shellCount * _v;
        var hard = new bool[nodes];
        var soft = new int[nodes];

        // The mesh boundary is a hard obstacle on every shell (copper stays on the surface).
        for (int i = 0; i < nodes; i++)
            if (_boundary[i])
                hard[i] = true;

        // Base copper of OTHER nets on each shell (pads, caller-placed traces, caller via pads) — HARD.
        for (int k = 0; k < _shellCount; k++)
            foreach (var f in _baseFeatures[k])
                if (!SameNet(net, f.Net))
                    BlockOnShell(k, f.Polyline, Keepout(f.Width), i => hard[i] = true);

        // Committed OTHER-net copper (this run's traces + vias) — SOFT (rippable), one mask bit each. A net
        // past the 31-bit mask capacity is treated as HARD (non-rippable): safe (it stays an obstacle).
        foreach (var m in committedNets)
        {
            bool rippable = netBit[m] < 31;
            int bit = rippable ? 1 << netBit[m] : 0;
            Action<int> mark = rippable ? i => soft[i] |= bit : i => hard[i] = true;
            var route = committed[m];
            foreach (var seg in route.Segments)
                BlockOnShell(seg.Shell, seg.Polyline(), Keepout(seg.Width), mark);
            foreach (int vtx in route.ViaVertices)
            {
                var span = _stack.ViaSpanAtVertex(vtx);
                for (int k = 0; k < _shellCount; k++)
                    BlockOnShell(k, [span[k]], Keepout(_viaPadDiameter), mark);
            }
        }
        return (hard, soft, committedNets);
    }

    private List<MidSurfaceFeature>[] BuildExisting(string net, Dictionary<string, NetRoute> committed)
    {
        var existing = new List<MidSurfaceFeature>[_shellCount];
        for (int k = 0; k < _shellCount; k++)
            existing[k] = [.. _baseFeatures[k]];
        foreach (var (m, route) in committed)
        {
            if (m == net)
                continue;
            foreach (var seg in route.Segments)
                existing[seg.Shell].Add(seg.Feature(m));
            foreach (int vtx in route.ViaVertices)
            {
                var span = _stack.ViaSpanAtVertex(vtx);
                for (int k = 0; k < _shellCount; k++)
                    existing[k].Add(ViaPadFeature(m, k, span));
            }
        }
        return existing;
    }

    /// <summary>Every via the candidate must clear via-to-via — the caller-placed vias plus every committed
    /// via (all of committed is another net during this net's routing; the rule is net-agnostic).</summary>
    private List<(IReadOnlyList<Vector3d> Span, double Pad)> BuildOtherViaSpans(Dictionary<string, NetRoute> committed)
    {
        var spans = new List<(IReadOnlyList<Vector3d> Span, double Pad)>(_baseVias);
        foreach (var route in committed.Values)
            foreach (int vtx in route.ViaVertices)
                spans.Add((_stack.ViaSpanAtVertex(vtx), _viaPadDiameter));
        return spans;
    }

    /// <summary>The surface keep-out from a shell vertex to another-net centre-line of the given width —
    /// the clearance, both half-widths, plus one longest edge so a mid-edge point of the routed trace is
    /// covered by the block on a vertex.</summary>
    private double Keepout(double otherWidth) =>
        _clearance + _traceWidth / 2 + otherWidth / 2 + _margin;

    private void BlockOnShell(int shell, IReadOnlyList<Vector3d> poly, double keepout, Action<int> mark)
    {
        var box = Bounds(poly).Expanded(keepout);
        double k2 = keepout * keepout;
        int baseNode = shell * _v;
        for (int vtx = 0; vtx < _v; vtx++)
        {
            var p = _pos[baseNode + vtx];
            if (!box.Contains(p))
                continue;
            if (SquaredDistToPolyline(p, poly) < k2)
                mark(baseNode + vtx);
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

    // ---- committing to the stack ---------------------------------------------

    private StackRouteResult Finish(List<string> order, Dictionary<string, NetRoute> committed, int ripUps)
    {
        var routedNets = order.Where(committed.ContainsKey).ToList();
        var unroutedNets = order.Where(n => !committed.ContainsKey(n))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();

        var placedTraces = new List<SurfaceTrace>();
        var placedVias = new List<SurfaceVia>();
        foreach (var net in order)
        {
            if (!committed.TryGetValue(net, out var route))
                continue;
            foreach (int vtx in route.ViaVertices)
                placedVias.Add(_stack.AddViaAtVertex(net, vtx, _viaPadDiameter));
            foreach (var seg in route.Segments)
                placedTraces.Add(_shells[seg.Shell].PlaceSurfaceTrace(net, seg.CentreLine, seg.Width, seg.Source));
        }
        return new StackRouteResult(_stack, routedNets, unroutedNets, placedTraces, placedVias,
            placedTraces.Count, placedVias.Count, ripUps);
    }

    /// <summary>One pad the router routes to/from: which shell it is on, its nearest mesh vertex (the graph
    /// seed on that shell), its exact surface point (so a trace endpoint lands ON the pad), and its
    /// source name.</summary>
    private readonly record struct Terminal(int Shell, int Seed, SurfacePoint Point, string Source);

    /// <summary>One committed per-shell trace segment: which shell, a source name, a width, and a
    /// centre-line of surface points.</summary>
    private sealed record Segment(int Shell, string Source, double Width, IReadOnlyList<SurfacePoint> CentreLine)
    {
        private Vector3d[]? _polyline;

        public IReadOnlyList<Vector3d> Polyline() => _polyline ??= [.. CentreLine.Select(p => p.Position)];

        public MidSurfaceFeature Feature(string? net) => new(net, Source, Width, CentreLine);
    }

    /// <summary>A net's committed cross-shell route: its per-shell trace segments and the mesh vertices
    /// where it drops a through-shell via. Removed as a unit when the net is ripped up.</summary>
    private sealed class NetRoute
    {
        public List<Segment> Segments { get; } = [];
        public List<int> ViaVertices { get; } = [];
    }
}
