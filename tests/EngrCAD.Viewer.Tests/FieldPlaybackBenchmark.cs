using System.Diagnostics;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The transient-playback (colour animation) decision's committed instrument, so the
/// numbers in todo.md's "Transient thermal playback" entry stop being unreproducible:
/// the per-frame CPU cost of the two candidate designs —
/// <b>(a)</b> colours-only (rebuild the <c>aFieldColor</c> buffer for the new step's
/// field; the GL side is one <c>glBufferData</c> of the printed size), against
/// <b>(c)</b> the existing publish path (<c>PartUploads.Build</c> per step).
/// Skipped unless <c>ENGRCAD_BENCH</c> is set:
/// <code>
/// $env:ENGRCAD_BENCH = "1"
/// dotnet test tests/EngrCAD.Viewer.Tests -c Release --filter FieldPlaybackBenchmark
/// </code>
/// Warm-up is a wall-clock budget and the reported figure is a MINIMUM over trials — the
/// estimator for a deterministic workload on a machine that background load can only
/// slow down (the recorded measurement lesson).
/// <para>Recorded (win-x64, i9-9900K, Release): typical 12k render verts —
/// (a) 0.042 ms + 140 KB/frame, (c) 2.2 ms; heavy 195k render verts — (a) 0.68 ms +
/// 2.3 MB/frame, (c) 27.4 ms. (a) is 40–50× cheaper and inside a 60 fps budget on the
/// heavy mesh; (c) is the scrubbing-grade path, exactly as the entry guessed.</para>
/// </summary>
public class FieldPlaybackBenchmark(ITestOutputHelper output)
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("ENGRCAD_BENCH") is not (null or "");

    private static double Best(Action act, int reps = 9)
    {
        var warm = Stopwatch.StartNew();
        while (warm.ElapsedMilliseconds < 1500)
            act();
        double best = double.MaxValue;
        for (int i = 0; i < reps; i++)
        {
            var t = Stopwatch.StartNew();
            act();
            best = Math.Min(best, t.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    [Fact]
    public void PerFrameCost_ColoursOnlyVersusFullPublish()
    {
        if (!Enabled)
            return;

        foreach (var (label, mesh) in new (string, HalfEdgeMesh)[]
        {
            ("typical (uv-sphere 64x32)", MeshPrimitives.UvSphere(20, 64, 32)),
            ("heavy (uv-sphere 256x128)", MeshPrimitives.UvSphere(20, 256, 128)),
        })
        {
            var part = new Part(label, mesh);
            int n = mesh.VertexCount;
            var values = new double[n];
            for (int v = 0; v < n; v++)
                values[v] = v % 100;
            var field = MeshField.Scalar("Temperature", "C", values);
            part.AddResult(field);
            part.FieldDisplay = new FieldDisplay
            {
                Field = "Temperature",
                Range = new FieldRange(0, 100),
            };

            var render = RenderMesh.CreateFlat(part.GetMesh());
            var range = new FieldRange(0, 100);

            double colours = Best(() =>
                FieldRendering.Colors(field, range, FieldColorMap.Viridis, render));
            double full = Best(() => PartUploads.Build(part, PartUploadRequest.All));

            output.WriteLine(
                $"{label}: {n:N0} source verts, {render.VertexCount:N0} render verts");
            output.WriteLine(
                $"  (a) colours-only rebuild : {colours:F3} ms/frame "
                + $"(upload {render.VertexCount * 3 * 4 / 1024.0:F0} KB/frame)");
            output.WriteLine(
                $"  (c) full publish rebuild : {full:F3} ms/frame (PartUploads.Build All)");
        }
    }
}
