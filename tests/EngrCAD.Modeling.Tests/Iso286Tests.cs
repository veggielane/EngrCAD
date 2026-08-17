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

    [Theory]
    // ISO 286-1 Table 4 (shafts a…c, upper deviation es) at their OWN sub-ranges — the
    // large-clearance letters, whose table splits where the main one does not (c changes
    // at 40 INSIDE the 30–50 step, which is exactly why the row could not be folded in).
    [InlineData(40, "c11", -120, -280)]   // H11/c11's shaft — the preferred loose fit
    [InlineData(45, "c11", -130, -290)]   // the same main range, the NEXT c cell
    [InlineData(10, "a11", -280, -370)]
    [InlineData(10, "b11", -150, -240)]
    [InlineData(10, "c11", -80, -170)]
    [InlineData(100, "c11", -170, -390)]
    // Table 5 (shafts r…z, lower deviation ei) — the interference letters.
    [InlineData(40, "r6", 50, 34)]        // H7/r6, the light press fit
    [InlineData(40, "s6", 59, 43)]        // H7/s6, the preferred medium drive fit
    [InlineData(40, "t6", 64, 48)]
    [InlineData(40, "u6", 76, 60)]        // H7/u6, the force fit
    [InlineData(40, "u7", 85, 60)]
    [InlineData(40, "v6", 84, 68)]
    [InlineData(40, "x6", 96, 80)]
    [InlineData(40, "y6", 110, 94)]
    [InlineData(40, "z6", 128, 112)]
    // 45 is the other half of the 30–50 main step, and WHICH letters split there is not
    // uniform: t…zc take a new cell at 40 while r and s carry one value across both.
    [InlineData(45, "u6", 86, 70)]
    [InlineData(45, "t6", 70, 54)]
    [InlineData(45, "z6", 152, 136)]
    [InlineData(45, "s6", 59, 43)]        // same as Ø40 — r and s do NOT split at 40
    [InlineData(45, "r6", 50, 34)]
    // Small and large ends of the same rows.
    [InlineData(8, "s6", 32, 23)]
    [InlineData(200, "s6", 151, 122)]
    [InlineData(20, "r6", 41, 28)]
    public void SplitLetterRows_ReadInTheStandardsOwnMicrometres(
        double size, string designation, double upper, double lower)
    {
        var (u, l) = Microns(size, designation);
        Assert.Equal(upper, u, 9);
        Assert.Equal(lower, l, 9);
    }

    [Theory]
    // The shaft-basis holes A–G, ISO 286-1 Table 2 — read here through the MIRROR RULE
    // (EI = −es of the same-letter shaft), so these rows check the rule as much as the
    // transcription: every one is the standard's own published hole deviation.
    [InlineData(40, "G7", 34, 9)]
    [InlineData(40, "F8", 64, 25)]
    [InlineData(40, "E9", 112, 50)]
    [InlineData(40, "D9", 142, 80)]
    [InlineData(40, "C11", 280, 120)]
    [InlineData(40, "B11", 330, 170)]
    [InlineData(40, "A11", 470, 310)]
    [InlineData(40, "H11", 160, 0)]
    [InlineData(10, "G7", 20, 5)]
    [InlineData(100, "F8", 90, 36)]
    public void ShaftBasisHoles_MirrorTheirOwnShaftLetter(
        double size, string designation, double upper, double lower)
    {
        var (u, l) = Microns(size, designation);
        Assert.Equal(upper, u, 9);
        Assert.Equal(lower, l, 9);
    }

    [Fact]
    public void TheTwoSystemsSpellTheSameFit_BecauseTheItWidthsCommute()
    {
        // The mirror rule's own consequence, and the reason it needs no correction term:
        // EI(G) = −es(g), so G7/h6's clearance extremes are H7/g6's EXACTLY — a shaft-basis
        // designer and a hole-basis one describe one joint. Asserted with ==, since both
        // sides are the same two IT widths added in the same order.
        var holeBasis = Iso286.Fit(40, "H7", "g6");
        var shaftBasis = Iso286.Fit(40, "G7", "h6");
        Assert.Equal(holeBasis.MinClearance, shaftBasis.MinClearance);
        Assert.Equal(holeBasis.MaxClearance, shaftBasis.MaxClearance);
        Assert.Equal(FitKind.Clearance, shaftBasis.Kind);
    }

    [Fact]
    public void ThePreferredFitsTheNewLettersUnlock()
    {
        // H11/c11 — the loose running fit, 120…440 µm of clearance at Ø40.
        var loose = Iso286.Fit(40, "H11", "c11");
        Assert.Equal(FitKind.Clearance, loose.Kind);
        Assert.Equal(0.120, loose.MinClearance, 12);
        Assert.Equal(0.440, loose.MaxClearance, 12);

        // H8/e8 — the running fit for a plain bearing.
        var running = Iso286.Fit(40, "H8", "e8");
        Assert.Equal(FitKind.Clearance, running.Kind);
        Assert.Equal(0.050, running.MinClearance, 12);
        Assert.Equal(0.128, running.MaxClearance, 12);

        // H7/s6 — the medium drive fit: ALWAYS interference, 18…59 µm.
        var drive = Iso286.Fit(40, "H7", "s6");
        Assert.Equal(FitKind.Interference, drive.Kind);
        Assert.Equal(-0.059, drive.MinClearance, 12);
        Assert.Equal(-0.018, drive.MaxClearance, 12);

        // H7/r6 — the light press fit, tighter band and less of it (9…50 µm).
        var press = Iso286.Fit(40, "H7", "r6");
        Assert.Equal(FitKind.Interference, press.Kind);
        Assert.Equal(-0.050, press.MinClearance, 12);
        Assert.Equal(-0.009, press.MaxClearance, 12);

        // H7/u6 — the force fit, heavier again. The three interference letters at one
        // size ORDER as the standard intends, which is what a swapped row would break.
        var force = Iso286.Fit(40, "H7", "u6");
        Assert.Equal(FitKind.Interference, force.Kind);
        Assert.True(force.MaxClearance < press.MaxClearance);
        Assert.True(drive.MaxClearance < press.MaxClearance);
    }

    [Fact]
    public void TheLettersWithNoSmallSizeCell_RefuseNamingTheSize()
    {
        // t, v and y are ABSENT from the standard's own table below 24, 14 and 18 mm.
        // An empty cell is a statement, so it is refused by name rather than interpolated.
        var t = Assert.Throws<ArgumentException>(() => Iso286.Limits(20, "t6"));
        Assert.Contains("only above 24", t.Message);
        var v = Assert.Throws<ArgumentException>(() => Iso286.Limits(12, "v6"));
        Assert.Contains("only above 14", v.Message);
        var y = Assert.Throws<ArgumentException>(() => Iso286.Limits(16, "y6"));
        Assert.Contains("only above 18", y.Message);

        // And each is fine one range up, so the refusal is the table's edge and not the
        // letter being missing.
        Assert.Equal(41, Iso286.Limits(28, "t6").Lower * 1000, 9);
        Assert.Equal(39, Iso286.Limits(16, "v6").Lower * 1000, 9);
        Assert.Equal(63, Iso286.Limits(20, "y6").Lower * 1000, 9);
    }

    [Fact]
    public void K_BelowThreeMillimetres_SitsOnTheNominal()
    {
        // ISO 286-1 Table 5's FIRST cell is 0 for k in every grade column — the +0.6·∛D
        // rule rounds away at that size — so k6 at Ø3 is the chart's +6/0, not +7/+1.
        var k6 = Iso286.Limits(3, "k6");
        Assert.Equal(0.0, k6.Lower, 12);
        Assert.Equal(0.006, k6.Upper, 12);
    }

    [Fact]
    public void EverySplitRowCoversTheWholeTable()
    {
        // A transcription that is one cell short reads plausibly at every size but the
        // last, so the coverage is asserted structurally: each split letter answers at
        // every sub-range boundary or refuses as an EMPTY CELL, never out of range.
        double[] probes =
        [
            2, 5, 8, 12, 16, 20, 28, 35, 45, 60, 70, 90, 110,
            130, 150, 170, 190, 210, 240, 270, 300, 340, 380, 420, 480,
        ];
        foreach (char letter in "abcrstuvxyz")
        {
            foreach (double size in probes)
            {
                try
                {
                    var limits = Iso286.Limits(size, $"{letter}6");
                    Assert.True(limits.Tolerance > 0);
                }
                catch (ArgumentException e) when (e.Message.Contains("only above"))
                {
                    // The standard's own empty cell — t, v, y at the small end.
                    Assert.True(letter is 't' or 'v' or 'y');
                    Assert.True(size < 30);
                }
            }
        }
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
        // Sizes outside the table, letters outside the transcribed set, the correction-
        // carrying holes, and case confusion each refuse BY NAME rather than guessing.
        Assert.Throws<ArgumentOutOfRangeException>(() => Iso286.Limits(0.5, "H7"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Iso286.Limits(600, "H7"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Iso286.Limits(40, "H14"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Iso286.Limits(0.5, "c11"));

        // K–ZC holes carry the delta = IT(n) − IT(n−1) correction with tabulated
        // exceptions; the refusal names it AND names the hole-basis spelling that works.
        var correction = Assert.Throws<ArgumentException>(() => Iso286.Limits(40, "S7"));
        Assert.Contains("delta", correction.Message);
        Assert.Contains("H7/s6", correction.Message);
        Assert.Contains("delta", Assert.Throws<ArgumentException>(
            () => Iso286.Limits(40, "K7")).Message);
        Assert.Contains("delta", Assert.Throws<ArgumentException>(
            () => Iso286.Limits(40, "J7")).Message);

        // The intermediate and extreme two-letter families are refused rather than
        // misread as their first letter — "cd9" is not a c fit, "za10" not a z one.
        foreach (var family in new[] { "cd9", "ef8", "fg7", "za10", "zb10", "zc10" })
        {
            var intermediate = Assert.Throws<ArgumentException>(
                () => Iso286.Limits(40, family));
            Assert.Contains("intermediate", intermediate.Message);
        }
        // ...while their single-letter neighbours still resolve.
        Assert.Equal(-130, Iso286.Limits(45, "c11").Upper * 1000, 9);
        Assert.Equal(-50, Iso286.Limits(40, "e8").Upper * 1000, 9);
        Assert.Equal(112, Iso286.Limits(40, "z6").Lower * 1000, 9);

        var j = Assert.Throws<ArgumentException>(() => Iso286.Limits(40, "j6"));
        Assert.Contains("per-grade special values", j.Message);
        var unknown = Assert.Throws<ArgumentException>(() => Iso286.Limits(40, "q6"));
        Assert.Contains("not in the table", unknown.Message);
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
