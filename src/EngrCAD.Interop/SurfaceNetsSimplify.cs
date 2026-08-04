using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Interop;

/// <summary>
/// The adaptive half of <see cref="SurfaceNets"/>: octree-clustered simplification of the
/// dual mesh, driven by the same <see cref="SurfaceQef"/> quadrics the sharp-feature pass
/// already computed.
/// <para>
/// <b>Bottom-up, and that is a correctness decision rather than a convenience.</b> The
/// textbook adaptive contouring is a top-down octree that stops subdividing where the
/// field looks flat, which would save the VISIT as well as the faces. It is declined for
/// exactly the reason <see cref="SurfaceCull"/>'s remarks give about seed-and-flood: a
/// stopping rule read off finitely many samples of a Lipschitz field cannot certify that
/// no feature hides between them, so it is a claim about geometry nobody has looked at.
/// Collapsing cells the uniform walk HAS looked at inherits the cull's completeness
/// argument unchanged and adds no new one. The cost of that honesty is stated: this saves
/// faces and everything downstream of them, and saves no evaluation time at all.
/// </para>
/// <para>
/// <b>Cracks are structurally impossible here</b>, which is the other reason for this
/// shape. Adaptive contouring's classic failure is a T-junction where a coarse cell meets
/// a fine one; there is none to make, because the connectivity is never re-derived — the
/// uniform walk's face buffer is RE-INDEXED through the clustering and nothing else. A
/// face whose corners all land in one cluster vanishes; a face straddling two becomes a
/// triangle; every other face keeps the quad it always was.
/// </para>
/// <para>
/// <b>What is NOT structural is manifoldness, and it is checked rather than argued.</b>
/// Contracting a connected set of dual vertices to a point is a manifold quotient only when
/// the set's induced subcomplex is a disk, which a cluster of cells need not be: two sheets
/// of a thin wall can pass through one octant, and a cluster can wrap an edge. Two guards
/// answer the two halves. Splitting each octant's members into CONNECTED COMPONENTS first
/// removes the two-sheets case by construction — the same rule, one level up, that gives a
/// cell one vertex per sheet rather than one in total. The rest is measured: the collapsed
/// face buffer is checked for a repeated corner, for a duplicate directed edge (what
/// <see cref="HalfEdgeMesh.Build"/> refuses) and for a pinched vertex link (what
/// <see cref="HalfEdgeMesh.NonManifoldVertices"/> reports), and any cluster implicated in a
/// violation is REVERTED. That loop terminates for a reason worth stating: reverting only
/// ever splits vertices apart, and splitting cannot create a violation — two directed edges
/// that were distinct cannot become equal by un-merging — so every round strictly removes
/// merges and the empty clustering is the original, valid mesh.
/// </para>
/// </summary>
internal static class SurfaceNetsSimplify
{
    /// <summary>
    /// Rounds of revert-and-recheck before a level is abandoned wholesale. Each round
    /// removes at least one cluster, so the loop is finite either way; the cap bounds the
    /// COST (each round rebuilds the face buffer) rather than the termination.
    /// </summary>
    private const int MaxRepairRounds = 8;

    public static HalfEdgeMesh Collapse(
        List<Vector3d> positions,
        int[] quadCorners,
        List<SurfaceQef> vertexQef,
        List<Vector3i> vertexCell,
        double tolerance,
        int maxLevels,
        double singularRatio)
    {
        int n = positions.Count;
        if (n == 0 || quadCorners.Length == 0 || vertexQef.Count != n)
            return HalfEdgeMesh.Build(positions, quadCorners, verticesPerFace: 4);

        // Per-cluster state. A cluster is named by the smallest vertex index in it; `label`
        // maps every vertex to its cluster and `members` inverts that.
        var label = new int[n];
        var members = new List<int>[n];
        var qef = new SurfaceQef[n];
        var place = new Vector3d[n];
        var low = new Vector3d[n];
        var high = new Vector3d[n];
        var cellOf = new Vector3i[n];
        for (int v = 0; v < n; v++)
        {
            label[v] = v;
            members[v] = [v];
            qef[v] = vertexQef[v];
            place[v] = positions[v];
            low[v] = positions[v];
            high[v] = positions[v];
            cellOf[v] = vertexCell[v];
        }

        var adjacency = BuildAdjacency(quadCorners, n);

        // Snapshots for the revert, refreshed per level.
        var previousLabel = new int[n];
        var previousPlace = new Vector3d[n];

        for (int level = 1; level <= maxLevels; level++)
        {
            Array.Copy(label, previousLabel, n);
            Array.Copy(place, previousPlace, n);

            var octants = new Dictionary<(int, int, int), List<int>>();
            for (int id = 0; id < n; id++)
            {
                if (label[id] != id)
                    continue;
                var c = cellOf[id];
                var key = (c.X >> level, c.Y >> level, c.Z >> level);
                if (!octants.TryGetValue(key, out var bucket))
                    octants[key] = bucket = [];
                bucket.Add(id);
            }

            bool merged = false;
            var seen = new HashSet<int>();
            var stack = new Stack<int>();
            var component = new List<int>();
            foreach (var bucket in octants.Values)
            {
                if (bucket.Count < 2)
                    continue;
                var inBucket = new HashSet<int>(bucket);
                seen.Clear();
                foreach (int seed in bucket)
                {
                    if (!seen.Add(seed))
                        continue;
                    component.Clear();
                    component.Add(seed);
                    stack.Clear();
                    stack.Push(seed);
                    while (stack.Count > 0)
                    {
                        int id = stack.Pop();
                        foreach (int v in members[id])
                        {
                            foreach (int w in adjacency.Neighbours(v))
                            {
                                int other = label[w];
                                if (other == id || !inBucket.Contains(other) || !seen.Add(other))
                                    continue;
                                component.Add(other);
                                stack.Push(other);
                            }
                        }
                    }
                    if (component.Count < 2)
                        continue;

                    var combined = qef[component[0]];
                    var lo = low[component[0]];
                    var hi = high[component[0]];
                    for (int c = 1; c < component.Count; c++)
                    {
                        combined += qef[component[c]];
                        lo = Vector3d.Min(lo, low[component[c]]);
                        hi = Vector3d.Max(hi, high[component[c]]);
                    }

                    var x = combined.Solve(singularRatio);
                    x = new Vector3d(
                        Math.Clamp(x.X, lo.X, hi.X),
                        Math.Clamp(x.Y, lo.Y, hi.Y),
                        Math.Clamp(x.Z, lo.Z, hi.Z));
                    // The tolerance is a LENGTH: the root-mean-square distance from the
                    // merged vertex to every tangent plane the cluster swallowed.
                    if (Math.Sqrt(combined.Error(x) / combined.Count) > tolerance)
                        continue;

                    int root = component[0];
                    foreach (int id in component)
                        root = Math.Min(root, id);
                    foreach (int id in component)
                    {
                        if (id == root)
                            continue;
                        foreach (int v in members[id])
                        {
                            label[v] = root;
                            members[root].Add(v);
                        }
                        members[id].Clear();
                    }
                    qef[root] = combined;
                    place[root] = x;
                    low[root] = lo;
                    high[root] = hi;
                    merged = true;
                }
            }

            if (!merged)
                break;

            if (!Repair(label, members, previousLabel, previousPlace, place, quadCorners, positions, n))
            {
                // The level could not be made manifold within the repair budget; take it
                // back whole and stop, rather than ship a mesh the builder would refuse.
                Array.Copy(previousLabel, label, n);
                Array.Copy(previousPlace, place, n);
                RebuildMembers(label, members, n);
                break;
            }
        }

        return Emit(label, place, quadCorners, positions, n);
    }

    /// <summary>
    /// Checks the collapsed face buffer against the three ways a contraction can leave a
    /// non-manifold mesh, reverting the clusters implicated in each until it is clean.
    /// Returns false only if the budget runs out.
    /// </summary>
    private static bool Repair(
        int[] label, List<int>[] members, int[] previousLabel, Vector3d[] previousPlace,
        Vector3d[] place, int[] quadCorners, List<Vector3d> positions, int n)
    {
        var offenders = new HashSet<int>();
        for (int round = 0; round < MaxRepairRounds; round++)
        {
            offenders.Clear();
            var (corners, starts) = MapFaces(label, quadCorners, offenders);

            if (offenders.Count == 0 && starts.Count > 1)
            {
                // A duplicate directed edge is what HalfEdgeMesh.Build refuses; find it
                // here so the cluster that caused it can be named and reverted.
                var directed = new HashSet<(int, int)>();
                for (int f = 0; f + 1 < starts.Count; f++)
                {
                    int s = starts[f], e = starts[f + 1];
                    for (int c = s; c < e; c++)
                    {
                        int u = corners[c], v = corners[c + 1 < e ? c + 1 : s];
                        if (!directed.Add((u, v)))
                        {
                            offenders.Add(u);
                            offenders.Add(v);
                        }
                    }
                }
            }

            if (offenders.Count == 0)
            {
                if (starts.Count <= 1)
                    return true;
                var mesh = HalfEdgeMesh.Build(
                    positions, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(corners),
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(starts));
                foreach (int v in mesh.NonManifoldVertices())
                    offenders.Add(v);
                if (offenders.Count == 0)
                    return true;
            }

            foreach (int id in offenders)
            {
                int root = label[id];
                if (members[root].Count < 2)
                    continue;
                foreach (int v in members[root])
                {
                    label[v] = previousLabel[v];
                    place[v] = previousPlace[v];
                }
                members[root].Clear();
            }
            RebuildMembers(label, members, n);
        }
        return false;
    }

    private static void RebuildMembers(int[] label, List<int>[] members, int n)
    {
        for (int v = 0; v < n; v++)
            members[v].Clear();
        for (int v = 0; v < n; v++)
            members[label[v]].Add(v);
    }

    /// <summary>
    /// The quad buffer re-indexed through the clustering: adjacent duplicates collapse
    /// (cyclically), faces left with fewer than three corners vanish, and a face still
    /// carrying a REPEATED corner names its cluster as an offender — that is the
    /// two-opposite-corners case, which is a doubled triangle rather than a polygon.
    /// </summary>
    private static (List<int> Corners, List<int> Starts) MapFaces(
        int[] label, int[] quadCorners, HashSet<int> offenders)
    {
        var corners = new List<int>(quadCorners.Length);
        var starts = new List<int> { 0 };
        Span<int> face = stackalloc int[4];
        for (int q = 0; q < quadCorners.Length; q += 4)
        {
            int count = 0;
            for (int c = 0; c < 4; c++)
            {
                int v = label[quadCorners[q + c]];
                if (count > 0 && face[count - 1] == v)
                    continue;
                face[count++] = v;
            }
            if (count > 1 && face[0] == face[count - 1])
                count--;
            if (count < 3)
                continue;
            if (count == 4 && (face[0] == face[2] || face[1] == face[3]))
            {
                offenders.Add(face[0]);
                offenders.Add(face[1]);
                continue;
            }
            for (int c = 0; c < count; c++)
                corners.Add(face[c]);
            starts.Add(corners.Count);
        }
        return (corners, starts);
    }

    private static HalfEdgeMesh Emit(
        int[] label, Vector3d[] place, int[] quadCorners, List<Vector3d> positions, int n)
    {
        var offenders = new HashSet<int>();
        var (corners, starts) = MapFaces(label, quadCorners, offenders);

        // Compact: only the clusters some surviving face names become vertices, numbered in
        // the ORIGINAL vertex order rather than in first-use order. That is what makes the
        // neutral settings the identity — with nothing merged the map is v → v and the face
        // buffer is the uniform walk's, bit for bit — where first-use order would renumber
        // a mesh nothing had been done to.
        var used = new bool[n];
        foreach (int id in corners)
            used[id] = true;
        var remap = new int[n];
        var kept = new List<Vector3d>();
        for (int id = 0; id < n; id++)
        {
            remap[id] = used[id] ? kept.Count : -1;
            if (used[id])
                kept.Add(place[id]);
        }
        for (int c = 0; c < corners.Count; c++)
            corners[c] = remap[corners[c]];
        _ = positions;
        return HalfEdgeMesh.Build(
            kept, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(corners),
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(starts));
    }

    /// <summary>Undirected vertex adjacency of the quad mesh, in CSR form.</summary>
    private readonly struct Adjacency(int[] starts, int[] items)
    {
        public ReadOnlySpan<int> Neighbours(int v) => items.AsSpan(starts[v], starts[v + 1] - starts[v]);
    }

    private static Adjacency BuildAdjacency(int[] quadCorners, int n)
    {
        var counts = new int[n + 1];
        for (int q = 0; q < quadCorners.Length; q += 4)
        {
            for (int c = 0; c < 4; c++)
            {
                counts[quadCorners[q + c]] += 2;
            }
        }
        var starts = new int[n + 1];
        int total = 0;
        for (int v = 0; v < n; v++)
        {
            starts[v] = total;
            total += counts[v];
        }
        starts[n] = total;

        var items = new int[total];
        var at = (int[])starts.Clone();
        for (int q = 0; q < quadCorners.Length; q += 4)
        {
            for (int c = 0; c < 4; c++)
            {
                int u = quadCorners[q + c];
                int v = quadCorners[q + (c + 1) % 4];
                items[at[u]++] = v;
                items[at[v]++] = u;
            }
        }
        return new Adjacency(starts, items);
    }
}
