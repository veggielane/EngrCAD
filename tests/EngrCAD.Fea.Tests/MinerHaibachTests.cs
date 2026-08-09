using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The Miner–Haibach sloped continuation of the S-N line past the endurance knee — the standard
/// variable-amplitude remedy for the STEP a flat endurance line puts in the damage function.
///
/// <para>The verification is a set of identities of the construction, plus the two consequences
/// that matter: a stress below the endurance limit now carries a FINITE life (so a sub-limit
/// cycle accumulates damage instead of none — the step in <c>D(k)</c> becomes continuous), and
/// the endurance PLATEAU is gone (so the infinite-life safety factor no longer exists and the
/// fatigue machinery requires a design life, the same rule a knee-less aluminium curve earns).</para>
/// </summary>
public class MinerHaibachTests(ITestOutputHelper output)
{
    private static void AssertRelative(double expected, double actual, double tol)
    {
        double scale = Math.Max(Math.Abs(expected), Math.Abs(actual));
        double rel = scale == 0 ? Math.Abs(expected - actual) : Math.Abs(expected - actual) / scale;
        Assert.True(rel <= tol, $"expected {expected:G17}, got {actual:G17} (relative {rel:E2})");
    }

    [Fact]
    public void TheContinuationIsShallowerAndContinuousAtTheKnee()
    {
        var flat = FatigueMaterials.Steel1045;
        var haibach = flat.WithHaibachSlope();
        double knee = flat.EnduranceLife!.Value;   // 1e6
        double limit = flat.EnduranceLimit!.Value;
        output.WriteLine($"{flat}\n{haibach}");

        Assert.True(haibach.HasHaibachContinuation);
        Assert.False(haibach.HasEnduranceLimit);   // the plateau is gone

        // Continuous at the knee: the Haibach line passes exactly through the knee point.
        AssertRelative(limit, haibach.StressAt(knee), 1e-12);
        AssertRelative(haibach.StressAt(knee * (1 - 1e-9)), haibach.StressAt(knee * (1 + 1e-9)), 1e-6);

        // Below the knee the two curves are IDENTICAL — Haibach only changes the beyond-knee
        // segment.
        foreach (double n in new[] { 1e3, 1e4, 1e5, 5e5 })
            AssertRelative(flat.StressAt(n), haibach.StressAt(n), 1e-12);

        // Beyond the knee the Haibach line keeps FALLING (below the plateau) but stays ABOVE the
        // steep pre-knee line extrapolated on — the shallower slope.
        foreach (double n in new[] { 2e6, 1e7, 1e8 })
        {
            double h = haibach.StressAt(n);
            Assert.True(h < limit, $"Haibach did not fall past the knee at {n}: {h} vs limit {limit}");
            Assert.True(h > flat.FatigueStrengthCoefficient * Math.Pow(2 * n, flat.FatigueStrengthExponent),
                $"Haibach steeper than the base line at {n}");
        }

        // The Haibach exponent is b/(2+b), shallower in magnitude than b.
        double b = flat.FatigueStrengthExponent;
        double expectedB = b / (2 + b);
        // Read the exponent back off two points on the continuation.
        double s1 = haibach.StressAt(2e6), s2 = haibach.StressAt(2e7);
        double measuredB = Math.Log(s2 / s1) / Math.Log(2e7 / 2e6);
        output.WriteLine($"b = {b}, b' expected {expectedB:G6}, measured {measuredB:G6}");
        AssertRelative(expectedB, measuredB, 1e-9);
        Assert.True(Math.Abs(expectedB) < Math.Abs(b));
    }

    [Fact]
    public void ASubLimitStressGainsAFiniteLife_TheStepDisappears()
    {
        var flat = FatigueMaterials.Steel1045;
        var haibach = flat.WithHaibachSlope();
        double limit = flat.EnduranceLimit!.Value;
        double knee = flat.EnduranceLife!.Value;

        // Under the flat line a stress below the limit lives forever; under Haibach it does not.
        Assert.True(double.IsPositiveInfinity(flat.LifeAt(0.9 * limit)));
        double life = haibach.LifeAt(0.9 * limit);
        output.WriteLine($"0.9·limit under Haibach lives {life:G4} cycles (> knee {knee:G4})");
        Assert.True(double.IsFinite(life) && life > knee, $"sub-limit life {life} not finite/past knee");

        // As the amplitude approaches the limit from below, the Haibach life approaches the knee
        // continuously — the step the flat line put there (infinite -> knee) is gone.
        double nearLimit = haibach.LifeAt(limit * (1 - 1e-7));
        AssertRelative(knee, nearLimit, 1e-5);

        // Round-trip on the continuation: the life of a stress, put back through the stress of a
        // life, returns the stress.
        double amp = 0.8 * limit;
        AssertRelative(amp, haibach.StressAt(haibach.LifeAt(amp)), 1e-9);
    }

    [Fact]
    public void ASubLimitCycleAccumulatesDamageUnderHaibachAndNoneUnderTheFlatLine()
    {
        // The step in the damage function, at the arithmetic the rainflow machinery runs: a
        // fully-reversed cycle just below the endurance limit contributes exactly zero damage
        // under the flat line and a small positive damage under Haibach.
        var flat = FatigueMaterials.Steel1045;
        var haibach = flat.WithHaibachSlope();
        double limit = flat.EnduranceLimit!.Value;

        // A fully reversed cycle (mean 0) at 0.9·limit, 1000 counts.
        var spectrum = new[] { new RainflowCycle(2 * 0.9 * limit, 0.0, 1000) };
        double flatDamage = FatigueAnalysis.Damage(spectrum, flat);
        double haibachDamage = FatigueAnalysis.Damage(spectrum, haibach);
        output.WriteLine($"sub-limit spectrum: flat damage {flatDamage:E3}, Haibach {haibachDamage:E3}");
        Assert.Equal(0.0, flatDamage);
        Assert.True(haibachDamage > 0, "Haibach did not accumulate damage below the limit");

        // A cycle ABOVE the limit accumulates the SAME damage either way — Haibach touches only
        // the sub-knee segment.
        var above = new[] { new RainflowCycle(2 * 1.5 * limit, 0.0, 1000) };
        AssertRelative(
            FatigueAnalysis.Damage(above, flat), FatigueAnalysis.Damage(above, haibach), 1e-12);
    }

    [Fact]
    public void TheInfiniteLifeFactorNoLongerExists_TheSameRuleAsAluminium()
    {
        // A Haibach curve has no endurance plateau, so a safety factor against INFINITE life
        // does not exist for it — the same refusal a knee-less aluminium curve earns, one rule.
        var haibach = FatigueMaterials.Steel1045.WithHaibachSlope();
        var spectrum = new[] { new RainflowCycle(400, 0.0, 1) };
        var ex = Assert.Throws<FeaException>(() =>
            FatigueAnalysis.LoadFactor(spectrum, haibach));   // null repetitions = infinite life
        Assert.Contains("no endurance limit", ex.Message);

        // With a stated repetition count it works, and the damage uses the sloped line.
        double factor = FatigueAnalysis.LoadFactor(spectrum, haibach, designRepetitions: 1e7);
        Assert.True(double.IsFinite(factor) && factor > 0, $"factor {factor}");
    }

    [Fact]
    public void TheRefusalsFireByName()
    {
        // Aluminium has no knee to continue past.
        var noKnee = Assert.Throws<FeaException>(() =>
            FatigueMaterials.Aluminium6061T6.WithHaibachSlope());
        Assert.Contains("no endurance knee", noKnee.Message);

        // A curve already carrying a continuation cannot gain a second.
        var twice = Assert.Throws<FeaException>(() =>
            FatigueMaterials.Steel1045.WithHaibachSlope().WithHaibachSlope());
        Assert.Contains("already carries", twice.Message);
    }
}
