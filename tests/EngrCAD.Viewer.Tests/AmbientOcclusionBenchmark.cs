using System.Diagnostics;
using EngrCAD.Implicit;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// What an ambient-occlusion bake costs, and — the part that matters more — a bit-exact
/// lock on what it produces, so a traversal change can be shown to be a pure speedup.
/// The benchmark half is inert unless <c>ENGRCAD_BENCH</c> is set:
/// <code>
/// $env:ENGRCAD_BENCH = "1"
/// dotnet test tests/EngrCAD.Viewer.Tests -c Release --filter FullyQualifiedName~AmbientOcclusionBenchmark -l "console;verbosity=detailed"
/// </code>
/// </summary>
[CollectionDefinition("ambient-occlusion-bench", DisableParallelization = true)]
public class AmbientOcclusionBenchmarkCollection;

/// <inheritdoc cref="AmbientOcclusionBenchmark"/>
[Collection("ambient-occlusion-bench")]
public class AmbientOcclusionBenchmark(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ENGRCAD_BENCH") is not (null or "");

    /// <summary>
    /// Fixtures spanning the range the bake actually meets: a small pocketed block, a
    /// drilled plate, a CSG blob at display resolution, and a gyroid lattice — the shape
    /// whose per-ray cost motivated <see cref="AmbientOcclusion.MaxTriangles"/>, kept just
    /// under that cap so it is baked rather than skipped.
    /// </summary>
    public static IEnumerable<(string Name, RenderMesh Mesh)> Fixtures()
    {
        yield return ("pocket", Flat(Shape.Box(4, 4, 2) - Shape.Box(2, 2, 1).Translate(0, 0, 1)));
        yield return ("drilled plate", Flat(
            Shape.Box(30, 20, 6)
            - Shape.Cylinder(2, 10).Translate(-8, 0, 0)
            - Shape.Cylinder(2, 10).Translate(8, 0, 0)
            - Shape.Box(6, 6, 3).Translate(0, 0, 2.5)));
        yield return ("csg blob", Flat(
            (Shape.Box(20, 20, 20) - Shape.Cylinder(6, 30)).SmoothUnion(
                Shape.Sphere(12).Translate(8, 3, 2), 2.5),
            new MeshQuality { SdfResolution = 96 }));
        yield return ("gyroid", Flat(
            Shape.Sphere(16).Lattice(Sdf.Gyroid(cellSize: 12, thickness: 1.2)),
            new MeshQuality { SdfResolution = 56 }));
    }

    private static RenderMesh Flat(Shape shape, MeshQuality? quality = null) =>
        RenderMesh.CreateFlat(new Part("p", shape).GetMesh(quality));

    /// <summary>FNV-1a over every occlusion float's exact bits.</summary>
    internal static long Fingerprint(float[] occlusion)
    {
        unchecked
        {
            long hash = (long)14695981039346656037UL;
            foreach (float value in occlusion)
            {
                hash ^= BitConverter.SingleToInt32Bits(value);
                hash *= 1099511628211L;
            }
            return hash;
        }
    }

    /// <summary>
    /// The bake's output, pinned bit for bit. This is what makes a traversal change
    /// honest: an ambient-occlusion "optimization" that quietly changes the shading is a
    /// different renderer, not a faster one — and the committed docs PNGs would move with
    /// it. Nearest-hit ordering, the per-triangle vertex cache and any future pruning all
    /// have to come through here unchanged.
    /// <para>Fingerprints were taken from the pre-optimization bake (linear scan order,
    /// vertices re-read from the float arrays per ray-triangle test). The two Surface Nets
    /// fixtures were re-taken once <see cref="PolygonFan"/> landed: their quads are
    /// genuinely non-planar, so the shorter-diagonal split gives a different set of
    /// triangles to cast against.</para>
    /// <para>The two B-Rep fixtures were re-taken when the boolean started CLIPPING its
    /// carrier curves to the pair's shared trim, and the reason is worth stating because it
    /// is exactly the case where "the golden moved" is the right answer. The clip stopped
    /// faces being split along geometry the pair does not share: the pocket went from 18
    /// faces to 11 and the drilled plate from 20 to 13, while the display mesh kept its
    /// polygon count (44 and 244) and its volume to nine decimals. The SOLID is the same
    /// solid — but the triangulation of a face-with-one-hole is not the triangulation of
    /// eight rectangles, so the flat render mesh's vertices sit in different places and
    /// their occlusion legitimately differs. Four of the 106 rendered docs PNGs moved with
    /// it, all four B-Rep-boolean scenes, none with a changed silhouette.</para>
    /// <para>The GYROID was re-taken once Surface Nets began giving an inside component one
    /// vertex per SHEET it bounds rather than one in total. Note what did NOT move: the flat
    /// vertex count is 228 348 either way, because the quads are the same quads — the fix
    /// splits a vertex two sheets were sharing, so 18 of the source mesh's 37 898 vertices
    /// become 36, every quad keeps its four corners, and only the positions of the quads
    /// around those pinch points move. It is the same surface with the pinch opened.</para>
    /// <para>Both SDF fixtures were re-taken AGAIN when Surface Nets began placing its
    /// vertices at the minimiser of the field's own tangent planes (dual contouring with
    /// Hermite data) instead of at the mean of the crossings. Note again what did NOT move:
    /// the flat vertex counts are 173 268 and 228 348 either way, because the quads are the
    /// same quads — the grouping that decides them runs before any position is computed.
    /// Every vertex moved a little and the occlusion each one bakes moved with it; the
    /// surface is the same surface, placed where the field says the surface is rather than
    /// where the average of its own crossings falls.</para>
    /// </summary>
    [Theory]
    [InlineData("pocket", 132, 6950379374013342215L)]
    [InlineData("drilled plate", 924, 1703752811058293293L)]
    [InlineData("csg blob", 173268, 8531903548274328920L)]
    [InlineData("gyroid", 228348, -785811624771655578L)]
    public void Bake_MatchesTheGoldenBitPattern(string name, int vertices, long fingerprint)
    {
        var mesh = Fixtures().First(f => f.Name == name).Mesh;
        Assert.Equal(vertices, mesh.VertexCount);
        Assert.Equal(fingerprint, Fingerprint(AmbientOcclusion.Bake(mesh)));
    }

    /// <summary>
    /// Bake cost per fixture. Reference machine (i9-9900K, win-x64, .NET 10.0.302,
    /// Release, otherwise idle), best of three after a wall-clock warm-up budget.
    /// </summary>
    [Fact]
    public void BakeCostByFixture()
    {
        if (!Enabled)
            return;

        var fixtures = Fixtures().ToList();
        var warm = Stopwatch.StartNew();
        do
        {
            AmbientOcclusion.Bake(fixtures[1].Mesh);
        }
        while (warm.ElapsedMilliseconds < 2000);

        output.WriteLine(" fixture       | triangles | vertices |      ms");
        foreach (var (name, mesh) in fixtures)
        {
            double best = double.PositiveInfinity;
            // Best of seven, not three: the bake saturates every core, so a background
            // build or a thermal excursion inflates a run by tens of percent and three
            // samples are not enough to see past it.
            for (int trial = 0; trial < 7; trial++)
            {
                var watch = Stopwatch.StartNew();
                AmbientOcclusion.Bake(mesh);
                watch.Stop();
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
            }
            output.WriteLine(
                $" {name,-13} | {mesh.TriangleCount,9} | {mesh.VertexCount,8} | {best,7:F1}");
        }
    }

    /// <summary>
    /// What halving the ray count buys and what it costs, as numbers rather than opinion:
    /// the time, and how far the occlusion actually moves per vertex. The bake is a
    /// cosine-weighted estimate over a deterministic direction set, so fewer rays is not
    /// noise — it is a coarser quadrature, and the question is whether the coarser answer
    /// is visibly different.
    /// </summary>
    [Fact]
    public void RayCountTradeoff()
    {
        if (!Enabled)
            return;

        var fixtures = Fixtures().ToList();
        var warm = Stopwatch.StartNew();
        do
        {
            AmbientOcclusion.Bake(fixtures[1].Mesh, rays: 8);
        }
        while (warm.ElapsedMilliseconds < 1500);

        output.WriteLine(" fixture       | rays |      ms | mean occ | max delta vs 16 | mean delta");
        foreach (var (name, mesh) in fixtures)
        {
            var reference = AmbientOcclusion.Bake(mesh, rays: 16);
            foreach (int rays in new[] { 8, 16, 32 })
            {
                double best = double.PositiveInfinity;
                float[] baked = [];
                for (int trial = 0; trial < 3; trial++)
                {
                    var watch = Stopwatch.StartNew();
                    baked = AmbientOcclusion.Bake(mesh, rays);
                    watch.Stop();
                    best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
                }
                double maxDelta = 0, sumDelta = 0, mean = 0;
                for (int v = 0; v < baked.Length; v++)
                {
                    double delta = Math.Abs(baked[v] - reference[v]);
                    maxDelta = Math.Max(maxDelta, delta);
                    sumDelta += delta;
                    mean += baked[v];
                }
                int count = Math.Max(1, baked.Length);
                output.WriteLine(
                    $" {name,-13} | {rays,4} | {best,7:F1} | {mean / count,8:F4} | " +
                    $"{maxDelta,15:F4} | {sumDelta / count,10:F5}");
            }
        }
    }
}
