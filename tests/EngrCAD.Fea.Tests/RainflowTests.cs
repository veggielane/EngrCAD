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

    // ---- fixtures --------------------------------------------------------------------

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
