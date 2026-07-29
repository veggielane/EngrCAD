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

    [Fact]
    public void ThroughputAcrossTheWholePipeline()
    {
        if (!Enabled)
            return;

        output.WriteLine(
            $"{"order",10} {"elements",10} {"free DOF",10} {"assemble",9} {"factor",8} "
            + $"{"solve",8} {"stress",8} {"total ms",9}");
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
                + $"{r.FactorMs,8:F0} {r.SolveMs,8:F1} {stressMs,8:F0} {solveMs + stressMs,9:F0}");
        }
    }
}
