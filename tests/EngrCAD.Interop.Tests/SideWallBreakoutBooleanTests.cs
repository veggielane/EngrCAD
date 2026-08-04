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
    private static double Removed(double length)
    {
        double d = H - Z0;
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
    public void BlindDrilledHoleWhoseFLATENDAlsoBreaksOut_StillRefuses()
    {
        // A separate, still-open gap, pinned so it cannot rot into a guess — and note what
        // separates it from the case above. `Shape.Cylinder`'s end cap is a PlaneSurface,
        // so a blind CYLINDER whose rim runs off the wall works (see
        // BlindBoreOffTheWallsTopEdge_...). `Drill`'s tool is ONE axis-touching revolve, so
        // its flat end is a RevolvedSurface POLE CAP — geometrically a disc in a plane, but
        // not a planar carrier any tier recognizes, so where THAT disc breaks out of the
        // top face the pair still falls to the marching tracer. The wall's own rim is fine;
        // the refusal names the disc's rim.
        var wall = SketchPlane.At((0, -Ly / 2, Z0), Vector3d.UnitX, Vector3d.UnitZ);
        var blind = ExtrudedPlate().Drill(HoleSpec.Simple(2 * R), [(0, 0)], Ly / 2, wall);

        var failure = Assert.Throws<InvalidOperationException>(() => blind.ToBrep());
        Assert.Contains("unclosed solid", failure.Message);
    }
}
