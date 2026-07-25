using System.Diagnostics;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Implicit.Tests;

/// <summary>
/// What a sparse lazy grid costs against a dense bake when queries are <em>localized</em>
/// (a boolean's classification probes, a section overlay, a mesher's band) rather than
/// spread over the whole box. Inert unless <c>ENGRCAD_BENCH</c> is set:
/// <code>
/// $env:ENGRCAD_BENCH = "1"
/// dotnet test tests/EngrCAD.Implicit.Tests --filter FullyQualifiedName~SparseGridBenchmark -l "console;verbosity=detailed"
/// </code>
/// <para>
/// Measured on the reference machine (win-arm64, .NET 10.0.302, Release, otherwise idle —
/// while other agents were building, the same lookup benchmark read 2.4 and 6.9 Mpts/s,
/// so quote the conditions or do not quote the number). Source is a <c>MeshSdf</c> over
/// 47 724 triangles; probes are 40 000 points in a shell around the surface:
/// </para>
/// <code>
///  cell  |    samples | dense bake            | sparse + 40k shell probes
///  0.02  |   2.5 M    |  2192 ms,  19.8 MB    | 2526 ms,  6.9 MB
///  0.01  |  20.3 M    | 18816 ms, 156.5 MB    | 8976 ms, 28.9 MB
///  0.0012|  11.7 G    | cannot be allocated   | 11113 ms, 80.6 MB (2000 probes)
/// </code>
/// The last row is the point of the feature, not the speedup: 11.7 G samples is 87 GB of
/// doubles and overflows <c>int</c> addressing, so the dense bake throws — and a flat block
/// table for that grid would itself be 5.7 GB of never-touched pointers. Sparse, it runs.
/// Note the first row honestly: at a coarse cell the shell probes reach most of the grid,
/// so the sparse grid buys memory and nothing else.
/// </summary>
public class SparseGridBenchmark(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ENGRCAD_BENCH") is not (null or "");

    private static (double Ms, long Bytes, T Value) Measure<T>(Func<T> body)
    {
        body(); // warm the JIT; these are seconds-scale, so a budget loop would take minutes
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetTotalMemory(true);
        var watch = Stopwatch.StartNew();
        var kept = body();
        watch.Stop();
        return (watch.Elapsed.TotalMilliseconds, GC.GetTotalMemory(false) - before, kept);
    }

    /// <summary>Points in a thin shell around the region's centre — how a boolean or a
    /// section overlay probes a field, NOT uniformly over the box.</summary>
    private static Vector3d[] ShellProbes(in Aabb region, int count, int seed)
    {
        var random = new Random(seed);
        var probes = new Vector3d[count];
        var centre = region.Center;
        for (int i = 0; i < count; i++)
        {
            double azimuth = random.NextDouble() * 2 * Math.PI;
            double polar = Math.Acos(2 * random.NextDouble() - 1);
            double radius = 1.0 + (random.NextDouble() - 0.5) * 0.08;
            probes[i] = centre + new Vector3d(
                radius * Math.Sin(polar) * Math.Cos(azimuth),
                radius * Math.Sin(polar) * Math.Sin(azimuth),
                radius * Math.Cos(polar));
        }
        return probes;
    }

    [Fact]
    public void SparseVersusDense()
    {
        if (!Enabled)
            return;

        var mesh = SurfaceNets.Polygonize(
            Sdf.Sphere(1) | Sdf.Box(1.4, 1.4, 1.4).Translate((0.7, 0.2, 0)),
            new Aabb((-1.3, -1.3, -1.3), (1.6, 1.3, 1.3)), 96);
        var source = new MeshSdf(mesh);
        var region = source.Bounds.Expanded(0.3);
        output.WriteLine($"{mesh.Triangulated().FaceCount} triangles, region {region.Size}");

        foreach (double cell in new[] { 0.02, 0.01 })
        {
            var probes = ShellProbes(region, 40000, 7);
            var (denseMs, denseBytes, _) = Measure(() => source.Sampled(region, cell));
            var (sparseMs, sparseBytes, grid) = Measure(() =>
            {
                var lazy = source.Sampled(region, cell, lazy: true);
                foreach (var probe in probes)
                    lazy.Evaluate(probe);
                return lazy;
            });
            output.WriteLine(
                $"cell {cell}: dense {denseMs,8:F0} ms {denseBytes / 1048576.0,7:F1} MB | " +
                $"sparse {sparseMs,8:F0} ms {sparseBytes / 1048576.0,7:F1} MB " +
                $"({((LazyGridSdf)grid).MaterializedBlocks} blocks)");
        }

        // The domain the dense path cannot address at all.
        const double fine = 0.0012;
        var message = Assert.Throws<ArgumentException>(() => source.Sampled(region, fine)).Message;
        output.WriteLine($"cell {fine}: dense -> {message}");
        var small = ShellProbes(region, 2000, 7);
        var (fineMs, fineBytes, fineGrid) = Measure(() =>
        {
            var lazy = source.Sampled(region, fine, lazy: true);
            foreach (var probe in small)
                lazy.Evaluate(probe);
            return lazy;
        });
        output.WriteLine(
            $"cell {fine}: sparse {fineMs:F0} ms {fineBytes / 1048576.0:F1} MB " +
            $"({((LazyGridSdf)fineGrid).MaterializedBlocks} blocks of {((LazyGridSdf)fineGrid).MaterializedBytes / 1048576.0:F1} MB)");
    }

    /// <summary>
    /// Steady-state lookup through the block table — the number the flat/grouped threshold
    /// exists to protect. Measured 6.2–6.9 Mpts/s both before and after the sparse table
    /// landed, on a grid that stays on the flat path.
    /// </summary>
    [Fact]
    public void BlockTableLookupThroughput()
    {
        if (!Enabled)
            return;

        var grid = Sdf.Sphere(50).Sampled(new Aabb((-60, -60, -60), (60, 60, 60)), 0.5, lazy: true);
        var random = new Random(11);
        var points = new Vector3d[1 << 16];
        for (int i = 0; i < points.Length; i++)
            points[i] = new Vector3d(
                -55 + 110 * random.NextDouble(), -55 + 110 * random.NextDouble(), -55 + 110 * random.NextDouble());
        foreach (var point in points)
            grid.Evaluate(point); // materialize every block these touch

        double sink = 0;
        var warm = Stopwatch.StartNew();
        do
        {
            foreach (var point in points)
                sink += grid.Evaluate(point);
        }
        while (warm.ElapsedMilliseconds < 1500);

        var watch = Stopwatch.StartNew();
        int runs = 0;
        do
        {
            foreach (var point in points)
                sink += grid.Evaluate(point);
            runs++;
        }
        while (watch.ElapsedMilliseconds < 3000);

        output.WriteLine(
            $"lazy-grid Evaluate: {points.Length * runs / watch.Elapsed.TotalMilliseconds / 1000.0:F2} Mpts/s (sink {sink:E2})");
    }
}
