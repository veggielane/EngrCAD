using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Extract a region of a mesh, edit it as a mesh in its own right, put it back
/// (g3 <c>RegionOperator</c>). The workflow every local operator wants: decimate one face
/// group, subdivide a patch, smooth a dent, replace a boss with a different boss — without
/// the operator having to know anything about the rest of the model.
/// <code>
/// var region = MeshRegionOperator.Extract(mesh, selection);
/// var edited = LoopSubdivision.Subdivide(region.Region, 2);
/// var result = region.Reinsert(edited).Base;
/// </code>
/// <para>
/// <b>The contract is the seam.</b> Reinsertion is refused unless the replacement's
/// boundary is the region's boundary — same directed edges, at bit-identical positions.
/// That is not a convenience check: the seam is where the caller's edit meets geometry it
/// never saw, and this engine welds shared geometry by exact equality, never by tolerance
/// (see the epsilon ladder). A replacement whose rim drifted by 1e-12 would weld into a
/// crack rather than fail, which is the failure mode this project treats as the worst one.
/// The message says which edge broke it.
/// </para>
/// <para>
/// <b>The seam may not be refined either</b> — worth stating plainly, because subdividing
/// the region is the first thing anyone tries. Splitting a seam edge in two leaves the base
/// face on the other side holding the un-split edge, so the result is a T-junction: an open
/// shell, not a solid. Refining across a seam means refining the neighbours as well, which
/// is a different operation and not this one's job; it is refused rather than half-done.
/// Edits that DO satisfy the contract: anything confined to the interior (moving,
/// retriangulating, adding or removing interior geometry), and <see cref="MeshDecimator"/>,
/// whose boundary preservation is exactly this contract. <see cref="LoopSubdivision"/> is
/// not one of them — it both splits and smooths the open boundary.
/// </para>
/// <para>
/// <b>Transactionality comes free here</b>, which is why this is not built on
/// <see cref="MeshChangeSet"/> the way g3 builds it on an in-place editor.
/// <see cref="HalfEdgeMesh"/> is immutable after <c>Build</c>, so a refused or failed
/// reinsertion leaves the caller holding the original mesh by construction — there is no
/// half-applied state for a journal to undo. <see cref="Reinsert"/> returns a NEW session
/// over the NEW mesh, so repeated edits chain (g3's <c>CurrentBaseTriangles</c> tracking,
/// without the mutation).
/// </para>
/// </summary>
public sealed class MeshRegionOperator
{
    private readonly HashSet<int> _regionFaces;

    private MeshRegionOperator(
        HalfEdgeMesh baseMesh, MeshFaceSelection selection, HalfEdgeMesh region,
        IReadOnlyList<int> regionToBaseVertex)
    {
        Base = baseMesh;
        Selection = selection;
        Region = region;
        RegionToBaseVertex = regionToBaseVertex;
        _regionFaces = [.. selection.Indices];
    }

    /// <summary>The mesh the region was taken from. Never modified.</summary>
    public HalfEdgeMesh Base { get; }

    /// <summary>The faces of <see cref="Base"/> that make up the region.</summary>
    public MeshFaceSelection Selection { get; }

    /// <summary>
    /// The region as a standalone mesh — open along the seam unless the selection was the
    /// whole of <see cref="Base"/>. Edit this (or build a replacement for it) and hand the
    /// result to <see cref="Reinsert"/>.
    /// </summary>
    public HalfEdgeMesh Region { get; }

    /// <summary>Region vertex index → the vertex of <see cref="Base"/> it came from.</summary>
    public IReadOnlyList<int> RegionToBaseVertex { get; }

    /// <summary>
    /// The seam, as directed vertex pairs of <see cref="Base"/> with the region on the
    /// left. A replacement must reproduce exactly these, positionally.
    /// </summary>
    public IReadOnlyList<(int From, int To)> SeamEdges { get; private init; } = [];

    /// <summary>
    /// Extracts <paramref name="region"/> from <paramref name="mesh"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The selection does not extract to a manifold
    /// mesh (it pinches at a vertex or an edge).</exception>
    public static MeshRegionOperator Extract(HalfEdgeMesh mesh, MeshFaceSelection region)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(region);
        if (!ReferenceEquals(mesh, region.Mesh))
            throw new ArgumentException("The selection belongs to a different mesh.", nameof(region));

        var submesh = region.ToMesh(out var vertexMap);
        var seam = new List<(int, int)>();
        foreach (var he in region.BoundaryHalfEdges())
            seam.Add((he.Origin.Index, he.Destination.Index));
        return new MeshRegionOperator(mesh, region, submesh, vertexMap) { SeamEdges = seam };
    }

    /// <summary>Extracts the faces with the given indices.</summary>
    public static MeshRegionOperator Extract(HalfEdgeMesh mesh, IEnumerable<int> faceIndices) =>
        Extract(mesh, MeshFaceSelection.FromIndices(mesh, faceIndices));

    /// <summary>
    /// Replaces the region with <paramref name="replacement"/> and returns a session over
    /// the resulting mesh, its selection being the reinserted faces — so the next edit can
    /// start from <c>result.Region</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The replacement's boundary is not the region's boundary (the message names an edge
    /// that differs), or the reinserted mesh is not manifold.
    /// </exception>
    public MeshRegionOperator Reinsert(HalfEdgeMesh replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var seamVertex = SeamVertices();
        VerifyBoundaryMatches(replacement);

        var positions = new List<Vector3d>(Base.VertexCount);
        var baseSlot = new int[Base.VertexCount];
        Array.Fill(baseSlot, -1);
        var faces = new List<int[]>(Base.FaceCount - _regionFaces.Count + replacement.FaceCount);

        // Everything outside the region, verbatim. Base vertices are pulled in lazily, so
        // vertices that only the removed faces used never reach the result.
        for (int f = 0; f < Base.FaceCount; f++)
        {
            if (_regionFaces.Contains(f))
                continue;
            var loop = new List<int>(3);
            foreach (var vertex in Base.GetFace(f).Vertices())
                loop.Add(MapBase(vertex.Index));
            faces.Add([.. loop]);
        }

        // The replacement: seam vertices resolve onto the base's own vertices (which is
        // what welds the two halves), everything else is new.
        var replacementSlot = new int[replacement.VertexCount];
        for (int v = 0; v < replacement.VertexCount; v++)
        {
            var position = replacement.GetPosition(v);
            replacementSlot[v] = seamVertex.TryGetValue(position, out int baseVertex)
                ? MapBase(baseVertex)
                : Fresh(position);
        }
        int firstReinserted = faces.Count;
        foreach (var face in replacement.Faces)
        {
            var loop = new List<int>(3);
            foreach (var vertex in face.Vertices())
                loop.Add(replacementSlot[vertex.Index]);
            faces.Add([.. loop]);
        }

        HalfEdgeMesh result;
        try
        {
            result = HalfEdgeMesh.Build(positions, faces);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                "Reinserting the region produced a non-manifold mesh. The boundary matched, " +
                "so the replacement's interior is at fault — most often a face wound the " +
                "opposite way from the region it replaces: " + ex.Message, ex);
        }

        // Faces are built in the order they were handed to Build, so the reinserted region
        // is the tail — which makes the returned session's selection exact rather than a
        // re-derivation that could drift from what was actually inserted.
        var reinserted = MeshFaceSelection.FromIndices(
            result, Enumerable.Range(firstReinserted, replacement.FaceCount));
        return Extract(result, reinserted);

        int MapBase(int vertex)
        {
            if (baseSlot[vertex] < 0)
            {
                baseSlot[vertex] = positions.Count;
                positions.Add(Base.GetPosition(vertex));
            }
            return baseSlot[vertex];
        }

        int Fresh(in Vector3d position)
        {
            positions.Add(position);
            return positions.Count - 1;
        }
    }

    /// <summary>
    /// Seam position → base vertex. Keyed by position because a replacement is a mesh in
    /// its own right and shares no indices with the base; keyed EXACTLY because that is how
    /// this engine welds geometry that two sides are supposed to share.
    /// </summary>
    private Dictionary<Vector3d, int> SeamVertices()
    {
        var map = new Dictionary<Vector3d, int>();
        foreach (var (from, to) in SeamEdges)
        {
            foreach (int vertex in (ReadOnlySpan<int>)[from, to])
            {
                var position = Base.GetPosition(vertex);
                if (map.TryGetValue(position, out int existing) && existing != vertex)
                    throw new InvalidOperationException(
                        $"The region's boundary has two distinct vertices ({existing} and {vertex}) at " +
                        $"{position}, so a replacement cannot be welded onto it unambiguously.");
                map[position] = vertex;
            }
        }
        return map;
    }

    private void VerifyBoundaryMatches(HalfEdgeMesh replacement)
    {
        // Directed, not undirected: matching direction is what proves the replacement is
        // oriented the same way round as the region it replaces.
        var wanted = new HashSet<(Vector3d, Vector3d)>();
        foreach (var (from, to) in SeamEdges)
            wanted.Add((Base.GetPosition(from), Base.GetPosition(to)));

        foreach (var face in replacement.Faces)
        {
            foreach (var he in face.HalfEdges())
            {
                if (!he.Twin.IsBoundary)
                    continue;
                var edge = (he.Origin.Position, he.Destination.Position);
                if (!wanted.Remove(edge))
                    throw new ArgumentException(
                        $"The replacement has a boundary edge {edge.Item1} → {edge.Item2} that the " +
                        "region does not. Reinsertion needs the boundary preserved exactly — this " +
                        "engine welds shared geometry by coordinate equality, so a moved or " +
                        "retopologized rim would silently become a crack.",
                        nameof(replacement));
            }
        }

        if (wanted.Count > 0)
        {
            var (from, to) = wanted.First();
            throw new ArgumentException(
                $"The replacement is missing {wanted.Count} of the region's boundary edge(s), " +
                $"e.g. {from} → {to}. Reinsertion needs the boundary preserved exactly.",
                nameof(replacement));
        }
    }
}
