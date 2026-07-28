using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>
/// One tetrahedron's four vertex indices, in the mesh's positive-orientation convention
/// (<c>Predicates3d.SignedVolume6(A, B, C, D) &gt; 0</c>).
/// </summary>
/// <param name="A">First vertex index.</param>
/// <param name="B">Second vertex index.</param>
/// <param name="C">Third vertex index.</param>
/// <param name="D">Fourth vertex index.</param>
public readonly record struct Tet(int A, int B, int C, int D)
{
    /// <summary>The i-th vertex index (0..3).</summary>
    public int this[int i] => i switch
    {
        0 => A,
        1 => B,
        2 => C,
        3 => D,
        _ => throw new ArgumentOutOfRangeException(nameof(i))
    };
}

/// <summary>
/// One triangular facet on the tet mesh's boundary: its three vertex indices wound
/// counter-clockwise seen from OUTSIDE the solid (matching <see cref="EngrCAD.Mesh.HalfEdgeMesh"/>'s
/// convention), the tetrahedron it belongs to, and the index of the input surface triangle
/// it came from.
/// </summary>
/// <param name="V0">First vertex index.</param>
/// <param name="V1">Second vertex index.</param>
/// <param name="V2">Third vertex index.</param>
/// <param name="Tet">Index of the (unique) tetrahedron carrying this facet.</param>
/// <param name="SourceTriangle">
/// Index of the input <see cref="EngrCAD.Mesh.HalfEdgeMesh"/> face this facet lies on, or -1 when
/// the mesh was built without a surface (a bare point-set tetrahedralization). Boundary
/// recovery may SUBDIVIDE an input triangle, so several facets can share one source; the
/// map is many-to-one and never many-to-many.
/// </param>
public readonly record struct TetFacet(int V0, int V1, int V2, int Tet, int SourceTriangle);

/// <summary>
/// A tetrahedral volume mesh: structure-of-arrays vertices, four vertex indices per
/// tetrahedron, a material region id per tetrahedron, and tagged boundary facets.
///
/// <para><b>Orientation is an invariant, not a convention to remember</b>: every tetrahedron
/// satisfies <c>Predicates3d.SignedVolume6(a, b, c, d) &gt; 0</c>, checked exactly at
/// construction. <see cref="Volume"/> is therefore a sum of positive terms and equals the
/// enclosed volume with no cancellation.</para>
///
/// <para><b>Surface fidelity contract.</b> When built from a closed
/// <see cref="EngrCAD.Mesh.HalfEdgeMesh"/>, the boundary of this mesh is the SAME SURFACE as the
/// input: every boundary vertex either is an input vertex or lies exactly in the plane of
/// (and inside) the input triangle it was inserted on, so the enclosed volume matches the
/// input mesh's exactly up to round-off. What refinement may change is the FACETING: a
/// boundary facet is a piece of an input triangle, not necessarily the whole of it, and
/// <see cref="TetFacet.SourceTriangle"/> names which one. Tags therefore survive refinement
/// — that is the whole reason the field exists.</para>
///
/// <para>The structure is immutable after construction; algorithms produce new meshes.</para>
/// </summary>
public sealed class TetMesh
{
    private readonly Vector3d[] _positions;
    private readonly int[] _tets;          // 4 vertex indices per tetrahedron
    private readonly int[] _regions;       // one material id per tetrahedron
    private readonly TetFacet[] _boundary;

    internal TetMesh(Vector3d[] positions, int[] tets, int[] regions, TetFacet[] boundary)
    {
        if (tets.Length % 4 != 0)
            throw new ArgumentException("Tetrahedron index array length must be a multiple of 4.", nameof(tets));
        if (regions.Length * 4 != tets.Length)
            throw new ArgumentException("Region array must carry one entry per tetrahedron.", nameof(regions));

        _positions = positions;
        _tets = tets;
        _regions = regions;
        _boundary = boundary;

        for (int t = 0; t < regions.Length; t++)
        {
            var (a, b, c, d) = (tets[4 * t], tets[4 * t + 1], tets[4 * t + 2], tets[4 * t + 3]);
            if (a == b || a == c || a == d || b == c || b == d || c == d)
                throw new ArgumentException($"Tetrahedron {t} repeats a vertex ({a}, {b}, {c}, {d}).");
            if (Predicates3d.SignedVolume6Sign(positions[a], positions[b], positions[c], positions[d]) <= 0)
                throw new ArgumentException(
                    $"Tetrahedron {t} ({a}, {b}, {c}, {d}) is degenerate or negatively oriented; " +
                    "TetMesh stores every tetrahedron with a strictly positive signed volume.");
        }
    }

    /// <summary>Vertex positions.</summary>
    public IReadOnlyList<Vector3d> Vertices => _positions;

    /// <summary>Number of vertices.</summary>
    public int VertexCount => _positions.Length;

    /// <summary>Number of tetrahedra.</summary>
    public int TetCount => _regions.Length;

    /// <summary>Number of boundary facets.</summary>
    public int BoundaryFacetCount => _boundary.Length;

    /// <summary>The boundary facets, each tagged with the input triangle it lies on.</summary>
    public IReadOnlyList<TetFacet> BoundaryFacets => _boundary;

    /// <summary>The four vertex indices of tetrahedron <paramref name="index"/>.</summary>
    public Tet GetTet(int index)
    {
        if ((uint)index >= (uint)TetCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return new Tet(_tets[4 * index], _tets[4 * index + 1], _tets[4 * index + 2], _tets[4 * index + 3]);
    }

    /// <summary>All tetrahedra, in storage order.</summary>
    public IEnumerable<Tet> Tets
    {
        get
        {
            for (int t = 0; t < TetCount; t++)
                yield return GetTet(t);
        }
    }

    /// <summary>The material region id of tetrahedron <paramref name="index"/> (0 by default).</summary>
    public int RegionOf(int index) => _regions[index];

    /// <summary>Distinct material region ids present, ascending.</summary>
    public IReadOnlyList<int> Regions => _regions.Distinct().Order().ToArray();

    /// <summary>Position of vertex <paramref name="index"/>.</summary>
    public Vector3d Position(int index) => _positions[index];

    /// <summary>The signed volume of one tetrahedron (always positive, by the class invariant).</summary>
    public double TetVolume(int index)
    {
        var t = GetTet(index);
        return SignedVolume(_positions[t.A], _positions[t.B], _positions[t.C], _positions[t.D]);
    }

    /// <summary>
    /// Total enclosed volume: the sum of the tetrahedra's volumes. Because every term is
    /// positive there is no cancellation, which is what makes the volume identity against
    /// the input surface mesh a meaningful check rather than a coincidence.
    /// </summary>
    public double Volume
    {
        get
        {
            double sum = 0;
            for (int t = 0; t < TetCount; t++)
                sum += TetVolume(t);
            return sum;
        }
    }

    /// <summary>Axis-aligned bounds of the vertices.</summary>
    public Aabb Bounds
    {
        get
        {
            var bounds = Aabb.Empty;
            foreach (var p in _positions)
                bounds = bounds.Union(p);
            return bounds;
        }
    }

    /// <summary>
    /// The boundary as a surface mesh — the tet mesh's own view of the solid's skin, useful
    /// for rendering and for checking the fidelity contract. Facet winding is outward.
    /// Vertices are re-indexed compactly; <paramref name="sourceVertices"/> maps each
    /// surface vertex back to its <see cref="TetMesh"/> index.
    /// </summary>
    public EngrCAD.Mesh.HalfEdgeMesh BoundaryMesh(out int[] sourceVertices)
    {
        var map = new Dictionary<int, int>();
        var positions = new List<Vector3d>();
        var order = new List<int>();

        int Map(int v)
        {
            if (map.TryGetValue(v, out int mapped))
                return mapped;
            mapped = positions.Count;
            map[v] = mapped;
            positions.Add(_positions[v]);
            order.Add(v);
            return mapped;
        }

        var faces = new List<int[]>(_boundary.Length);
        foreach (var f in _boundary)
            faces.Add([Map(f.V0), Map(f.V1), Map(f.V2)]);

        sourceVertices = [.. order];
        return EngrCAD.Mesh.HalfEdgeMesh.Build(positions, faces);
    }

    /// <summary>Vertex indices of a tetrahedron's four faces, wound OUTWARD for the mesh's
    /// positive-orientation convention. Face i is the face opposite vertex i.</summary>
    private static readonly int[][] FaceTable =
    [
        [1, 2, 3],
        [0, 3, 2],
        [0, 1, 3],
        [0, 2, 1],
    ];

    /// <summary>
    /// The outer surface of a SUBSET of the elements — the cutaway view. Every face used by
    /// exactly one selected element is emitted in that element's outward winding, so the
    /// result is a closed manifold whenever the selection is (and the interior elements a
    /// section plane exposes become visible geometry rather than an abstraction).
    /// </summary>
    /// <param name="includeTet">Predicate on element index.</param>
    /// <returns>A surface mesh of the selected elements' boundary.</returns>
    public EngrCAD.Mesh.HalfEdgeMesh SurfaceOf(Func<int, bool> includeTet)
    {
        ArgumentNullException.ThrowIfNull(includeTet);

        var selected = new bool[TetCount];
        for (int t = 0; t < TetCount; t++)
            selected[t] = includeTet(t);

        // Count how many SELECTED elements use each face; a face used once is on the cut.
        var usage = new Dictionary<(int, int, int), int>();
        for (int t = 0; t < TetCount; t++)
        {
            if (!selected[t])
                continue;
            var tet = GetTet(t);
            for (int f = 0; f < 4; f++)
            {
                var key = SortedKey(tet[FaceTable[f][0]], tet[FaceTable[f][1]], tet[FaceTable[f][2]]);
                usage[key] = usage.GetValueOrDefault(key) + 1;
            }
        }

        var map = new Dictionary<int, int>();
        var positions = new List<Vector3d>();
        var faces = new List<int[]>();

        int Map(int v)
        {
            if (map.TryGetValue(v, out int mapped))
                return mapped;
            mapped = positions.Count;
            map[v] = mapped;
            positions.Add(_positions[v]);
            return mapped;
        }

        for (int t = 0; t < TetCount; t++)
        {
            if (!selected[t])
                continue;
            var tet = GetTet(t);
            for (int f = 0; f < 4; f++)
            {
                int a = tet[FaceTable[f][0]], b = tet[FaceTable[f][1]], c = tet[FaceTable[f][2]];
                if (usage[SortedKey(a, b, c)] != 1)
                    continue;
                faces.Add([Map(a), Map(b), Map(c)]);
            }
        }

        return EngrCAD.Mesh.HalfEdgeMesh.Build(positions, faces);
    }

    private static (int, int, int) SortedKey(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    /// <summary>Six times the signed volume of a tetrahedron, as a plain double.</summary>
    internal static double SignedVolume(in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d d)
    {
        return (b - a).Cross(c - a).Dot(d - a) / 6.0;
    }
}
