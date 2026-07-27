namespace EngrCAD.Core;

/// <summary>
/// Which feature of a triangle the closest point lies on. Callers that only need the point
/// can ignore it; callers that need to know <i>what</i> was hit — a signed distance field
/// picking the angle-weighted pseudonormal of the closest feature, for one — cannot
/// reconstruct it from the point without re-testing.
/// </summary>
public enum TriangleRegion
{
    /// <summary>The triangle's interior.</summary>
    Face,
    /// <summary>The first corner.</summary>
    VertexA,
    /// <summary>The second corner.</summary>
    VertexB,
    /// <summary>The third corner.</summary>
    VertexC,
    /// <summary>The edge from the first corner to the second.</summary>
    EdgeAb,
    /// <summary>The edge from the second corner to the third.</summary>
    EdgeBc,
    /// <summary>The edge from the third corner back to the first.</summary>
    EdgeCa,
}

/// <summary>
/// Closest-point queries between a point and 3D primitives. Point-to-triangle is the one
/// every spatial query in this codebase ends up calling — mesh projection targets, mesh
/// signed distance fields, nearest-triangle BVH descents — so it lives here, in terms of
/// plain points, rather than being reimplemented per engine against per-engine handles.
/// </summary>
public static class Distance3d
{
    /// <summary>
    /// Closest point of triangle <c>abc</c> to <paramref name="p"/> — Ericson's
    /// Voronoi-region form (Real-Time Collision Detection §5.1.5): six barycentric sign
    /// tests locate the feature (vertex, edge or interior) and the answer is exact for that
    /// feature, with no tolerance anywhere. Degenerate (zero-area) triangles fall out
    /// through the edge cases.
    /// </summary>
    public static Vector3d ClosestPointOnTriangle(in Vector3d p, in Vector3d a, in Vector3d b, in Vector3d c) =>
        ClosestPointOnTriangle(p, a, b, c, out _);

    /// <summary>
    /// Closest point of triangle <c>abc</c> to <paramref name="p"/>, also reporting which
    /// feature it lies on. See the overload without <paramref name="region"/> for the
    /// method; this is the single implementation and that one delegates here, so the two
    /// can never disagree.
    /// </summary>
    public static Vector3d ClosestPointOnTriangle(
        in Vector3d p, in Vector3d a, in Vector3d b, in Vector3d c, out TriangleRegion region)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        double d1 = ab.Dot(ap), d2 = ac.Dot(ap);
        if (d1 <= 0 && d2 <= 0)
        {
            region = TriangleRegion.VertexA;
            return a;
        }

        var bp = p - b;
        double d3 = ab.Dot(bp), d4 = ac.Dot(bp);
        if (d3 >= 0 && d4 <= d3)
        {
            region = TriangleRegion.VertexB;
            return b;
        }

        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            double denom = d1 - d3;
            // Exact-zero division guard (epsilon ladder: algorithmic tier) — a zero
            // denominator means a and b coincide, so a is the answer.
            region = denom == 0 ? TriangleRegion.VertexA : TriangleRegion.EdgeAb;
            return denom == 0 ? a : a + ab * (d1 / denom);
        }

        var cp = p - c;
        double d5 = ab.Dot(cp), d6 = ac.Dot(cp);
        if (d6 >= 0 && d5 <= d6)
        {
            region = TriangleRegion.VertexC;
            return c;
        }

        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            double denom = d2 - d6;
            region = denom == 0 ? TriangleRegion.VertexA : TriangleRegion.EdgeCa;
            return denom == 0 ? a : a + ac * (d2 / denom);
        }

        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            double denom = d4 - d3 + (d5 - d6);
            region = denom == 0 ? TriangleRegion.VertexB : TriangleRegion.EdgeBc;
            return denom == 0 ? b : b + (c - b) * ((d4 - d3) / denom);
        }

        double sum = va + vb + vc;
        if (sum == 0)
        {
            region = TriangleRegion.VertexA; // fully degenerate triangle
            return a;
        }
        double inv = 1.0 / sum;
        region = TriangleRegion.Face;
        return a + ab * (vb * inv) + ac * (vc * inv);
    }

    /// <summary>
    /// Squared distance from <paramref name="p"/> to triangle <c>abc</c>. Squared because
    /// branch-and-bound nearest queries compare distances far more often than they report
    /// them, and a square root per comparison is pure cost.
    /// </summary>
    public static double DistanceSquaredToTriangle(in Vector3d p, in Vector3d a, in Vector3d b, in Vector3d c) =>
        ClosestPointOnTriangle(p, a, b, c).DistanceSquaredTo(p);
}
