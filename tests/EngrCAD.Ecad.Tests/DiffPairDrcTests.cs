using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The diff-pair-aware copper DRC: the two nets of a differential pair EXPLICITLY named to the DRC are
/// checked at the tighter intra-pair floor (<see cref="DrcRuleSet.MinDiffPairGap"/>) rather than the
/// general clearance, so a controlled-impedance pair may run tight — while each half still clears
/// everything ELSE at the general clearance, a SHORT within the pair is still a short, and a gap below
/// even the diff-pair floor still flags. The mutation that proves the exemption earns its place: the
/// SAME tight pair flags with no pairs named and passes when named, and the exemption reaches nothing
/// but the pair.
/// </summary>
public sealed class DiffPairDrcTests
{
    // General clearance 0.3; the tighter diff-pair floor 0.15. Trace width 0.4.
    private static readonly DrcRuleSet Rules = new(
        MinCopperClearance: 0.3, MinTraceWidth: 0.2, MinAnnularRing: 0.2,
        MinDrillToCopper: 0.3, MinCopperToEdge: 0.3, MinAcuteAngleDegrees: 80)
    { MinDiffPairGap = 0.15 };

    private const double W = 0.4;

    private static readonly DiffPair Pair = new("D_P", "D_N", TargetGapMm: 0.6);

    private static PcbBoard Board() => new([new(0, 0), new(30, 0), new(30, 20), new(0, 20)], 1.6);

    // A layout with the pair's two traces straddling y = 10 at the given centre-to-centre gap. The
    // edge-to-edge copper gap is (centreGap − W): at 0.6 it is 0.2 (below the general 0.3, above the
    // diff-pair 0.15), at 0.5 it is 0.1 (below both).
    private static PcbLayout Layout(double centreGap, bool p = true, bool n = true, double width = W)
    {
        var layout = new PcbLayout(new Schematic("dp"), Board());
        string layer = layout.Board.Stackup.Coppers[0].Name;
        double h = centreGap / 2;
        if (p) layout.AddTrace("D_P", layer, width, [new Vector2d(4, 10 + h), new Vector2d(26, 10 + h)]);
        if (n) layout.AddTrace("D_N", layer, width, [new Vector2d(4, 10 - h), new Vector2d(26, 10 - h)]);
        return layout;
    }

    private static string Layer(PcbLayout l) => l.Board.Stackup.Coppers[0].Name;

    // ==== 1) the core: the same tight pair flags un-named and passes when named ==========

    [Fact]
    public void ATightIntraPairGap_FlagsUnnamedButPassesWhenNamed()
    {
        var layout = Layout(0.6);   // edge gap 0.2: under general 0.3, over diff-pair 0.15

        // Un-named, the intra-pair gap is a plain clearance violation.
        var plain = PcbDrc.Check(layout, Rules);
        Assert.False(plain.Ok);
        Assert.NotEmpty(plain.OfRule(DrcRule.Clearance));

        // Named as a differential pair, the SAME geometry is clean.
        var aware = PcbDrc.Check(layout, Rules, [Pair]);
        Assert.True(aware.Ok, string.Join("; ", aware.Violations.Select(x => x.ToString())));
    }

    // ==== 2) the exemption reaches the pair ONLY — a third net still flags ================

    [Fact]
    public void TheExemptionIsIntraPairOnly_AThirdNetStillFlagsAtTheGeneralClearance()
    {
        var layout = Layout(0.6);
        // D_P's top edge is at 10.3 + 0.2 = 10.5; put an unrelated net 0.2 above it (edge gap 0.2 < 0.3).
        layout.AddTrace("OTHER", Layer(layout), W, [new Vector2d(4, 10.9), new Vector2d(26, 10.9)]);

        var aware = PcbDrc.Check(layout, Rules, [Pair]);
        Assert.False(aware.Ok);   // the exemption did not make D_P a "special" net

        // The surviving clearance violation is D_P-vs-OTHER, not the exempt D_P-vs-D_N pair.
        var clearances = aware.OfRule(DrcRule.Clearance).ToList();
        Assert.NotEmpty(clearances);
        Assert.All(clearances, cv => Assert.Contains("OTHER", cv.Message));
        Assert.DoesNotContain(clearances, cv => cv.Message.Contains("'D_N'") && cv.Message.Contains("'D_P'"));
    }

    // ==== 3) a short within a named pair is still a short =================================

    [Fact]
    public void AShortWithinANamedPair_IsStillAShort()
    {
        var layout = Layout(0.0);   // both traces on y = 10 — fully overlapping, different nets

        var aware = PcbDrc.Check(layout, Rules, [Pair]);
        Assert.False(aware.Ok);
        Assert.NotEmpty(aware.OfRule(DrcRule.Short));   // the exemption relaxes clearance, never a short
    }

    // ==== 4) below the diff-pair floor, a named pair still flags ==========================

    [Fact]
    public void AGapBelowTheDiffPairFloor_StillFlagsWhenNamed()
    {
        var layout = Layout(0.5);   // edge gap 0.1: under the diff-pair floor 0.15

        var aware = PcbDrc.Check(layout, Rules, [Pair]);
        Assert.False(aware.Ok);
        var cv = Assert.Single(aware.OfRule(DrcRule.Clearance));
        Assert.Equal(0.15, cv.Required, 6);        // measured against the tighter floor, not 0.3
        Assert.True(cv.Measured < 0.15);
    }

    // ==== 5) no pairs named is bit-identical to a stage-4 run ============================

    [Fact]
    public void NullEmptyAndAnUnrelatedPair_AreAllTheSameAsAStage4Run()
    {
        var layout = Layout(0.6);
        var plain = PcbDrc.Check(layout, Rules);
        var empty = PcbDrc.Check(layout, Rules, []);
        var unrelated = PcbDrc.Check(layout, Rules, [new DiffPair("X", "Y", 0.5)]);

        // A null, an empty, and a pair naming nets this board does not carry all leave the DRC exactly
        // as it was: the tight pair flags in every one of them.
        Assert.Equal(plain.Violations.Count, empty.Violations.Count);
        Assert.Equal(plain.Violations.Count, unrelated.Violations.Count);
        Assert.False(plain.Ok);
        Assert.False(empty.Ok);
        Assert.False(unrelated.Ok);
    }

    // ==== 6) the incremental Violates seam is diff-pair-aware ============================

    [Fact]
    public void TheIncrementalViolates_IsDiffPairAware()
    {
        // Base copper is the + net alone; the candidate is the − net's copper at the tight gap.
        var baseModel = PcbCopperModel.FromLayout(Layout(0.6, p: true, n: false));
        var nFeatures = PcbCopperModel.FromLayout(Layout(0.6, p: false, n: true))
            .Copper.Where(f => f.Net == "D_N").ToList();
        Assert.NotEmpty(nFeatures);

        // Un-named, adding the − copper violates clearance against the + copper.
        Assert.Contains(nFeatures, c => PcbDrc.Violates(baseModel, c, Rules).Violations.Count > 0);
        // Named, the same candidate is clean (the router can route a tight pair through this seam).
        Assert.All(nFeatures, c => Assert.Empty(PcbDrc.Violates(baseModel, c, Rules, [Pair]).Violations));
    }

    // ==== 7) CoupledRouter routes a tight pair ===========================================

    [Fact]
    public void CoupledRouter_RoutesATightPairThatTheGeneralClearanceWouldRefuse()
    {
        var layout = new PcbLayout(new Schematic("cr"), Board());
        string layer = Layer(layout);
        // gap 0.6, width 0.4 → intra-pair edge gap 0.2: under general 0.3, over the diff-pair floor.
        var centre = new[] { new Vector2d(4, 10), new Vector2d(26, 10) };

        var result = CoupledRouter.Route(layout, Pair, centre, layer, W, Rules);

        Assert.Equal(CoupledOutcome.Routed, result.Outcome);
        layout.AddTrace(result.Positive);
        layout.AddTrace(result.Negative);
        // The routed board is clean under the diff-pair-aware DRC and unrouted-free of the pair.
        Assert.True(PcbDrc.Check(layout, Rules, [Pair]).Ok);
        Assert.True(DiffPairs.Check(layout, Pair).WellCoupled);
    }

    // ==== 8) the rule set declares a diff-pair floor ====================================

    [Fact]
    public void TheRuleSetDeclaresADiffPairFloor_GrowingWithTheIpcClass()
    {
        Assert.Equal(0.1, DrcRuleSet.Default.MinDiffPairGap, 9);
        Assert.Equal(0.075, DrcRuleSet.ForIpcClass(1).MinDiffPairGap, 9);
        Assert.Equal(0.10, DrcRuleSet.ForIpcClass(2).MinDiffPairGap, 9);   // == Default
        Assert.Equal(0.125, DrcRuleSet.ForIpcClass(3).MinDiffPairGap, 9);
        // A length, so it scales with the board.
        Assert.Equal(100, DrcRuleSet.Default.Scaled(1000).MinDiffPairGap, 6);
    }
}
