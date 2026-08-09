using System.Diagnostics;
using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Implicit.Tests;

/// <summary>
/// What grouping the batch by sub-cell buys, and what it costs. Reported rather than asserted:
/// a throughput number is a property of the machine, and the CORRECTNESS claim (bit-identical to
/// the scalar path) is pinned by <c>BatchEvaluationTests</c>' catalogue, which is where it
/// belongs.
/// <para>
/// The A/B is interleaved within ONE process and taken as a MINIMUM over passes — the recorded
/// estimator for a deterministic workload that scheduling noise can only slow down. Both arms
/// run against the same production node, so the comparison is the seam and not two spellings of
/// the arithmetic.
/// </para>
/// </summary>
public class StrutLatticeBatchBenchmark(ITestOutputHelper output)
{
    [Fact]
    public void GroupedBatchThroughput()
    {
        var kinds = Enum.GetValues<StrutLatticeKind>();
        var points = GridPoints(48, 5.0);
        var distances = new double[points.Length];

        // Warm EVERY kind before measuring ANY, on a wall clock rather than a count. Warming
        // per kind inside the loop is not enough and the first row is what shows it: the first
        // kind measured eats the whole tiering promotion of both paths and read 0.95x where a
        // properly warmed run reads 2.0x — the recorded single-warm-up lesson, in the position
        // where it is easiest to mistake for a property of the geometry.
        foreach (var kind in kinds)
        {
            var field = Sdf.StrutLattice(kind, 5, 1.0);
            Warm(() => Scalar(field, points, distances));
            Warm(() => field.Evaluate(points, distances));
        }

        output.WriteLine($"{points.Length} points, z-fastest (the layout every bulk consumer generates)");
        output.WriteLine("kind                 scalar Mpts/s   grouped batch Mpts/s   speedup");
        foreach (var kind in kinds)
        {
            var field = Sdf.StrutLattice(kind, 5, 1.0);
            double scalar = double.MaxValue, batch = double.MaxValue;
            for (int pass = 0; pass < 4; pass++)
            {
                scalar = Math.Min(scalar, Time(() => Scalar(field, points, distances)));
                batch = Math.Min(batch, Time(() => field.Evaluate(points, distances)));
            }

            double n = points.Length / 1e6;
            output.WriteLine(
                $"{kind,-20} {n / scalar,13:0.###}   {n / batch,20:0.###}   {scalar / batch,7:0.##}x");
        }
    }

    private static void Scalar(Sdf field, Vector3d[] points, double[] into)
    {
        for (int i = 0; i < points.Length; i++)
            into[i] = field.Evaluate(points[i]);
    }

    private static Vector3d[] GridPoints(int n, double cell)
    {
        var points = new Vector3d[n * n * n];
        double step = 2.3 * cell / n;
        int at = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                for (int k = 0; k < n; k++)
                    points[at++] = new Vector3d(step * i, step * j, step * k);
        return points;
    }

    private static void Warm(Action action)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 0.4)
            action();
    }

    private static double Time(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        return sw.Elapsed.TotalSeconds;
    }
}
