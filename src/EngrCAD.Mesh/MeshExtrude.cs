using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Extrusion operations on half-edge meshes, construct-new (topology is immutable after
/// <see cref="HalfEdgeMesh.Build"/>) — the counterparts of geometry3Sharp's
/// <c>MeshExtrudeFaces</c> and <c>MeshExtrudeMesh</c>:
/// <see cref="Faces(HalfEdgeMesh, IEnumerable{int}, in Vector3d)"/> pulls a face patch off
/// the mesh and stitches side walls along the patch boundary, and
/// <see cref="Thicken"/> turns an open surface into a closed solid shell by offsetting a
/// reversed copy along vertex normals and stitching the boundary loops — the direct-mesh
/// complement of the SDF <c>Shell</c> operator.
/// </summary>
public static class MeshExtrude
{
    /// <summary>
    /// Extrudes the faces in <paramref name="faceIndices"/> by the uniform
    /// <paramref name="offset"/> vector: the patch faces move to the offset position,
    /// vertices shared with the rest of the mesh are duplicated, and side-wall quads are
    /// stitched along the patch boundary. Winding falls out of the half-edge structure
    /// (each wall supplies exactly the two directed edges freed by moving the patch), so a
    /// closed mesh stays closed and outward-oriented. Multiple disconnected patch regions
    /// are fine — each gets its own walls. Face indices of the input survive unchanged
    /// (patch faces are retargeted in place); wall quads are appended after them.
    /// </summary>
    public static HalfEdgeMesh Faces(HalfEdgeMesh mesh, IEnumerable<int> faceIndices, in Vector3d offset)
    {
        var patch = ValidatedPatch(mesh, faceIndices);
        var offsets = new Vector3d[mesh.VertexCount];
        Array.Fill(offsets, offset);
        return ExtrudePatch(mesh, patch, offsets);
    }

    /// <summary>
    /// Extrudes the faces in <paramref name="faceIndices"/> by
    /// <paramref name="distance"/> along per-vertex normals, where each patch vertex's
    /// normal is area-weighted over its incident <b>patch</b> faces only (so extruding a
    /// single planar face moves it exactly along the face normal, and joints between
    /// non-coplanar patch faces get the averaged direction). Throws when a patch vertex's
    /// normal is degenerate.
    /// </summary>
    public static HalfEdgeMesh Faces(HalfEdgeMesh mesh, IEnumerable<int> faceIndices, double distance)
    {
        var patch = ValidatedPatch(mesh, faceIndices);

        // Area-weighted patch-only vertex normals.
        var accumulated = new Vector3d[mesh.VertexCount];
        foreach (int f in patch)
        {
            var normal = mesh.FaceNormalRaw(f);
            foreach (var v in mesh.GetFace(f).Vertices())
                accumulated[v.Index] += normal;
        }

        var offsets = new Vector3d[mesh.VertexCount];
        foreach (int f in patch)
        {
            foreach (var v in mesh.GetFace(f).Vertices())
            {
                if (!accumulated[v.Index].TryNormalize(Tolerance.Default, out var n))
                    throw new InvalidOperationException(
                        $"Vertex {v.Index} has a degenerate patch normal; cannot extrude by distance.");
                offsets[v.Index] = n * distance;
            }
        }
        return ExtrudePatch(mesh, patch, offsets);
    }

    /// <summary>
    /// Thickens a surface into a solid shell: the input faces stay put as the front skin,
    /// a reversed copy is offset by <paramref name="thickness"/> <b>against</b> the vertex
    /// normals (material behind the surface), and each boundary loop is stitched with a
    /// quad band — g3's <c>MeshExtrudeMesh</c>. An open surface (plane patch, hemisphere)
    /// becomes a closed slab/shell; a closed mesh becomes a hollow two-shell solid whose
    /// volume is the wall volume. Vertex normals are the mesh's area-weighted normals
    /// (<see cref="HalfEdgeMesh.ComputeVertexNormals"/>); a degenerate normal on a used
    /// vertex throws. Output face order: original faces, reversed offset copies, then
    /// stitch quads.
    /// </summary>
    public static HalfEdgeMesh Thicken(HalfEdgeMesh mesh, double thickness)
    {
        if (!(thickness > 0))
            throw new ArgumentOutOfRangeException(nameof(thickness), "Thickness must be positive.");
        if (mesh.FaceCount == 0)
            throw new ArgumentException("Cannot thicken an empty mesh.", nameof(mesh));

        var normals = mesh.ComputeVertexNormals();
        var (positions, faces) = mesh.ToIndexed();
        int vertexCount = positions.Length;

        var outPositions = new List<Vector3d>(2 * vertexCount);
        outPositions.AddRange(positions);
        for (int v = 0; v < vertexCount; v++)
        {
            // Exact sentinel test, not a float comparison: ComputeVertexNormals returns
            // bit-exact Vector3d.Zero for degenerate/isolated vertices.
            if (normals[v] == Vector3d.Zero && !mesh.GetVertex(v).IsIsolated)
                throw new InvalidOperationException(
                    $"Vertex {v} has a degenerate normal; cannot offset the back skin.");
            outPositions.Add(positions[v] - normals[v] * thickness);
        }

        var outFaces = new List<int[]>(2 * faces.Count);
        outFaces.AddRange(faces);
        foreach (var face in faces)
        {
            var reversed = new int[face.Length];
            for (int i = 0; i < face.Length; i++)
                reversed[i] = face[face.Length - 1 - i] + vertexCount;
            outFaces.Add(reversed);
        }

        // Stitch each boundary loop: for boundary half-edge o→d (the free directed edge on
        // the front skin), the reversed back skin frees d'→o', so quad [o, d, d', o']
        // supplies both.
        foreach (var loop in mesh.BoundaryLoops())
        {
            foreach (var he in loop)
            {
                int o = he.Origin.Index;
                int d = he.Destination.Index;
                outFaces.Add([o, d, d + vertexCount, o + vertexCount]);
            }
        }

        return HalfEdgeMesh.Build(outPositions, outFaces);
    }

    // ---- internals ----

    private static HashSet<int> ValidatedPatch(HalfEdgeMesh mesh, IEnumerable<int> faceIndices)
    {
        ArgumentNullException.ThrowIfNull(faceIndices);
        var patch = new HashSet<int>();
        foreach (int f in faceIndices)
        {
            if (f < 0 || f >= mesh.FaceCount)
                throw new ArgumentOutOfRangeException(nameof(faceIndices),
                    $"Face index {f} is out of range (mesh has {mesh.FaceCount} faces).");
            patch.Add(f);
        }
        if (patch.Count == 0)
            throw new ArgumentException("At least one face is required.", nameof(faceIndices));
        return patch;
    }

    /// <summary>
    /// Core construct-new extrusion: patch vertices also used outside the patch (by
    /// non-patch faces or along the open mesh boundary) are duplicated at the offset
    /// position, interior patch vertices are moved in place, patch faces are retargeted,
    /// and each patch-boundary half-edge a→b gains the wall quad [a, b, b′, a′] — which
    /// supplies exactly the directed edge a→b vacated by the moved patch face and the
    /// directed edge b′→a′ its moved twin needs.
    /// </summary>
    private static HalfEdgeMesh ExtrudePatch(HalfEdgeMesh mesh, HashSet<int> patch, Vector3d[] offsets)
    {
        var (positions, faces) = mesh.ToIndexed();
        var outPositions = new List<Vector3d>(positions);

        // A patch vertex keeps its original when anything outside the patch still needs it:
        // a non-patch face, or a mesh-boundary edge of a patch face (walls reference it).
        var keepOriginal = new bool[mesh.VertexCount];
        for (int f = 0; f < faces.Count; f++)
        {
            if (patch.Contains(f))
                continue;
            foreach (int v in faces[f])
                keepOriginal[v] = true;
        }
        var boundaryEdges = new List<(int From, int To)>(); // patch-boundary directed edges a→b
        foreach (int f in patch)
        {
            foreach (var he in mesh.GetFace(f).HalfEdges())
            {
                var twin = he.Twin;
                bool isPatchBoundary = twin.IsBoundary || !patch.Contains(mesh.HeFace(twin.Index));
                if (!isPatchBoundary)
                    continue;
                int a = he.Origin.Index;
                int b = he.Destination.Index;
                boundaryEdges.Add((a, b));
                keepOriginal[a] = true;
                keepOriginal[b] = true;
            }
        }

        // Duplicate boundary-of-patch vertices at the offset position; move interior patch
        // vertices in place.
        var duplicate = new int[mesh.VertexCount];
        Array.Fill(duplicate, -1);
        foreach (int f in patch)
        {
            foreach (int v in faces[f])
            {
                if (keepOriginal[v])
                {
                    if (duplicate[v] < 0)
                    {
                        duplicate[v] = outPositions.Count;
                        outPositions.Add(positions[v] + offsets[v]);
                    }
                }
                else
                {
                    outPositions[v] = positions[v] + offsets[v];
                }
            }
        }

        // Retarget patch faces (face indices preserved), then append walls.
        var outFaces = new List<int[]>(faces.Count + boundaryEdges.Count);
        for (int f = 0; f < faces.Count; f++)
        {
            if (!patch.Contains(f))
            {
                outFaces.Add(faces[f]);
                continue;
            }
            var loop = faces[f];
            var mapped = new int[loop.Length];
            for (int i = 0; i < loop.Length; i++)
                mapped[i] = duplicate[loop[i]] >= 0 ? duplicate[loop[i]] : loop[i];
            outFaces.Add(mapped);
        }
        foreach (var (a, b) in boundaryEdges)
            outFaces.Add([a, b, duplicate[b], duplicate[a]]);

        return HalfEdgeMesh.Build(outPositions, outFaces);
    }
}
