using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// ISO 286 limits and fits. The tables are transcriptions, so the tests assert rows in
/// the STANDARD'S OWN micrometres — the form a human verifies against the printed
/// chart — and the classic fit numbers every handbook reproduces (Ø40 H7/g6 clears by
/// 9…50 µm). A re-derived formula would agree with its own mistake; a chart row cannot.
/// </summary>
public class Iso286Tests
{
    private static (double UpperMicrons, double LowerMicrons) Microns(double size, string designation)
    {
        var limits = Iso286.Limits(size, designation);
        return (limits.Upper * 1000, limits.Lower * 1000);
    }

    [Theory]
    // The most-reproduced chart rows in mechanical engineering, straight off the card.
    [InlineData(40, "H7", 25, 0)]
    [InlineData(40, "g6", -9, -25)]
    [InlineData(40, "f7", -25, -50)]
    [InlineData(40, "k6", 18, 2)]
    [InlineData(40, "n6", 33, 17)]
    [InlineData(40, "p6", 42, 26)]
    [InlineData(40, "h6", 0, -16)]
    [InlineData(25, "H7", 21, 0)]
    [InlineData(25, "g6", -7, -20)]
    [InlineData(10, "H7", 15, 0)]
    [InlineData(6, "g6", -4, -12)]
    [InlineData(6, "H8", 18, 0)]
    [InlineData(100, "H7", 35, 0)]
    [InlineData(100, "d9", -120, -207)]
    [InlineData(200, "H7", 46, 0)]
    public void ChartRows_ReadInTheStandardsOwnMicrometres(
        double size, string designation, double upper, double lower)
    {
        var (u, l) = Microns(size, designation);
        Assert.Equal(upper, u, 9);
        Assert.Equal(lower, l, 9);
    }

    [Fact]
    public void TheClassicSlidingFit_H7g6_AtForty()
    {
        var fit = Iso286.Fit(40, "H7", "g6");
        // The handbook numbers: clearance 9 to 50 µm, always positive.
        Assert.Equal(0.009, fit.MinClearance, 12);
        Assert.Equal(0.050, fit.MaxClearance, 12);
        Assert.Equal(FitKind.Clearance, fit.Kind);
        Assert.Equal("H7/g6", fit.Designation);
    }

    [Fact]
    public void TheKindIsDerivedFromTheExtremes_NeverLookedUp()
    {
        // k6: transition — can clear by 23 µm or bind by 18.
        var k = Iso286.Fit(40, "H7", "k6");
        Assert.Equal(FitKind.Transition, k.Kind);
        Assert.Equal(0.023, k.MaxClearance, 12);
        Assert.Equal(-0.018, k.MinClearance, 12);

        // p6: always binds — the press fit (max clearance −1 µm).
        var p = Iso286.Fit(40, "H7", "p6");
        Assert.Equal(FitKind.Interference, p.Kind);
        Assert.Equal(-0.001, p.MaxClearance, 12);
        Assert.Equal(-0.042, p.MinClearance, 12);

        // h6: the locational fit clears by exactly zero at its tightest.
        Assert.Equal(FitKind.Clearance, Iso286.Fit(40, "H7", "h6").Kind);
    }

    [Fact]
    public void Js_IsSymmetricAboutZero()
    {
        var js = Iso286.Limits(40, "js6");
        Assert.Equal(0.008, js.Upper, 12);
        Assert.Equal(-0.008, js.Lower, 12);
    }

    [Fact]
    public void K_OutsideGradesFourToSeven_HasZeroFundamentalDeviation()
    {
        // The standard's own rule: k's +0.6·∛D row applies to k4–k7 only; k8 and up
        // sit ON the nominal like h.
        var k8 = Iso286.Limits(40, "k8");
        Assert.Equal(0.0, k8.Lower, 12);
        Assert.Equal(0.039, k8.Upper, 12);
    }

    [Fact]
    public void RefusalsNameTheirReason()
    {
        // Sizes outside the table, letters outside the v1 set, shaft-basis holes,
        // and case confusion each refuse BY NAME rather than guessing.
        Assert.Throws<ArgumentOutOfRangeException>(() => Iso286.Limits(0.5, "H7"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Iso286.Limits(600, "H7"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Iso286.Limits(40, "H14"));
        var letter = Assert.Throws<ArgumentException>(() => Iso286.Limits(40, "s6"));
        Assert.Contains("sub-range", letter.Message);
        var holeBasis = Assert.Throws<ArgumentException>(() => Iso286.Limits(40, "F8"));
        Assert.Contains("shaft-basis", holeBasis.Message);
        var caseSwap = Assert.Throws<ArgumentException>(() => Iso286.Fit(40, "h7", "g6"));
        Assert.Contains("uppercase", caseSwap.Message);
    }
}

/// <summary>Worst-case + RSS stackups, checked by hand arithmetic (the chain is short
/// enough that the closed forms ARE the fixture).</summary>
public class ToleranceStackupTests
{
    [Fact]
    public void ATextbookGapChain_WorstCaseAndRss()
    {
        // A housing pocket 50 ±0.1 holds a bearing 20 +0/−0.05 and a spacer 10 ±0.02:
        // the gap left over is 50 − 20 − 10 = 20 nominal.
        var result = new ToleranceStackup()
            .Add("pocket", 50, 0.1, 0.1)
            .Subtract("bearing", 20, 0, 0.05)
            .Subtract("spacer", 10, 0.02, 0.02)
            .Evaluate();

        Assert.Equal(20.0, result.Nominal, 12);
        // Worst-case min: pocket smallest (49.9), bearing largest (20), spacer
        // largest (10.02) → 19.88. Max: 50.1 − 19.95 − 9.98 = 20.17.
        Assert.Equal(19.88, result.WorstCaseMin, 12);
        Assert.Equal(20.17, result.WorstCaseMax, 12);
        // RSS re-centres the bearing's asymmetric band on its mid (−19.975), so the
        // mean shifts +0.025 off the nominal; halves are 0.1, 0.025, 0.02.
        Assert.Equal(20.025, result.RssMean, 12);
        Assert.Equal(Math.Sqrt(0.1 * 0.1 + 0.025 * 0.025 + 0.02 * 0.02), result.RssHalfWidth, 12);
        // RSS is always inside worst case — the whole reason the method exists.
        Assert.True(result.RssMin > result.WorstCaseMin);
        Assert.True(result.RssMax < result.WorstCaseMax);
    }

    [Fact]
    public void AFitContributesItsClearanceBand()
    {
        var result = new ToleranceStackup()
            .Add("pin float", Iso286.Fit(40, "H7", "g6"))
            .Evaluate();
        Assert.Equal(0.0, result.Nominal, 12);
        Assert.Equal(0.009, result.WorstCaseMin, 12);
        Assert.Equal(0.050, result.WorstCaseMax, 12);
    }

    [Fact]
    public void RefusalsFireBeforeAnyArithmetic()
    {
        var stack = new ToleranceStackup();
        Assert.Throws<InvalidOperationException>(() => stack.Evaluate());
        Assert.Throws<ArgumentException>(() => stack.Add("", 1, 0.1, 0.1));
        var inverted = Assert.Throws<ArgumentOutOfRangeException>(
            () => stack.Add("bad", 1, -0.2, 0.1));
        Assert.Contains("inverted", inverted.Message);
    }
}
