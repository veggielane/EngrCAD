using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Singular configurations are NAMED, not stumbled into: at a dead centre the driven
/// variable is first-order stationary along the mechanism's remaining motion, so the
/// sweep refuses to guess a branch and says which joint and which parameter — and a
/// merely-unreachable target is deliberately NOT called singular.
/// </summary>
public class MechanismSingularityTests
{
    private static Frame3d At(double x, double y, double z) =>
        Frame3d.FromXY((x, y, z), Vector3d.UnitX, Vector3d.UnitY);

    private static Frame3d Posed(double x, double y, double angle) =>
        Frame3d.FromXY(
            (x, y, 0),
            (Math.Cos(angle), Math.Sin(angle), 0),
            (-Math.Sin(angle), Math.Cos(angle), 0));

    private static Part BoxPart(string name) => new(name, MeshPrimitives.Box(4, 2, 1));

    /// <summary>A slider-crank (r = 5, l = 20) authored exactly assembled at crank
    /// angle 90°, slider on the world X axis.</summary>
    private static (Mechanism Mechanism, RevoluteJoint CrankPin, PrismaticJoint Slide) SliderCrank()
    {
        var rig = new Assembly("engine");
        var ground = rig.Add(BoxPart("ground"));
        var crank = rig.Add(BoxPart("crank"));
        var rod = rig.Add(BoxPart("rod"));
        var slider = rig.Add(BoxPart("slider"));
        double x0 = Math.Sqrt(20 * 20 - 5 * 5);
        crank.Frame = Posed(0, 0, Math.PI / 2);
        rod.Frame = Posed(0, 5, Math.Atan2(-5, x0));
        slider.Frame = At(x0, 0, 0);

        var z = Vector3d.UnitZ;
        var crankPin = Joint.Revolute(
            MateGeometry.Axis(ground, (0, 0, 0), z), MateGeometry.Axis(crank, (0, 0, 0), z), "crank pin");
        var slide = Joint.Prismatic(
            MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitX),
            MateGeometry.Axis(slider, (0, 0, 0), Vector3d.UnitX), "slide");
        var mechanism = new Mechanism(rig)
            .Ground(ground)
            .Add(crankPin)
            .Add(Joint.Revolute(MateGeometry.Axis(crank, (5, 0, 0), z), MateGeometry.Axis(rod, (0, 0, 0), z), "wrist"))
            .Add(Joint.Revolute(MateGeometry.Axis(rod, (20, 0, 0), z), MateGeometry.Axis(slider, (0, 0, 0), z), "pin"))
            .Add(slide);
        mechanism.Assemble();
        return (mechanism, crankPin, slide);
    }

    [Fact]
    public void SliderDrivenIntoTopDeadCentre_IsNamedSingular()
    {
        var (mechanism, _, slide) = SliderCrank();

        // The slider's reach limit (crank + rod stretched, x = 25) IS the dead
        // centre: dz/dθ = 0 there. Driving the SLIDER toward and past it must stop,
        // be called singular, and name the driven joint.
        var study = mechanism.Sweep(MechanismDriver.Slide(slide), 0, 8, frames: 17);

        Assert.False(study.Completed);
        Assert.True(study.Singular, study.ToString());
        Assert.NotNull(study.FailedAt);
        Assert.Contains(study.Diagnostics, d => d.Contains("dead centre") && d.Contains("slide"));
        // The parameter is reported and honest: near the stretch limit 25 − √375.
        double limit = 25 - Math.Sqrt(375);
        Assert.True(Math.Abs(study.FailedAt!.Value - limit) < 0.2,
            $"failed at {study.FailedAt}, dead centre at {limit:g4}");
    }

    [Fact]
    public void CrankDrivenThroughTheSamePose_IsNotSingular()
    {
        var (mechanism, crankPin, _) = SliderCrank();

        // The SAME configuration is harmless when the crank is the driver: dz/dθ = 0
        // is a property of driving z, not of the pose. The crank sweeps through its
        // own 0° (slider at top dead centre) without complaint.
        var study = mechanism.Sweep(MechanismDriver.Angle(crankPin), 0, -2 * Math.PI, frames: 49);

        Assert.True(study.Completed, study.ToString());
        Assert.False(study.Singular);
    }

    [Fact]
    public void ContradictoryTarget_IsReportedAsUnreachable_NotSingular()
    {
        // A slider pinned by a Distance mate: driving it anywhere else is simply
        // unreachable — no rank is lost, so it must NOT be called a dead centre.
        var rig = new Assembly("rig");
        var ground = rig.Add(BoxPart("ground"));
        var slider = rig.Add(BoxPart("slider"), At(10, 0, 0));
        var slide = Joint.Prismatic(
            MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitX),
            MateGeometry.Axis(slider, (0, 0, 0), Vector3d.UnitX), "slide");
        var mechanism = new Mechanism(rig)
            .Ground(ground)
            .Add(slide)
            .Add(Mate.Distance(
                MateGeometry.Point(ground, (0, 0, 0)), MateGeometry.Point(slider, (0, 0, 0)), 10, "tether"));
        mechanism.Assemble();

        var study = mechanism.Sweep(MechanismDriver.Slide(slide), 0, 6, frames: 13);

        Assert.False(study.Completed);
        Assert.False(study.Singular, study.ToString());
        Assert.Contains(study.Diagnostics, d => d.Contains("outside what the linkage can reach"));
    }
}
