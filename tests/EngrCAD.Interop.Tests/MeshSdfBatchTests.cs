using System.Diagnostics;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// <see cref="MeshSdf"/> deliberately does <b>not</b> override the batch seam: its batches
/// are the base class's scalar loop over <c>Bvh.Nearest</c>. These tests pin that contract
/// — batch equals scalar bit for bit, everywhere, including the awkward cases — so that any
/// future batch kernel has to earn its place against the same bar.
/// <para>
/// <b>Two batch strategies were built and measured, and neither earned it</b> (see
/// <see cref="SeedingBenchmark"/> for the numbers and the reasoning). Recording that here
/// so the experiment is not repeated from scratch.
/// </para>
/// </summary>
public class MeshSdfBatchTests(ITestOutputHelper output)
{
    private static HalfEdgeMesh Model() => SurfaceNets.Polygonize(
        Sdf.Sphere(1) | Sdf.Box(1.4, 1.4, 1.4).Translate((0.7, 0.2, 0)),
        new Aabb((-1.3, -1.3, -1.3), (1.6, 1.3, 1.3)), 40);

    private static void AssertBatchMatchesScalar(MeshSdf field, IReadOnlyList<Vector3d> points)
    {
        var batched = new double[points.Count];
        field.Evaluate(points.ToArray(), batched);
        for (int i = 0; i < points.Count; i++)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(field.Evaluate(points[i])),
                BitConverter.DoubleToInt64Bits(batched[i]));
        }
    }

    /// <summary>A grid row marching through the model, inside and out, crossing the surface
    /// several times — the coherent shape every bulk sampler produces.</summary>
    [Fact]
    public void ACoherentGridRun_MatchesScalarBitForBit()
    {
        var field = new MeshSdf(Model());
        var region = field.Bounds.Expanded(0.3);
        foreach (double step in new[] { 0.004, 0.05, 0.4 })
        {
            var run = new List<Vector3d>();
            for (double t = region.Min.Z; t <= region.Max.Z; t += step)
                run.Add(new Vector3d(region.Center.X + 0.31, region.Center.Y - 0.22, t));
            AssertBatchMatchesScalar(field, run);
        }
    }

    /// <summary>The incoherent shape: a boolean's classification probes.</summary>
    [Fact]
    public void ScatteredPoints_MatchScalarBitForBit()
    {
        var field = new MeshSdf(Model());
        var region = field.Bounds.Expanded(0.5);
        var random = new Random(8675309);
        var points = new List<Vector3d>();
        for (int i = 0; i < 3000; i++)
            points.Add(new Vector3d(
                region.Min.X + region.Size.X * random.NextDouble(),
                region.Min.Y + region.Size.Y * random.NextDouble(),
                region.Min.Z + region.Size.Z * random.NextDouble()));
        AssertBatchMatchesScalar(field, points);
    }

    /// <summary>
    /// Samples sitting exactly ON the surface — vertices, edge midpoints, face interiors.
    /// This is the case that breaks a seeded branch and bound (the seed equals the true
    /// minimum, so nothing beats it and the search returns a miss), so it is the case any
    /// future batch kernel must be held to.
    /// </summary>
    [Fact]
    public void PointsOnTheSurface_MatchScalarBitForBit()
    {
        var mesh = Model();
        var field = new MeshSdf(mesh);
        var points = new List<Vector3d>();
        foreach (var face in mesh.Faces)
        {
            var vertices = face.Vertices().Select(v => v.Position).ToArray();
            points.Add(vertices[0]);
            points.Add(Vector3d.Lerp(vertices[0], vertices[1], 0.5));
            var centroid = Vector3d.Zero;
            foreach (var vertex in vertices)
                centroid += vertex;
            points.Add(centroid / vertices.Length);
        }
        AssertBatchMatchesScalar(field, points);
    }

    /// <summary>The winding-number sign source shares the distance path, so it must agree too.</summary>
    [Fact]
    public void WindingNumberMode_MatchesScalarBitForBit()
    {
        var field = new MeshSdf(Model(), MeshSignSource.WindingNumber);
        var region = field.Bounds.Expanded(0.3);
        var run = new List<Vector3d>();
        for (double t = region.Min.Z; t <= region.Max.Z; t += 0.01)
            run.Add(new Vector3d(region.Center.X - 0.18, region.Center.Y + 0.27, t));
        AssertBatchMatchesScalar(field, run);
    }

    /// <summary>Grid bakes go through the batch seam, so a sparse lazy grid over a mesh
    /// field must reproduce the dense one exactly — end to end, through both grid paths.</summary>
    [Fact]
    public void GridBakesOverAMeshField_Agree()
    {
        var field = new MeshSdf(Model());
        var region = field.Bounds.Expanded(0.2);
        var dense = field.Sampled(region, 0.08);
        var lazy = field.Sampled(region, 0.08, lazy: true);
        var random = new Random(4);
        for (int i = 0; i < 400; i++)
        {
            var p = new Vector3d(
                region.Min.X + region.Size.X * random.NextDouble(),
                region.Min.Y + region.Size.Y * random.NextDouble(),
                region.Min.Z + region.Size.Z * random.NextDouble());
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(dense.Evaluate(p)),
                BitConverter.DoubleToInt64Bits(lazy.Evaluate(p)));
        }
    }

    /// <summary>
    /// The measurement behind "MeshSdf keeps the scalar batch loop". Inert unless
    /// <c>ENGRCAD_BENCH</c> is set. Reference machine: win-arm64, .NET 10.0.302, Release,
    /// otherwise idle; a 47 724-triangle mesh.
    /// <para>
    /// <b>Where the time goes.</b> A narrow-band bake of this field spends 74–85% of its
    /// wall clock inside these queries (measured by baking the same grid from a cheap
    /// analytic field: 6 ms of 40 at a 0.05 cell, 22 ms of 85 at 0.03). So a mesh-specific
    /// band could at best be ~4–7×, and only by making the queries themselves cheaper.
    /// </para>
    /// <para>
    /// <b>Seeding the branch and bound does not do it.</b> Consecutive samples in a coherent
    /// run bound each other (distance is 1-Lipschitz), so starting the search at the
    /// previous answer plus the step instead of at infinity looks like a free win — and it
    /// is provably result-identical, given strict <c>&gt;</c> pruning and a strictly-greater
    /// seed. Implemented and verified bit-identical on every sample, it measured
    /// <b>1.12–1.20×</b> on the most coherent case available, and a small net <em>loss</em>
    /// on scattered probes (an extra square root per point). The reason is worth keeping:
    /// <b>a nearest-first branch and bound is already its own seed</b> — it descends the
    /// nearer child first, so it reaches a leaf and a tight bound in O(log n) node tests,
    /// and a seed can only save part of that first descent. A standalone prototype claimed
    /// 1.88×, but its baseline went through a <c>Func</c> delegate per triangle while the
    /// seeded path called the kernel directly; the gap was the delegate, not the seed.
    /// <b>Beware benchmarking an optimization against a baseline you wrote differently.</b>
    /// </para>
    /// <para>
    /// What is still untried: a <em>packet</em> query — one traversal per coherent block
    /// collecting the candidate triangles for all its points at once, then scanning that
    /// short list per point. That attacks the node-test cost across the whole block rather
    /// than the initial bound, which is the part seeding cannot reach. It needs care over
    /// tie-breaking (equidistant triangles must resolve to the same one as
    /// <c>Bvh.Nearest</c>) and a fallback when the candidate list blows up.
    /// </para>
    /// </summary>
    [Fact]
    public void SeedingBenchmark()
    {
        if (Environment.GetEnvironmentVariable("ENGRCAD_BENCH") is null or "")
            return;

        var mesh = SurfaceNets.Polygonize(
            Sdf.Sphere(1) | Sdf.Box(1.4, 1.4, 1.4).Translate((0.7, 0.2, 0)),
            new Aabb((-1.3, -1.3, -1.3), (1.6, 1.3, 1.3)), 96);
        var field = new MeshSdf(mesh);
        var region = field.Bounds.Expanded(0.3);
        output.WriteLine($"{mesh.Triangulated().FaceCount} triangles");

        var run = new List<Vector3d>();
        for (double t = region.Min.Z; t <= region.Max.Z; t += 0.004)
            run.Add(new Vector3d(region.Center.X + 0.31, region.Center.Y - 0.22, t));
        var points = run.ToArray();
        var values = new double[points.Length];

        double Bench(Action body)
        {
            var warm = Stopwatch.StartNew();
            do
            {
                body();
            }
            while (warm.ElapsedMilliseconds < 1500);
            var watch = Stopwatch.StartNew();
            int runs = 0;
            do
            {
                body();
                runs++;
            }
            while (watch.ElapsedMilliseconds < 3000);
            return watch.Elapsed.TotalMilliseconds / runs;
        }

        double scalar = Bench(() =>
        {
            for (int i = 0; i < points.Length; i++)
                values[i] = field.Evaluate(points[i]);
        });
        double batch = Bench(() => field.Evaluate(points, values));
        output.WriteLine($"{points.Length} coherent samples: per-point {scalar:F2} ms | batch {batch:F2} ms");

        // What a narrow band over this field costs, and how much of it is not query time.
        double band = Bench(() => field.NarrowBand(region, 0.03));
        double cheap = Bench(() => Sdf.Sphere(1.2).NarrowBand(region, 0.03));
        output.WriteLine(
            $"narrow band (cell 0.03): {band:F0} ms, of which {cheap:F0} ms " +
            $"({cheap / band * 100:F0}%) is not source evaluation");
    }
}
