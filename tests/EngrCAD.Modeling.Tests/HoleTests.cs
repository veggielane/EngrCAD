using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class HoleTests
{
    private static readonly SketchPlane Top = SketchPlane.At((0, 0, 0.5), Vector3d.UnitX, Vector3d.UnitY);

    /// <summary>Inscribed n-gon area — what a tessellated circle of radius r encloses.</summary>
    private static double NgonArea(int n, double r) => 0.5 * n * r * r * Math.Sin(2 * Math.PI / n);

    [Fact]
    public void SimpleHoles_ThroughAndBlind_ExactVolumes()
    {
        int n = 128;
        var plate = Shape.Box(4, 3, 1); // z ∈ [−0.5, 0.5]

        var through = plate.Drill(HoleSpec.Simple(0.8), [new(1, 0.5), new(-1, -0.5)], depth: 2, Top);
        var solid = through.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, n, 24);
        Assert.True(mesh.IsClosed);
        Assert.Equal(-2, mesh.EulerCharacteristic); // genus 2
        double exact = 12 - 2 * NgonArea(n, 0.4) * 1;
        Assert.True(Math.Abs(mesh.Volume() - exact) < 1e-9, $"volume {mesh.Volume()} vs {exact}");

        var blind = plate.Drill(HoleSpec.Simple(0.8), [new(0, 0)], depth: 0.6, Top);
        var blindSolid = blind.ToBrep();
        blindSolid.Validate();
        var blindMesh = BRepTessellator.Tessellate(blindSolid, n, 24);
        Assert.True(blindMesh.IsClosed);
        Assert.Equal(2, blindMesh.EulerCharacteristic); // still genus 0
        double blindExact = 12 - NgonArea(n, 0.4) * 0.6;
        Assert.True(Math.Abs(blindMesh.Volume() - blindExact) < 1e-9,
            $"volume {blindMesh.Volume()} vs {blindExact}");
    }

    [Fact]
    public void Counterbore_ExactVolume()
    {
        int n = 128;
        var drilled = Shape.Box(4, 3, 1)
            .Drill(HoleSpec.Counterbore(0.6, 1.2, 0.3), [new(0, 0)], depth: 2, Top);
        var solid = drilled.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, n, 24);
        Assert.True(mesh.IsClosed);
        double exact = 12 - NgonArea(n, 0.6) * 0.3 - NgonArea(n, 0.3) * 0.7;
        Assert.True(Math.Abs(mesh.Volume() - exact) < 1e-9, $"volume {mesh.Volume()} vs {exact}");
    }

    [Fact]
    public void Countersink_ExactVolume()
    {
        int n = 128;
        // 90° countersink: slope 1, so the cone runs from r=0.6 at the surface to
        // r=0.3 at depth 0.3. Tessellated cone = frustum of an n-gon pyramid.
        var drilled = Shape.Box(4, 3, 1)
            .Drill(HoleSpec.Countersink(0.6, 1.2, angleDegrees: 90), [new(0, 0)], depth: 2, Top);
        var solid = drilled.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, n, 24);
        Assert.True(mesh.IsClosed);
        double a1 = NgonArea(n, 0.6), a2 = NgonArea(n, 0.3);
        double frustum = 0.3 / 3 * (a1 + a2 + Math.Sqrt(a1 * a2));
        double exact = 12 - frustum - NgonArea(n, 0.3) * 0.7;
        Assert.True(Math.Abs(mesh.Volume() - exact) < 1e-9, $"volume {mesh.Volume()} vs {exact}");
    }

    [Fact]
    public void Holes_ImplicitIsNativeAndExact()
    {
        var drilled = Shape.Box(4, 3, 1)
            .Drill(HoleSpec.Counterbore(0.6, 1.2, 0.3), [new(1, 0.5)], depth: 2, Top);

        var report = drilled.Explain(TargetRep.Implicit);
        Assert.True(report.IsConvertible);
        Assert.All(report.Entries, e => Assert.Equal(NodeSupport.Native, e.Support));

        var sdf = drilled.ToImplicit();
        // Center of the bore, mid-plate: nearest surface is the bore wall, 0.3 away.
        Assert.True(Math.Abs(sdf.Evaluate((1, 0.5, 0)) - 0.3) < 1e-9);
        // Solid material far from the hole.
        Assert.True(sdf.Evaluate((-1.5, -1, 0)) < 0);
    }

    [Fact]
    public void StandardHoles_ProduceExpectedGeometry()
    {
        // M5 normal clearance: Ø5.5 → SDF distance from the bore axis is the radius.
        var plate = Shape.Box(40, 30, 8);
        var plateTop = SketchPlane.At((0, 0, 4), Vector3d.UnitX, Vector3d.UnitY);
        var drilled = plate.Drill(StandardHoles.Clearance(5), [new(0, 0)], depth: 10, plateTop);
        var sdf = drilled.ToImplicit();
        Assert.True(Math.Abs(sdf.Evaluate((0, 0, 0)) - 2.75) < 1e-9);

        // M4 counterbore: Ø8 recess to depth 4.5. On the axis 1 below the surface the
        // point is in the recess void; the CSG difference SDF max(a, −b) is a correct-
        // sign lower bound whose magnitude here is governed by the tool's overshoot cap
        // (0.5 above the surface): 1 + 0.5 = 1.5.
        var counterbored = plate.Drill(StandardHoles.Counterbored(4), [new(10, 0)], depth: 10, plateTop);
        Assert.True(Math.Abs(counterbored.ToImplicit().Evaluate((10, 0, 3)) - 1.5) < 1e-9);

        // Standard specs build valid B-Reps.
        var solid = drilled.ToBrep();
        solid.Validate();
        Assert.True(BRepTessellator.Tessellate(solid, 64, 24).IsClosed);

        Assert.True(StandardHoles.TrisertMinimumDepth(4) > 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => StandardHoles.Clearance(7));
    }

    [Fact]
    public void HoleSpec_ValidatesInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HoleSpec.Simple(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => HoleSpec.Counterbore(1, 0.8, 0.2));
        Assert.Throws<ArgumentOutOfRangeException>(() => HoleSpec.Countersink(1, 0.9));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Shape.Box(1, 1, 1).Drill(HoleSpec.Simple(0.2), [new(0, 0)], depth: 0));
    }
}
