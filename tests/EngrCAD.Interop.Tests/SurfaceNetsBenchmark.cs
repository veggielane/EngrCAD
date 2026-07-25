using System.Diagnostics;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Polygonization cost and memory, as a committed benchmark rather than a remembered
/// number. It is inert unless <c>ENGRCAD_BENCH</c> is set, so a normal
/// <c>dotnet test</c> run pays nothing:
/// <code>
/// $env:ENGRCAD_BENCH = "1"
/// dotnet test tests/EngrCAD.Interop.Tests --filter FullyQualifiedName~SurfaceNetsBenchmark -l "console;verbosity=detailed"
/// </code>
/// <para>
/// Measured on the reference machine (win-arm64, .NET 10.0.302, Release, otherwise idle —
/// concurrent builds have moved these numbers by 3×, so quote the conditions):
/// </para>
/// <code>
///  res |  before ms |  after ms |  before MB |  after MB
///   48 |        4.8 |       2.9 |        8.0 |       5.1
///   96 |       39.9 |      15.6 |       40.9 |      19.9
///  144 |       90.0 |      50.3 |      125.8 |      57.9
///  192 |      247.0 |     109.1 |      260.5 |     105.6
///  256 |      735.5 |     258.8 |      562.2 |     145.1
///  384 |     1922.7 |     747.5 |     1841.7 |     289.1
/// </code>
/// "before" is the dense sampler that materialized a <c>Vector3d[]</c> of every grid
/// corner; "after" is the deinterleaved sliding-slab sampler. The memory ratio grows with
/// resolution because the dense grid is O(n³) while the slab window is O(n²).
/// </summary>
public class SurfaceNetsBenchmark(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ENGRCAD_BENCH") is not (null or "");

    [Fact]
    public void PolygonizeCostByResolution()
    {
        if (!Enabled)
            return;

        var field = (Sdf.Box(2, 2, 2) - Sdf.Cylinder(0.6, 3))
            .SmoothUnion(Sdf.Sphere(1.2).Translate((0.8, 0.3, 0.2)), 0.25);
        var region = new Aabb((-2.2, -2.2, -2.2), (2.4, 2.2, 2.2));

        // A wall-clock warm-up BUDGET, not a warm-up count: JIT tiering makes a fixed
        // iteration count meaningless (the same kernel has measured 147, 314 and 548
        // Mpts/s across runs of the same binary).
        var warm = Stopwatch.StartNew();
        do
        {
            SurfaceNets.Polygonize(field, region, 48);
        }
        while (warm.ElapsedMilliseconds < 1200);

        output.WriteLine(" res |      ms |  alloc MB | vertices");
        foreach (int resolution in new[] { 48, 96, 144, 192, 256 })
        {
            double best = double.PositiveInfinity;
            long allocated = 0;
            int vertices = 0;
            for (int trial = 0; trial < 3; trial++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long before = GC.GetTotalAllocatedBytes(precise: true);
                var watch = Stopwatch.StartNew();
                var mesh = SurfaceNets.Polygonize(field, region, resolution);
                watch.Stop();
                if (watch.Elapsed.TotalMilliseconds < best)
                {
                    best = watch.Elapsed.TotalMilliseconds;
                    allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
                }
                vertices = mesh.VertexCount;
            }
            output.WriteLine($"{resolution,4} | {best,7:F1} | {allocated / 1048576.0,9:F1} | {vertices}");
        }
    }
}
