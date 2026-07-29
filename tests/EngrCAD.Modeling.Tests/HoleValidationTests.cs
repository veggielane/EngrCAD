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

    // ---- the coplanarity measure is a point-to-PLANE distance ------------------
    // The guard has two halves: the face's plane must be parallel to the tool's flat
    // bottom (within CoplanarFaceCosine = 0.081°), and a point of one must lie on the
    // other. The second half used to be measured along the DRILL AXIS to whatever
    // in-plane point IsPlanar happened to report — a box cap's origin is a CORNER — so
    // inside the admitted tilt band its answer was set by the plate's SIZE rather than
    // by the gap. These two facts pin the fix: at 0.0573° of tilt on a 200x150 plate the
    // old form was 0.075 model units adrift, which is the whole clearance under a blind
    // hole and enough to swallow a real breakout.

    /// <summary>A tilt inside the parallel band: cos(1e-3) = 1 - 5e-7, comfortably above
    /// CoplanarFaceCosine's 1 - 1e-6, so the face still counts as parallel to the tool
    /// bottom and the distance half of the test is what decides. Negative so the stored
    /// origin sits ABOVE the plane at the drill point, which puts the old form's false
    /// positive on a blind hole (the case that unambiguously lowers) rather than on a
    /// grazing breakout.</summary>
    private const double Tilt = -1e-3;

    /// <summary>200 x 150 x 20 about the origin (z in [-10, 10]), tilted about X. Its
    /// bottom cap's stored plane origin is the corner (-100, -75, -10) rotated — 75 units
    /// laterally from the drill point, which is the whole lever.</summary>
    private static Shape TiltedPlate() => Shape.Box(200, 150, 20).Rotate(Vector3d.UnitX, Tilt);

    /// <summary>The sketch plane sits above the plate and drills along global +Z/-Z.</summary>
    private static readonly SketchPlane HighTop =
        SketchPlane.At((0, 0, 12), Vector3d.UnitX, Vector3d.UnitY);

    /// <summary>Depth from <see cref="HighTop"/> at which the tool's flat bottom lies
    /// EXACTLY in the tilted bottom face's plane. The face is the rotation of z = -10, so
    /// for its unit normal n the plane is {x : n.x = 10}; the tool bottom at (0, 0, 12-d)
    /// gives n.bottom = -cos(tilt)*(12-d), hence 12 + 10/cos(tilt).</summary>
    private static readonly double CoplanarDepth = 12 + 10 / Math.Cos(Tilt);

    [Fact]
    public void Drill_BottomOnATiltedFacesPlane_IsRejected()
    {
        var drilled = TiltedPlate().Drill(HoleSpec.Simple(8), [new(0, 0)], CoplanarDepth, HighTop);
        var e = Assert.Throws<ArgumentException>(() => drilled.ToBrep());
        Assert.Contains("coplanar", e.Message);
    }

    /// <summary>The depth that puts the tool bottom at the same HEIGHT as the bottom
    /// cap's stored plane origin — which is a corner 75 units away across the tilt, so
    /// the bore still has real floor under it. This is exactly where the old axial form
    /// read zero and refused.</summary>
    private static readonly double ClearDepth =
        12 + 10 * Math.Cos(Tilt) + 75 * Math.Sin(Tilt);

    [Fact]
    public void Drill_ClearOfATiltedFace_IsNotRejected()
    {
        // State the geometry the test depends on rather than trusting the name: the true
        // floor left under the bore is the tool bottom's distance to the tilted plane.
        double floor = 10 + Math.Cos(Tilt) * (12 - ClearDepth);
        Assert.InRange(floor, 0.07, 0.08);

        // A perfectly ordinary blind hole with 0.075 of material under it, and the case
        // the old axial-gap form mistook for coplanarity: 75 * sin(1e-3) = 0.075 of the
        // stored origin's lateral offset leaked straight into the measurement.
        var drilled = TiltedPlate().Drill(HoleSpec.Simple(8), [new(0, 0)], ClearDepth, HighTop);
        drilled.ToBrep().Validate();
    }
}
