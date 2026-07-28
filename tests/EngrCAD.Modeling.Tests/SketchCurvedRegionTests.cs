using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The consumer end of the exact curved 2D tier: a sketch boolean whose arcs survive, and
/// the solid built from it.
///
/// <para>The payoff test is <see cref="ExtrudedSketchBoolean_IsExactWhereThePolygonalRouteIsNot"/>:
/// the same design routed through <c>ToRegions</c> and through <c>ToCurvedRegions</c>
/// differs by the whole flattening error, and only the curved route lands on the closed
/// form.</para>
/// </summary>
public class SketchCurvedRegionTests
{
    [Fact]
    public void CircleSketch_HasExactlyItsAnalyticAreaAsACurvedRegion()
    {
        var region = Assert.Single(Sketch.Circle(5).ToCurvedRegions());
        Assert.Equal(Math.PI * 25, region.Area, 12);
        var edge = Assert.Single(region.Outer);
        Assert.True(edge.IsFullCircle);
        // The polygonal route is measurably short, which is the whole reason for the tier.
        double flattened = Sketch.Circle(5).ToRegions().Single().Area;
        Assert.True(flattened < region.Area - 1e-4, $"flattened {flattened} vs exact {region.Area}");
    }

    [Fact]
    public void SlotSketch_KeepsItsTwoSemicircularEnds()
    {
        var region = Assert.Single(Sketch.Slot(20, 6).ToCurvedRegions());
        Assert.Equal(14 * 6 + Math.PI * 9, region.Area, 12);
        Assert.Equal(2, region.Outer.Count(e => e.IsArc));
    }

    [Fact]
    public void SketchBoolean_KeepsArcsAndLandsOnTheClosedForm()
    {
        var plate = Sketch.Rectangle(40, 20);
        var bore = Sketch.Circle(new Vector2d(0, 0), 6);
        var region = Assert.Single(plate.SubtractExact(bore));
        Assert.Equal(800 - Math.PI * 36, region.Area, 11);
        var hole = Assert.Single(region.Holes);
        Assert.All(hole, edge => Assert.True(edge.IsArc));
    }

    [Fact]
    public void SketchUnion_OfAPlateAndABoss_MergesTheOverlapExactly()
    {
        var plate = Sketch.Rectangle(20, 10);
        var boss = Sketch.Circle(new Vector2d(10, 0), 4);
        var region = Assert.Single(plate.UnionExact(boss));
        // Half the disc pokes out of the plate's right edge, so the union is the plate
        // plus one exact half-disc.
        Assert.Equal(200 + Math.PI * 16 / 2, region.Area, 10);
    }

    [Fact]
    public void FromCurvedRegion_RoundTripsASketchExactly()
    {
        var slot = Sketch.Slot(20, 6);
        var round = Sketch.FromCurvedRegion(slot.ToCurvedRegions().Single());
        Assert.Equal(slot.Area(), round.Area(), 12);
        Assert.Equal(slot.ToCurves().Count, round.ToCurves().Count);
    }

    [Fact]
    public void FromCurvedRegion_CarriesHolesBack()
    {
        var plate = Sketch.Rectangle(40, 20);
        var bore = Sketch.Circle(new Vector2d(0, 0), 6);
        var sketch = Sketch.FromCurvedRegion(plate.SubtractExact(bore).Single());
        Assert.Single(sketch.Holes);
        Assert.Equal(800 - Math.PI * 36, sketch.Area(), 11);
    }

    [Fact]
    public void ExtrudedSketchBoolean_IsExactWhereThePolygonalRouteIsNot()
    {
        const double height = 5;
        var plate = Sketch.Rectangle(40, 20);
        var bore = Sketch.Circle(new Vector2d(0, 0), 6);
        double exact = (800 - Math.PI * 36) * height;

        var curved = Sketch.FromCurvedRegion(plate.SubtractExact(bore).Single());
        var curvedSolid = Shape.Extrude(curved, height).ToBrep();
        double curvedVolume = BrepMassProperties.Compute(curvedSolid).Volume;

        var flattened = plate.Subtract(bore).Single();
        var (outer, holes) = EngrCAD.BRep.Profile.FromRegion(flattened);
        var flatSolid = EngrCAD.BRep.SolidFactory.Extrude(outer, (0, 0, height), holes);
        double flatVolume = BrepMassProperties.Compute(flatSolid).Volume;

        // The curved route is exact to the mass-property integrator's own accuracy...
        Assert.Equal(exact, curvedVolume, Math.Abs(exact) * 1e-6);
        // ...while the flattened one carries the default 1e-3 chord budget as a volume
        // error (measured ~3.6e-5 relative here, two decades above the curved route).
        double flatError = Math.Abs(flatVolume - exact) / exact;
        double curvedError = Math.Abs(curvedVolume - exact) / exact;
        Assert.True(flatError > 1e-5, $"expected the flattened route to be measurably off, got {flatError}");
        Assert.True(curvedError < flatError / 10, $"curved {curvedError} vs flattened {flatError}");
    }

    [Fact]
    public void ExtrudedCurvedRegion_ThroughProfileFromCurvedRegion_IsAnalytic()
    {
        // The B-Rep-direct route: the curved region becomes exact profiles with no sketch
        // in between, and the solid's volume is the closed form.
        var region = Assert.Single(
            CurvedRegion2dBoolean.Difference(
                CurvedRegion2d.Disc((0, 0), 10),
                CurvedRegion2d.Disc((0, 0), 4)));
        var (outer, holes) = EngrCAD.BRep.Profile.FromCurvedRegion(region);
        var solid = EngrCAD.BRep.SolidFactory.Extrude(outer, (0, 0, 3), holes);
        Assert.Equal((Math.PI * 100 - Math.PI * 16) * 3, BrepMassProperties.Compute(solid).Volume, 1e-3);
        // A whole circle came through as ONE closed curve, not a chain.
        Assert.True(outer.IsSingleClosedCurve);
    }

    [Fact]
    public void OffsetExact_RoundsAPlateWithTrueArcs()
    {
        var grown = Assert.Single(Sketch.Rectangle(20, 10).OffsetExact(2));
        Assert.Equal(200 + 2 * 2 * 30 + Math.PI * 4, grown.Area, 10);
        Assert.Equal(4, grown.Outer.Count(e => e.IsArc));
        // The polygonal offset must inscribe, so it comes out short.
        double flattened = Sketch.Rectangle(20, 10).Offset(2).Sum(r => r.Area);
        Assert.True(flattened < grown.Area - 1e-6);
    }

    [Fact]
    public void BezierSketches_AreFlattenedAndSaySo()
    {
        // A Bezier still crosses as chords (the documented gap in the tier), so this must
        // work without throwing and stay within the chord tolerance of the exact area.
        var petal = Sketch.Start(0, 0)
            .BezierTo(new Vector2d(10, 8), new Vector2d(0, 8), new Vector2d(10, 0))
            .LineTo(new Vector2d(0, 0))
            .Close();
        var region = Assert.Single(petal.ToCurvedRegions(1e-4));
        Assert.True(Math.Abs(petal.Area() - region.Area) < 5e-3,
            $"flattening lost {petal.Area() - region.Area}");
        Assert.All(region.Outer, edge => Assert.False(edge.IsArc));
    }

    [Fact]
    public void MultipleSketches_NestByContainmentIntoCurvedRegions()
    {
        var regions = Sketch.ToCurvedRegions([
            Sketch.Rectangle(40, 20),
            Sketch.Circle(new Vector2d(-10, 0), 3),
            Sketch.Circle(new Vector2d(10, 0), 3),
        ]);
        var region = Assert.Single(regions);
        Assert.Equal(2, region.Holes.Count);
        Assert.Equal(800 - 2 * Math.PI * 9, region.Area, 11);
    }
}
