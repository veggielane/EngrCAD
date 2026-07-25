using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Core.Tests;

public class Region2dTests
{
    private static Vector2d[] Box(double x0, double y0, double x1, double y1) =>
        [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];

    private static Vector2d[] BoxCw(double x0, double y0, double x1, double y1) =>
        [new(x0, y0), new(x0, y1), new(x1, y1), new(x1, y0)];

    // ---- area, orientation, bounds ----

    [Fact]
    public void SquareWithSquareHole_HasExactNetArea()
    {
        var region = new Region2d(Box(0, 0, 4, 4), [Box(1, 1, 3, 3)]);

        // 16 − 4 with no rounding anywhere: the shoelace terms are exact in doubles.
        Assert.Equal(12.0, region.Area);
        Assert.True(Region2d.SignedArea(region.Outer) > 0, "outer loop is normalized CCW");
        Assert.True(Region2d.SignedArea(region.Holes[0]) < 0, "hole loop is normalized CW");
        Assert.True(region.IsCounterClockwise);
    }

    [Fact]
    public void ClockwiseInput_IsReorientedToTheCanonicalForm()
    {
        var region = new Region2d(BoxCw(0, 0, 2, 2), [BoxCw(0.5, 0.5, 1.5, 1.5)]);

        Assert.True(Region2d.SignedArea(region.Outer) > 0);
        Assert.True(Region2d.SignedArea(region.Holes[0]) < 0);
        Assert.Equal(4.0 - 1.0, region.Area);
    }

    [Fact]
    public void Reversed_FlipsEveryLoopButNothingElse()
    {
        var region = new Region2d(Box(0, 0, 4, 4), [Box(1, 1, 3, 3)]);
        var reversed = region.Reversed();

        Assert.False(reversed.IsCounterClockwise);
        Assert.True(Region2d.SignedArea(reversed.Outer) < 0);
        Assert.True(Region2d.SignedArea(reversed.Holes[0]) > 0);
        Assert.Equal(region.Area, reversed.Area);
        Assert.Equal(region.Bounds.Min, reversed.Bounds.Min);
        Assert.True(reversed.Contains(new Vector2d(0.5, 0.5)));
        Assert.False(reversed.Contains(new Vector2d(2, 2)));

        var back = reversed.Reversed();
        Assert.True(back.IsCounterClockwise);
        Assert.Equal(region.Outer[0], back.Outer[0]);
    }

    [Fact]
    public void Bounds_CoverTheOuterLoopInThePlane()
    {
        var region = new Region2d(Box(-1, 2, 5, 7));
        Assert.Equal(-1, region.Bounds.Min.X, 12);
        Assert.Equal(2, region.Bounds.Min.Y, 12);
        Assert.Equal(5, region.Bounds.Max.X, 12);
        Assert.Equal(7, region.Bounds.Max.Y, 12);
        Assert.Equal(0, region.Bounds.Min.Z, 12);
        Assert.Equal(0, region.Bounds.Max.Z, 12);
    }

    // ---- containment ----

    [Fact]
    public void Contains_HonorsHolesAndTheClosedBoundaryConvention()
    {
        var region = new Region2d(Box(0, 0, 4, 4), [Box(1, 1, 3, 3)]);

        Assert.True(region.Contains(new Vector2d(0.5, 0.5)));    // material
        Assert.False(region.Contains(new Vector2d(2, 2)));       // inside the hole
        Assert.False(region.Contains(new Vector2d(-1, 2)));      // outside entirely

        // Closed set: outer edge, outer vertex, hole edge and hole vertex are all inside.
        Assert.True(region.Contains(new Vector2d(0, 2)));
        Assert.True(region.Contains(new Vector2d(0, 0)));
        Assert.True(region.Contains(new Vector2d(1, 2)));
        Assert.True(region.Contains(new Vector2d(1, 1)));
    }

    [Fact]
    public void Contains_IsExactForPointsLevelWithVertices()
    {
        // The classic parity trap: a ray at exactly a vertex's height. The half-open rule
        // must count the spike's two edges once, not twice or zero times.
        var spike = new Region2d([new(0, 0), new(4, 0), new(4, 2), new(2, 4), new(0, 2)]);

        Assert.True(spike.Contains(new Vector2d(1, 2)));         // level with the apex-ish vertex
        Assert.False(spike.Contains(new Vector2d(-1, 2)));
        Assert.False(spike.Contains(new Vector2d(5, 2)));
        Assert.True(spike.Contains(new Vector2d(2, 4)));         // exactly the apex vertex
        Assert.False(spike.Contains(new Vector2d(2, 4.000001)));
    }

    [Fact]
    public void Contains_IsExactWhereNaiveArithmeticIsNot()
    {
        // A hostile-magnitude triangle: the naive determinant of these coordinates is
        // polluted by roundoff, but the query point lies EXACTLY on the long edge, so the
        // closed-set convention must report it inside.
        var far0 = new Vector2d(-3 * Math.Pow(2, 20), -Math.Pow(2, 20));
        var far1 = new Vector2d(3 * Math.Pow(2, 20), Math.Pow(2, 20));
        var onEdge = new Vector2d(3 * Math.Pow(2, -20), Math.Pow(2, -20));
        var region = new Region2d([far0, far1, new Vector2d(0, 4 * Math.Pow(2, 20))]);

        Assert.True(region.Contains(onEdge));
        Assert.False(region.Contains(new Vector2d(onEdge.X, Math.BitDecrement(onEdge.Y))));
        Assert.True(region.Contains(new Vector2d(onEdge.X, Math.BitIncrement(onEdge.Y))));
    }

    // ---- validation ----

    [Fact]
    public void DegenerateAndCrossingInput_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new Region2d([new(0, 0), new(1, 0)]));
        Assert.Throws<ArgumentException>(() => new Region2d([new(0, 0), new(1, 0), new(2, 0)]));
        // Hole outside the outer loop.
        Assert.Throws<ArgumentException>(() => new Region2d(Box(0, 0, 2, 2), [Box(5, 5, 6, 6)]));
        // Hole straddling the outer boundary.
        Assert.Throws<ArgumentException>(() => new Region2d(Box(0, 0, 2, 2), [Box(1, 1, 3, 3)]));
        // Two holes crossing each other.
        Assert.Throws<ArgumentException>(() =>
            new Region2d(Box(0, 0, 10, 10), [Box(1, 1, 5, 5), Box(3, 3, 7, 7)]));
    }

    // ---- nesting classifier (PlanarComplex) ----

    [Fact]
    public void FromLoops_DetectsAHoleWithoutBeingTold()
    {
        var regions = Region2d.FromLoops([Box(0, 0, 4, 4), Box(1, 1, 3, 3)]);

        var region = Assert.Single(regions);
        Assert.Single(region.Holes);
        Assert.Equal(12.0, region.Area);
        Assert.True(Region2d.SignedArea(region.Outer) > 0);
        Assert.True(Region2d.SignedArea(region.Holes[0]) < 0);
    }

    [Fact]
    public void FromLoops_MakesAnIslandInsideAHoleItsOwnRegion()
    {
        // Depth 0 / 1 / 2: outer, hole, island.
        var regions = Region2d.FromLoops([Box(0, 0, 10, 10), Box(2, 2, 8, 8), Box(4, 4, 6, 6)])
            .OrderByDescending(r => r.Area).ToList();

        Assert.Equal(2, regions.Count);
        Assert.Equal(100.0 - 36.0, regions[0].Area);
        Assert.Single(regions[0].Holes);
        Assert.Equal(4.0, regions[1].Area);
        Assert.Empty(regions[1].Holes);

        // Total material equals the alternating sum — the classifier got every level right.
        Assert.Equal(100.0 - 36.0 + 4.0, regions.Sum(r => r.Area));
    }

    [Fact]
    public void FromLoops_KeepsDisjointOutersApartAndAssignsHolesToTheirImmediateParent()
    {
        var regions = Region2d.FromLoops([
            Box(0, 0, 4, 4), Box(1, 1, 3, 3),      // plate with a hole
            Box(10, 0, 14, 4), Box(11, 1, 12, 2),  // second plate with its own hole
        ]).OrderBy(r => r.Bounds.Min.X).ToList();

        Assert.Equal(2, regions.Count);
        Assert.Equal(12.0, regions[0].Area);
        Assert.Equal(15.0, regions[1].Area);
        Assert.Single(regions[0].Holes);
        Assert.Single(regions[1].Holes);
        // The far plate's hole did not get attached to the near plate.
        Assert.True(regions[0].Holes[0].All(p => p.X < 5));
        Assert.True(regions[1].Holes[0].All(p => p.X > 5));
    }

    [Fact]
    public void FromLoops_IgnoresInputWindingAndDegenerateLoops()
    {
        var regions = Region2d.FromLoops([
            BoxCw(0, 0, 4, 4),                                  // clockwise outer
            Box(1, 1, 3, 3),                                    // counter-clockwise hole
            [new Vector2d(9, 9), new Vector2d(9, 9)],           // too few points
            [new Vector2d(6, 0), new Vector2d(7, 0), new Vector2d(8, 0)], // zero area
        ]);

        var region = Assert.Single(regions);
        Assert.Equal(12.0, region.Area);
    }

    [Fact]
    public void FromLoops_OnAnEmptyBag_ReturnsNothing()
    {
        Assert.Empty(Region2d.FromLoops([]));
    }
}
