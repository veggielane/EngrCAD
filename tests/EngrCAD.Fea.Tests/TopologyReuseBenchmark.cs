using System.Diagnostics;
using EngrCAD.Core;
using EngrCAD.Core.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// What reusing ONE symbolic factorization across a topology loop buys. The loop's stiffness
/// has an identical sparsity pattern every iteration (mesh connectivity and eliminated DOFs are
/// fixed; only the per-element scales change), so <see cref="SparseCholesky.AnalyzePattern"/>
/// runs the ordering, elimination tree and column-count pass ONCE and every iteration then runs
/// only the numeric pass — the part that dominates. This measures the per-factorization saving
/// on real FEA stiffness matrices and extrapolates it to a 60-iteration run.
///
/// <para>Inert unless <c>ENGRCAD_BENCH</c> is set:
/// <code>
/// $env:ENGRCAD_BENCH = "1"
/// dotnet test tests/EngrCAD.Fea.Tests -c Release --filter FullyQualifiedName~TopologyReuseBenchmark -l "console;verbosity=detailed"
/// </code>
/// Reference machine: win-x64, i9-9900K, .NET 10.0.302, Release, otherwise idle. The reported
/// number is a MINIMUM over interleaved batches with both arms warmed first — a mean would
/// report whatever the neighbours were doing.</para>
/// </summary>
public class TopologyReuseBenchmark(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ENGRCAD_BENCH") is not (null or "");

    private static readonly Material Steel = new("benchmark steel", 210_000, 0.3);

    /// <summary>A fully-restrained cantilever's reduced (free-free) stiffness — the exact matrix
    /// the topology loop factorizes, at half density so it is well within double precision.</summary>
    private static (PackedSparseMatrix Matrix, int Elements, int FreeDofs) ReducedStiffness(
        int nx, int ny, int nz)
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(nx, ny, nz), nx, ny, nz);
        var mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, Steel);
        model.Fix(Facets.Tag(StructuredTetMesh.XMin));
        var rule = TetQuadrature.For(mesh.Order);
        var reduced = FeaAssembly.ReducedIndices(model, out int freeCount);
        var scale = new double[mesh.ElementCount];
        Array.Fill(scale, 0.5);
        var full = FeaAssembly.Stiffness(model, rule, scale);
        return (FeaAssembly.Reduce(full, reduced, freeCount), mesh.ElementCount, freeCount);
    }

    [Fact]
    public void SymbolicReuseCutsPerIterationFactorTime()
    {
        if (!Enabled)
            return;

        output.WriteLine(
            $"{"elements",10} {"free DOF",9} {"analyze ms",11} {"fresh ms",10} {"reuse ms",10} "
            + $"{"per-fact",9} {"60-loop before",15} {"after",10} {"loop x",7}");

        // (nx, ny, nz), reps per timing batch, batches. Bigger meshes are slower so fewer reps.
        foreach (var (nx, ny, nz, reps, batches) in new[]
        {
            (8, 4, 6, 12, 6),     // ~1 152 elements
            (20, 9, 10, 3, 4),    // ~10 800 elements
        })
        {
            var (matrix, elements, free) = ReducedStiffness(nx, ny, nz);
            const SparseOrdering ordering = SparseOrdering.Amd;

            // Warm both arms so the first batch does not measure JIT tiering.
            var warm = Stopwatch.StartNew();
            while (warm.ElapsedMilliseconds < 800)
            {
                SparseCholesky.Factorize(matrix, ordering);
                SparseCholesky.AnalyzePattern(matrix, ordering).Factorize(matrix);
            }

            double freshMs = double.MaxValue, reuseMs = double.MaxValue, analyzeMs = double.MaxValue;
            var sw = new Stopwatch();
            for (int b = 0; b < batches; b++)
            {
                sw.Restart();
                for (int r = 0; r < reps; r++)
                    SparseCholesky.Factorize(matrix, ordering);
                freshMs = Math.Min(freshMs, sw.Elapsed.TotalMilliseconds / reps);

                var symbolic = SparseCholesky.AnalyzePattern(matrix, ordering);
                sw.Restart();
                for (int r = 0; r < reps; r++)
                    symbolic.Factorize(matrix);
                reuseMs = Math.Min(reuseMs, sw.Elapsed.TotalMilliseconds / reps);

                sw.Restart();
                for (int r = 0; r < reps; r++)
                    SparseCholesky.AnalyzePattern(matrix, ordering);
                analyzeMs = Math.Min(analyzeMs, sw.Elapsed.TotalMilliseconds / reps);
            }

            double before = 60 * freshMs;                 // 60 fresh factorizations
            double after = analyzeMs + 60 * reuseMs;       // one analysis, 60 numeric passes
            output.WriteLine(
                $"{elements,10:N0} {free,9:N0} {analyzeMs,11:F2} {freshMs,10:F2} {reuseMs,10:F2} "
                + $"{freshMs / reuseMs,8:F2}x {before,14:F0} {after,10:F0} {before / after,6:F2}x");
        }
    }
}
