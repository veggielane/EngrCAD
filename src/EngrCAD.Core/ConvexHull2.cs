namespace EngrCAD.Core;

/// <summary>
/// 2D convex hull by Andrew's monotone chain: sort lexicographically, build the lower
/// and upper chains with cross-product turn tests. O(n log n); exactly-collinear edge
/// points are dropped, so the hull is strictly convex. The 2D counterpart of the mesh
/// engine's 3D quickhull (`ConvexHull` in EngrCAD.Mesh); geometry3Sharp: `ConvexHull2`.
/// </summary>
public static class ConvexHull2
{
    /// <summary>
    /// Indices into <paramref name="points"/> of the convex hull vertices in
    /// counter-clockwise order, starting from the lexicographically smallest point.
    /// Degenerate inputs degrade gracefully: coincident points yield a single index,
    /// collinear points the two extreme indices. Throws on an empty input.
    /// </summary>
    public static int[] ComputeIndices(IReadOnlyList<Vector2d> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        int n = points.Count;
        if (n == 0)
            throw new ArgumentException("Convex hull of an empty point set.", nameof(points));
        if (n == 1)
            return [0];

        var order = new int[n];
        for (int i = 0; i < n; i++)
            order[i] = i;
        Array.Sort(order, (i, j) =>
        {
            int byX = points[i].X.CompareTo(points[j].X);
            return byX != 0 ? byX : points[i].Y.CompareTo(points[j].Y);
        });

        // All points coincident: a single-vertex hull. Deliberate exact (bitwise)
        // equality — after the lexicographic sort, first == last means every point is
        // identical; near-coincident clouds still degrade gracefully via the chain pops.
        if (points[order[0]] == points[order[^1]])
            return [order[0]];

        // Lower then upper chain; strict right turns only (cross <= 0 pops), so
        // duplicates and collinear interior points never survive.
        var hull = new int[2 * n];
        int count = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            int chainStart = count;
            for (int s = 0; s < n; s++)
            {
                int index = order[pass == 0 ? s : n - 1 - s];
                var p = points[index];
                while (count - chainStart >= 2 &&
                       (points[hull[count - 1]] - points[hull[count - 2]])
                           .Cross(p - points[hull[count - 1]]) <= 0)
                    count--;
                hull[count++] = index;
            }
            count--; // the chain's last point starts the next chain / closes the loop
        }
        return hull.AsSpan(0, Math.Max(count, 2)).ToArray();
    }

    /// <summary>The hull vertices themselves (CCW), for callers that don't need indices.</summary>
    public static Vector2d[] Compute(IReadOnlyList<Vector2d> points)
    {
        int[] indices = ComputeIndices(points);
        var hull = new Vector2d[indices.Length];
        for (int i = 0; i < indices.Length; i++)
            hull[i] = points[indices[i]];
        return hull;
    }
}
