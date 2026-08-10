using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Ecad;

/// <summary>The routing angles the grid allows — 90° (orthogonal only) or 45° (orthogonal +
/// diagonal). 45° is the default; a diagonal move is only taken when it does not clip an obstacle
/// corner.</summary>
public enum RouteAngles
{
    /// <summary>Manhattan routing: the grid is 4-connected, so traces turn only at right angles.</summary>
    Deg90,

    /// <summary>The grid is 8-connected, so traces run at 45° as well as 90° (the default).</summary>
    Deg45,
}

/// <summary>
/// Options for <see cref="PcbRouter.Route"/>. Every field has a clearance-derived default (a zero
/// or null means "derive it"), so <c>new RouterOptions()</c> is a sensible auto-route. The grid is
/// an ACCELERATOR — the exact DRC decides every commit — so the grid resolution trades speed against
/// how tightly the router can weave, never correctness.
/// </summary>
public sealed record RouterOptions
{
    /// <summary>The routing grid pitch (mm); 0 = <c>TraceWidth + Clearance</c> (adjacent tracks are
    /// then DRC-clean by construction). Finer weaves tighter and routes slower.</summary>
    public double GridResolution { get; init; }

    /// <summary>The trace width (mm); 0 = <see cref="DrcRuleSet.MinTraceWidth"/>.</summary>
    public double TraceWidth { get; init; }

    /// <summary>The copper-to-copper clearance the router keeps (mm); 0 =
    /// <see cref="DrcRuleSet.MinCopperClearance"/>. The exact DRC is checked against the SAME rule
    /// set, so this only steers the grid.</summary>
    public double Clearance { get; init; }

    /// <summary>The routing angles (default 45°).</summary>
    public RouteAngles Angles { get; init; } = RouteAngles.Deg45;

    /// <summary>Whether the router may change layers through a via (default true). A single-layer
    /// board or <c>false</c> forbids vias, so a net needing to cross another is reported unroutable.</summary>
    public bool AllowVias { get; init; } = true;

    /// <summary>The finished via drill diameter (mm); 0 = a default 0.4 mm.</summary>
    public double ViaDrill { get; init; }

    /// <summary>The via annular pad diameter (mm); 0 = derived so the ring clears the annular-ring
    /// AND trace-width rules with margin.</summary>
    public double ViaPad { get; init; }

    /// <summary>The cost of placing a via (mm-equivalent), which trades layer changes against
    /// in-plane detours; 0 = <c>10 × GridResolution</c>.</summary>
    public double ViaCost { get; init; }

    /// <summary>The maximum number of times any one net may trigger a rip-up (default 8). When a net
    /// cannot find a clean route, the router routes it ACROSS the traces that block it (at a high
    /// cost), rips those traces up, and re-queues them — negotiated congestion. This bounds the
    /// rip-ups so a truly boxed-in net terminates and is reported UNROUTABLE by name. 0 disables
    /// rip-up (a pure greedy pass).</summary>
    public int MaxRipUpIterations { get; init; } = 8;

    /// <summary>An explicit net routing order (net names). Nets not listed follow in ordinal order.
    /// null = ordinal order. A fixed order + a fixed grid gives bit-deterministic routes.</summary>
    public IReadOnlyList<string>? NetOrder { get; init; }
}

/// <summary>
/// The result of <see cref="PcbRouter.Route"/>: the routed layout (traces and vias added), which
/// nets routed and which did NOT, and the counts. The routed <see cref="Layout"/> is guaranteed
/// DRC-clean against the rule set it was routed with (the router commits a trace only after the
/// exact DRC confirms it adds no violation), so a PARTIAL result — one that could not route every
/// net — is still clean, with the unroutable nets NAMED rather than faked.
/// </summary>
/// <param name="Layout">The routed layout (a new layout; the input is not mutated).</param>
/// <param name="RoutedNets">The nets that were routed (had ≥ 2 pads to join and got connected), in
/// routing order.</param>
/// <param name="UnroutedNets">The nets that could NOT be routed, in ordinal order — reported, not
/// faked.</param>
/// <param name="TracesAdded">The number of trace segments added.</param>
/// <param name="ViasAdded">The number of vias placed.</param>
/// <param name="RipUps">How many times a net was routed across another and the blocking net ripped
/// up and re-routed (0 = every net found a clean route the first time).</param>
public sealed record RoutedResult(
    PcbLayout Layout,
    IReadOnlyList<string> RoutedNets,
    IReadOnlyList<string> UnroutedNets,
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

/// <summary>
/// A DRC-aware grid/maze autorouter. It searches a uniform routing grid (x, y, layer) with A*,
/// changes layers through vias, decomposes multi-pin nets into 2-pin connections over an MST, and
/// rips up and re-routes when a net is boxed in by congestion.
///
/// <para><b>The grid is an accelerator; the exact DRC is the source of truth.</b> A candidate route
/// is committed only after <see cref="PcbDrc.Violates"/> (plus the drill / via rules) confirms it
/// adds NO violation to the board — so a grid rounding error can never produce a clearance-violating
/// trace. If a candidate the grid liked fails the exact check, it is rejected, not shipped. A net
/// that cannot be routed cleanly is reported UNROUTABLE by name (never a silent violation), and the
/// partial board is still DRC-clean.</para>
///
/// <para><b>v1 scope</b> (each boundary stated, the rest filed): a grid/maze A* with rip-up-reroute,
/// through-vias (spanning all copper layers) for layer changes, and 2-pin MST decomposition of
/// multi-pin nets. NOT in v1: topological / shove / push routing, length matching and differential
/// pairs, copper pours, teardrops, and cavity walls as routing obstacles.</para>
/// </summary>
public static class PcbRouter
{
    /// <summary>Auto-routes a placed layout against a rule set. Returns a NEW routed layout (the
    /// input is not mutated); a layout with nothing to route (every signal net already connected)
    /// returns unchanged, so <c>result.Layout.Save()</c> is byte-identical to the input's.</summary>
    /// <param name="layout">The placed layout to route.</param>
    /// <param name="rules">The design rules to route to and check against (null = the defaults).</param>
    /// <param name="options">The router options (null = the defaults).</param>
    public static RoutedResult Route(PcbLayout layout, DrcRuleSet? rules = null, RouterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return new RouterEngine(layout, rules ?? DrcRuleSet.Default, options ?? new RouterOptions()).Run();
    }
}

/// <summary>The routing engine — the grid, the A*, the MST and the rip-up loop. Deterministic: a
/// fixed net order and grid give bit-identical routes, so nothing is iterated in a
/// hash-order-dependent way that affects the output.</summary>
internal sealed class RouterEngine
{
    private readonly PcbLayout _layout;
    private readonly DrcRuleSet _rules;
    private readonly RouterOptions _opt;
    private readonly PcbBoard _board;
    private readonly PcbCopperModel _base;

    private readonly double _minX, _minY, _res;
    private readonly int _nx, _ny;
    private readonly string[] _layerNames;
    private readonly int _nLayers;
    private readonly double _traceWidth, _clearance, _viaDrill, _viaPad, _viaCost;
    private readonly string _viaTop, _viaBottom;

    // A* scratch (generation-stamped so it need not be re-zeroed per search).
    private readonly double[] _g;
    private readonly int[] _from;
    private readonly int[] _seen;
    private int _generation;

    // The in-plane grid moves, built once (the per-node A* loop is the hottest path).
    private readonly (int, int)[] _moves;
    private static readonly double Sqrt2 = Math.Sqrt(2);

    internal RouterEngine(PcbLayout layout, DrcRuleSet rules, RouterOptions opt)
    {
        _layout = layout;
        _rules = rules;
        _opt = opt;
        _board = layout.Board;
        _base = PcbCopperModel.FromLayout(layout);

        _layerNames = _board.Stackup.Coppers.Select(c => c.Name).ToArray();
        _nLayers = _layerNames.Length;
        _viaTop = _board.Stackup.Top.Name;
        _viaBottom = _board.Stackup.Bottom.Name;

        _clearance = opt.Clearance > 0 ? opt.Clearance : rules.MinCopperClearance;
        _traceWidth = opt.TraceWidth > 0 ? opt.TraceWidth : rules.MinTraceWidth;
        _res = opt.GridResolution > 0 ? opt.GridResolution : _traceWidth + _clearance;
        _viaDrill = opt.ViaDrill > 0 ? opt.ViaDrill : 0.4;
        double ringNeed = Math.Max(rules.MinAnnularRing, rules.MinTraceWidth);
        _viaPad = opt.ViaPad > 0 ? opt.ViaPad : _viaDrill + 2 * (ringNeed + 0.05);
        _viaCost = opt.ViaCost > 0 ? opt.ViaCost : 10 * _res;

        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        _minX = _minY = double.PositiveInfinity;
        foreach (var p in _board.OutlinePoints)
        {
            _minX = Math.Min(_minX, p.X);
            _minY = Math.Min(_minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }
        _nx = Math.Max(1, (int)Math.Round((maxX - _minX) / _res) + 1);
        _ny = Math.Max(1, (int)Math.Round((maxY - _minY) / _res) + 1);

        int cells = _nx * _ny * _nLayers;
        _g = new double[cells];
        _from = new int[cells];
        _seen = new int[cells];

        // 8-connected (45°) puts orthogonals first for determinism; the 4-connected (90°) case
        // takes only the first four.
        _moves = opt.Angles == RouteAngles.Deg45
            ? [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)]
            : [(1, 0), (-1, 0), (0, 1), (0, -1)];
    }

    // ---- cell encoding -------------------------------------------------------

    private int Cell(int i, int j, int layer) => (layer * _ny + j) * _nx + i;
    private (int I, int J, int Layer) Decode(int id)
    {
        int layer = id / (_nx * _ny);
        int rem = id % (_nx * _ny);
        return (rem % _nx, rem / _nx, layer);
    }
    private Vector2d Pos(int i, int j) => new(_minX + i * _res, _minY + j * _res);
    private (int I, int J) Snap(in Vector2d p) => (
        Math.Clamp((int)Math.Round((p.X - _minX) / _res), 0, _nx - 1),
        Math.Clamp((int)Math.Round((p.Y - _minY) / _res), 0, _ny - 1));

    // ---- the top-level rip-up-and-reroute loop -------------------------------

    internal RoutedResult Run()
    {
        var terminals = BuildTerminals();
        var unrouted = new HashSet<string>(PcbConnectivity.Analyze(_base).Unrouted, StringComparer.Ordinal);
        var routable = terminals.Keys
            .Where(net => unrouted.Contains(net) && terminals[net].Count >= 2)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var order = InitialOrder(routable);

        var committed = new Dictionary<string, RouteGeom>(StringComparer.Ordinal);
        if (order.Count == 0)
            return new RoutedResult(RebuildLayout(order, committed), [], [], 0, 0, 0);

        var queue = new Queue<string>(order);
        var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        int ripUps = 0;
        int budget = (order.Count + 1) * (_opt.MaxRipUpIterations + 1);

        for (int step = 0; step < budget && queue.Count > 0; step++)
        {
            string net = queue.Dequeue();
            if (committed.ContainsKey(net))
                continue;

            var model = BuildCommittedModel(committed);
            var blockage = BuildBlockage(net, committed, out var committedNets);

            // 1) A clean route (committed copper is a hard obstacle).
            var geom = RouteNet(net, terminals[net], model, blockage, allowRipUp: false, committedNets, out _);
            if (geom is not null)
            {
                committed[net] = geom;
                continue;
            }

            // 2) A rip-up route: cross the traces that block it (at a high cost), then rip THOSE
            //    up, re-route the net cleanly without them, and re-queue the ripped nets.
            if (_opt.MaxRipUpIterations > 0 && attempts.GetValueOrDefault(net) < _opt.MaxRipUpIterations)
            {
                _ = RouteNet(net, terminals[net], model, blockage, allowRipUp: true, committedNets, out var victims);
                if (victims.Count > 0)
                {
                    var saved = victims.ToDictionary(v => v, v => committed[v], StringComparer.Ordinal);
                    foreach (var v in victims)
                        committed.Remove(v);

                    var model2 = BuildCommittedModel(committed);
                    var block2 = BuildBlockage(net, committed, out var committedNets2);
                    var rerouted = RouteNet(net, terminals[net], model2, block2, allowRipUp: false, committedNets2, out _);
                    if (rerouted is not null)
                    {
                        committed[net] = rerouted;
                        attempts[net] = attempts.GetValueOrDefault(net) + 1;
                        ripUps++;
                        foreach (var v in victims)
                            queue.Enqueue(v);
                        continue;
                    }
                    foreach (var v in victims)   // re-route failed — restore the ripped nets
                        committed[v] = saved[v];
                }
            }
            // 3) Could not route even with rip-up — leave it unrouted (reported by name).
        }

        var routedNets = order.Where(committed.ContainsKey).ToList();
        var unroutedFinal = order.Where(n => !committed.ContainsKey(n))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        var routedLayout = RebuildLayout(order, committed);
        int traces = committed.Values.Sum(g => g.Traces.Count);
        int vias = committed.Values.Sum(g => g.Vias.Count);
        return new RoutedResult(routedLayout, routedNets, unroutedFinal, traces, vias, ripUps);
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

    private RouteGeom? RouteNet(
        string net, List<Terminal> terms, PcbCopperModel model, Blockage blockage,
        bool allowRipUp, List<string> committedNets, out HashSet<string> victims)
    {
        victims = new HashSet<string>(StringComparer.Ordinal);
        if (terms.Count < 2)
            return new RouteGeom();   // a single pad — nothing to route

        var geom = new RouteGeom();
        var inTree = new bool[terms.Count];
        inTree[0] = true;
        var treeCells = new HashSet<int>();
        var anchor = new Dictionary<int, Vector2d>();
        Seed(terms[0], treeCells, anchor);

        for (int added = 1; added < terms.Count; added++)
        {
            int next = NearestOutside(terms, inTree);
            var goals = new HashSet<int>(terms[next].Cells);

            var path = AStar(treeCells, goals, blockage, allowRipUp);
            if (path is null)
                return null;

            // Collect the committed nets this route crossed (rip-up victims).
            if (allowRipUp)
                foreach (int c in path)
                {
                    var (ci, cj, cl) = Decode(c);
                    int mask = blockage.Soft(ci, cj, cl);
                    for (int b = 0; b < committedNets.Count; b++)
                        if ((mask & (1 << b)) != 0)
                            victims.Add(committedNets[b]);
                }

            var edge = BuildEdge(net, path, anchor, terms[next].Center);
            if (!allowRipUp && !CandidateClean(model, edge))
                return null;   // the exact DRC rejects the clean route — never ship a violating trace

            geom.Merge(edge);
            inTree[next] = true;
            foreach (int c in path)
                treeCells.Add(c);
            Seed(terms[next], treeCells, anchor);
        }
        return geom;
    }

    private void Seed(Terminal t, HashSet<int> cells, Dictionary<int, Vector2d> anchor)
    {
        foreach (int c in t.Cells)
        {
            cells.Add(c);
            anchor[c] = t.Center;
        }
    }

    private static int NearestOutside(List<Terminal> terms, bool[] inTree)
    {
        int best = -1;
        double bestDist = double.PositiveInfinity;
        for (int i = 0; i < terms.Count; i++)
        {
            if (inTree[i])
                continue;
            double d = double.PositiveInfinity;
            for (int j = 0; j < terms.Count; j++)
                if (inTree[j])
                    d = Math.Min(d, terms[i].Center.DistanceTo(terms[j].Center));
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    // ---- A* over (i, j, layer) -----------------------------------------------

    private List<int>? AStar(HashSet<int> sources, HashSet<int> goals, Blockage blockage, bool allowRipUp)
    {
        var goalCells = goals.Select(Decode).Select(d => (d.I, d.J)).Distinct().ToList();
        if (goalCells.Count == 0)
            return null;

        _generation++;
        int gen = _generation;
        var open = new PriorityQueue<int, (double F, long Seq)>();
        long seq = 0;

        foreach (int s in sources.OrderBy(x => x))
        {
            var (si, sj, sl) = Decode(s);
            if (blockage.HardTrace(si, sj, sl))
                continue;
            if (!allowRipUp && blockage.Soft(si, sj, sl) != 0)
                continue;
            _g[s] = 0;
            _from[s] = -1;
            _seen[s] = gen;
            open.Enqueue(s, (Heuristic(si, sj, goalCells), seq++));
        }

        while (open.TryDequeue(out int cur, out _))
        {
            if (goals.Contains(cur))
                return Reconstruct(cur, gen);

            var (ci, cj, cl) = Decode(cur);
            double gc = _g[cur];

            foreach (var (di, dj) in _moves)
            {
                int ni = ci + di, nj = cj + dj;
                if (ni < 0 || nj < 0 || ni >= _nx || nj >= _ny)
                    continue;
                if (blockage.HardTrace(ni, nj, cl))
                    continue;
                int soft = blockage.Soft(ni, nj, cl);
                if (!allowRipUp && soft != 0)
                    continue;
                bool diagonal = di != 0 && dj != 0;
                if (diagonal && (blockage.CleanTraceBlocked(ci + di, cj, cl) || blockage.CleanTraceBlocked(ci, cj + dj, cl)))
                    continue;   // do not clip an obstacle corner on a diagonal
                double step = diagonal ? _res * Sqrt2 : _res;
                if (soft != 0)
                    step += RipUpPenalty;   // crossing a committed trace — allowed but costly
                Relax(cur, Cell(ni, nj, cl), gc + step, gen, open, ref seq, goalCells);
            }

            if (_opt.AllowVias && _nLayers > 1 && !blockage.CleanViaBlocked(ci, cj))
                for (int nl = 0; nl < _nLayers; nl++)
                    if (nl != cl)
                        Relax(cur, Cell(ci, cj, nl), gc + _viaCost, gen, open, ref seq, goalCells);
        }
        return null;
    }

    private double RipUpPenalty => 50 * _res;

    private void Relax(
        int cur, int nb, double newG, int gen,
        PriorityQueue<int, (double, long)> open, ref long seq, List<(int, int)> goalCells)
    {
        if (_seen[nb] == gen && _g[nb] <= newG)
            return;
        _g[nb] = newG;
        _from[nb] = cur;
        _seen[nb] = gen;
        var (ni, nj, _) = Decode(nb);
        open.Enqueue(nb, (newG + Heuristic(ni, nj, goalCells), seq++));
    }

    private double Heuristic(int i, int j, List<(int I, int J)> goals)
    {
        double best = double.PositiveInfinity;
        foreach (var (gi, gj) in goals)
        {
            int dx = Math.Abs(i - gi), dy = Math.Abs(j - gj);
            double h = _opt.Angles == RouteAngles.Deg45
                ? (Math.Min(dx, dy) * Sqrt2 + Math.Abs(dx - dy)) * _res
                : (dx + dy) * _res;
            best = Math.Min(best, h);
        }
        return best;
    }

    private List<int> Reconstruct(int goal, int gen)
    {
        var path = new List<int>();
        int c = goal;
        while (c != -1 && _seen[c] == gen)
        {
            path.Add(c);
            c = _from[c];
        }
        path.Reverse();
        return path;
    }

    // ---- turning a grid path into traces + vias ------------------------------

    private RouteGeom BuildEdge(string net, List<int> path, Dictionary<int, Vector2d> anchor, Vector2d goalCentre)
    {
        var geom = new RouteGeom();
        int start = 0;
        var viaCells = new HashSet<(int, int)>();
        var runs = new List<(int Start, int End, int Layer)>();
        for (int k = 1; k <= path.Count; k++)
        {
            var cur = Decode(path[k - 1]);
            if (k == path.Count || Decode(path[k]).Layer != cur.Layer)
            {
                runs.Add((start, k - 1, cur.Layer));
                start = k;
            }
        }
        for (int r = 1; r < runs.Count; r++)
        {
            var (vi, vj, _) = Decode(path[runs[r].Start]);
            if (viaCells.Add((vi, vj)))
            {
                var p = Pos(vi, vj);
                geom.Vias.Add(new Via(net, p.X, p.Y, _viaTop, _viaBottom, _viaDrill, _viaPad));
            }
        }
        for (int r = 0; r < runs.Count; r++)
        {
            var (a, b, layer) = runs[r];
            var pts = new List<Vector2d>(b - a + 1);
            for (int k = a; k <= b; k++)
            {
                var (i, j, _) = Decode(path[k]);
                pts.Add(Pos(i, j));
            }
            if (r == 0 && anchor.TryGetValue(path[a], out var srcCentre))
                pts.Insert(0, srcCentre);
            if (r == runs.Count - 1)
                pts.Add(goalCentre);

            var simplified = Simplify(pts);
            if (simplified.Count >= 2)
                geom.Traces.Add(new PcbTrace(net, _layerNames[layer], _traceWidth, simplified));
        }
        return geom;
    }

    /// <summary>Drops consecutive coincident points and collapses runs of collinear points, so a
    /// straight stretch of grid nodes becomes ONE segment (a clean, few-segment trace).</summary>
    private static List<Vector2d> Simplify(List<Vector2d> pts)
    {
        var dedup = new List<Vector2d>(pts.Count);
        foreach (var p in pts)
            if (dedup.Count == 0 || dedup[^1].DistanceTo(p) > CurvedRegion2d.BoundaryTolerance)
                dedup.Add(p);
        if (dedup.Count <= 2)
            return dedup;
        var outp = new List<Vector2d> { dedup[0] };
        for (int i = 1; i < dedup.Count - 1; i++)
        {
            var a = outp[^1];
            var b = dedup[i];
            var c = dedup[i + 1];
            double cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            double scale = (b - a).Length * (c - a).Length;
            if (Math.Abs(cross) > 1e-9 * Math.Max(scale, 1e-12))
                outp.Add(b);
        }
        outp.Add(dedup[^1]);
        return outp;
    }

    // ---- the exact DRC commit gate (the source of truth) ---------------------

    /// <summary>Whether a candidate route (all one net) adds NO violation to the model as it stands —
    /// the exact DRC check that decides every commit, so the grid can never ship a violating trace.
    /// It asks the DRC's OWN incremental seam rather than restating its constructions:
    /// <see cref="PcbDrc.Violates(PcbCopperModel, CopperFeature, DrcRuleSet?)"/> for every new copper
    /// feature (clearance / short / copper-to-edge / trace-width / acute-angle / drill-to-copper) and
    /// <see cref="PcbDrc.Violates(PcbCopperModel, PlacedVia, DrcRuleSet?)"/> for every new via (annular
    /// ring / drill-to-copper / via-to-via), the latter checked against the model AND the route's own
    /// other new vias so two vias of one route are spaced too.</summary>
    private bool CandidateClean(PcbCopperModel model, RouteGeom edge)
    {
        var newVias = edge.Vias.Select((v, k) => ResolveVia(v, $"cand~v{k}")).ToList();
        var newFeatures = new List<CopperFeature>();
        int fk = 0;
        foreach (var trace in edge.Traces)
            foreach (var region in TraceGeometry.Regions(trace))
                newFeatures.Add(new CopperFeature(trace.Layer, trace.Net, $"cand#{fk++}", region));
        foreach (var placed in newVias)
            foreach (var layer in placed.Layers)
                newFeatures.Add(new CopperFeature(layer, placed.Net, placed.Source, placed.PadRegion));

        // Every new copper feature (trace + via pad) clears other-net copper / the edge / other-net
        // drills / itself.
        foreach (var f in newFeatures)
            if (PcbDrc.Violates(model, f, _rules).Violations.Count > 0)
                return false;

        // Every new via clears the drill / via rules against the model AND the route's own other new
        // vias (a model that carries them, so a route placing two vias spaces them too).
        for (int i = 0; i < newVias.Count; i++)
        {
            var others = newVias.Where((_, k) => k != i);
            var withOthers = newVias.Count > 1
                ? new PcbCopperModel(_board, model.Copper, model.Drills, _base.Cavities, model.Vias.Concat(others))
                : model;
            if (PcbDrc.Violates(withOthers, newVias[i], _rules).Violations.Count > 0)
                return false;
        }
        return true;
    }

    // ---- the running copper model + blockage ---------------------------------

    private PcbCopperModel BuildCommittedModel(Dictionary<string, RouteGeom> committed)
    {
        var copper = new List<CopperFeature>(_base.Copper);
        var drills = new List<DrilledHole>(_base.Drills);
        var vias = new List<PlacedVia>(_base.Vias);
        foreach (var (net, geom) in committed)
        {
            int k = 0;
            foreach (var trace in geom.Traces)
                foreach (var region in TraceGeometry.Regions(trace))
                    copper.Add(new CopperFeature(trace.Layer, net, $"{net}#t{k++}", region));
            int vk = 0;
            foreach (var via in geom.Vias)
            {
                var placed = ResolveVia(via, $"{net}~v{vk++}");
                vias.Add(placed);
                drills.Add(new DrilledHole(placed.Center, placed.DrillDiameter, placed.PadDiameter, net, placed.Source));
                foreach (var layer in placed.Layers)
                    copper.Add(new CopperFeature(layer, net, placed.Source, placed.PadRegion));
            }
        }
        return new PcbCopperModel(_board, copper, drills, _base.Cavities, vias);
    }

    /// <summary>The per-net grid blockage. Copper of OTHER nets is either HARD (base pads / board
    /// vias / the board edge — never rippable) or SOFT (a committed trace / via, which a rip-up route
    /// may cross at a cost, then rip up). Trace blockage is per layer; via blockage spans all layers.
    /// This is an ACCELERATOR — the exact DRC decides the actual commit — so over- or under-blocking
    /// only costs a detour or a commit-time rejection, never correctness.</summary>
    private Blockage BuildBlockage(string net, Dictionary<string, RouteGeom> committed, out List<string> committedNets)
    {
        committedNets = committed.Keys.Where(n => n != net)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        var netBit = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int b = 0; b < committedNets.Count; b++)
            netBit[committedNets[b]] = b;

        int cells = _nx * _ny;
        var hardTrace = new bool[_nLayers][];
        var softTrace = new int[_nLayers][];
        for (int l = 0; l < _nLayers; l++)
        {
            hardTrace[l] = new bool[cells];
            softTrace[l] = new int[cells];
        }
        var hardVia = new bool[cells];
        var softVia = new int[cells];

        double traceThresh = _clearance + _traceWidth / 2;
        double viaThresh = _clearance + _viaPad / 2;

        // Base copper (pads, board vias) — HARD.
        foreach (var f in _base.Copper)
        {
            if (f.Net is not null && f.Net == net)
                continue;
            int layer = Array.IndexOf(_layerNames, f.Layer);
            var bounds = f.Region.Bounds;
            var poly = f.Region.ToRegion(Math.Min(_res * 0.1, 0.05));
            if (layer >= 0)
                RasterizeRegion(bounds, traceThresh, poly, idx => hardTrace[layer][idx] = true);
            RasterizeRegion(bounds, viaThresh, poly, idx => hardVia[idx] = true);
        }

        // Committed traces / vias of OTHER nets — SOFT (rippable), one mask bit each. A committed
        // net past the 31-bit mask capacity is treated as HARD (non-rippable): the router simply
        // cannot pick it as a rip-up victim, which is safe (it stays an obstacle and the net that
        // wanted it may be reported unrouted) rather than misattributing a shifted bit.
        foreach (var m in committedNets)
        {
            bool rippable = netBit[m] < 31;
            int bit = rippable ? 1 << netBit[m] : 0;
            void Copper(int layer, Aabb bounds, Region2d poly)
            {
                if (rippable)
                {
                    if (layer >= 0)
                        RasterizeRegion(bounds, traceThresh, poly, idx => softTrace[layer][idx] |= bit);
                    RasterizeRegion(bounds, viaThresh, poly, idx => softVia[idx] |= bit);
                }
                else
                {
                    if (layer >= 0)
                        RasterizeRegion(bounds, traceThresh, poly, idx => hardTrace[layer][idx] = true);
                    RasterizeRegion(bounds, viaThresh, poly, idx => hardVia[idx] = true);
                }
            }
            void Web(in Vector2d centre, double radius)
            {
                if (rippable)
                    RasterizeDisc(centre, radius, idx => softVia[idx] |= bit);
                else
                    RasterizeDisc(centre, radius, idx => hardVia[idx] = true);
            }
            foreach (var trace in committed[m].Traces)
            {
                int layer = Array.IndexOf(_layerNames, trace.Layer);
                foreach (var region in TraceGeometry.Regions(trace))
                    Copper(layer, region.Bounds, region.ToRegion(Math.Min(_res * 0.1, 0.05)));
            }
            foreach (var via in committed[m].Vias)
            {
                var placed = ResolveVia(via, "b");
                var poly = placed.PadRegion.ToRegion(Math.Min(_res * 0.1, 0.05));
                foreach (var layer in placed.Layers.Select(n => Array.IndexOf(_layerNames, n)))
                    Copper(layer, placed.PadRegion.Bounds, poly);
                Web(placed.Center, _rules.MinViaToVia + _viaDrill / 2 + placed.DrillDiameter / 2);
            }
        }

        // The board edge — HARD.
        var boardRegion = new Region2d([.. _board.OutlinePoints]);
        var traceInner = Region2dOffset.Offset(boardRegion, -(_rules.MinCopperToEdge + _traceWidth / 2));
        var viaInner = Region2dOffset.Offset(boardRegion, -(_rules.MinCopperToEdge + _viaPad / 2));
        for (int j = 0; j < _ny; j++)
            for (int i = 0; i < _nx; i++)
            {
                var p = Pos(i, j);
                if (!InsideAny(traceInner, p))
                    for (int l = 0; l < _nLayers; l++)
                        hardTrace[l][j * _nx + i] = true;
                if (!InsideAny(viaInner, p))
                    hardVia[j * _nx + i] = true;
            }

        // Base via drills space a new via (via-to-via web) — HARD.
        foreach (var ev in _base.Vias)
            RasterizeDisc(ev.Center, _rules.MinViaToVia + _viaDrill / 2 + ev.DrillDiameter / 2,
                idx => hardVia[idx] = true);

        return new Blockage(hardTrace, softTrace, hardVia, softVia, _nx);
    }

    private void RasterizeRegion(in Aabb bounds, double thresh, Region2d poly, Action<int> mark)
    {
        int i0 = Math.Max(0, (int)Math.Floor((bounds.Min.X - thresh - _minX) / _res));
        int i1 = Math.Min(_nx - 1, (int)Math.Ceiling((bounds.Max.X + thresh - _minX) / _res));
        int j0 = Math.Max(0, (int)Math.Floor((bounds.Min.Y - thresh - _minY) / _res));
        int j1 = Math.Min(_ny - 1, (int)Math.Ceiling((bounds.Max.Y + thresh - _minY) / _res));
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
                if (PointNearRegion(poly, Pos(i, j), thresh))
                    mark(j * _nx + i);
    }

    private void RasterizeDisc(in Vector2d centre, double radius, Action<int> mark)
    {
        int i0 = Math.Max(0, (int)Math.Floor((centre.X - radius - _minX) / _res));
        int i1 = Math.Min(_nx - 1, (int)Math.Ceiling((centre.X + radius - _minX) / _res));
        int j0 = Math.Max(0, (int)Math.Floor((centre.Y - radius - _minY) / _res));
        int j1 = Math.Min(_ny - 1, (int)Math.Ceiling((centre.Y + radius - _minY) / _res));
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
                if (Pos(i, j).DistanceTo(centre) < radius)
                    mark(j * _nx + i);
    }

    private static bool PointNearRegion(Region2d poly, in Vector2d p, double thresh)
    {
        if (RegionContains(poly, p))
            return true;   // the point is on the copper
        double d = DistToLoop(poly.Outer, p);
        foreach (var h in poly.Holes)
            d = Math.Min(d, DistToLoop(h, p));
        return d < thresh;
    }

    private static bool InsideAny(IReadOnlyList<Region2d> regions, in Vector2d p)
    {
        foreach (var r in regions)
            if (RegionContains(r, p))
                return true;
        return false;
    }

    /// <summary>Point-in-region: inside the outer loop and outside every hole.</summary>
    private static bool RegionContains(Region2d region, in Vector2d p)
    {
        if (!PcbGeometry.PolygonContains(region.Outer, p))
            return false;
        foreach (var h in region.Holes)
            if (PcbGeometry.PolygonContains(h, p))
                return false;
        return true;
    }

    private static double DistToLoop(IReadOnlyList<Vector2d> loop, in Vector2d p)
    {
        double best = double.PositiveInfinity;
        int n = loop.Count;
        for (int i = 0; i < n; i++)
            best = Math.Min(best, PointToSegment(p, loop[i], loop[(i + 1) % n]));
        return best;
    }

    private static double PointToSegment(in Vector2d p, in Vector2d a, in Vector2d b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 == 0)
            return p.DistanceTo(a);
        double t = Math.Clamp((p - a).Dot(ab) / len2, 0, 1);
        return p.DistanceTo(a + ab * t);
    }

    // ---- terminals -----------------------------------------------------------

    private Dictionary<string, List<Terminal>> BuildTerminals()
    {
        var viaSources = new HashSet<string>(_base.Vias.Select(v => v.Source), StringComparer.Ordinal);
        var result = new Dictionary<string, List<Terminal>>(StringComparer.Ordinal);
        var byNetSource = new Dictionary<(string Net, string Source), List<CopperFeature>>();

        foreach (var f in _base.Copper)
        {
            if (f.Net is null || viaSources.Contains(f.Source) || _base.TraceSources.Contains(f.Source))
                continue;
            var key = (f.Net, f.Source);
            if (!byNetSource.TryGetValue(key, out var list))
                byNetSource[key] = list = [];
            list.Add(f);
        }

        foreach (var (key, features) in byNetSource.OrderBy(kv => kv.Key.Source, StringComparer.Ordinal))
        {
            var centre = features[0].Region.Bounds.Center;
            var pt = new Vector2d(centre.X, centre.Y);
            var (si, sj) = Snap(pt);
            var cells = new List<int>();
            foreach (var f in features)
            {
                int layer = Array.IndexOf(_layerNames, f.Layer);
                if (layer >= 0)
                    cells.Add(Cell(si, sj, layer));
            }
            if (cells.Count == 0)
                continue;
            if (!result.TryGetValue(key.Net, out var terms))
                result[key.Net] = terms = [];
            terms.Add(new Terminal(pt, cells));
        }
        return result;
    }

    // ---- via resolution + layout rebuild -------------------------------------

    private PlacedVia ResolveVia(in Via via, string source)
    {
        var (type, layers) = ViaGeometry.Resolve(_board.Stackup, via);
        return new PlacedVia(via, source, type, layers);
    }

    private PcbLayout RebuildLayout(List<string> order, Dictionary<string, RouteGeom> committed)
    {
        var routed = new PcbLayout(_layout.Schematic, _board, _layout.BoardFrame);
        foreach (var p in _layout.Placements)
            routed.AddLoadedPlacement(p);
        foreach (var v in _layout.Vias)
            routed.AddLoadedVia(v);
        foreach (var t in _layout.Traces)
            routed.AddLoadedTrace(t);

        foreach (var net in order)
        {
            if (!committed.TryGetValue(net, out var geom))
                continue;
            foreach (var via in geom.Vias)
                routed.WithVia(via);
            foreach (var trace in geom.Traces)
                routed.AddTrace(trace);
        }
        return routed;
    }

    private sealed record Terminal(Vector2d Center, IReadOnlyList<int> Cells);

    private sealed class RouteGeom
    {
        public List<PcbTrace> Traces { get; } = [];
        public List<Via> Vias { get; } = [];
        public void Merge(RouteGeom other)
        {
            Traces.AddRange(other.Traces);
            Vias.AddRange(other.Vias);
        }
    }

    private sealed class Blockage(bool[][] hardTrace, int[][] softTrace, bool[] hardVia, int[] softVia, int nx)
    {
        public bool HardTrace(int i, int j, int layer) => hardTrace[layer][j * nx + i];
        public int Soft(int i, int j, int layer) => softTrace[layer][j * nx + i];
        public bool CleanTraceBlocked(int i, int j, int layer) =>
            hardTrace[layer][j * nx + i] || softTrace[layer][j * nx + i] != 0;
        public bool CleanViaBlocked(int i, int j) => hardVia[j * nx + i] || softVia[j * nx + i] != 0;
    }
}
