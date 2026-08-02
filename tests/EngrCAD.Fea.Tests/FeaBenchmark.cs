using System.Diagnostics;
using EngrCAD.Core;
using EngrCAD.Core.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// What a structural solve costs, and what the two big levers buy. Inert unless
/// <c>ENGRCAD_BENCH</c> is set:
/// <code>
/// $env:ENGRCAD_BENCH = "1"
/// dotnet test tests/EngrCAD.Fea.Tests -c Release --filter FullyQualifiedName~FeaBenchmark -l "console;verbosity=detailed"
/// </code>
///
/// <para>Measured on the reference machine (win-x64, i9-9900K, .NET 10.0.302,
/// <b>Release</b>, otherwise idle). Debug is several times slower and its numbers mean
/// nothing.</para>
/// </summary>
public class FeaBenchmark(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ENGRCAD_BENCH") is not (null or "");

    private static readonly Material Steel = new("benchmark steel", 210_000, 0.3);
    private static readonly Vector3d Size = new(40, 20, 10);

    private static StructuralModel Cantilever(ElementOrder order, int n)
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, Size, 4 * n, 2 * n, n);
        var mesh = order == ElementOrder.Linear ? AnalysisMesh.Of(tets) : AnalysisMesh.Quadratic(tets);
        var model = new StructuralModel(mesh, Steel);
        model.Fix(StructuredTetMesh.XMin);
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -4000));
        return model;
    }

    [Fact]
    public void OrderingIsTheLeverThatDecidesWhetherAQuadraticSolveIsPractical()
    {
        if (!Enabled)
            return;

        output.WriteLine($"{"elements",10} {"free DOF",10} {"ordering",9} {"factor nnz",12} {"factor ms",10} {"solve ms",9}");
        foreach (var (order, n) in new[]
        {
            (ElementOrder.Linear, 3), (ElementOrder.Linear, 5),
            (ElementOrder.Quadratic, 2), (ElementOrder.Quadratic, 3),
        })
        {
            foreach (var ordering in new[] { SparseOrdering.Natural, SparseOrdering.Amd })
            {
                var results = StructuralSolver.Solve(
                    Cantilever(order, n), new StructuralSolveOptions { Ordering = ordering });
                var r = results.Report;
                output.WriteLine(
                    $"{r.ElementCount,10:N0} {r.FreeDofs,10:N0} {ordering,9} {r.FactorNonZeros,12:N0} "
                    + $"{r.FactorMs,10:F0} {r.SolveMs,9:F1}   ({order})");
            }
        }
    }

    [Fact]
    public void DirectVersusIterative()
    {
        if (!Enabled)
            return;

        // The large linear rows are the point: the crossover only means something if it is
        // measured where the factorization actually hurts. n = 12 is the 46 800-DOF case
        // whose factor takes 79 s in ThroughputAcrossTheWholePipeline.
        output.WriteLine($"{"elements",10} {"free DOF",10} {"direct ms",10} {"CG iters",9} {"CG ms",9}");
        foreach (var (order, n) in new[]
        {
            (ElementOrder.Linear, 4), (ElementOrder.Linear, 6),
            (ElementOrder.Linear, 8), (ElementOrder.Linear, 12),
            (ElementOrder.Quadratic, 2), (ElementOrder.Quadratic, 3),
            (ElementOrder.Quadratic, 4),
        })
        {
            var direct = StructuralSolver.Solve(Cantilever(order, n));
            var iterative = StructuralSolver.Solve(Cantilever(order, n), new StructuralSolveOptions
            {
                Method = FeaSolveMethod.ConjugateGradient,
                Cg = new CgOptions { RelativeTolerance = 1e-10 },
            });
            output.WriteLine(
                $"{direct.Report.ElementCount,10:N0} {direct.Report.FreeDofs,10:N0} "
                + $"{direct.Report.FactorMs + direct.Report.SolveMs,10:F0} "
                + $"{iterative.Report.Iterations,9} {iterative.Report.SolveMs,9:F0}   ({order}, "
                + $"CG {(iterative.Report.Converged ? "converged" : "STALLED")})");
        }
    }

    /// <summary>
    /// What <see cref="StructuralSolver.SolveAll"/> buys: the classic second argument for a
    /// direct solver, measured rather than asserted. N load cases through one factorization
    /// against the same N solved one at a time.
    ///
    /// <para><b>Interleaved, alternating, best of three.</b> This machine returns absolute
    /// times several-fold apart across sittings, so an A/B taken in two sittings is noise
    /// with units; and a warm-up BUDGET rather than a warm-up count, because JIT tiering
    /// makes a single warm-up call meaningless.</para>
    /// </summary>
    [Fact]
    public void MultipleLoadCases()
    {
        if (!Enabled)
            return;

        static IReadOnlyList<StructuralModel> Cases(AnalysisMesh mesh, int count)
        {
            var list = new List<StructuralModel>();
            for (int i = 0; i < count; i++)
            {
                var model = new StructuralModel(mesh, Steel);
                model.Fix(StructuredTetMesh.XMin);
                model.Force(
                    Facets.Tag(StructuredTetMesh.XMax),
                    new Vector3d(200 * i, 500 * (i % 3), -4000 + 100 * i));
                list.Add(model);
            }
            return list;
        }

        static AnalysisMesh MeshFor(ElementOrder order, int n)
        {
            var tets = StructuredTetMesh.Box(Vector3d.Zero, Size, 4 * n, 2 * n, n);
            return order == ElementOrder.Linear ? AnalysisMesh.Of(tets) : AnalysisMesh.Quadratic(tets);
        }

        // Warm-up BUDGET, not a warm-up count.
        var warmMesh = MeshFor(ElementOrder.Linear, 2);
        var warmUntil = Stopwatch.StartNew();
        while (warmUntil.Elapsed.TotalSeconds < 1.5)
            _ = StructuralSolver.SolveAll(Cases(warmMesh, 2));

        output.WriteLine(
            $"{"order",10} {"free DOF",10} {"cases",6} {"separate ms",12} {"shared ms",10} "
            + $"{"speedup",8} {"factor ms",10} {"per extra rhs",14}");
        foreach (var (order, n, count) in new[]
        {
            (ElementOrder.Linear, 6, 4), (ElementOrder.Linear, 8, 4),
            (ElementOrder.Quadratic, 2, 4), (ElementOrder.Quadratic, 3, 4),
            (ElementOrder.Quadratic, 3, 8),
        })
        {
            var mesh = MeshFor(order, n);
            double separate = double.MaxValue, shared = double.MaxValue;
            double factorMs = 0, perExtra = 0;
            int freeDofs = 0;
            for (int trial = 0; trial < 3; trial++)
            {
                var stopwatch = Stopwatch.StartNew();
                foreach (var model in Cases(mesh, count))
                    _ = StructuralSolver.Solve(model);
                separate = Math.Min(separate, stopwatch.Elapsed.TotalMilliseconds);

                stopwatch.Restart();
                var all = StructuralSolver.SolveAll(Cases(mesh, count));
                shared = Math.Min(shared, stopwatch.Elapsed.TotalMilliseconds);
                factorMs = all[0].Report.FactorMs;
                freeDofs = all[0].Report.FreeDofs;
                perExtra = all.Skip(1).Average(r => r.Report.SolveMs);
            }
            output.WriteLine(
                $"{order,10} {freeDofs,10:N0} {count,6} {separate,12:F0} {shared,10:F0} "
                + $"{separate / shared,7:F2}x {factorMs,10:F0} {perExtra,13:F2} ms");
        }
    }

    /// <summary>
    /// Where the ASSEMBLY's time goes, which is a different question from where the SOLVE's
    /// time goes and has to be asked before anything is parallelised: the element loop looks
    /// embarrassingly parallel, but only the part that computes element stiffnesses actually
    /// is — the scatter into the builder is a shared write whose ORDER decides the last bits
    /// of every summed entry.
    /// </summary>
    [Fact]
    public void WhereAssemblyTimeGoes()
    {
        if (!Enabled)
            return;

        var warmUntil = Stopwatch.StartNew();
        while (warmUntil.Elapsed.TotalSeconds < 1.5)
            _ = StructuralSolver.Solve(Cantilever(ElementOrder.Linear, 2));

        output.WriteLine(
            $"{"order",10} {"elements",10} {"free DOF",10} {"ke only",9} {"assemble",9} "
            + $"{"ke share",9} {"reactions",10}");
        foreach (var (order, n) in new[]
        {
            (ElementOrder.Linear, 6), (ElementOrder.Linear, 8),
            (ElementOrder.Quadratic, 3), (ElementOrder.Quadratic, 4),
        })
        {
            var model = Cantilever(order, n);
            var mesh = model.Mesh;
            var rule = TetQuadrature.For(mesh.Order);
            int perElement = mesh.NodesPerElement;
            int elementDofs = 3 * perElement;

            double keOnly = double.MaxValue, assemble = double.MaxValue, reactions = double.MaxValue;
            for (int trial = 0; trial < 3; trial++)
            {
                // Element stiffnesses alone, thrown away: the parallelisable half.
                var ke = new double[elementDofs * elementDofs];
                var positions = new Vector3d[perElement];
                var stopwatch = Stopwatch.StartNew();
                double sink = 0;
                for (int e = 0; e < mesh.ElementCount; e++)
                {
                    var nodes = mesh.Element(e);
                    for (int i = 0; i < perElement; i++)
                        positions[i] = mesh.Position(nodes[i]);
                    TetElement.Stiffness(mesh.Order, positions, model.MaterialOf(e), rule, ke);
                    sink += ke[0];
                }
                keOnly = Math.Min(keOnly, stopwatch.Elapsed.TotalMilliseconds);
                Assert.NotEqual(0.0, sink);

                var results = StructuralSolver.Solve(model);
                assemble = Math.Min(assemble, results.Report.AssembleMs);
                reactions = Math.Min(reactions, results.Report.ReactionMs);
            }

            // The other two thirds of the question: of the 90% that is NOT element
            // stiffness, how much is appending to the builder and how much is packing it?
            double adds = double.MaxValue, pack = double.MaxValue;
            int rawEntries = 0, longestRow = 0;
            for (int trial = 0; trial < 3; trial++)
            {
                var reduced = FeaAssembly.ReducedIndices(model, out int freeCount);
                var builder = new SparseMatrixBuilder(freeCount, freeCount);
                var ke = new double[elementDofs * elementDofs];
                var positions = new Vector3d[perElement];
                var dofs = new int[elementDofs];
                var perRow = new int[freeCount];
                int entries = 0;

                var stopwatch = Stopwatch.StartNew();
                for (int e = 0; e < mesh.ElementCount; e++)
                {
                    var nodes = mesh.Element(e);
                    for (int i = 0; i < perElement; i++)
                    {
                        positions[i] = mesh.Position(nodes[i]);
                        for (int a = 0; a < 3; a++)
                            dofs[3 * i + a] = reduced[3 * nodes[i] + a];
                    }
                    TetElement.Stiffness(mesh.Order, positions, model.MaterialOf(e), rule, ke);
                    for (int i = 0; i < elementDofs; i++)
                    {
                        int ri = dofs[i];
                        if (ri < 0)
                            continue;
                        for (int j = 0; j < elementDofs; j++)
                        {
                            double v = ke[i * elementDofs + j];
                            if (v == 0)
                                continue;
                            int rj = dofs[j];
                            if (rj >= 0 && ri <= rj)
                            {
                                builder.Add(ri, rj, v);
                                perRow[ri]++;
                                entries++;
                            }
                        }
                    }
                }
                adds = Math.Min(adds, stopwatch.Elapsed.TotalMilliseconds);

                stopwatch.Restart();
                _ = builder.ToSymmetricUpper();
                pack = Math.Min(pack, stopwatch.Elapsed.TotalMilliseconds);
                rawEntries = entries;
                longestRow = perRow.Max();
            }

            output.WriteLine(
                $"{order,10} {mesh.ElementCount,10:N0} {3 * mesh.NodeCount,10:N0} {keOnly,9:F0} "
                + $"{assemble,9:F0} {keOnly / assemble,8:P0} {reactions,10:F0}"
                + $"   [ke+adds {adds:F0} ms, pack {pack:F0} ms, {rawEntries:N0} raw entries, "
                + $"longest row {longestRow}]");
        }
    }

    /// <summary>
    /// What would actually move the factorization wall, read off the symbolic pass rather
    /// than guessed from the algorithm's name. Three numbers decide it and none of them
    /// needs the factorization to be run: how much work there is
    /// (<see cref="SparseFactorAnalysis.UpdateCount"/>), how much of it is stuck on one
    /// dependency chain (<see cref="SparseFactorAnalysis.ParallelCeiling"/>), and how long
    /// the columns are (<see cref="SparseFactorAnalysis.LongestColumn"/>, which is what a
    /// blocked/supernodal kernel pays in proportion to).
    /// </summary>
    [Fact]
    public void WhatWouldMoveTheFactorizationWall()
    {
        if (!Enabled)
            return;

        output.WriteLine(
            $"{"order",10} {"free DOF",10} {"ordering",9} {"factor nnz",12} {"updates",10} "
            + $"{"longest col",12} {"parallel ceiling",17} {"analyse ms",11}");
        foreach (var (order, n) in new[]
        {
            (ElementOrder.Linear, 4), (ElementOrder.Linear, 8), (ElementOrder.Linear, 12),
            (ElementOrder.Quadratic, 2), (ElementOrder.Quadratic, 4),
        })
        {
            var model = Cantilever(order, n);
            var reduced = FeaAssembly.ReducedIndices(model, out int freeCount);
            var matrix = FeaAssembly.Reduce(
                FeaAssembly.Stiffness(model, TetQuadrature.For(model.Mesh.Order)), reduced, freeCount);

            foreach (var ordering in new[] { SparseOrdering.Natural, SparseOrdering.Amd })
            {
                var stopwatch = Stopwatch.StartNew();
                var analysis = SparseCholesky.Analyze(matrix, ordering);
                double ms = stopwatch.Elapsed.TotalMilliseconds;
                output.WriteLine(
                    $"{order,10} {freeCount,10:N0} {ordering,9} {analysis.FactorNonZeroCount,12:N0} "
                    + $"{analysis.UpdateCount,10:E2} {analysis.LongestColumn,12:N0} "
                    + $"{analysis.ParallelCeiling,16:F1}x {ms,11:F1}");
            }
        }
    }

    [Fact]
    public void ThroughputAcrossTheWholePipeline()
    {
        if (!Enabled)
            return;

        output.WriteLine(
            $"{"order",10} {"elements",10} {"free DOF",10} {"assemble",9} {"factor",8} "
            + $"{"solve",8} {"react",8} {"stress",8} {"total ms",9}");
        foreach (var (order, n) in new[]
        {
            (ElementOrder.Linear, 4), (ElementOrder.Linear, 8), (ElementOrder.Linear, 12),
            (ElementOrder.Quadratic, 2), (ElementOrder.Quadratic, 4),
        })
        {
            var model = Cantilever(order, n);
            var stopwatch = Stopwatch.StartNew();
            var results = StructuralSolver.Solve(model);
            double solveMs = stopwatch.Elapsed.TotalMilliseconds;

            stopwatch.Restart();
            _ = results.NodalVonMises;
            double stressMs = stopwatch.Elapsed.TotalMilliseconds;

            var r = results.Report;
            output.WriteLine(
                $"{order,10} {r.ElementCount,10:N0} {r.FreeDofs,10:N0} {r.AssembleMs,9:F0} "
                + $"{r.FactorMs,8:F0} {r.SolveMs,8:F1} {r.ReactionMs,8:F0} {stressMs,8:F0} "
                + $"{solveMs + stressMs,9:F0}");
        }
    }

    [Fact]
    public void WhetherAKInverseNormResidualEscapesTheBucklingFloor()
    {
        if (!Enabled)
            return;

        // The open question filed against the buckling residual floor: the ACCEPTED measure
        // |K phi - lambda Kg phi| / (|K phi| + |lambda||Kg phi|) is a total cancellation with
        // an eps·kappa(K) floor, and the entry asks whether measuring in the K^-1 norm —
        // |K^-1(K phi - lambda Kg phi)|, one extra back-substitution through the
        // factorization that already exists — escapes it. Note what that quantity IS:
        // K^-1·r = -lambda·(T phi - theta phi) for the shift-invert operator T = K^-1(-Kg),
        // i.e. the OPERATOR residual, the very quantity the textbook beta·|y| bound
        // describes and LanczosEigen deliberately declined to accept on.
        //
        // Method: the same pinned-pinned column as the floor test, three refinements. For
        // each, assemble K and -Kg once, factor K once, and run the eigensolver at
        // descending tolerances; each accepted pair's vector is measured BOTH ways, plus the
        // eigenvalue drift against the tightest acceptance, which is what says whether a
        // tighter default would buy accuracy or only a smaller number.
        foreach (var (nx, across) in new[] { (24, 2), (36, 3), (48, 4) })
        {
            var (model, _) = BucklingFixtures.Column(
                ColumnEnds.PinnedPinned, 120.0, 6.0, nx, across, ElementOrder.Quadratic);
            var statics = StructuralSolver.Solve(model);

            var stiffnessRule = TetQuadrature.For(ElementOrder.Quadratic);
            var geometricRule = TetQuadrature.ForGeometric(ElementOrder.Quadratic);
            var reduced = FeaAssembly.ReducedIndices(model, out int freeCount);
            var k = FeaAssembly.Reduce(
                FeaAssembly.Stiffness(model, stiffnessRule), reduced, freeCount);
            var b = FeaAssembly.Reduce(
                FeaAssembly.Geometric(statics, geometricRule, -1.0), reduced, freeCount);
            var factor = SparseCholesky.Factorize(k, SparseOrdering.Amd);

            output.WriteLine($"-- {nx}x{across}x{across} quadratic, {freeCount:N0} free DOF --");
            output.WriteLine(
                $"{"tolerance",10} {"steps",6} {"standard",12} {"K^-1 norm",12} "
                + $"{"lambda",16} {"drift",10}");

            // Tightest first, so the drift column is measured against the most converged
            // acceptance the mesh allows rather than against the loosest.
            double? reference = null;
            foreach (double tolerance in new[] { 1e-10, 1e-9, 3e-9, 1e-7, 1e-5, 1e-3, 1e-2 })
            {
                var eigen = LanczosEigen.Solve(
                    k, b, k, factor, 0.0, [], 1, tolerance, 60, maxRestarts: 1);
                if (eigen.Pairs.Count == 0)
                {
                    output.WriteLine(
                        $"{tolerance,10:E0} {eigen.Iterations,6} refused; candidate stalled at "
                        + $"{eigen.Candidate?.Residual:E2} (lambda {eigen.Candidate?.Eigenvalue:G12})");
                    continue;
                }

                var pair = eigen.Pairs[0];
                var phi = pair.Vector;
                double lambda = pair.Eigenvalue;

                var kPhi = k.Multiply(phi);
                var bPhi = b.Multiply(phi);
                var r = new double[freeCount];
                for (int i = 0; i < freeCount; i++)
                    r[i] = kPhi[i] - lambda * bPhi[i];
                double standard = Norm(r) / (Norm(kPhi) + Math.Abs(lambda) * Norm(bPhi));

                // One extra back-substitution: s = K^-1 r, reported relative to |phi|.
                var s = factor.Solve(r);
                double kInverse = Norm(s) / Norm(phi);

                reference ??= lambda;
                double drift = Math.Abs(lambda - reference.Value) / Math.Abs(reference.Value);
                output.WriteLine(
                    $"{tolerance,10:E0} {eigen.Iterations,6} {standard,12:E2} {kInverse,12:E2} "
                    + $"{lambda,16:G12} {drift,10:E2}");
            }
        }
    }

    private static double Norm(double[] v)
    {
        double sum = 0;
        foreach (double x in v)
            sum += x * x;
        return Math.Sqrt(sum);
    }
}
