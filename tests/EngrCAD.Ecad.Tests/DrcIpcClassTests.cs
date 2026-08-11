using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The IPC-6012 producibility-class preset (<see cref="DrcRuleSet.ForIpcClass"/>) and the
/// spec-vs-class cross-check (<see cref="DrcRuleSet.CheckSpec"/>). Verified the ECAD house way:
/// the transcribed class minimums are asserted in the datasheet form a human checks; the
/// monotonicity a transcription typo would break is asserted per rule (class 3 strictly stricter
/// than class 2 than class 1); the preset actually DRIVES <see cref="PcbDrc"/> (a board passes
/// class 2 and the SAME board fails the stricter class 3); the cross-check FLAGS a spec whose
/// stated minimum is looser than the class it claims (naming the value and the class minimum) and
/// reports "not checkable" rather than inventing a verdict; and <see cref="DrcRuleSet.Default"/> is
/// field-identical to before (nothing existing moved).
/// </summary>
public sealed class DrcIpcClassTests
{
    // ==== 1. preset values — the transcribed datasheet numbers per class ====================

    // The ⚠ nominal IPC-6012 figures, asserted in the form a human checks (the datasheet number),
    // per the repo's transcription-test rule. A re-typed value agrees with its own mistake, so
    // these numbers are the transcription itself.
    [Fact]
    public void ForIpcClass_TranscribesTheNominalMinimumsPerClass()
    {
        var c1 = DrcRuleSet.ForIpcClass(1);
        Assert.Equal(0.10, c1.MinCopperClearance);
        Assert.Equal(0.10, c1.MinTraceWidth);
        Assert.Equal(0.10, c1.MinAnnularRing);
        Assert.Equal(0.15, c1.MinDrillToCopper);
        Assert.Equal(0.20, c1.MinCopperToEdge);
        Assert.Equal(0.15, c1.MinViaToVia);
        Assert.Equal(90, c1.MinAcuteAngleDegrees);

        var c2 = DrcRuleSet.ForIpcClass(2);
        Assert.Equal(0.15, c2.MinCopperClearance);
        Assert.Equal(0.15, c2.MinTraceWidth);
        Assert.Equal(0.15, c2.MinAnnularRing);
        Assert.Equal(0.20, c2.MinDrillToCopper);
        Assert.Equal(0.25, c2.MinCopperToEdge);
        Assert.Equal(0.20, c2.MinViaToVia);
        Assert.Equal(90, c2.MinAcuteAngleDegrees);

        var c3 = DrcRuleSet.ForIpcClass(3);
        Assert.Equal(0.20, c3.MinCopperClearance);
        Assert.Equal(0.20, c3.MinTraceWidth);
        Assert.Equal(0.20, c3.MinAnnularRing);
        Assert.Equal(0.25, c3.MinDrillToCopper);
        Assert.Equal(0.30, c3.MinCopperToEdge);
        Assert.Equal(0.25, c3.MinViaToVia);
        Assert.Equal(90, c3.MinAcuteAngleDegrees);
    }

    // Class 2 IS the Class-2-ish Default, so the preset spreads around a rule set that already
    // shipped — a clean equality that a value drift on either side would break.
    [Fact]
    public void ForIpcClass2_IsFieldIdenticalToDefault()
    {
        Assert.Equal(DrcRuleSet.Default, DrcRuleSet.ForIpcClass(2));
    }

    // The monotonicity a transcription typo breaks: EVERY length minimum grows strictly with the
    // class (class 3 is the strictest floor), and the acid-trap angle is the same at every class.
    [Fact]
    public void EveryLengthRule_GrowsStrictlyWithTheClass_AndTheAngleIsConstant()
    {
        var (a, b, c) = (DrcRuleSet.ForIpcClass(1), DrcRuleSet.ForIpcClass(2), DrcRuleSet.ForIpcClass(3));

        void Increasing(Func<DrcRuleSet, double> f)
        {
            Assert.True(f(a) < f(b), $"class 1 ({f(a)}) must be < class 2 ({f(b)})");
            Assert.True(f(b) < f(c), $"class 2 ({f(b)}) must be < class 3 ({f(c)})");
        }

        Increasing(r => r.MinCopperClearance);
        Increasing(r => r.MinTraceWidth);
        Increasing(r => r.MinAnnularRing);
        Increasing(r => r.MinDrillToCopper);
        Increasing(r => r.MinCopperToEdge);
        Increasing(r => r.MinViaToVia);

        // The acid-trap threshold is a dimensionless angle, not a producibility floor — constant.
        Assert.Equal(a.MinAcuteAngleDegrees, b.MinAcuteAngleDegrees);
        Assert.Equal(b.MinAcuteAngleDegrees, c.MinAcuteAngleDegrees);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void ForIpcClass_RefusesAClassOutsideOneToThree_ByName(int cls)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => DrcRuleSet.ForIpcClass(cls));
        Assert.Contains("1, 2 or 3", ex.Message);
    }

    // The preset is a plain rule set, so it scales like any other (the relative-tolerance property).
    [Fact]
    public void APresetScalesLikeAnyOtherRuleSet()
    {
        var scaled = DrcRuleSet.ForIpcClass(3).Scaled(1000);
        Assert.Equal(200, scaled.MinCopperClearance, 9);   // 0.20 * 1000
        Assert.Equal(250, scaled.MinViaToVia, 9);          // 0.25 * 1000
        Assert.Equal(90, scaled.MinAcuteAngleDegrees);     // angle untouched
    }

    // ==== 2. the preset drives the existing PcbDrc =========================================

    // The point of the preset: it is a real DrcRuleSet, so it DRIVES PcbDrc with no PcbDrc change.
    // One board, two classes: a 0.18 mm gap PASSES class 2 (floor 0.15) and FAILS the stricter
    // class 3 (floor 0.20). The DRC outcome moving with the class is the whole demonstration.
    [Fact]
    public void ThePresetDrivesPcbDrc_PassingClass2AndFailingTheStricterClass3()
    {
        var board = PcbBoard.Rectangle(50, 40, 1.6);
        CopperFeature Pad(string net, double x) =>
            new("Top", net, net + ".1", CurvedRegion2d.Disc(new Vector2d(x, 0), 0.5));

        // Two Ø1 pads of different nets, centres 1.18 apart => an edge-to-edge gap of 0.18 mm.
        var model = new PcbCopperModel(board, new[] { Pad("A", 0), Pad("B", 1.18) });

        var class2 = PcbDrc.Check(model, DrcRuleSet.ForIpcClass(2));
        Assert.False(class2.Has(DrcRule.Clearance));   // 0.18 >= 0.15 => clears class 2
        Assert.True(class2.Ok);                        // and nothing else flags it

        var class3 = PcbDrc.Check(model, DrcRuleSet.ForIpcClass(3));
        Assert.True(class3.Has(DrcRule.Clearance));    // 0.18 < 0.20 => fails class 3
        var hit = Assert.Single(class3.OfRule(DrcRule.Clearance));
        Assert.Equal(0.18, hit.Measured, 4);           // measured gap = closed-form 1.18 - 1.0
        Assert.Equal(0.20, hit.Required, 9);           // required = class 3's floor
    }

    // ==== 3. the spec-vs-class cross-check =================================================

    // The flag with teeth: a spec claiming a strict class but stating a looser minimum is named,
    // with BOTH the stated value and the class minimum in the message.
    [Fact]
    public void CheckSpec_FlagsAStatedMinimumLooserThanTheClaimedClass_NamingValueAndClassMinimum()
    {
        // Claims class 3 (floors 0.20 / 0.20) but states class-2-loose minimums (0.15).
        var spec = new PcbFabricationSpec
        {
            Ipc6012Class = 3,
            MinTraceWidthMm = 0.15,
            MinClearanceMm = 0.15,
        };

        var check = DrcRuleSet.CheckSpec(spec);

        Assert.Equal(IpcClassCheckResult.NonConforming, check.Result);
        Assert.False(check.Conforms);
        Assert.True(check.IsCheckable);
        Assert.Equal(3, check.ClaimedClass);

        // Both offenders named, each carrying the stated value AND the class floor.
        Assert.Equal(2, check.Issues.Count);
        var joined = string.Join(" | ", check.Issues);
        Assert.Contains("minimum trace width 0.15 mm", joined);
        Assert.Contains("minimum clearance 0.15 mm", joined);
        Assert.Contains("class 3's minimum of 0.2 mm", joined);   // the class floor is stated
        Assert.Contains("looser", joined);
        // The summary carries the class and the offenders.
        Assert.Contains("class 3", check.Summary);
    }

    // A stated minimum FINER (larger) than the class it claims exceeds the floor and conforms —
    // over-committing is fine.
    [Fact]
    public void CheckSpec_ConformsWhenTheStatedMinimumsMeetTheClaimedClass()
    {
        // Claims class 2 (floor 0.15) and states exactly the floor / above it.
        var meets = new PcbFabricationSpec { Ipc6012Class = 2, MinTraceWidthMm = 0.15, MinClearanceMm = 0.20 };
        var m = DrcRuleSet.CheckSpec(meets);
        Assert.Equal(IpcClassCheckResult.Conforming, m.Result);
        Assert.True(m.Conforms);
        Assert.Empty(m.Issues);
        Assert.Equal(2, m.ClaimedClass);

        // Claims class 1 (floor 0.10) but states class-3-tight minimums — stricter than needed,
        // so it conforms (and then some).
        var overCommits = new PcbFabricationSpec { Ipc6012Class = 1, MinTraceWidthMm = 0.20, MinClearanceMm = 0.20 };
        Assert.True(DrcRuleSet.CheckSpec(overCommits).Conforms);
    }

    // Only one of the two minimums stated, and it fails: exactly one issue, and the other minimum
    // is not invented into a pass or a fail.
    [Fact]
    public void CheckSpec_ChecksOnlyTheMinimumsThatAreStated()
    {
        var spec = new PcbFabricationSpec { Ipc6012Class = 3, MinClearanceMm = 0.12 };   // no min trace
        var check = DrcRuleSet.CheckSpec(spec);
        Assert.Equal(IpcClassCheckResult.NonConforming, check.Result);
        var issue = Assert.Single(check.Issues);
        Assert.Contains("minimum clearance 0.12 mm", issue);
        Assert.DoesNotContain("trace width", issue);   // the unstated minimum is not invented
    }

    // "Not checkable, don't invent": no class stated -> nothing to check, with a reason.
    [Fact]
    public void CheckSpec_IsNotCheckableWithNoClass()
    {
        var spec = new PcbFabricationSpec { MinTraceWidthMm = 0.05, MinClearanceMm = 0.05 };
        var check = DrcRuleSet.CheckSpec(spec);
        Assert.Equal(IpcClassCheckResult.NotCheckable, check.Result);
        Assert.False(check.IsCheckable);
        Assert.False(check.Conforms);
        Assert.Null(check.ClaimedClass);
        Assert.Empty(check.Issues);
        Assert.Contains("no IPC-6012 class", check.Summary);
    }

    // A class but no minimum trace or clearance -> nothing to compare against, with a reason.
    [Fact]
    public void CheckSpec_IsNotCheckableWithAClassButNoStatedMinimum()
    {
        var spec = new PcbFabricationSpec { Ipc6012Class = 3, BaseMaterial = "FR-4" };
        var check = DrcRuleSet.CheckSpec(spec);
        Assert.Equal(IpcClassCheckResult.NotCheckable, check.Result);
        Assert.Equal(3, check.ClaimedClass);            // the class it claimed is still reported
        Assert.Empty(check.Issues);
        Assert.Contains("no minimum trace width or clearance", check.Summary);
    }

    // The empty spec has nothing to check either (write-only-when-stated, all the way down).
    [Fact]
    public void CheckSpec_IsNotCheckableForTheDefaultSpec()
    {
        Assert.Equal(IpcClassCheckResult.NotCheckable, DrcRuleSet.CheckSpec(PcbFabricationSpec.Default).Result);
    }

    // ==== 4. Default unchanged + determinism ===============================================

    // DrcRuleSet.Default is field-identical to what it always was — the preset is purely additive.
    [Fact]
    public void Default_IsUnchanged()
    {
        var d = DrcRuleSet.Default;
        Assert.Equal(0.15, d.MinCopperClearance);
        Assert.Equal(0.15, d.MinTraceWidth);
        Assert.Equal(0.15, d.MinAnnularRing);
        Assert.Equal(0.2, d.MinDrillToCopper);
        Assert.Equal(0.25, d.MinCopperToEdge);
        Assert.Equal(90, d.MinAcuteAngleDegrees);
        Assert.Equal(0.2, d.MinViaToVia);
    }

    // Two calls give equal values (records) — the preset and the check are pure functions.
    [Fact]
    public void ThePresetAndCheckAreDeterministic()
    {
        Assert.Equal(DrcRuleSet.ForIpcClass(3), DrcRuleSet.ForIpcClass(3));
        var spec = new PcbFabricationSpec { Ipc6012Class = 3, MinTraceWidthMm = 0.15 };
        Assert.Equal(DrcRuleSet.CheckSpec(spec).Summary, DrcRuleSet.CheckSpec(spec).Summary);
    }
}
