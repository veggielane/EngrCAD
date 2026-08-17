using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The adaptive loop, measured against the only comparison that means anything: UNIFORM
/// refinement of the same model, by the same mesher and solver, to the same estimated
/// error. "The mesh got finer where the error was" is a picture; "it reached this error with
/// this many fewer elements" is a number, and it is the number an adaptive scheme exists to
/// move.
///
/// <para>The fixture is the CANTILEVER, deliberately: its built-in end carries a genuine
/// stress singularity (the recorded reason its convergence order caps at 1.86 whatever the
/// element), so it is simultaneously the case adaptivity should win on and the case that
/// must NOT be allowed to refine forever.</para>
///
/// <para>The runs are SHARED between tests through <c>Lazy</c> statics. An adaptive round
/// meshes and factors, so re-running the same configuration once per assertion would spend
/// minutes to measure the same numbers.</para>
/// </summary>
public class AdaptiveRefinementTests(ITestOutputHelper output)
{
    private static readonly Material Steel = new("adaptive steel", 210_000, 0.3);

    private const double Length = 24.0;
    private const double Width = 5.0;
    private const double Height = 5.0;
    private const double TipLoad = 400.0;
    private const double BaseSize = 3.0;
    private const int Budget = 15_000;

    /// <summary>The direct solver throughout: these meshes are small enough that an AMD-ordered
    /// factorization is quick, and a factorization either succeeds or refuses by name, where a
    /// capped iterative solve can return a plausible wrong answer for the loop to refine
    /// against.</summary>
    private static readonly StructuralSolveOptions Direct = new() { Method = FeaSolveMethod.Direct };

    /// <summary>
    /// A box surface REMESHED to a uniform edge length, which is what makes the comparison
    /// mean anything. A primitive's two-triangle faces put the mesher's red-refinement ladder
    /// in charge of the element size instead of <c>MaxElementSize</c> — measured on this
    /// beam, sizes 3.0 / 2.4 / 2.0 all came back within 2% of 7 500 elements, so a "uniform
    /// ladder" over them would have compared one mesh against itself — and at size 1.5 the
    /// same surface produced slivers the direct factorization refused outright.
    /// </summary>
    private static readonly Lazy<HalfEdgeMesh> Surface = new(() => Remesher.Remesh(
        MeshPrimitives.Box(new Aabb(Vector3d.Zero, new Vector3d(Length, Width, Height))),
        new RemeshOptions(1.25) { Iterations = 12 }).Mesh);

    /// <summary>Clamped at x = 0, a downward tip load at x = L — one model, built fresh on
    /// whatever mesh a round hands over.</summary>
    private static StructuralModel BuildBeam(AnalysisMesh mesh)
    {
        var model = new StructuralModel(mesh, Steel);
        model.Fix(Facets.OnPlane(Vector3d.Zero, -Vector3d.UnitX));
        model.Force(
            Facets.OnPlane(new Vector3d(Length, 0, 0), Vector3d.UnitX),
            new Vector3d(0, 0, -TipLoad));
        return model;
    }

    private static AdaptiveResult RunAdaptive(
        double target = 0.02, int rounds = 4, int budget = Budget) =>
        AdaptiveSolve.Run(Surface.Value, BuildBeam, new AdaptiveOptions
        {
            TargetRelativeError = target,
            MaxRounds = rounds,
            MaxElements = budget,
            Solve = Direct,
            Mesh = new TetMeshOptions { MaxElementSize = BaseSize },
        });

    private static readonly Lazy<AdaptiveResult> Adaptive = new(() => RunAdaptive());

    private readonly record struct UniformRun(
        double Size, int Elements, double Error, double PeakToMean);

    /// <summary>The control arm: the same model meshed UNIFORMLY at a ladder of sizes.</summary>
    private static readonly Lazy<UniformRun[]> Ladder = new(() =>
        new[] { 3.0, 2.4, 2.0, 1.7, 1.4, 1.2 }.Select(size =>
        {
            var tets = TetMesher.Mesh(
                Surface.Value,
                new TetMeshOptions { RefineQuality = true, MaxElementSize = size });
            var mesh = AnalysisMesh.Of(tets);
            var estimate = StructuralSolver.Solve(BuildBeam(mesh), Direct).ErrorEstimate;
            return new UniformRun(
                size, mesh.ElementCount, estimate.RelativeError, PeakToMean(estimate.ElementError));
        }).ToArray());

    private void ReportLadder()
    {
        output.WriteLine($"{"size",7} {"elements",10} {"error",9} {"peak/mean",10}");
        foreach (var run in Ladder.Value)
        {
            output.WriteLine(
                $"{run.Size,7:F2} {run.Elements,10:N0} {run.Error * 100,8:F2}% {run.PeakToMean,10:F2}");
        }
    }

    /// <summary>The uniform ladder's convergence rate as a least-squares slope of
    /// <c>ln(error)</c> against <c>ln(N)</c> over EVERY rung — the robust summary, where any
    /// single pair of rungs is not (see <see cref="PairwiseLadderRates"/>).</summary>
    private static double FittedLadderRate()
    {
        var xs = Ladder.Value.Select(r => Math.Log(r.Elements)).ToArray();
        var ys = Ladder.Value.Select(r => Math.Log(r.Error)).ToArray();
        double mx = xs.Average(), my = ys.Average();
        double num = 0, den = 0;
        for (int i = 0; i < xs.Length; i++)
        {
            num += (xs[i] - mx) * (ys[i] - my);
            den += (xs[i] - mx) * (xs[i] - mx);
        }
        return -num / den;
    }

    /// <summary>The rate between each consecutive pair of rungs — reported, never asserted on,
    /// because it is the noisiest quantity in the comparison.</summary>
    private static IEnumerable<double> PairwiseLadderRates() =>
        Ladder.Value.Zip(Ladder.Value.Skip(1), (a, b) =>
            Math.Log(a.Error / b.Error) / Math.Log((double)b.Elements / a.Elements));

    private static double PeakToMean(IReadOnlyList<double> elementError)
    {
        double sum = 0, peak = 0;
        int counted = 0;
        foreach (double value in elementError)
        {
            if (double.IsNaN(value))
                continue;
            sum += value;
            peak = Math.Max(peak, value);
            counted++;
        }
        return peak * counted / sum;
    }

    [Fact]
    public void AdaptiveRefinementReachesAnErrorWithFewerElementsThanUniformRefinement()
    {
        var adaptive = Adaptive.Value;
        output.WriteLine(adaptive.ToText());
        output.WriteLine("");
        ReportLadder();

        double reached = adaptive.RelativeError;
        int adaptiveElements = adaptive.ElementCount;

        // Bracket the adaptive error in the uniform ladder and INTERPOLATE, rather than
        // taking the first uniform mesh that happens to clear it: the ladder's granularity is
        // an artefact of the sizes swept, and rounding up to the next rung would flatter the
        // adaptive arm by whatever the gap between two rungs happens to be.
        var coarser = Ladder.Value.LastOrDefault(r => r.Error > reached);
        var finer = Ladder.Value.FirstOrDefault(r => r.Error <= reached);
        Assert.True(
            coarser.Elements > 0 && finer.Elements > 0,
            $"the uniform ladder does not bracket the adaptive run's {reached * 100:F2}%");

        // error ~ C * N^-a over the bracketing pair, solved for N at the adaptive error.
        double rate =
            Math.Log(coarser.Error / finer.Error) / Math.Log((double)finer.Elements / coarser.Elements);
        double uniformAtSameError =
            coarser.Elements * Math.Pow(coarser.Error / reached, 1.0 / rate);

        double interpolated = uniformAtSameError / adaptiveElements;
        double firstClearing = (double)finer.Elements / adaptiveElements;

        output.WriteLine("");
        output.WriteLine(
            $"at {reached * 100:F2}% estimated error: adaptive {adaptiveElements:N0} elements; "
            + $"uniform {uniformAtSameError:N0} interpolated ({interpolated:F2}x), "
            + $"{finer.Elements:N0} at the first ladder rung that clears it ({firstClearing:F2}x); "
            + $"uniform rate error ~ N^-{rate:F3}");

        Assert.True(
            interpolated > 1.15,
            $"adaptive refinement used {adaptiveElements:N0} elements against uniform's "
            + $"interpolated {uniformAtSameError:N0} ({interpolated:F2}x) - no measurable saving");

        // THE FIXTURE MUST STILL CARRY THE SINGULARITY, and the uniform rate is what says so.
        // Linear tetrahedra converge at O(h) in the energy norm on a SMOOTH problem, which in
        // element count is O(N^-1/3) = N^-0.333. A rate measurably BELOW that is the signature
        // of a singularity-limited fixture - which is exactly what a clamped end should
        // produce, and exactly what makes adaptivity worth doing here. A fixture that measured
        // 0.333 would be quietly telling us the clamp is not biting and the whole comparison
        // is on the wrong problem, so this is the "assert the fixture still carries the
        // configuration it exists to test" rule rather than a second convergence claim.
        //
        // IT IS ASSERTED ON A FIT OVER THE WHOLE LADDER, NOT ON THE BRACKETING PAIR, and the
        // spread printed above is why: a two-point rate is a ratio of two errors and this
        // ladder is not a convergence SEQUENCE - the mesher's red-refinement overshoot sets
        // the element counts, so the rungs jump unevenly (4 806 -> 11 410 is 2.37x) and the
        // pairwise rates swing 0.2235 to 0.3776, TWO of them above the smooth-problem 1/3.
        //
        // The failure mode is FRAGILE rather than FLAKY, which is worse. Everything here is
        // deterministic - `TwoRunsOfTheSameProblemAreIdentical` asserts the rounds bit for bit
        // - so which pair brackets does NOT wander with machine load. It moves on the next
        // unrelated LEGITIMATE change: a mesher tweak that shifts the rungs, a different
        // fixture density, a tighter solver tolerance. At that point a pair-tuned band fails
        // with a message about the physics ("the clamp singularity is not biting") when what
        // actually moved was the bracket - and a flake gets re-run and dismissed where this
        // gets believed. Note this does not contradict `FeaConvergenceTests`' rule that the
        // LAST PAIR is the honest estimate of a convergence ORDER: that rule is for a
        // refinement sequence with a pre-asymptotic head, and this is a set of meshes at
        // assorted sizes where no pair is representative.
        double fitted = FittedLadderRate();
        output.WriteLine(
            $"uniform rate over the whole ladder {fitted:F3} (bracketing pair {rate:F3}); "
            + $"pairwise {string.Join(", ", PairwiseLadderRates().Select(r => r.ToString("F3")))}");

        // The bound IS the theory constant rather than a tuned number.
        Assert.True(
            fitted < 1.0 / 3.0,
            $"the uniform ladder converges at N^-{fitted:F3}, at or above the smooth-problem "
            + "1/3 - the clamp singularity is not limiting this fixture, so the comparison is "
            + "running on a different problem from the one it claims");
        Assert.True(fitted > 0.15, $"implausible uniform rate N^-{fitted:F3}; the ladder is broken");
    }

    /// <summary>
    /// The saving above has a cause, and this is it: the estimated error is spread far more
    /// evenly over an adapted mesh than over a uniform one of the same size, so no small group
    /// of elements is holding the global figure up.
    ///
    /// <para><b>The comparison is at MATCHED COST rather than across rounds, and the
    /// measurement is why.</b> The obvious form — peak/mean falls with every round — does NOT
    /// hold on a fixture with a singularity, and the reason is instructive rather than a
    /// defect: halving an element far from the clamp cuts its error roughly in half, while
    /// halving one AT the clamp barely moves it, so a round that refines both widens the
    /// spread even as it lowers the global figure. Measured on this beam the across-rounds
    /// series is nearly flat (reported below). Against uniform refinement at the same element
    /// count the difference is large and one-signed, which is the claim worth asserting.</para>
    /// </summary>
    [Fact]
    public void TheErrorIsSpreadFarMoreEvenlyThanUniformRefinementAtTheSameCost()
    {
        var adaptive = Adaptive.Value;
        output.WriteLine(adaptive.ToText());
        output.WriteLine("");
        ReportLadder();

        output.WriteLine("");
        output.WriteLine("across rounds: " + string.Join(
            " -> ", adaptive.Rounds.Select(r => $"{r.PeakToMeanElementError:F2}")));

        // The uniform mesh nearest in size from above - the honest matched-cost partner.
        var partner = Ladder.Value
            .Where(r => r.Elements >= adaptive.ElementCount)
            .OrderBy(r => r.Elements)
            .First();

        double adaptiveSpread = adaptive.Rounds[^1].PeakToMeanElementError;
        output.WriteLine(
            $"peak/mean element error: adaptive {adaptiveSpread:F2} on {adaptive.ElementCount:N0} "
            + $"elements, uniform {partner.PeakToMean:F2} on {partner.Elements:N0}");

        Assert.True(
            adaptiveSpread < partner.PeakToMean,
            $"the adapted mesh spreads its error no better than uniform: {adaptiveSpread:F2} "
            + $"against {partner.PeakToMean:F2}");
    }

    [Fact]
    public void RefinementConcentratesAtTheClampedEndSingularity()
    {
        var adaptive = Adaptive.Value;
        output.WriteLine(adaptive.ToText());

        var coarse = AnalysisMesh.Of(TetMesher.Mesh(
            Surface.Value,
            new TetMeshOptions { RefineQuality = true, MaxElementSize = BaseSize }));

        const double band = 5.0;
        double nearBefore = MeanSizeIn(coarse, 0, band);
        double farBefore = MeanSizeIn(coarse, Length - band, Length);
        double nearAfter = MeanSizeIn(adaptive.Mesh, 0, band);
        double farAfter = MeanSizeIn(adaptive.Mesh, Length - band, Length);

        output.WriteLine("");
        output.WriteLine($"{"band",8} {"before",9} {"after",9} {"ratio",8}");
        output.WriteLine(
            $"{"clamp",8} {nearBefore,9:F3} {nearAfter,9:F3} {nearAfter / nearBefore,8:F3}");
        output.WriteLine(
            $"{"tip",8} {farBefore,9:F3} {farAfter,9:F3} {farAfter / farBefore,8:F3}");

        // The clamp carries the singularity, so it must be refined HARDER than the tip.
        // Stated as the two bands' own before/after ratios, which needs no assumption that
        // the starting mesh was uniform.
        Assert.True(
            nearAfter / nearBefore < farAfter / farBefore,
            $"the clamp was not refined harder than the tip: clamp {nearAfter / nearBefore:F3}, "
            + $"tip {farAfter / farBefore:F3}");

        // And in absolute terms the finished mesh really is finer there.
        Assert.True(
            nearAfter < farAfter,
            $"final mesh is not finer at the clamp: {nearAfter:F3} against {farAfter:F3} at the tip");
    }

    /// <summary>Mean element size (the mesher's own measure, twice the circumradius) over the
    /// elements whose centroid lies in an x band.</summary>
    private static double MeanSizeIn(AnalysisMesh mesh, double from, double to)
    {
        double sum = 0;
        int count = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            var a = mesh.Position(nodes[0]);
            var b = mesh.Position(nodes[1]);
            var c = mesh.Position(nodes[2]);
            var d = mesh.Position(nodes[3]);
            double x = (a.X + b.X + c.X + d.X) * 0.25;
            if (x < from || x > to)
                continue;
            sum += TetGeometry.TryCircumcentre(a, b, c, d, out _, out double radius)
                ? 2 * radius
                : TetGeometry.LongestEdge(a, b, c, d);
            count++;
        }
        return count > 0 ? sum / count : double.NaN;
    }

    /// <summary>
    /// The model-fed boundary-condition story, which is what makes rebuilding the model per
    /// round SOUND rather than merely convenient: a condition names a B-Rep FACE through
    /// <c>Facets.Tag</c>, refinement subdivides an input triangle but never re-attributes it,
    /// so the same selector resolves to the same face on every mesh the loop builds.
    ///
    /// <para>The assertion with teeth is the tagged facets' AREA, not their count: a selector
    /// that drifted onto a neighbouring face would still return facets, and only the area
    /// says which face they are on.</para>
    /// </summary>
    [Fact]
    public void ATaggedConditionResolvesToTheSameFaceOnEveryRound()
    {
        // A SHORTER beam here, and deliberately: a B-Rep tessellation gives a box two
        // triangles per face, so the mesher's own red-refinement ladder sets the element
        // count and the full-length beam spends its whole budget on round 0.
        const double tagLength = 12.0;
        var solid = Shape.Box(tagLength, Width, Height).ToBrep();
        var (surface, tags) = BRepTessellator.TessellateForTetMesh(solid);

        // The two end faces by QUERY rather than by coordinate: a face is located by its
        // Bounds().Center, never by its plane's stored origin, which is an arbitrary in-plane
        // point (a box cap's is a corner).
        var faces = solid.Faces.ToList();
        var ends = Enumerable.Range(0, faces.Count)
            .Where(i => faces[i].IsPlanar(out _, out var n)
                && Math.Abs(n.Dot(Vector3d.UnitX)) > 0.99)
            .OrderBy(i => faces[i].Bounds().Center.X)
            .ToList();
        Assert.True(ends.Count == 2, $"expected two X-normal faces, found {ends.Count}");
        int clampFace = ends[0];
        int tipFace = ends[^1];

        var clampAreas = new List<double>();
        var clampCounts = new List<int>();
        var appliedForces = new List<Vector3d>();

        StructuralModel Build(AnalysisMesh mesh)
        {
            var model = new StructuralModel(mesh, Steel);
            model.Fix(Facets.Tag(clampFace));
            model.Force(Facets.Tag(tipFace), new Vector3d(0, 0, -TipLoad));
            return model;
        }

        var adaptive = AdaptiveSolve.Run(surface, Build, new AdaptiveOptions
        {
            TargetRelativeError = 0.02,
            MaxRounds = 3,
            MaxElements = 20_000,
            Solve = Direct,
            Mesh = new TetMeshOptions { MaxElementSize = BaseSize, FacetTags = tags },
            OnRound = (_, results) =>
            {
                clampAreas.Add(TaggedArea(results.Mesh, clampFace));
                clampCounts.Add(TaggedCount(results.Mesh, clampFace));
                appliedForces.Add(results.Report.AppliedForce);
            },
        });

        output.WriteLine(adaptive.ToText());
        output.WriteLine("");
        output.WriteLine($"{"round",6} {"clamp area",14} {"clamp facets",13} {"applied Fz",12}");
        for (int i = 0; i < clampAreas.Count; i++)
        {
            output.WriteLine(
                $"{i,6} {clampAreas[i],14:F9} {clampCounts[i],13:N0} {appliedForces[i].Z,12:F6}");
        }

        Assert.True(clampAreas.Count >= 2, "the run must take more than one round");

        // The tagged face's area is the face's own area on EVERY round, to round-off: the
        // selector re-resolves rather than being re-derived.
        double exact = Width * Height;
        foreach (double area in clampAreas)
            Assert.Equal(exact, area, 9);

        // And refinement really did subdivide it, so the invariance above is a statement about
        // the tagging rather than about a mesh that never changed.
        Assert.True(
            clampCounts[^1] > clampCounts[0],
            $"the tagged face was never refined: {clampCounts[0]} facets throughout");

        // The load is a stated TOTAL, so the resultant is identical on every round however the
        // facets are split.
        foreach (var force in appliedForces)
            Assert.Equal(-TipLoad, force.Z, 6);
    }

    private static double TaggedArea(AnalysisMesh mesh, int tag)
    {
        double area = 0;
        for (int f = 0; f < mesh.FacetCount; f++)
        {
            if (mesh.FacetTag(f) != tag)
                continue;
            var nodes = mesh.Facet(f);
            var a = mesh.Position(nodes[0]);
            var b = mesh.Position(nodes[1]);
            var c = mesh.Position(nodes[2]);
            area += 0.5 * (b - a).Cross(c - a).Length;
        }
        return area;
    }

    private static int TaggedCount(AnalysisMesh mesh, int tag)
    {
        int count = 0;
        for (int f = 0; f < mesh.FacetCount; f++)
        {
            if (mesh.FacetTag(f) == tag)
                count++;
        }
        return count;
    }

    [Fact]
    public void TwoRunsOfTheSameProblemAreIdentical()
    {
        var a = RunAdaptive(target: 0.05, rounds: 2, budget: 8_000);
        var b = RunAdaptive(target: 0.05, rounds: 2, budget: 8_000);

        Assert.Equal(a.Outcome, b.Outcome);
        Assert.Equal(a.Rounds.Count, b.Rounds.Count);
        for (int i = 0; i < a.Rounds.Count; i++)
        {
            Assert.Equal(a.Rounds[i].ElementCount, b.Rounds[i].ElementCount);
            Assert.Equal(a.Rounds[i].FreeDofs, b.Rounds[i].FreeDofs);
            // Bit-identical, not merely close: the loop is a deterministic function of its
            // input, and two runs that reached one answer by different routes would show here.
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(a.Rounds[i].RelativeError),
                BitConverter.DoubleToInt64Bits(b.Rounds[i].RelativeError));
        }
    }

    [Fact]
    public void AnUnreachableTargetIsReportedRatherThanRefinedForever()
    {
        // 0.1% on a model with a built-in end is not reachable in two rounds, and arguably not
        // at all - the clamp is singular. The honest answer is the figure it stalled at, with
        // the outcome naming which cap it hit.
        var adaptive = RunAdaptive(target: 0.001, rounds: 2, budget: 8_000);
        output.WriteLine(adaptive.ToText());

        Assert.False(adaptive.Converged);
        Assert.True(
            adaptive.Outcome is AdaptiveOutcome.RoundsExhausted
                or AdaptiveOutcome.Stalled
                or AdaptiveOutcome.ElementBudgetExceeded,
            $"unexpected outcome {adaptive.Outcome}");
        Assert.True(adaptive.RelativeError > 0.001);

        // The result is still a real solve on a real mesh, not a refusal.
        Assert.True(adaptive.Results.MaxDisplacement > 0);
        Assert.True(adaptive.Mesh.ElementCount > 0);
    }

    [Fact]
    public void EveryRefusalNamesWhatItRefused()
    {
        var surface = Surface.Value;

        var target = Assert.Throws<FeaException>(() => AdaptiveSolve.Run(
            surface, BuildBeam, new AdaptiveOptions { TargetRelativeError = 0 }));
        Assert.Contains("TargetRelativeError", target.Message);

        var rounds = Assert.Throws<FeaException>(() => AdaptiveSolve.Run(
            surface, BuildBeam, new AdaptiveOptions { MaxRounds = 0 }));
        Assert.Contains("MaxRounds", rounds.Message);

        var band = Assert.Throws<FeaException>(() => AdaptiveSolve.Run(
            surface, BuildBeam, new AdaptiveOptions { MinRefineFactor = 0 }));
        Assert.Contains("MinRefineFactor", band.Message);

        var reduction = Assert.Throws<FeaException>(() => AdaptiveSolve.Run(
            surface, BuildBeam, new AdaptiveOptions { ReductionPerRound = 1.5 }));
        Assert.Contains("ReductionPerRound", reduction.Message);

        var gradation = Assert.Throws<FeaException>(() => AdaptiveSolve.Run(
            surface, BuildBeam, new AdaptiveOptions { SizeGradation = 0 }));
        Assert.Contains("SizeGradation", gradation.Message);

        var budget = Assert.Throws<FeaException>(() => AdaptiveSolve.Run(
            surface, BuildBeam, new AdaptiveOptions
            {
                MaxElements = 10,
                Mesh = new TetMeshOptions { MaxElementSize = BaseSize },
            }));
        output.WriteLine(budget.Message);
        Assert.Contains("over the budget", budget.Message);

        // A callback that builds on its own mesh would leave the loop refining geometry
        // nothing is solved on - the one mistake the callback shape makes possible.
        var elsewhere = AnalysisMesh.Of(TetMesher.Mesh(
            surface, new TetMeshOptions { RefineQuality = true, MaxElementSize = BaseSize }));
        var wrongMesh = Assert.Throws<FeaException>(() => AdaptiveSolve.Run(
            surface, _ => BuildBeam(elsewhere), new AdaptiveOptions
            {
                Mesh = new TetMeshOptions { MaxElementSize = BaseSize },
            }));
        Assert.Contains("DIFFERENT analysis mesh", wrongMesh.Message);
    }

    [Fact]
    public void AMeshTooCoarseToEstimateIsRefusedByName()
    {
        // No size target at all: the conforming mesh of a box has no interior corner node, so
        // no recovery patch exists and the estimate is honestly UNKNOWN. Refining against NaN
        // would be refining against nothing.
        var box = MeshPrimitives.Box(new Aabb(Vector3d.Zero, new Vector3d(Length, Width, Height)));
        var coarse = Assert.Throws<FeaException>(() => AdaptiveSolve.Run(
            box, BuildBeam, new AdaptiveOptions { Mesh = new TetMeshOptions() }));
        output.WriteLine(coarse.Message);
        Assert.Contains("no interior corner node", coarse.Message);
    }

    /// <summary>
    /// The size field's Lipschitz limit is load-bearing rather than cosmetic, and this is the
    /// measurement that says so: switching it off leaves a field with a CLIFF where a refined
    /// region meets an unrefined one, and a mesher answers a cliff with slivers.
    /// </summary>
    [Fact]
    public void WithoutGradationLimitingTheNextMeshIsMeasurablyWorse()
    {
        AdaptiveRound[] Rounds(double gradation)
        {
            try
            {
                return AdaptiveSolve.Run(Surface.Value, BuildBeam, new AdaptiveOptions
                {
                    TargetRelativeError = 0.02,
                    MaxRounds = 2,
                    MaxElements = 60_000,
                    SizeGradation = gradation,
                    Solve = new StructuralSolveOptions
                    {
                        Method = FeaSolveMethod.ConjugateGradient,
                    },
                    Mesh = new TetMeshOptions { MaxElementSize = BaseSize },
                }).Rounds.ToArray();
            }
            catch (FeaException ex)
            {
                // A run that cannot even be solved is the strongest form of "worse".
                output.WriteLine("refused: " + ex.Message);
                return [];
            }
        }

        var limited = Rounds(0.4);
        var unlimited = Rounds(double.PositiveInfinity);

        output.WriteLine($"limited  : {string.Join(", ", limited.Select(Describe))}");
        output.WriteLine($"unlimited: {string.Join(", ", unlimited.Select(Describe))}");

        Assert.True(limited.Length == 2, "the limited run must reach a second round");
        Assert.True(
            limited[1].RelativeError < limited[0].RelativeError,
            "the limited run's second round must improve the estimate");

        // Either the unlimited run failed outright, or it is measurably worse at round 1: a
        // bigger mesh for a worse (or barely better) estimate.
        bool worse =
            unlimited.Length < 2
            || unlimited[1].RelativeError > limited[1].RelativeError
            || unlimited[1].ElementCount > limited[1].ElementCount * 1.5;
        Assert.True(worse, "gradation limiting made no measurable difference");
    }

    private static string Describe(AdaptiveRound r) =>
        $"{r.ElementCount:N0} el @ {r.RelativeError * 100:F2}%";
}
