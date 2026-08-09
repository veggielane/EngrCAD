using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Interop;

/// <summary>
/// Phase 2 of <see cref="MeshToBrep"/>: welds the fitted, trimmed faces into a
/// <see cref="BrepSolid"/>. A region boundary becomes the EXACT intersection of the two
/// fitted surfaces (a line, a circle) and a triple-point corner the exact meeting of three,
/// so the faces close; the shared edges are built once and referenced twice, so the result
/// is a manifold directly. <see cref="ShapeHealing.Heal"/> repairs shell orientation and
/// <see cref="BrepSolid.Validate"/> is the oracle.
/// </summary>
internal static class SolidAssembler
{
    internal static (BrepSolid? Solid, string? Reason) Assemble(
        HalfEdgeMesh mesh, int[] region, int regionCount, MeshToBrep.SurfaceFit[] fits,
        MeshToBrepOptions options, List<string> notes)
    {
        var faces = mesh.Faces.ToArray();
        int nv = mesh.VertexCount;

        // --- Regions incident to each vertex, and the "node" set (triple points) ---------
        var regionsAt = new HashSet<int>[nv];
        for (int i = 0; i < nv; i++) regionsAt[i] = [];
        for (int f = 0; f < faces.Length; f++)
            foreach (var v in faces[f].Vertices())
                regionsAt[v.Index].Add(region[f]);

        var region3 = new Aabb((0, 0, 0), (0, 0, 0));
        {
            var min = new Vector3d(double.MaxValue, double.MaxValue, double.MaxValue);
            var max = -min;
            foreach (var v in mesh.Vertices) { min = Vector3d.Min(min, v.Position); max = Vector3d.Max(max, v.Position); }
            region3 = new Aabb(min, max).Expanded((max - min).Length * 0.05 + 1);
        }

        // --- Chains: connected runs of border edges between two regions, split at nodes ----
        // A border edge is an undirected mesh edge whose two faces are in different regions.
        // Represent it by the half-edge with the smaller index. Union two border edges that
        // share a NON-node vertex and the same region pair.
        var border = new List<HalfEdge>();
        var borderId = new Dictionary<int, int>(); // he.Index (smaller of pair) -> border list index
        foreach (var h in mesh.HalfEdges)
        {
            if (h.Index > h.Twin.Index || h.Twin.IsBoundary)
                continue;
            int ra = region[h.Face.Index], rb = region[h.Twin.Face.Index];
            if (ra == rb) continue;
            borderId[h.Index] = border.Count;
            border.Add(h);
        }
        var uf = new UnionFind(border.Count);
        // Group border edges at each non-node vertex by region pair and union them.
        var atVertex = new Dictionary<(int Vertex, int Ra, int Rb), List<int>>();
        for (int b = 0; b < border.Count; b++)
        {
            var h = border[b];
            int ra = region[h.Face.Index], rb = region[h.Twin.Face.Index];
            (int lo, int hi) = ra < rb ? (ra, rb) : (rb, ra);
            foreach (int v in new[] { h.Origin.Index, h.Destination.Index })
            {
                if (regionsAt[v].Count >= 3) continue; // node: chains break here
                var key = (v, lo, hi);
                if (!atVertex.TryGetValue(key, out var list)) atVertex[key] = list = [];
                list.Add(b);
            }
        }
        foreach (var list in atVertex.Values)
            for (int i = 1; i < list.Count; i++)
                uf.Union(list[0], list[i]);

        // Chain records.
        var chainEdges = new Dictionary<int, List<int>>(); // chain root -> border edge indices
        for (int b = 0; b < border.Count; b++)
        {
            int root = uf.Find(b);
            if (!chainEdges.TryGetValue(root, out var list)) chainEdges[root] = list = [];
            list.Add(b);
        }

        // --- Snap triple-point corners to the exact meeting of their surfaces -------------
        var nodeVertex = new Dictionary<int, BrepVertex>();
        BrepVertex NodeVertexFor(int v)
        {
            if (nodeVertex.TryGetValue(v, out var bv)) return bv;
            var pos = mesh.GetPosition(v); // v < nv, cheap enough once per node
            var surfs = new List<Surface>();
            foreach (int r in regionsAt[v]) if (fits[r].Surface is { } s) surfs.Add(s);
            var snapped = pos;
            if (surfs.Count >= 3 &&
                SurfaceCorner.TrySolvePoint(surfs, pos, out var corner, out _) &&
                corner.Residual < (region3.Size.Length) * 1e-3)
                snapped = corner.Point;
            bv = new BrepVertex(snapped);
            nodeVertex[v] = bv;
            return bv;
        }

        // --- Build one shared BrepEdge per chain -----------------------------------------
        var chainInfo = new Dictionary<int, ChainRecord>();
        foreach (var (root, edges) in chainEdges)
        {
            var rec = BuildChain(mesh, region, fits, region3, border, edges, regionsAt, NodeVertexFor, out var why);
            if (rec is null)
                return (null, $"could not recover the exact edge for a region boundary: {why}");
            chainInfo[root] = rec.Value;
        }

        // --- Assemble each region's face --------------------------------------------------
        var brepFaces = new List<BrepFace>();
        for (int r = 0; r < regionCount; r++)
        {
            var fit = fits[r];
            if (fit.Surface is not { } surface)
                return (null, $"region {r} has no surface.");
            var loops = BuildFaceLoops(mesh, region, r, faces, surface, border, borderId, uf, chainInfo, out var why2);
            if (loops is null)
                return (null, $"region {r} ({fit.Kind}): {why2}");
            if (loops.Count == 0)
                return (null,
                    $"region {r} is a closed surface with no boundary (e.g. a whole sphere); a seamed " +
                    "single-face solid is not assembled in v1.");
            brepFaces.Add(new BrepFace(surface, loops, fit.Reversed));
        }

        BrepSolid solid;
        try
        {
            solid = new BrepSolid([new BrepShell(brepFaces)]);
        }
        catch (Exception ex)
        {
            return (null, $"topology construction failed: {ex.Message}");
        }

        // Heal repairs shell orientation (the sense-consistency flood + outward vote); on a
        // well-formed solid it is a no-op. Then Validate is the oracle.
        try
        {
            var healed = ShapeHealing.Heal(solid, new ShapeHealingOptions { RefitStraightEdges = false });
            solid = healed.Solid;
            if (healed.Report.EdgesSewn > 0 || healed.Report.VerticesMerged > 0)
                notes.Add($"healing sewed {healed.Report.EdgesSewn} edges, merged {healed.Report.VerticesMerged} vertices.");
            solid.Validate();
        }
        catch (Exception ex)
        {
            return (null, $"the assembled solid did not validate: {ex.Message}");
        }
        return (solid, null);
    }

    private readonly record struct ChainRecord(
        BrepEdge Edge, int StartMeshVertex, int EndMeshVertex, bool Closed);

    /// <summary>Builds the exact BrepEdge for one chain (a run of border edges between two regions).</summary>
    private static ChainRecord? BuildChain(
        HalfEdgeMesh mesh, int[] region, MeshToBrep.SurfaceFit[] fits, in Aabb region3,
        List<HalfEdge> border, List<int> edgeIds, HashSet<int>[] regionsAt,
        Func<int, BrepVertex> nodeVertexFor, out string why)
    {
        why = "";
        // Region pair.
        var h0 = border[edgeIds[0]];
        int ra = region[h0.Face.Index], rb = region[h0.Twin.Face.Index];
        var surfA = fits[ra].Surface!;
        var surfB = fits[rb].Surface!;

        // Order the chain's vertices into a polyline. Build vertex adjacency within the chain.
        var adj = new Dictionary<int, List<int>>();
        foreach (int e in edgeIds)
        {
            var h = border[e];
            int a = h.Origin.Index, b = h.Destination.Index;
            (adj.TryGetValue(a, out var la) ? la : adj[a] = []).Add(b);
            (adj.TryGetValue(b, out var lb) ? lb : adj[b] = []).Add(a);
        }
        // Endpoints are nodes (degree-1 in this chain's own graph, or ≥3 regions).
        var endpoints = adj.Where(kv => regionsAt[kv.Key].Count >= 3).Select(kv => kv.Key).Distinct().ToList();
        bool closed = endpoints.Count == 0;

        int start = closed ? adj.Keys.Min() : endpoints.Min();
        var polyline = OrderPolyline(adj, start, closed);
        if (polyline is null) { why = "the chain's edges do not form a simple path."; return null; }
        int end = closed ? start : polyline[^1];

        var startVertex = closed ? new BrepVertex(mesh.GetPosition(start)) : nodeVertexFor(start);
        var endVertex = closed ? startVertex : nodeVertexFor(end);

        // Exact edge geometry.
        Curve3d? curve;
        Interval domain;
        if (surfA is PlaneSurface && surfB is PlaneSurface && !closed)
        {
            // plane ∩ plane through the two snapped corners: an exact segment.
            curve = new Line3d(startVertex.Position, endVertex.Position);
            domain = Interval.Unit;
        }
        else if (!closed)
        {
            why = "a non-planar analytic edge between two corners is not recovered in v1 (only " +
                  "closed circular rims and straight edges).";
            return null;
        }
        else if (TryCircleFromPlaneCylinder(surfA, surfB, polyline, mesh, out var circle))
        {
            curve = circle;
            domain = new Interval(0, 2 * Math.PI);
            startVertex = new BrepVertex(circle.PointAt(0));
            endVertex = startVertex;
        }
        else if (TryTracedClosedCurve(surfA, surfB, region3, polyline, mesh, out var traced, out var seamStart))
        {
            curve = traced;
            domain = traced.Domain;
            startVertex = new BrepVertex(seamStart);
            endVertex = startVertex;
        }
        else
        {
            why = "a closed region boundary was not an exact circle (v1 recovers plane∩cylinder rims).";
            return null;
        }

        var edge = new BrepEdge(curve, domain, startVertex, endVertex);
        return new ChainRecord(edge, start, end, closed);
    }

    /// <summary>plane ⊥ cylinder axis → exact rim <see cref="Circle3d"/> in the plane, phased so
    /// its parameter 0 point is near the polyline's first vertex.</summary>
    private static bool TryCircleFromPlaneCylinder(
        Surface a, Surface b, List<int> polyline, HalfEdgeMesh mesh, out Circle3d circle)
    {
        circle = null!;
        (PlaneSurface? plane, CylinderSurface? cyl) =
            (a, b) switch
            {
                (PlaneSurface p, CylinderSurface c) => (p, c),
                (CylinderSurface c, PlaneSurface p) => (p, c),
                _ => (null, null),
            };
        if (plane is null || cyl is null) return false;

        var d = cyl.Axis;
        var np = plane.Normal;
        double dn = d.Dot(np);
        if (Math.Abs(dn) < 0.999) return false; // not perpendicular; leave to the tracer

        // Point where the cylinder axis crosses the plane = the circle centre.
        double t = (plane.Origin - cyl.Origin).Dot(np) / dn;
        var centre = cyl.Origin + d * t;
        var frame = Frame3d.FromNormal(centre, d.Dot(np) >= 0 ? d : -d);
        // Phase the seam near the polyline's first vertex so the seam vertex is a mesh vertex.
        var p0 = mesh.GetPosition(polyline[0]) - centre;
        var xdir = (p0 - frame.Z * p0.Dot(frame.Z));
        if (!xdir.TryNormalize(Tolerance.Default, out xdir)) xdir = frame.X;
        var ydir = frame.Z.Cross(xdir);
        circle = new Circle3d(centre, xdir, ydir, cyl.Radius);
        return true;
    }

    /// <summary>Fallback for a closed curve that is not a plane∩cylinder rim: run the analytic
    /// intersection and accept only a single closed analytic branch (circle/ellipse).</summary>
    private static bool TryTracedClosedCurve(
        Surface a, Surface b, in Aabb region3, List<int> polyline, HalfEdgeMesh mesh,
        out Curve3d curve, out Vector3d seamStart)
    {
        curve = null!;
        seamStart = default;
        var branches = SurfaceIntersection.Intersect(a, b, region3);
        var mid = mesh.GetPosition(polyline[polyline.Count / 2]);
        Curve3d? best = null;
        double bestDist = double.PositiveInfinity;
        foreach (var c in branches)
        {
            if (!c.IsClosed) continue;
            if (c is not (Circle3d or Ellipse3d)) continue;
            double dist = (c.PointAt(c.Domain.Mid) - mid).Length;
            if (dist < bestDist) { bestDist = dist; best = c; }
        }
        if (best is null) return false;
        curve = best;
        seamStart = best.PointAt(best.Domain.Start);
        return true;
    }

    private static List<int>? OrderPolyline(Dictionary<int, List<int>> adj, int start, bool closed)
    {
        var order = new List<int> { start };
        int prev = -1, cur = start;
        var guard = adj.Count + 2;
        while (order.Count <= guard)
        {
            var neighbours = adj[cur];
            int nextV = -1;
            foreach (int n in neighbours) if (n != prev) { nextV = n; break; }
            if (nextV < 0) return closed ? null : order; // dead end
            if (nextV == start) return order;            // closed loop complete
            order.Add(nextV);
            prev = cur; cur = nextV;
        }
        return closed ? order : order;
    }

    /// <summary>The ordered coedge loops of one region's face, from the mesh boundary walk.</summary>
    private static List<BrepLoop>? BuildFaceLoops(
        HalfEdgeMesh mesh, int[] region, int r, Face[] faces, Surface surface, List<HalfEdge> border,
        Dictionary<int, int> borderId, UnionFind uf, Dictionary<int, ChainRecord> chainInfo, out string why)
    {
        why = "";
        // Collect the region's border half-edges.
        var used = new HashSet<int>();
        var regionBorder = new List<HalfEdge>();
        foreach (int f in Enumerable.Range(0, faces.Length))
        {
            if (region[f] != r) continue;
            foreach (var h in faces[f].HalfEdges())
                if (region[h.Twin.Face.Index] != r)
                    regionBorder.Add(h);
        }
        if (regionBorder.Count == 0)
            return []; // whole-surface region; caller refuses

        var loops = new List<(List<BrepCoedge> Coedges, List<int> Vertices)>();
        foreach (var seed in regionBorder)
        {
            if (used.Contains(seed.Index)) continue;
            // Walk the boundary loop from seed.
            var loopHalfEdges = new List<HalfEdge>();
            var h = seed;
            do
            {
                used.Add(h.Index);
                loopHalfEdges.Add(h);
                h = NextBoundary(h, region, r);
            } while (h.Index != seed.Index && !used.Contains(h.Index) && loopHalfEdges.Count <= regionBorder.Count + 1);

            var (coedges, verts) = LoopToCoedges(mesh, loopHalfEdges, border, borderId, uf, chainInfo, out var why3);
            if (coedges is null) { why = why3; return null; }
            loops.Add((coedges, verts));
        }

        // Outer-loop classification for a planar face with holes (only a plane HAS a
        // well-defined outer loop; a cylindrical band's two rims are both boundaries).
        int outer = 0;
        if (loops.Count > 1 && surface is PlaneSurface plane)
        {
            var pn = plane.Normal;
            var po = plane.Origin;
            double bestArea = double.NegativeInfinity;
            for (int i = 0; i < loops.Count; i++)
            {
                double area = SignedArea(loops[i].Vertices, mesh, pn, po);
                if (area > bestArea) { bestArea = area; outer = i; }
            }
        }

        var result = new List<BrepLoop>();
        result.Add(new BrepLoop(loops[outer].Coedges));
        for (int i = 0; i < loops.Count; i++)
            if (i != outer) result.Add(new BrepLoop(loops[i].Coedges));
        return result;
    }

    private static (List<BrepCoedge>? Coedges, List<int> Vertices) LoopToCoedges(
        HalfEdgeMesh mesh, List<HalfEdge> loopHalfEdges, List<HalfEdge> border,
        Dictionary<int, int> borderId, UnionFind uf, Dictionary<int, ChainRecord> chainInfo, out string why)
    {
        why = "";
        var verts = loopHalfEdges.Select(h => h.Origin.Index).ToList();
        // Group consecutive half-edges by chain root.
        var coedges = new List<BrepCoedge>();
        int i = 0;
        while (i < loopHalfEdges.Count)
        {
            var h = loopHalfEdges[i];
            int bId = BorderIdOf(h, borderId);
            if (bId < 0) { why = "a loop half-edge is not a border edge."; return (null, verts); }
            int root = uf.Find(bId);
            int entryVertex = h.Origin.Index;
            // Extend the run while the same chain continues.
            int j = i + 1;
            while (j < loopHalfEdges.Count)
            {
                int b2 = BorderIdOf(loopHalfEdges[j], borderId);
                if (b2 < 0 || uf.Find(b2) != root) break;
                j++;
            }
            var rec = chainInfo[root];
            bool sameSense;
            if (rec.Closed)
            {
                // Direction: does the loop travel in the edge curve's + parameter direction?
                var tangent = rec.Edge.Curve.TangentAt(rec.Edge.Domain.Start);
                sameSense = h.Vector.Dot(tangent) >= 0;
            }
            else
            {
                sameSense = entryVertex == rec.StartMeshVertex;
            }
            coedges.Add(new BrepCoedge(rec.Edge, sameSense));
            i = j;
        }
        return (coedges, verts);
    }

    private static int BorderIdOf(HalfEdge h, Dictionary<int, int> borderId)
    {
        int key = Math.Min(h.Index, h.Twin.Index);
        return borderId.TryGetValue(key, out int id) ? id : -1;
    }

    /// <summary>Next border half-edge in a region's boundary loop after <paramref name="h"/>.</summary>
    private static HalfEdge NextBoundary(HalfEdge h, int[] region, int r)
    {
        var e = h.Next; // starts at h.Destination, same face/region
        int guard = 0;
        while (region[e.Twin.Face.Index] == r && ++guard < 100000)
            e = e.Twin.Next;
        return e;
    }

    private static double SignedArea(List<int> verts, HalfEdgeMesh mesh, in Vector3d normal, in Vector3d origin)
    {
        var frame = Frame3d.FromNormal(origin, normal);
        double area = 0;
        for (int i = 0; i < verts.Count; i++)
        {
            var p = mesh.GetPosition(verts[i]) - frame.Origin;
            var q = mesh.GetPosition(verts[(i + 1) % verts.Count]) - frame.Origin;
            double px = p.Dot(frame.X), py = p.Dot(frame.Y);
            double qx = q.Dot(frame.X), qy = q.Dot(frame.Y);
            area += px * qy - qx * py;
        }
        return 0.5 * area;
    }

    private sealed class UnionFind
    {
        private readonly int[] _parent;
        public UnionFind(int n) { _parent = new int[n]; for (int i = 0; i < n; i++) _parent[i] = i; }
        public int Find(int x) { while (_parent[x] != x) x = _parent[x] = _parent[_parent[x]]; return x; }
        public void Union(int a, int b) { _parent[Find(a)] = Find(b); }
    }
}
