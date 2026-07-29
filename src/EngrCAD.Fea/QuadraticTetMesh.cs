using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>
/// One 10-node quadratic tetrahedron. Nodes 0-3 are the corners in the linear element's
/// order; nodes 4-9 are the mid-edge nodes in the standard Abaqus C3D10 / VTK
/// <c>VTK_QUADRATIC_TETRA</c> order: 4 = mid(0,1), 5 = mid(1,2), 6 = mid(0,2),
/// 7 = mid(0,3), 8 = mid(1,3), 9 = mid(2,3).
/// </summary>
/// <param name="N0">Corner node 0.</param>
/// <param name="N1">Corner node 1.</param>
/// <param name="N2">Corner node 2.</param>
/// <param name="N3">Corner node 3.</param>
/// <param name="N4">Mid-edge node between corners 0 and 1.</param>
/// <param name="N5">Mid-edge node between corners 1 and 2.</param>
/// <param name="N6">Mid-edge node between corners 0 and 2.</param>
/// <param name="N7">Mid-edge node between corners 0 and 3.</param>
/// <param name="N8">Mid-edge node between corners 1 and 3.</param>
/// <param name="N9">Mid-edge node between corners 2 and 3.</param>
public readonly record struct QuadraticTet(
    int N0, int N1, int N2, int N3, int N4, int N5, int N6, int N7, int N8, int N9)
{
    /// <summary>The i-th node index (0..9).</summary>
    public int this[int i] => i switch
    {
        0 => N0, 1 => N1, 2 => N2, 3 => N3, 4 => N4,
        5 => N5, 6 => N6, 7 => N7, 8 => N8, 9 => N9,
        _ => throw new ArgumentOutOfRangeException(nameof(i))
    };
}

/// <summary>
/// One 6-node quadratic boundary facet: corners <paramref name="V0"/>..<paramref name="V2"/>
/// then the mid-edge nodes of (0,1), (1,2), (2,0) — the VTK <c>VTK_QUADRATIC_TRIANGLE</c>
/// order. Carries the same source-triangle tag as the linear facet it came from.
/// </summary>
/// <param name="V0">Corner node 0.</param>
/// <param name="V1">Corner node 1.</param>
/// <param name="V2">Corner node 2.</param>
/// <param name="M01">Mid-edge node between corners 0 and 1.</param>
/// <param name="M12">Mid-edge node between corners 1 and 2.</param>
/// <param name="M20">Mid-edge node between corners 2 and 0.</param>
/// <param name="Tet">Index of the tetrahedron carrying this facet.</param>
/// <param name="SourceTriangle">Tag inherited from the linear facet.</param>
public readonly record struct QuadraticTetFacet(
    int V0, int V1, int V2, int M01, int M12, int M20, int Tet, int SourceTriangle);

/// <summary>
/// The 10-node (quadratic) companion of a <see cref="TetMesh"/>, for second-order FEA.
///
/// <para><b>It is a pure function of the linear mesh</b>, deliberately: nothing here
/// re-meshes, moves a corner, or consults the original surface. Mid-edge nodes are the exact
/// midpoints of the linear element's edges, so the element geometry is the straight-sided
/// (sub-parametric) one every linear-elasticity formulation assumes, and the corner nodes'
/// indices are unchanged — corner-indexed data from the linear mesh stays valid.</para>
///
/// <para><b>Mid-edge nodes are shared, keyed on the canonical (min, max) corner pair</b>, so
/// two elements meeting on an edge get the SAME node and the assembled system is continuous.
/// That deduplication is the whole content of the class, and it is also why a curved
/// (iso-parametric) variant cannot simply be layered on later without deciding what a shared
/// node means where two boundary patches meet at an angle.</para>
///
/// <para>The node count is <c>V + E</c> for a mesh with V vertices and E distinct edges,
/// which for a typical tet mesh is about 7-8x the corner count — worth knowing before
/// assembling a system from one.</para>
/// </summary>
public sealed class QuadraticTetMesh
{
    private readonly Vector3d[] _nodes;
    private readonly QuadraticTet[] _tets;
    private readonly int[] _regions;
    private readonly QuadraticTetFacet[] _boundary;

    private QuadraticTetMesh(
        Vector3d[] nodes, QuadraticTet[] tets, int[] regions, QuadraticTetFacet[] boundary, int cornerCount)
    {
        _nodes = nodes;
        _tets = tets;
        _regions = regions;
        _boundary = boundary;
        CornerNodeCount = cornerCount;
    }

    /// <summary>All node positions: the linear mesh's vertices first, then the mid-edge nodes.</summary>
    public IReadOnlyList<Vector3d> Nodes => _nodes;

    /// <summary>Total node count (corners + mid-edge).</summary>
    public int NodeCount => _nodes.Length;

    /// <summary>
    /// How many of the leading nodes are corners. Node indices below this are exactly the
    /// linear mesh's vertex indices, so a field defined on the linear mesh transfers with no
    /// mapping at all.
    /// </summary>
    public int CornerNodeCount { get; }

    /// <summary>Number of elements (unchanged from the linear mesh).</summary>
    public int TetCount => _tets.Length;

    /// <summary>The elements.</summary>
    public IReadOnlyList<QuadraticTet> Tets => _tets;

    /// <summary>Quadratic boundary facets, tagged as in the linear mesh.</summary>
    public IReadOnlyList<QuadraticTetFacet> BoundaryFacets => _boundary;

    /// <summary>The material region id of element <paramref name="index"/>.</summary>
    public int RegionOf(int index) => _regions[index];

    /// <summary>Position of node <paramref name="index"/>.</summary>
    public Vector3d Position(int index) => _nodes[index];

    /// <summary>Builds the 10-node mesh from a linear one.</summary>
    public static QuadraticTetMesh From(TetMesh linear)
    {
        ArgumentNullException.ThrowIfNull(linear);

        var nodes = new List<Vector3d>(linear.VertexCount * 8);
        for (int v = 0; v < linear.VertexCount; v++)
            nodes.Add(linear.Position(v));
        int cornerCount = nodes.Count;

        var midEdge = new Dictionary<(int, int), int>(linear.TetCount * 6);

        int Mid(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (midEdge.TryGetValue(key, out int existing))
                return existing;
            int index = nodes.Count;
            // The midpoint of an edge is computed from the CANONICAL (min, max) pair, so two
            // elements sharing the edge get a bit-identical position as well as a shared
            // index. (a + b) * 0.5 is symmetric in IEEE-754 anyway, but deriving it from the
            // canonical pair is the same discipline the exact mesh boolean's seam welding
            // needs, and it costs nothing to keep.
            nodes.Add((linear.Position(key.Item1) + linear.Position(key.Item2)) * 0.5);
            midEdge[key] = index;
            return index;
        }

        var tets = new QuadraticTet[linear.TetCount];
        var regions = new int[linear.TetCount];
        for (int t = 0; t < linear.TetCount; t++)
        {
            var e = linear.GetTet(t);
            tets[t] = new QuadraticTet(
                e.A, e.B, e.C, e.D,
                Mid(e.A, e.B),   // 4
                Mid(e.B, e.C),   // 5
                Mid(e.A, e.C),   // 6
                Mid(e.A, e.D),   // 7
                Mid(e.B, e.D),   // 8
                Mid(e.C, e.D));  // 9
            regions[t] = linear.RegionOf(t);
        }

        var boundary = new QuadraticTetFacet[linear.BoundaryFacetCount];
        for (int i = 0; i < boundary.Length; i++)
        {
            var f = linear.BoundaryFacets[i];
            boundary[i] = new QuadraticTetFacet(
                f.V0, f.V1, f.V2,
                Mid(f.V0, f.V1), Mid(f.V1, f.V2), Mid(f.V2, f.V0),
                f.Tet, f.SourceTriangle);
        }

        return new QuadraticTetMesh([.. nodes], tets, regions, boundary, cornerCount);
    }

    /// <summary>
    /// The linear mesh's volume, recomputed from the quadratic elements' CORNERS. Because
    /// mid-edge nodes are exact midpoints the elements are straight-sided, so this must equal
    /// the linear mesh's volume exactly — which is what makes it a useful identity check
    /// rather than a second opinion.
    /// </summary>
    public double Volume
    {
        get
        {
            double sum = 0;
            foreach (var t in _tets)
                sum += TetMesh.SignedVolume(_nodes[t.N0], _nodes[t.N1], _nodes[t.N2], _nodes[t.N3]);
            return sum;
        }
    }
}
