using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Sketch → <see cref="Region2d"/> → 2D booleans → <see cref="Profile"/> → solid: the
/// sketch engine's front door. Curved input is flattened to a chord tolerance on the way in,
/// so every curved assertion here derives its tolerance from that discretization.
/// </summary>
public class SketchRegionBooleanTests
{
    private static double TotalArea(IReadOnlyList<Region2d> regions) => regions.Sum(r => r.Area);

    // ---- flattening fidelity ----

    [Fact]
    public void Circle_FlattensInsideTheChordTolerance()
    {
        const double radius = 5;
        const double tolerance = 1e-3;
        var region = Assert.Single(Sketch.Circle(radius).ToRegions(tolerance));

        // The polyline is inscribed and no chord bulges more than the tolerance inward, so
        // the polygon lies in the annulus [r − tol, r] — that brackets its area exactly.
        Assert.InRange(region.Area, Math.PI * (radius - tolerance) * (radius - tolerance), Math.PI * radius * radius);

        // Tightening the tolerance must move the area toward the true circle, not away.
        var finer = Assert.Single(Sketch.Circle(radius).ToRegions(1e-5));
        Assert.True(finer.Area > region.Area);
        Assert.True(Math.PI * radius * radius - finer.Area < Math.PI * radius * radius - region.Area);
    }

    [Fact]
    public void BezierAndArcSegments_Flatten_LinesStayExact()
    {
        // The parabolic cap sketch from SketchTests: exact area 2/3 of the unit chord.
        var parabolic = Sketch.Start(0, 0).QuadraticTo(new(0.5, 2), new(1, 0)).Close();
        var region = Assert.Single(parabolic.ToRegions(1e-6));
        Assert.Equal(2.0 / 3.0, region.Area, 5);

        // A pure-line sketch flattens with zero loss and keeps its exact vertex count.
        var triangle = Assert.Single(Sketch.Polygon([new(0, 0), new(4, 0), new(0, 3)]).ToRegions());
        Assert.Equal(3, triangle.Outer.Count);
        Assert.Equal(6.0, triangle.Area, 12);
    }

    // ---- automatic hole detection ----

    [Fact]
    public void ToRegions_CarriesDeclaredHolesThrough()
    {
        var washer = Sketch.Rectangle(10, 10).WithHole(Sketch.Rectangle(4, 4));
        var region = Assert.Single(washer.ToRegions());

        Assert.Single(region.Holes);
        Assert.Equal(100.0 - 16.0, region.Area, 12);
        Assert.True(region.Contains(new Vector2d(4.5, 0)));
        Assert.False(region.Contains(new Vector2d(0, 0)));
    }

    [Fact]
    public void ToRegions_OverSeparateSketches_DetectsNestingWithoutWithHole()
    {
        // No WithHole anywhere: the plate and its three bolt holes are just four loops.
        var plate = Sketch.Rectangle(20, 10);
        var holes = new[]
        {
            Sketch.Circle(new Vector2d(-6, 0), 1),
            Sketch.Circle(new Vector2d(0, 0), 1),
            Sketch.Circle(new Vector2d(6, 0), 1),
        };
        var regions = Sketch.ToRegions([plate, .. holes], 1e-5);

        var region = Assert.Single(regions);
        Assert.Equal(3, region.Holes.Count);
        Assert.Equal(200 - 3 * Math.PI, region.Area, 3);
        Assert.False(region.Contains(new Vector2d(6, 0)));
        Assert.True(region.Contains(new Vector2d(3, 0)));
    }

    [Fact]
    public void ToRegions_OverSeparateSketches_SplitsDisjointOutlines()
    {
        var regions = Sketch.ToRegions([
            Sketch.Rectangle(2, 2),
            Sketch.Polygon([new(10, 10), new(13, 10), new(13, 13), new(10, 13)]),
        ]);

        Assert.Equal(2, regions.Count);
        Assert.Equal(4.0 + 9.0, TotalArea(regions), 12);
    }

    // ---- sketch booleans ----

    [Fact]
    public void Subtract_CutsAPocketThatBecomesAHole()
    {
        var plate = Sketch.Rectangle(20, 10);
        var pocket = Sketch.Rectangle(6, 4);

        var region = Assert.Single(plate.Subtract(pocket));
        Assert.Equal(200.0 - 24.0, region.Area, 12);
        Assert.Single(region.Holes);
    }

    [Fact]
    public void Union_MergesOverlappingOutlinesIntoOne()
    {
        var a = Sketch.Rectangle(10, 4);                                 // x ∈ [−5, 5]
        var b = Sketch.Polygon([new(3, -8), new(7, -8), new(7, 8), new(3, 8)]);

        var union = Assert.Single(a.Union(b));
        Assert.Equal(40 + 4 * 16 - 2 * 4, union.Area, 12);               // overlap 2 wide, 4 tall
        Assert.Empty(union.Holes);
    }

    [Fact]
    public void Intersect_AndDifference_AgreeWithSetTheory()
    {
        var a = Sketch.Rectangle(10, 10);
        var b = Sketch.Polygon([new(0, 0), new(12, 0), new(12, 12), new(0, 12)]);

        double intersection = TotalArea(a.Intersect(b));
        Assert.Equal(25.0, intersection, 12);                            // the +x/+y quadrant
        Assert.Equal(100.0 - 25.0, TotalArea(a.Subtract(b)), 12);
        Assert.Equal(100.0 + 144.0 - 25.0, TotalArea(a.Union(b)), 12);
    }

    [Fact]
    public void Subtract_CanSplitASketchIntoSeveralRegions()
    {
        var plate = Sketch.Rectangle(20, 4);
        var bar = Sketch.Rectangle(2, 10);

        var pieces = plate.Subtract(bar).OrderBy(r => r.Bounds.Min.X).ToList();
        Assert.Equal(2, pieces.Count);
        Assert.Equal(36.0, pieces[0].Area, 12);
        Assert.Equal(36.0, pieces[1].Area, 12);
    }

    [Fact]
    public void SketchBooleans_ValidateTheirArguments()
    {
        var plate = Sketch.Rectangle(2, 2);
        Assert.Throws<ArgumentNullException>(() => plate.Union(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => plate.ToRegions(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => plate.ToRegions(-1));
    }

    // ---- the exact-SDF path must not regress ----

    [Fact]
    public void RegionWork_LeavesTheExactSketchSdfAlone()
    {
        // Canary: ToRegion() (exact signed distance) and ToRegions() (flattened polygons)
        // are different products of the same sketch, and the exact one is what makes sketch
        // extrusions implicit-Native.
        var sketch = Sketch.Rectangle(4, 2).WithHole(Sketch.Circle(new(0, 0), 0.5));
        IPlanarRegion exact = sketch.ToRegion();

        Assert.Equal(0.5, exact.SignedDistance(new(0, 0)), 12);
        Assert.Equal(-0.5, exact.SignedDistance(new(1.5, 0)), 12);
        Assert.Equal(Math.Sqrt(2), exact.SignedDistance(new(3, 2)), 12);

        // And a sketch extrusion is still implicit-Native (no bridging through a mesh).
        var report = Shape.Extrude(sketch, 1).Explain(TargetRep.Implicit);
        Assert.True(report.IsConvertible);
        Assert.All(report.Entries, entry => Assert.Equal(NodeSupport.Native, entry.Support));
    }

    // ---- end to end: regions feed the solid factories ----

    [Fact]
    public void BooleanRegion_ExtrudesToAValidSolidWithTheRightVolume()
    {
        // Plate minus a centred pocket → a ring region whose hole was CREATED by the
        // boolean, extruded 6 mm.
        var region = Assert.Single(Sketch.Rectangle(20, 10).Subtract(Sketch.Rectangle(6, 4)));
        var (outer, holes) = Profile.FromRegion(region, SketchPlane.XY.Frame);
        var shape = Shape.Extrude(outer, Vector3d.UnitZ * 6, holes);

        var solid = shape.ToBrep();
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 1));

        // Every face is planar, so tessellation is exact and the volume is the analytic one.
        var mesh = BRepTessellator.Tessellate(solid);
        Assert.True(mesh.IsClosed);
        Assert.Equal((200.0 - 24.0) * 6, mesh.Volume(), 9);
    }

    [Fact]
    public void MultipleAutoDetectedHoles_ExtrudeToAGenus3Solid()
    {
        var regions = Sketch.ToRegions([
            Sketch.Rectangle(20, 10),
            Sketch.Rectangle(2, 2),
            Sketch.Polygon([new(-7, -1), new(-5, -1), new(-5, 1), new(-7, 1)]),
            Sketch.Polygon([new(5, -1), new(7, -1), new(7, 1), new(5, 1)]),
        ]);
        var region = Assert.Single(regions);
        Assert.Equal(3, region.Holes.Count);

        var (outer, holes) = Profile.FromRegion(region);
        var solid = SolidFactory.Extrude(outer, Vector3d.UnitZ * 3, holes);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 3));
        Assert.Equal((200.0 - 3 * 4) * 3, BRepTessellator.Tessellate(solid).Volume(), 9);
    }

    // ---- offsetting ----

    [Fact]
    public void OffsettingARectangleOutward_IsExactUnderMiterJoins()
    {
        // Straight edges and 90-degree corners: no flattening enters at all.
        var grown = Assert.Single(Sketch.Rectangle(20, 10).Offset(1.5, OffsetJoin.Miter));

        Assert.Equal(23.0 * 13.0, grown.Area, 9);
        Assert.Equal(4, grown.Outer.Count);
    }

    [Fact]
    public void OffsettingACircleOutward_LandsJustInsidePiTimesRadiusPlusDeltaSquared()
    {
        const double radius = 5, delta = 1.5, tolerance = 1e-3;
        var grown = Assert.Single(Sketch.Circle(radius).Offset(delta, OffsetJoin.Round, chordTolerance: tolerance));

        // Both the circle and the corner arcs are inscribed, so the answer brackets exactly:
        // above the disk of radius (r + delta − tolerance), below the true offset disk.
        double exact = Math.PI * (radius + delta) * (radius + delta);
        Assert.InRange(grown.Area,
            Math.PI * (radius + delta - tolerance) * (radius + delta - tolerance), exact);
    }

    [Fact]
    public void OffsettingAPlateWithBoltHolesInward_GrowsEveryHole()
    {
        // The shell/clearance case: one outer boundary, four holes, all offset at once.
        var plate = Sketch.Rectangle(40, 20)
            .WithHole(Sketch.Circle(new Vector2d(-12, -5), 2))
            .WithHole(Sketch.Circle(new Vector2d(12, -5), 2))
            .WithHole(Sketch.Circle(new Vector2d(-12, 5), 2))
            .WithHole(Sketch.Circle(new Vector2d(12, 5), 2));

        var shrunk = Assert.Single(plate.Offset(-1, OffsetJoin.Miter));

        Assert.Equal(4, shrunk.Holes.Count);
        // Boundary 38x18 exactly (straight, mitred); each hole grows from r=2 to r=3, and
        // both circles are inscribed polygons, so the hole area is bracketed by the chord
        // tolerance rather than exact.
        Assert.InRange(shrunk.Area,
            38 * 18 - 4 * Math.PI * 3 * 3, 38 * 18 - 4 * Math.PI * (3 - 1e-3) * (3 - 1e-3));
    }

    [Fact]
    public void AnOffsetRegionExtrudesToASolid()
    {
        // The point of offsetting: the result is ordinary region input for the factories.
        var gasket = Sketch.RoundedRectangle(30, 16, 3).Offset(2, OffsetJoin.Round)[0];
        var (outer, holes) = Profile.FromRegion(gasket, SketchPlane.XY.Frame);

        Assert.Empty(holes);
        var solid = SolidFactory.Extrude(outer, Vector3d.UnitZ * 4);
        solid.Validate();
        Assert.Equal(gasket.Area * 4, BRepTessellator.Tessellate(solid).Volume(), 6);
    }

    [Fact]
    public void OffsettingARibInwardPastItsHalfWidth_LeavesNothing()
    {
        Assert.Empty(Sketch.Rectangle(40, 2).Offset(-1.5));
    }

    [Fact]
    public void RegionsPlaceOntoAnyPlane()
    {
        var region = Assert.Single(Sketch.Rectangle(4, 2).ToRegions());
        var (outer, holes) = Profile.FromRegion(region, SketchPlane.XZ.Frame);

        Assert.Empty(holes);
        // The XZ sketch plane has Y = world Z, so the profile lives in the world XZ plane.
        Assert.True(outer.Normal.IsParallelTo(Vector3d.UnitY, Tolerance.Default));
        Assert.All(outer.Segments, s => Assert.Equal(0, s.PointAt(s.Domain.Start).Y, 12));

        var solid = SolidFactory.Extrude(outer, Vector3d.UnitY * 5);
        solid.Validate();
        Assert.Equal(4 * 2 * 5, BRepTessellator.Tessellate(solid).Volume(), 9);
    }
}
