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
/// <b>The seam may be REFINED, and refining it refines the neighbours too.</b> Splitting a
/// seam edge in two leaves the base face on the other side holding the un-split edge, which
/// would be a T-junction — an open shell, not a solid — so the reinsertion carries the split
/// across: every base face using a refined seam edge gains the replacement's new vertices at
/// their exact positions (a base triangle is re-fanned from its opposite corner, exactly as
/// an edge split would leave it; a base polygon simply grows). The seam's original vertices
/// must still be there, at bit-identical positions: what a replacement may do is subdivide
/// the rim, never move it or retopologize it. So <see cref="LoopSubdivision"/> round-trips
/// <b>if it is told to preserve the boundary</b> — its default Warren rules smooth the open
/// boundary, which moves the rim, and that is still refused (and must be: a moved rim welds
/// into an invisible crack rather than failing).
/// </para>
/// <para>
/// Edits that satisfy the contract: anything confined to the interior (moving,
/// retriangulating, adding or removing interior geometry), <see cref="MeshDecimator"/>
/// (whose boundary preservation is exactly this contract), and now any refinement of the
/// rim — <see cref="Remesher"/> with the seam pinned, or Loop subdivision with
/// <c>preserveBoundary: true</c>.
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
        var refinements = MatchSeam(replacement);

        var positions = new List<Vector3d>(Base.VertexCount);
        var baseSlot = new int[Base.VertexCount];
        Array.Fill(baseSlot, -1);
        var faces = new List<int[]>(Base.FaceCount - _regionFaces.Count + replacement.FaceCount);
        // Refinement points, keyed by position so the base side and the replacement side
        // resolve to the SAME output vertex. Two independently created vertices at one
        // position would be a crack, which is the failure this whole class exists to avoid.
        var refinementSlot = new Dictionary<Vector3d, int>();

        // Everything outside the region, verbatim — except where a face uses a seam edge the
        // replacement subdivided, which gains the new vertices so no T-junction appears.
        for (int f = 0; f < Base.FaceCount; f++)
        {
            if (_regionFaces.Contains(f))
                continue;
            EmitBaseFace(Base.GetFace(f));
        }

        // The replacement: seam vertices resolve onto the base's own vertices (which is
        // what welds the two halves), refinement points onto the ones the base side just
        // created, everything else is new.
        var replacementSlot = new int[replacement.VertexCount];
        for (int v = 0; v < replacement.VertexCount; v++)
        {
            var position = replacement.GetPosition(v);
            replacementSlot[v] = seamVertex.TryGetValue(position, out int baseVertex)
                ? MapBase(baseVertex)
                : refinementSlot.TryGetValue(position, out int shared)
                    ? shared
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

        int RefinementVertex(in Vector3d position)
        {
            if (!refinementSlot.TryGetValue(position, out int slot))
            {
                slot = Fresh(position);
                refinementSlot[position] = slot;
            }
            return slot;
        }

        // The base side of a refined seam. A face outside the region traverses each seam edge
        // in the OPPOSITE direction to the region, so the insertion for its directed edge
        // (u → v) is the chain of the region's seam edge (v → u), reversed.
        void EmitBaseFace(Face face)
        {
            var corners = new List<int>(face.Degree);
            foreach (var vertex in face.Vertices())
                corners.Add(vertex.Index);

            int degree = corners.Count;
            var loop = new List<int>(degree);
            var cornerSlot = new int[degree];   // where each original corner landed in loop
            var refinedEdge = new bool[degree]; // edge i runs corner i → corner i+1
            bool anyRefined = false;
            for (int i = 0; i < degree; i++)
            {
                int u = corners[i], v = corners[(i + 1) % degree];
                cornerSlot[i] = loop.Count;
                loop.Add(MapBase(u));
                if (!refinements.TryGetValue((v, u), out var chain) || chain.Count == 0)
                    continue;
                refinedEdge[i] = true;
                anyRefined = true;
                // The region's chain runs v → u; this face runs u → v.
                for (int k = chain.Count - 1; k >= 0; k--)
                    loop.Add(RefinementVertex(chain[k]));
            }

            if (!anyRefined || degree != 3)
            {
                // Unrefined, or a polygon face — this engine keeps polygon faces as they are,
                // and an n-gon that grew a few boundary vertices is still one flat face.
                faces.Add([.. loop]);
                return;
            }

            // A triangle is re-fanned from the corner between two UNREFINED edges wherever one
            // exists, which reproduces exactly what an edge split would have left behind (the
            // new vertex joined to the opposite apex). With two or three refined edges no such
            // corner exists and any original corner does; the fan stays inside the triangle
            // either way, because every inserted vertex lies on an edge of it.
            int apex = cornerSlot[0];
            for (int i = 0; i < 3; i++)
            {
                if (!refinedEdge[(i + 2) % 3] && !refinedEdge[i])
                {
                    apex = cornerSlot[i];
                    break;
                }
            }

            int count = loop.Count;
            for (int k = 1; k <= count - 2; k++)
                faces.Add([loop[apex], loop[(apex + k) % count], loop[(apex + k + 1) % count]]);
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

    /// <summary>
    /// Matches the replacement's boundary against the region's seam, returning the points the
    /// replacement inserted into each seam edge (an empty list where it left the edge alone).
    /// <para>
    /// The match is directed — direction is what proves the replacement is oriented the same
    /// way round as the region it replaces — and positional, because a replacement is a mesh
    /// in its own right and shares no indices with the base.
    /// </para>
    /// <para>
    /// The order of the two checks is load-bearing. <b>Every original seam vertex must be
    /// present first</b>, before any chain is walked: without that, a replacement that MOVED a
    /// rim vertex is indistinguishable from one that removed it and inserted a new one nearby,
    /// and would be silently accepted as a refinement — welding a crack, the exact failure the
    /// seam contract exists to prevent. Only then is each seam edge's chain walked, and an
    /// intermediate point that is itself a seam vertex is refused (the replacement rewired the
    /// rim rather than subdividing it).
    /// </para>
    /// </summary>
    private Dictionary<(int From, int To), List<Vector3d>> MatchSeam(HalfEdgeMesh replacement)
    {
        var successor = new Dictionary<Vector3d, Vector3d>();
        foreach (var face in replacement.Faces)
        {
            foreach (var he in face.HalfEdges())
            {
                if (!he.Twin.IsBoundary)
                    continue;
                if (!successor.TryAdd(he.Origin.Position, he.Destination.Position))
                    throw new ArgumentException(
                        $"The replacement has two boundary edges leaving {he.Origin.Position}, so its " +
                        "rim cannot be matched to the region's unambiguously.",
                        nameof(replacement));
            }
        }

        var seamPositions = new HashSet<Vector3d>();
        foreach (var (from, to) in SeamEdges)
        {
            seamPositions.Add(Base.GetPosition(from));
            seamPositions.Add(Base.GetPosition(to));
        }
        foreach (var position in seamPositions)
        {
            if (!successor.ContainsKey(position))
                throw new ArgumentException(
                    $"The replacement has no boundary vertex at {position}, which is on the region's " +
                    "boundary. Reinsertion needs the rim's own vertices preserved exactly — it may be " +
                    "subdivided, but not moved or retopologized: this engine welds shared geometry by " +
                    "coordinate equality, so a rim that drifted would silently become a crack.",
                    nameof(replacement));
        }

        var chains = new Dictionary<(int From, int To), List<Vector3d>>();
        int consumed = 0;
        foreach (var (from, to) in SeamEdges)
        {
            var start = Base.GetPosition(from);
            var end = Base.GetPosition(to);
            var interior = new List<Vector3d>();
            var current = start;
            while (true)
            {
                var next = successor[current];
                consumed++;
                if (next == end)
                    break;
                if (seamPositions.Contains(next) || interior.Count > successor.Count)
                    throw new ArgumentException(
                        $"The replacement's boundary leaves {start} and reaches {next} before {end}, so " +
                        "it is not a subdivision of the region's boundary edge there. Reinsertion " +
                        "accepts a rim that was refined, not one that was rewired.",
                        nameof(replacement));
                interior.Add(next);
                current = next;
            }
            chains[(from, to)] = interior;
        }

        if (consumed != successor.Count)
            throw new ArgumentException(
                $"The replacement has {successor.Count - consumed} boundary edge(s) that are not part of " +
                "the region's boundary — it opened a hole, or covers less of the region than it replaces. " +
                "Reinsertion needs the boundary preserved exactly.",
                nameof(replacement));

        return chains;
    }
}
