using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Holes drilled by SEPARATE <see cref="Shape.Drill"/> calls have to clear each other for
/// exactly the reason holes within one call do: two tools whose surface circles touch or
/// cross make degenerate boolean input, which fails deep inside tessellation rather than
/// at the call site. Mixing clearance holes and counterbores in two calls is the normal
/// way to build a plate, so the single-call check was only half the guard.
/// </summary>
public class CrossDrillValidationTests
{
    private static Shape Plate() => Shape.Box(new Aabb((0, 0, 0), (60, 40, 10)));
    private static SketchPlane Top() => SketchPlane.At((0, 0, 10), Vector3d.UnitX, Vector3d.UnitY);

    /// <summary>Explicit specs so the surface diameters are known here, not read from internals.</summary>
    private static HoleSpec Bore(double diameter) => HoleSpec.Simple(diameter);

    private static HoleSpec Cbore(double diameter, double recess) =>
        HoleSpec.Counterbore(diameter, recess, 4);

    [Fact]
    public void OverlappingHolesFromTwoDrillCallsAreRejected()
    {
        var first = Plate().Drill(Bore(6), [new Vector2d(20, 20)], 14, Top());

        // An 11 mm counterbore recess 5 mm away swallows the 6 mm bore: the surface
        // circles cross, so the two tools would meet on the drilled plane.
        var error = Assert.Throws<ArgumentException>(() =>
            first.Drill(Cbore(6, 11), [new Vector2d(25, 20)], 14, Top()));

        Assert.Contains("earlier Drill call", error.Message);
        Assert.Contains("(25, 20)", error.Message);
        Assert.Contains("(20, 20)", error.Message);
    }

    [Fact]
    public void TangentHolesFromTwoDrillCallsAreRejected()
    {
        // Exactly tangent: the centre distance equals the mean of the two surface
        // diameters, (6 + 10)/2 = 8.
        var first = Plate().Drill(Bore(6), [new Vector2d(20, 20)], 14, Top());
        Assert.Throws<ArgumentException>(() =>
            first.Drill(Bore(10), [new Vector2d(28, 20)], 14, Top()));
    }

    [Fact]
    public void ClearHolesFromTwoDrillCallsAreAccepted()
    {
        var design = Plate()
            .Drill(Bore(6), [new Vector2d(12, 12), new Vector2d(48, 12)], 14, Top())
            .Drill(Bore(4), [new Vector2d(30, 28)], 14, Top());

        var solid = design.ToBrep();
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 3));
    }

    /// <summary>The plate's underside, looking down (its own 2D y runs opposite the top's).</summary>
    private static SketchPlane Bottom() => SketchPlane.At((0, 0, 0), Vector3d.UnitX, -Vector3d.UnitY);

    [Fact]
    public void OpposingCoaxialBoresThatMeetAreRejected()
    {
        // This exact layout used to be accepted, under a comment claiming opposing bores
        // "are not compared". They are the same bore from both sides: the top tool reaches
        // z = 4 and the bottom tool z = 6, so they overlap over 2 mm of coaxial material
        // and the boolean sees two tools sharing a volume.
        var first = Plate().Drill(Bore(6), [new Vector2d(20, 20)], 6, Top());

        var error = Assert.Throws<ArgumentException>(() =>
            first.Drill(Bore(6), [new Vector2d(20, -20)], 6, Bottom()));

        Assert.Contains("different plane", error.Message);
        Assert.Contains("(20, -20)", error.Message);
        Assert.Contains("(20, 20)", error.Message);
    }

    [Fact]
    public void OpposingCoaxialBoresThatStopShortAreAccepted()
    {
        // The same two bores, each 4 mm into a 10 mm plate: 2 mm of web between them, so
        // the tools clear (their overshoot is 0.05 x 6 = 0.3 mm each, nowhere near it).
        var design = Plate()
            .Drill(Bore(6), [new Vector2d(20, 20)], 4, Top())
            .Drill(Bore(6), [new Vector2d(20, -20)], 4, Bottom());

        var solid = design.ToBrep();
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0)); // two blind pockets, no through hole
    }

    [Fact]
    public void OffsetOpposingBoresAreAccepted()
    {
        // Full-depth from both sides but far apart in plan: the cheap pre-test settles
        // this one on a single segment-segment distance.
        var design = Plate()
            .Drill(Bore(6), [new Vector2d(15, 20)], 12, Top())
            .Drill(Bore(6), [new Vector2d(45, -20)], 12, Bottom());

        design.ToBrep().Validate();
    }

    [Fact]
    public void ACrossBoreMeetingATopBoreIsRejected()
    {
        // Perpendicular axes, the case a 2D centre-distance test cannot see at all: a
        // side bore driven through the plate's width passes straight through a top bore.
        var side = SketchPlane.At((0, 0, 0), Vector3d.UnitX, Vector3d.UnitZ); // normal −Y
        var first = Plate().Drill(Bore(6), [new Vector2d(20, 20)], 12, Top());

        var error = Assert.Throws<ArgumentException>(() =>
            first.Drill(Bore(4), [new Vector2d(20, 5)], 50, side));
        Assert.Contains("different plane", error.Message);
    }

    [Fact]
    public void ACrossBoreClearingATopBoreIsAccepted()
    {
        // The same cross bore moved 20 mm along the plate: perpendicular axes that never
        // come within their summed radii.
        var side = SketchPlane.At((0, 0, 0), Vector3d.UnitX, Vector3d.UnitZ);
        var design = Plate()
            .Drill(Bore(6), [new Vector2d(20, 20)], 12, Top())
            .Drill(Bore(4), [new Vector2d(45, 5)], 50, side);

        Assert.NotNull(design.ToBrep());
    }

    [Fact]
    public void ACountersinkConeMeetingAnOpposingBoreIsRejected()
    {
        // The one tool whose radius genuinely varies along its axis, so the whole-tool
        // pre-test is ambiguous and the slab refinement decides it. The cone opens to
        // 14 mm at the top face; a 5 mm bore from below reaching up into that flare must
        // be caught even though the two AXES are 5 mm apart.
        var first = Plate().Drill(HoleSpec.Countersink(6, 14), [new Vector2d(20, 20)], 9, Top());

        Assert.Throws<ArgumentException>(() =>
            first.Drill(Bore(5), [new Vector2d(25, -20)], 9.5, Bottom()));
    }

    [Fact]
    public void NestedDrillsLowerTheBodyOnce()
    {
        // A drill's expansion is `((child − tool₀) − tool₁) …`, so lowering the expansion
        // whole would lower the child a SECOND time on top of the lowering the
        // coplanarity validation already needed. The observable contract is just that the
        // result stays right; the cost is measured separately (1.77x on a 6+2-hole plate).
        var design = Plate()
            .Drill(Bore(6), [new Vector2d(12, 12), new Vector2d(48, 12), new Vector2d(12, 28)], 14, Top())
            .Drill(Bore(4), [new Vector2d(30, 20)], 14, Top());

        var solid = design.ToBrep();
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 4));

        double expected = 60 * 40 * 10 - 3 * Math.PI * 9 * 10 - Math.PI * 4 * 10;
        // Tessellated bores under-remove slightly; 1% covers the discretization.
        Assert.InRange(design.ToMesh().Volume(), expected, expected * 1.01);
    }

    [Fact]
    public void DrillDepthCoplanarityStillThrowsAgainstTheSharedLowering()
    {
        // The validation moved onto the already-lowered body; it must still fire.
        var error = Assert.Throws<ArgumentException>(() =>
            Plate().Drill(Bore(6), [new Vector2d(20, 20)], 10, Top()).ToBrep());
        Assert.Contains("coplanar", error.Message);
    }
}
