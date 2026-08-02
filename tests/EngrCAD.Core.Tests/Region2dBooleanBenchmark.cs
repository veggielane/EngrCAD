using System.Diagnostics;
using EngrCAD.Core.Geometry2;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Core.Tests;

/// <summary>
/// The bulk-union workload the classification cost was filed against — 120 overlapping
/// 32-gons — as a COMMITTED fixture, so the numbers in todo.md stop being unreproducible.
/// Skipped unless <c>ENGRCAD_BENCH</c> is set:
/// <code>
/// $env:ENGRCAD_BENCH = "1"
/// dotnet test tests/EngrCAD.Core.Tests -c Release --filter Region2dBooleanBenchmark
/// </code>
/// Warm-up is a wall-clock budget and the reported figure is a MINIMUM over trials — the
/// estimator for a deterministic workload on a machine that background load can only slow
/// down (the recorded measurement lesson).
///
/// <para><b>This fixture is also the bar a `ContainedIn` point-location index must beat,
/// and one has already been built, measured against it, and DECLINED.</b> A per-operand
/// y-bucket edge index asking Region2d's own per-edge rules (result-identical by
/// construction, goldens byte-for-byte) measured 40.1 → 41.8 ms at 120 polygons and
/// 135.7 → 137.8 ms at 480 (win-x64, i9-9900K, minima over interleaved runs): nothing,
/// twice, at both scales. The filed O(cells × operand vertices) term is real but this
/// workload cannot feel it — an overlap-heavy union's balanced fold keeps the CELL count
/// tiny exactly where the operand vertex counts are large, so the product never grows.
/// The workload that would feel it is one whose result KEEPS many cells against
/// many-vertex operands (two interleaved combs intersected, a grid of crossing strips);
/// see the todo.md entry.</para>
/// </summary>
public class Region2dBooleanBenchmark(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ENGRCAD_BENCH") is not (null or "");

    private static List<Region2d> OverlappingPolygons(int count, int gonSides)
    {
        // A 12-wide grid of 32-gons whose radius exceeds the spacing, so every polygon
        // overlaps its neighbours and the union is one big blob with a busy boundary.
        var regions = new List<Region2d>(count);
        for (int i = 0; i < count; i++)
        {
            double cx = i % 12 * 1.0;
            double cy = i / 12 * 1.0;
            double radius = 1.2 + 0.05 * (i % 3);
            var loop = new List<Vector2d>(gonSides);
            for (int k = 0; k < gonSides; k++)
            {
                double t = 2 * Math.PI * k / gonSides + i * 0.01;
                loop.Add(new Vector2d(cx + radius * Math.Cos(t), cy + radius * Math.Sin(t)));
            }
            regions.Add(new Region2d(loop, []));
        }
        return regions;
    }

    [Fact]
    public void UnionOfOverlappingPolygons()
    {
        if (!Enabled)
            return;

        foreach (int count in new[] { 120, 480 })
        {
            var regions = OverlappingPolygons(count, gonSides: 32);

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed.TotalMilliseconds < 500)
                Region2dBoolean.UnionAll(regions);

            double best = double.MaxValue;
            int resultCount = 0;
            for (int trial = 0; trial < 5; trial++)
            {
                var watch = Stopwatch.StartNew();
                var result = Region2dBoolean.UnionAll(regions);
                watch.Stop();
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
                resultCount = result.Count;
            }

            output.WriteLine($"UnionAll of {count} overlapping 32-gons: {best:F1} ms (best of 5), {resultCount} region(s)");
        }
    }
}
