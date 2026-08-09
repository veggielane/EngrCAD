using System.Diagnostics;
using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Implicit.Tests;

/// <summary>
/// <b>What a vector-kernel compiler could win, measured — which is the number the decision to
/// file it rests on.</b>
/// <para>
/// A compiler emitting <c>Vector&lt;double&gt;</c> expressions instead of scalar ones would
/// remove exactly two things from the batch path: the virtual call per NODE per chunk, and the
/// pooled scratch buffer each operator writes and its parent reads. It cannot remove the
/// AoS→SoA transpose (the public signature hands over interleaved points) and it cannot remove
/// the arithmetic. So its ceiling is whatever those two cost, and this benchmark reads it off a
/// chain of known depth: the MARGINAL cost of one more node in a deep tree against the cost of
/// that same node standing alone.
/// </para>
/// <para>
/// Reported rather than asserted — a throughput is a property of the machine — and paired with
/// the recorded finding that a SCALAR compiled walk loses to the batch path by 1.2–3.4x in
/// every case measured.
/// </para>
/// </summary>
public class VectorCompilerHeadroomBenchmark(ITestOutputHelper output)
{
    [Fact]
    public void PerNodeBatchCost_AgainstTheSameNodeAlone()
    {
        var points = Points(200_000);
        var distances = new double[points.Length];

        // A union chain of spheres: every node is the same kernel, so the depth is the only
        // variable and the marginal cost is a difference of two measurements of one thing.
        int[] depths = [1, 4, 12, 24, 48];
        var fields = depths.Select(Chain).ToArray();
        foreach (var f in fields)
            Warm(() => f.Evaluate(points, distances));

        output.WriteLine($"{points.Length} points; union chain of spheres, batch path");
        output.WriteLine("depth   ns/point   ns/point/node   marginal ns/node");
        double previousTime = 0;
        int previousDepth = 0;
        double marginal = 0;
        for (int i = 0; i < depths.Length; i++)
        {
            double seconds = double.MaxValue;
            for (int pass = 0; pass < 5; pass++)
                seconds = Math.Min(seconds, Time(() => fields[i].Evaluate(points, distances)));
            double ns = seconds * 1e9 / points.Length;
            if (i > 0)
                marginal = (ns - previousTime) / (depths[i] - previousDepth);
            output.WriteLine(
                $"{depths[i],5}   {ns,8:0.###}   {ns / depths[i],13:0.###}   " +
                $"{(i > 0 ? marginal.ToString("0.###") : "-"),16}");
            previousTime = ns;
            previousDepth = depths[i];
        }

        // The same kernel standing alone: one node, so it carries the whole transpose.
        var lone = Sdf.Sphere(5);
        Warm(() => lone.Evaluate(points, distances));
        double loneSeconds = double.MaxValue;
        for (int pass = 0; pass < 5; pass++)
            loneSeconds = Math.Min(loneSeconds, Time(() => lone.Evaluate(points, distances)));
        double loneNs = loneSeconds * 1e9 / points.Length;

        output.WriteLine($"one sphere alone: {loneNs:0.###} ns/point");
        output.WriteLine(
            $"marginal cost of a node inside the chain: {marginal:0.###} ns — the plumbing a " +
            "vector compiler would remove is the part of this that is not arithmetic.");
    }

    private static Sdf Chain(int depth)
    {
        Sdf field = Sdf.Sphere(5);
        for (int i = 1; i < depth; i++)
            field = field | Sdf.Sphere(5 + i * 0.01).Translate((i * 0.3, 0, 0));
        return field;
    }

    private static Vector3d[] Points(int n)
    {
        var rng = new Random(4242);
        var points = new Vector3d[n];
        for (int i = 0; i < n; i++)
            points[i] = new Vector3d(
                (rng.NextDouble() * 2 - 1) * 12,
                (rng.NextDouble() * 2 - 1) * 12,
                (rng.NextDouble() * 2 - 1) * 12);
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
