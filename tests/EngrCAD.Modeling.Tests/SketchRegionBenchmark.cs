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
/// <para><b>The elliptical-arc kernel</b> (<see cref="EllipseKernelCost"/>, a one-process
/// A/B over the <c>ellipseKernel</c> seam — both arms in one binary, alternated, best of
/// two passes; two sittings shown because the pair reproduced):</para>
/// <code>
/// case    | scalar Mpts/s  | batch Mpts/s   | Polygonize(res 96)
/// ellipse | 0.47 -> 2.04   | 0.48 -> 2.62   | 7.7 -> 6.1 ms     (sitting 1)
/// ellipse | 0.50 -> 2.11   | 0.49 -> 2.62   | 7.9 -> 6.7 ms     (sitting 2)
/// earcs   | 0.29 -> 1.42   | 0.29 -> 1.88   | 4.9 -> 3.1 ms     (sitting 1)
/// earcs   | 0.28 -> 1.54   | 0.28 -> 1.81   | 5.1 -> 3.1 ms     (sitting 2)
/// </code>
/// <para>
/// <b>Read the two columns as two separate measurements, because they are.</b> The scalar
/// column contains no SIMD at all, so its <b>4.2–5.6×</b> is purely the baked scan and the
/// hoisted cosine/sine pair — the shared <c>Curve2d.NearestPoint</c> evaluates the curve at
/// 65 fixed parameters per query and then calls three derivative methods per Newton step
/// that each recompute the same pair at the same angle. Batch over scalar within the "on"
/// arm is only <b>1.18–1.24×</b>, and that is the whole SIMD contribution: the scan
/// vectorizes but the Newton refinement cannot, so once the scan is baked the refinement is
/// most of what is left. The dominant cost moved, which is the standing rule — re-measure
/// after every win — and it also says where the next gain would have to come from, namely a
/// bit-exact vector cosine, which does not exist (see <c>EllipseRefine</c>).
/// </para>
/// <para>
/// Honest context: an elliptical arc is still by a distance the most expensive segment kind
/// (2.1 Mpts/s against "plate"'s 15.4 in the same sitting), so the reject in front of it
/// keeps earning its place.
/// </para>
/// </summary>
/// <remarks>
/// The four cases: "plate" is a rectangle with two bores and a rounded slot (14 segments);
/// "slot" is a stadium, so half its boundary is arc; "busy" is a 60-lobe outline with
/// twelve bézier scallops (108 segments, 48 of them cubics) standing in for engraved text;
/// "petals" is twelve large cubics and nothing else. "busy" and "petals" are both needed —
/// "busy"'s scallops are small and far apart, so most of its cubics never survive the
/// bounding-box reject and it measures the reject rather than the kernel. The two ellipse
/// cases follow "petals"' rule: "ellipse" is a rotated full ellipse with a counter-rotated
/// elliptical hole and "earcs" four partial elliptical arcs, both sized so the reject has
/// little to skip.
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

    /// <summary>
    /// A rotated ellipse with a counter-rotated elliptical hole, sized so most sample points
    /// are near one of the two — the ellipse counterpart of "petals", measuring the kernel
    /// rather than the bounding-box reject standing in front of it.
    /// </summary>
    private static Sketch Ellipses() => Sketch.Ellipse(new Vector2d(0, 0), 30, 18, 27)
        .WithHole(Sketch.Ellipse(new Vector2d(4, 2), 11, 6, -40));

    /// <summary>Partial elliptical arcs rather than full ellipses — an SVG-style outline of
    /// four, so every segment carries a real sweep and shares its endpoints.</summary>
    private static Sketch EllipseArcs() => Sketch.Start(-24, 0)
        .EllipticalArcTo(new Vector2d(0, 15), 24, 15, 0, largeArc: false, clockwise: false)
        .EllipticalArcTo(new Vector2d(24, 0), 27, 19, 20, largeArc: false, clockwise: false)
        .EllipticalArcTo(new Vector2d(0, -12), 24, 12, 0, largeArc: false, clockwise: false)
        .EllipticalArcTo(new Vector2d(-24, 0), 34, 22, -15, largeArc: false, clockwise: false)
        .Close();

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

    /// <summary>
    /// The ellipse kernel's A/B, and it is a genuine <b>one-process interleave</b> rather
    /// than two builds compared across sittings: <c>SketchRegion</c>'s internal
    /// <c>ellipseKernel</c> seam puts both paths in one binary, so the pair below is
    /// measured back to back with the same JIT state, the same caches and the same thermal
    /// conditions. That removes the standing hazard the repo keeps re-learning — this
    /// machine returns numbers 2× apart from the same binary across sittings, so a ratio
    /// taken across rebuilds is noise with units.
    /// <para>
    /// "off" routes elliptical arcs through <c>EllipseSeg.Distance</c>, i.e.
    /// <c>EngrCAD.BRep</c>'s shared <c>Curve2d.NearestPoint</c>, which re-evaluates the
    /// curve at all 65 scan parameters per query — 65 <c>Math.Cos</c> plus 65
    /// <c>Math.Sin</c> — and then calls three separate derivative methods per Newton step,
    /// each recomputing the same pair at the same angle. "on" bakes the scan and hoists the
    /// pair, then vectorizes the scan.
    /// </para>
    /// </summary>
    [Fact]
    public void EllipseKernelCost()
    {
        if (!Enabled)
            return;

        output.WriteLine($"Vector<double>.Count = {System.Numerics.Vector<double>.Count}, " +
            $"hardware accelerated = {System.Numerics.Vector.IsHardwareAccelerated}");

        foreach (var (name, sketch) in new (string, Sketch)[]
                 { ("ellipse", Ellipses()), ("earcs", EllipseArcs()) })
        {
            var bounds = sketch.Bounds;
            var random = new Random(12345);
            var points = new Vector2d[1 << 16];
            for (int i = 0; i < points.Length; i++)
                points[i] = new Vector2d(
                    bounds.Min.X - 2 + (bounds.Size.X + 4) * random.NextDouble(),
                    bounds.Min.Y - 2 + (bounds.Size.Y + 4) * random.NextDouble());
            var xs = points.Select(p => p.X).ToArray();
            var ys = points.Select(p => p.Y).ToArray();
            var into = new double[points.Length];

            double sink = 0;
            var scalar = new double[2];
            var batch = new double[2];
            var mesh = new double[2];
            // Alternate the two arms rather than running each to completion: any drift over
            // the sitting then lands on both equally instead of on whichever ran second.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int arm = 0; arm < 2; arm++)
                {
                    var region = new SketchRegion(sketch, forRevolution: false, ellipseKernel: arm == 1);
                    double s = MeanMs(() =>
                    {
                        double sum = 0;
                        foreach (var point in points)
                            sum += region.SignedDistance(point);
                        sink += sum;
                    });
                    double b = MeanMs(() =>
                    {
                        region.SignedDistance(xs, ys, into);
                        sink += into[0];
                    });
                    var prism = Sdf.ExtrudedRegion(region, 6);
                    var box = prism.Bounds.Expanded(1);
                    double m = MeanMs(() => SurfaceNets.Polygonize(prism, box, 96));
                    if (pass == 0 || s < scalar[arm])
                        scalar[arm] = s;
                    if (pass == 0 || b < batch[arm])
                        batch[arm] = b;
                    if (pass == 0 || m < mesh[arm])
                        mesh[arm] = m;
                }
            }

            output.WriteLine(
                $"{name,-8} scalar {points.Length / scalar[0] / 1000.0,7:F2} -> " +
                $"{points.Length / scalar[1] / 1000.0,7:F2} Mpts/s ({scalar[0] / scalar[1],5:F2}x)   " +
                $"batch {points.Length / batch[0] / 1000.0,7:F2} -> " +
                $"{points.Length / batch[1] / 1000.0,7:F2} Mpts/s ({batch[0] / batch[1],5:F2}x)   " +
                $"Polygonize {mesh[0],6:F1} -> {mesh[1],6:F1} ms ({mesh[0] / mesh[1],5:F2}x)   " +
                $"(sink {sink:E2})");
        }
    }
}
