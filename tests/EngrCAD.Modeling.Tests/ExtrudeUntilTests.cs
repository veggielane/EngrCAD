using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>Extrude/cut UNTIL a face of the body: the stop-distance resolution (the
/// internal seam, where the robustness lives), the overshoot rules, the loud
/// refusals, and end-to-end volumes on exact polyhedra.</summary>
public class ExtrudeUntilTests
{
    private static readonly SketchPlane Above =
        SketchPlane.At((0, 0, 20), Vector3d.UnitX, Vector3d.UnitY);

    /// <summary>One plate, z ∈ [0, 8].</summary>
    private static Shape Plate() => Shape.Extrude(Sketch.Rectangle(40, 30), 8);

    /// <summary>Two plates with a void between: z ∈ [0, 8] and z ∈ [−20, −12].</summary>
    private static Shape Stack() => Plate() | Plate().Translate(0, 0, -20);

    private static Sketch Square() => Sketch.Rectangle(6, 6);

    // ---- the distance resolution (internal seam) ----

    [Fact]
    public void CutNext_SinglePlate_StopsAtTheFarSideOfTheFirstWall()
    {
        var r = UntilResolver.Resolve(Plate(), Square(), Above, Until.Next, cut: true, null);
        Assert.Equal(20, r.Distance, 9);          // plane z=20 → plate bottom z=0
        Assert.Equal(0.02 * 40, r.Overshoot, 9);  // nothing beyond: capped overshoot
    }

    [Fact]
    public void CutNext_Stack_StopsInTheVoidBetweenThePlates()
    {
        var r = UntilResolver.Resolve(Stack(), Square(), Above, Until.Next, cut: true, null);
        Assert.Equal(20, r.Distance, 9);
        // The gap to the second plate is 12; half of it exceeds the 2% cap (0.8).
        Assert.Equal(0.8, r.Overshoot, 9);
        Assert.True(r.Height < 32, "the cut must not reach the second plate");
    }

    [Fact]
    public void CutLast_Stack_GoesThroughEverythingWithOvershoot()
    {
        var r = UntilResolver.Resolve(Stack(), Square(), Above, Until.Last, cut: true, null);
        Assert.Equal(40, r.Distance, 9);          // far face z = −20
        Assert.True(r.Overshoot > 0);             // the Drill never-coplanar rule
    }

    [Fact]
    public void AddNext_SinglePlate_LandsOnTheTopFaceAndOvershootsIn()
    {
        var r = UntilResolver.Resolve(Plate(), Square(), Above, Until.Next, cut: false, null);
        Assert.Equal(12, r.Distance, 9);          // plane z=20 → plate top z=8
        Assert.InRange(r.Overshoot, 1e-9, 4);     // into the material, ≤ half the wall
    }

    [Fact]
    public void AddLast_Stack_IsExactlyFlushWithTheFarFace()
    {
        var r = UntilResolver.Resolve(Stack(), Square(), Above, Until.Last, cut: false, null);
        Assert.Equal(40, r.Distance, 9);
        Assert.Equal(0, r.Overshoot);             // flush by definition
    }

    // ---- refusals ----

    [Fact]
    public void CurvedStop_RefusesNamingTheClusters()
    {
        var sphere = Shape.Sphere(10);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            UntilResolver.Resolve(sphere, Sketch.Circle(3), Above, Until.Next, cut: true, null));
        Assert.Contains("no single stop plane", exception.Message);
        Assert.Contains("rays", exception.Message);
        Assert.Contains("explicit depth", exception.Message);
    }

    [Fact]
    public void OverhangingProfile_RefusesCountingTheMisses()
    {
        var small = Shape.Extrude(Sketch.Rectangle(10, 10), 8);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            UntilResolver.Resolve(small, Sketch.Rectangle(20, 20), Above, Until.Next, cut: true, null));
        Assert.Contains("overhang", exception.Message);
        Assert.Contains("probe rays", exception.Message);
    }

    [Fact]
    public void BossFromAPlaneInsideTheBody_Refuses()
    {
        var inside = SketchPlane.At((0, 0, 4), Vector3d.UnitX, Vector3d.UnitY);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            UntilResolver.Resolve(Plate(), Square(), inside, Until.Next, cut: false, null));
        Assert.Contains("inside the body", exception.Message);
    }

    [Fact]
    public void BossOntoAPlaneTheBodyTouches_Refuses()
    {
        var onTop = SketchPlane.At((0, 0, 8), Vector3d.UnitX, Vector3d.UnitY);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            UntilResolver.Resolve(Plate(), Square(), onTop, Until.Next, cut: false, null));
        Assert.Contains("already touches", exception.Message);
    }

    // ---- end to end (exact polyhedral volumes) ----

    [Fact]
    public void CutUntilNext_CutsTheFirstPlateOnly()
    {
        var result = Stack().CutUntil(Square(), Above, Until.Next);
        // Both plates: 2 × 40·30·8 = 19200; the cut removes 6×6 through plate A only.
        Assert.Equal(19200 - 36 * 8, result.ToMesh().Volume(), 6);
    }

    [Fact]
    public void CutUntilLast_CutsBothPlates()
    {
        var result = Stack().CutUntil(Square(), Above, Until.Last);
        Assert.Equal(19200 - 2 * 36 * 8, result.ToMesh().Volume(), 6);
    }

    [Fact]
    public void CutUntilNext_FromThePlateTopFace_CutsThrough()
    {
        var onTop = SketchPlane.At((0, 0, 8), Vector3d.UnitX, Vector3d.UnitY);
        var result = Plate().CutUntil(Square(), onTop, Until.Next);
        Assert.Equal(9600 - 36 * 8, result.ToMesh().Volume(), 6);
    }

    [Fact]
    public void ExtrudeUntilNext_BridgesTheGapExactly()
    {
        var result = Plate().ExtrudeUntil(Square(), Above, Until.Next);
        // Boss spans plane (z=20) to plate top (z=8); the overshoot into the plate is
        // swallowed by the union, so the added volume is exactly 6·6·12.
        Assert.Equal(9600 + 36 * 12, result.ToMesh().Volume(), 6);
    }

    [Fact]
    public void ExplainStaysHonest_ResultIsOrdinaryNodes()
    {
        // The resolved shape is a plain boolean over a plain extrusion — Explain
        // reports it Native everywhere a plate|prism is, with no special node.
        var result = Plate().ExtrudeUntil(Square(), Above, Until.Next);
        Assert.True(result.CanConvertTo(TargetRep.Brep));
        Assert.True(result.CanConvertTo(TargetRep.Implicit));
        Assert.True(result.CanConvertTo(TargetRep.Mesh));
    }
}
