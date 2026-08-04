using System.Diagnostics;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;
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

    /// <summary>
    /// How much of a polygonization is mesh ASSEMBLY rather than field sampling — the
    /// question the surface cull left behind, since it made evaluation effectively free.
    /// "assembly" times exactly the call <see cref="SurfaceNets.Polygonize"/> makes at the
    /// end: <see cref="HalfEdgeMesh.Build"/> over the flat quad buffer.
    /// <para>
    /// Reference machine (i9-9900K, win-x64, .NET 10.0.302, Release, otherwise idle),
    /// best of three after a wall-clock warm-up budget, and the two builds
    /// <b>interleaved within one sitting</b> — this repo has measured the same Release
    /// binary 2× apart across sittings, so a ratio taken from two separate runs is noise
    /// with units. "before" is the <c>Dictionary&lt;(int, int), int&gt;</c> twin
    /// resolution fed one <c>int[4]</c> per quad; "after" is the counting sort over the
    /// edges' lower endpoint fed one flat index buffer. Output is bit-identical across the
    /// change (the golden fingerprints in <see cref="SurfaceNetsSamplingTests"/> pin it).
    /// </para>
    /// <code>
    ///  res | vertices | before asm | after asm | before share | after share | before total | after total
    ///   96 |   17 930 |     6.5 ms |    2.0 ms |        42.1% |       18.2% |      12.9 ms |    10.7 ms
    ///  192 |   72 232 |    27.0 ms |    8.0 ms |        38.4% |       15.8% |      67.1 ms |    50.6 ms
    ///  256 |  129 268 |    47.5 ms |   13.4 ms |        40.8% |       14.8% |     116.2 ms |    90.2 ms
    ///  384 |  289 726 |   132.3 ms |   35.5 ms |        41.0% |       16.7% |     322.5 ms |   212.6 ms
    /// </code>
    /// <para>
    /// Assembly is <b>3.3–3.7×</b> faster and the whole polygonization 1.2–1.5×, with
    /// allocation at resolution 256 falling 145 → 103 MB (the per-quad arrays and the
    /// half-million-entry dictionary are both gone). <b>The share is the number to watch,
    /// not the speedup</b>: assembly went from 38–51% of the call to 15–18%, so the next
    /// win is no longer here. What remains at resolution 384 is roughly 35 ms of building
    /// against 175 ms of grid walk — the per-cell component maps, the crossing
    /// interpolation and the quad passes — which is where the same question should be
    /// asked next.
    /// </para>
    /// </summary>
    [Fact]
    public void AssemblyShareByResolution()
    {
        if (!Enabled)
            return;

        var field = (Sdf.Box(2, 2, 2) - Sdf.Cylinder(0.6, 3))
            .SmoothUnion(Sdf.Sphere(1.2).Translate((0.8, 0.3, 0.2)), 0.25);
        var region = new Aabb((-2.2, -2.2, -2.2), (2.4, 2.2, 2.2));

        var warm = Stopwatch.StartNew();
        do
        {
            SurfaceNets.Polygonize(field, region, 48);
        }
        while (warm.ElapsedMilliseconds < 1200);

        output.WriteLine(" res | total ms | assembly ms | share | vertices |   quads");
        foreach (int resolution in new[] { 96, 192, 256, 384 })
        {
            var mesh = SurfaceNets.Polygonize(field, region, resolution);
            var (positions, faces) = mesh.ToIndexed();
            var corners = new int[faces.Count * 4];
            for (int f = 0; f < faces.Count; f++)
                faces[f].CopyTo(corners, f * 4);

            double total = Best(() => SurfaceNets.Polygonize(field, region, resolution));
            double assembly = Best(() => HalfEdgeMesh.Build(positions, corners, 4));

            output.WriteLine(
                $"{resolution,4} | {total,8:F1} | {assembly,11:F1} | {assembly / total,5:P1} | " +
                $"{positions.Length,8} | {faces.Count,7}");
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

    /// <summary>
    /// What sharp features and the adaptive pass cost, against the plain averaging walk.
    /// Both extra passes are batched — one gradient batch and one value batch per slab —
    /// so the cost is seven more field evaluations per CROSSING, not per sample.
    /// <para>
    /// Reference machine (i9-9900K, win-x64, .NET 10.0.302, Release, otherwise idle), best
    /// of four alternating in ONE process (the interleave rule: a ratio taken across
    /// sittings is noise with units):
    /// </para>
    /// <code>
    ///  res |  plain ms | sharp ms | ratio | adaptive ms | faces plain -> adaptive
    ///   48 |       2.6 |      5.3 | 2.09x |        12.7 |    4 542 ->     1 429
    ///   96 |      11.3 |     21.4 | 1.89x |        49.4 |   17 930 ->     2 912
    ///  192 |      51.4 |     84.7 | 1.65x |       190.1 |   72 232 ->     6 118
    ///  256 |      94.6 |    153.6 | 1.62x |       311.9 |  129 268 ->     8 798
    /// </code>
    /// <para>
    /// <b>The ratio FALLS with resolution, which is the shape worth keeping</b>: the extra
    /// work is per crossing and crossings are an O(n²) surface quantity, while the walk the
    /// cull leaves is a shell that still grows faster than that. So the feature is at its
    /// most expensive on small grids, where polygonization is cheap anyway.
    /// </para>
    /// <para>
    /// The adaptive column is a DIFFERENT trade and its own row says so: it roughly doubles
    /// the polygonization to divide the face count by 3.3 at resolution 48 and by <b>14.7 at
    /// 256</b> — the saving grows with the grid because the surface it is describing does
    /// not. It buys nothing in evaluation (see <see cref="SurfaceNetsSimplify"/> on why it
    /// is bottom-up) and everything downstream of the face count: rendering, export,
    /// booleans, occlusion baking, tet meshing.
    /// </para>
    /// </summary>
    [Fact]
    public void SharpFeatureCost()
    {
        if (!Enabled)
            return;

        var field = (Sdf.Box(2, 2, 2) - Sdf.Cylinder(0.6, 3))
            .SmoothUnion(Sdf.Sphere(1.2).Translate((0.8, 0.3, 0.2)), 0.25);
        var region = new Aabb((-2.2, -2.2, -2.2), (2.4, 2.2, 2.2));
        var plain = new SurfaceNetsOptions { SharpFeatures = false };

        var warm = Stopwatch.StartNew();
        do
        {
            SurfaceNets.Polygonize(field, region, 48, null, plain);
            SurfaceNets.Polygonize(field, region, 48);
        }
        while (warm.ElapsedMilliseconds < 1500);

        output.WriteLine(" res |  plain ms | sharp ms | ratio | adaptive ms | faces plain -> adaptive");
        foreach (int resolution in new[] { 48, 96, 192, 256 })
        {
            double cell = 4.6 / resolution;
            var adaptiveOptions = new SurfaceNetsOptions { SimplifyTolerance = 0.05 * cell };
            double p = double.MaxValue, s = double.MaxValue, a = double.MaxValue;
            int plainFaces = 0, adaptiveFaces = 0;
            for (int trial = 0; trial < 4; trial++)
            {
                var watch = Stopwatch.StartNew();
                plainFaces = SurfaceNets.Polygonize(field, region, resolution, null, plain).FaceCount;
                p = Math.Min(p, watch.Elapsed.TotalMilliseconds);
                watch.Restart();
                SurfaceNets.Polygonize(field, region, resolution);
                s = Math.Min(s, watch.Elapsed.TotalMilliseconds);
                watch.Restart();
                adaptiveFaces = SurfaceNets
                    .Polygonize(field, region, resolution, null, adaptiveOptions).FaceCount;
                a = Math.Min(a, watch.Elapsed.TotalMilliseconds);
            }
            output.WriteLine(
                $"{resolution,4} | {p,9:F1} | {s,8:F1} | {s / p,4:F2}x | {a,11:F1} | " +
                $"{plainFaces,8} -> {adaptiveFaces,8}");
        }
    }
}
