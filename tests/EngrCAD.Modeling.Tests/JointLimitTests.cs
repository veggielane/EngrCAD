using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Joint limits: min/max stops on revolute and prismatic joints. A solve past a stop
/// is rolled back and refused naming the joint; a sweep walks up to the stop and
/// reports it.
/// </summary>
public class JointLimitTests
{
    private static Part BoxPart(string name) => new(name, MeshPrimitives.Box(4, 2, 1));

    private static (Mechanism Mechanism, RevoluteJoint Hinge) HingeRig()
    {
        var rig = new Assembly("rig");
        var fixedOne = rig.Add(BoxPart("base"));
        var moving = rig.Add(BoxPart("door"));
        var hinge = Joint.Revolute(
            MateGeometry.Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(moving, (0, 0, 0), Vector3d.UnitZ), "hinge")
            .WithLimits(-45, 45);
        return (new Mechanism(rig).Ground(fixedOne).Add(hinge), hinge);
    }

    [Fact]
    public void WithinTheStops_SolvesNormally()
    {
        var (mechanism, hinge) = HingeRig();
        mechanism.SolveAt(MechanismDriver.Angle(hinge), 30 * Math.PI / 180);
        Assert.Equal(30, hinge.AngleDegrees, 8);
    }

    [Fact]
    public void PastTheStop_RefusesNamingTheJoint_AndRollsBack()
    {
        var (mechanism, hinge) = HingeRig();
        mechanism.SolveAt(MechanismDriver.Angle(hinge), 30 * Math.PI / 180);

        var exception = Assert.Throws<MateSolveException>(
            () => mechanism.SolveAt(MechanismDriver.Angle(hinge), 60 * Math.PI / 180));

        Assert.Contains("hinge", exception.Message);
        Assert.Contains("past its stop", exception.Message);
        // Rolled back: the door is exactly where the last good solve left it.
        Assert.Equal(30, hinge.AngleDegrees, 8);
    }

    [Fact]
    public void ASweep_WalksUpToTheStop_AndReportsIt()
    {
        var (mechanism, hinge) = HingeRig();

        var study = mechanism.Sweep(MechanismDriver.Angle(hinge), 0, Math.PI / 2, frames: 19);

        Assert.False(study.Completed);
        Assert.False(study.Singular);
        Assert.Contains(study.Diagnostics, d => d.Contains("past its stop") && d.Contains("hinge"));
        // It reached the stop itself (within the sweep's minimum subdivision).
        Assert.True(Math.Abs(study.FailedAt!.Value - Math.PI / 4) < Math.PI / 2 / 1024,
            $"failed at {study.FailedAt:g6}, stop at {Math.PI / 4:g6}");
        Assert.Equal(45, hinge.AngleDegrees, 3);
    }

    [Fact]
    public void PrismaticStops_WorkTheSameWay()
    {
        var rig = new Assembly("rig");
        var fixedOne = rig.Add(BoxPart("base"));
        var moving = rig.Add(BoxPart("ram"));
        var slide = Joint.Prismatic(
            MateGeometry.Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(moving, (0, 0, 0), Vector3d.UnitZ), "ram slide")
            .WithLimits(-2, 10);
        var mechanism = new Mechanism(rig).Ground(fixedOne).Add(slide);

        mechanism.SolveAt(MechanismDriver.Slide(slide), 10);   // exactly on the stop: legal
        Assert.Equal(10, slide.Displacement, 8);

        var exception = Assert.Throws<MateSolveException>(
            () => mechanism.SolveAt(MechanismDriver.Slide(slide), 10.5));
        Assert.Contains("ram slide", exception.Message);
        Assert.Equal(10, slide.Displacement, 8);
    }

    [Fact]
    public void Limits_RejectAnEmptyRange()
    {
        var rig = new Assembly("rig");
        var fixedOne = rig.Add(BoxPart("base"));
        var moving = rig.Add(BoxPart("door"));
        Assert.Throws<ArgumentException>(() => Joint.Revolute(
            MateGeometry.Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(moving, (0, 0, 0), Vector3d.UnitZ)).WithLimits(30, 30));
    }
}
