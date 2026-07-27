using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// Ericson's Voronoi-region closest-point form has seven exit paths — three vertices,
/// three edges and the interior — and a wrong sign test in any one of them is invisible
/// until a query happens to land in that region. Every case here names the region it aims
/// at and asserts both the point and the reported feature, plus an independent brute-force
/// check so the expectations are not just a transcription of the implementation.
/// </summary>
public class Distance3dTests
{
    // A right triangle in the z = 0 plane: a at the origin, b along +x, c along +y.
    private static readonly Vector3d A = new(0, 0, 0);
    private static readonly Vector3d B = new(4, 0, 0);
    private static readonly Vector3d C = new(0, 3, 0);

    private static Vector3d Closest(in Vector3d p, out TriangleRegion region) =>
        Distance3d.ClosestPointOnTriangle(p, A, B, C, out region);

    /// <summary>
    /// Dense barycentric sampling of the triangle: the closest point can only be one of
    /// these, so the minimum over a fine grid brackets the analytic answer from above.
    /// </summary>
    private static double BruteForceDistance(in Vector3d p, int steps = 400)
    {
        double best = double.MaxValue;
        for (int i = 0; i <= steps; i++)
        {
            for (int j = 0; i + j <= steps; j++)
            {
                double u = (double)i / steps, v = (double)j / steps;
                var q = A + (B - A) * u + (C - A) * v;
                best = Math.Min(best, q.DistanceTo(p));
            }
        }
        return best;
    }

    private static void AssertRegion(in Vector3d p, in Vector3d expected, TriangleRegion expectedRegion)
    {
        var closest = Closest(p, out var region);

        Assert.Equal(expectedRegion, region);
        Assert.True(closest.DistanceTo(expected) < 1e-12, $"closest point {closest} should be {expected}");
        // The grid can only overestimate, and its resolution here is ~1/400 of an edge.
        Assert.True(closest.DistanceTo(p) <= BruteForceDistance(p) + 1e-9,
            "the analytic closest point must be at least as close as the best sampled one");
        // The two overloads are one implementation; this pins that they stay so.
        Assert.Equal(closest, Distance3d.ClosestPointOnTriangle(p, A, B, C));
        Assert.Equal(closest.DistanceSquaredTo(p), Distance3d.DistanceSquaredToTriangle(p, A, B, C));
    }

    // ---------------------------------------------------------------- the three vertices

    [Fact]
    public void OutsideBothEdgesAtA_ReturnsVertexA() =>
        AssertRegion(new Vector3d(-2, -3, 1), A, TriangleRegion.VertexA);

    [Fact]
    public void OutsideBothEdgesAtB_ReturnsVertexB() =>
        AssertRegion(new Vector3d(9, -4, -2), B, TriangleRegion.VertexB);

    [Fact]
    public void OutsideBothEdgesAtC_ReturnsVertexC() =>
        AssertRegion(new Vector3d(-3, 8, 5), C, TriangleRegion.VertexC);

    // ---------------------------------------------------------------- the three edges

    [Fact]
    public void BesideEdgeAb_ReturnsTheFootOnAb() =>
        AssertRegion(new Vector3d(1.5, -2, 0.5), new Vector3d(1.5, 0, 0), TriangleRegion.EdgeAb);

    [Fact]
    public void BesideEdgeCa_ReturnsTheFootOnCa() =>
        AssertRegion(new Vector3d(-2, 1.5, -0.5), new Vector3d(0, 1.5, 0), TriangleRegion.EdgeCa);

    [Fact]
    public void BesideEdgeBc_ReturnsTheFootOnBc()
    {
        // The hypotenuse runs (4,0,0) to (0,3,0); its outward normal in-plane is (3,4)/5.
        // Stepping 5 units out from its midpoint (2, 1.5) lands the foot back on the midpoint.
        var midpoint = new Vector3d(2, 1.5, 0);
        AssertRegion(midpoint + new Vector3d(3, 4, 0), midpoint, TriangleRegion.EdgeBc);
    }

    // ---------------------------------------------------------------- the interior

    [Fact]
    public void AboveTheInterior_ProjectsStraightDown() =>
        AssertRegion(new Vector3d(1, 1, 7), new Vector3d(1, 1, 0), TriangleRegion.Face);

    [Fact]
    public void PointOnTheTriangle_IsItsOwnClosestPoint()
    {
        var p = new Vector3d(1, 1, 0);

        var closest = Closest(p, out var region);

        Assert.Equal(TriangleRegion.Face, region);
        Assert.Equal(p, closest);
        Assert.Equal(0, Distance3d.DistanceSquaredToTriangle(p, A, B, C));
    }

    // ---------------------------------------------------------------- degeneracies

    [Fact]
    public void CollapsedTriangle_ReturnsThePointItCollapsedTo()
    {
        // All three corners coincide: every branch's denominator is zero, and the guards
        // are what keep this from returning NaN.
        var p = new Vector3d(3, -2, 1);

        var closest = Distance3d.ClosestPointOnTriangle(p, A, A, A, out var region);

        Assert.Equal(A, closest);
        Assert.Equal(TriangleRegion.VertexA, region);
    }

    [Fact]
    public void SliverTriangle_StillReturnsAPointOnIt()
    {
        // Zero area but nonzero extent: a and b are distinct, c sits exactly between them.
        // The answer must be the foot on the segment, not a NaN from the interior branch.
        var p = new Vector3d(1, 5, 0);

        var closest = Distance3d.ClosestPointOnTriangle(p, A, B, new Vector3d(2, 0, 0), out _);

        Assert.True(closest.DistanceTo(new Vector3d(1, 0, 0)) < 1e-12, $"got {closest}");
    }

    [Fact]
    public void EveryRegionIsReachable()
    {
        // A shell of probes around the triangle must between them exercise all seven exits;
        // a sign test flipped so that some region can never be reported would slip past
        // case-by-case tests that each only prove their own branch is reachable.
        var seen = new HashSet<TriangleRegion>();
        for (double x = -4; x <= 8; x += 0.25)
        {
            for (double y = -4; y <= 7; y += 0.25)
            {
                Closest(new Vector3d(x, y, 1), out var region);
                seen.Add(region);
            }
        }

        Assert.Equal(7, seen.Count);
    }
}
