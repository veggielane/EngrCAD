using System.Diagnostics;
using EngrCAD.Core.Geometry2;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Core.Tests;

/// <summary>
/// The workload the CURVED classification cost was filed against, built deliberately so that
/// it CARRIES the cells × operand-edges product rather than merely being large.
///
/// <para><b>Why the polygonal twin's fixture could not settle this.</b> A point-location
/// index was built for <c>Region2dBoolean.ContainedIn</c>, measured against
/// <see cref="Region2dBooleanBenchmark"/>'s bulk union, and DECLINED at 1.0×: an
/// overlap-heavy union's balanced fold keeps the CELL count tiny exactly where the operand
/// edge counts grow, so the product never gets large and the classification is under the
/// noise floor. The curved entry inherited that verdict as a bar — do not build the curved
/// index without a fixture that provably carries the product — and this is that fixture.</para>
///
/// <para><b>The construction.</b> N horizontal stadiums (a rectangle capped by two exact
/// semicircular arcs) intersected with N vertical ones. Every crossing is its own kept cell,
/// so the result holds N² cells while each operand holds 4N edges: the product grows as N³
/// where the bulk union's stayed flat. The cell count is ASSERTED, so the fixture cannot
/// quietly stop carrying the term it exists to carry.</para>
///
/// <para>Skipped unless <c>ENGRCAD_BENCH</c> is set:
/// <code>
/// $env:ENGRCAD_BENCH = "1"
/// dotnet test tests/EngrCAD.Core.Tests -c Release --filter CurvedRegion2dBooleanBenchmark
/// </code>
/// Warm-up is a wall-clock budget and the reported figure is a MINIMUM over trials — the
/// estimator for a deterministic workload on a machine background load can only slow down.</para>
/// </summary>
public class CurvedRegion2dBooleanBenchmark(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ENGRCAD_BENCH") is not (null or "");

    /// <summary>
    /// A stadium: two straight sides and two exact semicircular ends, with each side split
    /// into <paramref name="pieces"/> collinear segments so the EDGE count can be raised
    /// without changing the shape or the cell count — which is what separates "the index
    /// works" from "this workload cannot feel it".
    /// </summary>
    private static CurvedRegion2d Stadium(Vector2d from, Vector2d to, double half, int pieces)
    {
        var along = (to - from).Normalized();
        var side = along.Perpendicular * half;
        double startAngle = Math.Atan2(side.Y, side.X);
        var edges = new List<CurvedEdge2d>(2 * pieces + 2);
        for (int i = 0; i < pieces; i++)
        {
            edges.Add(CurvedEdge2d.Line(
                Vector2d.Lerp(from, to, (double)i / pieces) + side,
                Vector2d.Lerp(from, to, (double)(i + 1) / pieces) + side));
        }
        edges.Add(CurvedEdge2d.Arc(to, half, startAngle, -Math.PI).WithEndpoints(to + side, to - side));
        for (int i = 0; i < pieces; i++)
        {
            edges.Add(CurvedEdge2d.Line(
                Vector2d.Lerp(to, from, (double)i / pieces) - side,
                Vector2d.Lerp(to, from, (double)(i + 1) / pieces) - side));
        }
        edges.Add(CurvedEdge2d.Arc(from, half, startAngle + Math.PI, -Math.PI)
            .WithEndpoints(from - side, from + side));
        return new CurvedRegion2d(edges);
    }

    private static (List<CurvedRegion2d> Horizontal, List<CurvedRegion2d> Vertical) Grid(
        int n, int pieces = 1)
    {
        const double span = 100;
        double spacing = span / (n + 1);
        double half = spacing / 6;   // thin enough that the strips never merge
        var horizontal = new List<CurvedRegion2d>(n);
        var vertical = new List<CurvedRegion2d>(n);
        for (int i = 1; i <= n; i++)
        {
            double at = i * spacing;
            horizontal.Add(Stadium((2, at), (span - 2, at), half, pieces));
            vertical.Add(Stadium((at, 2), (at, span - 2), half, pieces));
        }
        return (horizontal, vertical);
    }

    [Fact]
    public void TheFixtureCarriesTheCellsTimesEdgesProduct()
    {
        // Small enough to run in the ordinary suite: the point is the SHAPE of the workload,
        // not its size. Every crossing of a horizontal strip with a vertical one is its own
        // kept cell, so the result count is n^2 exactly.
        const int n = 6;
        var (horizontal, vertical) = Grid(n);
        var result = CurvedRegion2dBoolean.Intersection(horizontal, vertical);
        Assert.Equal(n * n, result.Count);
        Assert.Equal(4 * n, horizontal.Sum(r => r.Outer.Count));
        Assert.Equal(4 * n, vertical.Sum(r => r.Outer.Count));
    }

    [Fact]
    public void TheParityIndexIsResultIdentical()
    {
        const int n = 7;
        var (horizontal, vertical) = Grid(n);

        var indexed = CurvedRegion2dBoolean.Intersection(horizontal, vertical);
        IReadOnlyList<CurvedRegion2d> walked;
        try
        {
            CurvedRegion2dBoolean.UseParityIndex = false;
            walked = CurvedRegion2dBoolean.Intersection(horizontal, vertical);
        }
        finally
        {
            CurvedRegion2dBoolean.UseParityIndex = true;
        }

        // Parity is a COUNT over edges no skipped edge can pass, so the index removes only
        // zero terms: the two paths agree bit for bit rather than to a tolerance.
        Assert.Equal(walked.Count, indexed.Count);
        for (int i = 0; i < walked.Count; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(walked[i].Area),
                         BitConverter.DoubleToInt64Bits(indexed[i].Area));
            Assert.Equal(walked[i].Outer.Count, indexed[i].Outer.Count);
            for (int e = 0; e < walked[i].Outer.Count; e++)
                Assert.Equal(walked[i].Outer[e], indexed[i].Outer[e]);
        }
    }

    [Fact]
    public void MeasureCrossingStrips()
    {
        if (!Enabled)
            return;
        // (strips, side pieces): the first column grows cells AND edges together, the second
        // holds the cell count and raises the edge count alone — so a ratio that moves only
        // with the second is telling you the term is real and the first workload is not the
        // one that carries it.
        foreach (var (n, pieces) in ((int N, int Pieces)[])[(8, 1), (16, 1), (24, 1), (32, 1), (16, 8), (16, 32)])
        {
            var (horizontal, vertical) = Grid(n, pieces);
            int cells = CurvedRegion2dBoolean.Intersection(horizontal, vertical).Count;
            int edges = horizontal.Sum(r => r.Outer.Count);

            // A wall-clock warm-up BUDGET, never a warm-up count: tiered compilation is
            // promoted on a background queue, so a fixed number of calls does not establish
            // steady state (the recorded benchmark lesson).
            var warmup = Stopwatch.StartNew();
            while (warmup.ElapsedMilliseconds < 500)
            {
                Time(horizontal, vertical, true);
                Time(horizontal, vertical, false);
            }

            // INTERLEAVED trials, minima: the two arms then share one thermal and scheduling
            // state, so the ratio is a property of the code rather than of the neighbours.
            double indexed = double.PositiveInfinity;
            double walked = double.PositiveInfinity;
            for (int trial = 0; trial < 9; trial++)
            {
                indexed = Math.Min(indexed, Time(horizontal, vertical, true));
                walked = Math.Min(walked, Time(horizontal, vertical, false));
            }
            output.WriteLine(
                $"n={n,3}  cells={cells,5}  edges/operand={edges,5}  "
                + $"walk {walked,8:F1} ms   index {indexed,8:F1} ms   {walked / indexed,5:F2}x");
        }
    }

    private static double Time(
        IReadOnlyList<CurvedRegion2d> a, IReadOnlyList<CurvedRegion2d> b, bool useIndex)
    {
        try
        {
            CurvedRegion2dBoolean.UseParityIndex = useIndex;
            var clock = Stopwatch.StartNew();
            CurvedRegion2dBoolean.Intersection(a, b);
            return clock.Elapsed.TotalMilliseconds;
        }
        finally
        {
            CurvedRegion2dBoolean.UseParityIndex = true;
        }
    }
}
