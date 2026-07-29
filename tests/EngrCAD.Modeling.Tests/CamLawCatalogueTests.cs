using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The dwell–rise–dwell law catalogue, the rack-and-pinion coupling, and adaptive
/// swept-volume sampling.
/// <para>What a catalogue has to prove is that its members are genuinely DIFFERENT and
/// different in the documented way — a set of laws that all behave alike is decoration.
/// So each law is checked for (a) its end conditions, which is what decides whether it
/// can meet a dwell without an acceleration step, and (b) its peak acceleration factor,
/// which is the number an engineer picks between them on. Derivatives are checked
/// against central differences of the law's OWN lift, since the solver's Jacobian
/// consumes them and a law whose slope is not its lift's calculus would converge to the
/// wrong pose quietly.</para>
/// </summary>
public class CamLawCatalogueTests
{
    private const double Rise = 10;
    private const double Span = Math.PI;      // a 180° rise

    private static (double Lift, double Slope, double Curvature) At(CamLaw law, double angle)
    {
        law.Evaluate(angle, out double lift, out double slope, out double curvature);
        return (lift, slope, curvature);
    }

    /// <summary>
    /// Central differences of the law's own lift — the check that a law's stated
    /// calculus IS its lift's, which is what the solver's Jacobian consumes.
    /// <para>Samples land on HALF-steps deliberately. A central difference cannot see
    /// across a segment joint: at the top of a cycloidal rise the third derivative steps
    /// (that is what a piecewise law is), so a sample landing exactly on the joint reads
    /// a second difference of −h·Δh'''/6 — measured −0.0052 at h = 3.1e-4, which is real
    /// arithmetic about a one-sided window rather than a wrong derivative. Offsetting the
    /// grid keeps every window inside one segment, where the comparison means what it
    /// says.</para>
    /// </summary>
    private static void AssertDerivativesMatchTheLift(CamLaw law, double from, double to)
    {
        double h = (to - from) / 20000;
        for (int i = 0; i < 40; i++)
        {
            double x = from + (to - from) * (i + 0.5) / 40.0;
            var (_, slope, curvature) = At(law, x);
            double lp = At(law, x + h).Lift, lm = At(law, x - h).Lift, l0 = At(law, x).Lift;
            Assert.Equal((lp - lm) / (2 * h), slope, 4);
            Assert.Equal((lp - 2 * l0 + lm) / (h * h), curvature, 2);
        }
    }

    public static TheoryData<string> RiseLaws => new(["cycloidal", "harmonic", "modified-trapezoid"]);

    private static CamLaw RiseLaw(string name) => name switch
    {
        "cycloidal" => CamLaw.Cycloidal(Rise, Span),
        "harmonic" => CamLaw.HarmonicRise(Rise, Span),
        _ => CamLaw.ModifiedTrapezoid(Rise, Span),
    };

    [Theory]
    [MemberData(nameof(RiseLaws))]
    public void EveryRiseLawGoesFromZeroToTheRiseAndIsItsOwnCalculus(string name)
    {
        var law = RiseLaw(name);
        Assert.Equal(0, At(law, 0).Lift, 9);
        Assert.Equal(Rise, At(law, Span).Lift, 6);
        // Monotone: a rise that overshoots and comes back is not a rise.
        double previous = -1;
        for (int i = 0; i <= 200; i++)
        {
            double lift = At(law, Span * i / 200.0).Lift;
            Assert.True(lift >= previous - 1e-12, $"{name} is not monotone at {i}");
            previous = lift;
        }
        AssertDerivativesMatchTheLift(law, 0, Span);
    }

    [Theory]
    [MemberData(nameof(RiseLaws))]
    public void EveryRiseLawClampsOutsideItsSpanSoItComposes(string name)
    {
        var law = RiseLaw(name);
        foreach (double before in new[] { -1.0, -0.001 })
            Assert.Equal((0, 0, 0), At(law, before));
        foreach (double after in new[] { Span + 0.001, Span + 5 })
            Assert.Equal((Rise, 0, 0), At(law, after));
    }

    [Fact]
    public void CycloidalAndModifiedTrapezoidMeetADwellC2ButHarmonicDoesNot()
    {
        // The whole reason to choose between them. Just inside each end:
        const double eps = 1e-7;
        foreach (var (name, law, endAcceleration) in new (string, CamLaw, bool)[]
        {
            ("cycloidal", CamLaw.Cycloidal(Rise, Span), false),
            ("modified-trapezoid", CamLaw.ModifiedTrapezoid(Rise, Span), false),
            ("harmonic", CamLaw.HarmonicRise(Rise, Span), true),
        })
        {
            double start = Math.Abs(At(law, eps).Curvature);
            double end = Math.Abs(At(law, Span - eps).Curvature);
            double scale = Rise / (Span * Span);
            if (endAcceleration)
            {
                // A finite step from the dwell's zero — the classic cam-noise source.
                Assert.True(start > scale, $"{name} should step at the start, measured {start / scale:g4}");
                Assert.True(end > scale, $"{name} should step at the end");
            }
            else
            {
                Assert.True(start < 1e-4 * scale, $"{name} should start at zero acceleration, measured {start / scale:g4}");
                Assert.True(end < 1e-4 * scale, $"{name} should end at zero acceleration, measured {end / scale:g4}");
            }
        }
    }

    [Theory]
    [InlineData("cycloidal", 6.2832)]           // 2π
    [InlineData("harmonic", 4.9348)]            // π²/2
    [InlineData("modified-trapezoid", 4.8881)]  // 8π/(2+π) — the ~22% the compromise buys
    public void PeakAccelerationFactorsAreTheOnesTheCatalogueClaims(string name, double factor)
    {
        var law = RiseLaw(name);
        double peak = 0;
        for (int i = 0; i <= 20000; i++)
            peak = Math.Max(peak, Math.Abs(At(law, Span * i / 20000.0).Curvature));
        Assert.Equal(factor, peak / (Rise / (Span * Span)), 3);
    }

    [Fact]
    public void HarmonicHasTheLowestPeakVelocityOfTheThree()
    {
        double Peak(CamLaw law)
        {
            double peak = 0;
            for (int i = 0; i <= 20000; i++)
                peak = Math.Max(peak, Math.Abs(At(law, Span * i / 20000.0).Slope));
            return peak;
        }
        double harmonic = Peak(CamLaw.HarmonicRise(Rise, Span));
        Assert.True(harmonic < Peak(CamLaw.Cycloidal(Rise, Span)));
        Assert.True(harmonic < Peak(CamLaw.ModifiedTrapezoid(Rise, Span)));
        Assert.Equal(Math.PI / 2 * Rise / Span, harmonic, 4);
    }

    // ---- composing a cycle ----

    [Fact]
    public void SegmentsBuildADwellRiseDwellFallCycleThatReturnsToZero()
    {
        double quarter = Math.PI / 2;
        var law = CamLaw.Segments(
            (quarter, CamLaw.Dwell()),
            (quarter, CamLaw.Cycloidal(Rise, quarter)),
            (quarter, CamLaw.Dwell()),
            (quarter, CamLaw.Cycloidal(-Rise, quarter)));

        Assert.Equal(0, At(law, 0).Lift, 9);
        Assert.Equal(0, At(law, quarter * 0.9).Lift, 9);            // low dwell
        Assert.Equal(Rise, At(law, 2 * quarter).Lift, 6);           // top of the rise
        Assert.Equal(Rise, At(law, 2.9 * quarter).Lift, 6);         // high dwell
        Assert.Equal(0, At(law, 4 * quarter).Lift, 6);              // back home
        // Periodic: a whole turn returns to the start (the cycle's net lift is zero).
        Assert.Equal(At(law, 0.7).Lift, At(law, 0.7 + Math.Tau).Lift, 9);

        // The dwells are genuinely still, and the rise is genuinely moving.
        Assert.Equal(0, At(law, quarter * 0.5).Slope, 12);
        Assert.True(At(law, quarter * 1.5).Slope > 0);
        AssertDerivativesMatchTheLift(law, 0.05, Math.Tau - 0.05);
    }

    [Fact]
    public void SegmentSpansAreScaledToTheCycleSoADegreeStatedProfileKeepsItsShape()
    {
        // Same profile written in "degrees" (spans summing to 360) and in radians:
        // both must describe the same cam.
        var inDegrees = CamLaw.Segments(
            (90.0, CamLaw.Dwell()), (180.0, CamLaw.Cycloidal(Rise, 180)), (90.0, CamLaw.Dwell(Rise)));
        var inRadians = CamLaw.Segments(
            (Math.PI / 2, CamLaw.Dwell()), (Math.PI, CamLaw.Cycloidal(Rise, Math.PI)),
            (Math.PI / 2, CamLaw.Dwell(Rise)));

        for (int i = 0; i <= 40; i++)
        {
            double angle = Math.Tau * i / 40.0;
            Assert.Equal(At(inRadians, angle).Lift, At(inDegrees, angle).Lift, 6);
        }
    }

    [Fact]
    public void ADegenerateSegmentSetIsRefused()
    {
        Assert.Throws<ArgumentException>(() => CamLaw.Segments());
        Assert.Throws<ArgumentOutOfRangeException>(() => CamLaw.Segments((0.0, CamLaw.Dwell())));
        Assert.Throws<ArgumentOutOfRangeException>(() => CamLaw.Cycloidal(1, -1));
    }

    // ---- rack and pinion ----

    [Fact]
    public void RackAndPinionMovesTheRackByRadiusTimesAngle()
    {
        const double radius = 12.5;
        var rig = new Assembly("rig");
        var ground = rig.Add(new Part("base", MeshPrimitives.Box(4, 2, 1)));
        var pinion = rig.Add(new Part("pinion", MeshPrimitives.Box(4, 2, 1)));
        var rack = rig.Add(new Part("rack", MeshPrimitives.Box(4, 2, 1)));

        var spin = Joint.Revolute(
            MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(pinion, (0, 0, 0), Vector3d.UnitZ));
        var slide = Joint.Prismatic(
            MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitX),
            MateGeometry.Axis(rack, (0, 0, 0), Vector3d.UnitX));

        var mechanism = new Mechanism(rig).Ground(ground).Add(spin).Add(slide)
            .Add(Coupling.RackAndPinion(spin, slide, radius));

        // Two DOF, one coupling: driving the pinion pins the rack.
        var driver = MechanismDriver.Angle(spin);
        mechanism.SolveAt(driver, Math.PI / 3);
        Assert.Equal(radius * Math.PI / 3, slide.Displacement, 8);

        // ... and it keeps working through more than a turn, because the coupling reads
        // the UNWRAPPED angle (a wrapped one would send the rack back at every seam).
        mechanism.Sweep(driver, Math.PI / 3, 3 * Math.Tau, frames: 25);
        Assert.Equal(radius * 3 * Math.Tau, slide.Displacement, 6);
    }

    [Fact]
    public void RackAndPinionRatesAreTheDerivativeOfItsOwnRelation()
    {
        const double radius = 8;
        var rig = new Assembly("rig");
        var ground = rig.Add(new Part("base", MeshPrimitives.Box(4, 2, 1)));
        var pinion = rig.Add(new Part("pinion", MeshPrimitives.Box(4, 2, 1)));
        var rack = rig.Add(new Part("rack", MeshPrimitives.Box(4, 2, 1)));
        var spin = Joint.Revolute(
            MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(pinion, (0, 0, 0), Vector3d.UnitZ));
        var slide = Joint.Prismatic(
            MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitX),
            MateGeometry.Axis(rack, (0, 0, 0), Vector3d.UnitX));
        var mechanism = new Mechanism(rig).Ground(ground).Add(spin).Add(slide)
            .Add(Coupling.RackAndPinion(spin, slide, radius));

        var rates = mechanism.RatesAt(MechanismDriver.Angle(spin), 0.4, rate: 3, acceleration: 2);
        Assert.Equal(radius * 3, rates.For(slide).SlideRate, 8);
        Assert.Equal(radius * 2, rates.For(slide).SlideAcceleration, 7);
    }

    [Fact]
    public void AZeroPitchRadiusIsRefused()
    {
        var rig = new Assembly("rig");
        var ground = rig.Add(new Part("base", MeshPrimitives.Box(4, 2, 1)));
        var pinion = rig.Add(new Part("pinion", MeshPrimitives.Box(4, 2, 1)));
        var rack = rig.Add(new Part("rack", MeshPrimitives.Box(4, 2, 1)));
        var spin = Joint.Revolute(
            MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(pinion, (0, 0, 0), Vector3d.UnitZ));
        var slide = Joint.Prismatic(
            MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitX),
            MateGeometry.Axis(rack, (0, 0, 0), Vector3d.UnitX));
        Assert.Throws<ArgumentOutOfRangeException>(() => Coupling.RackAndPinion(spin, slide, 0));
    }
}
