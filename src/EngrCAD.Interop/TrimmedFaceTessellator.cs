using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Interop;

/// <summary>
/// Parameter-space triangulation for trimmed faces on curved surfaces — faces whose loops
/// do not cover the surface's natural grid domain (fragments produced by
/// <see cref="FaceSplitter.SplitByCurve"/>, e.g. a bore wall cut through by a slot).
/// The loops' shared edge-polyline samples are pulled into (u, v) space and ear-clipped
/// with an exact-coordinate clipper, then oversized interior edges are midpoint-split
/// down to the surface's natural grid density with the new vertices evaluated on the
/// exact surface. Boundary vertices are the exact shared edge samples — never
/// re-evaluated approximations — so neighboring faces weld without cracks.
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
    /// inverse evaluation, a loop winds the periodic direction (band-like regions are the
    /// grid path's job), or clipping gets stuck on degenerate input.
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
        //    periodic u direction along the loop.
        var loopUv = new List<List<Vector2d>>(face.Loops.Count);
        var loopPoints = new List<List<Vector3d>>(face.Loops.Count);
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
            if (period > 0 && Math.Round((uv[^1].X - uv[0].X) / period) != 0)
                return false; // the loop winds the periodic direction
            loopUv.Add(uv);
            loopPoints.Add(points);
        }

        // 2. Bring hole loops into the outer loop's period window.
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

        // Combined vertex arrays in [outer..., hole0..., hole1...] order.
        var uvAll = new List<Vector2d>();
        var pointsAll = new List<Vector3d>();
        var rings = new List<List<int>>();
        var boundaryEdges = new HashSet<(int, int)>();
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
            return false;
        if (Math.Abs(FaceGeometry.LoopSignedArea(loopUv[0])) < 1e-12 * extent * extent)
            return false;

        // 3. Ear-clip on the exact coordinates.
        var triangles = EarClip(uvAll, rings);
        if (triangles is null || triangles.Count == 0)
            return false;

        // Every boundary sample must appear in the triangulation — a dropped vertex
        // would leave a chord across the curved boundary that welding cannot close.
        var used = new bool[uvAll.Count];
        foreach (var (a, b, c) in triangles)
        {
            used[a] = true;
            used[b] = true;
            used[c] = true;
        }
        if (Array.IndexOf(used, false) >= 0)
            return false;

        // 4. Refine oversized interior edges to the natural grid density so the surface
        //    keeps its curvature between distant boundary samples.
        var (stepU, stepV) = NaturalSteps(surface, segmentsPerCircle, curveSamples);
        Refine(surface, period, uvAll, pointsAll, triangles, boundaryEdges, stepU, stepV);

        foreach (var (a, b, c) in triangles)
            polygons.Add([pointsAll[a], pointsAll[b], pointsAll[c]]);
        return true;
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

        var polygon = outer;
        var triangles = new List<(int, int, int)>(polygon.Count);
        int cursor = 0;
        while (polygon.Count > 3)
        {
            bool clipped = false;
            for (int scan = 0; scan < polygon.Count; scan++)
            {
                int ib = (cursor + scan) % polygon.Count;
                int ia = (ib + polygon.Count - 1) % polygon.Count;
                int ic = (ib + 1) % polygon.Count;
                if (!IsEar(uv, polygon, ia, ib, ic))
                    continue;
                triangles.Add((polygon[ia], polygon[ib], polygon[ic]));
                polygon.RemoveAt(ib);
                cursor = ib % polygon.Count;
                clipped = true;
                break;
            }
            if (!clipped)
                return null;
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
    /// Strictly convex corner with no other polygon vertex inside or on the closed ear
    /// triangle. Points coincident with an ear corner (hole-bridge duplicates) do not
    /// block — the diagonal merely ends at their position.
    /// </summary>
    private static bool IsEar(List<Vector2d> uv, List<int> polygon, int ia, int ib, int ic)
    {
        var a = uv[polygon[ia]];
        var b = uv[polygon[ib]];
        var c = uv[polygon[ic]];
        if ((b - a).Cross(c - b) <= 0)
            return false; // reflex or exactly straight — never emit zero-area ears

        for (int j = 0; j < polygon.Count; j++)
        {
            if (j == ia || j == ib || j == ic)
                continue;
            var p = uv[polygon[j]];
            if (Coincident(p, a) || Coincident(p, b) || Coincident(p, c))
                continue;
            if ((b - a).Cross(p - a) >= 0 &&
                (c - b).Cross(p - b) >= 0 &&
                (a - c).Cross(p - c) >= 0)
                return false; // inside the ear, or exactly on one of its edges
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
    /// Repeatedly splits the worst interior edge longer than one natural grid step
    /// (measured per-axis in step units) at its uv midpoint, lifting the new vertex onto
    /// the exact surface. Boundary edges are never split — their chords are the shared
    /// seam geometry.
    /// </summary>
    private static void Refine(
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
            return;

        double MetricSquared((int, int) e)
        {
            double du = double.IsInfinity(stepU) ? 0 : (uv[e.Item2].X - uv[e.Item1].X) / stepU;
            double dv = double.IsInfinity(stepV) ? 0 : (uv[e.Item2].Y - uv[e.Item1].Y) / stepV;
            return du * du + dv * dv;
        }

        for (int guard = 0; guard < 20000; guard++)
        {
            (int, int) worst = (-1, -1);
            double worstMetric = 1 + 1e-9;
            foreach (var (a, b, c) in triangles)
            {
                Span<(int, int)> edges = [EdgeKey(a, b), EdgeKey(b, c), EdgeKey(c, a)];
                foreach (var key in edges)
                {
                    if (boundaryEdges.Contains(key))
                        continue;
                    double m = MetricSquared(key);
                    if (m > worstMetric)
                    {
                        worstMetric = m;
                        worst = key;
                    }
                }
            }
            if (worst.Item1 < 0)
                break;

            var mid = new Vector2d(
                (uv[worst.Item1].X + uv[worst.Item2].X) / 2,
                (uv[worst.Item1].Y + uv[worst.Item2].Y) / 2);
            int midIndex = uv.Count;
            uv.Add(mid);
            points.Add(EvaluateAt(surface, period, mid));

            int count = triangles.Count;
            for (int i = 0; i < count; i++)
            {
                var (a, b, c) = triangles[i];
                if (EdgeKey(b, c) == worst)
                    (a, b, c) = (b, c, a);
                else if (EdgeKey(c, a) == worst)
                    (a, b, c) = (c, a, b);
                else if (EdgeKey(a, b) != worst)
                    continue;
                triangles[i] = (a, midIndex, c);
                triangles.Add((midIndex, b, c));
            }
        }
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
