using System.Collections.Immutable;
using System.Runtime.InteropServices;
using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Outcome of an <see cref="EditableMesh"/> operator. Guarded operators never corrupt
/// the mesh: every guard runs before the first mutation, so any value other than
/// <see cref="Ok"/> means the mesh is untouched.
/// </summary>
public enum MeshOperationResult
{
    /// <summary>The operation was applied.</summary>
    Ok,
    /// <summary>The half-edge index is not live.</summary>
    NotAHalfEdge,
    /// <summary>The face index is not live.</summary>
    NotAFace,
    /// <summary>An adjacent face is not a triangle (split/flip/collapse are triangle operators).</summary>
    NotATriangle,
    /// <summary>The edge lies on the boundary (flip requires an interior edge).</summary>
    BoundaryEdge,
    /// <summary>The half-edge is not a boundary half-edge (merge requires the boundary sides).</summary>
    NotABoundaryEdge,
    /// <summary>Both arguments name the same edge.</summary>
    SameEdge,
    /// <summary>The edge the operation would create already exists.</summary>
    EdgeAlreadyExists,
    /// <summary>The operation would pinch the surface, create a bow-tie, or duplicate edges/faces.</summary>
    WouldCreateNonManifold,
    /// <summary>Collapsing would flatten an isolated tetrahedron into a degenerate pillow.</summary>
    WouldCollapseTetrahedron,
    /// <summary>Collapsing would reduce a triangle attached only through its apex to an isolated edge.</summary>
    WouldCollapseLastTriangle,
    /// <summary>The edge borders no face at all (a wire edge); there is nothing to collapse.</summary>
    IsolatedEdge,
    /// <summary>A numeric parameter is out of range (e.g. split parameter outside (0, 1)).</summary>
    InvalidParameter,
}

/// <summary>Result details of <see cref="EditableMesh.SplitEdge"/>.</summary>
/// <param name="NewVertex">The vertex inserted on the edge.</param>
/// <param name="HalfEdgeToNew">The surviving half-edge, now ending at the new vertex (origin unchanged).</param>
/// <param name="HalfEdgeFromNew">The new half-edge continuing from the new vertex to the original destination.</param>
/// <param name="NewFaceLeft">New face on the left side of the (possibly twin-normalized) edge.</param>
/// <param name="NewFaceRight">New face on the right side, or -1 when the edge was a boundary edge.</param>
/// <param name="IsBoundary">True when the split edge was a boundary edge.</param>
public readonly record struct EdgeSplitInfo(
    int NewVertex, int HalfEdgeToNew, int HalfEdgeFromNew, int NewFaceLeft, int NewFaceRight, bool IsBoundary);

/// <summary>Result details of <see cref="EditableMesh.FlipEdge"/>.</summary>
/// <param name="HalfEdge">The flipped half-edge (same index, now connecting the opposite vertices).</param>
/// <param name="OppositeVertex0">Origin of the flipped half-edge (was the left face's apex).</param>
/// <param name="OppositeVertex1">Destination of the flipped half-edge (was the right face's apex).</param>
public readonly record struct EdgeFlipInfo(int HalfEdge, int OppositeVertex0, int OppositeVertex1);

/// <summary>Result details of <see cref="EditableMesh.CollapseEdge"/>.</summary>
/// <param name="KeptVertex">The surviving vertex (the collapsed half-edge's origin).</param>
/// <param name="RemovedVertex">The removed vertex (the collapsed half-edge's destination).</param>
/// <param name="RemovedFaceLeft">Removed face on the half-edge's left, or -1.</param>
/// <param name="RemovedFaceRight">Removed face on the half-edge's right, or -1.</param>
/// <param name="IsBoundary">True when the collapsed edge was a boundary edge.</param>
public readonly record struct EdgeCollapseInfo(
    int KeptVertex, int RemovedVertex, int RemovedFaceLeft, int RemovedFaceRight, bool IsBoundary);

/// <summary>Result details of <see cref="EditableMesh.PokeFace"/>.</summary>
/// <param name="NewVertex">The interior vertex the face was poked with.</param>
/// <param name="Faces">All faces of the fan (first is the reused original face index).
/// Immutable: a plain <c>int[]</c> here would let a caller rewrite the reported topology
/// after the fact (and record equality would silently compare stale contents).</param>
public readonly record struct FacePokeInfo(int NewVertex, ImmutableArray<int> Faces);

/// <summary>Result details of <see cref="EditableMesh.MergeEdges"/>.</summary>
/// <param name="KeptHalfEdge">The kept edge's interior half-edge (its twin is now the discarded edge's interior half-edge).</param>
/// <param name="RemovedVertex0">Vertex merged away at the kept half-edge's origin end, or -1 if already shared.</param>
/// <param name="RemovedVertex1">Vertex merged away at the kept half-edge's destination end, or -1 if already shared.</param>
/// <param name="ExtraWeldedEdges">Doubled boundary edges automatically welded after the merge.</param>
public readonly record struct EdgeMergeInfo(
    int KeptHalfEdge, int RemovedVertex0, int RemovedVertex1, int ExtraWeldedEdges);

public sealed partial class EditableMesh
{
    /// <summary>
    /// Splits an edge at parameter <paramref name="t"/> (measured from
    /// <paramref name="halfEdge"/>'s origin), inserting a new vertex. Each adjacent
    /// triangle splits into two; a boundary side just gains a boundary half-edge.
    /// Adjacent real faces must be triangles. Preserves the Euler characteristic.
    /// </summary>
    public MeshOperationResult SplitEdge(int halfEdge, out EdgeSplitInfo info, double t = 0.5)
    {
        info = default;
        if (!IsHalfEdge(halfEdge))
            return MeshOperationResult.NotAHalfEdge;
        if (!(t > 0.0 && t < 1.0))
            return MeshOperationResult.InvalidParameter;

        // Position measured on the caller's half-edge before any twin normalization.
        var p0 = _positions[_heOrigin[halfEdge]];
        var p1 = _positions[_heOrigin[_heTwin[halfEdge]]];
        var position = new Vector3d(
            p0.X + (p1.X - p0.X) * t,
            p0.Y + (p1.Y - p0.Y) * t,
            p0.Z + (p1.Z - p0.Z) * t);

        int h = _heFace[halfEdge] >= 0 ? halfEdge : _heTwin[halfEdge]; // real face on h's side
        int h2 = _heTwin[h];
        int f0 = _heFace[h];
        if (FaceDegree(f0) != 3)
            return MeshOperationResult.NotATriangle;
        int f1 = _heFace[h2];
        bool interior = f1 >= 0;
        if (interior && FaceDegree(f1) != 3)
            return MeshOperationResult.NotATriangle;

        BeginOperation();

        int hn = _heNext[h], hp = _hePrev[h];
        int c = _heOrigin[hp];
        int m = AllocVertex(position);

        int ht = AllocHalfEdge();  // m → b (pairs with h2)
        int hbm = AllocHalfEdge(); // m → a (pairs with h)
        int hmc = AllocHalfEdge(); // m → c
        int hcm = AllocHalfEdge(); // c → m
        int fB = AllocFace();

        SetHeTwin(h, hbm);
        SetHeTwin(hbm, h);
        SetHeTwin(ht, h2);
        SetHeTwin(h2, ht);
        SetHeTwin(hmc, hcm);
        SetHeTwin(hcm, hmc);

        // Left triangle (a,b,c) → (a,m,c) reusing f0: h(a→m), hmc(m→c), hp(c→a)
        SetHeNext(h, hmc);
        SetHePrev(hmc, h);
        SetHeNext(hmc, hp);
        SetHePrev(hp, hmc);
        SetHeNext(hp, h);
        SetHePrev(h, hp);
        SetHeOrigin(hmc, m);
        SetHeFace(hmc, f0);
        SetFaceHe(f0, h);

        // New face (m,b,c): ht(m→b), hn(b→c), hcm(c→m)
        SetHeNext(ht, hn);
        SetHePrev(hn, ht);
        SetHeNext(hn, hcm);
        SetHePrev(hcm, hn);
        SetHeNext(hcm, ht);
        SetHePrev(ht, hcm);
        SetHeOrigin(ht, m);
        SetHeOrigin(hcm, c);
        SetHeFace(ht, fB);
        SetHeFace(hn, fB);
        SetHeFace(hcm, fB);
        SetFaceHe(fB, ht);

        int fD = -1;
        if (interior)
        {
            int hn2 = _heNext[h2], hp2 = _hePrev[h2];
            int d = _heOrigin[hp2];
            int hmd = AllocHalfEdge(); // m → d
            int hdm = AllocHalfEdge(); // d → m
            fD = AllocFace();
            SetHeTwin(hmd, hdm);
            SetHeTwin(hdm, hmd);

            // Right triangle (b,a,d) → (b,m,d) reusing f1: h2(b→m), hmd(m→d), hp2(d→b)
            SetHeNext(h2, hmd);
            SetHePrev(hmd, h2);
            SetHeNext(hmd, hp2);
            SetHePrev(hp2, hmd);
            SetHeNext(hp2, h2);
            SetHePrev(h2, hp2);
            SetHeOrigin(hmd, m);
            SetHeFace(hmd, f1);
            SetFaceHe(f1, h2);

            // New face (m,a,d): hbm(m→a), hn2(a→d), hdm(d→m)
            SetHeNext(hbm, hn2);
            SetHePrev(hn2, hbm);
            SetHeNext(hn2, hdm);
            SetHePrev(hdm, hn2);
            SetHeNext(hdm, hbm);
            SetHePrev(hbm, hdm);
            SetHeOrigin(hbm, m);
            SetHeOrigin(hdm, d);
            SetHeFace(hbm, fD);
            SetHeFace(hn2, fD);
            SetHeFace(hdm, fD);
            SetFaceHe(fD, hbm);

            SetVertexOut(m, ht);
        }
        else
        {
            // Boundary side: h2 (was b→a) now ends at m; insert hbm (m→a) after it in
            // the boundary chain. The new vertex's outgoing pointer prefers the
            // boundary half-edge, per the structure's invariant.
            int bn = _heNext[h2];
            SetHeOrigin(hbm, m);
            SetHeFace(hbm, -1);
            SetHeNext(h2, hbm);
            SetHePrev(hbm, h2);
            SetHeNext(hbm, bn);
            SetHePrev(bn, hbm);
            SetVertexOut(m, hbm);
        }

        CommitOperation(nameof(SplitEdge));
        info = new EdgeSplitInfo(m, h, ht, fB, fD, !interior);
        return MeshOperationResult.Ok;
    }

    /// <summary>
    /// Flips an interior edge between two triangles: the edge connecting the shared
    /// vertices is replaced by the edge connecting the two opposite apexes (same
    /// half-edge indices, rewired). Refuses boundary edges, non-triangles, and flips
    /// whose target edge already exists.
    /// </summary>
    public MeshOperationResult FlipEdge(int halfEdge, out EdgeFlipInfo info)
    {
        info = default;
        if (!IsHalfEdge(halfEdge))
            return MeshOperationResult.NotAHalfEdge;
        int h = halfEdge, h2 = _heTwin[h];
        int t0 = _heFace[h], t1 = _heFace[h2];
        if (t0 < 0 || t1 < 0)
            return MeshOperationResult.BoundaryEdge;
        if (FaceDegree(t0) != 3 || FaceDegree(t1) != 3)
            return MeshOperationResult.NotATriangle;

        int a = _heOrigin[h], b = _heOrigin[h2];
        int hn = _heNext[h], hp = _hePrev[h];    // b→c, c→a
        int hn2 = _heNext[h2], hp2 = _hePrev[h2]; // a→d, d→b
        int c = _heOrigin[hp], d = _heOrigin[hp2];
        if (c == d)
            return MeshOperationResult.WouldCreateNonManifold;
        if (FindHalfEdge(c, d) >= 0)
            return MeshOperationResult.EdgeAlreadyExists;

        BeginOperation();

        // t0 becomes (c,d,b): h(c→d), hp2(d→b), hn(b→c)
        SetHeOrigin(h, c);
        SetHeNext(h, hp2);
        SetHePrev(hp2, h);
        SetHeNext(hp2, hn);
        SetHePrev(hn, hp2);
        SetHeNext(hn, h);
        SetHePrev(h, hn);
        SetHeFace(hp2, t0);
        SetFaceHe(t0, h);

        // t1 becomes (d,c,a): h2(d→c), hp(c→a), hn2(a→d)
        SetHeOrigin(h2, d);
        SetHeNext(h2, hp);
        SetHePrev(hp, h2);
        SetHeNext(hp, hn2);
        SetHePrev(hn2, hp);
        SetHeNext(hn2, h2);
        SetHePrev(h2, hn2);
        SetHeFace(hp, t1);
        SetFaceHe(t1, h2);

        // The flipped half-edges no longer leave a/b; repoint stale outgoing pointers.
        // (A boundary vertex's outgoing pointer is its boundary half-edge, never the
        // interior h/h2, so the boundary-preference invariant is unaffected.)
        if (_vertexOut[a] == h)
            SetVertexOut(a, hn2);
        if (_vertexOut[b] == h2)
            SetVertexOut(b, hn);

        CommitOperation(nameof(FlipEdge));
        info = new EdgeFlipInfo(h, c, d);
        return MeshOperationResult.Ok;
    }

    /// <summary>Pokes a face with a vertex at its centroid; see <see cref="PokeFace(int, in Vector3d, out FacePokeInfo)"/>.</summary>
    public MeshOperationResult PokeFace(int face, out FacePokeInfo info)
    {
        if (!IsFace(face))
        {
            info = default;
            return MeshOperationResult.NotAFace;
        }
        var centroid = Vector3d.Zero;
        int count = 0;
        foreach (int v in FaceVertices(face))
        {
            centroid += _positions[v];
            count++;
        }
        return PokeFace(face, centroid / count, out info);
    }

    /// <summary>
    /// Replaces a face (any degree n ≥ 3) with a fan of n triangles around a new
    /// interior vertex at <paramref name="position"/> (1→3 for a triangle). Preserves
    /// the Euler characteristic; for a planar face with the point on its plane, area
    /// and enclosed volume are unchanged.
    /// </summary>
    public MeshOperationResult PokeFace(int face, in Vector3d position, out FacePokeInfo info)
    {
        info = default;
        if (!IsFace(face))
            return MeshOperationResult.NotAFace;

        var loop = new List<int>();
        {
            int start = _faceHe[face];
            int he = start;
            do
            {
                loop.Add(he);
                he = _heNext[he];
            } while (he != start);
        }
        int n = loop.Count;

        BeginOperation();

        int m = AllocVertex(position);
        var spokesIn = new int[n];  // v_i → m
        var spokesOut = new int[n]; // m → v_i
        var faces = new int[n];
        for (int i = 0; i < n; i++)
        {
            spokesIn[i] = AllocHalfEdge();
            spokesOut[i] = AllocHalfEdge();
            faces[i] = i == 0 ? face : AllocFace();
        }
        for (int i = 0; i < n; i++)
        {
            SetHeTwin(spokesIn[i], spokesOut[i]);
            SetHeTwin(spokesOut[i], spokesIn[i]);
        }
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            int e = loop[i];
            int f = faces[i];
            int vNext = _heOrigin[loop[next]];
            // Fan triangle (v_i, v_{i+1}, m): e(v_i→v_{i+1}), spokesIn[next](v_{i+1}→m), spokesOut[i](m→v_i)
            SetHeNext(e, spokesIn[next]);
            SetHePrev(spokesIn[next], e);
            SetHeNext(spokesIn[next], spokesOut[i]);
            SetHePrev(spokesOut[i], spokesIn[next]);
            SetHeNext(spokesOut[i], e);
            SetHePrev(e, spokesOut[i]);
            SetHeOrigin(spokesIn[next], vNext);
            SetHeOrigin(spokesOut[i], m);
            SetHeFace(e, f);
            SetHeFace(spokesIn[next], f);
            SetHeFace(spokesOut[i], f);
            SetFaceHe(f, e);
        }
        SetVertexOut(m, spokesOut[0]);

        CommitOperation(nameof(PokeFace));
        // The scratch array is never touched again, so hand it over without a copy.
        info = new FacePokeInfo(m, ImmutableCollectionsMarshal.AsImmutableArray(faces));
        return MeshOperationResult.Ok;
    }

    /// <summary>
    /// Collapses an edge, merging the half-edge's destination vertex into its origin
    /// (the origin keeps its position; move it with <see cref="SetPosition"/> if
    /// desired). Adjacent real faces must be triangles and are removed. The full guard
    /// set refuses collapses that would pinch the surface: the link condition (shared
    /// neighbors must be exactly the opposite apexes), the interior-edge
    /// both-endpoints-on-boundary bow-tie case, duplicate edges between the endpoints,
    /// the isolated-tetrahedron case, and the last-triangle-on-a-spike case.
    /// </summary>
    public MeshOperationResult CollapseEdge(int halfEdge, out EdgeCollapseInfo info)
    {
        info = default;
        if (!IsHalfEdge(halfEdge))
            return MeshOperationResult.NotAHalfEdge;

        int h = halfEdge, h2 = _heTwin[h];
        int a = _heOrigin[h], b = _heOrigin[h2]; // keep a, remove b
        int t0 = _heFace[h], t1 = _heFace[h2];
        bool left = t0 >= 0, right = t1 >= 0;
        // A wire edge (void on both sides) has no triangles to remove and no boundary
        // chain to splice — the later guards would index face-less half-edges. Build
        // cannot produce one today, so this is defensive: refuse, never throw.
        if (!left && !right)
            return MeshOperationResult.IsolatedEdge;
        if (left && FaceDegree(t0) != 3)
            return MeshOperationResult.NotATriangle;
        if (right && FaceDegree(t1) != 3)
            return MeshOperationResult.NotATriangle;
        bool edgeBoundary = !left || !right;

        int hn = -1, hp = -1, c = -1, xbc = -1, xca = -1;
        if (left)
        {
            hn = _heNext[h];   // b→c
            hp = _hePrev[h];   // c→a
            c = _heOrigin[hp];
            xbc = _heTwin[hn]; // c→b (outside)
            xca = _heTwin[hp]; // a→c (outside)
        }
        int hn2 = -1, hp2 = -1, d = -1, xad = -1, xdb = -1;
        if (right)
        {
            hn2 = _heNext[h2];  // a→d
            hp2 = _hePrev[h2];  // d→b
            d = _heOrigin[hp2];
            xad = _heTwin[hn2]; // d→a (outside)
            xdb = _heTwin[hp2]; // b→d (outside)
        }
        if (left && right && c == d)
            return MeshOperationResult.WouldCreateNonManifold; // duplicate triangle / pillow

        // Interior edge with both endpoints on a boundary: collapsing joins the two
        // boundary fans through the interior — a bow-tie.
        if (!edgeBoundary && IsBoundaryVertex(a) && IsBoundaryVertex(b))
            return MeshOperationResult.WouldCreateNonManifold;

        var bRing = new List<int>();
        foreach (int he in OutgoingHalfEdges(b))
            bRing.Add(he);

        // Exactly one edge may connect a and b (a second one would collapse to a loop).
        int edgesToA = 0;
        foreach (int he in bRing)
        {
            if (_heOrigin[_heTwin[he]] == a)
                edgesToA++;
        }
        if (edgesToA != 1)
            return MeshOperationResult.WouldCreateNonManifold;

        // Link condition: the neighborhoods of a and b may only meet at the opposite
        // apexes (c and d); any other shared vertex means the collapse pinches the
        // surface into a non-manifold junction. (Same rule the decimator enforces on
        // its face-set representation.)
        var aNeighbors = new HashSet<int>();
        foreach (int he in OutgoingHalfEdges(a))
            aNeighbors.Add(_heOrigin[_heTwin[he]]);
        foreach (int he in bRing)
        {
            int nb = _heOrigin[_heTwin[he]];
            if (nb == a)
                continue;
            if (aNeighbors.Contains(nb) && nb != c && nb != d)
                return MeshOperationResult.WouldCreateNonManifold;
        }

        // Isolated tetrahedron: if the opposite apexes are joined by an interior edge
        // whose two faces contain a and b, the collapse flattens the tet into a
        // degenerate two-triangle pillow.
        if (left && right)
        {
            int hcd = FindHalfEdge(c, d);
            if (hcd >= 0)
            {
                int fcd0 = _heFace[hcd], fcd1 = _heFace[_heTwin[hcd]];
                if (fcd0 >= 0 && fcd1 >= 0 &&
                    ((FaceContains(fcd0, a) && FaceContains(fcd1, b)) ||
                     (FaceContains(fcd0, b) && FaceContains(fcd1, a))))
                    return MeshOperationResult.WouldCollapseTetrahedron;
            }
        }

        // Boundary edge whose triangle hangs on the mesh only through its apex: the
        // collapse would leave an isolated edge.
        if (edgeBoundary)
        {
            int e0 = left ? hn : hn2;
            int e1 = left ? hp : hp2;
            if (IsBoundaryEdge(e0) && IsBoundaryEdge(e1))
                return MeshOperationResult.WouldCollapseLastTriangle;
        }

        BeginOperation();

        // 1. Move b's surviving half-edges to a.
        foreach (int he in bRing)
        {
            if (he == h2 || (left && he == hn))
                continue; // these die with the collapsed faces
            SetHeOrigin(he, a);
        }

        // 2. Merge the edge pairs that become coincident: edge b–c merges into a–c
        //    (outside half-edges become twins), and likewise b–d into a–d.
        if (left)
        {
            SetHeTwin(xca, xbc);
            SetHeTwin(xbc, xca);
        }
        if (right)
        {
            SetHeTwin(xad, xdb);
            SetHeTwin(xdb, xad);
        }

        // 3. Splice the boundary chain past the collapsed edge's boundary half-edge.
        if (!left)
            LinkBoundary(_hePrev[h], _heNext[h]);
        if (!right)
            LinkBoundary(_hePrev[h2], _heNext[h2]);

        // 4. Free the collapsed elements.
        if (left)
        {
            FreeFace(t0);
            FreeHalfEdge(hn);
            FreeHalfEdge(hp);
        }
        if (right)
        {
            FreeFace(t1);
            FreeHalfEdge(hn2);
            FreeHalfEdge(hp2);
        }
        FreeHalfEdge(h);
        FreeHalfEdge(h2);
        FreeVertex(b);

        // 5. Repair outgoing pointers (a may have inherited b's boundary; c/d may have
        //    pointed at the freed interior half-edges).
        FixVertexOut(a, left ? xca : xdb);
        if (left)
            FixVertexOut(c, xbc);
        if (right)
            FixVertexOut(d, xad);

        CommitOperation(nameof(CollapseEdge));
        info = new EdgeCollapseInfo(a, b, left ? t0 : -1, right ? t1 : -1, edgeBoundary);
        return MeshOperationResult.Ok;
    }

    /// <summary>
    /// Welds two boundary half-edges into one interior edge — the crack-closing
    /// primitive. The discarded side is welded head-to-tail onto the kept side, so the
    /// discarded interior half-edge's origin merges into the kept edge's interior
    /// destination and vice versa; positions of the kept vertices are unchanged (for
    /// crack zipping the pairs are coincident anyway). Ends that already share a vertex
    /// are simply sealed. When the weld leaves the merged vertices with doubled
    /// boundary edges to a common neighbor (allowed by the guards only when weldable),
    /// they are welded automatically, g3-style. Guards refuse configurations that would
    /// create self-loops, duplicate interior edges, or unweldable doubles.
    /// </summary>
    public MeshOperationResult MergeEdges(int keepBoundaryHalfEdge, int discardBoundaryHalfEdge, out EdgeMergeInfo info)
    {
        info = default;
        if (!IsHalfEdge(keepBoundaryHalfEdge) || !IsHalfEdge(discardBoundaryHalfEdge))
            return MeshOperationResult.NotAHalfEdge;
        int kBnd = keepBoundaryHalfEdge, lBnd = discardBoundaryHalfEdge;
        if (_heFace[kBnd] >= 0 || _heFace[lBnd] >= 0)
            return MeshOperationResult.NotABoundaryEdge;
        if (kBnd == lBnd)
            return MeshOperationResult.SameEdge;

        int kInt = _heTwin[kBnd]; // a → b
        int lInt = _heTwin[lBnd]; // c → d
        int a = _heOrigin[kInt], b = _heOrigin[kBnd];
        int c = _heOrigin[lInt], d = _heOrigin[lBnd];
        // Identification: d merges into a, c merges into b (head-to-tail weld).
        if (d == b || c == a)
            return MeshOperationResult.WouldCreateNonManifold; // would merge an edge's own endpoints

        // Existing edges between vertices being identified become self-loops; edges
        // a–c / b–d become duplicates of the welded edge itself.
        if (d != a && FindHalfEdge(a, d) >= 0)
            return MeshOperationResult.WouldCreateNonManifold;
        if (c != b && FindHalfEdge(b, c) >= 0)
            return MeshOperationResult.WouldCreateNonManifold;
        // (When an end is already shared, the edge found here would be the discard edge
        // itself, so these duplicate-of-the-weld checks need both ends distinct; the
        // shared-end variants are covered by the neighborhood check below.)
        if (c != b && d != a && FindHalfEdge(a, c) >= 0)
            return MeshOperationResult.WouldCreateNonManifold;
        if (d != a && c != b && FindHalfEdge(b, d) >= 0)
            return MeshOperationResult.WouldCreateNonManifold;

        List<int>? dRing = null, cRing = null;
        if (d != a)
        {
            dRing = new List<int>();
            foreach (int he in OutgoingHalfEdges(d))
                dRing.Add(he);
            var guard = CheckMergeNeighborhood(dRing, skip: lBnd, mergeInto: a, mergedVertex: d, partner: c);
            if (guard != MeshOperationResult.Ok)
                return guard;
        }
        if (c != b)
        {
            cRing = new List<int>();
            foreach (int he in OutgoingHalfEdges(c))
                cRing.Add(he);
            var guard = CheckMergeNeighborhood(cRing, skip: lInt, mergeInto: b, mergedVertex: c, partner: d);
            if (guard != MeshOperationResult.Ok)
                return guard;
        }

        BeginOperation();

        // 1. Move the merged vertices' half-edges (the dying boundary half-edges excepted).
        if (dRing is not null)
        {
            foreach (int he in dRing)
            {
                if (he != lBnd)
                    SetHeOrigin(he, a);
            }
        }
        if (cRing is not null)
        {
            foreach (int he in cRing)
                SetHeOrigin(he, b);
        }

        // 2. Weld: the two interior half-edges become twins; the boundary pair leaves
        //    the boundary chains and dies.
        SetHeTwin(kInt, lInt);
        SetHeTwin(lInt, kInt);
        RemoveBoundaryPair(kBnd, lBnd);
        FreeHalfEdge(kBnd);
        FreeHalfEdge(lBnd);
        if (d != a)
            FreeVertex(d);
        if (c != b)
            FreeVertex(c);

        // 3. Repair outgoing pointers of the kept vertices.
        FixVertexOut(a, kInt);
        FixVertexOut(b, lInt);

        // 4. The merge may have doubled boundary edges at the kept vertices (two
        //    boundary edges to the same neighbor); weld them until none remain.
        int extra = 0;
        extra += WeldDoubledBoundaryEdgesAt(a);
        extra += WeldDoubledBoundaryEdgesAt(b);

        CommitOperation(nameof(MergeEdges));
        info = new EdgeMergeInfo(kInt, d != a ? d : -1, c != b ? c : -1, extra);
        return MeshOperationResult.Ok;
    }

    // Guard helper for MergeEdges: vertices `mergedVertex` → `mergeInto` are being
    // identified. Any common neighbor x would double the edges to x; that is only
    // recoverable (by the automatic post-weld) when both edges are boundary edges with
    // opposite interior orientations. A second edge to the merge partner would double
    // the welded edge itself.
    private MeshOperationResult CheckMergeNeighborhood(List<int> mergedRing, int skip, int mergeInto, int mergedVertex, int partner)
    {
        foreach (int he in mergedRing)
        {
            if (he == skip)
                continue;
            int x = _heOrigin[_heTwin[he]];
            if (x == partner)
                return MeshOperationResult.WouldCreateNonManifold; // second edge to the partner
            int other = FindHalfEdge(mergeInto, x);
            if (other < 0)
                continue;
            if (!IsBoundaryEdge(he) || !IsBoundaryEdge(other))
                return MeshOperationResult.WouldCreateNonManifold;
            // Orientation: after identification the two interior half-edges must run
            // opposite ways or the doubled pair cannot be welded.
            int iMerged = _heFace[he] >= 0 ? he : _heTwin[he];
            int iOther = _heFace[other] >= 0 ? other : _heTwin[other];
            bool mergedLeavesVertex = _heOrigin[iMerged] == mergedVertex;
            bool otherLeavesVertex = _heOrigin[iOther] == mergeInto;
            if (mergedLeavesVertex == otherLeavesVertex)
                return MeshOperationResult.WouldCreateNonManifold;
        }
        return MeshOperationResult.Ok;
    }

    /// <summary>
    /// Removes a pair of boundary half-edges from the boundary chains, splicing the
    /// chains across the sealed gap. Handles all adjacency cases: fully separate
    /// chains, one shared endpoint (the pair is consecutive), and the isolated
    /// two-half-edge loop (a slit being closed completely).
    /// </summary>
    private void RemoveBoundaryPair(int b1, int b2)
    {
        int n1 = _heNext[b1], p1 = _hePrev[b1];
        int n2 = _heNext[b2], p2 = _hePrev[b2];
        if (n1 == b2 && n2 == b1)
            return; // isolated 2-loop; both die with no splice
        if (n1 == b2)
        {
            LinkBoundary(p1, n2);
            return;
        }
        if (n2 == b1)
        {
            LinkBoundary(p2, n1);
            return;
        }
        LinkBoundary(p1, n2);
        LinkBoundary(p2, n1);
    }

    private void LinkBoundary(int he, int next)
    {
        SetHeNext(he, next);
        SetHePrev(next, he);
    }

    // After a merge, a kept vertex may carry two boundary edges to the same neighbor
    // (one outgoing boundary half-edge, one interior half-edge with a boundary twin).
    // Weld each such pair: their interior half-edges become twins and the boundary
    // half-edges die. Loops until no doubles remain (each weld strictly shrinks).
    private int WeldDoubledBoundaryEdgesAt(int v)
    {
        int welds = 0;
        while (true)
        {
            int found1 = -1, found2 = -1;
            var ring = new List<int>();
            foreach (int he in OutgoingHalfEdges(v))
                ring.Add(he);
            foreach (int o1 in ring)
            {
                if (_heFace[o1] >= 0)
                    continue; // want the outgoing boundary half-edge v→x
                int x = _heOrigin[_heTwin[o1]];
                foreach (int o2 in ring)
                {
                    if (o2 == o1 || _heOrigin[_heTwin[o2]] != x)
                        continue;
                    if (_heFace[o2] >= 0 && _heFace[_heTwin[o2]] < 0)
                    {
                        found1 = o1;
                        found2 = o2;
                        break;
                    }
                }
                if (found1 >= 0)
                    break;
            }
            if (found1 < 0)
                return welds;

            int bnd1 = found1;              // v→x boundary
            int bnd2 = _heTwin[found2];     // x→v boundary
            int int1 = _heTwin[found1];     // x→v interior
            int int2 = found2;              // v→x interior
            int x2 = _heOrigin[int1];
            RemoveBoundaryPair(bnd1, bnd2);
            SetHeTwin(int1, int2);
            SetHeTwin(int2, int1);
            FreeHalfEdge(bnd1);
            FreeHalfEdge(bnd2);
            FixVertexOut(v, int2);
            FixVertexOut(x2, int1);
            welds++;
        }
    }

    /// <summary>
    /// Recomputes a vertex's outgoing pointer from a known-live outgoing half-edge,
    /// preferring a boundary half-edge (the invariant that makes
    /// <see cref="IsBoundaryVertex"/> O(1)).
    /// </summary>
    private void FixVertexOut(int v, int fallback)
    {
        int chosen = fallback;
        int he = fallback;
        int guard = _heOrigin.Count;
        do
        {
            if (_heFace[he] < 0)
            {
                chosen = he;
                break;
            }
            he = _heTwin[_hePrev[he]];
            if (guard-- < 0)
                throw new InvalidOperationException($"Vertex {v}: outgoing walk did not close (corrupt topology).");
        } while (he != fallback);
        SetVertexOut(v, chosen);
    }

    private bool FaceContains(int face, int vertex)
    {
        int start = _faceHe[face];
        int he = start;
        do
        {
            if (_heOrigin[he] == vertex)
                return true;
            he = _heNext[he];
        } while (he != start);
        return false;
    }
}
