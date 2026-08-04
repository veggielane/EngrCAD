using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Rainflow counting and the variable-amplitude fatigue built on it.
///
/// <para><b>The transcription test with teeth is ASTM E1049's own worked example</b>
/// (Fig. 6), reproduced cycle for cycle — ranges, means AND counts, in algorithm order —
/// because a counting algorithm that gets the totals right can still pair the wrong
/// points (the total-variation identity below would pass it). The second anchor is the
/// degeneracy: a constant-amplitude history through the WHOLE pipeline — internally-built
/// transient states alternating between a SolveAll pair's two results — reproduces the
/// static-pair fatigue answer exactly, bit for bit where the arithmetic is shared.</para>
/// </summary>
public class RainflowTests(ITestOutputHelper output)
{
    // ---- the ASTM E1049 worked example, cycle for cycle -------------------------------

    [Fact]
    public void AstmE1049WorkedExampleReproducesCycleForCycle()
    {
        // E1049 Fig. 6: peaks and valleys A..I. Expected (range, mean, count) in the
        // order the standard's own algorithm meets them.
        double[] history = [-2, 1, -3, 5, -1, 3, -4, 4, -2];
        var cycles = Rainflow.Count(history);

        foreach (var c in cycles)
            output.WriteLine(c.ToString());

        (double Range, double Mean, double Count)[] expected =
        [
            (3, -0.5, 0.5),  // A-B
            (4, -1.0, 0.5),  // B-C
            (4, 1.0, 1.0),   // E-F — the one FULL cycle
            (8, 1.0, 0.5),   // C-D
            (9, 0.5, 0.5),   // D-G
            (8, 0.0, 0.5),   // G-H
            (6, 1.0, 0.5),   // H-I
        ];
        Assert.Equal(expected.Length, cycles.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Range, cycles[i].Range);
            Assert.Equal(expected[i].Mean, cycles[i].Mean);
            Assert.Equal(expected[i].Count, cycles[i].Count);
        }
    }

    [Fact]
    public void CountingDecomposesTheTotalVariationExactly()
    {
        // Every range a cycle consumes is walked twice per full count, so
        // sum(2·count·range) equals the turning-point series' total variation — an
        // identity that holds for ANY history and catches a dropped or double-counted
        // range on inputs nobody hand-checked. Deterministic pseudo-random history.
        var random = new Random(20260802);
        var history = new double[257];
        for (int i = 0; i < history.Length; i++)
            history[i] = Math.Round(20.0 * random.NextDouble() - 10.0, 3);

        var cycles = Rainflow.Count(history);
        double counted = 0;
        foreach (var c in cycles)
            counted += 2.0 * c.Count * c.Range;

        // The turning points' own total variation, extracted independently of the
        // counting (its own reduction loop, below).
        double variation = TotalVariationOfTurningPoints(history);

        output.WriteLine($"2·sum(count·range) = {counted}, total variation = {variation}");
        Assert.Equal(variation, counted, 1e-9);
    }

    private static double TotalVariationOfTurningPoints(double[] history)
    {
        var points = new List<double>();
        foreach (double v in history)
        {
            if (points.Count > 0 && points[^1] == v)
                continue;
            if (points.Count >= 2)
            {
                double a = points[^2], b = points[^1];
                if ((b > a && v > b) || (b < a && v < b))
                {
                    points[^1] = v;
                    continue;
                }
            }
            points.Add(v);
        }
        double variation = 0;
        for (int i = 1; i < points.Count; i++)
            variation += Math.Abs(points[i] - points[i - 1]);
        return variation;
    }

    [Fact]
    public void RepeatingModePairsEveryHalfIntoFullCycles()
    {
        // One period of a repeating load, rearranged to start at the largest-magnitude
        // extremum: E1049's own prescription, under which every count closes. The
        // constant-amplitude case is the sharp check: k swings must come back as exactly
        // k full cycles of one (range, mean) — the as-is counting's boundary halves all
        // paired — because that is what makes per-block damage accumulate with no phantom
        // half per repetition.
        double[] period = [5, -1, 5, -1, 5, -1];
        var cycles = Rainflow.Count(period, assumeRepeating: true);
        foreach (var c in cycles)
            output.WriteLine(c.ToString());

        var only = Assert.Single(cycles);
        Assert.Equal(6.0, only.Range);
        Assert.Equal(2.0, only.Mean);
        Assert.Equal(3.0, only.Count);

        // And the ASTM example read as repeating: total damage-relevant content is
        // conserved (the total-variation identity holds in this mode too).
        double[] history = [-2, 1, -3, 5, -1, 3, -4, 4, -2];
        double counted = 0;
        foreach (var c in Rainflow.Count(history, assumeRepeating: true))
            counted += 2.0 * c.Count * c.Range;
        // The rotated-and-closed series has its own variation: rotation to the largest
        // extremum plus closure changes the seam ranges, so compare against THAT series.
        // (-2 at both ends of the original merges into the rotation seamlessly: the
        // rotated sequence is 5,-1,3,-4,4,-2,-2,1,-3,5 with the doubled -2 merged.)
        double[] rotated = [5, -1, 3, -4, 4, -2, 1, -3, 5];
        Assert.Equal(TotalVariationOfTurningPoints(rotated), counted, 1e-9);
    }

    [Fact]
    public void EdgeCasesAnswerHonestly()
    {
        // Too short or constant: nothing to count.
        Assert.Empty(Rainflow.Count([]));
        Assert.Empty(Rainflow.Count([3.0]));
        Assert.Empty(Rainflow.Count([3.0, 3.0, 3.0]));

        // Two samples: one half cycle.
        var half = Assert.Single(Rainflow.Count([1.0, 5.0]));
        Assert.Equal(4.0, half.Range);
        Assert.Equal(3.0, half.Mean);
        Assert.Equal(0.5, half.Count);

        // Interior monotone samples are not reversals.
        var ramp = Assert.Single(Rainflow.Count([0.0, 1.0, 2.0, 5.0]));
        Assert.Equal(5.0, ramp.Range);

        // NaN is refused by name, not silently dropped.
        var ex = Assert.Throws<FeaException>(() => Rainflow.Count([1.0, double.NaN, 2.0]));
        Assert.Contains("NaN", ex.Message);
    }

    // ---- the pipeline: constant amplitude degenerates to the static pair --------------

    [Fact]
    public void ConstantAmplitudeHistoryDegeneratesExactlyToTheStaticPair()
    {
        // The two extremes of one proportional history, solved as the SolveAll pair the
        // static fatigue path consumes — then strung into a synthetic transient history
        // A,B,A,B,A through the REAL seam (internally-built TransientStates over the same
        // StructuralResults), rainflow-counted in repeating mode. Per node the counted
        // cycle's amplitude and mean are BIT-equal to the static pair's (same values,
        // same arithmetic), so the per-cycle life is bit-equal and the damage is exactly
        // cycleCount/life — the degeneracy asserted as identities, not tolerances.
        var (model, cases) = TwoCaseBar();
        var results = StructuralSolver.SolveAll(cases);
        var a = results[0];
        var b = results[1];
        var statics = FatigueAnalysis.Evaluate(a, b, FatigueMaterials.Steel1045);

        var history = SyntheticHistory(model, [a, b, a, b, a]);
        var rainflow = FatigueAnalysis.Evaluate(
            history, FatigueMaterials.Steel1045,
            new RainflowFatigueOptions { AssumeRepeating = true });

        int nodes = model.Mesh.NodeCount;
        int finite = 0;
        for (int v = 0; v < nodes; v++)
        {
            var cycles = rainflow.CyclesAt(v);
            if (statics.AlternatingStress[v] == 0)
            {
                // A node with no swing counts nothing.
                Assert.Equal(0.0, rainflow.Damage[v]);
                continue;
            }

            var only = Assert.Single(cycles);
            Assert.Equal(2.0, only.Count);                          // two full swings
            Assert.Equal(statics.AlternatingStress[v], only.Amplitude); // bit-equal
            Assert.Equal(statics.MeanStress[v], only.Mean);             // bit-equal

            double equivalent = FatigueAnalysis.EquivalentAlternating(
                FatigueMaterials.Steel1045, only.Amplitude, only.Mean,
                MeanStressCorrection.Goodman);
            double life = FatigueMaterials.Steel1045.LifeAt(equivalent);
            if (double.IsPositiveInfinity(life))
            {
                Assert.Equal(0.0, rainflow.Damage[v]);
                Assert.True(double.IsNaN(rainflow.Log10Repetitions[v]));
                Assert.True(double.IsNaN(statics.Log10Life[v]));
                continue;
            }
            finite++;
            // Damage is exactly count/life (one addition from zero), so the identity is
            // bit-level; life in cycles = repetitions·(cycles per repetition) matches the
            // static answer through the same log.
            Assert.Equal(2.0 / life, rainflow.Damage[v]);
            Assert.Equal(statics.Log10Life[v], Math.Log10(Math.Max(1.0, life)), 12);
        }
        output.WriteLine($"{finite} finite-life nodes checked bit-level; "
            + $"min log10 repetitions {rainflow.MinLog10Repetitions:G6}");
        Assert.True(finite > 0, "the fixture must stress some node above the endurance limit");
    }

    [Fact]
    public void OneShotModeCountsTheBoundaryHalvesTheStandardWay()
    {
        // The same synthetic A,B,A,B,A history WITHOUT the repeating assumption: E1049's
        // as-is counting yields four half cycles of the same (range, mean) — total count
        // 2.0, the same damage — with the open end read honestly rather than assumed
        // periodic. The two modes agreeing on DAMAGE while differing in cycle structure
        // is exactly the option's contract.
        var (model, cases) = TwoCaseBar();
        var results = StructuralSolver.SolveAll(cases);
        var history = SyntheticHistory(model, [results[0], results[1], results[0], results[1], results[0]]);

        var repeating = FatigueAnalysis.Evaluate(
            history, FatigueMaterials.Steel1045,
            new RainflowFatigueOptions { AssumeRepeating = true });
        var oneShot = FatigueAnalysis.Evaluate(
            history, FatigueMaterials.Steel1045,
            new RainflowFatigueOptions { AssumeRepeating = false });

        int node = repeating.MaxDamageNode;
        Assert.True(node >= 0);
        var halves = oneShot.CyclesAt(node);
        Assert.Equal(4, halves.Count);
        foreach (var half in halves)
            Assert.Equal(0.5, half.Count);
        Assert.Equal(repeating.Damage[node], oneShot.Damage[node]);
        output.WriteLine(
            $"node {node}: damage {oneShot.Damage[node]:G6} both modes, "
            + $"{halves.Count} halves one-shot vs {repeating.CyclesAt(node).Count} entry repeating");
    }

    [Fact]
    public void RefusalsFireByName()
    {
        var (model, cases) = TwoCaseBar();
        var results = StructuralSolver.SolveAll(cases);

        // A single stored state carries no cycle.
        var single = SyntheticHistory(model, [results[0]]);
        var ex = Assert.Throws<FeaException>(() => FatigueAnalysis.Evaluate(
            single, FatigueMaterials.Steel1045));
        Assert.Contains("single state", ex.Message);

        // Mixed recovery settings across states book the recovery gap as amplitude.
        var mixed = SyntheticHistory(model, [results[0], results[1], results[0]]);
        mixed.States[1].Results.Recovery = StressRecovery.Superconvergent;
        var ex2 = Assert.Throws<FeaException>(() => FatigueAnalysis.Evaluate(
            mixed, FatigueMaterials.Steel1045));
        Assert.Contains("Recovery", ex2.Message);
    }

    // ---- the safety factor, verified by its own definition ---------------------------

    /// <summary>
    /// The spectrum factor IS the load multiplier to the damage target, so the oracle is
    /// to APPLY it: re-solve the WHOLE history with every load case scaled by the measured
    /// factor, re-count, re-accumulate, and the critical node's damage must land exactly on
    /// the target (its factor reading 1). The scaling goes through the solver rather than
    /// through the counted cycles, so what is checked is the claim a user reads — "scale
    /// the loads by this" — and not the arithmetic restating itself.
    /// </summary>
    [Theory]
    [InlineData(MeanStressCorrection.Goodman)]
    [InlineData(MeanStressCorrection.Gerber)]
    public void ScalingTheHistoryByTheLoadFactorLandsOnTheDamageTarget(
        MeanStressCorrection correction)
    {
        const double Repetitions = 1e4;
        var options = new RainflowFatigueOptions
        {
            Correction = correction,
            AssumeRepeating = true,
            DesignRepetitions = Repetitions,
        };
        var (_, history) = SpectrumHistory(1.0);
        var fatigue = FatigueAnalysis.Evaluate(history, FatigueMaterials.Steel1045, options);

        double k = fatigue.MinSafetyFactor;
        output.WriteLine($"{correction}: min load factor {k:G8} at node "
            + $"{fatigue.MinSafetyFactorNode}, damage/pass {fatigue.MaxDamage:G6}");
        Assert.True(double.IsFinite(k) && k > 0);

        var (_, scaledHistory) = SpectrumHistory(k);
        var scaled = FatigueAnalysis.Evaluate(scaledHistory, FatigueMaterials.Steel1045, options);
        Assert.Equal(1.0, scaled.MinSafetyFactor, 9);

        int node = scaled.MinSafetyFactorNode;
        double target = 1.0 / Repetitions;
        output.WriteLine($"  scaled: node {node} damage {scaled.Damage[node]:G10} "
            + $"vs target {target:G10}");
        Assert.True(
            Math.Abs(scaled.Damage[node] - target) <= 1e-6 * target,
            $"damage {scaled.Damage[node]} vs target {target}");
    }

    /// <summary>
    /// The DEFAULT target is infinite life: the multiplier at which damage first appears.
    /// Applied, the whole history sits at the endurance limit (factor 1), and a further 1%
    /// starts consuming life — the two-sided statement, which is what makes it a threshold
    /// rather than a number that merely looks plausible.
    /// </summary>
    [Fact]
    public void TheDefaultTargetIsInfiniteLifeAndItsFactorIsWhereDamageBegins()
    {
        var options = new RainflowFatigueOptions { AssumeRepeating = true };
        var (_, history) = SpectrumHistory(1.0);
        var fatigue = FatigueAnalysis.Evaluate(history, FatigueMaterials.Steel1045, options);

        double k = fatigue.MinSafetyFactor;
        output.WriteLine($"infinite-life factor {k:G8}; damage at unit load {fatigue.MaxDamage:G6}");
        Assert.True(double.IsFinite(k) && k > 1);
        Assert.Equal(0.0, fatigue.MaxDamage);            // nothing reaches the limit yet

        var (_, at) = SpectrumHistory(k);
        Assert.Equal(1.0, FatigueAnalysis.Evaluate(at, FatigueMaterials.Steel1045, options)
            .MinSafetyFactor, 9);

        var (_, over) = SpectrumHistory(k * 1.01);
        var overloaded = FatigueAnalysis.Evaluate(over, FatigueMaterials.Steel1045, options);
        output.WriteLine($"  +1%: damage {overloaded.MaxDamage:G6}, "
            + $"factor {overloaded.MinSafetyFactor:G6}");
        Assert.True(overloaded.MaxDamage > 0);

        // And the factor is exactly INVERSE in the load scale, which is what "radial"
        // means: the infinite-life answer is the multiplier to a fixed strength, so
        // scaling the history by c divides it by c. (The buckling load factor's identity,
        // in another discipline.)
        Assert.Equal(1.0 / 1.01, overloaded.MinSafetyFactor, 1e-9);
    }

    /// <summary>
    /// <b>The closed form, and exactly where it holds.</b> Damage is a sum of power-law
    /// terms, so where every cycle's equivalent amplitude is LINEAR in the multiplier the
    /// whole sum scales as k^(-1/b) and the factor to a damage target is exactly
    /// <c>(R·D)^b</c>. That is an INDEPENDENT construction — one line of algebra against a
    /// bracketed bisection over the S-N lookup — so agreement is evidence rather than a
    /// restatement. Two spectra satisfy it: a knee-less curve (nothing can cross a knee it
    /// does not have), and a steel spectrum entirely above its knee at both ends.
    /// </summary>
    [Fact]
    public void TheClosedFormIsExactWhereTheSpectrumIsAPurePowerLaw()
    {
        // (a) No endurance knee at all: aluminium, zero mean, so the correction is the
        //     identity and every equivalent amplitude is exactly k·a.
        var aluminium = FatigueMaterials.Aluminium6061T6;
        RainflowCycle[] free = [new(600, 0, 1.0), new(400, 0, 3.0), new(200, 0, 10.0)];
        AssertClosedForm(free, aluminium, 100);

        // (b) A knee that exists but is never crossed: every amplitude is above SAE 1045's
        //     249.5 MPa endurance limit at unit load, and the factor is above 1, so the
        //     active set cannot change.
        var steel = FatigueMaterials.Steel1045;
        RainflowCycle[] high = [new(600, 0, 1.0), new(560, 0, 2.0), new(520, 0, 4.0)];
        Assert.True(260.0 > steel.EnduranceLimit);
        AssertClosedForm(high, steel, 1e4);

        void AssertClosedForm(RainflowCycle[] spectrum, SnCurve curve, double repetitions)
        {
            double damage = FatigueAnalysis.Damage(spectrum, curve, MeanStressCorrection.None);
            double closed = Math.Pow(repetitions * damage, curve.FatigueStrengthExponent);
            double solved = FatigueAnalysis.LoadFactor(
                spectrum, curve, MeanStressCorrection.None, repetitions);
            output.WriteLine($"{curve.Name}: damage {damage:G6}, closed form {closed:G17}, "
                + $"solved {solved:G17}");
            Assert.True(solved > 1);
            Assert.True(
                Math.Abs(solved - closed) <= 1e-12 * closed,
                $"solved {solved} vs closed form {closed}");
        }
    }

    /// <summary>
    /// And exactly where it stops. Two things break the power law and both are ordinary:
    /// a cycle CROSSING the endurance knee as the multiplier grows (the coefficient is
    /// piecewise, so a closed form read off the unit-load damage overstates the factor),
    /// and a tensile mean under a correction (the equivalent amplitude
    /// <c>k·a/(1 − k·m/S_ut)</c> is not a power of k at all, so it understates). The
    /// misses are MEASURED — a shortcut nobody quantified would sit here looking harmless —
    /// while the solved factor lands on the target in both.
    /// </summary>
    [Fact]
    public void TheClosedFormMissesWhereTheKneeOrTheMeanEngages()
    {
        var curve = FatigueMaterials.Steel1045;

        // (a) The knee: one cycle above the 249.5 MPa limit and fifty below it, at a target
        //     whose answer scales the small ones past the knee.
        RainflowCycle[] crossing = [new(600, 0, 1.0), new(400, 0, 50.0)];
        Report("endurance knee", crossing, MeanStressCorrection.None, 1e3);

        // (b) The mean: the same amplitudes with a tensile mean under Goodman.
        RainflowCycle[] tensile = [new(400, 150, 1.0), new(300, 120, 20.0)];
        Report("tensile mean", tensile, MeanStressCorrection.Goodman, 1e4);

        void Report(
            string what, RainflowCycle[] spectrum, MeanStressCorrection correction,
            double repetitions)
        {
            double damage = FatigueAnalysis.Damage(spectrum, curve, correction);
            double closed = Math.Pow(repetitions * damage, curve.FatigueStrengthExponent);
            double solved = FatigueAnalysis.LoadFactor(spectrum, curve, correction, repetitions);
            output.WriteLine($"{what}: closed form {closed:G8}, solved {solved:G8}, "
                + $"miss {100 * (closed - solved) / solved:F2}%");
            Assert.True(
                Math.Abs(closed - solved) > 1e-3 * solved,
                $"{what}: the closed form must MISS here, and it read {closed} vs {solved}");

            // The solved factor is the one that lands: scaling the spectrum by it puts the
            // damage on the target, while the closed form's does not.
            double target = 1.0 / repetitions;
            Assert.Equal(target, FatigueAnalysis.Damage(Scale(spectrum, solved), curve, correction),
                target * 1e-9);
        }
    }

    /// <summary>
    /// The bridge to the static path, and the sharpest cross-check available: a spectrum of
    /// ONE cycle counted once reaches a damage of 1/R exactly when that cycle's life is R,
    /// which is the static radial factor against the curve's strength at R — a closed form
    /// for EVERY mean and both corrections, including the region where the general spectrum
    /// has none. Two entirely different constructions (a bisection over a Miner sum against
    /// a line intersection) must agree, and they do.
    /// </summary>
    [Theory]
    [InlineData(MeanStressCorrection.None)]
    [InlineData(MeanStressCorrection.Goodman)]
    [InlineData(MeanStressCorrection.Gerber)]
    public void ASingleCycleSpectrumIsTheStaticRadialFactor(MeanStressCorrection correction)
    {
        var curve = FatigueMaterials.Steel1045;
        foreach (double repetitions in new[] { 1e4, 2e5 })
        {
            double strength = curve.StressAt(repetitions);
            foreach (double mean in new[] { -100.0, 0.0, 50.0, 200.0, 400.0 })
            {
                RainflowCycle[] one = [new(300, mean, 1.0)];
                double solved = FatigueAnalysis.LoadFactor(one, curve, correction, repetitions);
                double radial = FatigueAnalysis.SafetyFactor(
                    curve, strength, 150, mean, correction);
                output.WriteLine(
                    $"{correction}, R {repetitions:G3}, mean {mean:F0}: "
                    + $"solved {solved:G12}, radial {radial:G12}");
                Assert.True(
                    Math.Abs(solved - radial) <= 1e-9 * radial,
                    $"mean {mean}: solved {solved} vs radial {radial}");
            }
        }
    }

    /// <summary>
    /// The degeneracy through the WHOLE pipeline: a constant-amplitude history's factor is
    /// the static pair's, per node. Against infinite life it is BIT-equal — the counted
    /// cycle's amplitude and mean are bit-equal to the static decomposition's (asserted
    /// above) and the same helper answers both — and against a stated target it reproduces
    /// the static answer at the corresponding cycle count: two full cycles per pass, so R
    /// repetitions is a life of 2R cycles.
    /// </summary>
    [Fact]
    public void AConstantAmplitudeHistoryGivesBackTheStaticFactor()
    {
        var curve = FatigueMaterials.Steel1045;
        var (model, cases) = TwoCaseBar();
        var solved = StructuralSolver.SolveAll(cases);
        var history = SyntheticHistory(model, [solved[0], solved[1], solved[0], solved[1], solved[0]]);

        var statics = FatigueAnalysis.Evaluate(solved[0], solved[1], curve);
        var spectrum = FatigueAnalysis.Evaluate(
            history, curve, new RainflowFatigueOptions { AssumeRepeating = true });

        int compared = 0;
        for (int v = 0; v < model.Mesh.NodeCount; v++)
        {
            if (statics.AlternatingStress[v] == 0)
            {
                // No swing: no counted cycle, so the spectrum has nothing to measure.
                Assert.True(double.IsNaN(spectrum.SafetyFactor[v]));
                continue;
            }
            compared++;
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(statics.SafetyFactor[v]),
                BitConverter.DoubleToInt64Bits(spectrum.SafetyFactor[v]));
        }
        Assert.True(compared > 0);
        output.WriteLine($"{compared} nodes bit-equal against infinite life; "
            + $"min factor {spectrum.MinSafetyFactor:G6}");

        // The stated-target half: 2.0 counted cycles per pass, so R repetitions IS a life
        // of 2R cycles and the two paths must agree there too.
        const double Repetitions = 1e4;
        var statedStatic = FatigueAnalysis.Evaluate(
            solved[0], solved[1], curve, new FatigueOptions { DesignLife = 2 * Repetitions });
        var statedSpectrum = FatigueAnalysis.Evaluate(
            history, curve,
            new RainflowFatigueOptions { AssumeRepeating = true, DesignRepetitions = Repetitions });
        for (int v = 0; v < model.Mesh.NodeCount; v++)
        {
            if (statics.AlternatingStress[v] == 0)
                continue;
            Assert.True(
                Math.Abs(statedSpectrum.SafetyFactor[v] - statedStatic.SafetyFactor[v])
                    <= 1e-9 * statedStatic.SafetyFactor[v],
                $"node {v}: {statedSpectrum.SafetyFactor[v]} vs {statedStatic.SafetyFactor[v]}");
        }
        output.WriteLine($"stated target: min factor {statedSpectrum.MinSafetyFactor:G6} "
            + $"vs static {statedStatic.MinSafetyFactor:G6}");
    }

    /// <summary>
    /// The infinite-life factor is a THRESHOLD, so it is asserted from both sides: a
    /// nanometre under it the damage is EXACTLY zero (every cycle at or below the limit
    /// contributes nothing at all, not something small), and a nanometre over it there is
    /// damage. A one-sided check would pass for any factor small enough.
    /// </summary>
    [Fact]
    public void TheInfiniteLifeFactorBracketsWhereDamageAppears()
    {
        var curve = FatigueMaterials.Steel1045;
        RainflowCycle[] spectrum = [new(500, 100, 1.0), new(300, 50, 4.0), new(200, -80, 9.0)];
        double k = FatigueAnalysis.LoadFactor(spectrum, curve, MeanStressCorrection.Goodman);

        double under = FatigueAnalysis.Damage(
            Scale(spectrum, k * (1 - 1e-9)), curve, MeanStressCorrection.Goodman);
        double over = FatigueAnalysis.Damage(
            Scale(spectrum, k * (1 + 1e-9)), curve, MeanStressCorrection.Goodman);
        output.WriteLine($"factor {k:G10}: damage {under:G6} under, {over:G6} over");
        Assert.Equal(0.0, under);
        Assert.True(over > 0);

        // And the jump it steps over IS the flat-line model's own artefact: the cycle that
        // crosses lands at the knee, where its life is exactly the endurance life.
        Assert.Equal(1.0 / curve.EnduranceLife!.Value, over, 1e-12);
    }

    /// <summary>A history that never moves carries no cycle, so the spectrum path answers
    /// NaN however large the steady stress is — rainflow measures CYCLES, and a steady load
    /// is a static-strength question the static pair answers instead (two identical cases
    /// report the S_ut/mean margin). Named rather than papered over.</summary>
    [Fact]
    public void AConstantHistoryCarriesNoCycleAndSaysSo()
    {
        var curve = FatigueMaterials.Steel1045;
        var (model, cases) = TwoCaseBar();
        var solved = StructuralSolver.SolveAll(cases);
        var steady = SyntheticHistory(model, [solved[0], solved[0], solved[0]]);

        var spectrum = FatigueAnalysis.Evaluate(steady, curve);
        Assert.Equal(0.0, spectrum.MaxDamage);
        Assert.True(double.IsNaN(spectrum.MinSafetyFactor));
        Assert.Equal(-1, spectrum.MinSafetyFactorNode);

        // The static pair, given the same steady load twice, reports the static margin.
        var statics = FatigueAnalysis.Evaluate(solved[0], solved[0], curve);
        Assert.True(double.IsFinite(statics.MinSafetyFactor));
        output.WriteLine($"steady load: spectrum NaN, static margin {statics.MinSafetyFactor:G6}");
    }

    [Fact]
    public void PublishesThreeFieldsWithTheStatedUnits()
    {
        var (model, cases) = TwoCaseBar();
        var solved = StructuralSolver.SolveAll(cases);
        var history = SyntheticHistory(model, [solved[0], solved[1], solved[0]]);
        var fields = FatigueAnalysis.Evaluate(history, FatigueMaterials.Steel1045).Fields();

        Assert.Equal(3, fields.Count);
        Assert.Equal("per history",
            fields.Single(f => f.Name == RainflowFatigueResults.FieldNames.Damage).Units);
        Assert.Equal("log10(repetitions)",
            fields.Single(f => f.Name == RainflowFatigueResults.FieldNames.Repetitions).Units);
        Assert.Equal("",
            fields.Single(f => f.Name == RainflowFatigueResults.FieldNames.LoadFactor).Units);
        Assert.All(fields, f => Assert.Equal(model.Mesh.NodeCount, f.Count));

        // The spectrum's field names must not collide with the static pair's, or a part
        // carrying both analyses would have one silently replace the other.
        Assert.DoesNotContain(fields, f => f.Name == FatigueResults.FieldNames.SafetyFactor);
    }

    /// <summary>A material with no endurance limit has no infinite life to measure
    /// against, so the spectrum factor needs a stated repetition count — refused by name,
    /// exactly as the static path refuses a missing design life, and stated it works.</summary>
    [Fact]
    public void AluminiumNeedsAStatedRepetitionCount()
    {
        var (model, cases) = TwoCaseBar();
        var solved = StructuralSolver.SolveAll(cases);
        var history = SyntheticHistory(model, [solved[0], solved[1], solved[0]]);

        var ex = Assert.Throws<FeaException>(() => FatigueAnalysis.Evaluate(
            history, FatigueMaterials.Aluminium6061T6));
        Assert.Contains("no endurance limit", ex.Message);
        Assert.Contains("DesignRepetitions", ex.Message);

        // Directly, too — the public arithmetic guards itself rather than trusting a caller.
        var direct = Assert.Throws<FeaException>(() => FatigueAnalysis.LoadFactor(
            [new RainflowCycle(400, 0, 1.0)], FatigueMaterials.Aluminium6061T6));
        Assert.Contains("DesignRepetitions", direct.Message);

        var stated = FatigueAnalysis.Evaluate(
            history, FatigueMaterials.Aluminium6061T6,
            new RainflowFatigueOptions { DesignRepetitions = 1e5 });
        Assert.True(double.IsFinite(stated.MinSafetyFactor));
        output.WriteLine($"6061-T6 to 1e5 repetitions: factor {stated.MinSafetyFactor:G6}");
    }

    [Fact]
    public void RefusesARepetitionCountBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RainflowFatigueOptions { DesignRepetitions = 0.4 });
        Assert.Throws<ArgumentOutOfRangeException>(() => FatigueAnalysis.LoadFactor(
            [new RainflowCycle(400, 0, 1.0)], FatigueMaterials.Steel1045,
            MeanStressCorrection.Goodman, 0.4));
    }

    // ---- fixtures --------------------------------------------------------------------

    /// <summary>Every counted cycle scaled by one load multiplier — what scaling the whole
    /// history does to the spectrum, since the stress is linear in the load.</summary>
    private static RainflowCycle[] Scale(RainflowCycle[] cycles, double factor)
    {
        var scaled = new RainflowCycle[cycles.Length];
        for (int i = 0; i < cycles.Length; i++)
            scaled[i] = new RainflowCycle(
                factor * cycles[i].Range, factor * cycles[i].Mean, cycles[i].Count);
        return scaled;
    }

    /// <summary>The proportional load pattern one pass of the history follows: it crosses
    /// zero so the counting sees real reversals, carries several distinct amplitudes so
    /// the spectrum is genuinely variable, and is tensile-biased so the mean-stress
    /// correction engages.</summary>
    private static readonly double[] SpectrumPattern =
        [1.0, -0.4, 0.75, 0.1, 0.9, -0.25, 0.5, -0.4, 1.0];

    /// <summary>The same bar driven through <see cref="SpectrumPattern"/> at a stated load
    /// scale — solved through ONE factorization, so a "re-solve with the loads scaled"
    /// oracle costs one more SolveAll.</summary>
    private static (StructuralModel Model, TransientResults History) SpectrumHistory(double scale)
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(20, 10, 10), 2, 1, 1);
        var mesh = AnalysisMesh.Of(tets);
        var cases = new StructuralModel[SpectrumPattern.Length];
        for (int i = 0; i < cases.Length; i++)
        {
            var model = new StructuralModel(mesh, Materials.Steel);
            model.Fix(Facets.Tag(StructuredTetMesh.XMin));
            model.Force(
                Facets.Tag(StructuredTetMesh.XMax),
                new Vector3d(9000.0 * scale * SpectrumPattern[i], 0, 0));
            cases[i] = model;
        }
        var solved = StructuralSolver.SolveAll(cases);
        return (cases[0], SyntheticHistory(cases[0], [.. solved]));
    }

    /// <summary>A small bar under an axial load, with the SolveAll pair at +1 and -0.35
    /// of the pattern — a proportional history whose extremes stress the fixture above
    /// the endurance limit at the loaded end.</summary>
    private static (StructuralModel Model, StructuralModel[] Cases) TwoCaseBar()
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(20, 10, 10), 2, 1, 1);
        var mesh = AnalysisMesh.Of(tets);

        StructuralModel Case(double factor)
        {
            var m = new StructuralModel(mesh, Materials.Steel);
            m.Fix(Facets.Tag(StructuredTetMesh.XMin));
            m.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(35000.0 * factor, 0, 0));
            return m;
        }

        var caseA = Case(1.0);
        var caseB = Case(-0.35);
        return (caseA, [caseA, caseB]);
    }

    /// <summary>A synthetic transient whose stored states are the given static results in
    /// sequence — the constant-amplitude history through the real
    /// <see cref="TransientResults"/> seam (internal constructors; the report is a
    /// placeholder since nothing here reads it).</summary>
    private static TransientResults SyntheticHistory(
        StructuralModel model, StructuralResults[] sequence)
    {
        int nodes = model.Mesh.NodeCount;
        var zero = new Vector3d[nodes];
        var states = new TransientState[sequence.Length];
        for (int i = 0; i < sequence.Length; i++)
        {
            states[i] = new TransientState(
                i, i * 1.0, 1.0, sequence[i], zero, zero, 0.0,
                sequence[i].Report.StrainEnergy, Vector3d.Zero, Vector3d.Zero);
        }
        var report = new TransientSolveReport
        {
            NodeCount = nodes,
            ElementCount = model.Mesh.ElementCount,
            Order = model.Mesh.Order,
            TotalDofs = 3 * nodes,
            FreeDofs = 3 * nodes,
            TimeStep = 1.0,
            Steps = sequence.Length - 1 < 1 ? 1 : sequence.Length - 1,
            Duration = sequence.Length - 1.0,
            Integration = TimeIntegration.AverageAcceleration,
            Damping = "undamped",
            MatrixNonZeros = 0,
            FactorNonZeros = 0,
            Method = FeaSolveMethod.Direct,
            Ordering = EngrCAD.Core.Solvers.SparseOrdering.Amd,
            Factorizations = 0,
            WorstRelativeResidual = 0,
            Converged = true,
            InitialEnergy = 0,
            FinalEnergy = 0,
            PeakEnergy = 0,
            WorkDone = 0,
            Dissipated = 0,
            EnergyBalanceResidual = 0,
            PeakDisplacement = 0,
            PeakDisplacementTime = 0,
            WorstEquilibriumResidual = 0,
            AssembleMs = 0,
            FactorMs = 0,
            StepMs = 0,
        };
        return new TransientResults(model, states, report);
    }
}
