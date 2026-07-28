using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Drivers and the swept solve (Mechanism.cs): SolveAt pins one joint variable,
/// Sweep continues from the previous converged pose (the four-bar elbow-flip lesson),
/// and a sweep that cannot proceed reports the parameter and leaves the last good
/// pose.
/// </summary>
public class MechanismTests
{
    private static Frame3d At(double x, double y, double z) =>
        Frame3d.FromXY((x, y, z), Vector3d.UnitX, Vector3d.UnitY);

    private static Frame3d Posed(double x, double y, double angle) =>
        Frame3d.FromXY(
            (x, y, 0),
            (Math.Cos(angle), Math.Sin(angle), 0),
            (-Math.Sin(angle), Math.Cos(angle), 0));

    private static Part BoxPart(string name) => new(name, MeshPrimitives.Box(4, 2, 1));

    // ---- driving single joints ----

    [Fact]
    public void SlideDriver_MovesThePrismaticJointToTheTarget()
    {
        var rig = new Assembly("rig");
        var fixedOne = rig.Add(BoxPart("base"));
        var moving = rig.Add(BoxPart("ram"));
        var joint = Joint.Prismatic(
            MateGeometry.Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(moving, (0, 0, 0), Vector3d.UnitZ));
        var mechanism = new Mechanism(rig).Ground(fixedOne).Add(joint);

        var result = mechanism.SolveAt(MechanismDriver.Slide(joint), 7.25);

        Assert.True(result.Converged, result.ToString());
        Assert.Equal(7.25, joint.Displacement, 9);
        Assert.Equal(7.25, moving.Frame.Origin.Z, 9);
        // The driver consumed the joint's one DOF: fully constrained while driven.
        Assert.Equal(0, result.RemainingDegreesOfFreedom);
    }

    [Fact]
    public void AngleDriver_TurnsTheRevoluteJointToTheTarget()
    {
        var rig = new Assembly("rig");
        var fixedOne = rig.Add(BoxPart("base"));
        var moving = rig.Add(BoxPart("crank"));
        var joint = Joint.Revolute(
            MateGeometry.Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(moving, (0, 0, 0), Vector3d.UnitZ));
        var mechanism = new Mechanism(rig).Ground(fixedOne).Add(joint);

        mechanism.SolveAt(MechanismDriver.Angle(joint), Math.PI / 3);

        Assert.Equal(Math.PI / 3, joint.Angle, 9);
        // The body genuinely rotated: its local +X now points 60° around.
        var x = moving.Frame.ToWorldVector(Vector3d.UnitX);
        Assert.Equal(Math.Cos(Math.PI / 3), x.X, 9);
        Assert.Equal(Math.Sin(Math.PI / 3), x.Y, 9);
    }

    [Fact]
    public void SweepThroughFullTurns_UnwrapsTheAngle()
    {
        var rig = new Assembly("rig");
        var fixedOne = rig.Add(BoxPart("base"));
        var moving = rig.Add(BoxPart("crank"));
        var joint = Joint.Revolute(
            MateGeometry.Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(moving, (0, 0, 0), Vector3d.UnitZ));
        var mechanism = new Mechanism(rig).Ground(fixedOne).Add(joint);

        var study = mechanism.Sweep(MechanismDriver.Angle(joint), 0, 4 * Math.PI, frames: 25);

        Assert.True(study.Completed, study.ToString());
        Assert.Equal(25, study.Frames.Count);
        // Two full turns: the unwrapped coordinate reads 4π, not 0.
        Assert.Equal(4 * Math.PI, joint.Angle, 8);
        // And the pose is back where it started.
        Assert.Equal(1, moving.Frame.ToWorldVector(Vector3d.UnitX).X, 9);
    }

    [Fact]
    public void ScrewDrivenThroughTwoTurns_AdvancesTwoPitches()
    {
        var rig = new Assembly("rig");
        var fixedOne = rig.Add(BoxPart("base"));
        var moving = rig.Add(BoxPart("nut"));
        var joint = Joint.Screw(
            MateGeometry.Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(moving, (0, 0, 0), Vector3d.UnitZ),
            pitch: 1.25);
        var mechanism = new Mechanism(rig).Ground(fixedOne).Add(joint);

        var study = mechanism.Sweep(MechanismDriver.Angle(joint), 0, 4 * Math.PI, frames: 17);

        Assert.True(study.Completed, study.ToString());
        Assert.Equal(2 * 1.25, joint.Displacement, 8);
        Assert.Equal(2 * 1.25, moving.Frame.Origin.Z, 8);
    }

    // ---- the four-bar linkage: continuation through the cycle, no branch flip ----

    private const double Crank = 10, Coupler = 35, Rocker = 25, Span = 40;

    /// <summary>The coupler-rocker joint position for a crank angle, elbow-up branch:
    /// intersection of the circle about the crank pin (radius = coupler) with the
    /// circle about the rocker pivot (radius = rocker).</summary>
    private static Vector3d ElbowUp(double theta)
    {
        var a = new Vector3d(Crank * Math.Cos(theta), Crank * Math.Sin(theta), 0);
        var o2 = new Vector3d(Span, 0, 0);
        var toPivot = o2 - a;
        double d = toPivot.Length;
        var u = toPivot / d;
        double along = (d * d + Coupler * Coupler - Rocker * Rocker) / (2 * d);
        double h = Math.Sqrt(Coupler * Coupler - along * along);
        var perp = new Vector3d(-u.Y, u.X, 0);
        return a + u * along + perp * h;
    }

    private static (Mechanism Mechanism, RevoluteJoint CrankPin, Occurrence CouplerLink) FourBar()
    {
        var rig = new Assembly("linkage");
        var frame = rig.Add(BoxPart("frame"));
        var crank = rig.Add(BoxPart("crank"));
        var coupler = rig.Add(BoxPart("coupler"));
        var rocker = rig.Add(BoxPart("rocker"));

        // Author the links EXACTLY assembled on the elbow-up branch at crank angle 0.
        var elbow = ElbowUp(0);
        var crankTip = new Vector3d(Crank, 0, 0);
        crank.Frame = Posed(0, 0, 0);
        coupler.Frame = Posed(crankTip.X, crankTip.Y, Math.Atan2(elbow.Y - crankTip.Y, elbow.X - crankTip.X));
        rocker.Frame = Posed(Span, 0, Math.Atan2(elbow.Y, elbow.X - Span));

        var z = Vector3d.UnitZ;
        var crankPin = Joint.Revolute(
            MateGeometry.Axis(frame, (0, 0, 0), z), MateGeometry.Axis(crank, (0, 0, 0), z), "crank pin");
        var mechanism = new Mechanism(rig)
            .Ground(frame)
            .Add(crankPin)
            .Add(Joint.Revolute(
                MateGeometry.Axis(crank, (Crank, 0, 0), z),
                MateGeometry.Axis(coupler, (0, 0, 0), z), "coupler pin"))
            .Add(Joint.Revolute(
                MateGeometry.Axis(coupler, (Coupler, 0, 0), z),
                MateGeometry.Axis(rocker, (Rocker, 0, 0), z), "elbow pin"))
            .Add(Joint.Revolute(
                MateGeometry.Axis(frame, (Span, 0, 0), z),
                MateGeometry.Axis(rocker, (0, 0, 0), z), "rocker pivot"));
        return (mechanism, crankPin, coupler);
    }

    [Fact]
    public void FourBar_AssemblesWithOneDegreeOfFreedom()
    {
        var (mechanism, _, _) = FourBar();
        var result = mechanism.Assemble();
        Assert.True(result.Converged, result.ToString());
        // Three moving links, one mechanism DOF — the solver's rank sees through the
        // planar linkage's redundant spatial constraints (Grübler would say −2).
        Assert.Equal(18, result.FreeDegreesOfFreedom);
        Assert.Equal(1, result.RemainingDegreesOfFreedom);
    }

    [Fact]
    public void FourBar_SweepsAFullCrankCycleOnOneBranch()
    {
        var (mechanism, crankPin, couplerLink) = FourBar();
        mechanism.Assemble();

        var study = mechanism.Sweep(MechanismDriver.Angle(crankPin), 0, 2 * Math.PI, frames: 73);

        Assert.True(study.Completed, study.ToString());
        foreach (var frame in study.Frames)
        {
            var instance = frame.Instances.Single(i => i.Path == "linkage/coupler");
            var actual = instance.World.TransformPoint(new Vector3d(Coupler, 0, 0));
            var expected = ElbowUp(frame.Value);
            // Staying within solver tolerance of the ELBOW-UP closed form at every
            // frame IS the no-branch-flip assertion: the elbow-down branch sits ~2h
            // (tens of units) away.
            Assert.True(actual.DistanceTo(expected) < 1e-6,
                $"at crank angle {frame.Value:g4}: coupler end {actual} vs elbow-up {expected} " +
                $"(off by {actual.DistanceTo(expected):g3})");
        }
        // Full cycle: the crank came back around, the coordinate unwrapped, and the
        // coupler's pin is back on the crank tip.
        Assert.Equal(2 * Math.PI, crankPin.Angle, 8);
        Assert.Equal(Crank, couplerLink.Frame.Origin.X, 6);
        Assert.Equal(0, couplerLink.Frame.Origin.Y, 6);
    }

    // ---- honest failure ----

    [Fact]
    public void SweepBeyondTheLinkagesReach_ReportsTheParameterAndKeepsTheLastGoodPose()
    {
        // A two-link "dial": a crank pinned to ground and a rod pinned to the crank
        // tip, whose far end is driven along X by a prismatic joint. Driving the far
        // end past crank + rod length is unreachable.
        var rig = new Assembly("rig");
        var ground = rig.Add(BoxPart("ground"));
        var crank = rig.Add(BoxPart("crank"));
        var rod = rig.Add(BoxPart("rod"));
        var slider = rig.Add(BoxPart("slider"));
        // Author exactly assembled at crank angle 90°: tip (0, 5), slider at
        // x = √(20² − 5²) — safely away from the stretched dead centre at x = 25.
        double x0 = Math.Sqrt(20 * 20 - 5 * 5);
        crank.Frame = Posed(0, 0, Math.PI / 2);
        rod.Frame = Posed(0, 5, Math.Atan2(-5, x0));
        slider.Frame = At(x0, 0, 0);

        var z = Vector3d.UnitZ;
        var slide = Joint.Prismatic(
            MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitX),
            MateGeometry.Axis(slider, (0, 0, 0), Vector3d.UnitX), "slide");
        var mechanism = new Mechanism(rig)
            .Ground(ground)
            .Add(Joint.Revolute(MateGeometry.Axis(ground, (0, 0, 0), z), MateGeometry.Axis(crank, (0, 0, 0), z)))
            .Add(Joint.Revolute(MateGeometry.Axis(crank, (5, 0, 0), z), MateGeometry.Axis(rod, (0, 0, 0), z)))
            .Add(Joint.Revolute(MateGeometry.Axis(rod, (20, 0, 0), z), MateGeometry.Axis(slider, (0, 0, 0), z)))
            .Add(slide);
        mechanism.Assemble();

        // The slider can reach at most x = 25 (crank + rod stretched), i.e. a
        // displacement of 25 − x0 ≈ 5.64. Ask for 8 — unreachable.
        var study = mechanism.Sweep(MechanismDriver.Slide(slide), 0, 8, frames: 17);

        Assert.False(study.Completed);
        Assert.NotNull(study.FailedAt);
        // It got most of the way to the stretch limit, then stopped honestly.
        double limit = 25 - x0;
        Assert.True(study.FailedAt > 4 && study.FailedAt <= limit + 1e-6,
            $"failed at {study.FailedAt}, expected near the reach limit {limit:g4}");
        Assert.NotEmpty(study.Diagnostics);
        // The last good pose is intact: the driven coordinate matches FailedAt.
        Assert.Equal(study.FailedAt!.Value, slide.Displacement, 6);
    }

    [Fact]
    public void Driver_RefusesAVariableTheJointLocks()
    {
        var rig = new Assembly("rig");
        var fixedOne = rig.Add(BoxPart("base"));
        var moving = rig.Add(BoxPart("ram"));
        var prismatic = Joint.Prismatic(
            MateGeometry.Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(moving, (0, 0, 0), Vector3d.UnitZ));
        var revolute = Joint.Revolute(
            MateGeometry.Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(moving, (0, 0, 0), Vector3d.UnitZ));
        Assert.Throws<ArgumentException>(() => MechanismDriver.Angle(prismatic));
        Assert.Throws<ArgumentException>(() => MechanismDriver.Slide(revolute));
    }
}
