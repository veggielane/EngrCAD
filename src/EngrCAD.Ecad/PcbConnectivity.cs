using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Ecad;

/// <summary>
/// The connectivity of ONE net — whether its pads are all electrically joined by copper, and if not,
/// which islands they fall into (what a ratsnest line would bridge). <see cref="IsConnected"/> is the
/// real answer the multilayer caveat left open: a net whose pads sit on different layers is CONNECTED
/// once a via ties them, not an unrouted ratsnest.
/// </summary>
/// <param name="Net">The net name.</param>
/// <param name="PadCount">The distinct component pads (pins) of the net that carry copper — the
/// terminals that must be joined. Via pads are connectors, not terminals, so they are not counted.</param>
/// <param name="ComponentCount">The number of connected components the pads fall into (1 = fully
/// connected; 0 when the net has no pads).</param>
/// <param name="PadIslands">The pad sources grouped by connected component (each an island a router
/// still has to bridge), in a deterministic order.</param>
public sealed record NetConnectivity(
    string Net, int PadCount, int ComponentCount, IReadOnlyList<IReadOnlyList<string>> PadIslands)
{
    /// <summary>True when every pad of the net lies in ONE connected component (or the net has fewer
    /// than two pads, which cannot be disconnected).</summary>
    public bool IsConnected => ComponentCount <= 1;
}

/// <summary>The connectivity of every net on a board — the per-net answer and the
/// <see cref="Unrouted"/> subset (the ratsnest).</summary>
/// <param name="Nets">One entry per net that carries copper, in name order.</param>
public sealed record ConnectivityReport(IReadOnlyList<NetConnectivity> Nets)
{
    /// <summary>The names of the nets whose pads are NOT all joined — the ratsnest — in name order.
    /// This is what <see cref="PcbDrc"/> reports as unrouted (information, not a fault).</summary>
    public IReadOnlyList<string> Unrouted =>
        Nets.Where(n => !n.IsConnected).Select(n => n.Net).ToList();

    /// <summary>The connectivity of one named net, or a trivially-connected empty entry when the net
    /// carries no copper.</summary>
    public NetConnectivity Of(string net)
    {
        foreach (var n in Nets)
            if (n.Net == net)
                return n;
        return new NetConnectivity(net, 0, 0, []);
    }
}

/// <summary>
/// The net-connectivity engine — the seam an autorouter reuses. It builds a per-net graph over the
/// net's copper FEATURES (component pads, via pads, and — later — traces): two features are joined
/// when they TOUCH on the same layer (an EXACT region intersection, no tolerance) OR are the two ends
/// of a plated barrel (a via, or a through-hole pad, whose per-layer copies share a source). A net is
/// CONNECTED when all its component pads lie in one connected component.
///
/// <para><b>This is what makes vias real.</b> A net with a pad on one layer and a pad on another,
/// plus a via spanning both whose annular pads touch each, is CONNECTED — the caveat the multilayer
/// stage left open ("a net whose pads sit on different layers reads as an unrouted ratsnest") is now
/// answered by geometry. A via on the WRONG net does not connect it (only same-net features are
/// nodes), and a via elsewhere does not connect distant pads unless its copper reaches them.</para>
///
/// <para><b>The connection test IS the DRC's touch test.</b> Two features join when
/// <see cref="CurvedRegion2dBoolean.Intersection"/> is non-empty — the same exact query the DRC calls
/// a SHORT between different nets. Between same-net copper it is the intended connection, which is why
/// the netlist is the single source both read.</para>
/// </summary>
public static class PcbConnectivity
{
    /// <summary>Analyses the connectivity of a placed layout (derives the copper model first).</summary>
    public static ConnectivityReport Analyze(PcbLayout layout) =>
        Analyze(PcbCopperModel.FromLayout(layout));

    /// <summary>Analyses the connectivity of every net that carries copper.</summary>
    public static ConnectivityReport Analyze(PcbCopperModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var connectorSources = ConnectorSources(model);

        // Group the net-tagged copper by net (a null net is its own island — not part of connectivity).
        var byNet = new Dictionary<string, List<CopperFeature>>(StringComparer.Ordinal);
        foreach (var feature in model.Copper)
        {
            if (feature.Net is null)
                continue;
            if (!byNet.TryGetValue(feature.Net, out var list))
                byNet[feature.Net] = list = [];
            list.Add(feature);
        }

        var nets = new List<NetConnectivity>();
        foreach (var (name, features) in byNet.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            nets.Add(Compute(name, features, connectorSources));
        return new ConnectivityReport(nets);
    }

    /// <summary>The connectivity of one named net over a copper model.</summary>
    public static NetConnectivity For(PcbCopperModel model, string net)
    {
        ArgumentNullException.ThrowIfNull(model);
        var connectorSources = ConnectorSources(model);
        var features = model.Copper.Where(f => f.Net == net).ToList();
        return Compute(net, features, connectorSources);
    }

    /// <summary>The feature sources that are CONNECTORS rather than terminals — via pads, traces and
    /// pour components. A connector ties copper together but is not itself a pin to be joined, so it
    /// never counts as an island of its own (a floating via, a stray trace or a pour piece never makes
    /// a net read unconnected — but a pour that JOINS a net's pads does connect it, since two features
    /// still join by touch whether or not either is a terminal).</summary>
    private static HashSet<string> ConnectorSources(PcbCopperModel model)
    {
        var connectors = new HashSet<string>(model.Vias.Select(v => v.Source), StringComparer.Ordinal);
        foreach (var trace in model.TraceSources)
            connectors.Add(trace);
        foreach (var pour in model.PourSources)
            connectors.Add(pour);
        return connectors;
    }

    // ---- the per-net graph ---------------------------------------------------

    private static NetConnectivity Compute(
        string net, List<CopperFeature> features, HashSet<string> connectorSources)
    {
        int n = features.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++)
            parent[i] = i;

        // (1) Plated barrels: features sharing a source are one plated connector across layers — a via
        //     (its annular pads on every touched layer) or a through-hole pad (its per-layer copies).
        var firstOfSource = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            if (firstOfSource.TryGetValue(features[i].Source, out int first))
                Union(parent, first, i);
            else
                firstOfSource[features[i].Source] = i;
        }

        // (2) Same-layer touch: two features on one layer with DIFFERENT sources join when their copper
        //     regions intersect exactly. Grouped by layer, with an AABB broad phase (disjoint boxes
        //     bound disjoint regions, so a box gap proves no touch).
        var byLayer = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            if (!byLayer.TryGetValue(features[i].Layer, out var list))
                byLayer[features[i].Layer] = list = [];
            list.Add(i);
        }
        foreach (var indices in byLayer.Values)
        {
            for (int a = 0; a < indices.Count; a++)
                for (int b = a + 1; b < indices.Count; b++)
                {
                    int ia = indices[a], ib = indices[b];
                    if (Find(parent, ia) == Find(parent, ib))
                        continue;   // already one component (same source, or joined transitively)
                    var fa = features[ia];
                    var fb = features[ib];
                    if (!BoxesTouch(fa.Region.Bounds, fb.Region.Bounds))
                        continue;
                    if (CurvedRegion2dBoolean.Intersection([fa.Region], [fb.Region]).Count > 0)
                        Union(parent, ia, ib);
                }
        }

        // Endpoints are the COMPONENT PADS — the terminals to join. A via pad or a trace is a
        // CONNECTOR, not a terminal, so a floating/redundant via and a routed trace never make a net
        // read as unconnected (a net is connected when its component PADS end up in one component).
        var padRootOfSource = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            string src = features[i].Source;
            if (connectorSources.Contains(src))
                continue;
            padRootOfSource[src] = Find(parent, i);   // same source ⇒ same root (barrel union)
        }

        // Group the pad sources by their component root — the islands.
        var islands = new Dictionary<int, List<string>>();
        foreach (var (src, root) in padRootOfSource)
        {
            if (!islands.TryGetValue(root, out var list))
                islands[root] = list = [];
            list.Add(src);
        }
        var padIslands = islands.Values
            .Select(list => { list.Sort(StringComparer.Ordinal); return (IReadOnlyList<string>)list; })
            .OrderBy(list => list[0], StringComparer.Ordinal)
            .ToList();

        return new NetConnectivity(net, padRootOfSource.Count, padIslands.Count, padIslands);
    }

    // ---- union-find and geometry helpers -------------------------------------

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
            i = parent[i] = parent[parent[i]];
        return i;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra != rb)
            parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
    }

    /// <summary>Whether two 2D AABBs are not separated in the plane (they overlap or touch). Disjoint
    /// boxes bound disjoint regions, so a false here proves no touch without the exact boolean.</summary>
    private static bool BoxesTouch(in Aabb a, in Aabb b) =>
        a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y;
}
