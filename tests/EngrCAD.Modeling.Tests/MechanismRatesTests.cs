using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Velocities and accelerations from the analytic Jacobian (MateSolverRates.cs),
/// checked against the slider-crank's closed form — the acceptance model for exact
/// first- AND second-order kinematics.
/// </summary>
public class MechanismRatesTests
{
    private const double R = 5, L = 20;

    private static Frame3d At(double x, double y, double z) =>
        Frame3d.FromXY((x, y, z), Vector3d.UnitX, Vector3d.UnitY);

    private static Frame3d Posed(double x, double y, double angle) =>
        Frame3d.FromXY(
            (x, y, 0),
            (Math.Cos(angle), Math.Sin(angle), 0),
            (-Math.Sin(angle), Math.Cos(angle), 0));

    private static Part BoxPart(string name) => new(name, MeshPrimitives.Box(4, 2, 1));

    /// <summary>Slider-crank authored exactly assembled at crank angle 90° (joint
    /// coordinate zero = TRUE crank angle π/2).</summary>
    private static (Mechanism Mechanism, RevoluteJoint CrankPin, PrismaticJoint Slide) SliderCrank()
    {
        var rig = new Assembly("engine");
        var ground = rig.Add(BoxPart("ground"));
        var crank = rig.Add(BoxPart("crank"));
        var rod = rig.Add(BoxPart("rod"));
        var slider = rig.Add(BoxPart("slider"));
        double x0 = Math.Sqrt(L * L - R * R);
        crank.Frame = Posed(0, 0, Math.PI / 2);
        rod.Frame = Posed(0, R, Math.Atan2(-R, x0));
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
            .Add(Joint.Revolute(MateGeometry.Axis(crank, (R, 0, 0), z), MateGeometry.Axis(rod, (0, 0, 0), z), "wrist"))
            .Add(Joint.Revolute(MateGeometry.Axis(rod, (L, 0, 0), z), MateGeometry.Axis(slider, (0, 0, 0), z), "pin"))
            .Add(slide);
        mechanism.Assemble();
        return (mechanism, crankPin, slide);
    }

    /// <summary>Slider position and its θ-derivatives at TRUE crank angle θ:
    /// x = R cos θ + √(L² − R² sin²θ).</summary>
    private static (double X, double XPrime, double XSecond) ClosedForm(double theta)
    {
        double s = Math.Sin(theta), c = Math.Cos(theta);
        double root = Math.Sqrt(L * L - R * R * s * s);
        double x = R * c + root;
        double xp = -R * s - R * R * s * c / root;
        double xpp = -R * c - R * R * ((c * c - s * s) / root + R * R * s * s * c * c / (root * root * root));
        return (x, xp, xpp);
    }

    [Theory]
    [InlineData(-0.9)]
    [InlineData(-0.3)]
    [InlineData(0.4)]
    [InlineData(1.1)]
    public void SliderCrank_VelocityAndAcceleration_MatchTheClosedForm(double target)
    {
        var (mechanism, crankPin, slide) = SliderCrank();
        const double omega = 2.0;
        double theta = Math.PI / 2 + target;   // joint zero is the 90° authoring pose

        var rates = mechanism.RatesAt(MechanismDriver.Angle(crankPin), target, rate: omega);

        var (x, xp, xpp) = ClosedForm(theta);
        var slider = rates.For("slider");
        Assert.Equal(omega * xp, slider.Velocity.X, 6);
        Assert.Equal(0, slider.Velocity.Y, 6);
        Assert.Equal(0, slider.Velocity.Z, 6);
        Assert.Equal(omega * omega * xpp, slider.Acceleration.X, 6);
        Assert.Equal(0, slider.Acceleration.Y, 5);

        // The crank spins at exactly the driver rate, about +Z.
        var crank = rates.For("crank");
        Assert.Equal(omega, crank.AngularVelocity.Z, 8);
        Assert.Equal(0, crank.AngularVelocity.X, 8);
        Assert.Equal(0, crank.AngularAcceleration.Z, 6);

        // Joint-coordinate rates agree with the same closed form.
        Assert.Equal(omega, rates.For(crankPin).AngleRate, 8);
        Assert.Equal(omega * xp, rates.For(slide).SlideRate, 6);
        Assert.Equal(omega * omega * xpp, rates.For(slide).SlideAcceleration, 5);

        // Sanity: the pose itself matches (the driver drove where it claimed).
        Assert.Equal(x - Math.Sqrt(L * L - R * R), slide.Displacement, 7);
    }

    [Fact]
    public void DriverAcceleration_AddsTheFirstOrderTerm()
    {
        var (mechanism, crankPin, slide) = SliderCrank();
        const double omega = 0.7, alpha = 1.3;
        const double target = 0.4;
        double theta = Math.PI / 2 + target;

        var rates = mechanism.RatesAt(MechanismDriver.Angle(crankPin), target, rate: omega, acceleration: alpha);

        var (_, xp, xpp) = ClosedForm(theta);
        // ẍ = α·x′(θ) + ω²·x″(θ).
        Assert.Equal(alpha * xp + omega * omega * xpp, rates.For("slider").Acceleration.X, 6);
        Assert.Equal(alpha, rates.For(crankPin).AngleAcceleration, 7);
        Assert.Equal(alpha * xp + omega * omega * xpp, rates.For(slide).SlideAcceleration, 5);
    }

    [Fact]
    public void ScrewRates_FollowThePitchExactly()
    {
        var rig = new Assembly("rig");
        var fixedOne = rig.Add(BoxPart("base"));
        var moving = rig.Add(BoxPart("nut"));
        var joint = Joint.Screw(
            MateGeometry.Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(moving, (0, 0, 0), Vector3d.UnitZ),
            pitch: 1.5);
        var mechanism = new Mechanism(rig).Ground(fixedOne).Add(joint);
        const double omega = 3.0, alpha = -0.8;

        var rates = mechanism.RatesAt(MechanismDriver.Angle(joint), 1.0, rate: omega, acceleration: alpha);

        double k = 1.5 / (2 * Math.PI);
        Assert.Equal(k * omega, rates.For(joint).SlideRate, 8);
        Assert.Equal(k * alpha, rates.For(joint).SlideAcceleration, 7);
        Assert.Equal(k * omega, rates.For("nut").Velocity.Z, 8);
        Assert.Equal(k * alpha, rates.For("nut").Acceleration.Z, 7);
        Assert.Equal(omega, rates.For("nut").AngularVelocity.Z, 8);
    }

    [Fact]
    public void RatesOnAnUnderConstrainedMechanism_RefuseNamingTheFreedom()
    {
        // A cylindrical joint driven on its angle still slides freely: the velocity
        // is a family, not an answer.
        var rig = new Assembly("rig");
        var fixedOne = rig.Add(BoxPart("base"));
        var moving = rig.Add(BoxPart("pin"));
        var joint = Joint.Cylindrical(
            MateGeometry.Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(moving, (0, 0, 0), Vector3d.UnitZ));
        var mechanism = new Mechanism(rig).Ground(fixedOne).Add(joint);

        var exception = Assert.Throws<MechanismException>(
            () => mechanism.RatesAt(MechanismDriver.Angle(joint), 0.5));
        Assert.Contains("free degree", exception.Message);
    }
}
