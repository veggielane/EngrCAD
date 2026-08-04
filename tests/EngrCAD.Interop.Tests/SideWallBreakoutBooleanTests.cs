using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// A bore drilled into a sketch extrusion's SIDE WALL whose rim runs off that wall —
/// breaking out through the top face. The wall is a bounded planar patch, so the rim is a
/// genuine circle of the wall's own plane with only part of it on the wall; until the
/// analytic tier learned to CLIP the conic, the pair fell to the marching tracer and the
/// boolean came back as an unclosed solid.
///
/// <para>The plate is deliberately built two ways. As a <see cref="Shape.Box"/> its walls
/// are unbounded <c>PlaneSurface</c>s, which have always taken the analytic route; as a
/// sketch extrusion they are bounded patches, which is the case under test. The two are the
/// same solid, so the bounded route agreeing with the unbounded one AT EVERY DENSITY is a
/// stronger statement than either convergence table on its own.</para>
/// </summary>
public class SideWallBreakoutBooleanTests
{
    private const double Lx = 40, Ly = 30, H = 10, R = 3, Z0 = 9;

    /// <summary>Removed volume: the bore's disc less the circular segment above z = H.</summary>
    private static double Removed(double length) => RemovedAt(Z0, length);

    /// <summary><see cref="Removed"/> for a bore on any axis height — the signed distance
    /// d = H − z0 carries a bore whose axis sits ABOVE the face as naturally as one below,
    /// since acos takes the whole [−1, 1]; only a bore that never reaches the face at all
    /// (|d| ≥ R) is a separate case, and there the whole disc is removed.</summary>
    private static double RemovedAt(double z0, double length)
    {
        double d = H - z0;
        if (Math.Abs(d) >= R)
            return Math.PI * R * R * length;
        double segment = R * R * Math.Acos(d / R) - d * Math.Sqrt(R * R - d * d);
        return (Math.PI * R * R - segment) * length;
    }

    private static Shape ExtrudedPlate() => Shape.Extrude(Sketch.Rectangle(Lx, Ly), H);
    private static Shape BoxPlate() => Shape.Box(Lx, Ly, H).Translate((0, 0, H / 2));

    /// <summary>A bore along +Y spanning [y0, y1] with its axis at z = <see cref="Z0"/>.</summary>
    private static Shape Bore(double y0, double y1) =>
        Shape.Cylinder(R, y1 - y0)
             .Rotate(Vector3d.UnitX, -Math.PI / 2)
             .Translate((0, (y0 + y1) / 2, Z0));

    private static double SoundVolume(Shape shape, int segments, int genus = 0)
    {
        var solid = shape.ToBrep();
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus),
            $"result must satisfy Euler–Poincaré at genus {genus}");
        var mesh = BRepTessellator.Tessellate(solid, segments, segments);
        mesh.Validate();
        Assert.True(mesh.IsClosed, "result must tessellate closed");
        return mesh.Volume();
    }

    [Fact]
    public void ThroughBoreOffTheWallsTopEdge_ConvergesOnTheAnalyticVolume()
    {
        // THE regression. The tessellated solid is inscribed, so the error must be
        // one-signed and fall by 4 per doubling; a WANDERING error whose sign flips is the
        // signature of a fixed sampling floor, which is what the tracer's polyline rim was.
        double exact = Lx * Ly * H - Removed(Ly);
        double[] errors = [.. new[] { 32, 64, 128, 256 }
            .Select(n => SoundVolume(ExtrudedPlate() - Bore(-2 * Ly, 2 * Ly), n) - exact)];

        Assert.All(errors, e => Assert.True(e > 0, $"inscribed tessellation must over-report: {e}"));
        for (int i = 1; i < errors.Length; i++)
        {
            double ratio = errors[i - 1] / errors[i];
            Assert.True(ratio > 3.6 && ratio < 4.4,
                $"quadratic convergence expected, got {ratio:F2} " +
                $"({string.Join(", ", errors.Select(e => e.ToString("E3")))})");
        }
    }

    [Fact]
    public void ThroughBoreOffTheWallsTopEdge_BoundedWallAgreesWithTheUnboundedPlane()
    {
        // The same solid modelled two ways. A box's side is a PlaneSurface and takes the
        // analytic route whole; an extrusion's side is a bounded patch and takes the clip.
        // Agreement here says the clip reproduces the plane's own answer rather than
        // merely converging somewhere near it.
        foreach (int n in (int[])[32, 64, 128, 256])
        {
            double box = SoundVolume(BoxPlate() - Bore(-2 * Ly, 2 * Ly), n);
            double extruded = SoundVolume(ExtrudedPlate() - Bore(-2 * Ly, 2 * Ly), n);
            Assert.Equal(box, extruded, 9);
        }
    }

    [Fact]
    public void BlindBoreOffTheWallsTopEdge_ConvergesOnTheAnalyticVolume()
    {
        // Half the bore: it enters the −Y wall and its flat end sits inside the plate, so
        // the clipped rim is on one wall only.
        double exact = Lx * Ly * H - Removed(Ly / 2);
        double[] errors = [.. new[] { 32, 64, 128, 256 }
            .Select(n => SoundVolume(ExtrudedPlate() - Bore(-2 * Ly, 0), n) - exact)];

        Assert.All(errors, e => Assert.True(e > 0, $"inscribed tessellation must over-report: {e}"));
        for (int i = 1; i < errors.Length; i++)
            Assert.True(errors[i - 1] / errors[i] > 3.6, $"ratio {errors[i - 1] / errors[i]:F2}");
    }

    [Fact]
    public void DrilledThroughHoleOffTheWallsTopEdge_ConvergesOnTheAnalyticVolume()
    {
        // The Shape-level spelling: Drill's tools are axis-touching REVOLVES rather than
        // cylinders, which is the carrier the pipeline actually meets. Drilled THROUGH, so
        // the tool's flat end never lands inside the plate — see the blind case below for
        // why that matters and what it is measuring.
        var wall = SketchPlane.At((0, -Ly / 2, Z0), Vector3d.UnitX, Vector3d.UnitZ);
        double exact = Lx * Ly * H - Removed(Ly);

        double[] errors = [.. new[] { 32, 64, 128 }.Select(n =>
            SoundVolume(ExtrudedPlate().Drill(HoleSpec.Simple(2 * R), [(0, 0)], Ly * 1.1, wall), n) - exact)];

        Assert.All(errors, e => Assert.True(e > 0, $"inscribed tessellation must over-report: {e}"));
        for (int i = 1; i < errors.Length; i++)
            Assert.True(errors[i - 1] / errors[i] > 3.6, $"ratio {errors[i - 1] / errors[i]:F2}");
    }

    [Fact]
    public void BlindDrilledHoleWhoseFLATENDAlsoBreaksOut_ConvergesOnTheAnalyticVolume()
    {
        // `Drill`'s tool is ONE axis-touching revolve, so its flat end is a RevolvedSurface
        // POLE CAP rather than the PlaneSurface a `Shape.Cylinder`'s cap is — and when the
        // bore is BLIND that cap lands inside the body and gets CUT by the face the bore
        // breaks out of. A cut pole cap is what used to crack this solid (see
        // BrepBoolean.ProbePoint, and the family sweep below for the rule).
        var wall = SketchPlane.At((0, -Ly / 2, Z0), Vector3d.UnitX, Vector3d.UnitZ);
        double exact = Lx * Ly * H - Removed(Ly / 2);
        double[] errors = [.. new[] { 32, 64, 128, 256 }.Select(n =>
            SoundVolume(
                ExtrudedPlate().Drill(HoleSpec.Simple(2 * R), [(0, 0)], Ly / 2, wall), n) - exact)];

        Assert.All(errors, e => Assert.True(e > 0, $"inscribed tessellation must over-report: {e}"));
        for (int i = 1; i < errors.Length; i++)
        {
            double ratio = errors[i - 1] / errors[i];
            Assert.True(ratio > 3.6 && ratio < 4.4,
                $"quadratic convergence expected, got {ratio:F2} " +
                $"({string.Join(", ", errors.Select(e => e.ToString("E3")))})");
        }
    }

    [Fact]
    public void BlindDrilledHoleAndTheSameCutByACylinder_AgreeInTheLIMIT()
    {
        // The two-constructions oracle, at the strength it is honestly available at. The
        // pair above (a sketch extrusion's bounded wall against a box's unbounded plane)
        // agrees to 1e-11 AT EVERY DENSITY because both routes produce the same SURFACES
        // and so the same grid. These two do not: a `Drill` tool's side is a
        // RevolvedSurface band while a `Shape.Cylinder`'s is a CylinderSurface, and the two
        // carry different tessellation rules — measured, the drill route reads +0.674 where
        // the cylinder route reads +0.457 at 32 segments. So what can be asserted is that
        // the DIFFERENCE converges to zero at the same quadratic rate, which says the two
        // describe one solid rather than two that happen to be close.
        var wall = SketchPlane.At((0, -Ly / 2, Z0), Vector3d.UnitX, Vector3d.UnitZ);
        double[] gaps = [.. new[] { 32, 64, 128, 256 }.Select(n =>
            SoundVolume(ExtrudedPlate().Drill(HoleSpec.Simple(2 * R), [(0, 0)], Ly / 2, wall), n)
            - SoundVolume(ExtrudedPlate() - Bore(-2 * Ly, 0), n))];

        Assert.All(gaps, g => Assert.True(g > 0, $"the coarser route must over-report: {g}"));
        for (int i = 1; i < gaps.Length; i++)
            Assert.True(gaps[i - 1] / gaps[i] > 3.4, $"ratio {gaps[i - 1] / gaps[i]:F2}");
        Assert.True(gaps[^1] < 3e-3, $"the two routes must be converging together: {gaps[^1]:E3}");
    }

    /// <summary>
    /// The rule the case above turns on, measured across the family rather than at one
    /// depth: how far a blind drill's flat POLE CAP breaks out decides how the cut crosses
    /// it, and <see cref="BrepBoolean.ProbePoint"/> has to land inside the piece that
    /// survives at every one.
    ///
    /// <para>A single loop that WRAPS the periodic direction separates the pole from
    /// everything else, so such a face is the pole's side and every v strictly between the
    /// pole and the loop is inside AT EVERY u — which is what lets the probe skip the parity
    /// check. It must read the loop's CLOSEST APPROACH to the pole, though, not its average:
    /// an uncut cap's rim sits at one v so the two coincide, and a CUT one's do not, so the
    /// average names a v the face no longer reaches at every u.</para>
    ///
    /// <para>A FAMILY rather than a fixture because whether the average lands outside
    /// depends on where the cut runs — the alignment-not-tolerance reasoning the tangency
    /// and Surface Nets sweeps use. Measured, the average put the probe 0.106 above the
    /// plate's own top face at z0 = 9 and the whole boolean refused; three of these ten rows
    /// (10.5, 9.5, 9) fail without the rule and none with it, while 6.5 is the control whose
    /// cap is not cut at all.</para>
    /// </summary>
    [Fact]
    public void BlindDrilledHolesAcrossTheBreakoutFamily_AllCloseOnTheirAnalyticVolume()
    {
        foreach (double z0 in (double[])[11.5, 10.5, 9.5, 9, 8.5, 8, 7.5, 7.2, 7.05, 6.5])
        {
            var wall = SketchPlane.At((0, -Ly / 2, z0), Vector3d.UnitX, Vector3d.UnitZ);
            double removed = RemovedAt(z0, Ly / 2);
            double error =
                SoundVolume(ExtrudedPlate().Drill(HoleSpec.Simple(2 * R), [(0, 0)], Ly / 2, wall), 64)
                - (Lx * Ly * H - removed);

            // One-signed is the sharp half (an inscribed tool removes LESS than the analytic
            // one, so the plate must over-report); the magnitude is bounded by the inscribed
            // n-gon's own deficit, 2*pi^2/(3n^2) = 1.6e-3 of the removed volume at n = 64,
            // with a factor of ~3 of slack for the breakout's own faceting.
            Assert.True(error > 0, $"z0 {z0}: inscribed tessellation must over-report, got {error:E3}");
            Assert.True(error < 5e-3 * removed,
                $"z0 {z0}: error {error:E3} exceeds {5e-3 * removed:E3} on a removed volume of {removed:F3}");
        }
    }
}
