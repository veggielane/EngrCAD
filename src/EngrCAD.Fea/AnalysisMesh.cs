using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>Element order of an <see cref="AnalysisMesh"/>.</summary>
public enum ElementOrder
{
    /// <summary>4-node (linear) tetrahedra, 3-node boundary facets.</summary>
    Linear = 1,

    /// <summary>10-node (quadratic) tetrahedra, 6-node boundary facets.</summary>
    Quadratic = 2,
}

/// <summary>
/// A tetrahedral mesh as an ANALYSIS sees it: nodes, elements of a fixed node count,
/// per-element region ids, and tagged boundary facets — with the linear/quadratic
/// difference reduced to two integers (<see cref="NodesPerElement"/> and
/// <see cref="NodesPerFacet"/>) and one <see cref="Order"/> flag.
///
/// <para><b>Why the extra type.</b> Assembly, boundary conditions, load integration,
/// stress recovery and result publishing are identical for 4-node and 10-node elements
/// apart from the shape functions; writing them twice would be two chances to get the
/// same thing wrong. <see cref="TetMesh"/> and <see cref="QuadraticTetMesh"/> stay
/// exactly as they are — this wraps them, it does not replace them, and it copies
/// nothing but index arrays.</para>
///
/// <para><b>Node indices are preserved.</b> A linear analysis mesh's node i is the
/// <see cref="TetMesh"/>'s vertex i; a quadratic one's leading
/// <see cref="QuadraticTetMesh.CornerNodeCount"/> nodes are likewise the linear vertices,
/// so a field defined on the geometry transfers with no mapping.</para>
/// </summary>
public sealed class AnalysisMesh
{
    private readonly Vector3d[] _nodes;
    private readonly int[] _elements;   // NodesPerElement indices per element
    private readonly int[] _regions;
    private readonly int[] _facets;     // NodesPerFacet indices per facet
    private readonly int[] _facetTags;
    private readonly int[] _facetElement;
    private readonly IReadOnlyList<Vector3d> _nodesView;

    // Lazy caches, published lock-free by CompareExchange: each is a pure deterministic
    // function of the immutable arrays above, so a racing first read computes an identical
    // value and the losing copy is discarded — the same first-publish-wins pattern the
    // sampled SDF grids use. The two scalar results are BOXED because an object reference
    // publishes atomically where a Nullable<double>/Nullable<Aabb> write can tear.
    private IReadOnlyList<int>? _regionsView;
    private IReadOnlyList<int>? _facetTagsView;
    private object? _bounds;
    private object? _volume;
    private object? _regionSlots;

    private AnalysisMesh(
        ElementOrder order,
        Vector3d[] nodes,
        int[] elements,
        int[] regions,
        int[] facets,
        int[] facetTags,
        int[] facetElement)
    {
        Order = order;
        _nodes = nodes;
        _elements = elements;
        _regions = regions;
        _facets = facets;
        _facetTags = facetTags;
        _facetElement = facetElement;
        _nodesView = Array.AsReadOnly(nodes);
    }

    /// <summary>Linear or quadratic.</summary>
    public ElementOrder Order { get; }

    /// <summary>4 for linear elements, 10 for quadratic.</summary>
    public int NodesPerElement => Order == ElementOrder.Linear ? 4 : 10;

    /// <summary>3 for linear facets, 6 for quadratic.</summary>
    public int NodesPerFacet => Order == ElementOrder.Linear ? 3 : 6;

    /// <summary>Node positions. A read-only VIEW, not the internal array: the arrays behind
    /// this type are what every solver assembles from, so handing one out castable back to
    /// <c>Vector3d[]</c> would let any consumer silently move the mesh under a solve.</summary>
    public IReadOnlyList<Vector3d> Nodes => _nodesView;

    /// <summary>Number of nodes (the analysis has 3 displacement degrees of freedom each).</summary>
    public int NodeCount => _nodes.Length;

    /// <summary>Number of elements.</summary>
    public int ElementCount => _regions.Length;

    /// <summary>Number of boundary facets.</summary>
    public int FacetCount => _facetTags.Length;

    /// <summary>Position of node <paramref name="node"/>.</summary>
    public Vector3d Position(int node) => _nodes[node];

    /// <summary>The node indices of one element (length <see cref="NodesPerElement"/>).</summary>
    public ReadOnlySpan<int> Element(int element) =>
        _elements.AsSpan(element * NodesPerElement, NodesPerElement);

    /// <summary>The material region id of one element.</summary>
    public int RegionOf(int element) => _regions[element];

    /// <summary>The node indices of one boundary facet (length <see cref="NodesPerFacet"/>),
    /// wound counter-clockwise seen from OUTSIDE the solid.</summary>
    public ReadOnlySpan<int> Facet(int facet) =>
        _facets.AsSpan(facet * NodesPerFacet, NodesPerFacet);

    /// <summary>The tag of one boundary facet — <see cref="TetFacet.SourceTriangle"/>,
    /// which is the caller's <c>TetMeshOptions.FacetTags</c> entry when one was supplied
    /// and the raw input-triangle index otherwise. The handle boundary conditions grab.</summary>
    public int FacetTag(int facet) => _facetTags[facet];

    /// <summary>The element carrying one boundary facet.</summary>
    public int FacetElement(int facet) => _facetElement[facet];

    /// <summary>Distinct region ids present, ascending. Computed once and cached — the
    /// backing arrays never change — so <c>Regions.Contains(x)</c> in a loop costs a scan of
    /// the distinct ids, not a fresh sort of every element's region per call.</summary>
    public IReadOnlyList<int> Regions =>
        Publish(ref _regionsView, static self => Array.AsReadOnly(
            self._regions.Distinct().Order().ToArray()), this);

    /// <summary>Distinct facet tags present, ascending — what a boundary condition can name.
    /// Cached like <see cref="Regions"/>.</summary>
    public IReadOnlyList<int> FacetTags =>
        Publish(ref _facetTagsView, static self => Array.AsReadOnly(
            self._facetTags.Distinct().Order().ToArray()), this);

    // ---- material regions per node ------------------------------------------------------

    /// <summary>
    /// Which material regions each node touches, as a CSR table over the nodes: the distinct
    /// region ids of the elements meeting at a node, ascending. A "slot" is one (node, region)
    /// pair, and it is the index a per-region nodal quantity is stored under.
    /// </summary>
    /// <param name="Starts">Slot range per node (length <c>NodeCount + 1</c>).</param>
    /// <param name="Regions">Region id per slot.</param>
    /// <param name="InterfaceNodes">Nodes touching more than one region.</param>
    /// <param name="SlotsAreNodes">True when every node touches exactly one region, so a slot
    /// index IS a node index. The single-region case, and the reason a region-aware pass over
    /// slots is bit-for-bit the pass over nodes it replaced.</param>
    private sealed record RegionSlotTable(
        int[] Starts, int[] Regions, int InterfaceNodes, bool SlotsAreNodes);

    private RegionSlotTable Slots =>
        (RegionSlotTable)Publish(ref _regionSlots, static self => (object)BuildRegionSlots(self), this);

    /// <summary>
    /// The distinct material regions the elements at one node belong to, ascending.
    ///
    /// <para>More than one means the node sits on a material INTERFACE, where the stress
    /// tensor is genuinely discontinuous: only the traction across the interface is
    /// continuous, and the components parallel to it jump with the modulus. A field indexed
    /// by node holds one value there and therefore cannot hold the truth — see
    /// <see cref="StructuralResults.NodalStressIn"/> for the per-region value and
    /// <see cref="InterfaceNodeCount"/> for how many nodes are affected.</para>
    /// </summary>
    public ReadOnlySpan<int> RegionsAt(int node)
    {
        var table = Slots;
        return table.Regions.AsSpan(table.Starts[node], table.Starts[node + 1] - table.Starts[node]);
    }

    /// <summary>
    /// How many nodes touch more than one material region — the size of the material
    /// interface, and <b>zero for every mesh with one region and for disjoint bodies</b>
    /// (which share no node by construction).
    /// </summary>
    public int InterfaceNodeCount => Slots.InterfaceNodes;

    /// <summary>Total (node, region) slots — the length of a per-region nodal array.</summary>
    internal int RegionSlotCount => Slots.Regions.Length;

    /// <summary>True when a slot index is a node index (see
    /// <see cref="RegionSlotTable.SlotsAreNodes"/>).</summary>
    internal bool RegionSlotsAreNodes => Slots.SlotsAreNodes;

    /// <summary>First slot of one node, and one past its last.</summary>
    internal (int Start, int End) RegionSlotRange(int node)
    {
        var table = Slots;
        return (table.Starts[node], table.Starts[node + 1]);
    }

    /// <summary>The region id of one slot.</summary>
    internal int RegionOfSlot(int slot) => Slots.Regions[slot];

    /// <summary>
    /// The slot holding one node's values for one region. Refused BY NAME when the node
    /// touches no element of that region, because the alternative — returning some other
    /// region's value, or a sentinel — is a wrong number where the question has no answer.
    /// </summary>
    internal int RegionSlot(int node, int region)
    {
        var table = Slots;
        int start = table.Starts[node], end = table.Starts[node + 1];
        for (int k = start; k < end; k++)
        {
            if (table.Regions[k] == region)
                return k;
        }
        throw new ArgumentException(
            $"Node {node} touches no element of region {region} (it touches "
            + (end > start
                ? $"region(s) {string.Join(", ", table.Regions[start..end])})."
                : "no element at all)."),
            nameof(region));
    }

    private static RegionSlotTable BuildRegionSlots(AnalysisMesh mesh)
    {
        int nodes = mesh.NodeCount;

        // The overwhelmingly common case, and worth its own path: one region means one slot
        // per node, PROVIDED every node is in an element. A node in none would otherwise be
        // handed a region it does not touch, which is exactly the wrong number RegionSlot
        // exists to refuse.
        if (mesh.Regions.Count == 1)
        {
            var touched = new bool[nodes];
            for (int e = 0; e < mesh.ElementCount; e++)
            {
                foreach (int node in mesh.Element(e))
                    touched[node] = true;
            }
            if (Array.TrueForAll(touched, static t => t))
            {
                int only = mesh.Regions[0];
                var identity = new int[nodes + 1];
                var one = new int[nodes];
                for (int v = 0; v < nodes; v++)
                {
                    identity[v] = v;
                    one[v] = only;
                }
                identity[nodes] = nodes;
                return new RegionSlotTable(identity, one, 0, true);
            }
        }

        // Region id per element-node incidence, in a CSR over nodes; then each node's slice is
        // sorted and deduplicated. No hashing and no per-node allocation - the same counting
        // shape the recovery's node-to-element adjacency uses.
        var incidence = new int[nodes + 1];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            foreach (int node in mesh.Element(e))
                incidence[node + 1]++;
        }
        for (int v = 0; v < nodes; v++)
            incidence[v + 1] += incidence[v];

        var cursor = (int[])incidence.Clone();
        var flat = new int[incidence[nodes]];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            int region = mesh.RegionOf(e);
            foreach (int node in mesh.Element(e))
                flat[cursor[node]++] = region;
        }

        var starts = new int[nodes + 1];
        for (int v = 0; v < nodes; v++)
        {
            var slice = flat.AsSpan(incidence[v], incidence[v + 1] - incidence[v]);
            slice.Sort();
            int distinct = 0;
            for (int k = 0; k < slice.Length; k++)
            {
                if (k == 0 || slice[k] != slice[k - 1])
                    distinct++;
            }
            starts[v + 1] = starts[v] + distinct;
        }

        var regions = new int[starts[nodes]];
        int interfaceNodes = 0;
        bool slotsAreNodes = starts[nodes] == nodes;
        for (int v = 0; v < nodes; v++)
        {
            var slice = flat.AsSpan(incidence[v], incidence[v + 1] - incidence[v]);
            int at = starts[v];
            for (int k = 0; k < slice.Length; k++)
            {
                if (k == 0 || slice[k] != slice[k - 1])
                    regions[at++] = slice[k];
            }
            if (starts[v + 1] - starts[v] > 1)
                interfaceNodes++;
            if (starts[v] != v)
                slotsAreNodes = false;
        }
        return new RegionSlotTable(starts, regions, interfaceNodes, slotsAreNodes);
    }

    /// <summary>Axis-aligned bounds of the nodes. Cached like <see cref="Regions"/>.</summary>
    public Aabb Bounds
    {
        get
        {
            var boxed = Publish(ref _bounds, static self =>
            {
                var bounds = Aabb.Empty;
                foreach (var p in self._nodes)
                    bounds = bounds.Union(p);
                return (object)bounds;
            }, this);
            return (Aabb)boxed;
        }
    }

    /// <summary>
    /// First-publish-wins lazy initialization: compute on a miss, install by
    /// <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>, and return whichever copy
    /// won. Sound because every compute here is a pure function of immutable arrays, so a
    /// racing loser's value is identical to the winner's.
    /// </summary>
    private static T Publish<T>(ref T? field, Func<AnalysisMesh, T> compute, AnalysisMesh self)
        where T : class
    {
        var current = field;
        if (current is not null)
            return current;
        var computed = compute(self);
        return Interlocked.CompareExchange(ref field, computed, null) ?? computed;
    }

    /// <summary>
    /// The signed volume of one element, from its CORNER nodes. Positive by the
    /// <see cref="TetMesh"/> orientation invariant, which the quadratic layer inherits.
    /// </summary>
    public double ElementVolume(int element)
    {
        var e = Element(element);
        return TetMesh.SignedVolume(_nodes[e[0]], _nodes[e[1]], _nodes[e[2]], _nodes[e[3]]);
    }

    /// <summary>Outward area vector of one boundary facet (its magnitude is the area),
    /// from the facet's CORNER nodes — straight-sided, so a quadratic facet's mid-edge
    /// nodes lie in the same plane and add nothing.</summary>
    public Vector3d FacetArea(int facet)
    {
        var f = Facet(facet);
        var a = _nodes[f[0]];
        return (_nodes[f[1]] - a).Cross(_nodes[f[2]] - a) * 0.5;
    }

    /// <summary>Centroid of one boundary facet's corner triangle.</summary>
    public Vector3d FacetCentroid(int facet)
    {
        var f = Facet(facet);
        return (_nodes[f[0]] + _nodes[f[1]] + _nodes[f[2]]) / 3.0;
    }

    /// <summary>Total volume: the sum of the elements' volumes. Cached like
    /// <see cref="Regions"/>.</summary>
    public double Volume
    {
        get
        {
            var boxed = Publish(ref _volume, static self =>
            {
                double sum = 0;
                for (int e = 0; e < self.ElementCount; e++)
                    sum += self.ElementVolume(e);
                return (object)sum;
            }, this);
            return (double)boxed;
        }
    }

    /// <summary>
    /// Refuses a mesh whose index arithmetic would overflow <see cref="int"/>, up front and
    /// by name. Without it the failure is a bare <see cref="OverflowException"/> from a
    /// negative array length deep inside the copy — loud, but naming nothing. The binding
    /// limits are <c>elements·nodesPerElement</c> for the element table and <c>3·nodes</c>
    /// for the degree-of-freedom numbering every solver builds on top.
    /// <para>Internal rather than private so the tests can exercise the refusal with plain
    /// counts — a 215-million-element mesh is not a fixture anyone can build.</para>
    /// </summary>
    internal static void RequireAddressable(
        int elementCount, int facetCount, int nodeCount, ElementOrder order)
    {
        int perElement = order == ElementOrder.Linear ? 4 : 10;
        int perFacet = order == ElementOrder.Linear ? 3 : 6;
        if (elementCount > int.MaxValue / perElement)
            throw new FeaException(
                $"The mesh has {elementCount:N0} elements, past the "
                + $"{int.MaxValue / perElement:N0} an AnalysisMesh of {order} order can index "
                + $"(the element table holds {perElement} node indices per element in one "
                + "int-addressed array).");
        if (facetCount > int.MaxValue / perFacet)
            throw new FeaException(
                $"The mesh has {facetCount:N0} boundary facets, past the "
                + $"{int.MaxValue / perFacet:N0} an AnalysisMesh of {order} order can index "
                + $"(the facet table holds {perFacet} node indices per facet in one "
                + "int-addressed array).");
        if (nodeCount > int.MaxValue / 3)
            throw new FeaException(
                $"The mesh has {nodeCount:N0} nodes, past the {int.MaxValue / 3:N0} the "
                + "solvers can address (three displacement degrees of freedom per node are "
                + "numbered in one int-addressed array).");
    }

    /// <summary>Wraps a linear tet mesh (4-node elements).</summary>
    public static AnalysisMesh Of(TetMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        RequireAddressable(
            mesh.TetCount, mesh.BoundaryFacetCount, mesh.VertexCount, ElementOrder.Linear);

        var nodes = new Vector3d[mesh.VertexCount];
        for (int v = 0; v < nodes.Length; v++)
            nodes[v] = mesh.Position(v);

        var elements = new int[mesh.TetCount * 4];
        var regions = new int[mesh.TetCount];
        for (int t = 0; t < mesh.TetCount; t++)
        {
            var e = mesh.GetTet(t);
            elements[t * 4] = e.A;
            elements[t * 4 + 1] = e.B;
            elements[t * 4 + 2] = e.C;
            elements[t * 4 + 3] = e.D;
            regions[t] = mesh.RegionOf(t);
        }

        int fc = mesh.BoundaryFacetCount;
        var facets = new int[fc * 3];
        var tags = new int[fc];
        var owners = new int[fc];
        for (int i = 0; i < fc; i++)
        {
            var f = mesh.BoundaryFacets[i];
            facets[i * 3] = f.V0;
            facets[i * 3 + 1] = f.V1;
            facets[i * 3 + 2] = f.V2;
            tags[i] = f.SourceTriangle;
            owners[i] = f.Tet;
        }

        return new AnalysisMesh(ElementOrder.Linear, nodes, elements, regions, facets, tags, owners);
    }

    /// <summary>Wraps a 10-node quadratic tet mesh.</summary>
    public static AnalysisMesh Of(QuadraticTetMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        RequireAddressable(
            mesh.TetCount, mesh.BoundaryFacets.Count, mesh.NodeCount, ElementOrder.Quadratic);

        var nodes = new Vector3d[mesh.NodeCount];
        for (int v = 0; v < nodes.Length; v++)
            nodes[v] = mesh.Position(v);

        var elements = new int[mesh.TetCount * 10];
        var regions = new int[mesh.TetCount];
        for (int t = 0; t < mesh.TetCount; t++)
        {
            var e = mesh.Tets[t];
            for (int i = 0; i < 10; i++)
                elements[t * 10 + i] = e[i];
            regions[t] = mesh.RegionOf(t);
        }

        int fc = mesh.BoundaryFacets.Count;
        var facets = new int[fc * 6];
        var tags = new int[fc];
        var owners = new int[fc];
        for (int i = 0; i < fc; i++)
        {
            var f = mesh.BoundaryFacets[i];
            facets[i * 6] = f.V0;
            facets[i * 6 + 1] = f.V1;
            facets[i * 6 + 2] = f.V2;
            facets[i * 6 + 3] = f.M01;
            facets[i * 6 + 4] = f.M12;
            facets[i * 6 + 5] = f.M20;
            tags[i] = f.SourceTriangle;
            owners[i] = f.Tet;
        }

        return new AnalysisMesh(ElementOrder.Quadratic, nodes, elements, regions, facets, tags, owners);
    }

    /// <summary>The quadratic analysis mesh of a linear one — <c>Of(QuadraticTetMesh.From(mesh))</c>.</summary>
    public static AnalysisMesh Quadratic(TetMesh mesh) => Of(QuadraticTetMesh.From(mesh));

    /// <summary>
    /// Connected components of the node graph, by element connectivity: component id per
    /// node, and the count. Nodes touched by no element get their own singleton component
    /// (they carry zero stiffness, which is what the solver's restraint check reports).
    /// <para>Used to check restraint <b>per body</b>: a fully fixed part beside a floating
    /// one is a singular system whose rigid motion is not in the span of the whole model's
    /// six rigid modes, so a global check cannot see it.</para>
    /// </summary>
    public (int[] Component, int Count) ConnectedComponents()
    {
        var parent = new int[NodeCount];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }

        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb)
                parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }

        int perElement = NodesPerElement;
        for (int e = 0; e < ElementCount; e++)
        {
            var nodes = Element(e);
            for (int i = 1; i < perElement; i++)
                Union(nodes[0], nodes[i]);
        }

        var component = new int[NodeCount];
        var label = new Dictionary<int, int>();
        for (int v = 0; v < NodeCount; v++)
        {
            int root = Find(v);
            if (!label.TryGetValue(root, out int id))
                label[root] = id = label.Count;
            component[v] = id;
        }
        return (component, label.Count);
    }
}
