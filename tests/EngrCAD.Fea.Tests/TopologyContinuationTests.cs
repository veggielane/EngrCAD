using System;
using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// <see cref="TopologyOptions.PenaltyContinuation"/>: ramping the SIMP penalty from 1 up to the
/// target rather than holding it at the target from the start.
///
/// <para><b>The claim is start-independence, and it is only worth making if the fixture is
/// genuinely start-dependent without it.</b> The MBB beam at a low volume fraction and a small
/// filter is multimodal: fixed-<c>p</c> optimisation from two different starting designs falls
/// into two different structures — one of them nearly twice as compliant as the other.
/// Continuation, by settling the convex <c>p = 1</c> problem first and stepping the penalty up
/// from there, reaches essentially the same structure from both starts. Every number below is
/// measured; the flag being OFF is asserted bit-identical to the incumbent path.</para>
/// </summary>
public sealed class TopologyContinuationTests(ITestOutputHelper output)
{
    /// <summary>
    /// <b>With the flag OFF the run is bit-identical to the incumbent path, and turning it on
    /// changes the answer.</b> The first half is the safety contract (a run that does not ask
    /// for continuation gets exactly what it always did); the second proves the flag is not a
    /// no-op.
    /// </summary>
    [Fact]
    public void ContinuationOff_IsBitIdentical_AndOnChangesTheAnswer()
    {
        var model = TopologyFixtures.Cantilever(0, out _);
        TopologyOptions Options(bool cont) => new()
        {
            VolumeFraction = 0.4,
            FilterRadius = 6.0,
            Penalty = 3.0,
            PenaltyContinuation = cont,
            MaxIterations = 80,
        };

        // Default options (continuation absent) and explicit off must agree bit for bit.
        var byDefault = TopologyOptimizer.Minimize(model, new TopologyOptions
        {
            VolumeFraction = 0.4, FilterRadius = 6.0, Penalty = 3.0, MaxIterations = 80,
        });
        var explicitOff = TopologyOptimizer.Minimize(model, Options(false));
        Assert.Equal(byDefault.Density.Count, explicitOff.Density.Count);
        for (int e = 0; e < byDefault.Density.Count; e++)
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(byDefault.Density[e]),
                BitConverter.DoubleToInt64Bits(explicitOff.Density[e]));

        // Continuation ON must produce a DIFFERENT field (the schedule does something).
        var on = TopologyOptimizer.Minimize(model, Options(true));
        bool anyDifferent = false;
        for (int e = 0; e < on.Density.Count && !anyDifferent; e++)
            anyDifferent = BitConverter.DoubleToInt64Bits(on.Density[e])
                != BitConverter.DoubleToInt64Bits(explicitOff.Density[e]);
        Assert.True(anyDifferent, "continuation changed nothing");
    }

    /// <summary>
    /// <b>The penalty schedule.</b> Off, the effective penalty is the target at every iteration
    /// (exact — this is what makes the off path incumbent). On, it starts at 1 and steps up by
    /// <see cref="TopologyOptimizer.PenaltyStep"/> every
    /// <see cref="TopologyOptimizer.PenaltyHoldIterations"/> iterations, never decreasing, and
    /// holds at the target once reached.
    /// </summary>
    [Fact]
    public void TheSchedule_HoldsThenStepsUpToTheTarget()
    {
        var off = new TopologyOptions { VolumeFraction = 0.4, FilterRadius = 6, Penalty = 3.0 };
        for (int it = 1; it <= 200; it++)
            Assert.Equal(3.0, TopologyOptimizer.EffectivePenalty(off, it));

        var on = off with { PenaltyContinuation = true };
        int hold = TopologyOptimizer.PenaltyHoldIterations;
        Assert.Equal(1.0, TopologyOptimizer.EffectivePenalty(on, 1));
        Assert.Equal(1.0, TopologyOptimizer.EffectivePenalty(on, hold));           // still level 1
        Assert.Equal(1.5, TopologyOptimizer.EffectivePenalty(on, hold + 1));       // first step
        Assert.Equal(2.0, TopologyOptimizer.EffectivePenalty(on, 2 * hold + 1));
        Assert.Equal(3.0, TopologyOptimizer.EffectivePenalty(on, 4 * hold + 1));   // target reached
        Assert.Equal(3.0, TopologyOptimizer.EffectivePenalty(on, 500));            // held

        // Monotone non-decreasing, and never above the target.
        double previous = 0;
        for (int it = 1; it <= 200; it++)
        {
            double p = TopologyOptimizer.EffectivePenalty(on, it);
            Assert.True(p >= previous, $"penalty fell at it {it}");
            Assert.True(p <= 3.0 + 1e-15, $"penalty {p} exceeded the target at it {it}");
            previous = p;
        }

        // The convergence stop is deferred until the target penalty is reached, and only then.
        for (int it = 1; it <= 4 * hold; it++)
            Assert.False(TopologyOptimizer.RampComplete(on, it), $"ramp reported complete at it {it}");
        Assert.True(TopologyOptimizer.RampComplete(on, 4 * hold + 1));
    }

    /// <summary>
    /// <b>The oracle: on a multimodal fixture, continuation converges two different starting
    /// designs far closer together than fixed-<c>p</c> does, and reaches a compliance at least
    /// as good.</b>
    ///
    /// <para>The MBB beam at volume fraction 0.3 and filter radius 5 is genuinely start-
    /// dependent: seeding it with material biased toward the top traps a fixed-<c>p</c> run in a
    /// local minimum nearly twice as compliant as the one a bottom-biased seed reaches.
    /// Continuation escapes that basin — from both seeds it reaches essentially one structure
    /// and one compliance, close to the better fixed-<c>p</c> result and far below the worse
    /// one.</para>
    /// </summary>
    [Fact]
    public void Continuation_ReducesStartDependence_AndIsAtLeastAsGood()
    {
        const double frac = 0.3, radius = 5.0;
        const int maxIter = 200;
        _ = TopologyFixtures.MbbBeam(0, out var meshRef);
        var seedTop = TopologyFixtures.BiasedSeed(meshRef, 1);     // material graded toward +Z
        var seedBottom = TopologyFixtures.BiasedSeed(meshRef, 2);  // toward −Z

        TopologyResult Run(bool cont, double[] seed)
        {
            var model = TopologyFixtures.MbbBeam(0, out _);
            return TopologyOptimizer.Minimize(model, new TopologyOptions
            {
                VolumeFraction = frac,
                FilterRadius = radius,
                Penalty = 3.0,
                PenaltyContinuation = cont,
                MaxIterations = maxIter,
                InitialDensity = seed,
            });
        }

        var fixedTop = Run(false, seedTop);
        var fixedBottom = Run(false, seedBottom);
        var contTop = Run(true, seedTop);
        var contBottom = Run(true, seedBottom);

        double fixedSpread = TopologyFixtures.MeanAbsoluteDifference(
            TopologyFixtures.MbbBinned(meshRef, fixedTop.Density),
            TopologyFixtures.MbbBinned(meshRef, fixedBottom.Density));
        double contSpread = TopologyFixtures.MeanAbsoluteDifference(
            TopologyFixtures.MbbBinned(meshRef, contTop.Density),
            TopologyFixtures.MbbBinned(meshRef, contBottom.Density));

        output.WriteLine(
            $"fixed:  top c={fixedTop.Compliance:G6}, bottom c={fixedBottom.Compliance:G6}; "
            + $"spread {fixedSpread:F5}");
        output.WriteLine(
            $"cont:   top c={contTop.Compliance:G6}, bottom c={contBottom.Compliance:G6}; "
            + $"spread {contSpread:F5}");

        // The premise: the fixture IS start-dependent without continuation, or the test proves
        // nothing. (Measured ~0.20; a wide margin below.)
        Assert.True(fixedSpread > 0.05, $"fixture not start-dependent: fixed spread {fixedSpread:F5}");

        // The claim: continuation converges the two starts far closer together. (Measured ~193×;
        // a factor of four is a bulletproof margin.)
        Assert.True(contSpread < 0.25 * fixedSpread,
            $"continuation spread {contSpread:F5} not below fixed {fixedSpread:F5}");

        // At least as good: continuation's worst compliance is no worse than fixed-p's worst —
        // it escapes the bad basin one of the seeds fell into.
        double worstFixed = Math.Max(fixedTop.Compliance, fixedBottom.Compliance);
        double worstCont = Math.Max(contTop.Compliance, contBottom.Compliance);
        Assert.True(worstCont <= worstFixed,
            $"continuation worst {worstCont:G6} worse than fixed worst {worstFixed:G6}");

        // And the two continuation compliances agree — the direct statement of start-independence
        // in the objective, not just the field.
        double contGap = Math.Abs(contTop.Compliance - contBottom.Compliance)
            / Math.Max(contTop.Compliance, contBottom.Compliance);
        Assert.True(contGap < 0.02, $"continuation compliances disagree by {contGap:P2}");
    }
}
