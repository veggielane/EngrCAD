using EngrCAD.Core;

namespace EngrCAD.BRep;

public static partial class DirectEdit
{
    /// <summary>
    /// The second heal: delete a BLEND and let the neighbours it separated close up again.
    ///
    /// <para><b>What it is for.</b> A fillet band, a chamfer band and a drafted strip all sit
    /// BETWEEN two faces that used to meet. Removing one leaves a wound that runs only part of
    /// the way round its neighbours' loops, so there is no whole loop to stop referencing —
    /// the two neighbours have to be EXTENDED until they meet each other in a new edge. That
    /// is what makes "take the fillet off this imported part" an operation rather than a
    /// refusal, and it is why the result is not merely shaped like the unfilleted body: the
    /// kept faces keep their own carriers, so the recovered solid IS the one the blend was
    /// added to.</para>
    ///
    /// <para><b>The condition, stated once.</b> Every deleted face must be a STRIP: exactly
    /// two wound edges, whose kept neighbours are two distinct faces. That single requirement
    /// is what makes the answer well posed — the deleted face is replaced by ONE new edge
    /// between those two carriers, used once by each, so the result is two-manifold by
    /// construction rather than by repair. Anything else is refused by name: a face with one
    /// wound edge has nothing to extend it to meet, and a face with three or more has no
    /// opposite pair (a box's four sides extended past its deleted top never meet, which is
    /// exactly the case the refusal has always named).</para>
    ///
    /// <para><b>Where the new vertices come from.</b> An edge INTERIOR to the deleted set —
    /// the miter between two fillet bands, say — vanishes entirely, so its two endpoints
    /// become one point: the corner of the kept carriers around it, solved exactly by
    /// <see cref="SurfaceCorner"/>. Clustering the touched vertices under those interior edges
    /// and solving one corner per cluster is the whole vertex rule; nothing is averaged and
    /// nothing is snapped. A vertex the wound never touches is carried over verbatim, which is
    /// what keeps the far side of the body bit for bit what it was.</para>
    /// </summary>
    private static bool TryExtendNeighbours(
        BrepSolid solid, BrepFace[] faces, HashSet<BrepFace> deleted,
        Dictionary<BrepEdge, List<BrepFace>> users, HashSet<BrepEdge> wound,
        out BrepSolid healed, out string? reason)
    {
        healed = null!;
        reason = null;

        // ---- 1. Each deleted face must be a strip: two wound sides, two distinct neighbours.
        var sides = new Dictionary<BrepFace, (BrepFace A, BrepFace B, BrepEdge EdgeA, BrepEdge EdgeB)>();
        foreach (var face in faces)
        {
            if (!deleted.Contains(face))
                continue;
            var edges = new List<BrepEdge>();
            var partners = new List<BrepFace>();
            foreach (var loop in face.Loops)
            {
                foreach (var coedge in loop.Coedges)
                {
                    if (!wound.Contains(coedge.Edge) || edges.Contains(coedge.Edge))
                        continue;
                    edges.Add(coedge.Edge);
                    partners.Add(users[coedge.Edge].First(f => !deleted.Contains(f)));
                }
            }
            if (edges.Count != 2)
            {
                reason = $"a deleted {face.Surface.GetType().Name} face has {edges.Count} exposed edges " +
                         "rather than the two a blend strip has, so it has no opposite pair of neighbours " +
                         "to extend toward each other";
                return false;
            }
            if (ReferenceEquals(partners[0], partners[1]))
            {
                reason = $"both exposed edges of a deleted {face.Surface.GetType().Name} face border the " +
                         "SAME neighbour, so extending it would have to meet itself";
                return false;
            }
            sides[face] = (partners[0], partners[1], edges[0], edges[1]);
        }

        // ---- 2. Touched vertices, clustered by the interior edges that vanish under them.
        var vertices = solid.Vertices.ToArray();
        var index = new Dictionary<BrepVertex, int>(vertices.Length);
        for (int i = 0; i < vertices.Length; i++)
            index[vertices[i]] = i;

        var touched = new bool[vertices.Length];
        var incidentKept = new List<BrepFace>[vertices.Length];
        for (int i = 0; i < incidentKept.Length; i++)
            incidentKept[i] = [];
        foreach (var coedge in solid.Coedges)
        {
            var face = coedge.Loop.Face;
            foreach (var vertex in (ReadOnlySpan<BrepVertex>)[coedge.StartVertex, coedge.EndVertex])
            {
                int v = index[vertex];
                if (deleted.Contains(face))
                    touched[v] = true;
                else if (!incidentKept[v].Contains(face))
                    incidentKept[v].Add(face);
            }
        }

        var parent = new int[vertices.Length];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;
        int Find(int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }
        void Union(BrepVertex x, BrepVertex y)
        {
            int a = Find(index[x]);
            int b = Find(index[y]);
            if (a != b)
                parent[a] = b;
        }
        foreach (var edge in solid.Edges)
        {
            // Interior: both users deleted, so the edge itself goes and its two ends become
            // one point.
            if (users[edge].All(deleted.Contains))
                Union(edge.StartVertex, edge.EndVertex);
        }
        foreach (var (face, side) in sides)
        {
            // A strip that closes on itself — a fillet round a whole circular rim — has two
            // CLOSED wound edges and no interior edge at all, so its two seam vertices are
            // never joined above. They are the same point of the healed body: the replacement
            // is one closed edge, and a closed edge has exactly one vertex. Merging them is
            // also what puts BOTH neighbours' carriers in that corner's list, which is what
            // places it on the rim rather than on whichever single surface it started on.
            if (side.EdgeA.IsClosedEdge != side.EdgeB.IsClosedEdge)
            {
                reason = $"a deleted {face.Surface.GetType().Name} face has one closed and one open " +
                         "exposed edge, so the edge that replaces it is neither";
                return false;
            }
            if (side.EdgeA.IsClosedEdge)
                Union(side.EdgeA.StartVertex, side.EdgeB.StartVertex);
        }

        // ---- 3. One corner solve per cluster of touched vertices.
        var clusterMembers = new Dictionary<int, List<int>>();
        for (int i = 0; i < vertices.Length; i++)
        {
            if (!touched[i])
                continue;
            int root = Find(i);
            if (!clusterMembers.TryGetValue(root, out var list))
                clusterMembers[root] = list = [];
            list.Add(i);
        }

        var moved = new Dictionary<int, BrepVertex>();
        foreach (int root in clusterMembers.Keys.Order())
        {
            var members = clusterMembers[root];
            var carriers = new List<Surface>();
            var seed = Vector3d.Zero;
            foreach (int v in members)
            {
                seed += vertices[v].Position;
                foreach (var face in incidentKept[v])
                {
                    if (!carriers.Contains(face.Surface))
                        carriers.Add(face.Surface);
                }
            }
            seed /= members.Count;
            if (carriers.Count == 0)
            {
                reason = $"the vertex at {seed} is surrounded entirely by deleted faces, so nothing " +
                         "remains to place it";
                return false;
            }
            if (!SurfaceCorner.TrySolvePoint(carriers, seed, out var corner, out string? why))
            {
                reason = $"the neighbours around {seed} do not meet again when extended ({why})";
                return false;
            }
            moved[root] = new BrepVertex(corner.Point);
        }

        var kept = new Dictionary<int, BrepVertex>();
        BrepVertex VertexFor(BrepVertex vertex)
        {
            int i = index[vertex];
            if (touched[i])
                return moved[Find(i)];
            if (!kept.TryGetValue(i, out var copy))
                kept[i] = copy = new BrepVertex(vertex.Position);
            return copy;
        }

        // ---- 4. One new edge per deleted face, and a rebuild of every edge a moved vertex
        // reaches. A sound edge whose both ends stayed put is carried over VERBATIM — the
        // curve object, its domain and its endpoints — which is what makes the far side of the
        // healed body bit-identical rather than merely equal.
        var replacement = new Dictionary<BrepFace, BrepEdge>();
        foreach (var face in faces)
        {
            if (!deleted.Contains(face))
                continue;
            var (a, b, edgeA, edgeB) = sides[face];
            var start = VertexFor(edgeA.StartVertex);
            var end = VertexFor(edgeA.EndVertex);
            var otherStart = VertexFor(edgeB.StartVertex);
            var otherEnd = VertexFor(edgeB.EndVertex);
            bool matched = (ReferenceEquals(start, otherStart) && ReferenceEquals(end, otherEnd))
                || (ReferenceEquals(start, otherEnd) && ReferenceEquals(end, otherStart));
            if (!matched)
            {
                reason = $"the two exposed edges of a deleted {face.Surface.GetType().Name} face do not " +
                         "end at the same pair of corners, so the edge that replaces it has no endpoints";
                return false;
            }
            // A DOMAIN-DRIVEN neighbour must be lengthened before it is intersected — the
            // trim-the-surface rule running the other way. An extrusion's carrier is bounded
            // by its own parameter rectangle and the intersection is clipped to it, so a
            // sketch extrusion's wall stops exactly at the blend's tangency line and the two
            // neighbours "do not meet" for a bookkeeping reason rather than a geometric one.
            // Extending to reach the solved corners is what EXTEND means.
            var reach = new[] { start.Position, end.Position };
            if (!SurfaceCorner.TrySolveCurve(
                    CarrierBody.TrimToPoints(a.Surface, reach, extendOnly: true),
                    CarrierBody.TrimToPoints(b.Surface, reach, extendOnly: true),
                    start.Position, end.Position,
                    SurfaceCorner.CornerPolicy.ExactOnly, out var curve, out string? why))
            {
                reason = $"the two neighbours of a deleted {face.Surface.GetType().Name} face do not meet " +
                         $"in an exact curve when extended ({why})";
                return false;
            }
            replacement[face] = new BrepEdge(curve.Curve, curve.Curve.Domain, start, end);
        }

        var rebuilt = new Dictionary<BrepEdge, BrepEdge>();
        foreach (var edge in solid.Edges)
        {
            var both = users[edge];
            if (both.Any(deleted.Contains))
                continue;
            var start = VertexFor(edge.StartVertex);
            var end = VertexFor(edge.EndVertex);
            if (!touched[index[edge.StartVertex]] && !touched[index[edge.EndVertex]])
            {
                rebuilt[edge] = new BrepEdge(edge.Curve, edge.Domain, start, end);
                continue;
            }
            // A sound edge one of whose ends moved: same two carriers, new corners. Solving it
            // rather than re-parameterizing the old curve is what keeps a curved edge exact —
            // a box's vertical edge comes back as the Line3d between its new corners, and a
            // bore's rim as the conic through them.
            var span = new[] { start.Position, end.Position };
            if (!SurfaceCorner.TrySolveCurve(
                    CarrierBody.TrimToPoints(both[0].Surface, span, extendOnly: true),
                    CarrierBody.TrimToPoints(both[1].Surface, span, extendOnly: true),
                    start.Position, end.Position,
                    SurfaceCorner.CornerPolicy.ExactOnly, out var curve, out string? why))
            {
                reason = $"an edge the wound reaches, at {edge.Curve.PointAt(edge.Domain.Start)}, cannot be " +
                         $"re-solved between its two faces ({why})";
                return false;
            }
            rebuilt[edge] = new BrepEdge(curve.Curve, curve.Curve.Domain, start, end);
        }

        // ---- 5. Kept faces, with each wound coedge swapped for its deleted face's new edge.
        var healedFaces = new List<BrepFace>(faces.Length - deleted.Count);
        foreach (var face in faces)
        {
            if (deleted.Contains(face))
                continue;
            var loops = new List<BrepLoop>(face.Loops.Count);
            var boundary = new List<Vector3d>();
            bool changed = false;
            foreach (var loop in face.Loops)
            {
                var coedges = new List<BrepCoedge>(loop.Coedges.Count);
                foreach (var coedge in loop.Coedges)
                {
                    if (!wound.Contains(coedge.Edge))
                    {
                        var edge = rebuilt[coedge.Edge];
                        changed |= !ReferenceEquals(edge.Curve, coedge.Edge.Curve);
                        coedges.Add(new BrepCoedge(edge, coedge.SameSense));
                        Sample(edge, boundary);
                        continue;
                    }
                    changed = true;
                    var owner = users[coedge.Edge].First(deleted.Contains);
                    var replaced = replacement[owner];
                    coedges.Add(new BrepCoedge(replaced, SenseFor(coedge, replaced, VertexFor)));
                    Sample(replaced, boundary);
                }
                loops.Add(new BrepLoop(coedges));
            }
            // Domain-driven carriers must be re-trimmed to the loops they now carry: an
            // extrusion's or a revolve's grid ignores its loops, so a face whose boundary
            // grew past its parameter rectangle would refuse to tessellate. Untouched faces
            // keep the very surface object they had.
            var surface = changed ? CarrierBody.TrimToPoints(face.Surface, boundary) : face.Surface;
            healedFaces.Add(new BrepFace(surface, loops, face.IsReversed).DescendsFrom(face));
        }

        var result = new BrepSolid([new BrepShell(healedFaces)]);
        try
        {
            result.Validate();
        }
        catch (InvalidOperationException exception)
        {
            reason = $"the extended neighbours do not close a valid solid ({exception.Message})";
            return false;
        }
        healed = result;
        return true;
    }

    /// <summary>
    /// Which way a kept face traverses the edge that replaced its wound one. The two corners
    /// are DISTINCT objects wherever the strip is open, so the answer is combinatorial — no
    /// tolerance, no tangent comparison. A CLOSED strip (a fillet band round a whole rim) has
    /// one corner at both ends, so the direction is read from the curve instead, by comparing
    /// the new tangent with the direction the old coedge ran.
    /// </summary>
    private static bool SenseFor(BrepCoedge original, BrepEdge replaced, Func<BrepVertex, BrepVertex> map)
    {
        var start = map(original.StartVertex);
        if (!ReferenceEquals(replaced.StartVertex, replaced.EndVertex))
            return ReferenceEquals(start, replaced.StartVertex);

        var domain = original.Edge.Domain;
        var from = original.Edge.Curve.PointAt(domain.Start);
        var to = original.Edge.Curve.PointAt(domain.End);
        var was = original.SameSense ? to - from : from - to;
        var now = replaced.Curve.DerivativeAt(replaced.Domain.ParameterAt(0.5));
        return was.Dot(now) >= 0;
    }

    private static void Sample(BrepEdge edge, List<Vector3d> into)
    {
        for (int i = 0; i <= 8; i++)
            into.Add(edge.Curve.PointAt(edge.Domain.ParameterAt(i / 8.0)));
    }
}
