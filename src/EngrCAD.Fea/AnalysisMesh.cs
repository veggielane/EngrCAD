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
    }

    /// <summary>Linear or quadratic.</summary>
    public ElementOrder Order { get; }

    /// <summary>4 for linear elements, 10 for quadratic.</summary>
    public int NodesPerElement => Order == ElementOrder.Linear ? 4 : 10;

    /// <summary>3 for linear facets, 6 for quadratic.</summary>
    public int NodesPerFacet => Order == ElementOrder.Linear ? 3 : 6;

    /// <summary>Node positions.</summary>
    public IReadOnlyList<Vector3d> Nodes => _nodes;

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

    /// <summary>Distinct region ids present, ascending.</summary>
    public IReadOnlyList<int> Regions => _regions.Distinct().Order().ToArray();

    /// <summary>Distinct facet tags present, ascending — what a boundary condition can name.</summary>
    public IReadOnlyList<int> FacetTags => _facetTags.Distinct().Order().ToArray();

    /// <summary>Axis-aligned bounds of the nodes.</summary>
    public Aabb Bounds
    {
        get
        {
            var bounds = Aabb.Empty;
            foreach (var p in _nodes)
                bounds = bounds.Union(p);
            return bounds;
        }
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

    /// <summary>Total volume: the sum of the elements' volumes.</summary>
    public double Volume
    {
        get
        {
            double sum = 0;
            for (int e = 0; e < ElementCount; e++)
                sum += ElementVolume(e);
            return sum;
        }
    }

    /// <summary>Wraps a linear tet mesh (4-node elements).</summary>
    public static AnalysisMesh Of(TetMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

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
