using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Result of <see cref="MeshPlaneCut.Cut"/>: the cut (optionally capped) mesh, plus the
/// closed cut-boundary loops that lie in the plane. Each loop is an ordered vertex-position
/// cycle wound counter-clockwise when viewed from the plane-normal side (i.e. in the
/// winding a cap face covering it would use).
/// </summary>
public sealed record MeshPlaneCutResult(HalfEdgeMesh Mesh, IReadOnlyList<IReadOnlyList<Vector3d>> CutLoops);

/// <summary>
/// Slices a mesh by a plane and keeps the side the plane normal points <b>away</b> from
/// (material "below" the plane — the convention used when slicing a part for printing).
/// The algorithm follows geometry3Sharp's <c>MeshPlaneCut</c> in shape (edge-plane
/// crossings, triangle splitting, loop extraction) but builds a new mesh instead of
/// mutating: faces fully on the kept side are copied unchanged, crossing faces are
/// clipped against the half-space, and crossing points are computed once per undirected
/// edge from the exact line-plane parameter so shared edges weld exactly at
/// <see cref="HalfEdgeMesh.Build"/> time.
/// </summary>
public static class MeshPlaneCut
{
    /// <summary>
    /// Cuts <paramref name="mesh"/> by the plane through <paramref name="planeOrigin"/>
    /// with unit direction <paramref name="planeNormal"/> (normalized internally), keeping
    /// the half-space the normal points away from.
    /// <para>Semantics and edge cases:</para>
    /// <list type="bullet">
    /// <item>Vertices within <see cref="Tolerance.Default"/> of the plane are treated as
    /// exactly on-plane and are kept at their original positions (never perturbed).</item>
    /// <item>If no vertex lies strictly above the plane, nothing is removed and the
    /// <b>original mesh instance</b> is returned with no cut loops.</item>
    /// <item>If no vertex lies strictly below the plane, the cut would remove everything;
    /// an <see cref="InvalidOperationException"/> is thrown.</item>
    /// <item><paramref name="cap"/> = true triangulates each closed cut loop (ear clipping
    /// projected into the plane), so cutting a closed mesh yields a closed mesh.
    /// Nested loops (annular cut regions, e.g. slicing a tube along its axis) cannot be
    /// capped per-loop and throw <see cref="NotSupportedException"/> — cut with
    /// <paramref name="cap"/> = false and fill those yourself.</item>
    /// <item>Open input meshes are cut, but cut boundaries that merge with pre-existing
    /// boundaries (open spans) do not form on-plane loops and are neither reported nor
    /// capped.</item>
    /// </list>
    /// </summary>
    public static MeshPlaneCutResult Cut(HalfEdgeMesh mesh, in Vector3d planeOrigin, in Vector3d planeNormal, bool cap = true)
    {
        var tol = Tolerance.Default;
        var origin = planeOrigin;                     // locals: `in` params cannot be captured below
        var normal = planeNormal.Normalized(tol);     // throws on zero-length normal

        // 1. Classify every vertex by signed distance to the plane.
        int vertexCount = mesh.VertexCount;
        var distance = new double[vertexCount];
        var side = new int[vertexCount]; // -1 below (kept), 0 on-plane (kept), +1 above (removed)
        bool anyAbove = false, anyBelow = false;
        for (int v = 0; v < vertexCount; v++)
        {
            double d = (mesh.GetPosition(v) - origin).Dot(normal);
            distance[v] = d;
            int s = tol.IsZero(d) ? 0 : d > 0 ? 1 : -1;
            side[v] = s;
            anyAbove |= s > 0;
            anyBelow |= s < 0;
        }

        if (!anyAbove)
            return new MeshPlaneCutResult(mesh, []); // plane clears the mesh: nothing removed
        if (!anyBelow)
            throw new InvalidOperationException(
                "Plane cut removes the entire mesh: every vertex lies on or above the plane.");

        // 2. Emit kept/clipped polygons over a fresh vertex list.
        var (inputPositions, inputFaces) = mesh.ToIndexed();
        var outPositions = new List<Vector3d>();
        var outFaces = new List<int[]>();
        var keptIndex = new int[vertexCount];
        Array.Fill(keptIndex, -1);
        var crossingIndex = new Dictionary<(int Lo, int Hi), int>();

        int MapKept(int v)
        {
            if (keptIndex[v] < 0)
            {
                keptIndex[v] = outPositions.Count;
                outPositions.Add(inputPositions[v]);
            }
            return keptIndex[v];
        }

        int MapCrossing(int a, int b)
        {
            // Canonical (low, high) key: the exact line-plane parameter is evaluated in one
            // fixed direction, so both faces sharing the edge get the bitwise-identical point.
            var key = a < b ? (a, b) : (b, a);
            if (!crossingIndex.TryGetValue(key, out int index))
            {
                double t = distance[key.Item1] / (distance[key.Item1] - distance[key.Item2]);
                index = outPositions.Count;
                outPositions.Add(Vector3d.Lerp(inputPositions[key.Item1], inputPositions[key.Item2], t));
                crossingIndex.Add(key, index);
            }
            return index;
        }

        // Sutherland–Hodgman clip of one polygon against the kept half-space. On-plane
        // vertices count as kept, and a crossing point is generated only for strictly
        // opposite signs, so no duplicate points arise.
        void ClipLoop(ReadOnlySpan<int> loop)
        {
            var clipped = new List<int>(loop.Length + 2);
            for (int i = 0; i < loop.Length; i++)
            {
                int a = loop[i];
                int b = loop[(i + 1) % loop.Length];
                if (side[a] <= 0)
                    clipped.Add(MapKept(a));
                if (side[a] * side[b] < 0)
                    clipped.Add(MapCrossing(a, b));
            }
            if (clipped.Count >= 3)
                outFaces.Add([.. clipped]);
        }

        foreach (var loop in inputFaces)
        {
            int n = loop.Length;
            int below = 0, above = 0, crossings = 0;
            for (int i = 0; i < n; i++)
            {
                if (side[loop[i]] < 0) below++;
                else if (side[loop[i]] > 0) above++;
                if (side[loop[i]] * side[loop[(i + 1) % n]] < 0) crossings++;
            }

            if (above == 0)
            {
                // Fully on the kept side (possibly touching the plane): copy unchanged.
                var copy = new int[n];
                for (int i = 0; i < n; i++)
                    copy[i] = MapKept(loop[i]);
                outFaces.Add(copy);
            }
            else if (below == 0)
            {
                // Fully on the removed side (possibly touching the plane): drop.
            }
            else if (crossings <= 2)
            {
                ClipLoop(loop);
            }
            else
            {
                // A (necessarily non-convex) polygon crossing the plane more than twice:
                // single-polygon Sutherland–Hodgman would bridge the separate kept pieces,
                // and a vertex-0 fan is only valid for polygons star-shaped from that
                // vertex. Triangulate the face properly in its own plane and clip each
                // piece (each is convex, so it crosses at most twice); diagonal crossings
                // share MapCrossing keys, so the pieces still weld exactly.
                foreach (var piece in TriangulateFacePolygon(loop, inputPositions))
                    ClipLoop(piece);
            }
        }

        var openMesh = HalfEdgeMesh.Build(outPositions, outFaces);

        // 3. Cut loops = boundary loops of the open result that lie entirely in the plane
        //    (pre-existing boundaries of an open input survive but are not cut loops).
        //    A boundary half-edge runs opposite to its interior twin, so walk order is
        //    exactly the winding a cap face must use (CCW viewed from the normal side).
        var loopVertexIndices = new List<int[]>();
        foreach (var boundaryLoop in openMesh.BoundaryLoops())
        {
            bool onPlane = boundaryLoop.All(he => tol.IsZero((he.Origin.Position - origin).Dot(normal)));
            if (onPlane)
                loopVertexIndices.Add([.. boundaryLoop.Select(he => he.Origin.Index)]);
        }

        var cutLoops = new List<IReadOnlyList<Vector3d>>(loopVertexIndices.Count);
        foreach (var loop in loopVertexIndices)
            cutLoops.Add([.. loop.Select(v => outPositions[v])]);

        if (!cap || loopVertexIndices.Count == 0)
            return new MeshPlaneCutResult(openMesh, cutLoops);

        // 4. Cap: triangulate each loop in a plane basis chosen so that CCW in (u, v)
        //    faces +normal — matching both the loop winding and the outward orientation.
        var u = normal.ArbitraryPerpendicular(tol);
        var v2 = normal.Cross(u); // unit; u × v2 = normal

        foreach (var loop in loopVertexIndices)
        {
            var projected = new Vector2d[loop.Length];
            for (int i = 0; i < loop.Length; i++)
            {
                var p = outPositions[loop[i]] - origin;
                projected[i] = (p.Dot(u), p.Dot(v2));
            }

            if (PolygonTriangulator.SignedArea(projected) < 0)
                throw new NotSupportedException(
                    "The cut produced a nested (hole) loop — an annular cut region. Capping nested " +
                    "loops needs hole-aware planar filling, which is not supported yet; cut with " +
                    "cap: false and fill the returned loops yourself.");

            outFaces.AddRange(TriangulateWithChordZip(loop, projected));
        }

        return new MeshPlaneCutResult(HalfEdgeMesh.Build(outPositions, outFaces), cutLoops);
    }

    /// <summary>
    /// Triangulates one (possibly non-convex) face loop in its own plane, returning each
    /// piece as indices into <paramref name="positions"/>. The projection basis is derived
    /// from the Newell normal, so the projected loop is CCW and the pieces keep the face's
    /// orientation.
    /// </summary>
    private static List<int[]> TriangulateFacePolygon(int[] loop, IReadOnlyList<Vector3d> positions)
    {
        // Newell normal: robust for non-convex (and slightly non-planar) polygons.
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < loop.Length; i++)
        {
            var p = positions[loop[i]];
            var q = positions[loop[(i + 1) % loop.Length]];
            nx += (p.Y - q.Y) * (p.Z + q.Z);
            ny += (p.Z - q.Z) * (p.X + q.X);
            nz += (p.X - q.X) * (p.Y + q.Y);
        }
        var normal = new Vector3d(nx, ny, nz).Normalized(Tolerance.Default); // zero area throws
        var u = normal.ArbitraryPerpendicular(Tolerance.Default);
        var v = normal.Cross(u); // u × v = normal → the loop projects CCW

        var projected = new Vector2d[loop.Length];
        for (int i = 0; i < loop.Length; i++)
        {
            var p = positions[loop[i]];
            projected[i] = (p.Dot(u), p.Dot(v));
        }
        return TriangulateWithChordZip(loop, projected);
    }

    /// <summary>
    /// Ear-clips a CCW projected loop into triangles over the loop's vertex indices.
    /// Earcut filters exactly-collinear boundary vertices, but neighboring geometry (cut
    /// walls, adjacent faces) still references them — so any triangle edge that is a chord
    /// spanning a run of dropped vertices is expanded back into the loop's full vertex run
    /// (the seam-zip lesson from design.md §3), keeping subdivisions identical for exact
    /// welding.
    /// </summary>
    private static List<int[]> TriangulateWithChordZip(int[] loop, Vector2d[] projected)
    {
        var triangles = PolygonTriangulator.Triangulate(projected);
        var pieces = new List<int[]>(triangles.Count);

        var used = new bool[loop.Length];
        foreach (var (a, b, c) in triangles)
            used[a] = used[b] = used[c] = true;
        bool anyDropped = Array.IndexOf(used, false) >= 0;

        foreach (var (a, b, c) in triangles)
        {
            if (!anyDropped)
            {
                pieces.Add([loop[a], loop[b], loop[c]]);
                continue;
            }

            var polygon = new List<int>(6);
            AppendEdge(a, b);
            AppendEdge(b, c);
            AppendEdge(c, a);
            pieces.Add([.. polygon]);

            void AppendEdge(int from, int to)
            {
                polygon.Add(loop[from]);
                int n = loop.Length;
                int gap = (to - from + n) % n;
                if (gap <= 1)
                    return; // genuine loop edge (or interior diagonal handled below)
                for (int k = 1; k < gap; k++)
                {
                    if (used[(from + k) % n])
                        return; // interior diagonal, not a chord over dropped vertices
                }
                for (int k = 1; k < gap; k++)
                    polygon.Add(loop[(from + k) % n]);
            }
        }
        return pieces;
    }
}
