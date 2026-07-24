using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Interop;

/// <summary>
/// Parameter-space triangulation for trimmed faces on curved surfaces — faces whose loops
/// do not cover the surface's natural grid domain (fragments produced by
/// <see cref="FaceSplitter.SplitByCurve"/>, e.g. a bore wall cut through by a slot).
/// The loops' shared edge-polyline samples are pulled into (u, v) space; non-wrapping
/// regions are ear-clipped with an exact-coordinate clipper, band-like regions (loops
/// winding the periodic direction — rings subdivided into arcs, which the grid path can
/// no longer match) are strip-zipped between their two ring chains (or fanned to the
/// pole). Oversized interior edges are then midpoint-split down to the natural grid
/// density with new vertices evaluated on the exact surface. Boundary vertices are the
/// exact shared edge samples — never re-evaluated approximations — so neighboring faces
/// weld without cracks.
///
/// The clipper is deliberately not <see cref="Mesh.PolygonTriangulator"/> (earcut):
/// iso-parameter boundary runs (ring arcs at constant v) are exactly collinear in uv, and
/// earcut filters exactly-collinear vertices — on a curved surface a dropped sample opens
/// a crack no zip pass can repair, because uv-collinear points are not 3D-collinear.
/// This clipper keeps every vertex, never emits zero-area uv triangles, and treats points
/// lying exactly on a candidate ear as blocking so no diagonal ever passes through a
/// vertex.
/// </summary>
internal static class TrimmedFaceTessellator
{
    /// <summary>
    /// Attempts to tessellate a trimmed face, appending triangles to
    /// <paramref name="polygons"/> (counter-clockwise in uv, i.e. along the surface
    /// normal — the caller flips reversed faces). Returns false without touching
    /// <paramref name="polygons"/> when the face cannot be handled: a loop point fails
    /// inverse evaluation, the winding structure is unsupported (a band with extra hole
    /// loops), or clipping gets stuck on degenerate input.
    /// </summary>
    public static bool TryTessellate(
        BrepFace face,
        Dictionary<BrepEdge, List<Vector3d>> edgePolylines,
        int segmentsPerCircle,
        int curveSamples,
        List<IReadOnlyList<Vector3d>> polygons)
    {
        var surface = face.Surface;
        double period = FaceGeometry.PeriodU(surface);

        // 1. Pull every loop's shared edge samples into parameter space, unwrapping the
        //    periodic u direction along the loop, and record each loop's winding number.
        var loopUv = new List<List<Vector2d>>(face.Loops.Count);
        var loopPoints = new List<List<Vector3d>>(face.Loops.Count);
        var windings = new List<int>(face.Loops.Count);
        foreach (var loop in face.Loops)
        {
            var points = BRepTessellator.LoopPolyline(loop, edgePolylines);
            if (points.Count < 3)
                return false;
            var uv = new List<Vector2d>(points.Count);
            foreach (var p in points)
            {
                if (!surface.TryProjectPoint(p, out var q, 1e-6))
                    return false;
                if (period > 0 && uv.Count > 0)
                    q = new Vector2d(q.X + period * Math.Round((uv[^1].X - q.X) / period), q.Y);
                uv.Add(q);
            }
            int winding = period > 0 ? (int)Math.Round((uv[^1].X - uv[0].X) / period) : 0;
            if (Math.Abs(winding) > 1)
                return false;
            loopUv.Add(uv);
            loopPoints.Add(points);
            windings.Add(winding);
        }

        var uvAll = new List<Vector2d>();
        var pointsAll = new List<Vector3d>();
        var boundaryEdges = new HashSet<(int, int)>();
        List<(int A, int B, int C)>? triangles;
        if (windings.All(w => w == 0))
        {
            triangles = TriangulateRegion(loopUv, loopPoints, period, uvAll, pointsAll, boundaryEdges);
        }
        else
        {
            // Band-like: winding loops zip as strips; extra hole loops are unsupported.
            if (windings.Any(w => w == 0))
                return false;
            triangles = TriangulateBand(surface, period, loopUv, loopPoints, windings, uvAll, pointsAll, boundaryEdges);
        }
        if (triangles is null || triangles.Count == 0)
            return false;

        // 2. Refine oversized interior edges to the natural grid density so the surface
        //    keeps its curvature between distant boundary samples. A refinement that
        //    cannot converge must fail the whole face — emitting a partially refined
        //    set would break the no-touch-on-failure contract above.
        var (stepU, stepV) = NaturalSteps(surface, segmentsPerCircle, curveSamples);
        if (!Refine(surface, period, uvAll, pointsAll, triangles, boundaryEdges, stepU, stepV))
            return false;

        foreach (var (a, b, c) in triangles)
            polygons.Add([pointsAll[a], pointsAll[b], pointsAll[c]]);
        return true;
    }

    // ---- non-wrapping regions: exact ear clipping ----

    /// <summary>
    /// Triangulates a plain (non-wrapping) region: outer loop plus holes, ear-clipped on
    /// exact coordinates. Fills the shared vertex arrays and the boundary edge set, and
    /// verifies every boundary sample survived into the triangulation — a dropped vertex
    /// would leave a chord across the curved boundary that welding cannot close.
    /// </summary>
    private static List<(int A, int B, int C)>? TriangulateRegion(
        List<List<Vector2d>> loopUv,
        List<List<Vector3d>> loopPoints,
        double period,
        List<Vector2d> uvAll,
        List<Vector3d> pointsAll,
        HashSet<(int, int)> boundaryEdges)
    {
        // Bring hole loops into the outer loop's period window.
        if (period > 0 && loopUv.Count > 1)
        {
            double outerMid = loopUv[0].Average(p => p.X);
            for (int i = 1; i < loopUv.Count; i++)
            {
                double shift = period * Math.Round((outerMid - loopUv[i].Average(p => p.X)) / period);
                if (shift != 0)
                {
                    for (int j = 0; j < loopUv[i].Count; j++)
                        loopUv[i][j] = new Vector2d(loopUv[i][j].X + shift, loopUv[i][j].Y);
                }
            }
        }

        var rings = new List<List<int>>();
        for (int i = 0; i < loopUv.Count; i++)
        {
            int start = uvAll.Count;
            uvAll.AddRange(loopUv[i]);
            pointsAll.AddRange(loopPoints[i]);
            rings.Add([.. Enumerable.Range(start, loopUv[i].Count)]);
            for (int j = 0; j < loopUv[i].Count; j++)
                boundaryEdges.Add(EdgeKey(start + j, start + (j + 1) % loopUv[i].Count));
        }

        double extent = Math.Max(
            uvAll.Max(p => p.X) - uvAll.Min(p => p.X),
            uvAll.Max(p => p.Y) - uvAll.Min(p => p.Y));
        if (extent <= 0)
            return null;
        if (Math.Abs(FaceGeometry.LoopSignedArea(loopUv[0])) < 1e-12 * extent * extent)
            return null;

        var triangles = EarClip(uvAll, rings);
        if (triangles is null)
            return null;

        var used = new bool[uvAll.Count];
        foreach (var (a, b, c) in triangles)
        {
            used[a] = true;
            used[b] = true;
            used[c] = true;
        }
        return Array.IndexOf(used, false) >= 0 ? null : triangles;
    }

    // ---- band-like regions: strip zipping ----

    /// <summary>
    /// Triangulates a band-like region whose loops wind the periodic direction: two
    /// opposite-winding ring chains are zipped into a triangle strip by a monotone
    /// merge walk (like the cylinder band path, but tolerating rings subdivided into
    /// arcs with unrelated sample phases); a single winding chain fans to the surface's
    /// pole. Each chain gains a closure duplicate (same exact 3D point, uv one period
    /// on), so the strip's first and last cross edges weld to each other; those two
    /// cross edges are marked as boundary so refinement can never subdivide the welded
    /// pair inconsistently.
    /// </summary>
    private static List<(int A, int B, int C)>? TriangulateBand(
        Surface surface,
        double period,
        List<List<Vector2d>> loopUv,
        List<List<Vector3d>> loopPoints,
        List<int> windings,
        List<Vector2d> uvAll,
        List<Vector3d> pointsAll,
        HashSet<(int, int)> boundaryEdges)
    {
        if (loopUv.Count is not (1 or 2))
            return null;

        // Build each chain oriented by increasing u, with a closing duplicate vertex.
        List<int> BuildChain(int loopIndex, double alignToU)
        {
            var uv = loopUv[loopIndex];
            var pts = loopPoints[loopIndex];
            int n = uv.Count;
            var order = new int[n];
            for (int i = 0; i < n; i++)
                order[i] = windings[loopIndex] > 0 ? i : n - 1 - i;

            // Rotate the chain to start at the vertex closest above the alignment phase.
            int startAt = 0;
            double bestOffset = double.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                double offset = uv[order[i]].X - alignToU;
                offset -= period * Math.Floor(offset / period); // into [0, period)
                if (offset < bestOffset)
                {
                    bestOffset = offset;
                    startAt = i;
                }
            }

            var chain = new List<int>(n + 1);
            double previousU = alignToU + bestOffset;
            for (int i = 0; i < n; i++)
            {
                int src = order[(startAt + i) % n];
                double u = uv[src].X;
                u += period * Math.Round((previousU - u) / period);
                chain.Add(uvAll.Count);
                uvAll.Add(new Vector2d(u, uv[src].Y));
                pointsAll.Add(pts[src]);
                previousU = u;
            }
            // Closure duplicate: same exact 3D point, one period on.
            int first = chain[0];
            chain.Add(uvAll.Count);
            uvAll.Add(new Vector2d(uvAll[first].X + period, uvAll[first].Y));
            pointsAll.Add(pointsAll[first]);

            for (int i = 0; i + 1 < chain.Count; i++)
                boundaryEdges.Add(EdgeKey(chain[i], chain[i + 1]));
            return chain;
        }

        var triangles = new List<(int, int, int)>();
        if (loopUv.Count == 2)
        {
            if (windings[0] != -windings[1])
                return null;
            double anchor = loopUv[0][0].X;
            var first = BuildChain(0, anchor);
            var second = BuildChain(1, anchor);

            // Bottom chain = lower mean v; triangles CCW in (u, v).
            bool firstIsBottom =
                loopUv[0].Average(p => p.Y) <= loopUv[1].Average(p => p.Y);
            var bottom = firstIsBottom ? first : second;
            var top = firstIsBottom ? second : first;
            boundaryEdges.Add(EdgeKey(bottom[0], top[0]));
            boundaryEdges.Add(EdgeKey(bottom[^1], top[^1]));

            int i = 0, j = 0;
            while (i < bottom.Count - 1 || j < top.Count - 1)
            {
                bool advanceBottom =
                    i < bottom.Count - 1 &&
                    (j >= top.Count - 1 || uvAll[bottom[i + 1]].X <= uvAll[top[j + 1]].X);
                if (advanceBottom)
                {
                    triangles.Add((bottom[i], bottom[i + 1], top[j]));
                    i++;
                }
                else
                {
                    triangles.Add((bottom[i], top[j + 1], top[j]));
                    j++;
                }
            }
        }
        else
        {
            // A single winding loop bounds a pole cap: verify the far v row is
            // degenerate and fan the chain to the pole point.
            var domainV = surface.DomainV;
            double meanV = loopUv[0].Average(p => p.Y);
            double vFar = Math.Abs(meanV - domainV.Start) > Math.Abs(domainV.End - meanV)
                ? domainV.Start
                : domainV.End;
            if (!double.IsFinite(vFar))
                return null;
            var pole = surface.PointAt(surface.DomainU.Start, vFar);
            for (int k = 1; k <= 2; k++)
            {
                if (surface.PointAt(surface.DomainU.Start + period * k / 3.0, vFar)
                    .DistanceSquaredTo(pole) > 1e-18)
                    return null;
            }

            var chain = BuildChain(0, loopUv[0][0].X);
            int poleIndex = uvAll.Count;
            uvAll.Add(new Vector2d(uvAll[chain[0]].X + period / 2, vFar));
            pointsAll.Add(pole);
            boundaryEdges.Add(EdgeKey(chain[0], poleIndex));
            boundaryEdges.Add(EdgeKey(chain[^1], poleIndex));

            bool poleBelow = vFar < meanV;
            for (int i = 0; i + 1 < chain.Count; i++)
            {
                triangles.Add(poleBelow
                    ? (chain[i + 1], chain[i], poleIndex)
                    : (chain[i], chain[i + 1], poleIndex));
            }
        }
        return triangles;
    }

    // ---- exact ear clipping ----

    /// <summary>
    /// Ear clipping over index rings (outer first, holes after) with exact coordinates.
    /// Holes are bridged into the outer ring via mutually visible vertices (earcut's
    /// approach, with a conservative visibility test). Triangles come out CCW in uv
    /// regardless of the input winding. Returns null when no ear can be clipped
    /// (degenerate input) so the caller can fall back.
    /// </summary>
    private static List<(int A, int B, int C)>? EarClip(List<Vector2d> uv, List<List<int>> rings)
    {
        double RingArea(List<int> ring)
        {
            double area = 0;
            for (int i = 0; i < ring.Count; i++)
                area += uv[ring[i]].Cross(uv[ring[(i + 1) % ring.Count]]);
            return area / 2;
        }

        var outer = new List<int>(rings[0]);
        if (RingArea(outer) < 0)
            outer.Reverse();
        var holes = rings.Skip(1)
            .Select(r =>
            {
                var hole = new List<int>(r);
                if (RingArea(hole) > 0)
                    hole.Reverse();
                return hole;
            })
            .OrderBy(h => h.Min(i => uv[i].X))
            .ToList();

        foreach (var hole in holes)
        {
            if (!SpliceHole(uv, outer, hole, holes))
                return null;
        }

        // Inverse evaluation carries ~1e-9 jitter, so "on the boundary" needs a band:
        // a vertex a hair outside a candidate ear must still block it — otherwise the
        // ear's diagonal cuts that vertex off and the remaining polygon self-intersects.
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (var ring in rings)
        {
            foreach (int i in ring)
            {
                minX = Math.Min(minX, uv[i].X);
                maxX = Math.Max(maxX, uv[i].X);
                minY = Math.Min(minY, uv[i].Y);
                maxY = Math.Max(maxY, uv[i].Y);
            }
        }
        double blockBand = 1e-8 * Math.Max(maxX - minX, maxY - minY);

        var polygon = outer;
        var triangles = new List<(int, int, int)>(polygon.Count);
        while (polygon.Count > 3)
        {
            // Shortest-diagonal-first: clipping the ear with the shortest cut keeps the
            // triangulation zigzagging along boundary runs instead of fanning from the
            // corners — fans breed long interior diagonals that the curvature
            // refinement would then have to subdivide quadratically.
            int best = -1;
            double bestDiagonal = double.PositiveInfinity;
            for (int ib = 0; ib < polygon.Count; ib++)
            {
                int ia = (ib + polygon.Count - 1) % polygon.Count;
                int ic = (ib + 1) % polygon.Count;
                var diagonal = uv[polygon[ic]] - uv[polygon[ia]];
                double length = diagonal.Dot(diagonal);
                if (length >= bestDiagonal || !IsEar(uv, polygon, ia, ib, ic, blockBand))
                    continue;
                best = ib;
                bestDiagonal = length;
            }
            if (best < 0)
                return null;
            int prev = (best + polygon.Count - 1) % polygon.Count;
            int next = (best + 1) % polygon.Count;
            triangles.Add((polygon[prev], polygon[best], polygon[next]));
            polygon.RemoveAt(best);
        }
        // The final triangle can be collinear when the leftover region has zero area
        // (everything real is already covered); emit only genuine area.
        var pa = uv[polygon[0]];
        var pb = uv[polygon[1]];
        var pc = uv[polygon[2]];
        if ((pb - pa).Cross(pc - pb) > 0)
            triangles.Add((polygon[0], polygon[1], polygon[2]));
        return triangles;
    }

    /// <summary>
    /// Strictly convex corner with no other polygon vertex inside or within
    /// <paramref name="blockBand"/> of the closed ear triangle. Points coincident with
    /// an ear corner (hole-bridge duplicates) do not block — the diagonal merely ends
    /// at their position.
    /// </summary>
    private static bool IsEar(List<Vector2d> uv, List<int> polygon, int ia, int ib, int ic, double blockBand)
    {
        var a = uv[polygon[ia]];
        var b = uv[polygon[ib]];
        var c = uv[polygon[ic]];
        if ((b - a).Cross(c - b) <= 0)
            return false; // reflex or exactly straight — never emit zero-area ears

        double ab = (b - a).Length, bc = (c - b).Length, ca = (a - c).Length;
        for (int j = 0; j < polygon.Count; j++)
        {
            if (j == ia || j == ib || j == ic)
                continue;
            var p = uv[polygon[j]];
            if (Coincident(p, a) || Coincident(p, b) || Coincident(p, c))
                continue;
            if ((b - a).Cross(p - a) >= -blockBand * ab &&
                (c - b).Cross(p - b) >= -blockBand * bc &&
                (a - c).Cross(p - c) >= -blockBand * ca)
                return false; // inside the ear, or within the jitter band of its edges
        }
        return true;
    }

    private static bool Coincident(in Vector2d p, in Vector2d q) => p.X == q.X && p.Y == q.Y;

    /// <summary>
    /// Connects a hole into the outer polygon through a mutually visible vertex pair,
    /// duplicating both bridge endpoints (the polygon becomes weakly simple). Pairs are
    /// tried closest-first; a pair is visible when its segment touches no ring edge and
    /// its midpoint lies inside the region.
    /// </summary>
    private static bool SpliceHole(List<Vector2d> uv, List<int> outer, List<int> hole, List<List<int>> allHoles)
    {
        var pairs = new List<(double DistanceSquared, int OuterAt, int HoleAt)>();
        for (int i = 0; i < outer.Count; i++)
        {
            for (int j = 0; j < hole.Count; j++)
            {
                var d = uv[outer[i]] - uv[hole[j]];
                pairs.Add((d.Dot(d), i, j));
            }
        }
        pairs.Sort((x, y) => x.DistanceSquared.CompareTo(y.DistanceSquared));

        foreach (var (_, outerAt, holeAt) in pairs)
        {
            var p = uv[outer[outerAt]];
            var q = uv[hole[holeAt]];
            if (Coincident(p, q))
                continue;
            bool blocked = false;
            foreach (var ring in allHoles.Append(outer))
            {
                for (int e = 0; e < ring.Count && !blocked; e++)
                {
                    var a = uv[ring[e]];
                    var b = uv[ring[(e + 1) % ring.Count]];
                    if (Coincident(a, p) || Coincident(b, p) || Coincident(a, q) || Coincident(b, q))
                        continue; // incident at an endpoint
                    if (SegmentsTouch(p, q, a, b))
                        blocked = true;
                }
                if (blocked)
                    break;
            }
            if (blocked)
                continue;
            var mid = new Vector2d((p.X + q.X) / 2, (p.Y + q.Y) / 2);
            if (!InsideRegion(uv, outer, allHoles, hole, mid))
                continue;

            // outer: [..., o] + [h, hole walk..., h dup] + [o dup, ...]
            var insertion = new List<int>(hole.Count + 2);
            for (int k = 0; k <= hole.Count; k++)
                insertion.Add(hole[(holeAt + k) % hole.Count]);
            insertion.Add(outer[outerAt]);
            outer.InsertRange(outerAt + 1, insertion);
            return true;
        }
        return false;
    }

    /// <summary>Conservative segment test: any contact (crossing, touching, collinear overlap) counts.</summary>
    private static bool SegmentsTouch(in Vector2d p, in Vector2d q, in Vector2d a, in Vector2d b)
    {
        var pq = q - p;
        var ab = b - a;
        double d1 = pq.Cross(a - p);
        double d2 = pq.Cross(b - p);
        double d3 = ab.Cross(p - a);
        double d4 = ab.Cross(q - a);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
            ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            return true;
        bool OnSegment(in Vector2d s0, in Vector2d s1, in Vector2d x) =>
            Math.Min(s0.X, s1.X) <= x.X && x.X <= Math.Max(s0.X, s1.X) &&
            Math.Min(s0.Y, s1.Y) <= x.Y && x.Y <= Math.Max(s0.Y, s1.Y);
        if (d1 == 0 && OnSegment(p, q, a))
            return true;
        if (d2 == 0 && OnSegment(p, q, b))
            return true;
        if (d3 == 0 && OnSegment(a, b, p))
            return true;
        if (d4 == 0 && OnSegment(a, b, q))
            return true;
        return false;
    }

    /// <summary>Point strictly inside the outer ring and outside every hole except <paramref name="beingSpliced"/>.</summary>
    private static bool InsideRegion(
        List<Vector2d> uv, List<int> outer, List<List<int>> holes, List<int> beingSpliced, Vector2d point)
    {
        bool Inside(List<int> ring)
        {
            int crossings = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = uv[ring[i]];
                var b = uv[ring[(i + 1) % ring.Count]];
                if (a.X <= point.X == b.X <= point.X)
                    continue;
                double t = (point.X - a.X) / (b.X - a.X);
                if (a.Y + t * (b.Y - a.Y) > point.Y)
                    crossings++;
            }
            return (crossings & 1) == 1;
        }
        if (!Inside(outer))
            return false;
        foreach (var hole in holes)
        {
            if (!ReferenceEquals(hole, beingSpliced) && Inside(hole))
                return false;
        }
        return true;
    }

    // ---- refinement ----

    /// <summary>
    /// Natural grid spacing per parameter direction, mirroring the grid path's sampling
    /// rules; infinite where the surface is ruled in that direction (chords are exact).
    /// </summary>
    private static (double U, double V) NaturalSteps(Surface surface, int segmentsPerCircle, int curveSamples)
    {
        double FromCurve(Curve3d c) => c.Underlying is Line3d
            ? double.PositiveInfinity
            : c.Domain.Length / (c.IsClosed && c.Underlying is Circle3d ? segmentsPerCircle : curveSamples);
        return surface switch
        {
            CylinderSurface => (2 * Math.PI / segmentsPerCircle, double.PositiveInfinity),
            ExtrudedSurface e => (FromCurve(e.Generator), double.PositiveInfinity),
            RevolvedSurface r => (
                r.DomainU.Length / (r.IsFullTurn ? segmentsPerCircle : curveSamples),
                FromCurve(r.Generator)),
            SweptSurface s => (FromCurve(s.Generator), s.DomainV.Length / curveSamples),
            _ => (double.PositiveInfinity, double.PositiveInfinity),
        };
    }

    /// <summary>
    /// Splits interior edges longer than one natural grid step (measured per-axis in
    /// step units) at their uv midpoints, lifting new vertices onto the exact surface.
    /// Boundary edges are never split — their chords are the shared seam geometry.
    /// Termination is enforced by a monotone-decrease rule: a newly created edge is
    /// only queued when it is strictly shorter than the edge whose split created it.
    /// Plain midpoint bisection lacks this property — the median to the opposite
    /// vertex can be as long as the split edge, and on the skinny triangles a band
    /// zip makes from a v-excursioned chain against a sparse ring that becomes a
    /// self-sustaining cascade (each split enqueues more same-length edges) whose
    /// ever-closer midpoints eventually weld non-manifold. Some interior edges may
    /// stay oversized where the rule stops a cascade — a fidelity trade, never a
    /// correctness one. Returns false if the safety guard still trips, so the caller
    /// abandons the face instead of emitting a partially refined set.
    /// </summary>
    private static bool Refine(
        Surface surface,
        double period,
        List<Vector2d> uv,
        List<Vector3d> points,
        List<(int A, int B, int C)> triangles,
        HashSet<(int, int)> boundaryEdges,
        double stepU,
        double stepV)
    {
        if (double.IsInfinity(stepU) && double.IsInfinity(stepV))
            return true;

        double MetricSquared((int, int) e)
        {
            double du = double.IsInfinity(stepU) ? 0 : (uv[e.Item2].X - uv[e.Item1].X) / stepU;
            double dv = double.IsInfinity(stepV) ? 0 : (uv[e.Item2].Y - uv[e.Item1].Y) / stepV;
            return du * du + dv * dv;
        }
        bool Oversized((int, int) e) => MetricSquared(e) > 1 + 1e-9;

        var edgeOwners = new Dictionary<(int, int), List<int>>();
        void Register(int triangle, (int, int) key)
        {
            if (!edgeOwners.TryGetValue(key, out var owners))
                edgeOwners[key] = owners = [];
            owners.Add(triangle);
        }
        for (int t = 0; t < triangles.Count; t++)
        {
            var (a, b, c) = triangles[t];
            Register(t, EdgeKey(a, b));
            Register(t, EdgeKey(b, c));
            Register(t, EdgeKey(c, a));
        }

        static (int A, int B, int C) Rotate((int A, int B, int C) triangle, (int, int) key)
        {
            var (a, b, c) = triangle;
            if (EdgeKey(a, b) == key)
                return (a, b, c);
            if (EdgeKey(b, c) == key)
                return (b, c, a);
            if (EdgeKey(c, a) == key)
                return (c, a, b);
            return (-1, -1, -1); // already rewritten by a previous split
        }

        var queue = new Queue<(int, int)>(
            edgeOwners.Keys.Where(k => !boundaryEdges.Contains(k) && Oversized(k)));
        int guard = 200000;
        while (queue.Count > 0)
        {
            if (guard-- <= 0)
                return false; // refinement did not converge — abandon the face
            var key = queue.Dequeue();
            if (!edgeOwners.TryGetValue(key, out var owners) || !Oversized(key))
                continue;
            double parentMetric = MetricSquared(key);

            var mid = new Vector2d(
                (uv[key.Item1].X + uv[key.Item2].X) / 2,
                (uv[key.Item1].Y + uv[key.Item2].Y) / 2);
            var midPoint = EvaluateAt(surface, period, mid);

            // Inverse-evaluation jitter (~1e-9) makes iso-parameter runs only almost
            // collinear, so a chord can skip a boundary vertex by a hair; its midpoint
            // then lands exactly on that vertex's 3D position, and creating a second
            // vertex there would weld the mesh non-manifold. Snap to the coincident
            // opposite vertex instead and drop the degenerate sliver.
            int midIndex = -1;
            foreach (int t in owners)
            {
                var (_, _, c) = Rotate(triangles[t], key);
                if (c >= 0 && points[c].DistanceSquaredTo(midPoint) <= 1e-16)
                {
                    midIndex = c;
                    break;
                }
            }
            if (midIndex < 0)
            {
                midIndex = uv.Count;
                uv.Add(mid);
                points.Add(midPoint);
            }

            edgeOwners.Remove(key);
            foreach (int t in owners.ToList())
            {
                var (a, b, c) = Rotate(triangles[t], key);
                if (a < 0)
                    continue;
                if (c == midIndex)
                {
                    // The chord ran exactly through c: the sliver between them vanishes.
                    edgeOwners.TryGetValue(EdgeKey(b, c), out var bcOwners);
                    bcOwners?.Remove(t);
                    edgeOwners.TryGetValue(EdgeKey(c, a), out var caOwners);
                    caOwners?.Remove(t);
                    triangles[t] = (-1, -1, -1);
                    continue;
                }
                int fresh = triangles.Count;
                triangles[t] = (a, midIndex, c);
                triangles.Add((midIndex, b, c));

                // (c, a) stays with t; (b, c) moves to the fresh triangle.
                var moved = EdgeKey(b, c);
                if (edgeOwners.TryGetValue(moved, out var movedOwners))
                {
                    movedOwners.Remove(t);
                    movedOwners.Add(fresh);
                }
                Register(t, EdgeKey(a, midIndex));
                Register(t, EdgeKey(midIndex, c));
                Register(fresh, EdgeKey(midIndex, b));
                Register(fresh, EdgeKey(c, midIndex));

                foreach (var candidate in (ReadOnlySpan<(int, int)>)
                    [EdgeKey(a, midIndex), EdgeKey(midIndex, b), EdgeKey(midIndex, c)])
                {
                    // Monotone decrease: only strictly shorter children may continue
                    // the refinement (see the method remarks on termination).
                    if (!boundaryEdges.Contains(candidate) && Oversized(candidate) &&
                        MetricSquared(candidate) <= 0.99 * parentMetric)
                        queue.Enqueue(candidate);
                }
            }
        }
        triangles.RemoveAll(t => t.A < 0);
        return true;
    }

    /// <summary>Evaluates the surface at an unwrapped uv (periodic u brought back into the domain).</summary>
    private static Vector3d EvaluateAt(Surface surface, double period, in Vector2d uv)
    {
        double u = uv.X;
        var domainU = surface.DomainU;
        if (period > 0)
            u = domainU.Start + (((u - domainU.Start) % period) + period) % period;
        else
            u = domainU.Clamp(u);
        return surface.PointAt(u, surface.DomainV.Clamp(uv.Y));
    }

    private static (int, int) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);
}
