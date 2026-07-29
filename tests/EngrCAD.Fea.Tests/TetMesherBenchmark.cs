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
}
