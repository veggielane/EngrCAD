using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// The exact curved offset. The polygonal <see cref="Region2dOffset"/> can only inscribe a
/// round join, so its answers are always short of the truth by a sagitta; here every
/// assertion is an equality against the closed form, which is the whole point of the tier.
/// </summary>
public class CurvedRegion2dOffsetTests
{
    private static CurvedRegion2d Rectangle(double x0, double y0, double x1, double y1) =>
        new([
            CurvedEdge2d.Line((x0, y0), (x1, y0)),
            CurvedEdge2d.Line((x1, y0), (x1, y1)),
            CurvedEdge2d.Line((x1, y1), (x0, y1)),
            CurvedEdge2d.Line((x0, y1), (x0, y0)),
        ]);

    [Fact]
    public void DiscGrown_IsExactlyTheLargerDisc()
    {
        var grown = CurvedRegion2dOffset.Offset(CurvedRegion2d.Disc((0, 0), 5), 2);
        var region = Assert.Single(grown);
        Assert.Equal(Math.PI * 49, region.Area, 9);
        var edge = Assert.Single(region.Outer);
        Assert.True(edge.IsFullCircle);
        Assert.Equal(7, edge.Radius, 12);
    }

    [Fact]
    public void DiscShrunk_IsExactlyTheSmallerDisc()
    {
        var shrunk = CurvedRegion2dOffset.Offset(CurvedRegion2d.Disc((0, 0), 5), -2);
        var region = Assert.Single(shrunk);
        Assert.Equal(Math.PI * 9, region.Area, 8);
    }

    [Fact]
    public void DiscShrunkAway_ReturnsNothing()
    {
        Assert.Empty(CurvedRegion2dOffset.Offset(CurvedRegion2d.Disc((0, 0), 2), -3));
    }

    [Fact]
    public void RectangleRoundOffset_HasTheExactMinkowskiArea()
    {
        // w*h + 2d(w + h) + pi d^2 - the disc's whole area at the corners, not an
        // inscribed fan's.
        var grown = CurvedRegion2dOffset.Offset(Rectangle(0, 0, 20, 10), 3);
        var region = Assert.Single(grown);
        Assert.Equal(200 + 2 * 3 * 30 + Math.PI * 9, region.Area, 9);
        // Four straight sides and four exact quarter-circle corners.
        Assert.Equal(8, region.Outer.Count);
        Assert.Equal(4, region.Outer.Count(e => e.IsArc));
        Assert.All(region.Outer.Where(e => e.IsArc), e => Assert.Equal(3, e.Radius, 12));
        Assert.All(region.Outer.Where(e => e.IsArc), e => Assert.Equal(Math.PI / 2, Math.Abs(e.SweepAngle), 9));
    }

    [Fact]
    public void RoundJoinsAreExact_NotInscribed()
    {
        // The polygonal path inscribes its round joins, so it must come out SHORT; the
        // curved path lands on the closed form.
        var curved = CurvedRegion2dOffset.Offset(Rectangle(0, 0, 20, 10), 3);
        var polygonal = Region2dOffset.Offset(
            new Region2d([(0, 0), (20, 0), (20, 10), (0, 10)]), 3, OffsetJoin.Round, arcTolerance: 1e-3);
        double exact = 200 + 2 * 3 * 30 + Math.PI * 9;
        Assert.Equal(exact, curved.Sum(r => r.Area), 9);
        Assert.True(polygonal.Sum(r => r.Area) < exact - 1e-6,
            "the polygonal offset should be measurably short of the exact one");
    }

    [Fact]
    public void RectangleMiterOffset_IsTheGrownRectangle()
    {
        var grown = CurvedRegion2dOffset.Offset(Rectangle(0, 0, 20, 10), 3, OffsetJoin.Miter);
        var region = Assert.Single(grown);
        Assert.Equal(26 * 16, region.Area, 9);
        // A mitered rectangle stays a rectangle: the collinear T-junctions merge away.
        Assert.Equal(4, region.Outer.Count);
    }

    [Fact]
    public void RectangleChamferOffset_CutsEachCorner()
    {
        var grown = CurvedRegion2dOffset.Offset(Rectangle(0, 0, 20, 10), 3, OffsetJoin.Chamfer);
        var region = Assert.Single(grown);
        // The mitered area less the four corner triangles of legs d.
        Assert.Equal(26 * 16 - 4 * 0.5 * 9, region.Area, 9);
        Assert.Equal(8, region.Outer.Count);
    }

    [Fact]
    public void PlateWithABore_GrowsOutwardAndShrinksItsHole()
    {
        var plate = CurvedRegion2dBoolean
            .Difference(Rectangle(-10, -10, 10, 10), CurvedRegion2d.Disc((0, 0), 4))
            .Single();
        var grown = CurvedRegion2dOffset.Offset(plate, 1);
        var region = Assert.Single(grown);
        double expected = 400 + 2 * 1 * 40 + Math.PI * 1 - Math.PI * 9;
        Assert.Equal(expected, region.Area, 9);
        var hole = Assert.Single(region.Holes);
        Assert.Equal(Math.PI * 9, Math.Abs(CurvedRegion2d.SignedArea(hole)), 9);
    }

    [Fact]
    public void ARoundedSlot_KeepsItsExactArcsThroughAnOffset()
    {
        // A stadium: two straight flanks tangent to two semicircular ends. The joints are
        // tangent-continuous, so no corner join is raised at all.
        var slot = Stadium(10, 3);
        Assert.Equal(60 + Math.PI * 9, slot.Area, 10);
        var grown = CurvedRegion2dOffset.Offset(slot, 2);
        var region = Assert.Single(grown);
        // The straight length is unchanged; only the end radius grows.
        Assert.Equal(10 * 10 + Math.PI * 25, region.Area, 9);
        Assert.Equal(4, region.Outer.Count);
        Assert.Equal(2, region.Outer.Count(e => e.IsArc));
        Assert.All(region.Outer.Where(e => e.IsArc), e => Assert.Equal(5, e.Radius, 12));
    }

    [Theory]
    [InlineData(1e-3)]
    [InlineData(1.0)]
    [InlineData(1e3)]
    public void CurvedOffsets_AreScaleFree(double scale)
    {
        var grown = CurvedRegion2dOffset.Offset(Rectangle(0, 0, 2 * scale, scale), 0.3 * scale);
        double expected = 2 * scale * scale
            + 2 * 0.3 * scale * 3 * scale
            + Math.PI * 0.09 * scale * scale;
        Assert.Equal(expected, grown.Sum(r => r.Area), Math.Abs(expected) * 1e-9);
    }

    [Fact]
    public void ZeroOffset_ReturnsTheInputUntouched()
    {
        var disc = CurvedRegion2d.Disc((1, 2), 3);
        var same = Assert.Single(CurvedRegion2dOffset.Offset(disc, 0));
        Assert.Same(disc, same);
    }

    /// <summary>A stadium of straight length <paramref name="length"/> and radius
    /// <paramref name="radius"/>, centred on the origin and running along x.</summary>
    private static CurvedRegion2d Stadium(double length, double radius) =>
        new([
            CurvedEdge2d.Line((-length / 2, -radius), (length / 2, -radius)),
            CurvedEdge2d.Arc((length / 2, 0), radius, -Math.PI / 2, Math.PI),
            CurvedEdge2d.Line((length / 2, radius), (-length / 2, radius)),
            CurvedEdge2d.Arc((-length / 2, 0), radius, Math.PI / 2, Math.PI),
        ]);
}
