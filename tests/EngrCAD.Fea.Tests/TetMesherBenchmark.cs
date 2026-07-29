using System.Diagnostics;
using EngrCAD.Core;
using EngrCAD.Fea;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// What tetrahedral meshing costs, at the scales an FEA consumer actually asks for. Inert
/// unless <c>ENGRCAD_BENCH</c> is set:
/// <code>
/// $env:ENGRCAD_BENCH = "1"
/// dotnet test tests/EngrCAD.Fea.Tests -c Release --filter FullyQualifiedName~TetMesherBenchmark -l "console;verbosity=detailed"
/// </code>
///
/// <para>Measured on the reference machine (win-x64, i9-9900K, .NET 10.0.302, <b>Release</b>,
/// otherwise idle). Debug is 3-5x slower and its numbers mean nothing; and per the
/// JIT-tiering lesson in CLAUDE.md these use a wall-clock warm-up budget rather than a
/// warm-up count, because the same code has measured 2x apart across runs otherwise.</para>
///
/// <para>The split between phases is the interesting part rather than the totals: boundary
/// recovery is free on a well-formed surface (it is a presence check that passes), and the
/// cost is the Delaunay build plus, when refinement is on, the classification passes.</para>
/// </summary>
public class TetMesherBenchmark(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ENGRCAD_BENCH") is not (null or "");

    [Fact]
    public void MeshingCost_AtTenThousandAndOneHundredThousandElements()
    {
        if (!Enabled)
            return;

        output.WriteLine("case                     | tets    | verts   | ms     | tets/s   | recovery | escalations");
        output.WriteLine("-------------------------|---------|---------|--------|----------|----------|------------");

        // Conforming only (no refinement): the element count follows the surface's own
        // resolution, so a denser sphere is the knob.
        foreach (int segments in new[] { 24, 48, 96, 144 })
            Report($"sphere {segments}x{segments / 2}",
                () => MeshPrimitives.UvSphere(10.0, segments, segments / 2), null);

        // Refinement to a size target: this is where 10k and 100k element meshes come from,
        // and where the quality columns become worth reading.
        foreach (double size in new[] { 2.0, 1.2, 0.8 })
        {
            double captured = size;
            Report($"box 20^3, size {size}",
                () => MeshPrimitives.Box(new Aabb(new Vector3d(0, 0, 0), new Vector3d(20, 20, 20))),
                new TetMeshOptions { RefineQuality = true, RadiusEdgeRatio = 2.0, MaxElementSize = captured });
        }

        // The same spheres WITH refinement. The contrast with the conforming-only rows above
        // is the headline: a sphere's vertices are all exactly cospherical, so a
        // tetrahedralization with no interior vertices is slivers by construction.
        foreach (double size in new[] { 4.0, 2.5 })
        {
            double captured = size;
            Report($"sphere 48x24, size {size}",
                () => MeshPrimitives.UvSphere(10.0, 48, 24),
                new TetMeshOptions { RefineQuality = true, RadiusEdgeRatio = 2.0, MaxElementSize = captured });
        }
    }

    private void Report(string name, Func<HalfEdgeMesh> build, TetMeshOptions? options)
    {
        var surface = build();

        // Warm-up BUDGET, not a warm-up count: JIT tiering makes a single warm-up call
        // meaningless (the same code has measured 1.4x slower and 0.84x across sittings).
        var warmup = Stopwatch.StartNew();
        while (warmup.Elapsed.TotalMilliseconds < 400)
            TetMesher.Mesh(surface, options);

        Predicates3d.ResetEscalationCounters();
        double best = double.PositiveInfinity;
        TetMesh? kept = null;
        TetMeshDiagnostics report = default;
        for (int run = 0; run < 3; run++)
        {
            var watch = Stopwatch.StartNew();
            var mesh = TetMesher.Mesh(surface, options, out var diagnostics);
            watch.Stop();
            if (watch.Elapsed.TotalMilliseconds < best)
            {
                best = watch.Elapsed.TotalMilliseconds;
                kept = mesh;
                report = diagnostics;
            }
        }

        var quality = TetQuality.Analyze(kept!);
        output.WriteLine(
            $"{name,-24} | {kept!.TetCount,7} | {kept.VertexCount,7} | {best,6:F0} | " +
            $"{kept.TetCount / (best / 1000.0),8:F0} | {report.RecoveryRounds,8} | " +
            $"{report.InSphereEscalations,11}");
        output.WriteLine(
            $"    residual {report.VolumeResidual:E2} | dihedral min {quality.MinDihedralDegrees:E2} " +
            $"mean-min {quality.MeanMinDihedralDegrees:F1} deg | radius-edge max {quality.MaxRadiusEdgeRatio:F2} " +
            $"mean {quality.MeanRadiusEdgeRatio:F2} | aspect min {quality.MinAspectRatio:E2} " +
            $"mean {quality.MeanAspectRatio:F3} | min vol {quality.MinVolume:E2} | " +
            $"slivers<10deg {quality.SliverCount} ({100.0 * quality.SliverCount / quality.TetCount:F1}%)");
    }

    /// <summary>
    /// What <see cref="Predicates3d.InSphere"/>'s exact stage costs, and how often the mesher
    /// pays it. The exact stage escalates to <see cref="System.Numerics.BigInteger"/> and so
    /// ALLOCATES, unlike <c>Orient3d</c>'s stack-allocated expansion form — a deliberate trade
    /// recorded in the class doc, and the backlog asks whether it matters.
    ///
    /// <para>The measurement that answers it is the SHARE, not the per-call cost: a
    /// tetrahedralization's escalation count is reported by
    /// <see cref="TetMeshDiagnostics.InSphereEscalations"/>, so what a real workload spends is
    /// observable rather than estimated.</para>
    /// </summary>
    [Fact]
    public void InSphereExactStage_CostAndHowOftenItIsPaid()
    {
        if (!Enabled)
            return;

        // (a) Per-call cost and allocation, filtered vs escalated. A cubic lattice is
        //     cospherical everywhere, so it escalates constantly; a jittered cloud never does.
        output.WriteLine("input            | calls   | escalations | ns/call | bytes/call");
        output.WriteLine("-----------------|---------|-------------|---------|-----------");

        MeasureInSphere("cubic lattice", Lattice(8));
        MeasureInSphere("jittered cloud", Jittered(512));

        // (b) What a real mesh pays.
        output.WriteLine("");
        output.WriteLine("mesh                 | tets   | escalations | per tet | mesh MB | est. exact MB");
        output.WriteLine("---------------------|--------|-------------|---------|---------|--------------");

        foreach (var (name, surface, options) in new (string, HalfEdgeMesh, TetMeshOptions?)[]
        {
            ("box 20^3 conforming", MeshPrimitives.Box(new Aabb(Vector3d.Zero, new Vector3d(20, 20, 20))), null),
            ("box 20^3 h=2", MeshPrimitives.Box(new Aabb(Vector3d.Zero, new Vector3d(20, 20, 20))),
                new TetMeshOptions { RefineQuality = true, MaxElementSize = 2.0 }),
            ("sphere r10 48x24", MeshPrimitives.UvSphere(10, 48, 24),
                new TetMeshOptions { RefineQuality = true, MaxElementSize = 2.5 }),
        })
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var mesh = TetMesher.Mesh(surface, options, out var diagnostics);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            // 15 BigIntegers built per escalation plus the determinant products; measured
            // per-call below, applied here so the SHARE is the answer rather than a guess.
            double perEscalation = EscalationBytes;
            output.WriteLine(
                $"{name,-20} | {mesh.TetCount,6} | {diagnostics.InSphereEscalations,11} | " +
                $"{(double)diagnostics.InSphereEscalations / Math.Max(1, mesh.TetCount),7:F2} | " +
                $"{allocated / 1048576.0,7:F1} | " +
                $"{diagnostics.InSphereEscalations * perEscalation / 1048576.0,13:F1}");
        }
    }

    /// <summary>Bytes an escalated <c>InSphere</c> allocates, filled in by the micro-benchmark.</summary>
    private static double EscalationBytes = 0;

    private void MeasureInSphere(string name, Vector3d[] points)
    {
        // Warm-up BUDGET, not a count (the JIT-tiering lesson).
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalMilliseconds < 300)
            RunInSphere(points, out _, out _);

        double best = double.MaxValue;
        long escalations = 0, bytes = 0, calls = 0;
        for (int trial = 0; trial < 5; trial++)
        {
            Predicates3d.ResetEscalationCounters();
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            long n = RunInSphere(points, out _, out _);
            watch.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            if (watch.Elapsed.TotalMilliseconds < best)
            {
                best = watch.Elapsed.TotalMilliseconds;
                escalations = Predicates3d.InSphereEscalations;
                bytes = allocated;
                calls = n;
            }
        }

        double bytesPerCall = (double)bytes / calls;
        if (escalations > 0)
            EscalationBytes = (double)bytes / escalations;

        output.WriteLine(
            $"{name,-16} | {calls,7} | {escalations,11} | {best * 1e6 / calls,7:F0} | {bytesPerCall,10:F1}");
    }

    private static long RunInSphere(Vector3d[] p, out double last, out int sign)
    {
        last = 0;
        sign = 0;
        long calls = 0;
        for (int i = 0; i + 4 < p.Length; i++)
        {
            last = Predicates3d.InSphere(p[i], p[i + 1], p[i + 2], p[i + 3], p[i + 4]);
            sign ^= Math.Sign(last);
            calls++;
        }
        return calls;
    }

    private static Vector3d[] Lattice(int n)
    {
        var points = new List<Vector3d>(n * n * n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                for (int k = 0; k < n; k++)
                    points.Add(new Vector3d(i, j, k));
        return [.. points];
    }

    private static Vector3d[] Jittered(int count)
    {
        // Deterministic, and deliberately NOT on any common sphere.
        var points = new Vector3d[count];
        ulong state = 0x9E3779B97F4A7C15;
        double Next()
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            return (state >> 11) * (1.0 / 9007199254740992.0);
        }
        for (int i = 0; i < count; i++)
            points[i] = new Vector3d(Next() * 100, Next() * 100, Next() * 100);
        return points;
    }

    /// <summary>
    /// What the sliver-removal post-pass costs and buys. The interesting column is the sliver
    /// count, because that is the defect radius-edge refinement provably cannot bound.
    /// </summary>
    [Fact]
    public void SmoothingCost_AndWhatItBuys()
    {
        if (!Enabled)
            return;

        output.WriteLine("case            | tets   | ms    | min dihedral    | mean-min      | slivers      | drift");
        output.WriteLine("----------------|--------|-------|-----------------|---------------|--------------|-------");

        foreach (double target in new[] { 3.0, 2.0, 1.5 })
        {
            var surface = MeshPrimitives.Box(new Aabb(Vector3d.Zero, new Vector3d(20, 20, 20)));
            var mesh = TetMesher.Mesh(surface,
                new TetMeshOptions { RefineQuality = true, MaxElementSize = target });
            var before = TetQuality.Analyze(mesh);

            var watch = Stopwatch.StartNew();
            var smoothed = TetSmoothing.Smooth(mesh, null, out var report);
            watch.Stop();
            var after = TetQuality.Analyze(smoothed);

            output.WriteLine(
                $"box 20^3 h={target,-5} | {mesh.TetCount,6} | {watch.Elapsed.TotalMilliseconds,5:F0} | " +
                $"{before.MinDihedralDegrees,6:F2} -> {after.MinDihedralDegrees,5:F2} | " +
                $"{before.MeanMinDihedralDegrees,5:F1} -> {after.MeanMinDihedralDegrees,5:F1} | " +
                $"{before.SliverCount,5} -> {after.SliverCount,-5} | {report.VolumeChangeRelative:E1}");
        }
    }
}
