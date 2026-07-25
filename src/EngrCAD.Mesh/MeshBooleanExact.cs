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
/// </summary>
internal static class MeshBooleanExact
{
    public static HalfEdgeMesh Combine(HalfEdgeMesh a, HalfEdgeMesh b, BooleanOperation operation)
    {
        if (!a.IsClosed || !b.IsClosed)
            throw new ArgumentException("Boolean operations require closed meshes.");

        var imprint = MeshMeshCut.Imprint(a, b);
        var seamA = SeamEdges(imprint.MeshA, imprint);
        var seamB = SeamEdges(imprint.MeshB, imprint);

        // Union keeps what is outside the other solid, intersection what is inside;
        // difference keeps A's outside plus B's inside, flipped so the tool's surface
        // faces into the removed material.
        bool keepAInside = operation == BooleanOperation.Intersection;
        bool keepBInside = operation != BooleanOperation.Union;
        bool reverseB = operation == BooleanOperation.Difference;

        var positions = new List<Vector3d>();
        var index = new Dictionary<Vector3d, int>();
        var faces = new List<int[]>();
        Emit(imprint.MeshA, seamA, new MeshWindingNumber(imprint.MeshB), keepAInside, reverse: false);
        Emit(imprint.MeshB, seamB, new MeshWindingNumber(imprint.MeshA), keepBInside, reverseB);
        return HalfEdgeMesh.Build(positions, faces);

        void Emit(HalfEdgeMesh mesh, HashSet<(int, int)> seam, MeshWindingNumber other, bool keepInside, bool reverse)
        {
            var patch = Patches(mesh, seam, out int patchCount);
            var inside = Classify(mesh, patch, patchCount, other);
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                if (inside[patch[f]] != keepInside)
                    continue;
                var loop = mesh.GetFace(f).Vertices().Select(v => Intern(v.Position)).ToArray();
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

    /// <summary>Flood-fills faces across every edge that is not on the intersection curve.</summary>
    private static int[] Patches(HalfEdgeMesh mesh, HashSet<(int, int)> seam, out int patchCount)
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
                foreach (var halfEdge in mesh.GetFace(face).HalfEdges())
                {
                    int p = halfEdge.Origin.Index, q = halfEdge.Destination.Index;
                    if (seam.Contains(p < q ? (p, q) : (q, p)))
                        continue;
                    var twin = halfEdge.Twin;
                    if (twin.IsBoundary)
                        continue;
                    int neighbour = twin.Face.Index;
                    if (patch[neighbour] >= 0)
                        continue;
                    patch[neighbour] = id;
                    stack.Push(neighbour);
                }
            }
        }
        return patch;
    }

    /// <summary>
    /// One inside/outside decision per patch, taken at the centroid of its largest
    /// triangle — the sample furthest from the seam the patch has to offer.
    /// </summary>
    private static bool[] Classify(HalfEdgeMesh mesh, int[] patch, int patchCount, MeshWindingNumber other)
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

        var inside = new bool[patchCount];
        for (int p = 0; p < patchCount; p++)
            inside[p] = other.IsInside(mesh.GetFace(probe[p]).Centroid());
        return inside;
    }
}
