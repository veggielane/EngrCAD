using System.Text;
using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// Bit-level fingerprints of the POLYGONAL 2D path — <see cref="Region2dBoolean"/> and
/// <see cref="Region2dOffset"/> — over a fixed corpus.
///
/// <para>The curved tier (<see cref="CurvedArrangement2d"/> and friends) was deliberately
/// built as a PARALLEL type so that this path would not move; the diff that introduced it
/// is purely additive, so bit-identity holds by construction rather than by measurement.
/// These fingerprints exist to keep it that way: the straight path carries
/// <c>Shape.Section</c>, <c>Shape.Silhouette</c>, <c>Sketch.Offset</c> and every rendered
/// docs image, and a shared-helper change that "obviously cannot matter" is exactly how a
/// unified arrangement would have leaked into it. A failure here is not necessarily a bug —
/// it is a claim that needs the same oracle the render suite gives, restated deliberately.</para>
/// </summary>
public class Region2dGoldenTests
{
    private static Region2d Box(double x0, double y0, double x1, double y1) =>
        new([(x0, y0), (x1, y0), (x1, y1), (x0, y1)]);

    private static Region2d RotatedSquare(double angle, double radius, double half)
    {
        var centre = new Vector2d(radius * Math.Cos(angle), radius * Math.Sin(angle));
        double c = Math.Cos(angle), s = Math.Sin(angle);
        var corners = new Vector2d[4];
        var local = new Vector2d[] { (-half, -half), (half, -half), (half, half), (-half, half) };
        for (int i = 0; i < 4; i++)
            corners[i] = centre + new Vector2d(local[i].X * c - local[i].Y * s, local[i].X * s + local[i].Y * c);
        return new Region2d(corners);
    }

    /// <summary>FNV-1a over the RAW BITS of every emitted coordinate, in output order, with
    /// the loop structure folded in — so a reordering, a re-nesting or a one-ulp move all
    /// change the answer.</summary>
    private static string Fingerprint(IReadOnlyList<Region2d> regions)
    {
        ulong hash = 14695981039346656037UL;
        void Mix(long value)
        {
            for (int i = 0; i < 8; i++)
            {
                hash ^= (byte)(value >> (8 * i));
                hash *= 1099511628211UL;
            }
        }

        Mix(regions.Count);
        foreach (var region in regions)
        {
            Mix(region.Holes.Count);
            Mix(BitConverter.DoubleToInt64Bits(region.Area));
            foreach (var loop in region.AllLoops())
            {
                Mix(loop.Count);
                foreach (var point in loop)
                {
                    Mix(BitConverter.DoubleToInt64Bits(point.X));
                    Mix(BitConverter.DoubleToInt64Bits(point.Y));
                }
            }
        }
        return hash.ToString("x16");
    }

    public static TheoryData<string, string> Corpus() => new()
    {
        { "union", "5a4d72653c436c81" },
        { "intersection", "81d88c8f825d2ea1" },
        { "difference-with-hole", "916b36fa385e3988" },
        { "offset-round", "97f0a3d105b68a47" },
        { "offset-miter", "30b6b37ea86e1dea" },
        { "offset-inward", "c4478a24e6ff1ff8" },
        { "union-all-rotated", "0ce99a335521ffb9" },
        { "stroke", "59ea390f056cab58" },
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void PolygonalPath_IsBitIdentical(string name, string expected) =>
        Assert.Equal(expected, Fingerprint(Run(name)));

    /// <summary>Prints every fingerprint, so a deliberate change can be re-baselined in one
    /// place rather than eight.</summary>
    [Fact]
    public void EveryCorpusMemberHasAFingerprint()
    {
        var report = new StringBuilder();
        foreach (var row in Corpus())
            report.AppendLine($"{{ \"{row[0]}\", \"{Fingerprint(Run((string)row[0]))}\" }},");
        Assert.Equal(Corpus().Count(), report.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    private static IReadOnlyList<Region2d> Run(string name) => name switch
    {
        "union" => Region2dBoolean.Union(Box(0, 0, 10, 10), Box(5, 5, 15, 15)),
        "intersection" => Region2dBoolean.Intersection(Box(0, 0, 10, 10), Box(5, 5, 15, 15)),
        "difference-with-hole" => Region2dBoolean.Difference(Box(0, 0, 10, 10), Box(3, 3, 7, 7)),
        "offset-round" => Region2dOffset.Offset(Box(0, 0, 20, 10), 3),
        "offset-miter" => Region2dOffset.Offset(Box(0, 0, 20, 10), 3, OffsetJoin.Miter),
        "offset-inward" => Region2dOffset.Offset(Box(0, 0, 20, 10), -2, OffsetJoin.Round),
        "union-all-rotated" => Region2dBoolean.UnionAll(
            [.. Enumerable.Range(0, 8).Select(i => RotatedSquare(i * Math.PI / 4, 5, 3))]),
        "stroke" => Region2dOffset.Stroke(
            [(0, 0), (10, 0), (10, 8), (3, 8)], 2, StrokeCap.Round, OffsetJoin.Round),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown corpus member"),
    };
}
