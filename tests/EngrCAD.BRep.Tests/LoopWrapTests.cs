using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// The wrap test that separates a band boundary from a contractible region boundary on a
/// periodic surface. The rule is the net u DRIFT over the traversal; the u SPAN is only the
/// cheap first half of an AND, because a contractible loop may legitimately reach most of the
/// way round the period and come back (a threaded rod's end-chamfer facet spans 272°).
/// </summary>
public class LoopWrapTests
{
    private const double Period = 2 * Math.PI;

    /// <summary>A rectangle in (u, v) reaching <paramref name="degrees"/> round and back.</summary>
    private static List<Vector2d> ContractibleFacet(double degrees, int samples = 64)
    {
        double span = degrees * Math.PI / 180;
        var loop = new List<Vector2d>();
        for (int i = 0; i < samples; i++)
            loop.Add(new Vector2d(span * i / samples, 0.25));
        loop.Add(new Vector2d(span, 0.375));
        for (int i = 0; i < samples; i++)
            loop.Add(new Vector2d(span * (samples - i) / samples, 0.5));
        loop.Add(new Vector2d(0, 0.375));
        return loop;
    }

    /// <summary>A ring that genuinely goes round: u advances a full period, minus one step.</summary>
    private static List<Vector2d> WrappingRing(int samples = 64)
    {
        var loop = new List<Vector2d>();
        for (int i = 0; i < samples; i++)
            loop.Add(new Vector2d(Period * i / samples, 0.4));
        return loop;
    }

    [Fact]
    public void ContractibleFacetSpanningMostOfThePeriod_DoesNotWrap()
    {
        // 272 degrees is the measured span of a threaded rod's end-chamfer facet on its cone.
        var facet = ContractibleFacet(272);
        double span = facet.Max(p => p.X) - facet.Min(p => p.X);

        // It DOES span more than three quarters of the period — so a span test calls it a band.
        Assert.True(span > 0.75 * Period);
        // ...but its net drift is nil, which is what actually decides.
        Assert.True(Math.Abs(facet[^1].X - facet[0].X) < 0.01 * Period);
        Assert.False(FaceGeometry.LoopWrapsPeriod(facet, Period));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(269)]
    [InlineData(272)]
    [InlineData(359)]
    public void ContractibleFacets_NeverWrap_AtAnySpan(double degrees) =>
        Assert.False(FaceGeometry.LoopWrapsPeriod(ContractibleFacet(degrees), Period));

    [Fact]
    public void WrappingRing_Wraps_InBothDirections()
    {
        var ring = WrappingRing();
        Assert.True(FaceGeometry.LoopWrapsPeriod(ring, Period));

        var reversed = ring.AsEnumerable().Reverse().ToList();
        Assert.True(FaceGeometry.LoopWrapsPeriod(reversed, Period));
    }

    [Fact]
    public void DoubleWrap_Wraps()
    {
        var loop = new List<Vector2d>();
        for (int i = 0; i < 128; i++)
            loop.Add(new Vector2d(2 * Period * i / 128, 0.4));
        Assert.True(FaceGeometry.LoopWrapsPeriod(loop, Period));
    }

    [Fact]
    public void AperiodicSurface_NeverWraps() =>
        Assert.False(FaceGeometry.LoopWrapsPeriod(WrappingRing(), 0));
}
