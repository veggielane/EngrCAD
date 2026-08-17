using System.Diagnostics;
using EngrCAD.Core;
using EngrCAD.Core.Spatial;
using EngrCAD.Mesh;

namespace EngrCAD.Fea;

/// <summary>How an adaptive run ended. Every value is a statement a caller can act on;
/// none of them means "something went wrong quietly".</summary>
public enum AdaptiveOutcome
{
    /// <summary>The estimated relative error reached
    /// <see cref="AdaptiveOptions.TargetRelativeError"/>.</summary>
    Converged,

    /// <summary>The round budget ran out with the target unmet. The figure the run stalled
    /// at is <see cref="AdaptiveResult.RelativeError"/> — a model with a genuine
    /// singularity converges slowly and honestly, and refining forever is not an
    /// improvement on saying so.</summary>
    RoundsExhausted,

    /// <summary>A round did not improve on the one before it, so more rounds would spend
    /// elements for nothing.</summary>
    Stalled,

    /// <summary>The next mesh would have exceeded <see cref="AdaptiveOptions.MaxElements"/>.
    /// The result is the last mesh that fitted, and its error is reported as measured.</summary>
    ElementBudgetExceeded,
}

/// <summary>
/// Controls for <see cref="AdaptiveSolve.Run"/>.
/// </summary>
public sealed record AdaptiveOptions
{
    /// <summary>
    /// The estimated relative error the loop is trying to reach — the classical ZZ figure
    /// <see cref="ErrorEstimate.RelativeError"/>, so 0.05 is the textbook 5%. Must lie
    /// strictly between 0 and 1.
    /// </summary>
    public double TargetRelativeError { get; init; } = 0.05;

    /// <summary>
    /// Hard cap on remeshing rounds. The loop stops at the cap and REPORTS, because a
    /// re-entrant corner or a point load has no mesh that reaches an arbitrary target and
    /// a loop that refines until it does is a loop that never returns.
    /// </summary>
    public int MaxRounds { get; init; } = 4;

    /// <summary>Element order for every round.</summary>
    public ElementOrder Order { get; init; } = ElementOrder.Linear;

    /// <summary>
    /// Smallest factor by which one round may shrink an element's target size — the
    /// refining end of the clamp band. The equidistribution rule is unbounded where an
    /// element's error is large (a singular corner asks for zero size), so the band is what
    /// makes a round's cost predictable: the element count grows by at most about
    /// <c>MinRefineFactor^-3</c>, so the default 0.5 is "at most halve an element per
    /// round", an eightfold ceiling.
    /// </summary>
    public double MinRefineFactor { get; init; } = 0.5;

    /// <summary>
    /// Largest factor by which one round may grow an element's target size — the coarsening
    /// end of the band. Above 1 the loop may take material back from regions the estimate
    /// says are over-resolved; exactly 1 forbids coarsening.
    /// </summary>
    public double MaxCoarsenFactor { get; init; } = 1.5;

    /// <summary>
    /// The most one round may try to cut the estimated error by, as a fraction of what it
    /// measured - so a round aims at <c>max(TargetRelativeError, measured x
    /// ReductionPerRound)</c> rather than at the final target every time.
    ///
    /// <para><b>Without it a far target degenerates into UNIFORM refinement</b>, which is
    /// the opposite of the point. The size rule is <c>(e_target/e)^(1/p)</c>: when the
    /// target is far below what one round can deliver, EVERY element's ratio falls below
    /// <see cref="MinRefineFactor"/> and the whole mesh clamps to the same factor - the
    /// elements go everywhere equally, and the run pays a uniform refinement's bill for an
    /// adaptive scheme's complexity. Aiming at a reachable improvement keeps the ratios
    /// SPREAD across the band, which is what puts elements where the error is. 1 disables
    /// it and asks for the final target every round.</para>
    ///
    /// <para><b>What it does NOT change is efficiency</b>, which is worth stating because it
    /// is the thing one would expect a knob like this to buy. Measured on the cantilever
    /// fixture, round 1 came back at 13 284 elements / 20.30%, 9 406 / 22.49% and 7 373 /
    /// 24.76% for 0.5, 0.7 and 0.85 — and against the same fixture's own uniform rate
    /// (error ~ N^-0.30) those three sit at 350, 350 and 358 on one curve. So it sets how
    /// BIG a round is, not how much a round is worth; 0.7 is the default as the middle of a
    /// measured plateau.</para>
    /// </summary>
    public double ReductionPerRound { get; init; } = 0.7;

    /// <summary>
    /// How fast the requested size may change with distance — the size field's Lipschitz
    /// bound, enforced as <c>h_i &lt;= h_j + SizeGradation x |c_i - c_j|</c> over
    /// node-adjacent elements. <see cref="double.PositiveInfinity"/> disables it.
    ///
    /// <para><b>It is not a smoothing nicety, it is what makes the next mesh solvable.</b>
    /// The raw field is piecewise constant over the old elements, so a refined region meets
    /// an unrefined one at a CLIFF, and a mesher asked to put a 0.4 mm element against a
    /// 3 mm one across a single face answers with slivers. Measured on the cantilever
    /// fixture without limiting: round 1's conjugate-gradient solve failed to converge at
    /// all (13 185 free DOF, 250 s, the estimate coming back at 100%), where the same round
    /// with limiting converges in the ordinary way. A size field with a cliff in it is a
    /// request for a bad mesh, not a request for a fine one.</para>
    /// </summary>
    public double SizeGradation { get; init; } = 0.4;

    /// <summary>An absolute floor on the requested element size, or null for none. The
    /// honest way to bound a run against a singularity, whose local error does not fall
    /// however small the elements get.</summary>
    public double? MinElementSize { get; init; }

    /// <summary>Refuse to solve a mesh larger than this, reporting
    /// <see cref="AdaptiveOutcome.ElementBudgetExceeded"/> and returning the last mesh that
    /// fitted.</summary>
    public int MaxElements { get; init; } = 400_000;

    /// <summary>
    /// Base meshing options, used for round 0 and carried into every later round. The
    /// caller's <c>FacetTags</c>, <c>RadiusEdgeRatio</c> and <c>MaxElementSize</c> ride
    /// through unchanged; a caller-supplied <c>SizingField</c> is COMPOSED with the
    /// adaptive one by taking the smaller of the two, so a stated grading is never
    /// silently discarded.
    /// <para><see cref="TetMeshOptions.RefineQuality"/> is forced on, because the sizing
    /// field is only consulted by the refinement passes it gates: without it the loop would
    /// compute a field every round and mesh as though it had not.</para>
    /// </summary>
    public TetMeshOptions Mesh { get; init; } = new();

    /// <summary>Solver settings for every round, or null for the defaults.</summary>
    public StructuralSolveOptions? Solve { get; init; }

    /// <summary>Called once per completed round with its summary and its results — the
    /// streaming seam, so a caller can write a <c>.vtu</c> per round without the loop
    /// retaining every mesh.</summary>
    public Action<AdaptiveRound, StructuralResults>? OnRound { get; init; }
}

/// <summary>What one round of <see cref="AdaptiveSolve.Run"/> did.</summary>
/// <param name="Number">0 for the first mesh.</param>
/// <param name="ElementCount">Elements solved this round.</param>
/// <param name="NodeCount">Nodes solved this round.</param>
/// <param name="FreeDofs">Degrees of freedom actually solved for.</param>
/// <param name="RelativeError">The ZZ figure — <see cref="ErrorEstimate.RelativeError"/>.</param>
/// <param name="ErrorNorm">Energy norm of the estimated error.</param>
/// <param name="PeakToMeanElementError">Largest element error over the mean — the
/// EQUIDISTRIBUTION measure. A uniform mesh over a model with a hot spot reads high; a
/// well-adapted mesh reads near 1, which is what "the error is spread evenly" means and
/// what a global figure alone cannot say.</param>
/// <param name="SmallestRequestedSize">Smallest element size the NEXT round was asked for,
/// or NaN when no next round was requested.</param>
/// <param name="PredictedNextElements">Roughly how many elements the next round's sizing
/// field asks for - <c>sum (h_old/h_new)^3</c> - or 0 when no next round was requested. It
/// is what the element budget is checked against, so a run that would melt says so before
/// paying for the mesh that proves it.</param>
/// <param name="FinestBoundaryFacet">Circumradius of the finest boundary facet in this
/// mesh — the size floor the input SURFACE imposes (see
/// <c>TetMeshDiagnostics.MinBoundaryFacetSize</c>). When a run stalls with this well above
/// the size it is asking for, the surface tessellation is the thing to change.</param>
/// <param name="MeshMilliseconds">Wall time spent meshing.</param>
/// <param name="SolveMilliseconds">Wall time spent solving and estimating.</param>
public readonly record struct AdaptiveRound(
    int Number,
    int ElementCount,
    int NodeCount,
    int FreeDofs,
    double RelativeError,
    double ErrorNorm,
    double PeakToMeanElementError,
    double SmallestRequestedSize,
    double PredictedNextElements,
    double FinestBoundaryFacet,
    double MeshMilliseconds,
    double SolveMilliseconds)
{
    /// <inheritdoc/>
    public override string ToString() =>
        $"round {Number}: {ElementCount:N0} elements, {FreeDofs:N0} free DOF, "
        + $"error {RelativeError * 100:F2}%, peak/mean {PeakToMeanElementError:F2}";
}

/// <summary>The result of an adaptive run: the final solve, and what every round cost.</summary>
public sealed class AdaptiveResult
{
    internal AdaptiveResult(
        AdaptiveOutcome outcome,
        StructuralResults results,
        TetMesh tets,
        IReadOnlyList<AdaptiveRound> rounds)
    {
        Outcome = outcome;
        Results = results;
        TetMesh = tets;
        Rounds = rounds;
    }

    /// <summary>How the run ended.</summary>
    public AdaptiveOutcome Outcome { get; }

    /// <summary>True only for <see cref="AdaptiveOutcome.Converged"/>.</summary>
    public bool Converged => Outcome == AdaptiveOutcome.Converged;

    /// <summary>The final solve — the answer, on the final mesh.</summary>
    public StructuralResults Results { get; }

    /// <summary>The final volume mesh.</summary>
    public TetMesh TetMesh { get; }

    /// <summary>The final analysis mesh.</summary>
    public AnalysisMesh Mesh => Results.Mesh;

    /// <summary>One entry per round, in order.</summary>
    public IReadOnlyList<AdaptiveRound> Rounds { get; }

    /// <summary>The final round's estimated relative error.</summary>
    public double RelativeError => Rounds[^1].RelativeError;

    /// <summary>Elements in the final mesh.</summary>
    public int ElementCount => Rounds[^1].ElementCount;

    /// <summary>An aligned per-round table plus the verdict.</summary>
    public string ToText()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine(
            $"{"round",5} {"elements",10} {"free DOF",10} {"error",9} {"peak/mean",10} "
            + $"{"mesh ms",9} {"solve ms",9}");
        foreach (var r in Rounds)
        {
            text.AppendLine(
                $"{r.Number,5} {r.ElementCount,10:N0} {r.FreeDofs,10:N0} "
                + $"{r.RelativeError * 100,8:F2}% {r.PeakToMeanElementError,10:F2} "
                + $"{r.MeshMilliseconds,9:F0} {r.SolveMilliseconds,9:F0}");
        }
        text.Append(ToString());
        return text.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() => Outcome switch
    {
        AdaptiveOutcome.Converged =>
            $"converged in {Rounds.Count} round(s) at {RelativeError * 100:F2}% "
            + $"on {ElementCount:N0} elements",
        AdaptiveOutcome.RoundsExhausted =>
            $"stopped after {Rounds.Count} round(s) at {RelativeError * 100:F2}% "
            + $"on {ElementCount:N0} elements; the target was not reached",
        AdaptiveOutcome.Stalled =>
            $"stalled after {Rounds.Count} round(s) at {RelativeError * 100:F2}% "
            + $"on {ElementCount:N0} elements; refining stopped improving the estimate",
        _ =>
            $"stopped after {Rounds.Count} round(s) at {RelativeError * 100:F2}% "
            + $"on {ElementCount:N0} elements; the next mesh would have exceeded the budget",
    };
}

/// <summary>
/// Solve, estimate, refine where the error is, repeat — the loop that turns
/// <see cref="StructuralResults.ErrorEstimate"/> from a report into a mesh.
///
/// <para><b>The estimate is the input, and that is the whole point.</b> A solve returns a
/// number whatever the mesh; the ZZ estimate says where that number is worth least
/// (<see cref="ErrorEstimate.ElementError"/>), and this loop spends elements exactly
/// there. The rule is the classical one, <c>h_new = h_old · (target/e_local)^(1/p)</c> for
/// an EQUIDISTRIBUTED error, with <c>p</c> the element degree because that is the
/// energy-norm rate; the per-element target comes from the global one by
/// <c>e_target = E_target/sqrt(N)</c>, which is what "equidistributed" means.</para>
///
/// <para><b>Two things make it more than a for-loop.</b> (1) <see cref="TetMesher"/>
/// re-meshes from the SURFACE, so a round cannot refine the previous volume mesh in place —
/// the errors have to travel as a SPATIAL field, which is a BVH over the old elements'
/// bounding boxes answering with the nearest element's requested size (a piecewise-constant
/// Voronoi field over the old centroids). (2) The stopping rule is a decision rather than a
/// convergence test: a genuine singularity never reaches a stated target, so the loop caps
/// its rounds and REPORTS the figure it stalled at — the same shape as boundary recovery's
/// non-convergence detection, and the same reason.</para>
///
/// <para><b>The size a round measures is the mesher's own.</b> <c>TetMeshOptions.SizingField</c>
/// is compared against twice a tetrahedron's circumradius, so that is what an element's
/// current size is read as here — which makes "ask for the size you already have" a fixed
/// point rather than an accidental refinement. And each round measures the size the previous
/// mesh ACHIEVED rather than the size it requested, so the loop self-corrects where the
/// input surface's own tessellation floors the element size
/// (<c>TetMeshDiagnostics.MinBoundaryFacetSize</c>) instead of asking again for something it
/// cannot have.</para>
///
/// <para><b>The model is rebuilt per round through a callback</b>, because a
/// <see cref="StructuralModel"/> is bound to one <see cref="AnalysisMesh"/> and every round
/// has a new one. That is sound precisely because boundary conditions are named by
/// <see cref="Facets"/> SELECTORS rather than by facet indices: a tag rides on
/// <c>TetFacet.SourceTriangle</c>, refinement subdivides an input triangle but never
/// re-attributes it, so <c>Facets.Tag(faceId)</c> resolves to the same B-Rep face on every
/// mesh the loop builds.</para>
///
/// <para><b>There is deliberately no recovery setting here</b>, which is worth stating
/// because the absence looks like an omission: <see cref="StructuralResults.ErrorEstimate"/>
/// is computed from the SUPERCONVERGENT recovery whatever
/// <see cref="StructuralResults.Recovery"/> is set to — that is what makes it an estimator
/// rather than a comparison of a field with itself — so the loop's input needs no knob and
/// offering one would suggest it changes the estimate. What
/// <see cref="StructuralResults.Recovery"/> chooses is the stress the FINAL result reports,
/// and the caller can set it on <see cref="AdaptiveResult.Results"/>.</para>
/// </summary>
public static class AdaptiveSolve
{
    /// <summary>
    /// Meshes, solves and refines until the estimated relative error reaches
    /// <paramref name="options"/>'s target or the loop runs out of rounds.
    /// </summary>
    /// <param name="surface">The closed surface mesh to fill — re-meshed every round.</param>
    /// <param name="buildModel">Builds the model for one round's mesh: materials, supports
    /// and loads. Called once per round with a fresh <see cref="AnalysisMesh"/>, and REQUIRED
    /// to build on the mesh it is handed (a model over some other mesh is refused by name,
    /// because the loop would then be refining a mesh nothing is solved on).</param>
    /// <param name="options">Controls, or null for the defaults.</param>
    /// <param name="progress">Optional cooperative cancellation and coarse progress.</param>
    /// <exception cref="FeaException">The target is out of range, the round budget is
    /// non-positive, the callback returned a model over a different mesh, the first mesh
    /// already exceeds the element budget, or the estimate is unavailable because the mesh
    /// has no interior corner node.</exception>
    public static AdaptiveResult Run(
        HalfEdgeMesh surface,
        Func<AnalysisMesh, StructuralModel> buildModel,
        AdaptiveOptions? options = null,
        ProgressCancel? progress = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(buildModel);
        options ??= new AdaptiveOptions();

        if (!(options.TargetRelativeError > 0) || options.TargetRelativeError >= 1)
        {
            throw new FeaException(
                $"AdaptiveOptions.TargetRelativeError must lie strictly between 0 and 1; "
                + $"got {options.TargetRelativeError:G6}. It is the classical ZZ relative "
                + "error, so 0.05 means 5%.");
        }
        if (options.MaxRounds < 1)
            throw new FeaException($"AdaptiveOptions.MaxRounds must be at least 1; got {options.MaxRounds}.");
        if (!(options.MinRefineFactor > 0) || options.MinRefineFactor > 1)
        {
            throw new FeaException(
                $"AdaptiveOptions.MinRefineFactor must lie in (0, 1]; got {options.MinRefineFactor:G6}. "
                + "It is the most one round may SHRINK an element, so 0.4 allows a 2.5x reduction.");
        }
        if (!(options.MaxCoarsenFactor >= 1))
        {
            throw new FeaException(
                $"AdaptiveOptions.MaxCoarsenFactor must be at least 1; got {options.MaxCoarsenFactor:G6}. "
                + "Values below 1 would forbid a round from leaving an element alone.");
        }
        if (!(options.SizeGradation > 0))
        {
            throw new FeaException(
                $"AdaptiveOptions.SizeGradation must be positive; got {options.SizeGradation:G6}. "
                + "It is how fast the requested element size may change with distance; pass "
                + "double.PositiveInfinity to disable the limit.");
        }
        if (!(options.ReductionPerRound > 0) || options.ReductionPerRound > 1)
        {
            throw new FeaException(
                $"AdaptiveOptions.ReductionPerRound must lie in (0, 1]; got "
                + $"{options.ReductionPerRound:G6}. It is the most one round aims to cut the "
                + "measured error by, and 1 means aim at the final target every round.");
        }
        if (options.MinElementSize is { } floor && !(floor > 0))
            throw new FeaException($"AdaptiveOptions.MinElementSize must be positive when stated; got {floor:G6}.");
        if (options.MaxElements < 1)
            throw new FeaException($"AdaptiveOptions.MaxElements must be at least 1; got {options.MaxElements}.");

        // RefineQuality gates the passes that read the sizing field at all, so the loop
        // forces it on: a field the mesher never consults would make every round after the
        // first identical to the first.
        var baseOptions = options.Mesh with { RefineQuality = true };
        var callerField = options.Mesh.SizingField;
        int degree = options.Order == ElementOrder.Linear ? 1 : 2;

        var rounds = new List<AdaptiveRound>();
        Func<Vector3d, double>? adaptiveField = null;
        StructuralResults? results = null;
        TetMesh? tets = null;

        for (int round = 0; round < options.MaxRounds; round++)
        {
            progress?.ThrowIfCancelled();

            var meshOptions = baseOptions with { SizingField = Compose(callerField, adaptiveField) };
            var meshWatch = Stopwatch.StartNew();
            TetMesh nextTets;
            TetMeshDiagnostics diagnostics;
            try
            {
                nextTets = TetMesher.Mesh(surface, meshOptions, out diagnostics, progress);
            }
            catch (TetMeshException ex)
            {
                throw new FeaException(
                    $"Adaptive refinement round {round} could not mesh the surface: {ex.Message}", ex);
            }
            meshWatch.Stop();

            if (nextTets.TetCount > options.MaxElements)
            {
                if (results is null)
                {
                    throw new FeaException(
                        $"The first adaptive mesh has {nextTets.TetCount:N0} elements, over the "
                        + $"budget of {options.MaxElements:N0}. Raise AdaptiveOptions.MaxElements, or "
                        + "coarsen the starting mesh (TetMeshOptions.MaxElementSize) - note that a "
                        + "fine surface tessellation floors the element size whatever that asks for; "
                        + $"the finest boundary facet here is {diagnostics.MinBoundaryFacetSize:G3}.");
                }
                return new AdaptiveResult(AdaptiveOutcome.ElementBudgetExceeded, results, tets!, rounds);
            }

            tets = nextTets;
            var mesh = options.Order == ElementOrder.Linear
                ? AnalysisMesh.Of(tets)
                : AnalysisMesh.Quadratic(tets);

            var model = buildModel(mesh)
                ?? throw new FeaException(
                    $"The model builder returned null for adaptive round {round}.");
            if (!ReferenceEquals(model.Mesh, mesh))
            {
                throw new FeaException(
                    $"The model builder returned a model over a DIFFERENT analysis mesh in adaptive "
                    + "round " + round + ". It must build on the mesh it is handed: the loop refines "
                    + "that mesh, so a model over another one would be solved on geometry the "
                    + "refinement never sees.");
            }

            var solveWatch = Stopwatch.StartNew();
            results = StructuralSolver.Solve(model, options.Solve, progress);
            var estimate = results.ErrorEstimate;
            solveWatch.Stop();

            if (!results.Report.Converged)
            {
                throw new FeaException(
                    $"The adaptive round {round} solve did not converge: the iterative solver hit "
                    + $"its cap at a relative residual of {results.Report.RelativeResidual:E3} over "
                    + $"{results.Report.FreeDofs:N0} free degrees of freedom. Refining against a "
                    + "solution that was never found would be refining against noise. Raise "
                    + "CgOptions.MaxIterations, or use FeaSolveMethod.Direct.");
            }

            if (double.IsNaN(estimate.RelativeError))
            {
                throw new FeaException(
                    $"The error estimate is unavailable on the adaptive round {round} mesh "
                    + $"({mesh.ElementCount:N0} elements): no recovery patch could be assembled, which "
                    + "means the mesh has no interior corner node. Start from a finer mesh "
                    + "(TetMeshOptions.MaxElementSize) so there is something to estimate against.");
            }

            var sizes = ElementSizes(mesh);
            double peakToMean = PeakToMean(estimate.ElementError);

            // The per-element target that equidistributes the global one. E_target follows
            // from the ZZ figure's own definition, eta = E / sqrt(U^2 + E^2), solved for E --
            // computed at THIS round's target, which is the final one only once it is within
            // reach (see AdaptiveOptions.ReductionPerRound).
            double target = options.TargetRelativeError;
            double roundTarget = Math.Max(
                target, estimate.RelativeError * options.ReductionPerRound);
            double targetErrorNorm =
                roundTarget * estimate.SolutionNorm / Math.Sqrt(1 - roundTarget * roundTarget);
            double perElementTarget = targetErrorNorm / Math.Sqrt(mesh.ElementCount);

            bool reached = estimate.RelativeError <= target;
            bool lastRound = round == options.MaxRounds - 1;
            bool stalled = rounds.Count > 0 && estimate.RelativeError >= rounds[^1].RelativeError;

            double smallestRequested = double.NaN;
            double predicted = 0;
            Func<Vector3d, double>? nextField = null;
            bool overBudget = false;
            if (!reached && !lastRound && !stalled)
            {
                var requested = RequestedSizes(
                    sizes, estimate.ElementError, perElementTarget, degree, options);
                LimitGradation(mesh, requested, options.SizeGradation);
                smallestRequested = Min(requested);
                predicted = PredictedElements(sizes, requested);
                // Cheaper AND more honest than meshing first: the sizing field is already
                // known, so an element of size h asked to become h' contributes about
                // (h/h')^3, and a run that would blow the budget can say so without paying
                // for the mesh that proves it.
                overBudget = predicted > options.MaxElements;
                if (!overBudget)
                    nextField = BuildField(mesh, requested);
            }

            var summary = new AdaptiveRound(
                round,
                mesh.ElementCount,
                mesh.NodeCount,
                results.Report.FreeDofs,
                estimate.RelativeError,
                estimate.ErrorNorm,
                peakToMean,
                smallestRequested,
                predicted,
                diagnostics.MinBoundaryFacetSize,
                meshWatch.Elapsed.TotalMilliseconds,
                solveWatch.Elapsed.TotalMilliseconds);
            rounds.Add(summary);
            options.OnRound?.Invoke(summary, results);

            if (reached)
                return new AdaptiveResult(AdaptiveOutcome.Converged, results, tets, rounds);
            if (overBudget)
                return new AdaptiveResult(
                    AdaptiveOutcome.ElementBudgetExceeded, results, tets, rounds);
            if (stalled)
                return new AdaptiveResult(AdaptiveOutcome.Stalled, results, tets, rounds);
            if (lastRound)
                return new AdaptiveResult(AdaptiveOutcome.RoundsExhausted, results, tets, rounds);

            adaptiveField = nextField;
        }

        // Unreachable: the loop returns on its last round.
        throw new FeaException("Adaptive refinement ended without a result.");
    }

    /// <summary>The smaller of two sizing fields, or whichever one exists.</summary>
    private static Func<Vector3d, double>? Compose(
        Func<Vector3d, double>? caller, Func<Vector3d, double>? adaptive)
    {
        if (caller is null) return adaptive;
        if (adaptive is null) return caller;
        return p => Math.Min(caller(p), adaptive(p));
    }

    /// <summary>
    /// Each element's CURRENT size, measured the way <see cref="TetMesher"/>'s own
    /// refinement measures it: twice the circumradius, since a tetrahedron is refined when
    /// its circumradius exceeds half the field. Reading it any other way would make "ask
    /// for the size you already have" a request to refine.
    /// <para>A sliver's circumradius over-states its size, so a sliver is asked to be
    /// coarser than intended — the safe direction, since the loop then simply takes another
    /// round rather than over-refining.</para>
    /// </summary>
    private static double[] ElementSizes(AnalysisMesh mesh)
    {
        var sizes = new double[mesh.ElementCount];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            var a = mesh.Position(nodes[0]);
            var b = mesh.Position(nodes[1]);
            var c = mesh.Position(nodes[2]);
            var d = mesh.Position(nodes[3]);
            sizes[e] = TetGeometry.TryCircumcentre(a, b, c, d, out _, out double radius)
                ? 2 * radius
                : TetGeometry.LongestEdge(a, b, c, d);
        }
        return sizes;
    }

    /// <summary>
    /// The equidistribution rule, clamped: <c>h_new = h_old · (e_target/e)^(1/p)</c>.
    /// </summary>
    private static double[] RequestedSizes(
        double[] sizes,
        IReadOnlyList<double> elementError,
        double perElementTarget,
        int degree,
        AdaptiveOptions options)
    {
        var requested = new double[sizes.Length];
        for (int e = 0; e < sizes.Length; e++)
        {
            double error = elementError[e];
            double ratio = error > 0 && !double.IsNaN(error)
                ? Math.Pow(perElementTarget / error, 1.0 / degree)
                : options.MaxCoarsenFactor;

            ratio = Math.Clamp(ratio, options.MinRefineFactor, options.MaxCoarsenFactor);
            double size = ratio * sizes[e];
            if (options.MinElementSize is { } floor)
                size = Math.Max(size, floor);
            requested[e] = size;
        }
        return requested;
    }

    /// <summary>
    /// The requested sizes as a SPATIAL field: a BVH over the old elements' bounding boxes,
    /// answering with the nearest element's size. Nearest by CENTROID, so the field is
    /// piecewise constant over the old centroids' Voronoi cells; the BVH prunes on box
    /// distance, which is sound because an element's centroid lies inside its own box, so a
    /// node's box distance is a lower bound on any centroid distance within it.
    /// </summary>
    private static Func<Vector3d, double> BuildField(AnalysisMesh mesh, double[] requested)
    {
        int count = mesh.ElementCount;
        var boxes = new Aabb[count];
        var centroids = new Vector3d[count];
        for (int e = 0; e < count; e++)
        {
            var nodes = mesh.Element(e);
            var a = mesh.Position(nodes[0]);
            var b = mesh.Position(nodes[1]);
            var c = mesh.Position(nodes[2]);
            var d = mesh.Position(nodes[3]);
            boxes[e] = Aabb.Empty.Union(a).Union(b).Union(c).Union(d);
            centroids[e] = (a + b + c + d) * 0.25;
        }

        var bvh = Bvh.Build(boxes);
        var sizes = requested;
        return p =>
        {
            var metric = new CentroidDistance(centroids, p);
            return bvh.Nearest(p, ref metric, out int nearest, out _) ? sizes[nearest] : 0;
        };
    }

    private readonly struct CentroidDistance(Vector3d[] centroids, Vector3d point) : IBvhDistance
    {
        public double DistanceTo(int item) => centroids[item].DistanceTo(point);
    }

    /// <summary>Largest element error over the mean — 1 for a perfectly equidistributed
    /// mesh, and NaN for a mesh with no elements carrying error.</summary>
    private static double PeakToMean(IReadOnlyList<double> elementError)
    {
        double sum = 0, peak = 0;
        int counted = 0;
        for (int e = 0; e < elementError.Count; e++)
        {
            double value = elementError[e];
            if (double.IsNaN(value))
                continue;
            sum += value;
            peak = Math.Max(peak, value);
            counted++;
        }
        return counted > 0 && sum > 0 ? peak * counted / sum : double.NaN;
    }

    /// <summary>
    /// Enforces the size field's Lipschitz bound over NODE-adjacent elements, in place. Each
    /// pass relaxes every node's incident elements against the smallest size reachable
    /// through that node; sizes only ever fall, so the sweep is monotone and terminates.
    /// <para>Node adjacency rather than face adjacency, deliberately: a cliff between two
    /// elements that merely share an edge or a corner produces exactly the same sliver as
    /// one between face neighbours, and the node table is what the mesh already hands out.</para>
    /// </summary>
    private static void LimitGradation(AnalysisMesh mesh, double[] sizes, double gradation)
    {
        if (double.IsPositiveInfinity(gradation))
            return;

        int count = mesh.ElementCount;
        var centroids = new Vector3d[count];
        for (int e = 0; e < count; e++)
        {
            var nodes = mesh.Element(e);
            centroids[e] = (mesh.Position(nodes[0]) + mesh.Position(nodes[1])
                          + mesh.Position(nodes[2]) + mesh.Position(nodes[3])) * 0.25;
        }

        // Corner nodes only: the mid-edge nodes of a quadratic element carry no adjacency a
        // corner does not already carry.
        var incident = new List<int>[mesh.NodeCount];
        for (int e = 0; e < count; e++)
        {
            var nodes = mesh.Element(e);
            for (int i = 0; i < 4; i++)
                (incident[nodes[i]] ??= []).Add(e);
        }

        for (int pass = 0; pass < 8; pass++)
        {
            bool changed = false;
            for (int n = 0; n < incident.Length; n++)
            {
                var elements = incident[n];
                if (elements is null || elements.Count < 2)
                    continue;

                var p = mesh.Position(n);
                double reach = double.PositiveInfinity;
                foreach (int e in elements)
                    reach = Math.Min(reach, sizes[e] + gradation * centroids[e].DistanceTo(p));

                foreach (int e in elements)
                {
                    double limit = reach + gradation * centroids[e].DistanceTo(p);
                    if (limit < sizes[e])
                    {
                        sizes[e] = limit;
                        changed = true;
                    }
                }
            }
            if (!changed)
                return;
        }
    }

    /// <summary>Roughly how many elements the requested sizes ask for: an element of size h
    /// asked to become h' fills its own volume with about <c>(h/h')^3</c> of them.</summary>
    private static double PredictedElements(double[] sizes, double[] requested)
    {
        double total = 0;
        for (int e = 0; e < sizes.Length; e++)
        {
            double ratio = sizes[e] / requested[e];
            total += ratio * ratio * ratio;
        }
        return total;
    }

    private static double Min(double[] values)
    {
        double best = double.PositiveInfinity;
        foreach (double value in values)
            best = Math.Min(best, value);
        return best;
    }
}
