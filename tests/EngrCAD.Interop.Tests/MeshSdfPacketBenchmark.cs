using System.Diagnostics;
using EngrCAD.Core;
using EngrCAD.Core.Spatial;
using EngrCAD.Implicit;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// The measurement behind "<see cref="MeshSdf"/> keeps the per-point <c>Bvh.Nearest</c>
/// loop", second attempt. Inert unless <c>ENGRCAD_BENCH</c> is set:
/// <code>
/// $env:ENGRCAD_BENCH = "1"
/// dotnet test tests/EngrCAD.Interop.Tests -c Release --filter FullyQualifiedName~MeshSdfPacketBenchmark -l "console;verbosity=detailed"
/// </code>
/// <para>
/// <b>The idea.</b> 74–85% of a mesh narrow band's wall clock is inside
/// <c>Bvh.Nearest</c>. Seeding the branch and bound was built, verified bit-identical and
/// measured at only 1.12–1.20× (see <see cref="MeshSdfBatchTests"/>); the untried lever was
/// a <em>packet</em> query — one traversal per coherent group of points, per-point pruning
/// at the leaves, so the node-test cost is amortized across the group rather than the
/// initial bound being improved.
/// </para>
/// <para>
/// <b>Built and measured, and it does not survive contact with the batch seam.</b>
/// Reference machine (win-x64, .NET 10.0.302, Release, otherwise idle), 47 724 triangles,
/// points on a narrow band around the surface, baseline = the production
/// <c>Bvh.Nearest</c> with the same struct metric <see cref="MeshSdf"/> uses:
/// </para>
/// <code>
///  cell | group      | points  | Bvh.Nearest | squared | packet | packet+seed
///  0.05 | 2^3 block  |   78576 |      291 ms |   0.99x |  1.16x |       1.03x
///  0.05 | 4^3 block  |  116864 |      794 ms |   0.97x |  0.96x |       0.98x
///  0.05 | 8^3 block  |  129024 |     1073 ms |   0.98x |  0.47x |       0.49x
///  0.03 | 2^3 block  |  223472 |      562 ms |   0.98x |  1.45x |       1.22x
///  0.03 | 4^3 block  |  426048 |     1755 ms |   0.99x |  1.30x |       1.31x
///  0.03 | row of 8   |  222672 |      550 ms |   0.94x |  0.86x |       0.80x
///  0.03 | row of 64  |  103616 |      964 ms |   0.98x |  0.30x |       0.31x
/// </code>
/// <para>
/// <b>Why the rows are the verdict and the blocks are not.</b> A packet's shared bound is
/// governed by the group's DIAMETER: a node is visited when it can beat the worst point's
/// best, so a spread-out group visits the union of every member's traversal. A 2³ block is
/// compact enough to win 1.45×; the same 8 points laid out as a z-consecutive ROW already
/// lose (0.86×), and 64 of them span 1.9 units on a model 3 units across, at which point
/// the shared bound is the whole model and the packet degenerates into a brute-force scan
/// (0.30×). <b>Rows are what the batch seam actually delivers.</b>
/// <c>Sdf.Evaluate(points, distances)</c> hands over a flat span with no structure, and
/// every bulk consumer — grid bakes, narrow-band fills, Surface Nets sampling — generates
/// it z-fastest. <c>MeshSdf</c> cannot regroup a collinear run into blocks, and giving the
/// batch contract a "these points form a compact block" channel is a large API change to
/// buy at most 1.45× in a case no caller produces.
/// </para>
/// <para>
/// <b>Two negatives worth keeping alongside it.</b> Seeding the packet from one exact query
/// at the group's centre — a very tight upper bound for every member — changes nothing
/// (0.80–1.31×, and it makes the best case WORSE), which is the same lesson the previous
/// experiment learned: <em>a nearest-first branch and bound is already its own seed.</em>
/// And pruning on SQUARED distances throughout, removing one <c>Math.Sqrt</c> from every
/// box test and every triangle test, measures 0.94–0.99× — the obvious next guess, and not
/// a lever either.
/// </para>
/// </summary>
public class MeshSdfPacketBenchmark(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ENGRCAD_BENCH") is not (null or "");

    /// <summary>The same distance kernel <see cref="MeshSdf"/> runs, so the baseline is the
    /// production path rather than something written differently for the comparison.</summary>
    private readonly struct Metric(Vector3d[] a, Vector3d[] b, Vector3d[] c, Vector3d p) : IBvhDistance
    {
        public double DistanceTo(int t) =>
            Math.Sqrt(Distance3d.ClosestPointOnTriangle(p, a[t], b[t], c[t], out _).DistanceSquaredTo(p));
    }

    [Fact]
    public void PacketVersusPerPointNearest()
    {
        if (!Enabled)
            return;

        var mesh = SurfaceNets.Polygonize(
            Sdf.Sphere(1) | Sdf.Box(1.4, 1.4, 1.4).Translate((0.7, 0.2, 0)),
            new Aabb((-1.3, -1.3, -1.3), (1.6, 1.3, 1.3)), 96).Triangulated();

        int n = mesh.FaceCount;
        var a = new Vector3d[n];
        var b = new Vector3d[n];
        var c = new Vector3d[n];
        var boxes = new Aabb[n];
        foreach (var face in mesh.Faces)
        {
            int i = face.Index;
            var h = face.AnyHalfEdge;
            a[i] = h.Origin.Position;
            b[i] = h.Next.Origin.Position;
            c[i] = h.Next.Next.Origin.Position;
            boxes[i] = Aabb.FromPoints([a[i], b[i], c[i]]);
        }
        var bvh = Bvh.Build(boxes);
        var region = mesh.ComputeBounds().Expanded(0.3);
        output.WriteLine($"{n} triangles");
        output.WriteLine(" cell | group      | points  | Bvh.Nearest | squared | packet | packet+seed");

        double TriangleSquared(int t, in Vector3d p) =>
            Distance3d.ClosestPointOnTriangle(p, a[t], b[t], c[t], out _).DistanceSquaredTo(p);

        foreach (var (cell, side, rows) in new (double, int, bool)[]
        {
            (0.05, 2, false), (0.05, 4, false), (0.05, 8, false),
            (0.03, 2, false), (0.03, 4, false),
            (0.03, 2, true), (0.03, 4, true),
        })
        {
            int size = side * side * side;
            var groups = new List<Vector3d[]>();
            int nx = (int)(region.Size.X / cell), ny = (int)(region.Size.Y / cell), nz = (int)(region.Size.Z / cell);
            for (int i = 0; i + side <= nx; i += side)
            {
                for (int j = 0; j + side <= ny; j += side)
                {
                    int span = rows ? size : side;   // a row reaches `size` cells along z
                    for (int k = 0; k + span <= nz; k += side)
                    {
                        var centre = region.Min +
                            ((i + side * 0.5) * cell, (j + side * 0.5) * cell, (k + side * 0.5) * cell);
                        var probe = new Metric(a, b, c, centre);
                        bvh.Nearest(centre, ref probe, out _, out double distance);
                        if (distance > 3 * side * cell)
                            continue;   // outside the band a narrow-band bake never evaluates
                        var points = new Vector3d[size];
                        int q = 0;
                        if (rows)
                        {
                            for (int w = 0; w < size; w++)
                                points[q++] = region.Min + (i * cell, j * cell, (k + w) * cell);
                        }
                        else
                        {
                            for (int u = 0; u < side; u++)
                            {
                                for (int v = 0; v < side; v++)
                                {
                                    for (int w = 0; w < side; w++)
                                        points[q++] = region.Min + ((i + u) * cell, (j + v) * cell, (k + w) * cell);
                                }
                            }
                        }
                        groups.Add(points);
                    }
                }
            }

            var best = new double[size];
            var item = new int[size];
            var live = new int[size];
            var root = bvh.Root;

            void PerPoint()
            {
                foreach (var points in groups)
                {
                    for (int p = 0; p < points.Length; p++)
                    {
                        var metric = new Metric(a, b, c, points[p]);
                        bvh.Nearest(points[p], ref metric, out item[p], out best[p]);
                    }
                }
            }

            void PerPointSquared()
            {
                var stack = new Bvh.NodeView[64];
                foreach (var points in groups)
                {
                    for (int p = 0; p < points.Length; p++)
                    {
                        var query = points[p];
                        double bestSquared = double.PositiveInfinity;
                        int top = 0;
                        stack[top++] = root;
                        while (top > 0)
                        {
                            var node = stack[--top];
                            if (BoxDistanceSquared(node.Bounds, query) >= bestSquared)
                                continue;
                            if (node.IsLeaf)
                            {
                                foreach (int t in node.Items)
                                    bestSquared = Math.Min(bestSquared, TriangleSquared(t, query));
                                continue;
                            }
                            var (l, r) = (node.Left, node.Right);
                            bool nearerLeft =
                                BoxDistanceSquared(l.Bounds, query) <= BoxDistanceSquared(r.Bounds, query);
                            stack[top++] = nearerLeft ? r : l;
                            stack[top++] = nearerLeft ? l : r;
                        }
                        best[p] = Math.Sqrt(bestSquared);
                    }
                }
            }

            void Packet(bool seed)
            {
                var stack = new Bvh.NodeView[64];
                foreach (var points in groups)
                {
                    var box = Aabb.FromPoints(points);
                    double worst;
                    if (seed)
                    {
                        var metric = new Metric(a, b, c, box.Center);
                        bvh.Nearest(box.Center, ref metric, out int start, out _);
                        worst = 0;
                        for (int p = 0; p < points.Length; p++)
                        {
                            best[p] = TriangleSquared(start, points[p]);
                            item[p] = start;
                            worst = Math.Max(worst, best[p]);
                        }
                    }
                    else
                    {
                        Array.Fill(best, double.PositiveInfinity);
                        worst = double.PositiveInfinity;
                    }

                    int top = 0;
                    stack[top++] = root;
                    while (top > 0)
                    {
                        var node = stack[--top];
                        if (BoxDistanceSquared(node.Bounds, box) >= worst)
                            continue;
                        if (!node.IsLeaf)
                        {
                            var (l, r) = (node.Left, node.Right);
                            bool nearerLeft =
                                BoxDistanceSquared(l.Bounds, box) <= BoxDistanceSquared(r.Bounds, box);
                            stack[top++] = nearerLeft ? r : l;
                            stack[top++] = nearerLeft ? l : r;
                            continue;
                        }

                        // One box distance per POINT per leaf — never per (point, triangle).
                        int liveCount = 0;
                        var bounds = node.Bounds;
                        for (int p = 0; p < points.Length; p++)
                        {
                            if (BoxDistanceSquared(bounds, points[p]) < best[p])
                                live[liveCount++] = p;
                        }
                        if (liveCount == 0)
                            continue;
                        foreach (int t in node.Items)
                        {
                            for (int i = 0; i < liveCount; i++)
                            {
                                int p = live[i];
                                double d = TriangleSquared(t, points[p]);
                                if (d < best[p])
                                {
                                    best[p] = d;
                                    item[p] = t;
                                }
                            }
                        }
                        worst = 0;
                        for (int p = 0; p < points.Length; p++)
                            worst = Math.Max(worst, best[p]);
                    }
                }
            }

            double baseline = Bench(PerPoint);
            double squared = Bench(PerPointSquared);
            double packet = Bench(() => Packet(seed: false));
            double seeded = Bench(() => Packet(seed: true));
            string label = rows ? $"row of {size}" : $"{side}^3 block";
            output.WriteLine(
                $" {cell:F2} | {label,-10} | {groups.Count * size,7} | {baseline,8:F0} ms | " +
                $"{baseline / squared,6:F2}x | {baseline / packet,5:F2}x | {baseline / seeded,10:F2}x");
        }
    }

    /// <summary>A wall-clock warm-up BUDGET, then best-of over a fixed budget — a warm-up
    /// COUNT is meaningless under JIT tiering.</summary>
    private static double Bench(Action body)
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
        while (watch.ElapsedMilliseconds < 2500);
        return watch.Elapsed.TotalMilliseconds / runs;
    }

    private static double BoxDistanceSquared(in Aabb box, in Vector3d p)
    {
        double dx = Math.Max(0, Math.Max(box.Min.X - p.X, p.X - box.Max.X));
        double dy = Math.Max(0, Math.Max(box.Min.Y - p.Y, p.Y - box.Max.Y));
        double dz = Math.Max(0, Math.Max(box.Min.Z - p.Z, p.Z - box.Max.Z));
        return dx * dx + dy * dy + dz * dz;
    }

    private static double BoxDistanceSquared(in Aabb x, in Aabb y)
    {
        double dx = Math.Max(0, Math.Max(x.Min.X - y.Max.X, y.Min.X - x.Max.X));
        double dy = Math.Max(0, Math.Max(x.Min.Y - y.Max.Y, y.Min.Y - x.Max.Y));
        double dz = Math.Max(0, Math.Max(x.Min.Z - y.Max.Z, y.Min.Z - x.Max.Z));
        return dx * dx + dy * dy + dz * dz;
    }
}
