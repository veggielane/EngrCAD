using EngrCAD.Core;

namespace EngrCAD.Mesh;

public static partial class MeshRepair
{
    /// <summary>
    /// Closes cracks by welding coincident boundary edge PAIRS, in place, with
    /// <see cref="EditableMesh.MergeEdges"/> — the topological complement of vertex
    /// welding.
    /// <para>
    /// Two boundary half-edges pair up when they run in opposite directions with matching
    /// endpoints (within <paramref name="tolerance"/>): that is exactly the head-to-tail
    /// arrangement <c>MergeEdges</c> welds, and it is what a genuine crack looks like from
    /// both sides. Because every merge runs the operator's manifold guards, the tolerance
    /// can be loosened well past what vertex welding tolerates: a merge that would pinch
    /// the surface, duplicate an edge or create a bow-tie is simply refused and the crack
    /// is left open, rather than silently corrupting the mesh. Vertex positions never
    /// move — merged vertices keep the coordinates the file gave them.
    /// </para>
    /// <para>
    /// Candidate pairs come from a spatial hash on the edge midpoints, so the pass is
    /// linear in the number of boundary edges for well-spread geometry. Returns the number
    /// of edge pairs welded (including the doubled boundary edges <c>MergeEdges</c> welds
    /// automatically as it closes a chain).
    /// </para>
    /// </summary>
    public static int MergeCoincidentEdges(EditableMesh mesh, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (tolerance <= 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Crack tolerance must be positive.");

        var boundary = new List<int>();
        foreach (int he in mesh.HalfEdgeIndices())
        {
            if (mesh.IsBoundaryHalfEdge(he))
                boundary.Add(he);
        }
        if (boundary.Count < 2)
            return 0;

        // Spatial hash on midpoints: a crack's two sides have coincident midpoints, and a
        // hash is the same tool the readers and MeshWelder use for the vertex case.
        var cells = new Dictionary<(long, long, long), List<int>>();
        foreach (int he in boundary)
        {
            var key = Cell(Midpoint(mesh, he), tolerance);
            if (!cells.TryGetValue(key, out var bucket))
                cells[key] = bucket = [];
            bucket.Add(he);
        }

        double toleranceSquared = tolerance * tolerance;
        int merged = 0;
        foreach (int keep in boundary)
        {
            // Earlier merges free half-edges (their own, and any doubled boundary edges
            // welded automatically); positions never move, so the hash stays valid.
            if (!mesh.IsHalfEdge(keep) || !mesh.IsBoundaryHalfEdge(keep))
                continue;
            var from = mesh.GetPosition(mesh.Origin(keep));
            var to = mesh.GetPosition(mesh.Destination(keep));
            var cell = Cell(Midpoint(mesh, keep), tolerance);

            for (long dx = -1; dx <= 1 && mesh.IsHalfEdge(keep) && mesh.IsBoundaryHalfEdge(keep); dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        if (!cells.TryGetValue((cell.X + dx, cell.Y + dy, cell.Z + dz), out var bucket))
                            continue;
                        foreach (int discard in bucket)
                        {
                            if (discard == keep || !mesh.IsHalfEdge(discard) || !mesh.IsBoundaryHalfEdge(discard))
                                continue;
                            // MergeEdges identifies the discarded edge's head with the kept
                            // edge's tail and vice versa, so the two boundary half-edges
                            // must run in OPPOSITE directions for the weld to be a crack
                            // closure rather than a fold.
                            if (mesh.GetPosition(mesh.Origin(discard)).DistanceSquaredTo(to) > toleranceSquared ||
                                mesh.GetPosition(mesh.Destination(discard)).DistanceSquaredTo(from) > toleranceSquared)
                                continue;
                            if (mesh.MergeEdges(keep, discard, out var info) != MeshOperationResult.Ok)
                                continue; // a merge that would break manifoldness leaves the crack open
                            merged += 1 + info.ExtraWeldedEdges;
                            goto nextEdge;
                        }
                    }
                }
            }
        nextEdge:;
        }
        return merged;
    }

    private static Vector3d Midpoint(EditableMesh mesh, int halfEdge) =>
        (mesh.GetPosition(mesh.Origin(halfEdge)) + mesh.GetPosition(mesh.Destination(halfEdge))) * 0.5;

    private static (long X, long Y, long Z) Cell(in Vector3d p, double size) => (
        (long)Math.Floor(p.X / size),
        (long)Math.Floor(p.Y / size),
        (long)Math.Floor(p.Z / size));
}
