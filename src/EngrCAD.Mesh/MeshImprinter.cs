using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// What one mesh has to gain so the intersection curve becomes part of it: seam points
/// that already coincide with a vertex, points that sit on an edge, points that sit
/// inside a face, and the segments joining them. Built by <see cref="MeshMeshCut"/>,
/// consumed by <see cref="MeshImprinter"/>.
/// </summary>
internal sealed class ImprintPlan
{
    /// <summary>Seam points that ARE an existing mesh vertex (point id → vertex).</summary>
    public Dictionary<int, int> PointVertex { get; } = [];

    /// <summary>Half-edge → the points to insert along it, with the parameter from its origin.</summary>
    public Dictionary<int, List<(double T, int Point)>> EdgePoints { get; } = [];

    /// <summary>Face → seam points known to lie on it, not yet located precisely.</summary>
    public Dictionary<int, List<int>> FacePoints { get; } = [];

    /// <summary>Face → intersection segments crossing it, as seam point id pairs.</summary>
    public Dictionary<int, List<(int P, int Q)>> FaceSegments { get; } = [];

    /// <summary>
    /// Existing vertices that must move onto a shared seam coordinate (see
    /// <c>MeshMeshCut.SnapToExistingVertices</c>). Displacements are bounded by the
    /// relative degeneracy guard.
    /// </summary>
    public Dictionary<int, Vector3d> VertexMoves { get; } = [];

    public void MapToVertex(int point, int vertex) => PointVertex[point] = vertex;

    public void MoveVertex(int vertex, in Vector3d position) => VertexMoves[vertex] = position;

    public void AddEdgePoint(int halfEdge, double t, int point)
    {
        if (!EdgePoints.TryGetValue(halfEdge, out var list))
            EdgePoints[halfEdge] = list = [];
        foreach (var entry in list)
        {
            if (entry.Point == point)
                return;
        }
        list.Add((t, point));
    }

    public void AddFacePoint(int face, int point)
    {
        if (!FacePoints.TryGetValue(face, out var list))
            FacePoints[face] = list = [];
        if (!list.Contains(point))
            list.Add(point);
    }

    public void AddSegment(int face, int p, int q)
    {
        if (!FaceSegments.TryGetValue(face, out var list))
            FaceSegments[face] = list = [];
        if (!list.Contains((p, q)) && !list.Contains((q, p)))
            list.Add((p, q));
    }
}

/// <summary>
/// Cuts one mesh along a set of intersection segments using nothing but the guarded Euler
/// operators of <see cref="EditableMesh"/>: <c>SplitEdge</c> for points on edges,
/// <c>PokeFace</c> for points inside a face, and <c>FlipEdge</c> for constraint recovery
/// (Anglada's flip algorithm — after the points are in, the missing segment is obtained by
/// flipping the edges that cross it). Splitting an edge updates BOTH adjacent faces, which
/// is what keeps the mesh free of T-junctions while it is being cut.
/// <para>
/// Every seam vertex is written at the exact shared intersection coordinate after the
/// split (the operator's lerp is only used to pick a valid topological parameter), so the
/// two meshes of an imprint end up bit-identical along the seam.
/// </para>
/// <para>
/// The whole cut runs inside one <see cref="MeshChangeSet"/>: any refusal or geometric
/// surprise reverts the journal, leaving the mesh bit-identical to its pre-cut state
/// instead of half-cut.
/// </para>
/// </summary>
internal static class MeshImprinter
{
    // A stalled flip recovery must fail loudly rather than spin; the bound is far above
    // anything a real face's constraint set needs (crossings are a handful per face).
    private const int MaxRecoveryIterations = 4096;

    public static HalfEdgeMesh Imprint(
        HalfEdgeMesh mesh, ImprintPlan plan, IReadOnlyList<Vector3d> points, double epsilon, string label)
    {
        var editable = EditableMesh.FromMesh(mesh);
        if (!TryImprint(editable, plan, points, epsilon, out string? error))
            throw new InvalidOperationException(
                $"Exact mesh boolean: cutting the {label} mesh along the intersection curve failed " +
                $"({error}). The mesh was rolled back unchanged.");
        return editable.ToMesh();
    }

    /// <summary>
    /// Applies the plan transactionally. On failure the mesh is reverted through the
    /// change-set journal — bit-identical to its state on entry — and the reason is
    /// reported instead of thrown.
    /// </summary>
    internal static bool TryImprint(
        EditableMesh mesh, ImprintPlan plan, IReadOnlyList<Vector3d> points, double epsilon, out string? error)
    {
        var journal = mesh.BeginChangeSet();
        try
        {
            Run(mesh, plan, points, epsilon);
            mesh.EndChangeSet();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            mesh.EndChangeSet();
            journal.Revert(mesh);
            error = ex.Message;
            return false;
        }
    }

    private static void Run(EditableMesh mesh, ImprintPlan plan, IReadOnlyList<Vector3d> points, double epsilon)
    {
        // Vertices that both meshes claim for one seam point move first: face frames and
        // every geometric decision below must see the reconciled positions.
        foreach (var (vertex, position) in plan.VertexMoves)
            mesh.SetPosition(vertex, position);

        // Face geometry is captured before the first cut: fragments stay coplanar with the
        // face that produced them, so one frame per original face serves the whole cut.
        var frames = new Dictionary<int, FaceFrame>();
        foreach (int face in plan.FaceSegments.Keys)
            frames[face] = FaceFrame.Of(mesh, face);

        var vertexOf = new Dictionary<int, int>(plan.PointVertex);
        var origin = new Dictionary<int, int>();   // current face → the face it was cut from
        var fragments = new Dictionary<int, List<int>>();
        foreach (int face in plan.FaceSegments.Keys)
            fragments[face] = [face];

        int OriginOf(int face) => origin.TryGetValue(face, out int o) ? o : face;
        void Track(int newFace, int parent)
        {
            if (newFace < 0)
                return;
            int root = OriginOf(parent);
            origin[newFace] = root;
            if (fragments.TryGetValue(root, out var list))
                list.Add(newFace);
        }

        PlacePoints(mesh, plan, points, epsilon, frames, vertexOf, out var interiorPoints);
        InsertEdgePoints(mesh, plan, points, epsilon, vertexOf, Track);
        InsertInteriorPoints(mesh, points, epsilon, frames, fragments, OriginOf, vertexOf, interiorPoints, Track);
        RecoverSegments(mesh, plan, epsilon, frames, OriginOf, vertexOf);
    }

    // ------------------------------------------------------------------ 1. placement

    /// <summary>
    /// Decides, for every seam point not already pinned to an edge, whether it coincides
    /// with a corner of its host face, lies on one of its edges, or is strictly interior.
    /// A point is tested against EVERY face that reported it and the sharpest outcome wins
    /// (corner beats edge beats interior) — a point placed inside one face but on the
    /// shared edge of its neighbour would leave a T-junction.
    /// </summary>
    private static void PlacePoints(
        EditableMesh mesh, ImprintPlan plan, IReadOnlyList<Vector3d> points, double epsilon,
        Dictionary<int, FaceFrame> frames, Dictionary<int, int> vertexOf,
        out Dictionary<int, List<int>> interiorPoints)
    {
        var pinned = new HashSet<int>();
        foreach (var list in plan.EdgePoints.Values)
        {
            foreach (var (_, point) in list)
                pinned.Add(point);
        }

        var hosts = new Dictionary<int, List<int>>();
        foreach (var (face, ids) in plan.FacePoints)
        {
            foreach (int id in ids)
            {
                if (!hosts.TryGetValue(id, out var list))
                    hosts[id] = list = [];
                list.Add(face);
            }
        }

        interiorPoints = [];
        foreach (var (id, faces) in hosts)
        {
            if (vertexOf.ContainsKey(id) || pinned.Contains(id))
                continue;
            var p = points[id];
            int onEdge = -1;
            double edgeParameter = 0;
            int host = faces[0];
            bool placed = false;
            foreach (int face in faces)
            {
                foreach (int he in frames[face].HalfEdges)
                {
                    if (p.DistanceSquaredTo(mesh.GetPosition(mesh.Origin(he))) <= epsilon * epsilon)
                    {
                        vertexOf[id] = mesh.Origin(he);
                        placed = true;
                        break;
                    }
                    if (onEdge < 0 && OnSegment(mesh, he, p, epsilon, out double t))
                    {
                        onEdge = he;
                        edgeParameter = t;
                    }
                }
                if (placed)
                    break;
            }
            if (placed)
                continue;
            if (onEdge >= 0)
            {
                plan.AddEdgePoint(onEdge, edgeParameter, id);
                continue;
            }
            if (!interiorPoints.TryGetValue(host, out var interior))
                interiorPoints[host] = interior = [];
            interior.Add(id);
        }
    }

    private static bool OnSegment(EditableMesh mesh, int halfEdge, in Vector3d p, double epsilon, out double t)
    {
        var a = mesh.GetPosition(mesh.Origin(halfEdge));
        var b = mesh.GetPosition(mesh.Destination(halfEdge));
        var ab = b - a;
        double lengthSquared = ab.LengthSquared;
        t = 0;
        if (lengthSquared <= 0)
            return false;
        t = (p - a).Dot(ab) / lengthSquared;
        if (t <= 0 || t >= 1)
            return false;
        return (p - (a + ab * t)).LengthSquared <= epsilon * epsilon;
    }

    // ------------------------------------------------------------------ 2. edge points

    private static void InsertEdgePoints(
        EditableMesh mesh, ImprintPlan plan, IReadOnlyList<Vector3d> points, double epsilon,
        Dictionary<int, int> vertexOf, Action<int, int> track)
    {
        // One list per undirected edge: the two faces sharing an edge report it through
        // opposite half-edges, so parameters are mirrored onto the lower-indexed one.
        var byEdge = new Dictionary<int, List<(double T, int Point)>>();
        foreach (var (halfEdge, insertions) in plan.EdgePoints)
        {
            int twin = mesh.Twin(halfEdge);
            bool canonical = halfEdge <= twin;
            int key = canonical ? halfEdge : twin;
            if (!byEdge.TryGetValue(key, out var list))
                byEdge[key] = list = [];
            foreach (var (t, point) in insertions)
            {
                double parameter = canonical ? t : 1 - t;
                bool duplicate = false;
                foreach (var entry in list)
                    duplicate |= entry.Point == point;
                if (!duplicate)
                    list.Add((parameter, point));
            }
        }

        foreach (var (halfEdge, insertions) in byEdge)
        {
            insertions.Sort((x, y) => x.T.CompareTo(y.T));
            int destination = mesh.Destination(halfEdge);
            double edgeLength = mesh.GetPosition(destination)
                .DistanceTo(mesh.GetPosition(mesh.Origin(halfEdge)));
            double endMargin = edgeLength > 0 ? epsilon / edgeLength : 0.5;

            int cursor = halfEdge;
            double consumed = 0;
            foreach (var (t, point) in insertions)
            {
                if (vertexOf.ContainsKey(point))
                    continue;
                // Ends snap to the existing vertices instead of producing a zero-length
                // sliver edge; the crossing test already keeps genuine crossings clear of
                // the corners by the same margin.
                if (t <= endMargin)
                {
                    vertexOf[point] = mesh.Origin(cursor);
                    continue;
                }
                if (t >= 1 - endMargin)
                {
                    vertexOf[point] = destination;
                    continue;
                }

                double local = (t - consumed) / (1 - consumed);
                if (!(local > 0 && local < 1))
                    throw new InvalidOperationException(
                        $"two intersection points collapse onto the same spot on edge {halfEdge}");

                int left = mesh.FaceOf(cursor);
                int right = mesh.FaceOf(mesh.Twin(cursor));
                var outcome = mesh.SplitEdge(cursor, out var info, local);
                if (outcome != MeshOperationResult.Ok)
                    throw new InvalidOperationException($"SplitEdge refused ({outcome}) on edge {cursor}");
                // The split lands the vertex on the chord; move it to the exact
                // intersection coordinate both meshes share.
                mesh.SetPosition(info.NewVertex, points[point]);
                vertexOf[point] = info.NewVertex;
                track(info.NewFaceLeft, left);
                track(info.NewFaceRight, right);

                cursor = mesh.FindHalfEdge(info.NewVertex, destination);
                if (cursor < 0)
                    throw new InvalidOperationException("edge split lost the remainder of the edge");
                consumed = t;
            }
        }
    }

    // ------------------------------------------------------------------ 3. interior points

    private static void InsertInteriorPoints(
        EditableMesh mesh, IReadOnlyList<Vector3d> points, double epsilon,
        Dictionary<int, FaceFrame> frames, Dictionary<int, List<int>> fragments,
        Func<int, int> originOf, Dictionary<int, int> vertexOf,
        Dictionary<int, List<int>> interiorPoints, Action<int, int> track)
    {
        foreach (var (face, ids) in interiorPoints)
        {
            var frame = frames[face];
            foreach (int id in ids)
            {
                if (vertexOf.ContainsKey(id))
                    continue;
                var p = points[id];
                int host = -1;
                int hostEdge = -1;
                double hostEdgeParameter = 0;
                double best = double.NegativeInfinity;
                foreach (int fragment in fragments[face])
                {
                    if (!mesh.IsFace(fragment) || originOf(fragment) != face)
                        continue;
                    double margin = Margin(mesh, fragment, frame, p, out int edge, out double t);
                    if (margin > best)
                    {
                        best = margin;
                        host = fragment;
                        hostEdge = edge;
                        hostEdgeParameter = t;
                    }
                }
                if (host < 0 || best < -epsilon)
                    throw new InvalidOperationException($"no fragment of face {face} contains an interior seam point");

                if (best <= epsilon && hostEdge >= 0)
                {
                    // The point landed on an edge created by an earlier cut: split that
                    // edge rather than poking a zero-area sliver next to it. The parameter
                    // only has to be topologically valid — the exact position is written
                    // immediately after — so it is clamped well clear of both ends.
                    const double parameterMargin = 1e-6;
                    int left = mesh.FaceOf(hostEdge);
                    int right = mesh.FaceOf(mesh.Twin(hostEdge));
                    var split = mesh.SplitEdge(
                        hostEdge, out var splitInfo, Math.Clamp(hostEdgeParameter, parameterMargin, 1 - parameterMargin));
                    if (split != MeshOperationResult.Ok)
                        throw new InvalidOperationException($"SplitEdge refused ({split}) placing an interior seam point");
                    mesh.SetPosition(splitInfo.NewVertex, p);
                    vertexOf[id] = splitInfo.NewVertex;
                    track(splitInfo.NewFaceLeft, left);
                    track(splitInfo.NewFaceRight, right);
                    continue;
                }

                var poke = mesh.PokeFace(host, p, out var pokeInfo);
                if (poke != MeshOperationResult.Ok)
                    throw new InvalidOperationException($"PokeFace refused ({poke}) on fragment {host}");
                vertexOf[id] = pokeInfo.NewVertex;
                for (int i = 1; i < pokeInfo.Faces.Length; i++)
                    track(pokeInfo.Faces[i], host);
            }
        }
    }

    /// <summary>
    /// Signed distance from <paramref name="p"/> to the nearest edge of a triangle,
    /// positive inside. Also reports that nearest edge and the point's parameter along it,
    /// so a point sitting on an edge can be inserted by splitting instead of poking.
    /// </summary>
    private static double Margin(
        EditableMesh mesh, int face, in FaceFrame frame, in Vector3d p, out int edge, out double parameter)
    {
        edge = -1;
        parameter = 0;
        double best = double.PositiveInfinity;
        int start = mesh.FaceHalfEdge(face);
        int he = start;
        var q = frame.To2d(p);
        do
        {
            var a = frame.To2d(mesh.GetPosition(mesh.Origin(he)));
            var b = frame.To2d(mesh.GetPosition(mesh.Destination(he)));
            var ab = b - a;
            double length = ab.Length;
            double distance = length > 0 ? Predicates2d.Orient2d(a, b, q) / length : 0;
            if (distance < best)
            {
                best = distance;
                edge = he;
                parameter = length > 0 ? (q - a).Dot(ab) / (length * length) : 0.5;
            }
            he = mesh.Next(he);
        } while (he != start);
        return best;
    }

    // ------------------------------------------------------------------ 4. segments

    private static void RecoverSegments(
        EditableMesh mesh, ImprintPlan plan, double epsilon,
        Dictionary<int, FaceFrame> frames, Func<int, int> originOf, Dictionary<int, int> vertexOf)
    {
        var locked = new HashSet<(int, int)>();
        foreach (var (face, segments) in plan.FaceSegments)
        {
            var frame = frames[face];
            foreach (var (p, q) in segments)
            {
                if (!vertexOf.TryGetValue(p, out int vp) || !vertexOf.TryGetValue(q, out int vq))
                    throw new InvalidOperationException("an intersection segment endpoint was never inserted");
                if (vp == vq)
                    continue; // the segment collapsed onto a single vertex
                Recover(mesh, frame, face, vp, vq, originOf, locked);
            }
        }
    }

    private static void Recover(
        EditableMesh mesh, in FaceFrame frame, int face, int vp, int vq,
        Func<int, int> originOf, HashSet<(int, int)> locked)
    {
        if (vp == vq)
            return; // a waypoint split the segment exactly at one of its own ends
        int iterations = 0;
        while (mesh.FindHalfEdge(vp, vq) < 0)
        {
            if (++iterations > MaxRecoveryIterations)
                throw new InvalidOperationException(
                    $"flip recovery of the intersection segment {vp}–{vq} did not converge");

            var crossings = Walk(mesh, frame, face, vp, vq, originOf, out int waypoint);
            if (waypoint >= 0)
            {
                // The segment runs through an existing vertex: recover the two halves.
                Recover(mesh, frame, face, vp, waypoint, originOf, locked);
                Recover(mesh, frame, face, waypoint, vq, originOf, locked);
                return;
            }
            if (crossings.Count == 0)
                throw new InvalidOperationException(
                    $"no triangle strip connects the intersection segment {vp}–{vq}");

            bool flipped = false;
            foreach (int he in crossings)
            {
                var key = Key(mesh.Origin(he), mesh.Destination(he));
                if (locked.Contains(key))
                    throw new InvalidOperationException(
                        "two intersection segments cross inside one face (self-intersecting input?)");
                if (!IsConvexQuad(mesh, frame, he))
                    continue;
                if (mesh.FlipEdge(he, out _) != MeshOperationResult.Ok)
                    continue;
                flipped = true;
                break;
            }
            if (!flipped)
                throw new InvalidOperationException(
                    $"flip recovery of the intersection segment {vp}–{vq} stalled (no flippable crossing edge)");
        }
        locked.Add(Key(vp, vq));
    }

    private static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

    /// <summary>
    /// The half-edges crossed by the open segment <paramref name="vp"/> →
    /// <paramref name="vq"/>, walking triangle to triangle. If the segment passes through
    /// an existing vertex, that vertex is reported as a waypoint and the walk stops — the
    /// caller recovers the two halves instead.
    /// </summary>
    private static List<int> Walk(
        EditableMesh mesh, in FaceFrame frame, int face, int vp, int vq, Func<int, int> originOf, out int waypoint)
    {
        waypoint = -1;
        var crossings = new List<int>();
        var a = frame.To2d(mesh.GetPosition(vp));
        var b = frame.To2d(mesh.GetPosition(vq));

        int current = -1;
        foreach (int he in mesh.OutgoingHalfEdges(vp))
        {
            if (mesh.FaceOf(he) < 0 || originOf(mesh.FaceOf(he)) != face)
                continue;
            int next = mesh.Next(he);
            int d1 = mesh.Destination(he);
            int d2 = mesh.Destination(next);
            var p1 = frame.To2d(mesh.GetPosition(d1));
            var p2 = frame.To2d(mesh.GetPosition(d2));
            int s1 = Predicates2d.Orient2dSign(a, p1, b);
            int s2 = Predicates2d.Orient2dSign(a, p2, b);
            // A zero turn only says "on the line through vp and this neighbour" — the
            // wedge test cannot tell the ray from its opposite, so the direction has to be
            // confirmed before the wedge is accepted or a waypoint is declared. (Exactly
            // collinear neighbours are common: a rim vertex can land precisely on the
            // diagonal a quad face was triangulated along.)
            if (s1 == 0)
            {
                if (!Forward(a, b, p1))
                    continue;
                waypoint = Waypoint(a, b, p1, d1);
                return crossings;
            }
            if (s2 == 0)
            {
                if (!Forward(a, b, p2))
                    continue;
                waypoint = Waypoint(a, b, p2, d2);
                return crossings;
            }
            if (s1 > 0 && s2 < 0)
            {
                current = next; // the edge opposite vp in this triangle
                break;
            }
        }
        if (current < 0)
            return crossings;

        for (int step = 0; step <= MaxRecoveryIterations; step++)
        {
            crossings.Add(current);
            int twin = mesh.Twin(current);
            int neighbour = mesh.FaceOf(twin);
            if (neighbour < 0 || originOf(neighbour) != face)
                throw new InvalidOperationException(
                    $"the segment {vp}–{vq} ({mesh.GetPosition(vp)} → {mesh.GetPosition(vq)}) left " +
                    $"face {face} at step {step} through edge {mesh.Origin(current)}–{mesh.Destination(current)}");

            // The crossed edge runs tail → head; the neighbouring triangle is
            // (head, tail, apex), entered through that edge. Its exit edge is the one
            // whose endpoints straddle the segment: tail→apex when the apex sits on the
            // head's side, apex→head otherwise.
            int head = mesh.Destination(current);
            int exitFromTail = mesh.Next(twin);       // tail → apex
            int apex = mesh.Destination(exitFromTail);
            if (apex == vq)
                return crossings;

            var pApex = frame.To2d(mesh.GetPosition(apex));
            int sApex = Predicates2d.Orient2dSign(a, b, pApex);
            if (sApex == 0)
            {
                waypoint = Waypoint(a, b, pApex, apex);
                return crossings;
            }
            int sHead = Predicates2d.Orient2dSign(a, b, frame.To2d(mesh.GetPosition(head)));
            current = sApex == sHead ? exitFromTail : mesh.Prev(twin);
        }
        throw new InvalidOperationException("the walk along an intersection segment did not terminate");
    }

    /// <summary>True when a point collinear with the segment lies on its forward side.</summary>
    private static bool Forward(in Vector2d a, in Vector2d b, in Vector2d p) => (b - a).Dot(p - a) > 0;

    /// <summary>
    /// Accepts a vertex that is collinear with, and ahead of, the segment start as a
    /// waypoint — it must fall strictly short of the far end. A vertex sitting beyond the
    /// end means the segment terminates inside an existing edge, which no valid
    /// triangulation of the face can express.
    /// </summary>
    private static int Waypoint(in Vector2d a, in Vector2d b, in Vector2d p, int vertex)
    {
        if ((b - a).Dot(p - a) >= (b - a).LengthSquared)
            throw new InvalidOperationException(
                "an intersection segment ends inside an existing edge (collinear vertices)");
        return vertex;
    }

    /// <summary>
    /// True when flipping <paramref name="halfEdge"/> is geometrically legal: the two
    /// triangles must form a strictly convex quadrilateral in the face's plane, otherwise
    /// the flip would fold them over.
    /// </summary>
    private static bool IsConvexQuad(EditableMesh mesh, in FaceFrame frame, int halfEdge)
    {
        int twin = mesh.Twin(halfEdge);
        if (mesh.FaceOf(halfEdge) < 0 || mesh.FaceOf(twin) < 0)
            return false;
        var a = frame.To2d(mesh.GetPosition(mesh.Origin(halfEdge)));
        var b = frame.To2d(mesh.GetPosition(mesh.Destination(halfEdge)));
        var c = frame.To2d(mesh.GetPosition(mesh.Destination(mesh.Next(halfEdge))));
        var d = frame.To2d(mesh.GetPosition(mesh.Destination(mesh.Next(twin))));
        return Predicates2d.Orient2dSign(c, d, a) * Predicates2d.Orient2dSign(c, d, b) < 0;
    }

    /// <summary>
    /// A face's plane as an orthonormal 2D frame. Captured before the first cut: every
    /// fragment of the face stays in this plane, so all crossing and convexity tests can
    /// use exact 2D predicates.
    /// </summary>
    private readonly struct FaceFrame
    {
        private readonly Vector3d _origin;
        private readonly Vector3d _x;
        private readonly Vector3d _y;

        private FaceFrame(in Vector3d origin, in Vector3d x, in Vector3d y, int[] halfEdges)
        {
            _origin = origin;
            _x = x;
            _y = y;
            HalfEdges = halfEdges;
        }

        /// <summary>The face's half-edges as they were before any cut.</summary>
        public int[] HalfEdges { get; }

        public static FaceFrame Of(EditableMesh mesh, int face)
        {
            var loop = new List<int>();
            int start = mesh.FaceHalfEdge(face);
            int he = start;
            do
            {
                loop.Add(he);
                he = mesh.Next(he);
            } while (he != start);

            var a = mesh.GetPosition(mesh.Origin(loop[0]));
            var b = mesh.GetPosition(mesh.Origin(loop[1]));
            var c = mesh.GetPosition(mesh.Origin(loop[2]));
            var cross = (b - a).Cross(c - a);
            double length = cross.Length;
            if (length <= 0)
                throw new InvalidOperationException($"face {face} is degenerate and cannot be cut");
            var (x, y) = MeshMeshCut.Basis(cross / length);
            return new FaceFrame(a, x, y, [.. loop]);
        }

        public Vector2d To2d(in Vector3d p) => MeshMeshCut.Project(p - _origin, _x, _y);
    }
}
