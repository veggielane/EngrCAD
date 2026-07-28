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

    /// <summary>
    /// What the surface cull buys, per field and per resolution. Both columns produce the
    /// bit-identical mesh (locked by
    /// <see cref="SurfaceNetsSamplingTests.TheCulledWalk_IsBitIdenticalToTheFullWalk"/>), so
    /// this is purely a cost measurement.
    /// <para>
    /// Reference machine (win-arm64, .NET 10.0.302, Release, otherwise idle), best of three
    /// after a wall-clock warm-up budget:
    /// </para>
    /// <code>
    ///  field       | res |  full ms | culled ms | speedup
    ///  csg         |  48 |      3.6 |       3.1 |   1.16x
    ///  csg         |  96 |     21.4 |      13.4 |   1.60x
    ///  csg         | 192 |    138.8 |      63.7 |   2.18x
    ///  csg         | 256 |    310.7 |     126.3 |   2.46x
    ///  mesh sdf    |  48 |     44.7 |      36.8 |   1.21x
    ///  mesh sdf    |  96 |    352.4 |     117.9 |   2.99x
    /// </code>
    /// <para>
    /// <b>The speedup is smaller than the sample saving, and that is the finding.</b> The
    /// cull evaluates 28.4% of the grid at resolution 96, 15.6% at 192 and 12.0% at 256 —
    /// an 8× saving — yet buys only 2.5×, because polygonization stops being sample-bound
    /// once the samples are gone. Measured at resolution 256 with a field whose evaluation
    /// is a single square root: the whole call still takes 132.8 ms against 129.3 ms for the
    /// real CSG field, i.e. evaluation is now free and the cost is assembly —
    /// <see cref="HalfEdgeMesh.Build"/> alone is 39% (56.9 of 145.8 ms at 131 294 vertices,
    /// 48% at resolution 192), the rest being the per-cell component maps, the quad lists and
    /// the sample window. Anything further has to attack the mesh assembly, not the grid.
    /// </para></summary>
    [Fact]
    public void CullSpeedupByFieldAndResolution()
    {
        if (!Enabled)
            return;

        var csg = (Sdf.Box(2, 2, 2) - Sdf.Cylinder(0.6, 3))
            .SmoothUnion(Sdf.Sphere(1.2).Translate((0.8, 0.3, 0.2)), 0.25);
        var csgRegion = new Aabb((-2.2, -2.2, -2.2), (2.4, 2.2, 2.2));

        // An expensive field: every sample is a BVH nearest-triangle query.
        var mesh = SurfaceNets.Polygonize(csg, csgRegion, 64);
        var meshField = new MeshSdf(mesh);
        var meshRegion = meshField.Bounds.Expanded(0.3);

        var warm = Stopwatch.StartNew();
        do
        {
            SurfaceNets.Polygonize(csg, csgRegion, 40);
        }
        while (warm.ElapsedMilliseconds < 1200);

        output.WriteLine(" field       | res |  full ms | culled ms | speedup");
        foreach (var (label, field, region, resolutions) in new (string, Sdf, Aabb, int[])[]
        {
            ("csg", csg, csgRegion, [48, 96, 192, 256]),
            ("mesh sdf", meshField, meshRegion, [48, 96]),
        })
        {
            foreach (int resolution in resolutions)
            {
                double full = Best(() =>
                    SurfaceNets.Polygonize(field, region, resolution, null, int.MaxValue, cull: false));
                double culled = Best(() => SurfaceNets.Polygonize(field, region, resolution));
                output.WriteLine(
                    $" {label,-11} | {resolution,3} | {full,8:F1} | {culled,9:F1} | {full / culled,6:F2}x");
            }
        }
    }

    private static double Best(Action body)
    {
        double best = double.PositiveInfinity;
        for (int trial = 0; trial < 3; trial++)
        {
            var watch = Stopwatch.StartNew();
            body();
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }
        return best;
    }
}
