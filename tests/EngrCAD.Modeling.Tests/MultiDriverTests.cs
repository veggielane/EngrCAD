using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Driving MORE than one joint variable at once. A 2-DOF mechanism — a cylindrical
/// joint's spin AND slide, a two-hinge arm — has no single answer under one driver, and
/// the honest response is a target per actuated variable rather than a pose picked by
/// the solver's seed. The residual machinery already carried N driver rows; what is new
/// is the API and a sweep over a parameter VECTOR.
/// <para>The sweep is a straight line through driver space (every driver runs its own
/// From→To over one shared s), which is what keeps the continuation logic identical to
/// the single-driver case: one parameter, one step to halve.</para>
/// </summary>
public class MultiDriverTests
{
    private static Part BoxPart(string name) => new(name, MeshPrimitives.Box(4, 2, 1));

    /// <summary>A cylindrical joint: two free coordinates on ONE joint — the smallest
    /// honest 2-DOF fixture, and the case where two drivers on the same joint must be
    /// allowed (different variables) while the same variable twice must not.</summary>
    private static (Mechanism Mechanism, CylindricalJoint Joint, Occurrence Moving) Cylindrical()
    {
        var rig = new Assembly("rig");
        var ground = rig.Add(BoxPart("base"));
        var moving = rig.Add(BoxPart("sleeve"));
        var joint = Joint.Cylindrical(
            MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(moving, (0, 0, 0), Vector3d.UnitZ));
        return (new Mechanism(rig).Ground(ground).Add(joint), joint, moving);
    }

    [Fact]
    public void TwoDriversOnOneCylindricalJointPinBothCoordinates()
    {
        var (mechanism, joint, moving) = Cylindrical();

        var result = mechanism.SolveAt(
        [
            (MechanismDriver.Angle(joint), Math.PI / 4),
            (MechanismDriver.Slide(joint), 3.5),
        ]);

        Assert.True(result.Converged, result.ToString());
        Assert.Equal(Math.PI / 4, joint.Angle, 9);
        Assert.Equal(3.5, joint.Displacement, 9);
        // Both DOF consumed: a 2-DOF mechanism under two drivers is fully constrained,
        // which is exactly what one driver could not deliver.
        Assert.Equal(0, result.RemainingDegreesOfFreedom);
        Assert.Equal(3.5, moving.Frame.Origin.Z, 9);
    }

    [Fact]
    public void OneDriverLeavesTheOtherCoordinateFree()
    {
        var (mechanism, joint, _) = Cylindrical();
        var result = mechanism.SolveAt(MechanismDriver.Angle(joint), Math.PI / 4);

        Assert.True(result.Converged, result.ToString());
        Assert.Equal(1, result.RemainingDegreesOfFreedom);   // the slide is still a family
    }

    [Fact]
    public void DrivingOneCoordinateTwiceIsRefusedByName()
    {
        var (mechanism, joint, _) = Cylindrical();
        var error = Assert.Throws<ArgumentException>(() => mechanism.SolveAt(
        [
            (MechanismDriver.Angle(joint), 0.5),
            (MechanismDriver.Angle(joint), 0.9),
        ]));
        Assert.Contains("driven twice", error.Message);
        Assert.Contains(joint.Name, error.Message);
    }

    [Fact]
    public void AnEmptyDriverSetIsRefused()
    {
        var (mechanism, _, _) = Cylindrical();
        Assert.Throws<ArgumentException>(() => mechanism.SolveAt(Array.Empty<DriverTarget>()));
        Assert.Throws<ArgumentException>(() => mechanism.Sweep(Array.Empty<DriverRange>()));
    }

    [Fact]
    public void ASweepMovesEveryDriverAlongOneParameterAndRecordsEveryValue()
    {
        var (mechanism, joint, moving) = Cylindrical();
        var spin = MechanismDriver.Angle(joint);
        var slide = MechanismDriver.Slide(joint);

        // A helix: one full turn while sliding 10 — the coordinated motion a grid of
        // combinations would not describe.
        var study = mechanism.Sweep([(spin, 0, Math.Tau), (slide, 0, 10.0)], frames: 21);

        Assert.True(study.Completed, study.ToString());
        Assert.Equal(21, study.Frames.Count);
        Assert.Equal([spin, slide], study.Drivers);
        Assert.Same(spin, study.Driver);

        for (int i = 0; i < study.Frames.Count; i++)
        {
            double s = i / 20.0;
            var frame = study.Frames[i];
            Assert.Equal(2, frame.Values.Count);
            Assert.Equal(Math.Tau * s, frame.Values[0], 9);
            Assert.Equal(10 * s, frame.Values[1], 9);
            // Value stays the FIRST driver's, so a single-driver consumer is unchanged.
            Assert.Equal(frame.Values[0], frame.Value, 12);
        }
        Assert.Equal(Math.Tau, joint.Angle, 8);
        Assert.Equal(10, moving.Frame.Origin.Z, 8);
    }

    [Fact]
    public void ASingleDriverSweepStillReportsOneValuePerFrame()
    {
        var (mechanism, joint, _) = Cylindrical();
        var study = mechanism.Sweep(MechanismDriver.Angle(joint), 0, 1, frames: 5);

        Assert.Single(study.Drivers);
        foreach (var frame in study.Frames)
            Assert.Equal([frame.Value], frame.Values);
    }

    [Fact]
    public void RatesUnderTwoDriversAnswerRatherThanRefuse()
    {
        var (mechanism, joint, moving) = Cylindrical();
        var spin = MechanismDriver.Angle(joint);
        var slide = MechanismDriver.Slide(joint);

        // With ONE driver the pose is under-constrained and rates are a family, which
        // the solver refuses to call an answer — the reason the multi-driver form has
        // to exist at all.
        Assert.Throws<MechanismException>(() => mechanism.RatesAt(spin, 0.3, rate: 1));

        var rates = mechanism.RatesAt(
        [
            new DriverMotion(spin, 0.3, Rate: 2),
            new DriverMotion(slide, 1.5, Rate: 5),
        ]);

        var sleeve = rates.For("sleeve");
        // Pure axial slide at 5, plus a spin about Z at 2 rad/s: the origin is ON the
        // axis, so its velocity is the slide alone and the angular rate is the spin.
        Assert.Equal(0, sleeve.Velocity.X, 8);
        Assert.Equal(0, sleeve.Velocity.Y, 8);
        Assert.Equal(5, sleeve.Velocity.Z, 8);
        Assert.Equal(2, sleeve.AngularVelocity.Z, 8);
    }

    [Fact]
    public void RatesDoNotLeakIntoTheNextPlainSolve()
    {
        var (mechanism, joint, _) = Cylindrical();
        var spin = MechanismDriver.Angle(joint);
        var slide = MechanismDriver.Slide(joint);
        mechanism.RatesAt([new DriverMotion(spin, 0.3, Rate: 2), new DriverMotion(slide, 1.5, Rate: 5)]);

        // A rates query is a question, not a state change: the drivers must be back to
        // rest or the NEXT solve carries a velocity nobody asked for.
        var again = mechanism.SolveAt([(spin, 0.3), (slide, 1.5)]);
        Assert.True(again.Converged, again.ToString());
        Assert.Equal(0.3, joint.Angle, 9);
        Assert.Equal(1.5, joint.Displacement, 9);
    }
}
