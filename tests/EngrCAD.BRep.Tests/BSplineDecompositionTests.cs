using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Bézier decomposition (A5.6 + the A5.1 clamping an unclamped vector needs first).
///
/// <para>The claim is a CHANGE OF BASIS, so the oracle is the curve itself: every segment
/// must evaluate to the source at every parameter of its own span. That is what separates a
/// decomposition from a fit, and it is asserted densely rather than at the joints — a wrong
/// split (the classic failure: taking control points four at a time on a knot vector that is
/// not in Bézier form) reproduces the ENDPOINTS and misses everything between them, which is
/// exactly why the joint-only check would pass it. The mutation is measured here rather than
/// argued.</para>
/// </summary>
public class BSplineDecompositionTests
{
    private static readonly Vector2d[] Wave =
    [
        new(0, 0), new(3, 4), new(7, -4), new(10, 0), new(13, 4), new(17, -4), new(20, 0),
    ];

    /// <summary>Largest distance between a segment and the source over its own span.</summary>
    private static double MaxDeviation(NurbsCurve2d source, IReadOnlyList<NurbsCurve2d> segments, int samples = 257)
    {
        double worst = 0;
        foreach (var segment in segments)
        {
            for (int i = 0; i <= samples; i++)
            {
                double u = segment.Domain.Start
                    + (segment.Domain.End - segment.Domain.Start) * i / samples;
                worst = Math.Max(worst, segment.PointAt(u).DistanceTo(source.PointAt(u)));
            }
        }
        return worst;
    }

    /// <summary>
    /// A curve ALREADY in Bézier form comes back bit-identical — not by a fast path, but
    /// because A5.6's inner loop is skipped wherever an interior knot's multiplicity already
    /// equals the degree, so no arithmetic touches a control point. That is what makes it
    /// safe for a consumer to route every spline through the general decomposition instead of
    /// keeping a narrow special case beside it.
    /// </summary>
    [Fact]
    public void ACurveAlreadyInBezierForm_DecomposesToItselfBitForBit()
    {
        var curve = new NurbsCurve2d(3, Wave, null, [0, 0, 0, 0, 1, 1, 1, 2, 2, 2, 2]);
        var segments = BSplineDecomposition.ToBezierSegments(curve);

        Assert.Equal(2, segments.Count);
        for (int s = 0; s < 2; s++)
        {
            for (int i = 0; i < 4; i++)
            {
                var expected = Wave[3 * s + i];
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(expected.X),
                    BitConverter.DoubleToInt64Bits(segments[s].ControlPoints[i].X));
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(expected.Y),
                    BitConverter.DoubleToInt64Bits(segments[s].ControlPoints[i].Y));
                Assert.Equal(1.0, segments[s].Weights[i]);
            }
        }
        Assert.Equal([0, 0, 0, 0, 1, 1, 1, 1], segments[0].Knots);
        Assert.Equal([1, 1, 1, 1, 2, 2, 2, 2], segments[1].Knots);
    }

    /// <summary>
    /// The case the DXF reader used to refuse: a UNIFORM, unclamped knot vector. It needs
    /// clamping (knot insertion at both domain ends) before A5.6 has anything to split, and
    /// the result is exact.
    /// </summary>
    [Fact]
    public void AUniformUnclampedCubic_DecomposesExactly()
    {
        var curve = new NurbsCurve2d(
            3, [new(0, 0), new(3, 4), new(7, -4), new(10, 0), new(12, 3)], null,
            [0, 1, 2, 3, 4, 5, 6, 7, 8]);
        var segments = BSplineDecomposition.ToBezierSegments(curve);

        // Domain [3, 5]: two non-empty spans.
        Assert.Equal(2, segments.Count);
        Assert.Equal(3, segments[0].Domain.Start, 12);
        Assert.Equal(4, segments[0].Domain.End, 12);
        Assert.Equal(4, segments[1].Domain.Start, 12);
        Assert.Equal(5, segments[1].Domain.End, 12);

        double deviation = MaxDeviation(curve, segments);
        Assert.True(deviation < 1e-13, $"the decomposition drifts by {deviation}");
    }

    /// <summary>
    /// The dangerous shape, and the mutation that proves the test has teeth: SEVEN control
    /// points is a Bézier-compatible COUNT (3k + 1), so a reader splitting them four at a
    /// time produces a plausible curve at the wrong parameters. The clamped-with-single-
    /// interior-knots spline is a genuine B-spline of FOUR spans, and the naive split is
    /// measurably a different curve.
    /// </summary>
    [Fact]
    public void AClampedCubicWithSingleInteriorKnots_SplitsIntoFourSpansAndNotIntoTwoNaiveBeziers()
    {
        var curve = new NurbsCurve2d(3, Wave, null, [0, 0, 0, 0, 1, 2, 3, 4, 4, 4, 4]);
        var segments = BSplineDecomposition.ToBezierSegments(curve);

        Assert.Equal(4, segments.Count);
        double deviation = MaxDeviation(curve, segments);
        Assert.True(deviation < 1e-13, $"the decomposition drifts by {deviation}");

        // The naive four-at-a-time split over the same control points: the two curves share
        // their endpoints, so only an interior sample can tell them apart.
        var naive = new BezierCurve2d(Wave[0], Wave[1], Wave[2], Wave[3]);
        double naiveMiss = 0;
        for (int i = 0; i <= 64; i++)
        {
            double t = i / 64.0;
            // The naive reading maps its own [0,1] onto the first QUARTER of the domain.
            naiveMiss = Math.Max(naiveMiss, naive.PointAt(t).DistanceTo(curve.PointAt(t)));
        }
        Assert.True(naiveMiss > 1, $"the naive split is only {naiveMiss} away — the fixture cannot see it");
    }

    /// <summary>
    /// Rational curves decompose too — insertion runs on homogeneous coordinates, so weights
    /// ride along. A quarter circle is the check with teeth: its exact rational form has a
    /// weight of 1/√2 in the middle, and a decomposition that dropped or mangled the weights
    /// would return a parabola through the same three points.
    /// </summary>
    [Fact]
    public void ARationalArc_DecomposesWithItsWeights()
    {
        // A half circle as one rational quadratic with an interior knot: two quarter arcs.
        var curve = NurbsCurve2d.Arc((0, 0), 5, 0, Math.PI);
        var segments = BSplineDecomposition.ToBezierSegments(curve);

        Assert.True(segments.Count >= 2);
        double deviation = MaxDeviation(curve, segments);
        Assert.True(deviation < 1e-12, $"the rational decomposition drifts by {deviation}");

        // Every sample sits on the circle exactly — the property a dropped weight destroys.
        foreach (var segment in segments)
        {
            for (int i = 0; i <= 32; i++)
            {
                double u = segment.Domain.Start + (segment.Domain.End - segment.Domain.Start) * i / 32.0;
                Assert.Equal(5.0, segment.PointAt(u).Length, 12);
            }
        }
        Assert.Contains(segments, s => s.Weights.Any(w => Math.Abs(w - 1) > 1e-6));
    }

    [Fact]
    public void HighDegreeSplines_DecomposeExactly()
    {
        // Degree 5 over a knot vector with mixed interior multiplicities.
        var points = new Vector2d[]
        {
            new(0, 0), new(2, 6), new(5, -3), new(9, 5), new(13, -2), new(16, 4), new(20, 0),
        };
        var curve = new NurbsCurve2d(5, points, null, [0, 0, 0, 0, 0, 0, 1, 2, 2, 2, 2, 2, 2]);
        var segments = BSplineDecomposition.ToBezierSegments(curve);

        Assert.Equal(2, segments.Count);
        foreach (var segment in segments)
            Assert.Equal(6, segment.ControlPoints.Count);
        double deviation = MaxDeviation(curve, segments);
        Assert.True(deviation < 1e-12, $"the degree-5 decomposition drifts by {deviation}");
    }

    /// <summary>
    /// The pieces CHAIN: each segment's end is the next one's start. A decomposition that
    /// clamped one end wrongly would still evaluate correctly inside each span and leave a
    /// gap here.
    /// </summary>
    [Fact]
    public void ConsecutiveSegments_ShareTheirEndpoints()
    {
        var curve = new NurbsCurve2d(
            3, [new(0, 0), new(3, 4), new(7, -4), new(10, 0), new(12, 3)], null,
            [0, 1, 2, 3, 4, 5, 6, 7, 8]);
        var segments = BSplineDecomposition.ToBezierSegments(curve);
        for (int i = 1; i < segments.Count; i++)
        {
            double gap = segments[i - 1].ControlPoints[^1].DistanceTo(segments[i].ControlPoints[0]);
            Assert.True(gap < 1e-12, $"segments {i - 1} and {i} are {gap} apart");
        }
    }

    /// <summary>
    /// TWO independent constructions, checked against each other.
    /// <see cref="NurbsCurve2d.TryToCurvedEdges"/> already produced exact Bézier pieces for a
    /// non-rational spline of degree ≤ 3, by reading each span's HERMITE data — endpoints and
    /// end derivatives — while this decomposition interpolates control points through knot
    /// insertion. They share no arithmetic, so agreement is evidence rather than tautology.
    /// </summary>
    [Fact]
    public void KnotInsertionAndTheHermiteRoute_AgreeControlPointForControlPoint()
    {
        var curve = new NurbsCurve2d(3, Wave, null, [0, 0, 0, 0, 1, 2, 3, 4, 4, 4, 4]);
        var inserted = BSplineDecomposition.ToBezierSegments(curve);

        var hermite = new List<CurvedEdge2d>();
        Assert.True(curve.TryToCurvedEdges(hermite));
        Assert.Equal(inserted.Count, hermite.Count);

        double worst = 0;
        for (int s = 0; s < inserted.Count; s++)
        {
            var edge = hermite[s];
            Assert.True(edge.IsBezier, "the Hermite route demoted a span this fixture needs curved");
            Vector2d[] fromHermite = [edge.Start, edge.Control1, edge.Control2, edge.End];
            for (int i = 0; i < 4; i++)
                worst = Math.Max(worst, fromHermite[i].DistanceTo(inserted[s].ControlPoints[i]));
        }
        Assert.True(worst < 1e-12, $"the two constructions differ by {worst}");
    }

    [Fact]
    public void The3dOverload_DecomposesExactly()
    {
        var curve = new NurbsCurve(
            3,
            [new(0, 0, 0), new(3, 4, 1), new(7, -4, 2), new(10, 0, 3), new(12, 3, 4)],
            null,
            [0, 1, 2, 3, 4, 5, 6, 7, 8]);
        var segments = BSplineDecomposition.ToBezierSegments(curve);

        Assert.Equal(2, segments.Count);
        double worst = 0;
        foreach (var segment in segments)
        {
            for (int i = 0; i <= 128; i++)
            {
                double u = segment.Domain.Start + (segment.Domain.End - segment.Domain.Start) * i / 128.0;
                worst = Math.Max(worst, segment.PointAt(u).DistanceTo(curve.PointAt(u)));
            }
        }
        Assert.True(worst < 1e-12, $"the 3D decomposition drifts by {worst}");
    }

    /// <summary>
    /// An INTERIOR knot above the degree makes the curve discontinuous there — two curves
    /// sharing a knot vector rather than one with Bézier pieces — so it is refused BY NAME
    /// rather than answered with pieces that do not chain.
    /// </summary>
    [Fact]
    public void ADiscontinuousInteriorKnot_IsRefusedByName()
    {
        // Degree 2, clamped, with the INTERIOR knot 1 repeated three times: the two halves
        // meet at a break rather than joining.
        var curve = new NurbsCurve2d(
            2, [new(0, 0), new(2, 3), new(4, 0), new(6, 3), new(8, 0), new(10, 3)], null,
            [0, 0, 0, 1, 1, 1, 2, 2, 2]);
        var message = Assert.Throws<ArgumentException>(
            () => BSplineDecomposition.ToBezierSegments(curve)).Message;
        Assert.Contains("multiplicity", message);
        Assert.Contains("discontinuous", message);
    }
}
