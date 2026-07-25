using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// The imprint boolean: cut both meshes along their exact intersection curve
/// (<see cref="MeshMeshCut"/>), decide inside/outside per surface patch, keep the halves
/// the operation asks for, and weld them back together.
/// <para>
/// <b>Classification assumption.</b> After the imprint no face of either mesh straddles
/// the other's surface, so a face is wholly inside or wholly outside — and, because the
/// intersection curve is an edge of both meshes, so is every <em>patch</em> of faces
/// bounded by that curve. Classification is therefore done once per patch, not per face,
/// at the centroid of the patch's largest triangle: the sample is then as far from the
/// other surface as the patch allows, which is what keeps sliver triangles along the seam
/// from deciding anything. (The same "probe the biggest fragment" rule the B-Rep boolean
/// learned the hard way.) The generalized winding number of a closed mesh is a clean 0/1
/// step function, so the ½ threshold is exact away from the surface.
/// </para>
/// <para>
/// Welding is by exact coordinate equality, never by tolerance: the imprint guarantees
/// both meshes carry the seam at bit-identical positions, so the two kept halves join
/// with no gap to bridge. At every seam edge exactly one face survives on each side
/// (adjacent faces sit on opposite sides of the other surface), which is what makes the
/// result closed and manifold.
/// </para>
/// <para>
/// <b>Coincident surface.</b> Where the two solids share boundary instead of crossing it
/// (flush-mating parts), the winding number is exactly ½ and decides nothing, so those
/// faces are classified by <em>normal agreement</em> instead — see
/// <see cref="CoincidentSurface"/> and <see cref="KeepCoincident"/>. The rule is the set
/// algebra of the shared surface, and its asymmetry is deliberate: both meshes cover the
/// region, so whenever it survives at all it survives as the FIRST mesh's copy and the
/// second mesh's duplicate is always dropped. Coincidence is also a patch boundary — the
/// flood fill never merges coincident faces with ordinary ones, so a wrong ½ can never
/// leak into an ordinary patch's probe.
/// </para>
/// </summary>
internal static class MeshBooleanExact
{
    public static HalfEdgeMesh Combine(
        HalfEdgeMesh a, HalfEdgeMesh b, BooleanOperation operation, ProgressCancel? progress = null)
    {
        if (!a.IsClosed || !b.IsClosed)
            throw new ArgumentException("Boolean operations require closed meshes.");

        // The imprint is the long phase; classification and welding are linear passes, so
        // the progress slice reflects that.
        var imprint = MeshMeshCut.Imprint(a, b, Slice(progress, 0, 0.85));
        var seamA = SeamEdges(imprint.MeshA, imprint);
        var seamB = SeamEdges(imprint.MeshB, imprint);
        progress?.ThrowIfCancelled();

        // Union keeps what is outside the other solid, intersection what is inside;
        // difference keeps A's outside plus B's inside, flipped so the tool's surface
        // faces into the removed material.
        bool keepAInside = operation == BooleanOperation.Intersection;
        bool keepBInside = operation != BooleanOperation.Union;
        bool reverseB = operation == BooleanOperation.Difference;

        // Each mesh is tested against the OTHER mesh's coincident triangles: "does my face
        // lie on their boundary?".
        var onB = CoincidentSurface.For(imprint.CoincidentFacesB, imprint.Epsilon);
        var onA = CoincidentSurface.For(imprint.CoincidentFacesA, imprint.Epsilon);

        var positions = new List<Vector3d>();
        var index = new Dictionary<Vector3d, int>();
        var faces = new List<int[]>();
        Emit(imprint.MeshA, seamA, imprint.MeshB, onB, keepAInside, reverse: false, fromA: true);
        progress?.Report(0.95);
        Emit(imprint.MeshB, seamB, imprint.MeshA, onA, keepBInside, reverseB, fromA: false);
        progress?.ThrowIfCancelled();
        var result = HalfEdgeMesh.Build(positions, faces);
        progress?.Report(1);
        return result;

        void Emit(
            HalfEdgeMesh mesh, HashSet<(int, int)> seam, HalfEdgeMesh otherMesh,
            CoincidentSurface? coincident, bool keepInside, bool reverse, bool fromA)
        {
            var shared = Coincidences(mesh, coincident);
            var patch = Patches(mesh, seam, shared, out int patchCount);
            var keep = Classify(mesh, patch, patchCount, shared, otherMesh, keepInside, operation, fromA);
            // The loop walk is manual, not LINQ: Face.Vertices() allocates two iterators per
            // call, and this runs once per face of both operands.
            var corners = new List<int>(4);
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                if (!keep[patch[f]])
                    continue;
                corners.Clear();
                var start = mesh.GetFace(f).AnyHalfEdge;
                var he = start;
                do
                {
                    corners.Add(Intern(he.Origin.Position));
                    he = he.Next;
                } while (he != start);
                var loop = corners.ToArray();
                if (reverse)
                    Array.Reverse(loop);
                faces.Add(loop);
            }
        }

        int Intern(in Vector3d position)
        {
            if (index.TryGetValue(position, out int existing))
                return existing;
            index[position] = positions.Count;
            positions.Add(position);
            return positions.Count - 1;
        }
    }

    /// <summary>
    /// A view of <paramref name="progress"/> whose [0, 1] maps onto
    /// [<paramref name="from"/>, <paramref name="to"/>], so a sub-phase can report its own
    /// completion without knowing where it sits in the whole operation. Cancellation passes
    /// straight through.
    /// </summary>
    private static ProgressCancel? Slice(ProgressCancel? progress, double from, double to) =>
        progress is null
            ? null
            : new ProgressCancel(
                () => progress.CancelRequested,
                fraction => progress.Report(from + (to - from) * fraction));

    /// <summary>
    /// The imprinted mesh's edges that lie on the intersection curve, as canonical vertex
    /// pairs. Looked up by exact position — the seam points are vertices of both meshes at
    /// exactly the reported coordinates.
    /// </summary>
    private static HashSet<(int, int)> SeamEdges(HalfEdgeMesh mesh, MeshImprint imprint)
    {
        var edges = new HashSet<(int, int)>();
        if (imprint.Segments.Count == 0)
            return edges;

        var byPosition = new Dictionary<Vector3d, int>(mesh.VertexCount);
        foreach (var vertex in mesh.Vertices)
            byPosition[vertex.Position] = vertex.Index;

        foreach (var (start, end) in imprint.Segments)
        {
            if (!byPosition.TryGetValue(imprint.Points[start], out int p) ||
                !byPosition.TryGetValue(imprint.Points[end], out int q))
                throw new InvalidOperationException(
                    "Exact mesh boolean: an intersection point is missing from the imprinted mesh.");
            edges.Add(p < q ? (p, q) : (q, p));
        }
        return edges;
    }

    /// <summary>Per face: whether it lies on the other mesh's boundary, and how.</summary>
    private static Coincidence[] Coincidences(HalfEdgeMesh mesh, CoincidentSurface? coincident)
    {
        var shared = new Coincidence[mesh.FaceCount];
        if (coincident is null)
            return shared; // all Coincidence.None — the transversal case costs nothing
        for (int f = 0; f < mesh.FaceCount; f++)
            shared[f] = coincident.Classify(mesh, f);
        return shared;
    }

    /// <summary>
    /// Flood-fills faces across every edge that is not on the intersection curve, never
    /// merging faces with different <see cref="Coincidence"/>. Coincidence is a patch
    /// boundary in its own right: the coincident region's rim IS imprinted (see
    /// <see cref="CoincidentSurface"/>), so it is normally a seam edge anyway, but making
    /// the flood respect it directly means a probe can never be taken on the very surface
    /// where the winding number is undefined.
    /// </summary>
    private static int[] Patches(
        HalfEdgeMesh mesh, HashSet<(int, int)> seam, Coincidence[] shared, out int patchCount)
    {
        var patch = new int[mesh.FaceCount];
        Array.Fill(patch, -1);
        patchCount = 0;
        var stack = new Stack<int>();
        for (int seed = 0; seed < mesh.FaceCount; seed++)
        {
            if (patch[seed] >= 0)
                continue;
            int id = patchCount++;
            patch[seed] = id;
            stack.Push(seed);
            while (stack.Count > 0)
            {
                int face = stack.Pop();
                // Manual loop walk: Face.HalfEdges() allocates an iterator per call, and the
                // flood visits every face of both operands.
                var start = mesh.GetFace(face).AnyHalfEdge;
                var halfEdge = start;
                do
                {
                    int p = halfEdge.Origin.Index, q = halfEdge.Destination.Index;
                    var twin = halfEdge.Twin;
                    halfEdge = halfEdge.Next;
                    if (seam.Contains(p < q ? (p, q) : (q, p)) || twin.IsBoundary)
                        continue;
                    int neighbour = twin.Face.Index;
                    if (patch[neighbour] >= 0 || shared[neighbour] != shared[face])
                        continue;
                    patch[neighbour] = id;
                    stack.Push(neighbour);
                } while (halfEdge != start);
            }
        }
        return patch;
    }

    /// <summary>
    /// One keep/drop decision per patch. Ordinary patches are decided inside/outside at the
    /// centroid of their largest triangle — the sample furthest from the seam the patch has
    /// to offer. Patches lying on the other solid's boundary bypass the winding number
    /// entirely (it is ½ there) and follow <see cref="KeepCoincident"/>.
    /// </summary>
    private static bool[] Classify(
        HalfEdgeMesh mesh, int[] patch, int patchCount, Coincidence[] shared,
        HalfEdgeMesh otherMesh, bool keepInside, BooleanOperation operation, bool fromA)
    {
        var probe = new int[patchCount];
        var area = new double[patchCount];
        Array.Fill(probe, -1);
        for (int f = 0; f < mesh.FaceCount; f++)
        {
            double faceArea = mesh.GetFace(f).Area;
            if (probe[patch[f]] < 0 || faceArea > area[patch[f]])
            {
                probe[patch[f]] = f;
                area[patch[f]] = faceArea;
            }
        }

        int questions = 0;
        for (int p = 0; p < patchCount; p++)
        {
            if (shared[probe[p]] == Coincidence.None)
                questions++;
        }
        var other = Winding(otherMesh, questions);

        var keep = new bool[patchCount];
        for (int p = 0; p < patchCount; p++)
        {
            var coincidence = shared[probe[p]];
            keep[p] = coincidence == Coincidence.None
                ? other.IsInside(mesh.GetFace(probe[p]).Centroid()) == keepInside
                : KeepCoincident(operation, coincidence, fromA);
        }
        return keep;
    }

    /// <summary>
    /// Roughly how many exact solid-angle queries the Barill multipole hierarchy costs to
    /// build, per triangle. Measured on this benchmark set: the build runs ~0.7 µs per
    /// triangle, an exact query ~20 ns per triangle.
    /// </summary>
    private const int HierarchyBreakEvenQueries = 32;

    /// <summary>
    /// The winding-number oracle for one classification pass, sized to the number of
    /// questions it will actually be asked.
    /// <para>
    /// This is the boolean's biggest measured surprise: a boolean asks ONE question per
    /// surface patch — typically two to eight — and building an O(n log n) multipole
    /// hierarchy to answer eight questions cost 66–86% of the whole classification phase
    /// (43 ms of 65 on two 32k-triangle spheres). The exact sum is O(n) per query with a
    /// tiny constant, so below the break-even the "slow" algorithm is the fast one.
    /// </para>
    /// <para>
    /// Both entry points take the imprinted mesh directly: it is already fully
    /// triangulated, so the public constructor's defensive <c>Triangulated()</c> copy was
    /// pure waste as well.
    /// </para>
    /// </summary>
    private static MeshWindingNumber Winding(HalfEdgeMesh triangulated, int questions) =>
        questions <= HierarchyBreakEvenQueries
            ? MeshWindingNumber.Direct(triangulated)
            : MeshWindingNumber.FromTriangulated(triangulated);

    /// <summary>
    /// The set algebra of surface the two solids share. With normals AGREEING both solids
    /// lie on the same side, so the surface bounds the union and the intersection (one copy
    /// of it) and vanishes from the difference — locally A minus B removes all of A's
    /// material. With normals OPPOSING the solids mate back to back: union and intersection
    /// bury the surface inside the result, while the difference leaves the first solid
    /// untouched and keeps its copy.
    /// <para>
    /// Both meshes cover the region, so exactly one copy can ever survive and it is always
    /// the first mesh's: the second's is redundant when they agree, and back-to-front when
    /// they oppose. (The difference reverses the second mesh's faces, so keeping ITS copy
    /// would also be geometrically right — but keeping A's needs no special case.)
    /// </para>
    /// </summary>
    private static bool KeepCoincident(BooleanOperation operation, Coincidence coincidence, bool fromA)
    {
        if (!fromA)
            return false;
        return coincidence == Coincidence.SameNormal
            ? operation != BooleanOperation.Difference
            : operation == BooleanOperation.Difference;
    }
}
