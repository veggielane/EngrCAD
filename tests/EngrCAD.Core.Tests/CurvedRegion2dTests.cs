using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// The curved 2D tier: <see cref="CurvedEdge2d"/> / <see cref="CurvedRegion2d"/> and the
/// closed-form intersections under them. Every assertion here is against a CLOSED FORM —
/// a disc's area, a lens's area, a circular segment's area — because the whole point of
/// carrying arcs through the arrangement is that those numbers come out exactly rather
/// than as an inscribed polygon's approximation.
/// </summary>
public class CurvedRegion2dTests
{
    private static double LensArea(double radius, double centreDistance) =>
        2 * radius * radius * Math.Acos(centreDistance / (2 * radius))
        - centreDistance / 2 * Math.Sqrt(4 * radius * radius - centreDistance * centreDistance);

    /// <summary>Area of the circular segment cut off by the chord at x = h on a disc of radius r.</summary>
    private static double SegmentArea(double radius, double h) =>
        radius * radius * Math.Acos(h / radius) - h * Math.Sqrt(radius * radius - h * h);

    private static CurvedRegion2d Rectangle(double x0, double y0, double x1, double y1) =>
        new([
            CurvedEdge2d.Line((x0, y0), (x1, y0)),
            CurvedEdge2d.Line((x1, y0), (x1, y1)),
            CurvedEdge2d.Line((x1, y1), (x0, y1)),
            CurvedEdge2d.Line((x0, y1), (x0, y0)),
        ]);

    // ---- the edge vocabulary ----

    [Fact]
    public void Disc_AreaIsExactlyPiRSquared()
    {
        var disc = CurvedRegion2d.Disc((3, -4), 2.5);
        Assert.Equal(Math.PI * 2.5 * 2.5, disc.Area, 12);
    }

    [Fact]
    public void HalfDiscAndItsChord_CloseToTheAnalyticHalfArea()
    {
        // Semicircle: the arc from 0 to pi plus the diameter chord back.
        var region = new CurvedRegion2d([
            CurvedEdge2d.Arc((0, 0), 4, 0, Math.PI),
            CurvedEdge2d.Line((-4, 0), (4, 0)),
        ]);
        Assert.Equal(Math.PI * 16 / 2, region.Area, 12);
    }

    [Fact]
    public void ArcBounds_IncludeTheCardinalExtremesInsideTheSweep()
    {
        // A three-quarter arc from -pi/2 round to pi reaches the +x, +y and -y extremes.
        var edge = CurvedEdge2d.Arc((0, 0), 1, -Math.PI / 2, 1.5 * Math.PI);
        var box = edge.Bounds();
        Assert.Equal(1, box.Max.X, 12);
        Assert.Equal(1, box.Max.Y, 12);
        Assert.Equal(-1, box.Min.Y, 12);
        Assert.Equal(-1, box.Min.X, 12);
    }

    [Fact]
    public void RayCrossings_PutTheCentreInsideADiscAndPointsOutsideOut()
    {
        var disc = CurvedRegion2d.Disc((10, 10), 3);
        Assert.True(disc.ParityInside((10, 10)));
        Assert.True(disc.ParityInside((12.9, 10)));
        Assert.False(disc.ParityInside((13.1, 10)));
        Assert.False(disc.ParityInside((6.9, 10)));
        Assert.False(disc.ParityInside((10, 13.1)));
    }

    [Fact]
    public void ParityAcrossACirclesSeam_IsNotFooledByTheClosureGap()
    {
        // The seam of a full-turn arc is where sin(2 pi) != 0 shows: an ordinate a few ulps
        // BELOW the start point used to fall between the piece's two endpoints, and a point
        // measurably inside the disc came back outside. Regression for the two rules that
        // fixed it (a full turn's End IS its Start, and the first/last monotone piece takes
        // its ordinate from the stored endpoints).
        var disc = CurvedRegion2d.Disc((0, 0), 10);
        Assert.Equal(disc.Outer[0].Start, disc.Outer[0].End);
        foreach (double y in new[] { 0.0, -1e-15, 1e-15, -4e-16, 4e-16, -1e-9, 1e-9 })
        {
            Assert.True(disc.ParityInside((7.5, y)), $"y = {y}");
            Assert.False(disc.ParityInside((12.5, y)), $"y = {y}");
        }
    }

    [Fact]
    public void ReversedRegion_KeepsAreaAndContainment()
    {
        var disc = CurvedRegion2d.Disc((0, 0), 5);
        var reversed = disc.Reversed();
        Assert.Equal(disc.Area, reversed.Area, 12);
        Assert.True(reversed.Contains((1, 1)));
        Assert.False(reversed.IsCounterClockwise);
    }

    // ---- closed-form intersections ----

    [Fact]
    public void LineArcIntersection_IsExactOnTheTwoTransversalRoots()
    {
        var arc = CurvedEdge2d.Circle((0, 0), 5);
        var line = CurvedEdge2d.Line((-10, 3), (10, 3));
        var contacts = CurveIntersection2d.Intersect(line, arc, 1e-9);
        Assert.Equal(2, contacts.Count);
        foreach (var contact in contacts)
        {
            Assert.Equal(3, contact.Point.Y, 12);
            Assert.Equal(4, Math.Abs(contact.Point.X), 12);
        }
    }

    [Fact]
    public void LineTangentToACircle_ReportsExactlyOneTouch()
    {
        var arc = CurvedEdge2d.Circle((0, 0), 5);
        var line = CurvedEdge2d.Line((-10, 5), (10, 5));
        var contacts = CurveIntersection2d.Intersect(line, arc, 1e-9);
        var contact = Assert.Single(contacts);
        Assert.Equal(0, contact.Point.X, 9);
        Assert.Equal(5, contact.Point.Y, 12);
    }

    [Fact]
    public void LineMissingACircle_ReportsNothing()
    {
        var arc = CurvedEdge2d.Circle((0, 0), 5);
        var line = CurvedEdge2d.Line((-10, 5.001), (10, 5.001));
        Assert.Empty(CurveIntersection2d.Intersect(line, arc, 1e-9));
    }

    [Fact]
    public void ArcArcIntersection_IsExactOnBothRoots()
    {
        // Two unit circles centred 1 apart meet at x = 1/2, y = +-sqrt(3)/2.
        var a = CurvedEdge2d.Circle((0, 0), 1);
        var b = CurvedEdge2d.Circle((1, 0), 1);
        var contacts = CurveIntersection2d.Intersect(a, b, 1e-9);
        Assert.Equal(2, contacts.Count);
        foreach (var contact in contacts)
        {
            Assert.Equal(0.5, contact.Point.X, 12);
            Assert.Equal(Math.Sqrt(3) / 2, Math.Abs(contact.Point.Y), 12);
        }
    }

    [Fact]
    public void ExternallyTangentCircles_ReportOneTouch()
    {
        var a = CurvedEdge2d.Circle((0, 0), 2);
        var b = CurvedEdge2d.Circle((5, 0), 3);
        var contact = Assert.Single(CurveIntersection2d.Intersect(a, b, 1e-9));
        Assert.Equal(2, contact.Point.X, 9);
        Assert.Equal(0, contact.Point.Y, 9);
    }

    [Fact]
    public void InternallyTangentCircles_ReportOneTouch()
    {
        var a = CurvedEdge2d.Circle((0, 0), 5);
        var b = CurvedEdge2d.Circle((2, 0), 3);
        var contact = Assert.Single(CurveIntersection2d.Intersect(a, b, 1e-9));
        Assert.Equal(5, contact.Point.X, 9);
        Assert.Equal(0, contact.Point.Y, 9);
    }

    [Fact]
    public void ConcentricCircles_NeverIntersect()
    {
        var a = CurvedEdge2d.Circle((0, 0), 5);
        var b = CurvedEdge2d.Circle((0, 0), 3);
        Assert.Empty(CurveIntersection2d.Intersect(a, b, 1e-9));
    }

    [Fact]
    public void NestedCirclesWithNoContact_NeverIntersect()
    {
        var a = CurvedEdge2d.Circle((0, 0), 5);
        var b = CurvedEdge2d.Circle((1, 0), 3);
        Assert.Empty(CurveIntersection2d.Intersect(a, b, 1e-9));
    }

    [Fact]
    public void SameCarrier_SeesCocircularArcsAndCollinearSegments()
    {
        Assert.True(CurveIntersection2d.SameCarrier(
            CurvedEdge2d.Arc((1, 2), 3, 0, 1), CurvedEdge2d.Arc((1, 2), 3, 2, 1), 1e-9));
        Assert.False(CurveIntersection2d.SameCarrier(
            CurvedEdge2d.Arc((1, 2), 3, 0, 1), CurvedEdge2d.Arc((1, 2), 3.001, 2, 1), 1e-9));
        Assert.True(CurveIntersection2d.SameCarrier(
            CurvedEdge2d.Line((0, 0), (1, 1)), CurvedEdge2d.Line((5, 5), (7, 7)), 1e-9));
        Assert.False(CurveIntersection2d.SameCarrier(
            CurvedEdge2d.Line((0, 0), (1, 1)), CurvedEdge2d.Line((0, 1), (1, 2)), 1e-9));
    }

    // ---- booleans that keep their arcs ----

    [Fact]
    public void TwoOverlappingDiscs_UnionMatchesTheAnalyticArea()
    {
        var a = CurvedRegion2d.Disc((0, 0), 1);
        var b = CurvedRegion2d.Disc((1, 0), 1);
        var union = a.Union(b);
        var region = Assert.Single(union);
        Assert.Equal(2 * Math.PI - LensArea(1, 1), region.Area, 10);
        Assert.All(region.Outer, edge => Assert.True(edge.IsArc));
    }

    [Fact]
    public void TwoOverlappingDiscs_IntersectionIsTheAnalyticLens()
    {
        var a = CurvedRegion2d.Disc((0, 0), 1);
        var b = CurvedRegion2d.Disc((1, 0), 1);
        var region = Assert.Single(a.Intersect(b));
        Assert.Equal(LensArea(1, 1), region.Area, 10);
        Assert.Equal(2, region.Outer.Count);
        Assert.All(region.Outer, edge => Assert.True(edge.IsArc));
    }

    [Fact]
    public void TwoOverlappingDiscs_DifferenceIsTheAnalyticCrescent()
    {
        var a = CurvedRegion2d.Disc((0, 0), 1);
        var b = CurvedRegion2d.Disc((1, 0), 1);
        var region = Assert.Single(a.Subtract(b));
        Assert.Equal(Math.PI - LensArea(1, 1), region.Area, 10);
    }

    [Fact]
    public void DiscMinusAChord_IsTheAnalyticCircularSegment()
    {
        var disc = CurvedRegion2d.Disc((0, 0), 10);
        var knife = Rectangle(5, -20, 20, 20);
        var region = Assert.Single(disc.Intersect(knife));
        Assert.Equal(SegmentArea(10, 5), region.Area, 9);
        // One arc and one chord: the boolean did not flatten anything.
        Assert.Equal(2, region.Outer.Count);
        Assert.Equal(1, region.Outer.Count(e => e.IsArc));
    }

    [Fact]
    public void DiscInsideARectangle_BecomesAHole()
    {
        var plate = Rectangle(-10, -10, 10, 10);
        var bore = CurvedRegion2d.Disc((0, 0), 3);
        var region = Assert.Single(plate.Subtract(bore));
        Assert.Equal(400 - Math.PI * 9, region.Area, 10);
        var hole = Assert.Single(region.Holes);
        Assert.All(hole, edge => Assert.True(edge.IsArc));
        Assert.Equal(Math.PI * 9, Math.Abs(CurvedRegion2d.SignedArea(hole)), 10);
    }

    [Fact]
    public void DisjointDiscs_UnionKeepsBothAndTheCircleStaysOneEdge()
    {
        var a = CurvedRegion2d.Disc((0, 0), 1);
        var b = CurvedRegion2d.Disc((5, 0), 2);
        var union = a.Union(b);
        Assert.Equal(2, union.Count);
        Assert.Equal(Math.PI + 4 * Math.PI, union.Sum(r => r.Area), 10);
        // MergeChain fuses the two half-arcs the arrangement had to split each circle into.
        Assert.All(union, r => Assert.Single(r.Outer));
        Assert.All(union, r => Assert.True(r.Outer[0].IsFullCircle));
    }

    [Fact]
    public void UnionOfIdenticalDiscs_IsOneDisc()
    {
        var a = CurvedRegion2d.Disc((2, 3), 4);
        var b = CurvedRegion2d.Disc((2, 3), 4);
        var region = Assert.Single(a.Union(b));
        Assert.Equal(Math.PI * 16, region.Area, 10);
    }

    [Fact]
    public void IntersectionOfDisjointDiscs_IsEmpty()
    {
        var a = CurvedRegion2d.Disc((0, 0), 1);
        var b = CurvedRegion2d.Disc((5, 0), 1);
        Assert.Empty(a.Intersect(b));
    }

    [Fact]
    public void StraightOnlyRegions_AgreeWithThePolygonalBoolean()
    {
        var a = Rectangle(0, 0, 10, 10);
        var b = Rectangle(5, 5, 15, 15);
        var curved = a.Union(b);
        var polygonal = Region2dBoolean.Union(
            new Region2d([(0, 0), (10, 0), (10, 10), (0, 10)]),
            new Region2d([(5, 5), (15, 5), (15, 15), (5, 15)]));
        Assert.Equal(polygonal.Sum(r => r.Area), curved.Sum(r => r.Area), 12);
        Assert.Equal(polygonal.Count, curved.Count);
        // Collinear T-junctions are merged away, exactly as WithoutCollinearVertices does.
        Assert.Equal(polygonal[0].Outer.Count, curved[0].Outer.Count);
    }

    [Theory]
    [InlineData(1e-3)]
    [InlineData(1.0)]
    [InlineData(1e3)]
    public void CurvedBooleans_AreScaleFree(double scale)
    {
        var a = CurvedRegion2d.Disc((0, 0), scale);
        var b = CurvedRegion2d.Disc((scale, 0), scale);
        var region = Assert.Single(a.Intersect(b));
        double expected = LensArea(scale, scale);
        Assert.Equal(expected, region.Area, Math.Abs(expected) * 1e-9);
    }

    // ---- the tangency policy ----

    [Fact]
    public void ExternallyTangentDiscs_UnionKeepsBothAreasAndStaysValid()
    {
        var a = CurvedRegion2d.Disc((0, 0), 1);
        var b = CurvedRegion2d.Disc((2, 0), 1);
        var union = a.Union(b);
        Assert.Equal(2 * Math.PI, union.Sum(r => r.Area), 9);
    }

    [Fact]
    public void ADiscTangentToAStraightEdge_OrdersTheNodeByCURVATURE_NotByTheNoisyDirection()
    {
        // Regression for the tangency comparator. At an exact tangency the two departures'
        // computed directions differ only by round-off - the arc leaving this node reads
        // (-1.22e-16, -1), whose x sign is nothing but the error in sin(pi) - and the exact
        // orientation predicate then makes a CONFIDENT WRONG decision about a quantity that
        // carries no information. Before the curvature re-ordering pass, the tightest-turn
        // walk closed no face at all and this union came back EMPTY.
        var plate = Rectangle(0, 0, 20, 10);
        var boss = CurvedRegion2d.Disc((24, 5), 4);   // touches x = 20 at exactly one point
        var union = plate.Union(boss);
        Assert.Equal(2, union.Count);
        Assert.Equal(200 + Math.PI * 16, union.Sum(r => r.Area), 8);

        // The mirror image exercises the other half-plane, and the vertical mirror the
        // other sign of the arc's noise.
        var left = CurvedRegion2dBoolean.Union(Rectangle(0, 0, 20, 10), CurvedRegion2d.Disc((-4, 5), 4));
        Assert.Equal(200 + Math.PI * 16, left.Sum(r => r.Area), 8);
        var below = CurvedRegion2dBoolean.Union(Rectangle(0, 0, 20, 10), CurvedRegion2d.Disc((10, -4), 4));
        Assert.Equal(200 + Math.PI * 16, below.Sum(r => r.Area), 8);
        var above = CurvedRegion2dBoolean.Union(Rectangle(0, 0, 20, 10), CurvedRegion2d.Disc((10, 14), 4));
        Assert.Equal(200 + Math.PI * 16, above.Sum(r => r.Area), 8);
    }

    [Fact]
    public void TwoDiscsTangentToOneLineAtTheSamePoint_AreOrderedByCurvature()
    {
        // Three curves through one node with a common tangent: a straight edge and two
        // circles of different radii on the SAME side. Only the curvature separates them.
        var plate = Rectangle(0, 0, 20, 10);
        var small = CurvedRegion2d.Disc((22, 5), 2);
        var large = CurvedRegion2d.Disc((26, 5), 6);
        var union = CurvedRegion2dBoolean.UnionAll([plate, small, large]);
        // The small disc sits entirely inside the large one (centres 4 apart, radii 2 and 6
        // - internally tangent at x = 20), so the union is the plate plus the large disc.
        Assert.Equal(200 + Math.PI * 36, union.Sum(r => r.Area), 7);
    }

    [Fact]
    public void ATangentChordThroughADisc_LeavesTheDiscWhole()
    {
        // The knife's edge is exactly tangent to the circle: nothing is removed.
        var disc = CurvedRegion2d.Disc((0, 0), 10);
        var knife = Rectangle(10, -20, 30, 20);
        var region = Assert.Single(disc.Subtract(knife));
        Assert.Equal(Math.PI * 100, region.Area, 8);
    }

    // ---- polygonal bridges ----

    [Fact]
    public void FromRegion_AndBack_PreservesAPolygon()
    {
        var square = new Region2d([(0, 0), (4, 0), (4, 3), (0, 3)]);
        var curved = CurvedRegion2d.FromRegion(square);
        Assert.Equal(12, curved.Area, 12);
        Assert.All(curved.Outer, edge => Assert.False(edge.IsArc));
        Assert.Equal(12, curved.ToRegion().Area, 12);
    }

    [Fact]
    public void ToRegion_InscribesArcsWithinTheChordTolerance()
    {
        var disc = CurvedRegion2d.Disc((0, 0), 10);
        var flattened = disc.ToRegion(1e-3);
        // Inscribed: strictly under the true area, and within the sagitta budget.
        Assert.True(flattened.Area < disc.Area);
        Assert.True(disc.Area - flattened.Area < 0.05, $"flattening lost {disc.Area - flattened.Area}");
        foreach (var point in flattened.Outer)
            Assert.True(point.Length <= 10 + 1e-12);
    }

    [Fact]
    public void RegionConstructor_RefusesAnUnclosedChain()
    {
        var error = Assert.Throws<ArgumentException>(() => new CurvedRegion2d([
            CurvedEdge2d.Line((0, 0), (1, 0)),
            CurvedEdge2d.Line((1, 0), (1, 1)),
            CurvedEdge2d.Line((1, 1), (0.5, 2)),
        ]));
        Assert.Contains("not closed", error.Message);
    }

    [Fact]
    public void RegionConstructor_RefusesASelfCrossingChain()
    {
        // A bow tie: the two diagonals cross transversally.
        var error = Assert.Throws<ArgumentException>(() => new CurvedRegion2d([
            CurvedEdge2d.Line((0, 0), (4, 4)),
            CurvedEdge2d.Line((4, 4), (4, 0)),
            CurvedEdge2d.Line((4, 0), (0, 4)),
            CurvedEdge2d.Line((0, 4), (0, 0)),
        ]));
        Assert.Contains("crosses itself", error.Message);
    }
}
