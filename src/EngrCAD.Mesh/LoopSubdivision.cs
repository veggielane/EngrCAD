using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Loop subdivision for triangle meshes (with Warren boundary rules): each triangle
/// splits into four, positions move toward the C² limit surface. Faces must all be
/// triangles — call <see cref="HalfEdgeMesh.Triangulated"/> first for polygon meshes.
/// </summary>
public static class LoopSubdivision
{
    /// <summary>Subdivides <paramref name="mesh"/> <paramref name="iterations"/> times.</summary>
    /// <param name="mesh">A pure triangle mesh.</param>
    /// <param name="iterations">How many times to subdivide.</param>
    /// <param name="preserveBoundary">
    /// Keep every boundary vertex exactly where it is, instead of applying the Warren
    /// boundary rule (which smooths the outline toward the limit curve and therefore MOVES
    /// it). New boundary vertices are edge midpoints either way, so with this on the refined
    /// outline is a subdivision of the original polyline rather than a different curve near
    /// it. That is what an open patch needs when it has to be stitched back into a
    /// surrounding mesh: <see cref="MeshRegionOperator"/> accepts a rim that was subdivided
    /// but not one that moved, so Loop subdivision round-trips through a region operator only
    /// with this set.
    /// </param>
    public static HalfEdgeMesh Subdivide(HalfEdgeMesh mesh, int iterations = 1, bool preserveBoundary = false)
    {
        if (iterations < 0) throw new ArgumentOutOfRangeException(nameof(iterations));
        for (int i = 0; i < iterations; i++)
            mesh = SubdivideOnce(mesh, preserveBoundary);
        return mesh;
    }

    private static HalfEdgeMesh SubdivideOnce(HalfEdgeMesh mesh, bool preserveBoundary)
    {
        foreach (var face in mesh.Faces)
        {
            if (face.Degree != 3)
                throw new ArgumentException("Loop subdivision requires a pure triangle mesh.");
        }

        int vertexCount = mesh.VertexCount;
        var newPositions = new Vector3d[vertexCount + mesh.EdgeCount];

        // One new vertex per undirected edge.
        var edgeIndexOfHalfEdge = new int[mesh.HalfEdgeCount];
        int edgeCounter = 0;
        foreach (var he in mesh.Edges)
        {
            edgeIndexOfHalfEdge[he.Index] = edgeCounter;
            edgeIndexOfHalfEdge[he.Twin.Index] = edgeCounter;

            var a = he.Origin.Position;
            var b = he.Destination.Position;
            Vector3d point;
            if (he.IsBoundaryEdge)
            {
                point = (a + b) * 0.5;
            }
            else
            {
                // Opposite vertices of the two incident triangles.
                var c = he.Prev.Origin.Position;
                var d = he.Twin.Prev.Origin.Position;
                point = (a + b) * (3.0 / 8.0) + (c + d) * (1.0 / 8.0);
            }
            newPositions[vertexCount + edgeCounter] = point;
            edgeCounter++;
        }

        // Reposition original vertices.
        foreach (var vertex in mesh.Vertices)
        {
            var p = vertex.Position;
            if (vertex.IsIsolated)
            {
                newPositions[vertex.Index] = p;
                continue;
            }

            if (vertex.IsBoundary)
            {
                if (preserveBoundary)
                {
                    // Pinned: the refined outline is a subdivision of the original polyline
                    // (new vertices are edge midpoints), not a smoothed curve near it.
                    newPositions[vertex.Index] = p;
                    continue;
                }

                // Boundary rule: only the two neighbors along the boundary participate.
                var boundaryNeighbors = vertex.OutgoingHalfEdges()
                    .Where(h => h.IsBoundaryEdge)
                    .Select(h => h.Destination.Position)
                    .ToList();
                if (boundaryNeighbors.Count != 2)
                    throw new InvalidOperationException(
                        $"Vertex {vertex.Index} has {boundaryNeighbors.Count} boundary edges; expected 2 on a manifold boundary.");
                newPositions[vertex.Index] = p * 0.75 + (boundaryNeighbors[0] + boundaryNeighbors[1]) * 0.125;
            }
            else
            {
                int n = 0;
                var sum = Vector3d.Zero;
                foreach (var neighbor in vertex.Neighbors())
                {
                    sum += neighbor.Position;
                    n++;
                }
                double k = 3.0 / 8.0 + Math.Cos(2 * Math.PI / n) / 4.0;
                double beta = (5.0 / 8.0 - k * k) / n;
                newPositions[vertex.Index] = p * (1 - n * beta) + sum * beta;
            }
        }

        // Each triangle (v0, v1, v2) with edge midpoints m01, m12, m20 → 4 triangles.
        var faces = new List<int[]>(mesh.FaceCount * 4);
        foreach (var face in mesh.Faces)
        {
            var h0 = face.AnyHalfEdge;
            var h1 = h0.Next;
            var h2 = h1.Next;
            int v0 = h0.Origin.Index;
            int v1 = h1.Origin.Index;
            int v2 = h2.Origin.Index;
            int m01 = vertexCount + edgeIndexOfHalfEdge[h0.Index];
            int m12 = vertexCount + edgeIndexOfHalfEdge[h1.Index];
            int m20 = vertexCount + edgeIndexOfHalfEdge[h2.Index];

            faces.Add([v0, m01, m20]);
            faces.Add([v1, m12, m01]);
            faces.Add([v2, m20, m12]);
            faces.Add([m01, m12, m20]);
        }

        return HalfEdgeMesh.Build(newPositions, faces);
    }
}
