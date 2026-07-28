using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The joints vocabulary (Joints.cs): each joint is a named combination of ordinary
/// mates whose NOMINAL degree-of-freedom count is asserted against the solver's
/// measured rank — so these tests exercise both the definitions and the assertion
/// machinery itself.
/// </summary>
public class JointTests
{
    private static Frame3d At(double x, double y, double z) =>
        Frame3d.FromXY((x, y, z), Vector3d.UnitX, Vector3d.UnitY);

    private static Frame3d RotatedAboutZ(double x, double y, double z, double radians) =>
        Frame3d.FromXY(
            (x, y, z),
            (Math.Cos(radians), Math.Sin(radians), 0),
            (-Math.Sin(radians), Math.Cos(radians), 0));

    private static Part BoxPart(string name) => new(name, MeshPrimitives.Box(10, 6, 2));

    private static (Assembly Rig, Occurrence Base, Occurrence Moving) Rig(Frame3d? movingAt = null)
    {
        var rig = new Assembly("rig");
        var fixedOne = rig.Add(BoxPart("base"));
        var moving = rig.Add(BoxPart("arm"), movingAt ?? At(1, 2, 3));
        return (rig, fixedOne, moving);
    }

    private static MateRef Axis(Occurrence occurrence, in Vector3d origin, in Vector3d direction) =>
        MateGeometry.Axis(occurrence, origin, direction);

    // ---- nominal DOF asserted against the solver's measured rank ----

    [Theory]
    [InlineData("revolute", 1)]
    [InlineData("prismatic", 1)]
    [InlineData("cylindrical", 2)]
    [InlineData("spherical", 3)]
    [InlineData("planar", 3)]
    [InlineData("screw", 1)]
    [InlineData("fixed", 0)]
    public void EveryJointKind_MeasuresItsNominalDof(string kind, int expected)
    {
        var (rig, fixedOne, moving) = Rig();
        var a = Axis(fixedOne, (0, 0, 1), Vector3d.UnitZ);
        var b = Axis(moving, (0, 0, -1), Vector3d.UnitZ);
        Joint joint = kind switch
        {
            "revolute" => Joint.Revolute(a, b),
            "prismatic" => Joint.Prismatic(a, b),
            "cylindrical" => Joint.Cylindrical(a, b),
            "spherical" => Joint.Spherical(a, b),
            "planar" => Joint.Planar(
                MateGeometry.Axis(fixedOne, (0, 0, 1), Vector3d.UnitZ),
                MateGeometry.Axis(moving, (0, 0, -1), -Vector3d.UnitZ)),
            "screw" => Joint.Screw(a, b, pitch: 1.5),
            _ => Joint.Fixed(a, b),
        };

        Assert.Equal(expected, joint.NominalDegreesOfFreedom);
        var before = moving.Frame;
        var report = joint.VerifyDegreesOfFreedom(rig);
        Assert.Equal(expected, report.RemainingDegreesOfFreedom);
        // The probe is side-effect-free: the occurrence pose it solved with is restored.
        Assert.Equal(before.Origin, moving.Frame.Origin);
        Assert.Equal(before.X, moving.Frame.X);
    }

    [Fact]
    public void ScrewCoupling_RemovesExactlyOneDofFromCylindrical()
    {
        // Identical axes; the ONLY difference is the pitch coupling row. Cylindrical
        // measures 2 free, screw 1 — the coupling is genuinely in the residual vector.
        var (rig, fixedOne, moving) = Rig();
        var a = Axis(fixedOne, (0, 0, 1), Vector3d.UnitZ);
        var b = Axis(moving, (0, 0, -1), Vector3d.UnitZ);
        Assert.Equal(2, Joint.Cylindrical(a, b).VerifyDegreesOfFreedom(rig).RemainingDegreesOfFreedom);
        Assert.Equal(1, Joint.Screw(a, b, 2.0).VerifyDegreesOfFreedom(rig).RemainingDegreesOfFreedom);
    }

    // ---- joint coordinates ----

    [Fact]
    public void JointCoordinates_ReadZeroAtConstruction()
    {
        var (_, fixedOne, moving) = Rig(At(4, -2, 7));
        var joint = Joint.Cylindrical(
            Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
            Axis(moving, (1, 1, 0), (0.3, -0.2, 1)));
        Assert.Equal(0, joint.Angle, 12);
        Assert.Equal(0, joint.Displacement, 12);
    }

    [Fact]
    public void Angle_ReadsAnImposedRotationAboutTheAxis()
    {
        var (_, fixedOne, moving) = Rig(At(0, 0, 0));
        var joint = Joint.Revolute(
            Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
            Axis(moving, (0, 0, 0), Vector3d.UnitZ));

        moving.Frame = RotatedAboutZ(0, 0, 0, Math.PI / 6);
        Assert.Equal(Math.PI / 6, joint.Angle, 12);

        moving.Frame = RotatedAboutZ(0, 0, 0, -Math.PI / 3);
        Assert.Equal(-Math.PI / 3, joint.Angle, 12);
        Assert.Equal(-60, joint.AngleDegrees, 10);
    }

    [Fact]
    public void Displacement_ReadsAnImposedSlideAlongTheAxis()
    {
        var (_, fixedOne, moving) = Rig(At(0, 0, 0));
        var joint = Joint.Prismatic(
            Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
            Axis(moving, (0, 0, 0), Vector3d.UnitZ));

        moving.Frame = At(0, 0, 5);
        Assert.Equal(5, joint.Displacement, 12);
    }

    [Fact]
    public void UnwrappedAngle_KeepsCountingThroughTheHalfTurnSeam()
    {
        // Committed increments accumulate: 170° then +20° more lands at 190°, not −170°.
        var state = new JointSweepState();
        state.Commit(170 * Math.PI / 180);
        state.Commit(-170 * Math.PI / 180);
        Assert.Equal(190 * Math.PI / 180, state.AccumulatedAngle, 12);
        // And an uncommitted read is continuous relative to the last commit.
        Assert.Equal(195 * Math.PI / 180, state.Unwrapped(-165 * Math.PI / 180), 12);
    }

    // ---- the screw pair through the real solver ----

    [Fact]
    public void ScrewJoint_CouplesSlideToSpinInTheSolver()
    {
        var (rig, fixedOne, moving) = Rig(At(0, 0, 0));
        var joint = Joint.Screw(
            Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
            Axis(moving, (0, 0, 0), Vector3d.UnitZ),
            pitch: 2.0);

        // Displace the nut along the axis so the pitch coupling is violated (z = 3,
        // θ = 0), then solve: the converged pose must sit back on the helix.
        moving.Frame = At(0, 0, 3);
        var mates = new MateSet(rig).Ground(fixedOne);
        foreach (var mate in joint.Mates)
            mates.Add(mate);
        var result = mates.TrySolve(new MateSolverSettings(), joint.Couplings);

        Assert.True(result.Converged, result.ToString());
        Assert.True(
            Math.Abs(joint.Displacement - joint.AdvancePerRadian * joint.Angle) <= 1e-9,
            $"z = {joint.Displacement}, θ = {joint.Angle}: off the helix by " +
            $"{joint.Displacement - joint.AdvancePerRadian * joint.Angle}");
        // One DOF remains: position along the helix.
        Assert.Equal(1, result.RemainingDegreesOfFreedom);
    }

    // ---- refusals ----

    [Fact]
    public void Joint_RejectsTwoWorldFixedEnds()
    {
        var a = MateGeometry.World((0, 0, 0), Vector3d.UnitZ);
        var b = MateGeometry.World((0, 0, 5), Vector3d.UnitZ);
        Assert.Throws<ArgumentException>(() => Joint.Revolute(a, b));
    }

    [Fact]
    public void Joint_RejectsBothEndsOnOneBody()
    {
        var (_, fixedOne, _) = Rig();
        var a = Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ);
        var b = Axis(fixedOne, (5, 0, 0), Vector3d.UnitZ);
        Assert.Throws<ArgumentException>(() => Joint.Cylindrical(a, b));
    }

    [Fact]
    public void Screw_RejectsZeroPitch()
    {
        var (_, fixedOne, moving) = Rig();
        Assert.Throws<ArgumentOutOfRangeException>(() => Joint.Screw(
            Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
            Axis(moving, (0, 0, 0), Vector3d.UnitZ),
            pitch: 0));
    }

    [Fact]
    public void AxisJoint_RejectsAMissingDirection()
    {
        var (_, fixedOne, moving) = Rig();
        Assert.Throws<ArgumentException>(() => Joint.Revolute(
            MateGeometry.Point(fixedOne, (0, 0, 0)),
            Axis(moving, (0, 0, 0), Vector3d.UnitZ)));
    }

    // ---- semantic references: the same refs mates use ----

    [Fact]
    public void Revolute_FromCylindricalFaceSelectors_WorksEndToEnd()
    {
        var lower = new Part("lower", Shape.Box(40, 30, 6).Drill(
            HoleSpec.Simple(6), [new(0, 0)], 8,
            SketchPlane.At((0, 0, 3), Vector3d.UnitX, Vector3d.UnitY)));
        var upper = new Part("upper", Shape.Box(40, 30, 6).Drill(
            HoleSpec.Simple(6), [new(0, 0)], 8,
            SketchPlane.At((0, 0, 3), Vector3d.UnitX, Vector3d.UnitY)));
        var rig = new Assembly("rig");
        var baseOcc = rig.Add(lower);
        var lidOcc = rig.Add(upper, At(3, 1, 9));

        var joint = Joint.Revolute(
            MateGeometry.CylindricalFace(baseOcc, FaceRef.One(FaceSetRef.Cylindrical())),
            MateGeometry.CylindricalFace(lidOcc, FaceRef.One(FaceSetRef.Cylindrical())),
            "hinge pin");

        var report = joint.VerifyDegreesOfFreedom(rig);
        Assert.Equal(1, report.RemainingDegreesOfFreedom);
    }
}
