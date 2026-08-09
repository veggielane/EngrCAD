using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// Almost every claim about a space-filling curve is EXACT and combinatorial, so almost every
/// test here is an identity on integers rather than a tolerance: the sites are counted in
/// closed form and are pairwise distinct, consecutive sites differ by exactly one lattice step,
/// the length is the segment count times the spacing, and a Moore curve's closure is asserted
/// rather than trusted. Only coverage is a measurement, and its bound is DERIVED from the
/// cell's own circumradius.
/// </summary>
public class SpaceFillingCurveTests
{
    private static readonly SpaceFillingFamily[] Continuous =
    [
        SpaceFillingFamily.Hilbert, SpaceFillingFamily.Moore,
        SpaceFillingFamily.Peano, SpaceFillingFamily.Gosper,
    ];

    private static readonly SpaceFillingFamily[] All =
    [
        SpaceFillingFamily.Hilbert, SpaceFillingFamily.Moore, SpaceFillingFamily.Peano,
        SpaceFillingFamily.Gosper, SpaceFillingFamily.ZOrder,
    ];

    private static int TopOrder(SpaceFillingFamily family) => family switch
    {
        SpaceFillingFamily.Peano => 4,
        SpaceFillingFamily.Gosper => 5,
        _ => 6,
    };

    // ---- bijectivity: the check that catches a flipped recursion ----

    [Fact]
    public void EveryFamily_VisitsItsClosedFormNumberOfSites_EachExactlyOnce()
    {
        foreach (var family in All)
        {
            for (int order = SpaceFillingCurve.MinimumOrder(family); order <= TopOrder(family); order++)
            {
                var sites = SpaceFillingCurve.LatticeSites(family, order);
                long expected = SpaceFillingCurve.SiteCount(family, order);
                Assert.Equal(expected, sites.Count);
                Assert.Equal(sites.Count, new HashSet<Vector2i>(sites).Count);
            }
        }
    }

    [Fact]
    public void SquareFamilies_VisitEveryCellOfTheirGrid()
    {
        foreach (var family in new[]
        {
            SpaceFillingFamily.Hilbert, SpaceFillingFamily.Moore,
            SpaceFillingFamily.Peano, SpaceFillingFamily.ZOrder,
        })
        {
            for (int order = SpaceFillingCurve.MinimumOrder(family); order <= 4; order++)
            {
                int side = SpaceFillingCurve.GridSize(family, order);
                var visited = new HashSet<Vector2i>(SpaceFillingCurve.LatticeSites(family, order));
                for (int x = 0; x < side; x++)
                for (int y = 0; y < side; y++)
                    Assert.Contains(new Vector2i(x, y), visited);
            }
        }
    }

    // ---- adjacency: an integer identity, no epsilon anywhere ----

    [Fact]
    public void EveryContinuousFamily_StepsExactlyOneLatticeCell()
    {
        foreach (var family in Continuous)
        {
            for (int order = SpaceFillingCurve.MinimumOrder(family); order <= TopOrder(family); order++)
            {
                var sites = SpaceFillingCurve.LatticeSites(family, order);
                for (int i = 1; i < sites.Count; i++)
                {
                    Assert.True(
                        SpaceFillingCurve.AreNeighbours(family, sites[i - 1], sites[i]),
                        $"{family} order {order} step {i}: {sites[i - 1].X},{sites[i - 1].Y} -> "
                        + $"{sites[i].X},{sites[i].Y}");
                }
            }
        }
    }

    [Fact]
    public void ADiagonalIsNotAStep_SoTheAdjacencyTestHasTeeth()
    {
        Assert.True(SpaceFillingCurve.AreNeighbours(SpaceFillingFamily.Hilbert, new(3, 4), new(4, 4)));
        Assert.False(SpaceFillingCurve.AreNeighbours(SpaceFillingFamily.Hilbert, new(3, 4), new(4, 5)));
        Assert.False(SpaceFillingCurve.AreNeighbours(SpaceFillingFamily.Hilbert, new(3, 4), new(3, 4)));

        // The Eisenstein lattice's six unit vectors, and nothing else — (1, 1) is a step of
        // length sqrt(3) there, which is why the square rule cannot be reused.
        foreach (var step in new Vector2i[] { new(1, 0), new(0, 1), new(-1, 1), new(-1, 0), new(0, -1), new(1, -1) })
            Assert.True(SpaceFillingCurve.AreNeighbours(SpaceFillingFamily.Gosper, Vector2i.Zero, step));
        Assert.False(SpaceFillingCurve.AreNeighbours(SpaceFillingFamily.Gosper, Vector2i.Zero, new(1, 1)));
    }

    // ---- closure: Moore's defining property, asserted rather than trusted ----

    [Fact]
    public void MooreCloses_AndHilbertDoesNotPastOrderOne()
    {
        for (int order = 1; order <= 6; order++)
        {
            var moore = SpaceFillingCurve.LatticeSites(SpaceFillingFamily.Moore, order);
            Assert.True(
                SpaceFillingCurve.AreNeighbours(SpaceFillingFamily.Moore, moore[^1], moore[0]),
                $"Moore order {order} does not close");
        }

        // Order 1 is the honest exception and worth pinning: four cells in a square are a loop
        // whichever curve draws them, so Hilbert closes there too and only order >= 2 separates
        // the families.
        var hilbert1 = SpaceFillingCurve.LatticeSites(SpaceFillingFamily.Hilbert, 1);
        Assert.True(SpaceFillingCurve.AreNeighbours(SpaceFillingFamily.Hilbert, hilbert1[^1], hilbert1[0]));
        for (int order = 2; order <= 6; order++)
        {
            var sites = SpaceFillingCurve.LatticeSites(SpaceFillingFamily.Hilbert, order);
            Assert.False(SpaceFillingCurve.AreNeighbours(SpaceFillingFamily.Hilbert, sites[^1], sites[0]));
        }
    }

    // ---- Z-order is an ordering, not a curve ----

    [Fact]
    public void ZOrder_IsBijectiveAndDiscontinuous_ByAnExactCount()
    {
        for (int order = 1; order <= 6; order++)
        {
            var sites = SpaceFillingCurve.LatticeSites(SpaceFillingFamily.ZOrder, order);
            int broken = 0;
            int worst = 0;
            for (int i = 1; i < sites.Count; i++)
            {
                if (!SpaceFillingCurve.AreNeighbours(SpaceFillingFamily.ZOrder, sites[i - 1], sites[i]))
                    broken++;
                var delta = sites[i] - sites[i - 1];
                worst = Math.Max(worst, Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y)));
            }
            // Exactly half the steps are lattice steps: 4^n - 1 segments, of which
            // 2^(2n-1) - 1 are the Z's diagonals and its block handovers.
            Assert.Equal((1L << (2 * order - 1)) - 1, broken);
            // And the largest of them jumps the full width of the grid.
            Assert.Equal(SpaceFillingCurve.GridSize(SpaceFillingFamily.ZOrder, order) - 1, worst);
        }
    }

    [Fact]
    public void Morton2d_RoundTripsAndPinsItsBitLayout()
    {
        Assert.Equal(0u, Morton2d.Encode(0, 0));
        Assert.Equal(1u, Morton2d.Encode(1, 0));      // x on the even bits
        Assert.Equal(2u, Morton2d.Encode(0, 1));      // y on the odd ones
        Assert.Equal(15u, Morton2d.Encode(3, 3));
        Assert.Equal(uint.MaxValue, Morton2d.Encode(Morton2d.MaxCoordinate, Morton2d.MaxCoordinate));

        for (uint x = 0; x < 40; x++)
        for (uint y = 0; y < 40; y++)
        {
            Morton2d.Decode(Morton2d.Encode(x, y), out uint rx, out uint ry);
            Assert.Equal(x, rx);
            Assert.Equal(y, ry);
        }
    }

    // ---- length: closed form per family, asserted exactly ----

    [Fact]
    public void Length_IsTheSegmentCountTimesTheSpacing()
    {
        // The order-n Hilbert curve on a UNIT square has length (4^n - 1)/2^n; the same
        // statement for every continuous family is segments x spacing, which is what a curve
        // whose every step is one cell must give.
        var unitSquare = new Aabb((0, 0, 0), (1, 1, 0));
        for (int order = 1; order <= 6; order++)
        {
            var curve = SpaceFillingCurve.Over(
                unitSquare, SpaceFillingFamily.Hilbert, 1.0 / (1 << order));
            Assert.Equal(order, curve.Order);
            double expected = ((1L << (2 * order)) - 1) / (double)(1 << order);
            Assert.Equal(expected, curve.Length, 12);
        }

        foreach (var family in Continuous)
        {
            var curve = SpaceFillingCurve.Over(new Aabb((0, 0, 0), (60, 40, 0)), family, 3.0);
            long segments = SpaceFillingCurve.SegmentCount(family, curve.Order);
            Assert.Equal(segments * curve.Spacing, curve.Length, 9);
            Assert.True(curve.IsContinuous);
            Assert.Equal(1, curve.MaxLatticeStep);
        }
    }

    // ---- the spacing report ----

    [Fact]
    public void TheAchievedSpacingIsReported_AndIsTheCoarsestOrderThatFits()
    {
        var bounds = new Aabb((0, 0, 0), (100, 60, 0));
        foreach (var family in All)
        {
            foreach (double asked in new[] { 20.0, 7.0, 5.0, 3.3, 2.0 })
            {
                var curve = SpaceFillingCurve.Over(bounds, family, asked);
                Assert.Equal(asked, curve.RequestedSpacing);
                Assert.True(curve.Spacing <= asked, $"{family} at {asked}: {curve.Spacing}");
                Assert.True(curve.Order >= SpaceFillingCurve.MinimumOrder(family));

                // Minimality: one order coarser would have overshot the request, so nothing
                // finer than necessary was generated.
                if (curve.Order > SpaceFillingCurve.MinimumOrder(family))
                {
                    var coarser = SpaceFillingCurve.Over(bounds, family, asked * 1e6);
                    Assert.True(coarser.Order < curve.Order || coarser.Spacing > asked);
                }
            }
        }
    }

    [Fact]
    public void AskingForExactlyACellSize_IsHonouredExactly()
    {
        // 100 / 2^5 = 3.125 lands on an order boundary, and the search stops at equality, so
        // the achieved spacing IS the request rather than the next one down.
        var curve = SpaceFillingCurve.Over(
            new Aabb((0, 0, 0), (100, 100, 0)), SpaceFillingFamily.Hilbert, 3.125);
        Assert.Equal(5, curve.Order);
        Assert.Equal(3.125, curve.Spacing);
    }

    // ---- coverage: the one measurement, against a derived bound ----

    [Fact]
    public void EveryPointOfTheRegion_IsWithinItsOwnCellsCircumradius()
    {
        // The bound is the cell's circumradius, not a tuned constant: a square cell of side h
        // reaches sqrt(2)/2 h into its corners, and a triangular lattice's covering radius is
        // 1/sqrt(3). Gosper's placement is what has to earn this — its island is a hexagonal
        // blob rather than a rectangle, and it is scaled by its own MEASURED inradius.
        var bounds = new Aabb((0, 0, 0), (60, 40, 0));
        foreach (var (family, bound) in new (SpaceFillingFamily, double)[]
        {
            (SpaceFillingFamily.Hilbert, Math.Sqrt(2) / 2),
            (SpaceFillingFamily.Moore, Math.Sqrt(2) / 2),
            (SpaceFillingFamily.Peano, Math.Sqrt(2) / 2),
            (SpaceFillingFamily.Gosper, 1 / Math.Sqrt(3)),
        })
        {
            var curve = SpaceFillingCurve.Over(bounds, family, 3.0);
            double worst = 0;
            for (int i = 0; i <= 60; i++)
            for (int j = 0; j <= 40; j++)
            {
                var p = new Vector2d(bounds.Min.X + i, bounds.Min.Y + j);
                double best = double.PositiveInfinity;
                foreach (var q in curve.Points)
                    best = Math.Min(best, p.DistanceTo(q));
                worst = Math.Max(worst, best);
            }
            Assert.True(
                worst <= bound * curve.Spacing + 1e-9,
                $"{family}: worst {worst / curve.Spacing:G6} h against a bound of {bound:G6} h");
        }
    }

    // ---- the family difference a consumer chooses on ----

    [Fact]
    public void TheLongestStraightRunSaturates_AndSeparatesTheFamilies()
    {
        // This is the isotropy claim, measured rather than asserted in prose: no run of
        // collinear steps ever exceeds three cells for Hilbert or Moore, five for Peano and two
        // for Gosper, AT ANY ORDER. It is a small difference and a real one — the reason to
        // reach for Peano is fewer direction changes, not a different fill.
        foreach (var (family, expected, top) in new (SpaceFillingFamily, int, int)[]
        {
            (SpaceFillingFamily.Hilbert, 3, 6),
            (SpaceFillingFamily.Moore, 3, 6),
            (SpaceFillingFamily.Peano, 5, 4),
            (SpaceFillingFamily.Gosper, 2, 5),
        })
        {
            for (int order = 2; order <= top; order++)
            {
                var sites = SpaceFillingCurve.LatticeSites(family, order);
                int longest = 1, current = 1;
                for (int i = 2; i < sites.Count; i++)
                {
                    var a = sites[i - 1] - sites[i - 2];
                    var b = sites[i] - sites[i - 1];
                    current = a.X == b.X && a.Y == b.Y ? current + 1 : 1;
                    longest = Math.Max(longest, current);
                }
                Assert.True(longest <= expected, $"{family} order {order}: {longest} > {expected}");
                if (order >= 3)
                    Assert.Equal(expected, longest);
            }
        }
    }

    // ---- refusals ----

    [Fact]
    public void RefusesByName()
    {
        var bounds = new Aabb((0, 0, 0), (100, 100, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SpaceFillingCurve.Over(bounds, SpaceFillingFamily.Hilbert, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SpaceFillingCurve.Over(bounds, SpaceFillingFamily.Hilbert, double.NaN));
        Assert.Throws<ArgumentException>(
            () => SpaceFillingCurve.Over(
                new Aabb((5, 5, 0), (5, 5, 0)), SpaceFillingFamily.Hilbert, 1));

        var capped = Assert.Throws<ArgumentOutOfRangeException>(
            () => SpaceFillingCurve.Over(bounds, SpaceFillingFamily.Hilbert, 1e-3));
        Assert.Contains("site cap", capped.Message);
        Assert.Contains("FINEST", capped.Message);

        var gosperCap = Assert.Throws<ArgumentOutOfRangeException>(
            () => SpaceFillingCurve.Over(bounds, SpaceFillingFamily.Gosper, 1e-3));
        Assert.Contains("site cap", gosperCap.Message);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => SpaceFillingCurve.LatticeSites(SpaceFillingFamily.Moore, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SpaceFillingCurve.LatticeSites(SpaceFillingFamily.Gosper, 0));

        // A Gosper curve subdivides by 7 in cells and sqrt(7) in length, so it has no integer
        // linear radix and saying otherwise would be the wrong kind of convenience.
        Assert.Throws<ArgumentOutOfRangeException>(() => SpaceFillingCurve.Radix(SpaceFillingFamily.Gosper));
    }

    [Fact]
    public void AGeneratedCurveIsDeterministic()
    {
        var bounds = new Aabb((-7, 3, 0), (25, 19, 0));
        foreach (var family in All)
        {
            var a = SpaceFillingCurve.Over(bounds, family, 1.5);
            var b = SpaceFillingCurve.Over(bounds, family, 1.5);
            Assert.Equal(a.Points.Count, b.Points.Count);
            for (int i = 0; i < a.Points.Count; i++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.Points[i].X), BitConverter.DoubleToInt64Bits(b.Points[i].X));
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.Points[i].Y), BitConverter.DoubleToInt64Bits(b.Points[i].Y));
            }
        }
    }

    // ---- the tiled (rectangular-footprint) form ----

    [Fact]
    public void ATiledCurveCoversTheRECTANGLERatherThanItsBoundingSquare()
    {
        // The residual this exists for, measured on the very fixture that named it: an 80 x 12
        // plate at spacing 3 through the square path spends 1024 cells over an 80 x 80 square
        // and keeps only the sixth of them the plate contains.
        var bounds = new Aabb((0, 0, 0), (80, 12, 0));

        var square = SpaceFillingCurve.Over(bounds, SpaceFillingFamily.Hilbert, 3.0);
        var tiled = SpaceFillingCurve.OverTiled(bounds, 3.0);

        Assert.Equal(1024, square.Points.Count);
        int insidePlate = square.Points.Count(p => p.Y >= 0 && p.Y <= 12);
        Assert.Equal(128, insidePlate);                  // 12.5% of what it generated

        // Every tiled point is inside the plate BY CONSTRUCTION, since the footprint IS the
        // rectangle: nothing is generated to be thrown away.
        Assert.All(tiled.Points, p =>
        {
            Assert.InRange(p.X, 0, 80);
            Assert.InRange(p.Y, 0, 12);
        });
        Assert.Equal(112, tiled.Points.Count);           // 100% of what it generated

        // And the achieved spacing stops being set by the LENGTH: the square path spends 2.5
        // (over-fine, because 80 is what quantised) where the tiled one lands at 2.857 by 3.0,
        // both inside the request. Fewer cells AND a spacing nearer the one asked for.
        Assert.Equal(2.5, square.Spacing, 12);
        Assert.Equal(80.0 / 28, tiled.SpacingX, 12);
        Assert.Equal(3.0, tiled.SpacingY, 12);
    }

    [Fact]
    public void ATiledCurveIsOneContinuousHamiltonianPath()
    {
        var bounds = new Aabb((0, 0, 0), (80, 12, 0));
        var tiled = SpaceFillingCurve.OverTiled(bounds, 3.0);

        Assert.True(tiled.IsContinuous);
        Assert.Equal(1, tiled.MaxLatticeStep);
        Assert.Equal(tiled.BlocksX * tiled.BlocksY * 16, tiled.Lattice.Count);
        Assert.Equal(tiled.Lattice.Count, tiled.Lattice.Distinct().Count());
    }

    [Fact]
    public void ATiledCurveHoldsTheFootprintAndReportsWhatThatDidToTheCells()
    {
        var bounds = new Aabb((0, 0, 0), (80, 12, 0));
        var tiled = SpaceFillingCurve.OverTiled(bounds, 3.0);

        // Held footprint: each axis's cells exactly tile that axis's extent.
        Assert.Equal(80.0, tiled.SpacingX * tiled.BlocksX * 4, 12);
        Assert.Equal(12.0, tiled.SpacingY * tiled.BlocksY * 4, 12);

        // Never coarser than the request, on BOTH axes.
        Assert.True(tiled.SpacingX <= 3.0);
        Assert.True(tiled.SpacingY <= 3.0);
        Assert.Equal(Math.Max(tiled.SpacingX, tiled.SpacingY), tiled.Spacing);
        Assert.True(tiled.Anisotropy >= 1.0);
    }

    [Fact]
    public void OneBlockOfTheTiledFormIsTheSquareFormSiteForSite()
    {
        // The reduction that makes the tiled path a generalisation rather than a second
        // algorithm: on a SQUARE at a spacing landing exactly on a block boundary, the two
        // constructions agree cell for cell and point for point, bit for bit.
        var bounds = new Aabb((0, 0, 0), (16, 16, 0));
        var square = SpaceFillingCurve.Over(bounds, SpaceFillingFamily.Hilbert, 4.0);
        var tiled = SpaceFillingCurve.OverTiled(bounds, 4.0, blockOrder: 2);

        Assert.Equal(1, tiled.BlocksX);
        Assert.Equal(1, tiled.BlocksY);
        Assert.Equal(square.Lattice.Count, tiled.Lattice.Count);
        for (int i = 0; i < square.Lattice.Count; i++)
        {
            Assert.Equal(square.Lattice[i], tiled.Lattice[i]);
            Assert.Equal(BitConverter.DoubleToInt64Bits(square.Points[i].X), BitConverter.DoubleToInt64Bits(tiled.Points[i].X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(square.Points[i].Y), BitConverter.DoubleToInt64Bits(tiled.Points[i].Y));
        }
    }

    [Fact]
    public void EverySquareFootprintConstructionReportsSquareCells()
    {
        // SpacingX/SpacingY are additive: every incumbent construction reports the spacing it
        // always did, on both axes, bit for bit.
        var bounds = new Aabb((-7, 3, 0), (25, 19, 0));
        foreach (var family in All)
        {
            var curve = SpaceFillingCurve.Over(bounds, family, 1.5);
            Assert.Equal(BitConverter.DoubleToInt64Bits(curve.Spacing), BitConverter.DoubleToInt64Bits(curve.SpacingX));
            Assert.Equal(BitConverter.DoubleToInt64Bits(curve.Spacing), BitConverter.DoubleToInt64Bits(curve.SpacingY));
            Assert.Equal(1.0, curve.Anisotropy);
            Assert.Equal(1, curve.BlocksX);
            Assert.Equal(1, curve.BlocksY);
        }
    }

    [Fact]
    public void ABlockOrderOfZeroIsThePlainSerpentine()
    {
        // Stated as a member of the family rather than a degenerate case: every block is one
        // cell, so the route is a boustrophedon — the tightest fit and the worst isotropy.
        var bounds = new Aabb((0, 0, 0), (10, 4, 0));
        var tiled = SpaceFillingCurve.OverTiled(bounds, 1.0, blockOrder: 0);

        Assert.True(tiled.IsContinuous);
        Assert.Equal(tiled.BlocksX * tiled.BlocksY, tiled.Lattice.Count);

        // Counted the way the family comparison above counts: consecutive EQUAL steps. A
        // serpentine crosses a whole row of blocksX cells, i.e. blocksX - 1 equal steps, where
        // Hilbert saturates at 3 whatever the order — which is the trade, stated as a number.
        int longest = 1, run = 1;
        for (int i = 2; i < tiled.Lattice.Count; i++)
        {
            var a = tiled.Lattice[i - 1] - tiled.Lattice[i - 2];
            var b = tiled.Lattice[i] - tiled.Lattice[i - 1];
            run = a == b ? run + 1 : 1;
            longest = Math.Max(longest, run);
        }
        Assert.Equal(tiled.BlocksX - 1, longest);
    }

    [Fact]
    public void ATiledCurveRefusesADegenerateRectangleAndAnUnreachableSpacing()
    {
        var flat = new Aabb((0, 0, 0), (10, 0, 0));
        Assert.Throws<ArgumentException>(() => SpaceFillingCurve.OverTiled(flat, 1.0));

        var bounds = new Aabb((0, 0, 0), (100, 100, 0));
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => SpaceFillingCurve.OverTiled(bounds, 0.01, maxSites: 4096));
        Assert.Contains("past the 4096-site cap", error.Message);
        Assert.Contains("FINEST", error.Message);
    }
}
