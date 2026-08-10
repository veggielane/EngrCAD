using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Stage-4 copper DRC. The bar is higher than usual because ECAD fails plausibly (the tamper-mesh
/// guarantee): a known violation is FOUND and a near miss at clearance + ε PASSES, both measured
/// against the CLOSED-FORM gap; a short NAMES BOTH NETS while same-net copper is never flagged (the
/// one-declaration identity); every threshold is RELATIVE (scale-invariant); the clearance is
/// PROVEN by an empty region intersection.
/// </summary>
public class PcbDrcTests
{
    // ---- hand-model fixtures (precise geometric control) ---------------------

    private static PcbBoard RectBoard(double w = 50, double h = 40) => PcbBoard.Rectangle(w, h, 1.6);

    private static CurvedRegion2d Disc(double cx, double cy, double r) =>
        CurvedRegion2d.Disc(new Vector2d(cx, cy), r);

    private static CurvedRegion2d Rect(double cx, double cy, double w, double h)
    {
        double hw = w / 2, hh = h / 2;
        Vector2d P(double x, double y) => new(cx + x, cy + y);
        return new CurvedRegion2d([
            CurvedEdge2d.Line(P(-hw, -hh), P(hw, -hh)),
            CurvedEdge2d.Line(P(hw, -hh), P(hw, hh)),
            CurvedEdge2d.Line(P(hw, hh), P(-hw, hh)),
            CurvedEdge2d.Line(P(-hw, hh), P(-hw, -hh)),
        ]);
    }

    private static CopperFeature F(string? net, string source, CurvedRegion2d region, string layer = "Top") =>
        new(layer, net, source, region);

    private static PcbCopperModel Model(PcbBoard board, params CopperFeature[] copper) =>
        new(board, copper);

    // ---- clearance: found, and a near miss passes (against the closed form) ---

    [Theory]
    [InlineData(1.14, true)]    // gap 0.14 < 0.15 → violation
    [InlineData(1.15, false)]   // gap exactly 0.15 → passes (touch at the clearance)
    [InlineData(1.16, false)]   // gap 0.16 > 0.15 → passes
    public void ClearanceIsFoundBelowTheLimitAndPassesAtOrAbove(double centerDistance, bool violation)
    {
        // Two Ø1 round pads (radius 0.5) of different nets. Closed-form gap = centerDistance − 1.0.
        var rules = DrcRuleSet.Default with { MinCopperClearance = 0.15 };
        var model = Model(RectBoard(),
            F("A", "P1", Disc(0, 0, 0.5)),
            F("B", "P2", Disc(centerDistance, 0, 0.5)));

        var report = PcbDrc.Check(model, rules);

        Assert.Equal(violation, report.Has(DrcRule.Clearance));
        if (violation)
        {
            var hit = Assert.Single(report.OfRule(DrcRule.Clearance));
            // Measured against the closed-form gap.
            Assert.Equal(centerDistance - 1.0, hit.Measured, 4);
            Assert.Equal(0.15, hit.Required, 12);
            Assert.Contains("'A'", hit.Message);
            Assert.Contains("'B'", hit.Message);
        }
    }

    [Fact]
    public void TheClearanceIsProvenByAnEmptyRegionIntersection()
    {
        // The tamper-mesh doctrine: grow each by half the clearance; an EMPTY intersection PROVES
        // the clearance and a NON-EMPTY one is the violation. Assert both directly.
        const double c = 0.15;
        var passA = Disc(0, 0, 0.5);
        var passB = Disc(1.16, 0, 0.5);        // gap 0.16 ≥ clearance
        var failA = Disc(0, 0, 0.5);
        var failB = Disc(1.14, 0, 0.5);        // gap 0.14 < clearance

        IReadOnlyList<CurvedRegion2d> Grow(CurvedRegion2d r) =>
            CurvedRegion2dOffset.Offset(r, c / 2);

        Assert.Empty(CurvedRegion2dBoolean.Intersection(Grow(passA), Grow(passB)));
        Assert.NotEmpty(CurvedRegion2dBoolean.Intersection(Grow(failA), Grow(failB)));

        // And the DRC agrees.
        Assert.False(PcbDrc.Check(Model(RectBoard(), F("A", "P1", passA), F("B", "P2", passB))).Has(DrcRule.Clearance));
        Assert.True(PcbDrc.Check(Model(RectBoard(), F("A", "P1", failA), F("B", "P2", failB))).Has(DrcRule.Clearance));
    }

    // ---- shorts, and same-net not flagged (the one-declaration identity) ------

    [Fact]
    public void AShortNamesBothNetsAndIsNotAMereClearanceMiss()
    {
        // Two overlapping pads of DIFFERENT nets is a SHORT (a stronger failure than a near miss).
        var model = Model(RectBoard(),
            F("VCC", "R1.1", Disc(0, 0, 0.5)),
            F("GND", "R2.1", Disc(0.5, 0, 0.5)));   // centres 0.5 apart, radii 0.5 → overlap

        var report = PcbDrc.Check(model);

        var shorts = report.OfRule(DrcRule.Short).ToList();
        var hit = Assert.Single(shorts);
        Assert.Contains("'VCC'", hit.Message);
        Assert.Contains("'GND'", hit.Message);
        Assert.Contains("R1.1", hit.Message);
        Assert.Contains("R2.1", hit.Message);
        // A short is reported AS a short, not as a clearance violation.
        Assert.False(report.Has(DrcRule.Clearance));
    }

    [Fact]
    public void SameNetCopperTouchingIsTheIntendedConnectionAndIsNeverFlagged()
    {
        // Two overlapping pads of the SAME net is the intended connection — no short, no clearance.
        var model = Model(RectBoard(),
            F("VCC", "R1.1", Disc(0, 0, 0.5)),
            F("VCC", "R2.1", Disc(0.5, 0, 0.5)));

        var report = PcbDrc.Check(model);

        Assert.False(report.Has(DrcRule.Short));
        Assert.False(report.Has(DrcRule.Clearance));
    }

    [Fact]
    public void TwoUnconnectedPadsAreDifferentNetsAndMustClear()
    {
        // Two floating (unconnected) pads are electrically distinct — they must clear each other.
        var overlap = Model(RectBoard(),
            F(net: null, "R1.1", Disc(0, 0, 0.5)),
            F(net: null, "R2.1", Disc(0.5, 0, 0.5)));
        Assert.True(PcbDrc.Check(overlap).Has(DrcRule.Short));
    }

    // ---- annular ring: exactly at min passes, below min fails -----------------

    [Theory]
    [InlineData(1.30, false)]   // ring (1.6−1.30)/2 = 0.15 = min → passes
    [InlineData(1.32, true)]    // ring (1.6−1.32)/2 = 0.14 < min → fails
    public void AnnularRingPassesAtTheMinimumAndFailsBelowIt(double drill, bool violation)
    {
        var rules = DrcRuleSet.Default with { MinAnnularRing = 0.15 };
        var model = new PcbCopperModel(RectBoard(), [],
            [new DrilledHole(new Vector2d(0, 0), drill, PadDiameter: 1.6, Net: "A", Source: "J1.1")]);

        var report = PcbDrc.Check(model, rules);

        Assert.Equal(violation, report.Has(DrcRule.AnnularRing));
        if (violation)
            Assert.Equal((1.6 - drill) / 2, Assert.Single(report.OfRule(DrcRule.AnnularRing)).Measured, 12);
    }

    // ---- copper-to-edge: both sides against the outline -----------------------

    [Theory]
    [InlineData(24.26, true)]    // right edge at 24.76, gap 0.24 < 0.25 → fail
    [InlineData(24.24, false)]   // right edge at 24.74, gap 0.26 > 0.25 → pass
    public void CopperToEdgeIsFoundNearTheOutlineAndPassesClearOfIt(double cx, bool violation)
    {
        // Board right wall at x = 25. Ø1 pad (radius 0.5). Closed-form edge gap = 25 − (cx + 0.5).
        var rules = DrcRuleSet.Default with { MinCopperToEdge = 0.25 };
        var model = Model(RectBoard(50, 40), F("A", "P1", Disc(cx, 0, 0.5)));

        var report = PcbDrc.Check(model, rules);

        Assert.Equal(violation, report.Has(DrcRule.CopperToEdge));
        if (violation)
            Assert.Equal(25 - (cx + 0.5), Assert.Single(report.OfRule(DrcRule.CopperToEdge)).Measured, 3);
    }

    // ---- drill-to-copper (cross-layer), both sides ----------------------------

    [Theory]
    [InlineData(1.19, true)]    // gap 0.19 < 0.2 → fail
    [InlineData(1.21, false)]   // gap 0.21 > 0.2 → pass
    public void DrillToCopperIsFoundNearOtherNetCopperAndPassesClearOfIt(double centerDistance, bool violation)
    {
        // A Ø1 via (own net) and a Ø1 pad of net A. Closed-form gap = centerDistance − 1.0.
        var rules = DrcRuleSet.Default with { MinDrillToCopper = 0.2 };
        var model = new PcbCopperModel(RectBoard(),
            [F("A", "P1", Disc(centerDistance, 0, 0.5))],
            [new DrilledHole(new Vector2d(0, 0), 1.0, PadDiameter: 0, Net: null, Source: "via")]);

        var report = PcbDrc.Check(model, rules);

        Assert.Equal(violation, report.Has(DrcRule.DrillToCopper));
        if (violation)
            Assert.Equal(centerDistance - 1.0, Assert.Single(report.OfRule(DrcRule.DrillToCopper)).Measured, 3);
    }

    [Fact]
    public void ADrillNeverClearsItsOwnPad()
    {
        // A through-hole pad's own copper surrounds its own drill — that is the annular ring, not a
        // drill-to-copper violation, even when the pad is on no net (both drill and pad null-net).
        var model = new PcbCopperModel(RectBoard(),
            [F(net: null, "J1.1", Disc(0, 0, 0.8))],
            [new DrilledHole(new Vector2d(0, 0), 0.9, PadDiameter: 1.6, Net: null, Source: "J1.1")]);
        Assert.False(PcbDrc.Check(model).Has(DrcRule.DrillToCopper));
    }

    // ---- trace width (on a hand-built trace and a thin pad) -------------------

    [Theory]
    [InlineData(0.1, true)]    // 0.1 wide < 0.15 → fail
    [InlineData(0.2, false)]   // 0.2 wide > 0.15 → pass
    public void TraceWidthFlagsCopperNarrowerThanTheMinimum(double width, bool violation)
    {
        var rules = DrcRuleSet.Default with { MinTraceWidth = 0.15 };
        var model = Model(RectBoard(), F("A", "T1", Rect(0, 0, 5, width)));

        var report = PcbDrc.Check(model, rules);

        Assert.Equal(violation, report.Has(DrcRule.TraceWidth));
        if (violation)
            Assert.Equal(width, Assert.Single(report.OfRule(DrcRule.TraceWidth)).Measured, 3);
    }

    [Fact]
    public void OrdinaryPadsAreNotFlaggedForTraceWidthOrAcuteAngles()
    {
        // A round pad and a rectangular pad — no narrow neck, no acute corner.
        var model = Model(RectBoard(),
            F("A", "R1.1", Disc(0, 0, 0.8)),
            F("B", "R2.1", Rect(10, 0, 1.2, 1.4)));
        var report = PcbDrc.Check(model);
        Assert.False(report.Has(DrcRule.TraceWidth));
        Assert.False(report.Has(DrcRule.AcuteAngle));
    }

    // ---- acute-angle / acid trap ---------------------------------------------

    [Fact]
    public void AnAcuteCopperCornerIsFlaggedAndASquareCornerIsNot()
    {
        // A thin triangle with a ~26.6° apex is an acid-trap risk; a rectangle's 90° corners pass.
        var triangle = new CurvedRegion2d([
            CurvedEdge2d.Line(new Vector2d(0, 0), new Vector2d(10, 1)),
            CurvedEdge2d.Line(new Vector2d(10, 1), new Vector2d(10, -1)),
            CurvedEdge2d.Line(new Vector2d(10, -1), new Vector2d(0, 0)),
        ]);
        var acute = PcbDrc.Check(Model(RectBoard(60, 40), F("A", "T1", triangle)));
        Assert.True(acute.Has(DrcRule.AcuteAngle));

        var square = PcbDrc.Check(Model(RectBoard(), F("A", "P1", Rect(0, 0, 2, 2))));
        Assert.False(square.Has(DrcRule.AcuteAngle));
    }

    // ---- scale invariance (relative tolerances) -------------------------------

    [Fact]
    public void ARuleSetAndBoardThatPassStillPassAfterAUniformScale()
    {
        PcbCopperModel Build(double s) => Model(RectBoard(50 * s, 40 * s),
            F("A", "P1", Disc(0, 0, 0.5 * s)),
            F("B", "P2", Disc(1.14 * s, 0, 0.5 * s)));   // gap 0.14·s — a violation at every scale

        var small = PcbDrc.Check(Build(1e-3), DrcRuleSet.Default.Scaled(1e-3));
        var unit = PcbDrc.Check(Build(1), DrcRuleSet.Default);
        var large = PcbDrc.Check(Build(1e3), DrcRuleSet.Default.Scaled(1e3));

        Assert.True(unit.Has(DrcRule.Clearance));
        Assert.Equal(unit.Violations.Count, small.Violations.Count);
        Assert.Equal(unit.Violations.Count, large.Violations.Count);
        // The measured gap scales with the board.
        Assert.Equal(0.14e3, large.OfRule(DrcRule.Clearance).Single().Measured, 1);
    }

    // ---- determinism, and a clean board ---------------------------------------

    [Fact]
    public void OneInputGivesOneReportDeterministically()
    {
        var model = Model(RectBoard(),
            F("A", "P1", Disc(0, 0, 0.5)),
            F("B", "P2", Disc(1.1, 0, 0.5)),
            F("C", "P3", Disc(0, 0.55, 0.5)));

        var first = PcbDrc.Check(model);
        var second = PcbDrc.Check(model);

        Assert.Equal(first.Messages.ToList(), second.Messages.ToList());
        Assert.Equal(first.Ratsnest, second.Ratsnest);
    }

    [Fact]
    public void ACleanBoardReportsZeroViolations()
    {
        var model = Model(RectBoard(),
            F("A", "P1", Disc(-10, 0, 0.5)),
            F("B", "P2", Disc(10, 0, 0.5)));
        var report = PcbDrc.Check(model);
        Assert.True(report.Ok);
        Assert.Empty(report.Violations);
    }

    // ---- integration over a placed layout, and the ratsnest -------------------

    [Fact]
    public void ThePlacedFixtureLayoutIsDrcCleanWithAnUnroutedRatsnest()
    {
        // The stage-2 fixture: two parts wired into two 2-pin nets (VCC, SIG), placed apart. Its
        // copper is clean, and — with only pads, no traces — both signal nets are UNROUTED, which
        // is INFORMATION (a ratsnest), not a violation: a bare-pads board is not "wrong".
        var report = PcbDrc.Check(PcbFixtures.Layout());

        Assert.True(report.Ok);
        Assert.Empty(report.Violations);
        Assert.Equal(["SIG", "VCC"], report.Ratsnest);
    }

    [Fact]
    public void RoutingTwoPadsOfANetTogetherClearsItFromTheRatsnest()
    {
        // A net whose copper is ONE connected region (a stage-5 trace joining its pads) is routed;
        // a net whose copper is two islands is not. The ratsnest is the inverse of a short.
        var routed = Model(RectBoard(),
            F("N", "P1", Rect(0, 0, 6, 0.3)),           // one pad
            F("N", "P2", Rect(2.9, 0, 0.3, 0.3)));      // overlapping copper → one island
        Assert.Empty(PcbDrc.Check(routed).Ratsnest);

        var unrouted = Model(RectBoard(),
            F("N", "P1", Disc(-5, 0, 0.5)),
            F("N", "P2", Disc(5, 0, 0.5)));             // two islands
        Assert.Equal(["N"], PcbDrc.Check(unrouted).Ratsnest);
    }

    // ---- the incremental router seam ------------------------------------------

    [Fact]
    public void TheIncrementalEntryCostsACandidateAgainstTheModel()
    {
        // The stage-5 router seam: does adding THIS copper violate anything?
        var model = Model(RectBoard(), F("A", "P1", Disc(0, 0, 0.5)));

        var clashing = new CopperFeature("Top", "B", "trace", Disc(1.1, 0, 0.5));   // gap 0.1 < 0.15
        Assert.True(PcbDrc.Violates(model, clashing).Has(DrcRule.Clearance));

        var clear = new CopperFeature("Top", "B", "trace", Disc(2.0, 0, 0.5));      // gap 1.0
        var clearReport = PcbDrc.Violates(model, clear);
        Assert.True(clearReport.Ok, string.Join("; ", clearReport.Messages));

        // Same-net copper the candidate overlaps is the intended connection, never a short.
        var sameNet = new CopperFeature("Top", "A", "trace", Disc(0.5, 0, 0.5));
        Assert.False(PcbDrc.Violates(model, sameNet).Has(DrcRule.Short));
    }
}
