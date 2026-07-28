using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// Robustness of the curved 2D tier on the configurations that break naive implementations:
/// bulk unions, exact tangency, concentric and cocircular carriers, chained differences,
/// and cross-checks against the polygonal path (which must agree to within its own
/// flattening budget and no better).
/// </summary>
public class CurvedRegion2dRobustnessTests
{
    private static CurvedRegion2d Rectangle(double x0, double y0, double x1, double y1) =>
        new([
            CurvedEdge2d.Line((x0, y0), (x1, y0)),
            CurvedEdge2d.Line((x1, y0), (x1, y1)),
            CurvedEdge2d.Line((x1, y1), (x0, y1)),
            CurvedEdge2d.Line((x0, y1), (x0, y0)),
        ]);

    [Fact]
    public void ManyOverlappingDiscs_UnionToOneRegionAndTheAreaIsBracketed()
    {
        // Twelve discs on a bolt circle, each overlapping both neighbours: one region, and
        // its area sits between the biggest single disc and the plain sum.
        var discs = Enumerable.Range(0, 12)
            .Select(i => CurvedRegion2d.Disc(
                (8 * Math.Cos(i * Math.PI / 6), 8 * Math.Sin(i * Math.PI / 6)), 2.5))
            .ToList();
        var union = CurvedRegion2dBoolean.UnionAll(discs);
        var region = Assert.Single(union);

        double sum = discs.Sum(d => d.Area);
        Assert.True(region.Area < sum, "overlaps must be counted once");
        Assert.True(region.Area > sum * 0.8);
        Assert.Single(region.Holes);   // the ring encloses one
        Assert.All(region.Outer, edge => Assert.True(edge.IsArc));
    }

    [Fact]
    public void CurvedUnion_AgreesWithThePolygonalOneToWithinTheFlatteningBudget()
    {
        var a = CurvedRegion2d.Disc((0, 0), 10);
        var b = CurvedRegion2d.Disc((9, 3), 7);
        var curved = CurvedRegion2dBoolean.Union(a, b).Sum(r => r.Area);

        // Same operands, flattened at 1e-4 first.
        var polygonal = Region2dBoolean
            .Union(a.ToRegion(1e-4), b.ToRegion(1e-4))
            .Sum(r => r.Area);

        // Inscribed, so the polygonal answer is SHORT - measurably, and by no more than
        // the sagitta budget over the two perimeters.
        Assert.True(polygonal < curved);
        Assert.True(curved - polygonal < 1e-4 * 2 * Math.PI * (10 + 7),
            $"curved {curved} vs polygonal {polygonal}");
    }

    [Fact]
    public void InternallyTangentDiscs_SubtractCleanly()
    {
        // A small disc tangent to the inside of a large one: the difference is a crescent
        // that pinches to a point, and its area is exactly the difference of the two.
        var big = CurvedRegion2d.Disc((0, 0), 10);
        var small = CurvedRegion2d.Disc((4, 0), 6);
        var region = Assert.Single(big.Subtract(small));
        Assert.Equal(Math.PI * 100 - Math.PI * 36, region.Area, 8);
    }

    [Fact]
    public void AnAnnulus_KeepsBothCirclesAndItsExactArea()
    {
        var annulus = Assert.Single(
            CurvedRegion2d.Disc((0, 0), 10).Subtract(CurvedRegion2d.Disc((0, 0), 4)));
        Assert.Equal(Math.PI * (100 - 16), annulus.Area, 10);
        Assert.True(Assert.Single(annulus.Outer).IsFullCircle);
        Assert.True(Assert.Single(Assert.Single(annulus.Holes)).IsFullCircle);
        Assert.True(annulus.Contains((7, 0)));
        Assert.False(annulus.ParityInside((2, 0)));
    }

    [Fact]
    public void ChainedDifferences_StayExact()
    {
        var plate = Rectangle(-20, -10, 20, 10);
        var result = new List<CurvedRegion2d> { plate };
        double removed = 0;
        for (int i = 0; i < 5; i++)
        {
            var bore = CurvedRegion2d.Disc((-14 + 7 * i, 0), 2);
            result = [.. CurvedRegion2dBoolean.Difference(result, [bore])];
            removed += bore.Area;
        }
        var region = Assert.Single(result);
        Assert.Equal(800 - removed, region.Area, 9);
        Assert.Equal(5, region.Holes.Count);
    }

    [Fact]
    public void ACocircularOverlap_DedupesInsteadOfDoubling()
    {
        // Two half-discs on the SAME circle, overlapping over a quarter: the shared arc runs
        // must dedupe as one carrier, not double-count into a mess.
        var upper = new CurvedRegion2d([
            CurvedEdge2d.Arc((0, 0), 5, 0, Math.PI),          // (5,0) round the top to (-5,0)
            CurvedEdge2d.Line((-5, 0), (5, 0)),
        ]);
        var left = new CurvedRegion2d([
            CurvedEdge2d.Arc((0, 0), 5, Math.PI / 2, Math.PI), // (0,5) round the left to (0,-5)
            CurvedEdge2d.Line((0, -5), (0, 5)),
        ]);
        var region = Assert.Single(upper.Union(left));
        // Half a disc plus the lower-left quarter.
        Assert.Equal(Math.PI * 25 * 0.75, region.Area, 9);
    }

    [Fact]
    public void ADiscTangentToAPlateEdge_UnionsWithoutLosingArea()
    {
        var plate = Rectangle(0, 0, 20, 10);
        var boss = CurvedRegion2d.Disc((20, 5), 4);   // tangent internally is not the case;
        var union = plate.Union(boss);                 // this one straddles the right edge
        Assert.Equal(200 + Math.PI * 16 / 2, union.Sum(r => r.Area), 9);
    }

    [Fact]
    public void ADiscExactlyTangentToAPlateEdgeFromOutside_KeepsBothPieces()
    {
        var plate = Rectangle(0, 0, 20, 10);
        var boss = CurvedRegion2d.Disc((24, 5), 4);   // touches x = 20 at a single point
        var union = plate.Union(boss);
        Assert.Equal(200 + Math.PI * 16, union.Sum(r => r.Area), 8);
    }

    [Fact]
    public void IntersectionWithATangentDisc_IsEmptyOrAPoint()
    {
        var plate = Rectangle(0, 0, 20, 10);
        var boss = CurvedRegion2d.Disc((24, 5), 4);
        // A point has no area; whatever comes back must carry none.
        Assert.True(plate.Intersect(boss).Sum(r => r.Area) < 1e-12);
    }

    [Fact]
    public void ManyEdges_StillValidateInReasonableTime()
    {
        // A 400-edge flattened loop exercises the crossing check's sweep broad phase; the
        // all-pairs form would be 80 000 curve intersections.
        const int n = 400;
        var edges = new CurvedEdge2d[n];
        for (int i = 0; i < n; i++)
        {
            var a = Point(i);
            var b = Point((i + 1) % n);
            edges[i] = CurvedEdge2d.Line(a, b);
        }
        var region = new CurvedRegion2d(edges);
        Assert.True(region.Area > 0);

        static Vector2d Point(int i)
        {
            double t = i * 2 * Math.PI / n;
            double r = 10 + 2 * Math.Cos(5 * t);
            return new Vector2d(r * Math.Cos(t), r * Math.Sin(t));
        }
    }

    [Theory]
    [InlineData(1e-3)]
    [InlineData(1.0)]
    [InlineData(1e3)]
    public void ChainedCurvedOperations_AreScaleFree(double s)
    {
        var plate = Rectangle(-20 * s, -10 * s, 20 * s, 10 * s);
        var bore = CurvedRegion2d.Disc((0, 0), 5 * s);
        var slot = Rectangle(-30 * s, -2 * s, 0, 2 * s);
        var cut = Assert.Single(CurvedRegion2dBoolean.Difference(
            CurvedRegion2dBoolean.Difference([plate], [bore]), [slot]));
        // The slot overlaps the bore, so the removed area is |bore ∪ slot| within the plate.
        var removed = Assert.Single(CurvedRegion2dBoolean.Intersection(
            CurvedRegion2dBoolean.Union(bore, slot), [plate]));
        Assert.Equal(plate.Area - removed.Area, cut.Area, Math.Abs(plate.Area) * 1e-12);
    }
}
