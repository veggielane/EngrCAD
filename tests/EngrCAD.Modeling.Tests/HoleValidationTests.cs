using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Up-front validation of degenerate <see cref="Shape.Drill"/> configurations, which
/// previously failed deep inside tessellation with "Directed edge appears twice":
/// overlapping/tangent hole circles are rejected at <c>Drill</c> itself, and a tool
/// bottom coplanar with a planar body face is rejected during B-Rep lowering (the rim
/// features' validate-against-the-lowered-solid precedent).
/// </summary>
public class HoleValidationTests
{
    private static readonly SketchPlane Top = SketchPlane.At((0, 0, 0.5), Vector3d.UnitX, Vector3d.UnitY);

    private static Shape Plate() => Shape.Box(4, 3, 1); // z ∈ [−0.5, 0.5]

    // ---- overlapping / tangent holes: rejected at Drill() ----

    [Fact]
    public void Drill_OverlappingHoles_ThrowsNamingThePair()
    {
        var e = Assert.Throws<ArgumentException>(() =>
            Plate().Drill(HoleSpec.Simple(0.8), [new(0, 0), new(0.5, 0)], depth: 2, Top));
        Assert.Contains("overlap or are tangent", e.Message);
        Assert.Contains("0.8", e.Message);           // the surface diameter
        Assert.Contains("(0.5, 0)", e.Message);      // the offending point
    }

    [Fact]
    public void Drill_ExactlyTangentHoles_Throws()
    {
        // Center distance exactly one surface diameter: circles touch.
        Assert.Throws<ArgumentException>(() =>
            Plate().Drill(HoleSpec.Simple(0.8), [new(-0.4, 0), new(0.4, 0)], depth: 2, Top));
    }

    [Fact]
    public void Drill_CounterboreRecessesOverlap_ThrowsEvenWhenBoresClear()
    {
        // Bores (dia 0.6) are far apart, but the counterbore recesses (dia 1.4) overlap:
        // the RECESS circle is the surface-level footprint that must stay clear.
        Assert.Throws<ArgumentException>(() =>
            Plate().Drill(HoleSpec.Counterbore(0.6, 1.4, 0.3), [new(-0.6, 0), new(0.6, 0)], depth: 2, Top));
    }

    [Fact]
    public void Drill_ClearlySeparatedHoles_Lowers()
    {
        var drilled = Plate().Drill(HoleSpec.Simple(0.8), [new(-1, 0), new(1, 0)], depth: 2, Top);
        var solid = drilled.ToBrep();
        solid.Validate();
    }

    // ---- tool bottom coplanar with the far face: rejected at B-Rep lowering ----

    [Fact]
    public void Drill_BottomCoplanarWithFarFace_ThrowsWithGuidance()
    {
        // Depth 1.0 from the top plane at z = 0.5 puts the flat tool bottom exactly on
        // the plate's bottom face at z = −0.5.
        var drilled = Plate().Drill(HoleSpec.Simple(0.8), [new(0, 0)], depth: 1.0, Top);
        var e = Assert.Throws<ArgumentException>(() => drilled.ToBrep());
        Assert.Contains("coplanar", e.Message);
        Assert.Contains("increase depth so the tool clears the far face, or reduce it for a blind hole", e.Message);
    }

    [Fact]
    public void Drill_BottomCoplanarWithFarFace_ThrowsForMeshToo()
    {
        // The mesh path tessellates the lowered B-Rep, so it validates identically.
        var drilled = Plate().Drill(HoleSpec.Simple(0.8), [new(0, 0)], depth: 1.0, Top);
        Assert.Throws<ArgumentException>(() => drilled.ToMesh());
    }

    [Fact]
    public void Drill_ThroughAndBlindDepths_StillLower()
    {
        // Just past the far face (through) and safely short of it (blind) both remain
        // legal — only exact coplanarity is degenerate.
        Plate().Drill(HoleSpec.Simple(0.8), [new(0, 0)], depth: 1.2, Top).ToBrep().Validate();
        Plate().Drill(HoleSpec.Simple(0.8), [new(0, 0)], depth: 0.6, Top).ToBrep().Validate();
    }
}
