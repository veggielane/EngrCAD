using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The user-facing cross-drill path: a drilled box (revolved-surface bore from
/// <see cref="Shape.Drill"/>) minus a perpendicular cylinder through the bore, entirely
/// through the Shape API — exercising the non-planar wrap-split of the tool band and
/// the bore band's wavy hole loops end to end through lowering and tessellation.
/// </summary>
public class CrossDrillShapeTests
{
    [Fact]
    public void Box_DrilledThenCrossDrilled_MeshesClosedWithGenus3()
    {
        var top = SketchPlane.At((0, 0, 0.5), Vector3d.UnitX, Vector3d.UnitY);
        var drilled = Shape.Box(2, 2, 1) // z ∈ [−0.5, 0.5]
            .Drill(HoleSpec.Simple(0.8), [new Vector2d(0, 0)], depth: 2, top);
        var tool = Shape.Cylinder(0.25, 4).RotateY(Math.PI / 2); // along +X through the bore
        var result = drilled.Subtract(tool);

        var solid = result.ToBrep();
        solid.Validate();
        // Two perpendicular through-holes joined at a lens = one X-shaped tunnel with
        // 4 openings ⇒ genus 4 − 1 = 3 (see CrossDrillBooleanTests for the derivation).
        Assert.True(solid.SatisfiesEulerFormula(genus: 3));

        var mesh = result.ToMesh();
        mesh.Validate();
        Assert.True(mesh.IsClosed, "cross-drilled shape must tessellate closed");
        Assert.Equal(-4, mesh.EulerCharacteristic); // χ = 2 − 2·3

        // Volume against the smooth analytic value box − boreZ − cylX + bicylinder
        // lens (V = ∫₋ₐᵃ 4√(a²−y²)√(b²−y²) dy, ≈ 0.148980 for a = 0.25, b = 0.4;
        // 16a³/3 when a = b). Tolerance is discretization-derived: the mesh inscribes
        // the curved surfaces with on-surface vertices, so the deficit is bounded by
        // area × the n-gon sagitta r(1 − cos(π/n)) of the coarsest circle (n = 32
        // default quality ⇒ bound ≈ 3.6e-2; measured error ≈ 3.1e-3).
        double lens = BicylinderVolume(0.25, 0.4);
        double expected = 4.0 - Math.PI * 0.4 * 0.4 - Math.PI * 0.25 * 0.25 * 2 + lens;
        double tolerance = mesh.SurfaceArea() * 0.4 * (1 - Math.Cos(Math.PI / 32));
        Assert.InRange(mesh.Volume(), expected - tolerance, expected + tolerance);
    }

    private static double BicylinderVolume(double a, double b)
    {
        const int n = 2000;
        double h = Math.PI / n;
        double sum = 0;
        for (int i = 0; i <= n; i++)
        {
            double t = -Math.PI / 2 + i * h;
            double f = Math.Cos(t) * Math.Cos(t) * Math.Sqrt(b * b - a * a * Math.Sin(t) * Math.Sin(t));
            sum += (i == 0 || i == n ? 1 : i % 2 == 1 ? 4 : 2) * f;
        }
        return 4 * a * a * sum * h / 3;
    }
}
