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
/// Measured on the reference machine (win-x64, <c>Vector&lt;double&gt;.Count</c> = 4;
/// .NET 10.0.302, Release, otherwise idle, because concurrent builds have moved numbers
/// in this project by 3×). Ratios only ever mean anything <b>within one sitting</b> — this
/// laptop returns numbers 2× apart from the same binary across sittings — so each block
/// below was taken by alternating the two builds in one run, best of two passes.
/// </para>
/// <para><b>Structure-of-arrays plus the batch entry</b> (the earlier sitting that
/// introduced the line and full-circle kernels; the Polygonize column also carries the
/// Surface Nets sampling rework, and its 10.5× on "busy" is mostly the extruded node's
/// per-(x, y) memoization — a prism's field does not vary along z, and the polygonizer
/// samples z fastest):</para>
/// <code>
/// case   | scalar SignedDistance | Polygonize(prism, res 96)
/// plate  |  3.78 ->  10.92 Mpts/s |  15.8 ->  7.9 ms
/// busy   |  0.18 ->   0.81 Mpts/s | 107.2 -> 10.2 ms
/// </code>
/// <para><b>Partial-arc and cubic-bézier kernels</b> (this sitting). The batch column is
/// the one the lane-wise kernels serve; the scalar column sees only their loop-invariant
/// hoisting — the arc endpoints (four transcendentals per out-of-sweep query before) and
/// the bézier's 17 scan points:</para>
/// <code>
/// case   | scalar Mpts/s  | batch Mpts/s   | Polygonize(res 96)
/// plate  |  8.43 ->  8.42 | 12.64 -> 15.35 |  8.8 ->  8.7 ms
/// slot   | 13.45 -> 14.56 | 14.31 -> 31.90 |  7.4 ->  7.4 ms
/// busy   |  0.72 ->  0.75 |  0.82 ->  0.91 | 11.5 -> 12.4 ms  (noise)
/// petals |  0.42 ->  0.50 |  0.41 ->  0.56 | 10.6 ->  9.3 ms
/// </code>
/// So the arc kernel is worth <b>2.23×</b> on an arc-dominated profile and the bézier
/// kernel <b>1.37×</b> on a bézier-dominated one, and nothing here loses — a block whose
/// lanes are all bounding-box-rejected is still skipped whole, and a block with one live
/// lane costs what one scalar solve did. Polygonize barely moves except on "petals",
/// because everywhere else the extruded node's memoization and the Surface Nets machinery
/// dominate what is left.
/// </summary>
/// <remarks>
/// The four cases: "plate" is a rectangle with two bores and a rounded slot (14 segments);
/// "slot" is a stadium, so half its boundary is arc; "busy" is a 60-lobe outline with
/// twelve bézier scallops (108 segments, 48 of them cubics) standing in for engraved text;
/// "petals" is twelve large cubics and nothing else. "busy" and "petals" are both needed —
/// "busy"'s scallops are small and far apart, so most of its cubics never survive the
/// bounding-box reject and it measures the reject rather than the kernel.
/// </remarks>
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

    /// <summary>
    /// Twelve large cubics and nothing else, sized so every sample point is near one of
    /// them: the bounding-box reject has almost nothing to skip, so this measures the bézier
    /// kernel itself rather than the reject standing in front of it (which is what "busy"
    /// mostly measures — its twelve scallops are small and far apart).
    /// </summary>
    private static Sketch Petals()
    {
        const int lobes = 12;
        const double inner = 20, outer = 34;
        static Vector2d At(double radius, double angle) =>
            new(radius * Math.Cos(angle), radius * Math.Sin(angle));

        var builder = Sketch.Start(inner, 0);
        for (int i = 1; i <= lobes; i++)
        {
            double a0 = 2 * Math.PI * (i - 1) / lobes, a1 = 2 * Math.PI * i / lobes;
            double third = (a1 - a0) / 3;
            builder = builder.BezierTo(At(outer, a0 + third), At(outer, a1 - third), At(inner, a1));
        }
        return builder.Close();
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

        output.WriteLine($"Vector<double>.Count = {System.Numerics.Vector<double>.Count}, " +
            $"hardware accelerated = {System.Numerics.Vector.IsHardwareAccelerated}");

        foreach (var (name, sketch) in new (string, Sketch)[]
                 { ("plate", Plate()), ("slot", Sketch.Slot(20, 8)), ("busy", Busy()), ("petals", Petals()) })
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

            // The batch entry is what the lane-wise kernels actually serve; the scalar column
            // above only sees their loop-invariant hoisting.
            var xs = points.Select(p => p.X).ToArray();
            var ys = points.Select(p => p.Y).ToArray();
            var into = new double[points.Length];
            double batchMs = MeanMs(() =>
            {
                region.SignedDistance(xs, ys, into);
                sink += into[0];
            });

            var prism = Sdf.ExtrudedRegion(region, 6);
            var box = prism.Bounds.Expanded(1);
            double meshMs = MeanMs(() => SurfaceNets.Polygonize(prism, box, 96));

            output.WriteLine(
                $"{name,-6} scalar {points.Length / scalarMs / 1000.0,7:F2}   " +
                $"batch {points.Length / batchMs / 1000.0,7:F2} Mpts/s   " +
                $"Polygonize(res 96) {meshMs,7:F1} ms   (sink {sink:E2})");
        }
    }
}
