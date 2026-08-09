using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// The 3D Hilbert curve's claims are integer identities, so the tests are exact: the site count
/// is closed form, the sites are pairwise DISTINCT (the check that catches a flipped recursion —
/// a broken walk still emits the right NUMBER of cells), consecutive sites differ by exactly one
/// lattice step, and the length is the segment count times the achieved spacing exactly.
/// </summary>
public class SpaceFillingCurve3dTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ItVisitsEveryCellOfTheCubeExactlyOnce(int order)
    {
        var sites = SpaceFillingCurve3d.LatticeSites(order);
        int side = SpaceFillingCurve3d.GridSize(order);

        Assert.Equal(SpaceFillingCurve3d.SiteCount(order), sites.Length);
        Assert.Equal((long)side * side * side, sites.Length);

        // Bijectivity onto the cube: distinct AND in range is the same statement as onto, given
        // the count. A set comparison is what catches an orientation table applied backwards,
        // which leaves the count untouched.
        var seen = new HashSet<Vector3i>(sites.Length);
        foreach (var site in sites)
        {
            Assert.InRange(site.X, 0, side - 1);
            Assert.InRange(site.Y, 0, side - 1);
            Assert.InRange(site.Z, 0, side - 1);
            Assert.True(seen.Add(site), $"Site {site} is visited twice at order {order}.");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ConsecutiveSitesAreExactlyOneLatticeStepApart(int order)
    {
        var sites = SpaceFillingCurve3d.LatticeSites(order);
        for (int i = 1; i < sites.Length; i++)
        {
            Assert.True(
                SpaceFillingCurve3d.AreNeighbours(sites[i - 1], sites[i]),
                $"Order {order}: sites {i - 1} and {i} ({sites[i - 1]} to {sites[i]}) are not neighbours.");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TheTwoTerminalsAreAdjacentCornersOfTheCube(int order)
    {
        var sites = SpaceFillingCurve3d.LatticeSites(order);
        int last = SpaceFillingCurve3d.GridSize(order) - 1;

        // MEASURED off the walk rather than asserted from the literature: both ends are corners,
        // and the two corners share an edge. That is what would let blocks be tiled, and it is
        // the property a caller wiring two terminals onto a boundary depends on.
        Assert.Equal(new Vector3i(0, 0, 0), sites[0]);
        var end = sites[^1];
        foreach (int c in new[] { end.X, end.Y, end.Z })
            Assert.True(c == 0 || c == last, $"The end cell {end} is not a corner of the {last + 1}-cube.");

        int differing = (end.X != 0 ? 1 : 0) + (end.Y != 0 ? 1 : 0) + (end.Z != 0 ? 1 : 0);
        Assert.Equal(1, differing);
    }

    [Fact]
    public void ThePlacementReportsTheSpacingItAchievedAndItIsNeverCoarser()
    {
        var bounds = new Aabb(new Vector3d(0, 0, 0), new Vector3d(20, 12, 8));

        // The footprint is the bounding CUBE over the LARGEST extent, so the achieved spacing is
        // set by 20 and is finer than the ask by however much the integer order overshoots.
        var curve = SpaceFillingCurve3d.Over(bounds, 3.0);

        Assert.True(curve.Spacing <= curve.RequestedSpacing);
        Assert.Equal(3.0, curve.RequestedSpacing);
        Assert.Equal(20.0 / SpaceFillingCurve3d.GridSize(curve.Order), curve.Spacing, 12);
        Assert.True(curve.IsContinuous);
        Assert.Equal(1, curve.MaxLatticeStep);
    }

    [Fact]
    public void TheLengthIsTheSegmentCountTimesTheSpacingExactly()
    {
        var bounds = new Aabb(new Vector3d(-5, -5, -5), new Vector3d(5, 5, 5));
        var curve = SpaceFillingCurve3d.Over(bounds, 1.5);

        double expected = SpaceFillingCurve3d.SegmentCount(curve.Order) * curve.Spacing;
        Assert.Equal(expected, curve.Length, 9);
    }

    [Fact]
    public void ARequestLandingExactlyOnACellSizeIsHonouredExactly()
    {
        // 8 / 2^3 = 1 exactly, so the search stops at order 3 rather than spending one more.
        var bounds = new Aabb(new Vector3d(0, 0, 0), new Vector3d(8, 8, 8));
        var curve = SpaceFillingCurve3d.Over(bounds, 1.0);

        Assert.Equal(3, curve.Order);
        Assert.Equal(1.0, curve.Spacing);
    }

    [Fact]
    public void TheSiteCapIsRefusedByNameWithTheFinestSpacingItAllows()
    {
        var bounds = new Aabb(new Vector3d(0, 0, 0), new Vector3d(100, 100, 100));
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => SpaceFillingCurve3d.Over(bounds, 0.01, maxSites: 5000));

        Assert.Contains("past the 5000-site cap", error.Message);
        Assert.Contains("FINEST", error.Message);
    }

    [Fact]
    public void AnEmptyOrDegenerateRegionIsRefusedByName()
    {
        Assert.Throws<ArgumentException>(() => SpaceFillingCurve3d.Over(Aabb.Empty, 1.0));

        var point = new Aabb(new Vector3d(1, 2, 3), new Vector3d(1, 2, 3));
        var error = Assert.Throws<ArgumentException>(() => SpaceFillingCurve3d.Over(point, 1.0));
        Assert.Contains("point", error.Message);
    }

    [Fact]
    public void TheLongestStraightRunIsMeasuredRatherThanAssumed()
    {
        // The 2D file measures this because "Hilbert is the isotropic member" should be a number.
        // Its 3D member saturates too; the value is recorded rather than predicted.
        var runs = new List<int>();
        for (int order = 2; order <= 5; order++)
        {
            var sites = SpaceFillingCurve3d.LatticeSites(order);
            int longest = 1, run = 1;
            for (int i = 1; i < sites.Length; i++)
            {
                var step = sites[i] - sites[i - 1];
                var previous = i >= 2 ? sites[i - 1] - sites[i - 2] : new Vector3i(0, 0, 0);
                run = step == previous ? run + 1 : 1;
                longest = Math.Max(longest, run);
            }
            runs.Add(longest);
        }

        // MEASURED: 3 cells at orders 2, 3, 4 and 5 — SATURATED, and the same number the 2D
        // Hilbert curve reports, so "no preferred direction" carries over to the volume rather
        // than being asserted of it.
        Assert.Equal(new[] { 3, 3, 3, 3 }, runs);
    }

    [Fact]
    public void ThePointsSitAtCellCentresSoTheCurveInsetsByHalfACell()
    {
        var bounds = new Aabb(new Vector3d(0, 0, 0), new Vector3d(4, 4, 4));
        var curve = SpaceFillingCurve3d.Over(bounds, 1.0);

        Assert.Equal(curve.Spacing / 2, curve.Bounds.Min.X, 12);
        Assert.Equal(4 - curve.Spacing / 2, curve.Bounds.Max.X, 12);
        Assert.Equal(curve.Spacing / 2, curve.Bounds.Min.Z, 12);
    }
}
