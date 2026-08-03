using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The closed-curve seam family, in the parity machinery: a full turn's END must be its
/// START <i>exactly</i>, or the y-monotone pieces either side of the seam leave a gap and
/// an upward-ray parity count misses a crossing.
///
/// <para><b>Why the band is reachable without anyone writing a tiny number</b>:
/// <c>sin(2π)</c> is −2.449e-16, so a full-circle arc's last piece used to end at
/// <c>centre.Y − r·2.449e-16</c> while its first piece began at <c>centre.Y</c> exactly.
/// Any ordinate in that gap crossed NEITHER right-branch piece, so a point measurably
/// inside the disc came back positive — the sign, not the magnitude. Sampling a full turn
/// inclusively (θ = 2πi/n for i up to n) lands the last sample exactly there, which is how
/// it surfaced: an odd 121 boundary transitions around a 60-tooth ring, combinatorially
/// impossible on a closed curve.</para>
/// </summary>
public class SketchRegionSeamTests
{
    /// <summary>The reported case, verbatim: r = 70.5, ten units inside, at the seam.</summary>
    [Fact]
    public void AFullCircleIsClosedAtItsSeamOrdinate()
    {
        var region = new SketchRegion(Sketch.Circle(70.5));

        // (60, 0) always worked; (60, −1.47e-14) is the reported failure. Both are 10.5
        // inside, so the MAGNITUDE was never in doubt — only the parity.
        Assert.Equal(-10.5, region.SignedDistance(new Vector2d(60, 0)), 9);
        Assert.Equal(-10.5, region.SignedDistance(new Vector2d(60, -1.47e-14)), 9);
        Assert.Equal(-10.5, region.SignedDistance(new Vector2d(60, -1e-9)), 9);
    }

    /// <summary>
    /// The band is at the seam's own ordinate, so sweep it rather than probing one point:
    /// a single sample could sit either side of a gap that is only ~1.7e-14 wide.
    /// </summary>
    [Theory]
    [InlineData(70.5)]
    [InlineData(1.0)]
    [InlineData(1e4)]
    public void NoOrdinateNearTheSeamReportsAnInteriorPointOutside(double radius)
    {
        var region = new SketchRegion(Sketch.Circle(radius));
        double x = radius * 0.5;                       // comfortably inside, on the +x ray
        double expected = -(radius - x);

        // Ordinates spanning the old gap by three decades either side of it.
        for (int k = -40; k <= 40; k++)
        {
            double y = k == 0 ? 0 : Math.Sign(k) * radius * Math.Pow(10, -18 + Math.Abs(k) / 4.0);
            double d = region.SignedDistance(new Vector2d(x, y));
            Assert.True(d < 0, $"r={radius} y={y:E3} read {d:F6}, expected ~{expected:F6} (inside)");
        }
    }

    /// <summary>
    /// The failure as it was actually noticed: a boundary crossing count that is ODD is
    /// impossible on a closed curve, whatever the geometry. This is the assertion with
    /// teeth — it needs no knowledge of where the seam is.
    /// </summary>
    [Fact]
    public void ARadialSweepCrossesAClosedBoundaryAnEvenNumberOfTimes()
    {
        var region = new SketchRegion(Sketch.Circle(30));
        const int samples = 720;

        int transitions = 0;
        bool previous = region.SignedDistance(PointOnRay(0)) < 0;
        // INCLUSIVE of 2π — that is the sampling which lands on the seam, and a caller
        // closing a loop naturally writes it.
        for (int i = 1; i <= samples; i++)
        {
            bool inside = region.SignedDistance(PointOnRay(2 * Math.PI * i / samples)) < 0;
            if (inside != previous)
                transitions++;
            previous = inside;
        }

        Assert.Equal(0, transitions % 2);
        return;

        // A point that orbits the centre at a radius well inside the disc: every sample is
        // interior, so the honest answer is zero transitions and any odd count is the bug.
        static Vector2d PointOnRay(double theta) =>
            new(20 * Math.Cos(theta), 20 * Math.Sin(theta));
    }

    /// <summary>
    /// The fix must not move a partial arc: only a CLOSED sweep gets the exact-substitution
    /// rule, since a partial arc's two ends are genuinely different points.
    /// </summary>
    [Fact]
    public void APartialArcKeepsItsOwnEndpoints()
    {
        // A half-disc: flat along y = 0, bulging up.
        var half = Sketch.Start(-10, 0)
            .ArcTo(new Vector2d(10, 0), 10, clockwise: true)
            .Close();
        var region = new SketchRegion(half);

        Assert.True(region.SignedDistance(new Vector2d(0, 5)) < 0, "inside the bulge");
        Assert.True(region.SignedDistance(new Vector2d(0, -5)) > 0, "below the flat side");
    }
}
