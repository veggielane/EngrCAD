using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// <see cref="RayleighDamping"/> and <see cref="ModalDamping"/>: the fit, the shape of the
/// curve it produces, and what it refuses.
/// </summary>
public class DampingTests(ITestOutputHelper output)
{
    [Fact]
    public void AFitReproducesBothRatiosExactly()
    {
        // Two equations, two unknowns — so the fit is not an approximation and the assertion
        // is at round-off, not at a tolerance somebody chose.
        var damping = RayleighDamping.FromRatios(50.0, 0.02, 500.0, 0.02);
        output.WriteLine(damping.ToString());

        Assert.Equal(0.02, damping.RatioAtFrequency(50.0), 1e-15);
        Assert.Equal(0.02, damping.RatioAtFrequency(500.0), 1e-15);
        Assert.True(damping.Alpha > 0 && damping.Beta > 0);
    }

    [Fact]
    public void TheCurveIsAUWithItsMinimumWhereTheClosedFormSaysItIs()
    {
        // zeta(w) = alpha/(2w) + beta·w/2 has its minimum sqrt(alpha·beta) at
        // w = sqrt(alpha/beta), by differentiating. Both are asserted against a SEARCH over
        // the curve rather than against the same formula, so the property is measured.
        var damping = RayleighDamping.FromRatios(20.0, 0.03, 800.0, 0.01);
        output.WriteLine(damping.ToString());

        double best = double.MaxValue, bestAt = 0;
        for (int i = 0; i <= 200_000; i++)
        {
            double f = 1.0 + i * 0.05;
            double r = damping.RatioAtFrequency(f);
            if (r < best)
            {
                best = r;
                bestAt = f;
            }
        }

        output.WriteLine(
            $"searched minimum {best:P5} at {bestAt:N2} Hz; closed form "
            + $"{damping.MinimumRatio:P5} at {damping.FrequencyOfMinimumRatio:N2} Hz");
        Assert.Equal(damping.MinimumRatio, best, 1e-9);
        Assert.Equal(damping.FrequencyOfMinimumRatio!.Value, bestAt, 0.1);

        // And the whole point of the U: outside the fitted pair the damping is HIGHER, at
        // both ends, which is the property that quietly over-damps modes nobody looked at.
        output.WriteLine(
            $"at 5 Hz {damping.RatioAtFrequency(5):P2}, at 5 kHz {damping.RatioAtFrequency(5000):P2} "
            + "(fitted 3.00% and 1.00%)");
        Assert.True(damping.RatioAtFrequency(5) > 0.03);
        Assert.True(damping.RatioAtFrequency(5000) > 0.01);
    }

    [Fact]
    public void PureMassAndStiffnessFormsAreMonotoneInOppositeDirections()
    {
        var mass = RayleighDamping.MassProportional(100.0, 0.02);
        var stiffness = RayleighDamping.StiffnessProportional(100.0, 0.02);
        Assert.Equal(0.02, mass.RatioAtFrequency(100.0), 1e-15);
        Assert.Equal(0.02, stiffness.RatioAtFrequency(100.0), 1e-15);

        // Mass-proportional damps the LOW modes and stiffness-proportional the HIGH ones —
        // the fact that decides which one over-damps what.
        Assert.True(mass.RatioAtFrequency(10) > mass.RatioAtFrequency(100));
        Assert.True(mass.RatioAtFrequency(1000) < mass.RatioAtFrequency(100));
        Assert.True(stiffness.RatioAtFrequency(10) < stiffness.RatioAtFrequency(100));
        Assert.True(stiffness.RatioAtFrequency(1000) > stiffness.RatioAtFrequency(100));

        // Neither has an interior minimum, so neither reports one.
        Assert.Null(mass.FrequencyOfMinimumRatio);
        Assert.Null(stiffness.FrequencyOfMinimumRatio);
        output.WriteLine($"{mass}; {stiffness}");
    }

    [Fact]
    public void AnImpossibleFitIsRefusedRatherThanReturningANegativeCoefficient()
    {
        // The curve cannot fall faster than 1/w, so asking for a ratio that drops by a factor
        // of 20 across a decade has no solution — and the fit that produces it comes out with
        // a negative alpha, i.e. damping that ADDS energy at low frequency. Returning it would
        // be a physically impossible model with entirely plausible numbers in it.
        var error = Assert.Throws<ArgumentException>(
            () => RayleighDamping.FromRatios(100.0, 0.20, 1000.0, 0.001));
        output.WriteLine(error.Message);
        Assert.Contains("No Rayleigh curve passes through", error.Message);
        Assert.Contains("ADDS energy", error.Message);

        Assert.Throws<ArgumentException>(() => RayleighDamping.FromRatios(100, 0.02, 100, 0.02));
        Assert.Throws<ArgumentException>(() => RayleighDamping.FromRatios(500, 0.02, 100, 0.02));
        Assert.Throws<ArgumentOutOfRangeException>(() => RayleighDamping.FromRatios(0, 0.02, 100, 0.02));
        Assert.Throws<ArgumentOutOfRangeException>(() => RayleighDamping.FromRatios(50, -0.01, 100, 0.02));
    }

    [Fact]
    public void ModalDampingModelsReportWhatTheyAre()
    {
        Assert.Equal(0.0, ModalDamping.None.RatioForMode(1, 100.0));
        Assert.Equal("undamped", ModalDamping.None.Describe());

        var uniform = ModalDamping.Uniform(0.05);
        Assert.Equal(0.05, uniform.RatioForMode(1, 10.0));
        Assert.Equal(0.05, uniform.RatioForMode(9, 1e6));

        var table = ModalDamping.PerMode([0.01, 0.02, 0.03], beyond: 0.04);
        Assert.Equal(0.01, table.RatioForMode(1, 0));
        Assert.Equal(0.03, table.RatioForMode(3, 0));
        Assert.Equal(0.04, table.RatioForMode(4, 0));
        output.WriteLine(table.Describe());

        var rayleigh = ModalDamping.Rayleigh(100.0, 0.02, 1000.0, 0.02);
        Assert.Equal(0.02, rayleigh.RatioForMode(1, 2 * Math.PI * 100.0), 1e-15);
        output.WriteLine(rayleigh.Describe());

        Assert.Throws<ArgumentOutOfRangeException>(() => ModalDamping.Uniform(-0.01));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModalDamping.PerMode([0.01, -0.02]));
    }

    [Fact]
    public void ARigidModeHasNoDampingRatioRatherThanAnInfiniteOne()
    {
        // alpha/(2w) is infinite at w = 0, which is a true statement about a ratio that does
        // not exist there: a rigid-body mode has no stiffness to be a fraction of. Zero is
        // returned and the case is handled where it belongs — HarmonicSolver refuses to
        // superpose rigid modes at all.
        var damping = RayleighDamping.MassProportional(100.0, 0.02);
        Assert.Equal(0.0, damping.RatioAt(0.0));
        Assert.Equal(0.0, damping.RatioAt(-1.0));
    }
}
