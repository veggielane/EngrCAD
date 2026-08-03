using System;
using System.Linq;
using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Core.Tests;

public class Region2dOffsetTests
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

    /// <summary>Regular polygon inscribed in a circle (vertices exactly on it).</summary>
    private static Region2d InscribedPolygon(double radius, int sides, Vector2d centre = default)
    {
        var points = new Vector2d[sides];
        for (int i = 0; i < sides; i++)
        {
            double angle = 2 * Math.PI * i / sides;
            points[i] = centre + new Vector2d(radius * Math.Cos(angle), radius * Math.Sin(angle));
        }
        return new Region2d(points);
    }

    /// <summary>The area a round join contributes: a fan of <paramref name="segments"/>
    /// isoceles triangles of side <paramref name="radius"/>, which is what an inscribed
    /// polygonal arc IS.</summary>
    private static double InscribedFanArea(double sweep, double radius, int segments) =>
        0.5 * segments * radius * radius * Math.Sin(sweep / segments);

    /// <summary>Mirrors <c>Region2dOffset.ArcSegments</c> so the tests can predict the
    /// flattening exactly instead of guessing a tolerance.</summary>
    private static int ArcSegments(double sweep, double radius, double tolerance) =>
        Math.Max(1, (int)Math.Ceiling(sweep / (2 * Math.Acos(Math.Max(1 - tolerance / radius, -1)))));

    // ---- straight-edged joins are EXACT: no flattening anywhere ----

    [Fact]
    public void SquareGrownWithMiterJoins_IsExactlyTheLargerSquare()
    {
        var result = Region2dOffset.Offset(Box(0, 0, 10, 10), 2, OffsetJoin.Miter);

        var grown = Assert.Single(result);
        Assert.Equal(14 * 14, grown.Area, 9);
        Assert.Equal(-2.0, grown.Bounds.Min.X, 9);
        Assert.Equal(12.0, grown.Bounds.Max.Y, 9);
        Assert.Equal(4, grown.Outer.Count);      // a square stays a 4-sided square
        AssertCanonical(result);
    }

    [Fact]
    public void SquareGrownWithChamferJoins_LosesExactlyTheCornerTriangles()
    {
        const double d = 2;
        var result = Region2dOffset.Offset(Box(0, 0, 10, 10), d, OffsetJoin.Chamfer);

        // Each 90-degree corner is bevelled by a right isoceles triangle of legs d.
        var grown = Assert.Single(result);
        Assert.Equal(14 * 14 - 4 * (0.5 * d * d), grown.Area, 9);
        Assert.Equal(8, grown.Outer.Count);
        AssertCanonical(result);
    }

    [Fact]
    public void LShapeGrownWithMiterJoins_MovesEveryEdgeOutIncludingTheReflexCorner()
    {
        // Vertices (0,0) (6,0) (6,2) (2,2) (2,6) (0,6): one reflex corner at (2,2).
        // Mitering moves every edge line out by 1, so the answer is the L with the same
        // reflex structure: an 8x8 square minus a 4x4 notch.
        var l = new Region2d([
            new Vector2d(0, 0), new(6, 0), new(6, 2), new(2, 2), new(2, 6), new(0, 6)]);

        var result = Region2dOffset.Offset(l, 1, OffsetJoin.Miter);

        var grown = Assert.Single(result);
        Assert.Equal(64 - 16, grown.Area, 9);
        Assert.Equal(6, grown.Outer.Count);
        Assert.True(grown.Contains(new Vector2d(2.9, 2.9)), "the reflex corner mitres out to (3,3)");
        Assert.False(grown.Contains(new Vector2d(3.1, 3.1)));
        AssertCanonical(result);
    }

    [Fact]
    public void MiterLimit_CutsSharpCornersBackToAChamfer()
    {
        // A 20:1 sliver triangle: its two sharp corners would mitre out ~10x the offset.
        var sliver = new Region2d([new Vector2d(0, 0), new(20, 0), new(20, 1)]);

        var unlimited = Region2dOffset.Offset(sliver, 0.5, OffsetJoin.Miter, miterLimit: 100);
        var limited = Region2dOffset.Offset(sliver, 0.5, OffsetJoin.Miter, miterLimit: 2);
        var chamfered = Region2dOffset.Offset(sliver, 0.5, OffsetJoin.Chamfer);

        // The 90-degree corner mitres within the limit under both; only the two sharp ones
        // are cut back, so the limited answer sits strictly between the two extremes.
        Assert.True(TotalArea(limited) < TotalArea(unlimited));
        Assert.True(TotalArea(limited) > TotalArea(chamfered));
    }

    // ---- round joins: exact against the inscribed arc, bounded against the true circle ----

    [Fact]
    public void SquareGrownWithRoundJoins_MatchesTheInscribedSteinerArea()
    {
        const double d = 2;
        const double tolerance = 1e-3;
        var result = Region2dOffset.Offset(Box(0, 0, 10, 10), d, OffsetJoin.Round, arcTolerance: tolerance);

        // Steiner: area + perimeter*d + (the four quarter-disks). Regions are polygonal, so
        // the quarter-disks are inscribed fans -- predicted exactly, not approximated.
        int segments = ArcSegments(Math.PI / 2, d, tolerance);
        double expected = 100 + 40 * d + 4 * InscribedFanArea(Math.PI / 2, d, segments);

        var grown = Assert.Single(result);
        Assert.Equal(expected, grown.Area, 9);

        // ...and the documented bound against the true Minkowski sum: inscribed arcs can
        // only fall SHORT, and by less than the sagitta times the arc length.
        double exact = 100 + 40 * d + Math.PI * d * d;
        Assert.True(grown.Area < exact, "an inscribed arc never exceeds its circle");
        Assert.True(exact - grown.Area < 2 * Math.PI * d * tolerance,
            $"flattening deficit {exact - grown.Area} exceeded the sagitta bound");
        AssertCanonical(result);
    }

    [Fact]
    public void CircleGrownOutward_ApproachesPiTimesRadiusPlusDeltaSquared()
    {
        // The task's ground truth: a circle offset by d has area pi(r+d)^2. Regions are
        // polygonal, so both the circle and its corner arcs are inscribed -- the answer
        // must sit just BELOW the analytic value, by an amount the discretization predicts.
        const double r = 5, d = 1.5, tolerance = 1e-4;
        const int sides = 256;
        var circle = InscribedPolygon(r, sides);

        var result = Region2dOffset.Offset(circle, d, OffsetJoin.Round, arcTolerance: tolerance);
        var grown = Assert.Single(result);

        // The documented bound: three inscription deficits stack up -- the N-gon's own area,
        // its shorter perimeter times d, and the flattened corner arcs. All are one-sided.
        double exact = Math.PI * (r + d) * (r + d);
        Assert.True(grown.Area < exact, "inscribed everywhere, so never above the true disk");
        Assert.True(exact - grown.Area < 1e-4 * exact,
            $"deficit {exact - grown.Area} is larger than the discretization explains");

        // Exact against the discretization: the N-gon's own area, its perimeter times d,
        // and one inscribed fan per corner (the exterior angles sum to a full turn).
        double sweep = 2 * Math.PI / sides;
        double side = 2 * r * Math.Sin(Math.PI / sides);
        double expected =
            0.5 * sides * r * r * Math.Sin(sweep)
            + sides * side * d
            + sides * InscribedFanArea(sweep, d, ArcSegments(sweep, d, tolerance));
        Assert.Equal(expected, grown.Area, 9);
        AssertCanonical(result);
    }

    // ---- holes ----

    [Fact]
    public void GrowingARegionWithAHole_ShrinksTheHole()
    {
        var plate = new Region2d(
            [new Vector2d(0, 0), new(10, 0), new(10, 10), new(0, 10)],
            [[new Vector2d(3, 3), new(3, 7), new(7, 7), new(7, 3)]]);

        var result = Region2dOffset.Offset(plate, 1, OffsetJoin.Miter);

        var grown = Assert.Single(result);
        Assert.Equal(12 * 12 - 2 * 2, grown.Area, 9);
        var hole = Assert.Single(grown.Holes);
        Assert.Equal(4, hole.Count);
        AssertCanonical(result);
    }

    [Fact]
    public void GrowingPastAHolesHalfWidth_ClosesItCompletely()
    {
        var plate = new Region2d(
            [new Vector2d(0, 0), new(10, 0), new(10, 10), new(0, 10)],
            [[new Vector2d(3, 3), new(3, 7), new(7, 7), new(7, 3)]]);

        var result = Region2dOffset.Offset(plate, 2.5, OffsetJoin.Miter);

        var grown = Assert.Single(result);
        Assert.Empty(grown.Holes);
        Assert.Equal(15 * 15, grown.Area, 9);
    }

    [Fact]
    public void GrowingTwoSeparateSquares_MergesThemWhenTheyMeet()
    {
        IReadOnlyList<Region2d> pair = [Box(0, 0, 4, 4), Box(6, 0, 10, 4)];

        var apart = Region2dOffset.Offset(pair, 0.5, OffsetJoin.Miter);
        var merged = Region2dOffset.Offset(pair, 2, OffsetJoin.Miter);

        Assert.Equal(2, apart.Count);
        Assert.Equal(2 * 5 * 5, TotalArea(apart), 9);

        var one = Assert.Single(merged);
        Assert.Equal(14 * 8, one.Area, 9);   // the two 8x8 squares overlap over x in [4,6]
        AssertCanonical(merged);
    }

    // ---- inward: erosion, splitting and vanishing ----

    [Fact]
    public void ShrinkingAConvexPolygon_IsExactWithEveryJoinStyle()
    {
        // Erosion of a CONVEX polygon has no arcs at all (the complement's corners are
        // reflex, where adjacent slabs already overlap), so the answer is the inner
        // parallel polygon exactly -- the same for round, miter and chamfer.
        foreach (var join in new[] { OffsetJoin.Round, OffsetJoin.Miter, OffsetJoin.Chamfer })
        {
            var result = Region2dOffset.Offset(Box(0, 0, 10, 10), -1.5, join);
            var shrunk = Assert.Single(result);
            Assert.Equal(7 * 7, shrunk.Area, 9);
            Assert.Equal(1.5, shrunk.Bounds.Min.X, 9);
            Assert.Equal(8.5, shrunk.Bounds.Max.X, 9);
            AssertCanonical(result);
        }
    }

    [Fact]
    public void ShrinkingARegionWithAHole_GrowsTheHole()
    {
        var plate = new Region2d(
            [new Vector2d(0, 0), new(20, 0), new(20, 20), new(0, 20)],
            [[new Vector2d(6, 6), new(6, 14), new(14, 14), new(14, 6)]]);

        var result = Region2dOffset.Offset(plate, -2, OffsetJoin.Miter);

        var shrunk = Assert.Single(result);
        Assert.Equal(16 * 16 - 12 * 12, shrunk.Area, 9);
        Assert.Single(shrunk.Holes);
        AssertCanonical(result);
    }

    [Fact]
    public void ShrinkingPastHalfTheWidth_ReturnsNothing()
    {
        // A 2-wide bar cannot survive a 1.5 erosion: the region does not become inverted,
        // it ceases to exist.
        var bar = Box(0, 0, 20, 2);

        Assert.Empty(Region2dOffset.Offset(bar, -1.5, OffsetJoin.Round));
        Assert.Empty(Region2dOffset.Offset(bar, -1.5, OffsetJoin.Miter));
    }

    [Fact]
    public void ShrinkingThroughAThinNeck_SplitsTheRegionInTwo()
    {
        // Two 8x8 pads joined by a 1-tall neck. Eroding by 0.75 eats the neck through, so
        // the answer is two regions -- the case a naive edge-offset would turn inside out.
        IReadOnlyList<Region2d> dumbbell = [Box(0, 0, 8, 8), Box(8, 3.5, 14, 4.5), Box(14, 0, 22, 8)];
        var joined = Region2dBoolean.UnionAll(dumbbell);
        Assert.Single(joined);

        var result = Region2dOffset.Offset(joined, -0.75, OffsetJoin.Round);

        Assert.Equal(2, result.Count);
        foreach (var piece in result)
        {
            // Each pad erodes to 6.5 x 6.5 plus a small bulge where the neck used to widen it.
            Assert.True(piece.Area >= 6.5 * 6.5 - 1e-9);
            Assert.True(piece.Area < 6.5 * 6.5 + 0.5);
        }
        AssertCanonical(result);
        Assert.True(result[0].Bounds.Max.X < result[1].Bounds.Min.X
                 || result[1].Bounds.Max.X < result[0].Bounds.Min.X, "the two pieces are disjoint in x");
    }

    // ---- contracts ----

    [Fact]
    public void ZeroOffset_ReturnsTheInputUnchanged()
    {
        var square = Box(0, 0, 10, 10);
        var result = Region2dOffset.Offset(square, 0);
        Assert.Same(square, Assert.Single(result));
    }

    [Fact]
    public void InvalidParameters_AreRejected()
    {
        var square = Box(0, 0, 10, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => Region2dOffset.Offset(square, 1, arcTolerance: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Region2dOffset.Offset(square, 1, miterLimit: 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Region2dOffset.Offset(square, double.NaN));
        Assert.Throws<ArgumentNullException>(() => Region2dOffset.Offset((Region2d)null!, 1));
    }

    [Fact]
    public void GrowThenShrinkByTheSameDelta_RecoversAConvexRegionExactly()
    {
        // The classic morphological round trip: closing a convex shape is a no-op. The
        // triangle's 56-degree corner mitres out 2.12x the offset, so the DEFAULT limit of 2
        // would chamfer it -- raise the limit and the round trip is exact.
        var triangle = new Region2d([new Vector2d(0, 0), new(12, 0), new(6, 9)]);

        var closed = Region2dOffset.Offset(
            Region2dOffset.Offset(triangle, 1.5, OffsetJoin.Miter, miterLimit: 100),
            -1.5, OffsetJoin.Miter, miterLimit: 100);

        var recovered = Assert.Single(closed);
        Assert.Equal(triangle.Area, recovered.Area, 6);
        Assert.Equal(3, recovered.Outer.Count);
    }

    // ---- open-path strokes ---------------------------------------------------

    [Fact]
    public void Stroke_StraightSegment_ButtCap_IsTheExactRectangle()
    {
        var stroke = Region2dOffset.Stroke(
            [new Vector2d(0, 0), new Vector2d(10, 0)], width: 2, StrokeCap.Butt);

        var region = Assert.Single(stroke);
        Assert.Equal(10 * 2, region.Area, 9);
        AssertCanonical(stroke);
    }

    [Fact]
    public void Stroke_SquareCaps_ExtendHalfTheWidth()
    {
        var stroke = Region2dOffset.Stroke(
            [new Vector2d(0, 0), new Vector2d(10, 0)], width: 2, StrokeCap.Square);

        Assert.Equal((10 + 2) * 2, TotalArea(stroke), 9);
    }

    [Fact]
    public void Stroke_RoundCaps_MakeTheInscribedCapsule()
    {
        var stroke = Region2dOffset.Stroke(
            [new Vector2d(0, 0), new Vector2d(10, 0)], width: 2, StrokeCap.Round);

        // Rectangle plus two inscribed half-discs: below the true capsule area,
        // within the fan's sagitta of it.
        double area = TotalArea(stroke);
        double exact = 10 * 2 + Math.PI;
        Assert.True(area <= exact + 1e-9, $"inscribed arcs must stay inside: {area} vs {exact}");
        Assert.True(area > exact * 0.995, $"cap fans too coarse: {area} vs {exact}");
    }

    [Fact]
    public void Stroke_RightAngleMiter_IsExact()
    {
        // Two length-10 legs (measured to the corner), width 2, miter joins: the
        // outer corner squares off, so area = 10*2 + 10*2 - (w/2)^2 + (w/2)^2 = 40.
        var stroke = Region2dOffset.Stroke(
            [new Vector2d(-10, 0), new Vector2d(0, 0), new Vector2d(0, 10)],
            width: 2, StrokeCap.Butt, OffsetJoin.Miter);

        Assert.Equal(40, TotalArea(stroke), 9);
    }

    [Fact]
    public void Stroke_DoubledBackPath_GetsTheRoundNose()
    {
        // Out and straight back: the reversal at the far end must close with a round
        // nose (both sides' half-discs), giving exactly one capsule.
        var stroke = Region2dOffset.Stroke(
            [new Vector2d(0, 0), new Vector2d(10, 0), new Vector2d(0, 0)],
            width: 2, StrokeCap.Round);

        var region = Assert.Single(stroke);
        double exact = 10 * 2 + Math.PI;
        Assert.True(region.Area <= exact + 1e-9 && region.Area > exact * 0.99,
            $"doubled-back stroke should be one capsule: {region.Area} vs {exact}");
    }

    [Fact]
    public void Stroke_SelfCrossingPath_CoversTheOverlapOnce()
    {
        // An X: two crossing strokes; the union covers the crossing once, so the area
        // is less than the two rectangles' sum.
        var stroke = Region2dOffset.Stroke(
            [new Vector2d(-5, -5), new Vector2d(5, 5), new Vector2d(-5, 5), new Vector2d(5, -5)],
            width: 1, StrokeCap.Butt, OffsetJoin.Miter);

        double area = TotalArea(stroke);
        Assert.True(area > 0);
        double segments = 2 * Math.Sqrt(200) * 1 + Math.Sqrt(200);   // three leg rectangles
        Assert.True(area < segments, $"overlap must not double-count: {area} vs {segments}");
        AssertCanonical(stroke);
    }

    [Fact]
    public void Stroke_ClosedCircuit_EnclosesAHole()
    {
        // Stroking a closed square circuit (first point repeated) leaves the middle
        // empty: one region with one hole.
        var stroke = Region2dOffset.Stroke(
            [
                new Vector2d(0, 0), new Vector2d(10, 0), new Vector2d(10, 10),
                new Vector2d(0, 10), new Vector2d(0, 0),
            ],
            width: 2, StrokeCap.Round, OffsetJoin.Miter);

        var region = Assert.Single(stroke);
        Assert.Single(region.Holes);
        // Outer 12x12 minus inner 8x8 hole, with round corner sagitta slack... the
        // three round joins are inscribed; the start/end corner is closed by the two
        // round caps. Outer bound: 12*12 - 8*8 = 80.
        Assert.True(region.Area is > 78 and <= 80.0 + 1e-9, $"circuit area {region.Area}");
    }

    [Fact]
    public void Stroke_ValidatesItsArguments()
    {
        Assert.Throws<ArgumentNullException>(() => Region2dOffset.Stroke(null!, 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Region2dOffset.Stroke([new Vector2d(0, 0), new Vector2d(1, 0)], 0));
        Assert.Throws<ArgumentException>(
            () => Region2dOffset.Stroke([new Vector2d(1, 1), new Vector2d(1, 1)], 1));
    }

    // ---- turn handedness ----

    /// <summary>
    /// A stroke's area cannot depend on which way the path turns: mirroring a path maps
    /// every left turn to a right turn and is an isometry, so the two footprints are
    /// congruent. That is the assertion a same-handed fixture structurally cannot make,
    /// and it is what caught the right side's corner joins being dropped —
    /// <c>Cross(-a, -b) == Cross(a, b)</c> exactly, so negating both normals did not flip
    /// the turn and the gate admitted both wedges or refused both. The deficit was
    /// exactly (clockwise corners) x w^2/4, invisible to every left-turning test.
    /// </summary>
    [Theory]
    [InlineData(OffsetJoin.Miter)]
    [InlineData(OffsetJoin.Round)]
    [InlineData(OffsetJoin.Chamfer)]
    public void Stroke_AreaIsIndependentOfTurnHandedness(OffsetJoin join)
    {
        // A U with two LEFT turns, and its mirror in x, which has two RIGHT turns.
        Vector2d[] left = [new(0, 0), new(10, 0), new(10, 8), new(0, 8)];
        var right = left.Select(p => new Vector2d(-p.X, p.Y)).ToArray();

        const double width = 2.0;
        double leftArea = TotalArea(Region2dOffset.Stroke(left, width, StrokeCap.Butt, join));
        double rightArea = TotalArea(Region2dOffset.Stroke(right, width, StrokeCap.Butt, join));

        Assert.Equal(leftArea, rightArea, 9);

        // And the fills are genuinely PRESENT rather than both missing, which is the way
        // the defect passed a same-handed test. The three slabs total 56 and overlap in a
        // half-width square at each of the two corners, so the bare union is 54; a miter
        // adds the full (w/2)^2 corner square at each one.
        double bareSlabs = (10 + 8 + 10) * width - 2 * (width / 2) * (width / 2);
        Assert.Equal(54.0, bareSlabs, 9);
        if (join == OffsetJoin.Miter)
            Assert.Equal(bareSlabs + 2 * (width / 2) * (width / 2), leftArea, 9);
        else
            Assert.True(leftArea > bareSlabs,
                $"{join} corner fills are missing: {leftArea} is the bare slab union");
    }
}
