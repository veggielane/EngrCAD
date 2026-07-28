using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Higher pairs (HigherPairs.cs): gear ratios, belts and cams as scalar coupling rows
/// in the same residual vector as the mates — no new solver.
/// </summary>
public class HigherPairTests
{
    private static Frame3d At(double x, double y, double z) =>
        Frame3d.FromXY((x, y, z), Vector3d.UnitX, Vector3d.UnitY);

    private static Part BoxPart(string name) => new(name, MeshPrimitives.Box(4, 2, 1));

    /// <summary>Two revolute "gears" on parallel axes 30 apart, plus their mechanism.</summary>
    private static (Mechanism Mechanism, RevoluteJoint A, RevoluteJoint B) GearRig()
    {
        var rig = new Assembly("gearbox");
        var housing = rig.Add(BoxPart("housing"));
        var gearA = rig.Add(BoxPart("gearA"), At(0, 0, 0));
        var gearB = rig.Add(BoxPart("gearB"), At(30, 0, 0));
        var z = Vector3d.UnitZ;
        var pinA = Joint.Revolute(
            MateGeometry.Axis(housing, (0, 0, 0), z), MateGeometry.Axis(gearA, (0, 0, 0), z), "pin A");
        var pinB = Joint.Revolute(
            MateGeometry.Axis(housing, (30, 0, 0), z), MateGeometry.Axis(gearB, (0, 0, 0), z), "pin B");
        var mechanism = new Mechanism(rig).Ground(housing).Add(pinA).Add(pinB);
        return (mechanism, pinA, pinB);
    }

    [Fact]
    public void GearPair_CounterRotatesAtTheToothRatio()
    {
        var (mechanism, pinA, pinB) = GearRig();
        mechanism.Add(Coupling.Gear(pinA, pinB, teethA: 20, teethB: 40));

        // The coupling consumed one of the two spins: 1 mechanism DOF.
        Assert.Equal(1, mechanism.Assemble().RemainingDegreesOfFreedom);

        mechanism.SolveAt(MechanismDriver.Angle(pinA), Math.PI / 2);
        Assert.Equal(Math.PI / 2, pinA.Angle, 9);
        Assert.Equal(-Math.PI / 4, pinB.Angle, 9);

        // And through full turns: the unwrapped coordinates keep the ratio.
        var study = mechanism.Sweep(MechanismDriver.Angle(pinA), Math.PI / 2, 4 * Math.PI, frames: 25);
        Assert.True(study.Completed, study.ToString());
        Assert.Equal(-2 * Math.PI, pinB.Angle, 8);
    }

    [Fact]
    public void InternalMeshAndBelt_TurnTheSameWay()
    {
        var (mechanism, pinA, pinB) = GearRig();
        mechanism.Add(Coupling.Belt(pinA, pinB, radiusA: 24, radiusB: 8));
        mechanism.SolveAt(MechanismDriver.Angle(pinA), 0.5);
        Assert.Equal(1.5, pinB.Angle, 9);
    }

    [Fact]
    public void GearRates_FollowTheRatioExactly()
    {
        var (mechanism, pinA, pinB) = GearRig();
        mechanism.Add(Coupling.Gear(pinA, pinB, teethA: 20, teethB: 40));

        var rates = mechanism.RatesAt(MechanismDriver.Angle(pinA), 0.7, rate: 3.0, acceleration: 1.1);

        Assert.Equal(3.0, rates.For(pinA).AngleRate, 8);
        Assert.Equal(-1.5, rates.For(pinB).AngleRate, 8);
        Assert.Equal(-0.55, rates.For(pinB).AngleAcceleration, 7);
        Assert.Equal(-1.5, rates.For("gearB").AngularVelocity.Z, 8);
    }

    // ---- cams ----

    /// <summary>A cam on a revolute and a follower on a vertical prismatic.</summary>
    private static (Mechanism Mechanism, RevoluteJoint CamPin, PrismaticJoint FollowerSlide) CamRig()
    {
        var rig = new Assembly("camshaft");
        var frame = rig.Add(BoxPart("frame"));
        var cam = rig.Add(BoxPart("cam"));
        var follower = rig.Add(BoxPart("follower"), At(0, 20, 0));
        var z = Vector3d.UnitZ;
        var camPin = Joint.Revolute(
            MateGeometry.Axis(frame, (0, 0, 0), z), MateGeometry.Axis(cam, (0, 0, 0), z), "cam pin");
        var followerSlide = Joint.Prismatic(
            MateGeometry.Axis(frame, (0, 20, 0), Vector3d.UnitY),
            MateGeometry.Axis(follower, (0, 0, 0), Vector3d.UnitY), "follower");
        var mechanism = new Mechanism(rig).Ground(frame).Add(camPin).Add(followerSlide);
        return (mechanism, camPin, followerSlide);
    }

    [Fact]
    public void HarmonicCam_LiftsTheFollowerByTheLaw()
    {
        var (mechanism, camPin, followerSlide) = CamRig();
        mechanism.Add(Coupling.Cam(camPin, followerSlide, CamLaw.Harmonic(amplitude: 6)));

        var study = mechanism.Sweep(MechanismDriver.Angle(camPin), 0, 2 * Math.PI, frames: 25);

        Assert.True(study.Completed, study.ToString());
        foreach (var frame in study.Frames)
        {
            double expected = 3 * (1 - Math.Cos(frame.Value));
            var follower = frame.Instances.Single(i => i.Path == "camshaft/follower");
            double lift = follower.World.TransformPoint(Vector3d.Zero).Y - 20;
            Assert.True(Math.Abs(lift - expected) < 1e-8,
                $"at cam angle {frame.Value:g4}: lift {lift:g6} vs law {expected:g6}");
        }
    }

    [Fact]
    public void HarmonicCamRates_CarryTheSlopeAndCurvature()
    {
        var (mechanism, camPin, followerSlide) = CamRig();
        mechanism.Add(Coupling.Cam(camPin, followerSlide, CamLaw.Harmonic(amplitude: 6)));
        const double omega = 2.5, alpha = -0.9, target = 1.1;

        var rates = mechanism.RatesAt(MechanismDriver.Angle(camPin), target, rate: omega, acceleration: alpha);

        double slope = 3 * Math.Sin(target);
        double curvature = 3 * Math.Cos(target);
        Assert.Equal(slope * omega, rates.For(followerSlide).SlideRate, 7);
        Assert.Equal(curvature * omega * omega + slope * alpha, rates.For(followerSlide).SlideAcceleration, 6);
    }

    [Fact]
    public void SketchCam_AnEccentricCircle_MatchesItsClosedForm()
    {
        // A circular profile of radius 8 centred 3 off the pivot: the textbook
        // eccentric cam, whose radial law is r(φ) = e·cosφ + √(a² − e²·sin²φ).
        const double a = 8, e = 3;
        var law = CamLaw.FromSketch(Sketch.Circle(new Vector2d(e, 0), a), followerAngle: 0, samples: 720);

        for (double theta = 0; theta < 2 * Math.PI; theta += 0.37)
        {
            // The profile rotates by θ, so the follower reads local angle −θ; cosine
            // is even, so the law equals r(θ) directly.
            double expected = e * Math.Cos(theta) + Math.Sqrt(a * a - e * e * Math.Sin(theta) * Math.Sin(theta));
            law.Evaluate(theta, out double lift, out double slope, out _);
            Assert.True(Math.Abs(lift - expected) < 1e-6, $"lift at {theta:g4}: {lift} vs {expected}");
            double s = Math.Sin(theta), c = Math.Cos(theta);
            double root = Math.Sqrt(a * a - e * e * s * s);
            double expectedSlope = -e * s - e * e * s * c / root;
            Assert.True(Math.Abs(slope - expectedSlope) < 1e-4, $"slope at {theta:g4}: {slope} vs {expectedSlope}");
        }
    }

    [Fact]
    public void SketchCam_DrivesAFollowerThroughTheSolver()
    {
        const double a = 8, e = 3;
        var (mechanism, camPin, followerSlide) = CamRig();
        mechanism.Add(Coupling.Cam(
            camPin, followerSlide, CamLaw.FromSketch(Sketch.Circle(new Vector2d(e, 0), a))));

        mechanism.SolveAt(MechanismDriver.Angle(camPin), 1.3);

        double r0 = e + a;   // lift at construction (θ = 0)
        double expected = e * Math.Cos(1.3) + Math.Sqrt(a * a - e * e * Math.Sin(1.3) * Math.Sin(1.3)) - r0;
        Assert.True(Math.Abs(followerSlide.Displacement - expected) < 1e-6,
            $"follower at {followerSlide.Displacement:g6} vs closed form {expected:g6}");
    }

    // ---- refusals ----

    [Fact]
    public void Couplings_RefuseLockedCoordinates()
    {
        var (mechanism, camPin, followerSlide) = CamRig();
        var (_, otherPin, _) = CamRig();
        _ = mechanism;
        // A prismatic joint cannot be spin-coupled; a revolute cannot follow a cam.
        Assert.Throws<ArgumentException>(() => Coupling.Gear(camPin, followerSlide, 20, 40));
        Assert.Throws<ArgumentException>(() => Coupling.Cam(camPin, otherPin, CamLaw.Harmonic(1)));
    }

    [Fact]
    public void Coupling_MustReferenceJointsAlreadyInTheMechanism()
    {
        var (mechanism, camPin, _) = CamRig();
        var rig2 = new Assembly("other");
        var f2 = rig2.Add(BoxPart("f"));
        var m2 = rig2.Add(BoxPart("m"));
        var foreign = Joint.Revolute(
            MateGeometry.Axis(f2, (0, 0, 0), Vector3d.UnitZ), MateGeometry.Axis(m2, (0, 0, 0), Vector3d.UnitZ));
        Assert.Throws<ArgumentException>(
            () => mechanism.Add(Coupling.Ratio(camPin, foreign, 2)));
    }
}
