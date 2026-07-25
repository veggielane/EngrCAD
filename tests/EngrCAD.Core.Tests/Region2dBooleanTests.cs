using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Core.Tests;

public class Region2dBooleanTests
{
    private static Region2d Box(double x0, double y0, double x1, double y1) =>
        new([new Vector2d(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)]);

    private static double TotalArea(IReadOnlyList<Region2d> regions) => regions.Sum(r => r.Area);

    private static void AssertCanonical(IReadOnlyList<Region2d> regions)
    {
        foreach (var region in regions)
        {
            Assert.True(region.IsCounterClockwise);
            Assert.True(Region2d.SignedArea(region.Outer) > 0, "outer loops come out CCW");
            foreach (var hole in region.Holes)
                Assert.True(Region2d.SignedArea(hole) < 0, "hole loops come out CW");
        }
    }

    // ---- overlapping squares: every operation against analytic areas ----

    [Fact]
    public void TwoOverlappingSquares_HaveExactBooleanAreas()
    {
        var a = Box(0, 0, 4, 4);       // 16
        var b = Box(2, 2, 6, 6);       // 16, overlapping in a 2x2 corner

        var union = Region2dBoolean.Union(a, b);
        var intersection = Region2dBoolean.Intersection(a, b);
        var difference = Region2dBoolean.Difference(a, b);

        Assert.Single(union);
        Assert.Equal(16 + 16 - 4, TotalArea(union), 12);
        Assert.Empty(union[0].Holes);

        Assert.Single(intersection);
        Assert.Equal(4.0, TotalArea(intersection), 12);
        Assert.Equal(2.0, intersection[0].Bounds.Min.X, 12);
        Assert.Equal(2.0, intersection[0].Bounds.Min.Y, 12);
        Assert.Equal(4.0, intersection[0].Bounds.Max.X, 12);

        Assert.Single(difference);
        Assert.Equal(12.0, TotalArea(difference), 12);
        Assert.False(difference[0].Contains(new Vector2d(3, 3)));
        Assert.True(difference[0].Contains(new Vector2d(1, 1)));

        AssertCanonical(union);
        AssertCanonical(intersection);
        AssertCanonical(difference);
    }

    [Fact]
    public void SquareMinusCentredSquare_CreatesAHole()
    {
        // The key case: the difference is a ring, so the operation must CREATE a hole loop
        // that no input carried.
        var result = Region2dBoolean.Difference(Box(0, 0, 10, 10), Box(3, 3, 7, 7));

        var ring = Assert.Single(result);
        Assert.Equal(100.0 - 16.0, ring.Area, 12);
        var hole = Assert.Single(ring.Holes);
        Assert.Equal(4, hole.Count);
        Assert.True(Region2d.SignedArea(hole) < 0);
        Assert.True(ring.Contains(new Vector2d(1, 5)));
        Assert.False(ring.Contains(new Vector2d(5, 5)));
    }

    [Fact]
    public void DifferenceCanSplitOneRegionIntoTwo()
    {
        // A tall thin bar cuts the plate clean through the middle.
        var result = Region2dBoolean.Difference(Box(0, 0, 10, 4), Box(4, -1, 6, 5))
            .OrderBy(r => r.Bounds.Min.X).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal(16.0, result[0].Area, 12);
        Assert.Equal(16.0, result[1].Area, 12);
        Assert.Equal(6.0, result[1].Bounds.Min.X, 12);   // the bar removed x in [4, 6]
        AssertCanonical(result);
    }

    [Fact]
    public void ContainedOperand_BehavesLikeSetTheory()
    {
        var outer = Box(0, 0, 10, 10);
        var inner = Box(3, 3, 7, 7);

        Assert.Equal(100.0, TotalArea(Region2dBoolean.Union(outer, inner)), 12);
        Assert.Equal(16.0, TotalArea(Region2dBoolean.Intersection(outer, inner)), 12);
        Assert.Empty(Region2dBoolean.Difference(inner, outer));      // B contains A: empty
        Assert.Equal(84.0, TotalArea(Region2dBoolean.Difference(outer, inner)), 12);
    }

    [Fact]
    public void UnionOfAContainedOperand_HasNoLeftoverInteriorEdges()
    {
        // The inner square's edges have kept cells on both sides, so they must vanish
        // entirely — the union is the plain outer square, four points, no holes.
        var union = Region2dBoolean.Union(Box(0, 0, 10, 10), Box(3, 3, 7, 7));

        var region = Assert.Single(union);
        Assert.Equal(4, region.Outer.Count);
        Assert.Empty(region.Holes);
    }

    [Fact]
    public void DisjointOperands_StayApart()
    {
        var a = Box(0, 0, 2, 2);
        var b = Box(10, 10, 13, 13);

        var union = Region2dBoolean.Union(a, b).OrderBy(r => r.Area).ToList();
        Assert.Equal(2, union.Count);
        Assert.Equal(4.0, union[0].Area, 12);
        Assert.Equal(9.0, union[1].Area, 12);

        Assert.Empty(Region2dBoolean.Intersection(a, b));
        var difference = Assert.Single(Region2dBoolean.Difference(a, b));
        Assert.Equal(4.0, difference.Area, 12);
    }

    [Fact]
    public void CoincidentEdges_MergeInsteadOfDoubling()
    {
        // Two squares sharing a whole edge: the arrangement's snapping dedupes it, the
        // shared edge ends up interior to the union, and the result is one clean rectangle.
        var union = Region2dBoolean.Union(Box(0, 0, 2, 2), Box(2, 0, 5, 2));

        var region = Assert.Single(union);
        Assert.Equal(10.0, region.Area, 12);
        Assert.Empty(region.Holes);
        // The shared edge's two T-junction vertices are exactly collinear on the merged
        // side and get dropped, so the union is a plain 4-sided rectangle.
        Assert.Equal(4, region.Outer.Count);

        // Identical operands are the degenerate coincident case.
        var identical = Region2dBoolean.Union(Box(0, 0, 2, 2), Box(0, 0, 2, 2));
        Assert.Equal(4.0, TotalArea(identical), 12);
        Assert.Equal(4.0, TotalArea(Region2dBoolean.Intersection(Box(0, 0, 2, 2), Box(0, 0, 2, 2))), 12);
        Assert.Empty(Region2dBoolean.Difference(Box(0, 0, 2, 2), Box(0, 0, 2, 2)));
    }

    [Fact]
    public void OperandsWithHoles_KeepAndCombineThem()
    {
        // A plate with a hole, unioned with a bar that plugs part of that hole.
        var plate = new Region2d(
            [new Vector2d(0, 0), new(10, 0), new(10, 10), new(0, 10)],
            [[new Vector2d(3, 3), new(3, 7), new(7, 7), new(7, 3)]]);
        var bar = Box(4, 2, 6, 8);

        var union = Region2dBoolean.Union(plate, bar);
        var region = Assert.Single(union);
        // 100 − 16 (hole) + 2 * 4 (the two hole halves the bar plugs)
        Assert.Equal(100.0 - 16.0 + 8.0, region.Area, 12);
        Assert.Equal(2, region.Holes.Count);      // the hole was split into two pockets

        // Intersecting the plate with a disc-ish square over its hole yields an annulus piece.
        var intersection = Region2dBoolean.Intersection(plate, Box(2, 2, 8, 8));
        Assert.Equal(36.0 - 16.0, TotalArea(intersection), 12);
    }

    [Fact]
    public void RegionSets_AreTreatedAsTheUnionOfTheirMembers()
    {
        IReadOnlyList<Region2d> plates = [Box(0, 0, 2, 2), Box(4, 0, 6, 2)];
        IReadOnlyList<Region2d> cutter = [Box(1, -1, 5, 3)];

        var difference = Region2dBoolean.Difference(plates, cutter).OrderBy(r => r.Bounds.Min.X).ToList();
        Assert.Equal(2, difference.Count);
        Assert.Equal(2.0, difference[0].Area, 12);
        Assert.Equal(2.0, difference[1].Area, 12);
        Assert.Equal(8.0 - 4.0, TotalArea(difference), 12);
    }

    [Fact]
    public void InstanceSugar_MatchesTheStaticEntryPoints()
    {
        var a = Box(0, 0, 4, 4);
        var b = Box(2, 2, 6, 6);
        Assert.Equal(TotalArea(Region2dBoolean.Union(a, b)), TotalArea(a.Union(b)), 12);
        Assert.Equal(TotalArea(Region2dBoolean.Intersection(a, b)), TotalArea(a.Intersect(b)), 12);
        Assert.Equal(TotalArea(Region2dBoolean.Difference(a, b)), TotalArea(a.Subtract(b)), 12);
    }

    [Fact]
    public void OperandsTouchingAtASinglePoint_TraceIntoSeparateLoops()
    {
        // A pinch vertex: the boundary walk must pair the four directed edges meeting there
        // so each kept square gets its own loop instead of one figure-eight.
        var union = Region2dBoolean.Union(Box(0, 0, 2, 2), Box(2, 2, 5, 5))
            .OrderBy(r => r.Area).ToList();

        Assert.Equal(2, union.Count);
        Assert.Equal(4.0, union[0].Area, 12);
        Assert.Equal(9.0, union[1].Area, 12);
        Assert.Equal(4, union[0].Outer.Count);
        Assert.Equal(4, union[1].Outer.Count);
    }

    [Fact]
    public void PluggingAHoleExactly_MakesItDisappear()
    {
        var plate = new Region2d(
            [new Vector2d(0, 0), new(10, 0), new(10, 10), new(0, 10)],
            [[new Vector2d(3, 3), new(3, 7), new(7, 7), new(7, 3)]]);
        var plug = Box(3, 3, 7, 7);

        // Every edge of the hole now has kept cells on both sides, so the hole loop is
        // interior and vanishes; the collinear T-junctions it left behind go too.
        var region = Assert.Single(Region2dBoolean.Union(plate, plug));
        Assert.Equal(100.0, region.Area, 12);
        Assert.Empty(region.Holes);
        Assert.Equal(4, region.Outer.Count);
        Assert.True(region.Contains(new Vector2d(5, 5)));

        // And intersecting the plate with its own plug leaves nothing but the shared edges.
        Assert.Empty(Region2dBoolean.Intersection(plate, plug));
    }

    [Fact]
    public void ToolTouchingTheBoundaryFromOutside_LeavesTheOperandAlone()
    {
        // The cutter's left edge is exactly the plate's right edge: a coincident edge with
        // kept material on one side only, so it survives as boundary and nothing is removed.
        var difference = Assert.Single(Region2dBoolean.Difference(Box(0, 0, 4, 4), Box(4, 0, 8, 4)));
        Assert.Equal(16.0, difference.Area, 12);
        Assert.Equal(4, difference.Outer.Count);

        Assert.Empty(Region2dBoolean.Intersection(Box(0, 0, 4, 4), Box(4, 0, 8, 4)));
        Assert.Equal(32.0, TotalArea(Region2dBoolean.Union(Box(0, 0, 4, 4), Box(4, 0, 8, 4))), 12);
    }

    [Fact]
    public void OperandsFarFromTheOrigin_AreDecidedJustAsExactly()
    {
        // 1e6 offsets: the parity/orientation decisions are exact predicates, so nothing
        // about the classification degrades — only the coordinates carry magnitude.
        const double offset = 1e6;
        var a = Box(offset, offset, offset + 4, offset + 4);
        var b = Box(offset + 2, offset + 2, offset + 6, offset + 6);

        Assert.Equal(28.0, TotalArea(Region2dBoolean.Union(a, b)), 6);
        Assert.Equal(4.0, TotalArea(Region2dBoolean.Intersection(a, b)), 6);
        Assert.Equal(12.0, TotalArea(Region2dBoolean.Difference(a, b)), 6);
    }

    // ---- fuzz: the inclusion-exclusion identity over random polygon pairs ----

    [Fact]
    public void InclusionExclusion_HoldsForRandomConvexPolygons()
    {
        var random = new Random(20260725);
        int overlaps = 0;

        for (int trial = 0; trial < 200; trial++)
        {
            var a = RandomHullRegion(random);
            var b = RandomHullRegion(random);

            var union = Region2dBoolean.Union(a, b);
            var intersection = Region2dBoolean.Intersection(a, b);
            var difference = Region2dBoolean.Difference(a, b);

            // |A ∪ B| + |A ∩ B| = |A| + |B|, exactly the identity that fails when a cell is
            // classified twice or dropped.
            double scale = a.Area + b.Area;
            Assert.Equal(scale, TotalArea(union) + TotalArea(intersection), 9);
            // |A − B| = |A| − |A ∩ B|
            Assert.Equal(a.Area - TotalArea(intersection), TotalArea(difference), 9);
            AssertCanonical(union);
            AssertCanonical(intersection);
            AssertCanonical(difference);

            if (TotalArea(intersection) > 1e-9)
                overlaps++;
        }
        Assert.True(overlaps > 100, $"expected mostly-overlapping random pairs, got {overlaps}");
    }

    // ---- UnionAll: same answer as one big arrangement, different fold shape ----

    [Fact]
    public void UnionAll_OfATilingOfSquares_IsTheWholeRectangle()
    {
        // 8x5 unit squares laid edge to edge: every interior edge must disappear.
        var tiles = new List<Region2d>();
        for (int x = 0; x < 8; x++)
        for (int y = 0; y < 5; y++)
            tiles.Add(Box(x, y, x + 1, y + 1));

        var result = Region2dBoolean.UnionAll(tiles);

        var sheet = Assert.Single(result);
        Assert.Equal(40.0, sheet.Area, 12);
        Assert.Equal(4, sheet.Outer.Count);   // collinear T-junctions are dropped exactly
        Assert.Empty(sheet.Holes);
        AssertCanonical(result);
    }

    [Fact]
    public void UnionAll_MatchesTheSingleArrangementAnswerOnOverlappingInput()
    {
        var random = new Random(20250725);
        var regions = new List<Region2d>();
        for (int i = 0; i < 24; i++)
            regions.Add(RandomHullRegion(random));

        var tree = Region2dBoolean.UnionAll(regions);
        var flat = Region2dBoolean.Union(regions, []);

        Assert.Equal(flat.Count, tree.Count);
        Assert.Equal(TotalArea(flat), TotalArea(tree), 9);
        AssertCanonical(tree);
    }

    [Fact]
    public void UnionAll_OfDisjointRegions_KeepsEveryOne()
    {
        var ring = new List<Region2d>();
        for (int i = 0; i < 9; i++)
        {
            double angle = 2 * Math.PI * i / 9;
            double cx = 10 * Math.Cos(angle), cy = 10 * Math.Sin(angle);
            ring.Add(Box(cx - 1, cy - 1, cx + 1, cy + 1));
        }

        var result = Region2dBoolean.UnionAll(ring);

        Assert.Equal(9, result.Count);
        Assert.Equal(9 * 4.0, TotalArea(result), 9);
    }

    [Fact]
    public void UnionAll_OfNothingOrOne_IsTrivial()
    {
        Assert.Empty(Region2dBoolean.UnionAll([]));
        var square = Box(0, 0, 3, 3);
        Assert.Same(square, Assert.Single(Region2dBoolean.UnionAll([square])));
    }

    private static Region2d RandomHullRegion(Random random)
    {
        // Convex hulls of random point clouds are simple polygons by construction; the
        // clouds are offset so pairs usually overlap partially.
        while (true)
        {
            var cloud = new Vector2d[10];
            double cx = random.NextDouble() * 4;
            double cy = random.NextDouble() * 4;
            for (int i = 0; i < cloud.Length; i++)
                cloud[i] = new Vector2d(cx + random.NextDouble() * 6, cy + random.NextDouble() * 6);
            var hull = ConvexHull2.Compute(cloud);
            if (hull.Length >= 3 && Math.Abs(Region2d.SignedArea(hull)) > 1e-6)
                return new Region2d(hull);
        }
    }
}
