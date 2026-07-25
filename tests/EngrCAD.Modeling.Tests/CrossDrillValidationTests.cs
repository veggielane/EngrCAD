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

    [Fact]
    public void HolesOnDifferentPlanesAreNotCompared()
    {
        // Documented limitation: the check is a 2D centre-distance test on ONE placement
        // plane. Opposing bores on the two faces of a plate are not compared (deciding
        // that needs a tool-vs-tool solid intersection), so this must not throw.
        var bottom = SketchPlane.At((0, 0, 0), Vector3d.UnitX, -Vector3d.UnitY);
        var design = Plate()
            .Drill(Bore(6), [new Vector2d(20, 20)], 6, Top())
            .Drill(Bore(6), [new Vector2d(20, -20)], 6, bottom);
        Assert.NotNull(design);
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
