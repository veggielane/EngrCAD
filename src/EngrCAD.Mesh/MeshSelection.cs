using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Read-only face-index selection over a <see cref="HalfEdgeMesh"/> — v1 of the
/// selection/region model (g3 <c>MeshFaceSelection</c>). Selections are immutable:
/// <see cref="Grow"/>/<see cref="Contract"/> return new selections, matching the engine's
/// immutable-after-<c>Build</c> meshes. <see cref="MeshRegionOperator"/> builds the
/// extract-modify-reinsert workflow (g3 <c>RegionOperator</c>) on top.
/// </summary>
public sealed class MeshFaceSelection
{
    private readonly HashSet<int> _set;

    public HalfEdgeMesh Mesh { get; }

    private MeshFaceSelection(HalfEdgeMesh mesh, HashSet<int> set)
    {
        Mesh = mesh;
        _set = set;
    }

    /// <summary>Selection of the given face indices (validated against the mesh).</summary>
    public static MeshFaceSelection FromIndices(HalfEdgeMesh mesh, IEnumerable<int> faceIndices)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(faceIndices);
        var set = new HashSet<int>();
        foreach (int f in faceIndices)
        {
            if (f < 0 || f >= mesh.FaceCount)
                throw new ArgumentOutOfRangeException(nameof(faceIndices),
                    $"Face index {f} is out of range (mesh has {mesh.FaceCount} faces).");
            set.Add(f);
        }
        return new MeshFaceSelection(mesh, set);
    }

    /// <summary>Selection of every face of the mesh.</summary>
    public static MeshFaceSelection All(HalfEdgeMesh mesh) =>
        new(mesh, [.. Enumerable.Range(0, mesh.FaceCount)]);

    public int Count => _set.Count;
    public bool Contains(int faceIndex) => _set.Contains(faceIndex);
    public IReadOnlyCollection<int> Indices => _set;
    public IEnumerable<Face> Faces => _set.OrderBy(f => f).Select(f => Mesh.GetFace(f));

    /// <summary>Sum of selected face areas.</summary>
    public double Area() => _set.Sum(f => Mesh.GetFace(f).Area);

    /// <summary>
    /// New selection expanded by <paramref name="rings"/> one-ring steps: each step adds
    /// every face sharing a <b>vertex</b> with the selection (g3
    /// <c>ExpandToOneRingNeighbours</c>).
    /// </summary>
    public MeshFaceSelection Grow(int rings = 1)
    {
        var set = new HashSet<int>(_set);
        for (int step = 0; step < rings; step++)
        {
            var frontier = new List<int>();
            foreach (int f in set)
            {
                foreach (var v in Mesh.GetFace(f).Vertices())
                {
                    foreach (var nf in v.IncidentFaces())
                    {
                        if (!set.Contains(nf.Index))
                            frontier.Add(nf.Index);
                    }
                }
            }
            if (frontier.Count == 0)
                break;
            foreach (int f in frontier)
                set.Add(f);
        }
        return new MeshFaceSelection(Mesh, set);
    }

    /// <summary>
    /// New selection contracted by <paramref name="rings"/> one-ring steps: each step
    /// removes every selected face touching a border vertex — a vertex that also belongs
    /// to an unselected face (g3 <c>ContractBorderByOneRingNeighbours</c>). Inverse-ish of
    /// <see cref="Grow"/>; vertices on the open mesh boundary do not count as border by
    /// themselves.
    /// </summary>
    public MeshFaceSelection Contract(int rings = 1)
    {
        var set = new HashSet<int>(_set);
        for (int step = 0; step < rings; step++)
        {
            var borderVertices = new HashSet<int>();
            foreach (int f in set)
            {
                foreach (var v in Mesh.GetFace(f).Vertices())
                {
                    if (v.IncidentFaces().Any(nf => !set.Contains(nf.Index)))
                        borderVertices.Add(v.Index);
                }
            }
            if (borderVertices.Count == 0)
                break;
            set.RemoveWhere(f => Mesh.GetFace(f).Vertices().Any(v => borderVertices.Contains(v.Index)));
        }
        return new MeshFaceSelection(Mesh, set);
    }

    /// <summary>
    /// The selection's boundary: half-edges of selected faces whose twin lies in an
    /// unselected face or on the mesh boundary, in ascending index order.
    /// </summary>
    public List<HalfEdge> BoundaryHalfEdges()
    {
        var result = new List<HalfEdge>();
        foreach (int f in _set.OrderBy(f => f))
        {
            foreach (var he in Mesh.GetFace(f).HalfEdges())
            {
                var twin = he.Twin;
                if (twin.IsBoundary || !_set.Contains(twin.Face.Index))
                    result.Add(he);
            }
        }
        return result;
    }

    /// <summary>
    /// Boundary half-edges chained into closed loops (selected face on the left). The
    /// successor of a boundary half-edge is found by rotating around its destination
    /// through selected faces, which stays correct even at pinch vertices where the
    /// selection touches itself.
    /// </summary>
    public List<List<HalfEdge>> BoundaryLoops()
    {
        var boundary = BoundaryHalfEdges();
        var pending = new HashSet<int>(boundary.Select(he => he.Index));
        var loops = new List<List<HalfEdge>>();
        foreach (var start in boundary)
        {
            if (!pending.Contains(start.Index))
                continue;
            var loop = new List<HalfEdge>();
            var he = start;
            do
            {
                loop.Add(he);
                pending.Remove(he.Index);
                // Rotate around the destination vertex through selected faces until the
                // next boundary half-edge of the selection is reached.
                var candidate = he.Next;
                while (!candidate.Twin.IsBoundary && _set.Contains(candidate.Twin.Face.Index))
                    candidate = candidate.Twin.Next;
                he = candidate;
            } while (he.Index != start.Index);
            loops.Add(loop);
        }
        return loops;
    }

    /// <summary>All vertices of the selected faces.</summary>
    public MeshVertexSelection ToVertices() =>
        MeshVertexSelection.FromIndices(Mesh,
            _set.SelectMany(f => Mesh.GetFace(f).Vertices().Select(v => v.Index)));

    /// <summary>All undirected edges of the selected faces.</summary>
    public MeshEdgeSelection ToEdges() =>
        MeshEdgeSelection.FromHalfEdgeIndices(Mesh,
            _set.SelectMany(f => Mesh.GetFace(f).HalfEdges().Select(he => he.Index)));

    /// <summary>
    /// Extracts the selected faces as their own mesh (vertices remapped in ascending
    /// original-index order, faces in ascending face-index order). The subset must be
    /// manifold on its own: a selection touching itself only at a vertex (pinch) fails the
    /// bow-tie check in <see cref="HalfEdgeMesh.Build"/> and throws
    /// <see cref="ArgumentException"/>.
    /// </summary>
    public HalfEdgeMesh ToMesh() => ToMesh(out _);

    /// <summary>
    /// <see cref="ToMesh()"/>, additionally reporting where each extracted vertex came
    /// from: <paramref name="vertexMap"/><c>[sub]</c> is the index of that vertex in
    /// <see cref="Mesh"/>. <see cref="MeshRegionOperator"/> needs the map to put an edited
    /// region back.
    /// </summary>
    public HalfEdgeMesh ToMesh(out IReadOnlyList<int> vertexMap)
    {
        if (_set.Count == 0)
            throw new InvalidOperationException("Cannot extract an empty selection.");

        var usedVertices = new SortedSet<int>();
        var faceLoops = new List<int[]>();
        foreach (int f in _set.OrderBy(f => f))
        {
            var loop = Mesh.GetFace(f).Vertices().Select(v => v.Index).ToArray();
            faceLoops.Add(loop);
            foreach (int v in loop)
                usedVertices.Add(v);
        }

        var remap = new Dictionary<int, int>(usedVertices.Count);
        var positions = new List<Vector3d>(usedVertices.Count);
        var toBase = new List<int>(usedVertices.Count);
        foreach (int v in usedVertices)
        {
            remap.Add(v, positions.Count);
            positions.Add(Mesh.GetPosition(v));
            toBase.Add(v);
        }
        vertexMap = toBase;

        var faces = new List<int[]>(faceLoops.Count);
        foreach (var loop in faceLoops)
            faces.Add([.. loop.Select(v => remap[v])]);

        try
        {
            return HalfEdgeMesh.Build(positions, faces);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                "The face selection does not extract to a manifold mesh (it touches itself " +
                "at a pinch vertex or edge): " + ex.Message, ex);
        }
    }
}

/// <summary>
/// Read-only vertex-index selection (g3 <c>MeshVertexSelection</c>); immutable —
/// <see cref="Grow"/>/<see cref="Contract"/> return new selections.
/// </summary>
public sealed class MeshVertexSelection
{
    private readonly HashSet<int> _set;

    public HalfEdgeMesh Mesh { get; }

    private MeshVertexSelection(HalfEdgeMesh mesh, HashSet<int> set)
    {
        Mesh = mesh;
        _set = set;
    }

    public static MeshVertexSelection FromIndices(HalfEdgeMesh mesh, IEnumerable<int> vertexIndices)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(vertexIndices);
        var set = new HashSet<int>();
        foreach (int v in vertexIndices)
        {
            if (v < 0 || v >= mesh.VertexCount)
                throw new ArgumentOutOfRangeException(nameof(vertexIndices),
                    $"Vertex index {v} is out of range (mesh has {mesh.VertexCount} vertices).");
            set.Add(v);
        }
        return new MeshVertexSelection(mesh, set);
    }

    public int Count => _set.Count;
    public bool Contains(int vertexIndex) => _set.Contains(vertexIndex);
    public IReadOnlyCollection<int> Indices => _set;
    public IEnumerable<Vertex> Vertices => _set.OrderBy(v => v).Select(v => Mesh.GetVertex(v));

    /// <summary>New selection with each step adding every one-ring neighbor vertex.</summary>
    public MeshVertexSelection Grow(int rings = 1)
    {
        var set = new HashSet<int>(_set);
        for (int step = 0; step < rings; step++)
        {
            var frontier = new List<int>();
            foreach (int v in set)
            {
                foreach (var n in Mesh.GetVertex(v).Neighbors())
                {
                    if (!set.Contains(n.Index))
                        frontier.Add(n.Index);
                }
            }
            if (frontier.Count == 0)
                break;
            foreach (int v in frontier)
                set.Add(v);
        }
        return new MeshVertexSelection(Mesh, set);
    }

    /// <summary>New selection with each step removing every vertex that has an unselected
    /// one-ring neighbor (the interior of the selection).</summary>
    public MeshVertexSelection Contract(int rings = 1)
    {
        var set = new HashSet<int>(_set);
        for (int step = 0; step < rings; step++)
        {
            var border = set
                .Where(v => Mesh.GetVertex(v).Neighbors().Any(n => !set.Contains(n.Index)))
                .ToList();
            if (border.Count == 0)
                break;
            foreach (int v in border)
                set.Remove(v);
        }
        return new MeshVertexSelection(Mesh, set);
    }

    /// <summary>
    /// Faces whose vertices are covered by the selection: all of them when
    /// <paramref name="requireAll"/> (default), else any of them.
    /// </summary>
    public MeshFaceSelection ToFaces(bool requireAll = true)
    {
        var faces = new HashSet<int>();
        foreach (int v in _set)
        {
            foreach (var f in Mesh.GetVertex(v).IncidentFaces())
            {
                if (!requireAll || f.Vertices().All(fv => _set.Contains(fv.Index)))
                    faces.Add(f.Index);
            }
        }
        return MeshFaceSelection.FromIndices(Mesh, faces);
    }
}

/// <summary>
/// Read-only undirected-edge selection (g3 <c>MeshEdgeSelection</c>). Edges are stored as
/// canonical half-edge indices — the lower index of each twin pair, matching
/// <see cref="HalfEdgeMesh.Edges"/>. Immutable; <see cref="Grow"/>/<see cref="Contract"/>
/// return new selections.
/// </summary>
public sealed class MeshEdgeSelection
{
    private readonly HashSet<int> _set; // canonical half-edge indices

    public HalfEdgeMesh Mesh { get; }

    private MeshEdgeSelection(HalfEdgeMesh mesh, HashSet<int> set)
    {
        Mesh = mesh;
        _set = set;
    }

    /// <summary>Selection from half-edge indices; each is canonicalized to min(he, twin).</summary>
    public static MeshEdgeSelection FromHalfEdgeIndices(HalfEdgeMesh mesh, IEnumerable<int> halfEdgeIndices)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(halfEdgeIndices);
        var set = new HashSet<int>();
        foreach (int he in halfEdgeIndices)
        {
            if (he < 0 || he >= mesh.HalfEdgeCount)
                throw new ArgumentOutOfRangeException(nameof(halfEdgeIndices),
                    $"Half-edge index {he} is out of range (mesh has {mesh.HalfEdgeCount} half-edges).");
            set.Add(Canonical(mesh, he));
        }
        return new MeshEdgeSelection(mesh, set);
    }

    private static int Canonical(HalfEdgeMesh mesh, int he) => Math.Min(he, mesh.HeTwin(he));

    public int Count => _set.Count;

    /// <summary>True when the undirected edge of the given half-edge is selected.</summary>
    public bool Contains(int halfEdgeIndex) => _set.Contains(Canonical(Mesh, halfEdgeIndex));

    /// <summary>Canonical half-edge indices (the lower of each twin pair).</summary>
    public IReadOnlyCollection<int> Indices => _set;

    public IEnumerable<HalfEdge> Edges => _set.OrderBy(he => he).Select(he => Mesh.GetHalfEdge(he));

    /// <summary>New selection with each step adding every edge sharing a vertex with a selected edge.</summary>
    public MeshEdgeSelection Grow(int rings = 1)
    {
        var set = new HashSet<int>(_set);
        for (int step = 0; step < rings; step++)
        {
            var frontier = new List<int>();
            foreach (int heIndex in set)
            {
                var he = Mesh.GetHalfEdge(heIndex);
                foreach (var v in (ReadOnlySpan<Vertex>)[he.Origin, he.Destination])
                {
                    foreach (var outgoing in v.OutgoingHalfEdges())
                    {
                        int canonical = Canonical(Mesh, outgoing.Index);
                        if (!set.Contains(canonical))
                            frontier.Add(canonical);
                    }
                }
            }
            if (frontier.Count == 0)
                break;
            foreach (int he in frontier)
                set.Add(he);
        }
        return new MeshEdgeSelection(Mesh, set);
    }

    /// <summary>New selection with each step removing every edge that shares a vertex with
    /// an unselected edge (the interior of the selection).</summary>
    public MeshEdgeSelection Contract(int rings = 1)
    {
        var set = new HashSet<int>(_set);
        for (int step = 0; step < rings; step++)
        {
            var border = new List<int>();
            foreach (int heIndex in set)
            {
                var he = Mesh.GetHalfEdge(heIndex);
                bool isBorder = false;
                foreach (var v in (ReadOnlySpan<Vertex>)[he.Origin, he.Destination])
                {
                    foreach (var outgoing in v.OutgoingHalfEdges())
                    {
                        if (!set.Contains(Canonical(Mesh, outgoing.Index)))
                        {
                            isBorder = true;
                            break;
                        }
                    }
                    if (isBorder)
                        break;
                }
                if (isBorder)
                    border.Add(heIndex);
            }
            if (border.Count == 0)
                break;
            foreach (int he in border)
                set.Remove(he);
        }
        return new MeshEdgeSelection(Mesh, set);
    }

    /// <summary>Endpoint vertices of the selected edges.</summary>
    public MeshVertexSelection ToVertices() =>
        MeshVertexSelection.FromIndices(Mesh, _set.SelectMany(he =>
        {
            var h = Mesh.GetHalfEdge(he);
            return new[] { h.Origin.Index, h.Destination.Index };
        }));
}
