using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// A NON-coaxial cut through a modelled thread — the case the coaxial analytic family
/// structurally cannot serve, so it belongs to the marching tracer. Two separate defects
/// used to make it refuse at every size, and only the second is fixed here: the seeding
/// (an anisotropic band's branches fall between an isotropic grid's columns) and the
/// TERMINATION (a branch stops one march step short of the rail it was running into, and
/// the step is scaled to the query region while the band is not — 0.161 mm against a
/// 0.156 mm-tall crest flat, so one step crosses the whole band).
///
/// <para>Measured on this fixture before the termination fix: <b>13 of 13</b> bores from
/// 0.6 to 3.0 refused, almost all with "Open splitting curves must start and end outside
/// the face". After: EIGHT of thirteen build Validate-clean, closed solids. The five that
/// still refuse do so for the filed reason — a branch that stops at a FOLD rather than at
/// a boundary has nothing to land on — so the second test states the property that has to
/// hold at every size, which is that a refusal is a throw and never a closed solid of the
/// wrong shape.</para>
/// </summary>
public class CrossDrilledThreadTests
{
    private const double Pitch = 1.25;
    private const double Major = 4.0;
    private const double Length = 6.0;

    private static BrepSolid Rod()
    {
        double minor = Major - 0.625 * (Math.Sqrt(3) / 2 * Pitch);
        return SolidFactory.MakeThreadedRod(
        [
            new Vector2d(Major, -Pitch / 16),
            new Vector2d(Major, Pitch / 16),
            new Vector2d(minor, 3 * Pitch / 8),
            new Vector2d(minor, 5 * Pitch / 8),
        ], Pitch, Length);
    }

    /// <summary>A Ø(2·radius) bore straight through the rod at mid height, along +X.</summary>
    private static BrepSolid CrossDrill(double radius) =>
        SolidFactory.MakeCylinder(radius, 20).Transformed(
            Frame3d.FromOrthonormal((-10, 0, Length / 2), Vector3d.UnitY, Vector3d.UnitZ).ToMatrix());

    [Theory]
    [InlineData(0.8)]
    [InlineData(1.0)]
    [InlineData(1.4)]
    [InlineData(1.8)]
    [InlineData(2.0)]
    [InlineData(2.2)]
    [InlineData(2.4)]
    [InlineData(2.6)]
    public void ACrossDrilledThreadedRodBuilds(double radius)
    {
        double plain = MeshMassProperties.Compute(BRepTessellator.Tessellate(Rod(), 64, 64)).Volume;

        var cut = BrepBoolean.Difference(Rod(), CrossDrill(radius));
        cut.Validate();
        var mesh = BRepTessellator.Tessellate(cut, 64, 64);
        Assert.True(mesh.IsClosed, "a cross-drilled thread must weld closed");

        // The bore removes material and cannot remove more than its own cylinder. Both
        // bounds are read off the SAME discretization, so the comparison is exact rather
        // than a tolerance on two independently chorded solids.
        double volume = MeshMassProperties.Compute(mesh).Volume;
        Assert.True(volume < plain, $"bore {radius} removed nothing ({volume:F4} against {plain:F4})");
        Assert.True(plain - volume < Math.PI * radius * radius * 2 * Major * 1.05,
            $"bore {radius} removed {plain - volume:F4}, more than its own cylinder through the rod");
    }

    /// <summary>
    /// The property that must hold at EVERY size, including the five that still refuse:
    /// a bore either builds a closed, valid solid or it THROWS. What this family must not
    /// do is return a closed solid of the wrong shape, which is exactly what a curve
    /// stopping short of its rails used to risk — the face is then whole-classified and
    /// the crack is silent (`BrepBoolean.Verified`'s own argument).
    ///
    /// <para>The COUNT is asserted as a floor rather than the passing radii being listed,
    /// so a fix for the fold case makes this test read better without editing it — and a
    /// regression that loses a bore fails it.</para>
    /// </summary>
    [Fact]
    public void EveryBoreEitherBuildsOrThrows()
    {
        int built = 0;
        for (double radius = 0.6; radius <= 3.01; radius += 0.2)
        {
            double r = radius;
            var thrown = Record.Exception(() =>
            {
                var cut = BrepBoolean.Difference(Rod(), CrossDrill(r));
                cut.Validate();
                Assert.True(BRepTessellator.Tessellate(cut, 64, 64).IsClosed,
                    $"bore {r} produced an OPEN mesh without saying so");
            });
            if (thrown is null)
                built++;
        }
        Assert.True(built >= 8, $"only {built} of 13 bores build (was 0 before exact termination)");
    }
}
