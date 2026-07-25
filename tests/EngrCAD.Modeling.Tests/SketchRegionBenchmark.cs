using System.Diagnostics;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Cost of the 2D sketch field, which is the inner loop of every implicit sketch
/// extrusion and revolution. Inert unless <c>ENGRCAD_BENCH</c> is set:
/// <code>
/// $env:ENGRCAD_BENCH = "1"
/// dotnet test tests/EngrCAD.Modeling.Tests --filter FullyQualifiedName~SketchRegionBenchmark -l "console;verbosity=detailed"
/// </code>
/// <para>
/// Measured on the reference machine (win-arm64 — <c>Vector&lt;double&gt;.Count</c> is
/// only 2 there, so SIMD alone can never pay more than 2×; .NET 10.0.302, Release,
/// otherwise idle, because concurrent builds have moved numbers in this project by 3×):
/// </para>
/// <code>
/// case  | scalar SignedDistance | Polygonize(prism, res 96)
/// plate |  3.78 ->  10.92 Mpts/s |  15.8 ->  7.9 ms
/// busy  |  0.18 ->   0.81 Mpts/s | 107.2 -> 10.2 ms
/// </code>
/// "plate" is a rectangle with two bores and a rounded slot (14 segments); "busy" is a
/// 60-lobe outline with twelve bézier scallops (108 segments, 48 of them cubics) standing
/// in for engraved text. The Polygonize column also carries the Surface Nets sampling
/// rework, and its 10.5× on "busy" is mostly the extruded node's per-(x, y) memoization:
/// a prism's field does not vary along z, and the polygonizer samples z fastest.
/// </summary>
public class SketchRegionBenchmark(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ENGRCAD_BENCH") is not (null or "");

    private static Sketch Plate() => Sketch.Rectangle(40, 24)
        .WithHole(Sketch.Circle(new Vector2d(-12, 0), 4))
        .WithHole(Sketch.Circle(new Vector2d(12, 0), 4))
        .WithHole(Sketch.RoundedRectangle(10, 6, 1.5));

    /// <summary>Many segments, half of them cubics — the shape of engraved lettering.</summary>
    private static Sketch Busy()
    {
        var builder = Sketch.Start(30, 0);
        const int lobes = 60;
        for (int i = 1; i <= lobes; i++)
        {
            double angle = 2 * Math.PI * i / lobes;
            double radius = i % 2 == 0 ? 30 : 26;
            builder = builder.LineTo(new Vector2d(radius * Math.Cos(angle), radius * Math.Sin(angle)));
        }
        var outline = builder.Close();
        for (int i = 0; i < 12; i++)
        {
            double angle = 2 * Math.PI * i / 12;
            var centre = new Vector2d(14 * Math.Cos(angle), 14 * Math.Sin(angle));
            outline = outline.WithHole(Sketch.Start(centre.X - 2, centre.Y)
                .BezierTo(centre + new Vector2d(-2, 2.6), centre + new Vector2d(2, 2.6), centre + new Vector2d(2, 0))
                .BezierTo(centre + new Vector2d(2, -2.6), centre + new Vector2d(-2, -2.6), centre + new Vector2d(-2, 0))
                .Close());
        }
        return outline;
    }

    /// <summary>A wall-clock warm-up BUDGET, not a warm-up count (JIT tiering makes a
    /// fixed count meaningless), then the mean over a fixed measurement budget.</summary>
    private static double MeanMs(Action body)
    {
        var warm = Stopwatch.StartNew();
        do
        {
            body();
        }
        while (warm.ElapsedMilliseconds < 1200);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var watch = Stopwatch.StartNew();
        int runs = 0;
        do
        {
            body();
            runs++;
        }
        while (watch.Elapsed.TotalMilliseconds < 2000);
        return watch.Elapsed.TotalMilliseconds / runs;
    }

    [Fact]
    public void SketchFieldCost()
    {
        if (!Enabled)
            return;

        foreach (var (name, sketch) in new (string, Sketch)[] { ("plate", Plate()), ("busy", Busy()) })
        {
            var region = new SketchRegion(sketch);
            var bounds = sketch.Bounds;
            var random = new Random(12345);
            var points = new Vector2d[1 << 16];
            for (int i = 0; i < points.Length; i++)
                points[i] = new Vector2d(
                    bounds.Min.X - 2 + (bounds.Size.X + 4) * random.NextDouble(),
                    bounds.Min.Y - 2 + (bounds.Size.Y + 4) * random.NextDouble());

            double sink = 0;
            double scalarMs = MeanMs(() =>
            {
                double sum = 0;
                foreach (var point in points)
                    sum += region.SignedDistance(point);
                sink += sum;
            });

            var prism = Sdf.ExtrudedRegion(region, 6);
            var box = prism.Bounds.Expanded(1);
            double meshMs = MeanMs(() => SurfaceNets.Polygonize(prism, box, 96));

            output.WriteLine(
                $"{name,-6} scalar {points.Length / scalarMs / 1000.0,7:F2} Mpts/s   " +
                $"Polygonize(res 96) {meshMs,7:F1} ms   (sink {sink:E2})");
        }
    }
}
