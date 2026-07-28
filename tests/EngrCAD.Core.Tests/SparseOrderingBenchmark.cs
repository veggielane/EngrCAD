using System.Diagnostics;
using EngrCAD.Core.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Core.Tests;

/// <summary>
/// What a fill-reducing ordering is worth, as a committed benchmark rather than a
/// remembered number. Inert unless <c>ENGRCAD_BENCH</c> is set:
/// <code>
/// $env:ENGRCAD_BENCH = "1"
/// dotnet test tests/EngrCAD.Core.Tests -c Release --filter FullyQualifiedName~SparseOrderingBenchmark -l "console;verbosity=detailed"
/// </code>
/// <para>
/// Two grids, because the answer differs sharply between them: the 5-point 2D Laplacian
/// is the mesh-smoother/deformation shape this library was built for, and the 7-point 3D
/// Laplacian is the FEA shape it is headed for. Natural-order fill on a g×g grid grows
/// like g³ (bandwidth g) but on a g×g×g grid like g⁵ (bandwidth g²), so the same code
/// meets a qualitatively different problem in 3D — which is the whole reason this item
/// was filed against FEA rather than against deformation.
/// </para>
/// <para>
/// Reference machine (i9-9900K, win-x64, .NET 10.0.302, <b>Release</b>, otherwise idle),
/// wall-clock warm-up budget then best of three. "factor" is order + symbolic + numeric;
/// "3 RHS" is factor plus three substitutions, the x/y/z shape; "CG" is one
/// Jacobi-preconditioned solve to 1e-10 relative.
/// </para>
/// </summary>
public class SparseOrderingBenchmark(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ENGRCAD_BENCH") is not (null or "");

    [Fact]
    public void OrderingCostAndFillByProblemSize()
    {
        if (!Enabled)
            return;

        // Warm-up budget, not a warm-up count: JIT tiering makes a fixed iteration count
        // meaningless (this repo has measured the same kernel 3.7x apart across runs).
        var warm = AmdOrderingTests.GridLaplacian2d(40);
        var warmB = Rhs(warm.Rows, 1);
        var budget = Stopwatch.StartNew();
        do
        {
            SparseCholesky.Factorize(warm).Solve(warmB);
            SparseCholesky.Factorize(warm, SparseOrdering.Amd).Solve(warmB);
            SparseSymmetricCG.Solve(warm, warmB, new double[warm.Rows], new CgOptions { RelativeTolerance = 1e-10 });
        }
        while (budget.ElapsedMilliseconds < 1500);

        output.WriteLine(
            " grid           |       n |  nnz(A) | nat fill | nat fac ms | nat sol ms | amd fill | amd fac ms | amd sol ms |   CG ms | CG it | RHS to beat CG");
        foreach (var (label, matrix) in Cases())
        {
            int n = matrix.Rows;
            var b = Rhs(n, 17);

            var natural = Measure(matrix, SparseOrdering.Natural, b);
            var amd = Measure(matrix, SparseOrdering.Amd, b);

            double cgMs = double.PositiveInfinity;
            int cgIterations = 0;
            for (int trial = 0; trial < 3; trial++)
            {
                var x = new double[n];
                var watch = Stopwatch.StartNew();
                var report = SparseSymmetricCG.Solve(
                    matrix, b, x, new CgOptions { RelativeTolerance = 1e-10, MaxIterations = 20_000 });
                watch.Stop();
                cgMs = Math.Min(cgMs, watch.Elapsed.TotalMilliseconds);
                cgIterations = report.Iterations;
            }

            output.WriteLine(
                $" {label,-14} | {n,7} | {matrix.NonZeroCount,7} | {natural.Fill,8} | {natural.FactorMs,10:F1} | " +
                $"{natural.SolveMs,10:F2} | {amd.Fill,8} | {amd.FactorMs,10:F1} | {amd.SolveMs,10:F2} | " +
                $"{cgMs,7:F1} | {cgIterations,5} | {BreakEven(amd, cgMs),14}");
        }
    }

    /// <summary>
    /// How many right-hand sides a factorization has to serve before it beats running CG
    /// once per side: the smallest k with <c>factor + k·solve ≤ k·CG</c>. "never" when a
    /// single substitution already costs more than a whole CG solve, which is not a
    /// rounding artefact but the real verdict at that size.
    /// </summary>
    private static string BreakEven((int Fill, double FactorMs, double SolveMs) run, double cgMs)
    {
        double gain = cgMs - run.SolveMs;
        if (gain <= 0)
            return "never";
        return ((int)Math.Ceiling(run.FactorMs / gain)).ToString();
    }

    private static IEnumerable<(string Label, PackedSparseMatrix Matrix)> Cases()
    {
        // 2D: the sizes the Core README already quotes for natural ordering.
        foreach (int g in new[] { 50, 80, 120, 250 })
            yield return ($"2D {g}x{g}", AmdOrderingTests.GridLaplacian2d(g));
        // 3D at matched unknown counts: 2 744 / 6 859 / 13 824 / 64 000.
        foreach (int g in new[] { 14, 19, 24, 40 })
            yield return ($"3D {g}^3", AmdOrderingTests.GridLaplacian3d(g));
        // ... and the same patterns WITHOUT the identity shift (pure Dirichlet
        // Laplacians). The shifted operator is strongly diagonally dominant, so CG
        // converges in an n-INDEPENDENT ~35 iterations and the CG column above flatters
        // it badly. Dirichlet conditioning grows like g², which is the regime an FEA
        // stiffness matrix actually lives in — so these two rows are the honest half of
        // the direct-vs-iterative comparison.
        yield return ("2D 250 dirich", AmdOrderingTests.GridLaplacian2dDirichlet(250));
        yield return ("3D 24 dirich", AmdOrderingTests.GridLaplacian3dDirichlet(24));
    }

    private static (int Fill, double FactorMs, double SolveMs) Measure(
        PackedSparseMatrix a, SparseOrdering ordering, double[] b)
    {
        double factorMs = double.PositiveInfinity;
        double solveMs = double.PositiveInfinity;
        int fill = 0;
        for (int trial = 0; trial < 3; trial++)
        {
            var watch = Stopwatch.StartNew();
            var chol = SparseCholesky.Factorize(a, ordering);
            watch.Stop();
            factorMs = Math.Min(factorMs, watch.Elapsed.TotalMilliseconds);
            fill = chol.FactorNonZeroCount;

            var x = new double[a.Rows];
            var solves = Stopwatch.StartNew();
            for (int rhs = 0; rhs < 3; rhs++)
                chol.Solve(b, x);
            solves.Stop();
            solveMs = Math.Min(solveMs, solves.Elapsed.TotalMilliseconds / 3);
        }
        return (fill, factorMs, solveMs);
    }

    private static double[] Rhs(int n, int seed)
    {
        var rng = new Random(seed);
        var b = new double[n];
        for (int i = 0; i < n; i++)
            b[i] = rng.NextDouble() * 2 - 1;
        return b;
    }
}
