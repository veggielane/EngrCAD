using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Ear-clipping triangulation of simple 2D polygons, with hole support via bridge edges
/// (each hole is connected to the outer boundary through a mutually visible vertex pair,
/// then the merged polygon is ear-clipped — the earcut approach). O(n²); intended for
/// boundary loops from tessellation. Output triangles are counter-clockwise in the 2D
/// plane regardless of input orientation.
/// </summary>
public static class PolygonTriangulator
{
    public static List<(int A, int B, int C)> Triangulate(IReadOnlyList<Vector2d> polygon)
    {
        if (polygon.Count < 3)
            throw new ArgumentException("Polygon needs at least 3 vertices.", nameof(polygon));

        var loop = new List<int>(polygon.Count);
        for (int i = 0; i < polygon.Count; i++)
            loop.Add(i);
        if (SignedArea(polygon, loop) < 0)
            loop.Reverse();
        return EarClip(polygon, loop);
    }

    /// <summary>
    /// Triangulates an outer polygon with holes. Returned indices refer to the
    /// concatenation [outer..., holes[0]..., holes[1]...] in the given order.
    /// </summary>
    public static List<(int A, int B, int C)> TriangulateWithHoles(
        IReadOnlyList<Vector2d> outer, IReadOnlyList<IReadOnlyList<Vector2d>> holes)
    {
        var points = new List<Vector2d>(outer);
        var loop = new List<int>(outer.Count);
        for (int i = 0; i < outer.Count; i++)
            loop.Add(i);
        if (SignedArea(points, loop) < 0)
            loop.Reverse();

        // Hole index loops, wound clockwise, processed right-to-left so a bridge is never
        // blocked by a not-yet-merged hole.
        var holeLoops = new List<List<int>>();
        foreach (var hole in holes)
        {
            var holeLoop = new List<int>(hole.Count);
            foreach (var p in hole)
            {
                holeLoop.Add(points.Count);
                points.Add(p);
            }
            if (SignedArea(points, holeLoop) > 0)
                holeLoop.Reverse();
            holeLoops.Add(holeLoop);
        }
        foreach (var holeLoop in holeLoops.OrderByDescending(h => h.Max(i => points[i].X)))
            loop = SpliceHole(points, loop, holeLoop);

        return EarClip(points, loop);
    }

    /// <summary>Connects a hole to the outer loop through a bridge at a mutually visible vertex pair.</summary>
    private static List<int> SpliceHole(List<Vector2d> points, List<int> outer, List<int> hole)
    {
        // Rightmost hole vertex, and a +x ray from it toward the outer boundary.
        int mSlot = 0;
        for (int i = 1; i < hole.Count; i++)
        {
            if (points[hole[i]].X > points[hole[mSlot]].X)
                mSlot = i;
        }
        var m = points[hole[mSlot]];

        double bestX = double.PositiveInfinity;
        int bridgeSlot = -1;
        for (int i = 0; i < outer.Count; i++)
        {
            int j = (i + 1) % outer.Count;
            var a = points[outer[i]];
            var b = points[outer[j]];
            if (a.Y == b.Y)
                continue;
            double t = (m.Y - a.Y) / (b.Y - a.Y);
            if (t < 0 || t > 1)
                continue;
            double x = a.X + t * (b.X - a.X);
            if (x < m.X - 1e-12 || x >= bestX)
                continue;
            bestX = x;
            // If the ray hits an endpoint exactly, that vertex is the connection (bridging
            // to the other endpoint could cut through a previously merged hole). Otherwise
            // the candidate is the intersected edge's endpoint with the larger x.
            if (Math.Abs(a.Y - m.Y) < 1e-12 && Math.Abs(a.X - x) < 1e-9)
                bridgeSlot = i;
            else if (Math.Abs(b.Y - m.Y) < 1e-12 && Math.Abs(b.X - x) < 1e-9)
                bridgeSlot = j;
            else
                bridgeSlot = a.X > b.X ? i : j;
        }
        if (bridgeSlot < 0)
            throw new ArgumentException("Hole lies outside the outer polygon.");

        // If a reflex outer vertex sits inside the triangle (m, intersection, candidate),
        // it blocks visibility — bridge to the blocking vertex closest in angle to +x.
        var intersection = new Vector2d(bestX, m.Y);
        var candidate = points[outer[bridgeSlot]];
        double bestMetric = double.PositiveInfinity;
        for (int i = 0; i < outer.Count; i++)
        {
            var p = points[outer[i]];
            if (p.X < m.X || i == bridgeSlot)
                continue;
            var prev = points[outer[(i - 1 + outer.Count) % outer.Count]];
            var next = points[outer[(i + 1) % outer.Count]];
            bool reflex = (p - prev).Cross(next - p) < 0;
            if (!reflex || !PointInTriangle(p, m, intersection, candidate))
                continue;
            double dx = p.X - m.X;
            double metric = Math.Abs(p.Y - m.Y) / Math.Max(dx, 1e-12); // tan of angle from +x
            if (metric < bestMetric)
            {
                bestMetric = metric;
                bridgeSlot = i;
            }
        }

        // Merge: ... bridge, hole cycle from m, back to m, back to bridge, ...
        var merged = new List<int>(outer.Count + hole.Count + 2);
        merged.AddRange(outer.Take(bridgeSlot + 1));
        for (int k = 0; k <= hole.Count; k++)
            merged.Add(hole[(mSlot + k) % hole.Count]);
        merged.Add(outer[bridgeSlot]);
        merged.AddRange(outer.Skip(bridgeSlot + 1));
        return merged;
    }

    private static List<(int A, int B, int C)> EarClip(IReadOnlyList<Vector2d> points, List<int> loop)
    {
        var triangles = new List<(int, int, int)>(Math.Max(0, loop.Count - 2));
        int guard = 0;
        int guardLimit = loop.Count * loop.Count + 16;
        while (loop.Count > 3)
        {
            bool clipped = false;
            for (int i = 0; i < loop.Count; i++)
            {
                int prev = loop[(i - 1 + loop.Count) % loop.Count];
                int curr = loop[i];
                int next = loop[(i + 1) % loop.Count];

                var a = points[prev];
                var b = points[curr];
                var c = points[next];
                if ((b - a).Cross(c - b) <= 0)
                    continue; // reflex or degenerate corner

                bool containsOther = false;
                for (int s = 0; s < loop.Count; s++)
                {
                    // Skip the ear's own three slots (by position, not value — bridge
                    // splicing duplicates vertices and each occurrence is distinct).
                    if (s == (i - 1 + loop.Count) % loop.Count || s == i || s == (i + 1) % loop.Count)
                        continue;
                    var p = points[loop[s]];
                    // Only reflex (or straight) vertices can block an ear, and a bridge
                    // duplicate coinciding with the ear's first corner never does — the
                    // earcut rules.
                    if ((p - a).LengthSquared < 1e-24)
                        continue;
                    var sPrev = points[loop[(s - 1 + loop.Count) % loop.Count]];
                    var sNext = points[loop[(s + 1) % loop.Count]];
                    if ((p - sPrev).Cross(sNext - p) > 0)
                        continue;
                    if (PointInTriangle(p, a, b, c))
                    {
                        containsOther = true;
                        break;
                    }
                }
                if (containsOther)
                    continue;

                if ((b - a).Cross(c - a) > 1e-24)
                    triangles.Add((prev, curr, next));
                loop.RemoveAt(i);
                clipped = true;
                break;
            }

            if (!clipped)
            {
                // No ear exists — typical when several bridge corridors meet at one
                // vertex whose reflex duplicates block every ear. Split the polygon along
                // a valid internal diagonal and recurse on the halves (earcut's
                // splitEarcut recovery).
                if (TrySplit(points, loop, out var first, out var second))
                {
                    triangles.AddRange(EarClip(points, first));
                    triangles.AddRange(EarClip(points, second));
                    return triangles;
                }
                // Last resort for degenerate slivers: fan what remains.
                for (int i = 1; i < loop.Count - 1; i++)
                    triangles.Add((loop[0], loop[i], loop[i + 1]));
                return triangles;
            }
            if (++guard > guardLimit)
                throw new InvalidOperationException("Ear clipping failed to terminate.");
        }

        if (loop.Count == 3)
        {
            var (a, b, c) = (points[loop[0]], points[loop[1]], points[loop[2]]);
            if ((b - a).Cross(c - a) > 1e-24)
                triangles.Add((loop[0], loop[1], loop[2]));
        }
        return triangles;
    }

    /// <summary>Finds a diagonal fully inside the polygon and cuts the loop into two loops along it.</summary>
    private static bool TrySplit(
        IReadOnlyList<Vector2d> points, List<int> loop, out List<int> first, out List<int> second)
    {
        int n = loop.Count;

        // Pinch split: two occurrences of the same coordinates (bridge anchors) can
        // separate the loop into two rings sharing only that point — no new edges.
        for (int i = 0; i < n; i++)
        {
            for (int offset = 2; offset <= n - 2; offset++)
            {
                int j = (i + offset) % n;
                if ((points[loop[j]] - points[loop[i]]).LengthSquared >= 1e-24)
                    continue;
                if (TryTakeSplit(points, loop, i, j, out first, out second))
                    return true;
            }
        }

        for (int i = 0; i < n; i++)
        {
            for (int offset = 2; offset <= n - 2; offset++)
            {
                int j = (i + offset) % n;
                var pi = points[loop[i]];
                var pj = points[loop[j]];
                if ((pj - pi).LengthSquared < 1e-24)
                    continue;
                if (!LocallyInside(points, loop, i, pj) || !LocallyInside(points, loop, j, pi))
                    continue;

                // No loop vertex may sit on the open diagonal (touching a vertex pinches
                // the halves in a way ear clipping cannot recover from).
                bool touchesVertex = false;
                var dir = pj - pi;
                double lengthSquared = dir.LengthSquared;
                for (int s = 0; s < n && !touchesVertex; s++)
                {
                    var p = points[loop[s]];
                    if ((p - pi).LengthSquared < 1e-24 || (p - pj).LengthSquared < 1e-24)
                        continue;
                    double t = (p - pi).Dot(dir) / lengthSquared;
                    if (t <= 0 || t >= 1)
                        continue;
                    touchesVertex = ((p - pi) - dir * t).LengthSquared < 1e-18;
                }
                if (touchesVertex)
                    continue;

                // A diagonal that coincides (by coordinates) with an existing edge —
                // e.g. between bridge duplicates — would duplicate that edge in a half.
                bool coincidesWithEdge = false;
                for (int s = 0; s < n && !coincidesWithEdge; s++)
                {
                    var a = points[loop[s]];
                    var b = points[loop[(s + 1) % n]];
                    coincidesWithEdge =
                        ((a - pi).LengthSquared < 1e-24 && (b - pj).LengthSquared < 1e-24) ||
                        ((a - pj).LengthSquared < 1e-24 && (b - pi).LengthSquared < 1e-24);
                }
                if (coincidesWithEdge)
                    continue;

                bool blocked = false;
                for (int s = 0; s < n && !blocked; s++)
                {
                    var a = points[loop[s]];
                    var b = points[loop[(s + 1) % n]];
                    // Edges touching a diagonal endpoint (by coordinate — bridge
                    // duplicates included) are judged by the sector tests instead.
                    if ((a - pi).LengthSquared < 1e-24 || (a - pj).LengthSquared < 1e-24 ||
                        (b - pi).LengthSquared < 1e-24 || (b - pj).LengthSquared < 1e-24)
                        continue;
                    if (SegmentsIntersect(pi, pj, a, b))
                        blocked = true;
                }
                if (blocked)
                    continue;

                if (TryTakeSplit(points, loop, i, j, out first, out second))
                    return true;
            }
        }
        first = second = null!;
        return false;
    }

    private static bool TryTakeSplit(
        IReadOnlyList<Vector2d> points, List<int> loop, int i, int j,
        out List<int> first, out List<int> second)
    {
        int n = loop.Count;
        first = new List<int>();
        for (int s = i; ; s = (s + 1) % n)
        {
            first.Add(loop[s]);
            if (s == j)
                break;
        }
        second = new List<int>();
        for (int s = j; ; s = (s + 1) % n)
        {
            second.Add(loop[s]);
            if (s == i)
                break;
        }
        // A valid interior split of a CCW loop yields two CCW halves.
        if (first.Count < 3 || second.Count < 3 ||
            SignedArea(points, first) <= 1e-12 || SignedArea(points, second) <= 1e-12)
        {
            first = second = null!;
            return false;
        }
        return true;
    }

    /// <summary>Whether the direction from the loop vertex at <paramref name="slot"/> toward <paramref name="target"/> enters the polygon interior.</summary>
    private static bool LocallyInside(IReadOnlyList<Vector2d> points, List<int> loop, int slot, in Vector2d target)
    {
        int n = loop.Count;
        var p = points[loop[slot]];
        var inDir = p - points[loop[(slot - 1 + n) % n]];
        var outDir = points[loop[(slot + 1) % n]] - p;
        var d = target - p;
        // Interior of a CCW loop lies to the LEFT of both incident edges: at a convex
        // corner the direction must be left of both; at a reflex corner, left of either.
        return inDir.Cross(outDir) >= 0
            ? inDir.Cross(d) > 1e-12 && outDir.Cross(d) > 1e-12   // convex corner
            : inDir.Cross(d) > 1e-12 || outDir.Cross(d) > 1e-12;  // reflex corner
    }

    private static bool SegmentsIntersect(in Vector2d p1, in Vector2d p2, in Vector2d q1, in Vector2d q2)
    {
        double d1 = (p2 - p1).Cross(q1 - p1);
        double d2 = (p2 - p1).Cross(q2 - p1);
        double d3 = (q2 - q1).Cross(p1 - q1);
        double d4 = (q2 - q1).Cross(p2 - q1);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
               ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    public static double SignedArea(IReadOnlyList<Vector2d> polygon)
    {
        var loop = new List<int>(polygon.Count);
        for (int i = 0; i < polygon.Count; i++)
            loop.Add(i);
        return SignedArea(polygon, loop);
    }

    private static double SignedArea(IReadOnlyList<Vector2d> points, List<int> loop)
    {
        double area = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            var p = points[loop[i]];
            var q = points[loop[(i + 1) % loop.Count]];
            area += p.Cross(q);
        }
        return area * 0.5;
    }

    private static bool PointInTriangle(in Vector2d p, in Vector2d a, in Vector2d b, in Vector2d c)
    {
        const double eps = 1e-12;
        double d1 = (b - a).Cross(p - a);
        double d2 = (c - b).Cross(p - b);
        double d3 = (a - c).Cross(p - c);
        return d1 >= -eps && d2 >= -eps && d3 >= -eps;
    }
}
