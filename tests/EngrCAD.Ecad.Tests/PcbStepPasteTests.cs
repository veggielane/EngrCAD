using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Ecad;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// STEP (multi-level) solder-paste stencils — a foil milled to different thicknesses in different zones,
/// with its own aperture expansion per level, so a fine-pitch part gets a thin foil / reduced aperture and
/// a large thermal pad a thick foil / more paste. The fab consumes ONE PASTE GERBER PER LEVEL. The bar is
/// the same fab-house style as the flat stencil, plus the two properties a step stencil must have:
/// backward-compatible BYTE-IDENTITY when no steps are declared, and every SMD aperture on EXACTLY ONE
/// level (a partition — no pad printed twice, none dropped). A stencil that double-prints or pastes a
/// through-hole pad is a real defect, so every property is verified hard.
/// </summary>
public sealed class PcbStepPasteTests
{
    // ==== region-equality oracle (area AND symmetric difference) — the twin decoder ==========

    private static void AssertRegionsEqual(
        IReadOnlyList<CurvedRegion2d> expected, IReadOnlyList<CurvedRegion2d> actual,
        double tolerance, string what)
    {
        double expectedArea = expected.Sum(r => r.Area);
        double actualArea = actual.Sum(r => r.Area);
        double reference = Math.Max(expectedArea, 1e-30);

        Assert.True(Math.Abs(expectedArea - actualArea) <= tolerance * reference,
            $"{what}: area {actualArea:g9} recovered, expected {expectedArea:g9}");

        double symmetric =
            CurvedRegion2dBoolean.Difference(expected, actual).Sum(r => r.Area)
            + CurvedRegion2dBoolean.Difference(actual, expected).Sum(r => r.Area);
        Assert.True(symmetric <= tolerance * reference,
            $"{what}: recovered differs from expected by area {symmetric:g9} ({symmetric / reference:g3} relative)");
    }

    // ==== 1) NO-STEPS BYTE-IDENTITY (the backward-compatible contract) ========================

    [Fact]
    public void NoStencilArgument_IsByteIdentical_ToTheFlatSingleStencil()
    {
        // Passing no stencil is EXACTLY the flat path — the copper / mask / silk / outline / drill AND the
        // paste Gerbers are byte-identical, since nothing about the flat path changed.
        var layout = RichBoard().WithPaste(new PcbPasteSettings(Expansion: -0.05));

        var baseline = PcbGerberExport.Generate(layout, "b");                 // the current single-stencil output
        var again = PcbGerberExport.Generate(layout, "b", stencil: null);    // the no-op stencil argument

        Assert.Equal(baseline.PasteLayers.Count, again.PasteLayers.Count);
        for (int i = 0; i < baseline.PasteLayers.Count; i++)
        {
            Assert.Equal(baseline.PasteLayers[i].Layer, again.PasteLayers[i].Layer);
            Assert.Null(again.PasteLayers[i].PasteLevelToken);               // a flat paste has no level token
            Assert.Equal(baseline.PasteLayers[i].Gerber, again.PasteLayers[i].Gerber);
        }
        // ... and the rest of the set is untouched too.
        for (int i = 0; i < baseline.CopperLayers.Count; i++)
            Assert.Equal(baseline.CopperLayers[i].Gerber, again.CopperLayers[i].Gerber);
        Assert.Equal(baseline.OutlineGerber, again.OutlineGerber);
        Assert.Equal(baseline.Drill, again.Drill);
    }

    [Fact]
    public void OneLevelStep_AtTheDefaultExpansion_IsByteIdenticalToFlatPaste()
    {
        // A step stencil that is a SINGLE default level at the default expansion prints the SAME apertures
        // as a flat stencil — the Gerber CONTENT is byte-identical (only the file NAME carries the foil
        // thickness), and the PcbPaste apertures are the same regions.
        var layout = RichBoard();
        var model = PcbCopperModel.FromLayout(layout);
        const double e = PcbPasteSettings.DefaultExpansion;   // -0.05

        var flat = PcbGerberExport.Generate(layout.WithPaste(new PcbPasteSettings(Expansion: e)), "b");
        var one = PcbGerberExport.Generate(RichBoard(), "b",
            new PasteStencil(PasteStep.Default(0.1, e)));

        // The Gerber content of the single level equals the flat side's, byte for byte.
        foreach (var side in new[] { "Top", "Bottom" })
        {
            string flatGerber = flat.PasteLayers.Single(l => l.Layer == side).Gerber;
            var stepLevels = one.PasteLayers.Where(l => l.Layer == side).ToList();
            // Every SMD pad landed on the one level, so there is at most one content per side.
            Assert.True(stepLevels.Count <= 1);
            if (stepLevels.Count == 1)
            {
                Assert.Equal("100um", stepLevels[0].PasteLevelToken);
                Assert.Equal(flatGerber, stepLevels[0].Gerber);   // byte-identical content
            }
        }

        // And PcbPaste itself: the one-level stencil's combined apertures equal the flat stencil's.
        var flatPaste = PcbPaste.For(model);
        var stepPaste = PcbPaste.For(model, new PasteStencil(PasteStep.Default(0.1, e)));
        Assert.False(flatPaste.IsStepped);
        Assert.True(stepPaste.IsStepped);
        AssertRegionsEqual(
            flatPaste.Top.Apertures.Select(a => a.Region).ToList(),
            stepPaste.Top.Apertures.Select(a => a.Region).ToList(),
            1e-9, "one-level step vs flat (top)");
    }

    // ==== 2) THE PARTITION — every SMD aperture on EXACTLY ONE level ==========================

    [Fact]
    public void EverySmdApertureIsOnExactlyOneLevel_AndTheUnionEqualsTheFlatSet()
    {
        // A fine-pitch level (U1's small pads), a thick level (P1's power pad by component) and a default
        // (R1's ordinary pads). Every SMD pad has a home, on exactly one level, and the union of the levels
        // equals the flat single-stencil pad set — no pad printed twice, none dropped.
        var model = PcbCopperModel.FromLayout(RichBoard());
        var stencil = RichStencil();

        var stepped = PcbPaste.For(model, stencil);
        var flat = PcbPaste.For(model, new PcbPasteSettings(Expansion: -0.05));

        // Count conservation: the total apertures across all levels equals the flat stencil's total.
        int flatCount = flat.Top.Apertures.Count + flat.Bottom.Apertures.Count;
        int stepCount = stepped.Layers.Sum(l => l.Apertures.Count);
        Assert.Equal(flatCount, stepCount);
        Assert.Equal(7, stepCount);   // U1(4) + P1(1) + R1(2)

        // Each source appears on EXACTLY ONE level (the partition).
        var perLevelSources = stepped.Layers
            .Select(l => l.Apertures.Select(a => a.Source).ToList())
            .ToList();
        var all = perLevelSources.SelectMany(x => x).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());   // no source twice, ever

        // The union of the levels' sources equals the flat stencil's source set.
        var flatSources = flat.Top.Apertures.Concat(flat.Bottom.Apertures)
            .Select(a => a.Source).ToHashSet(StringComparer.Ordinal);
        var stepSources = all.ToHashSet(StringComparer.Ordinal);
        Assert.True(flatSources.SetEquals(stepSources),
            $"flat {{{string.Join(",", flatSources)}}} vs stepped {{{string.Join(",", stepSources)}}}");

        // And each pad went to the RIGHT level (the assignment, not merely a partition).
        var thin = LevelSources(stepped, "100um");
        var thick = LevelSources(stepped, "200um");
        var def = LevelSources(stepped, "150um");
        Assert.Equal(new[] { "U1.1", "U1.2", "U1.3", "U1.4" }.OrderBy(s => s), thin.OrderBy(s => s));
        Assert.Equal(new[] { "P1.1" }, thick.ToArray());
        Assert.Equal(new[] { "R1.1", "R1.2" }.OrderBy(s => s), def.OrderBy(s => s));
    }

    [Fact]
    public void OverlappingZones_ResolveByFirstMatch_AStatedRuleNotAnError()
    {
        // A pad covered by TWO zones is not ambiguous — the FIRST matching level (list order) wins, a
        // stated rule, and the second (which then catches nothing) is an empty level that emits no file.
        var layout = RoundPadBoard();   // one Ø1.0 pad "T1.1" at (0, 0)
        var model = PcbCopperModel.FromLayout(layout);

        var first = PasteLevelSelector.InRectangle(new(-2, -2), new(2, 2));    // covers (0,0)
        var second = PasteLevelSelector.InRectangle(new(-3, -3), new(3, 3));   // ALSO covers (0,0)
        var stencil = new PasteStencil(
            PasteStep.For(0.1, -0.05, first),
            PasteStep.For(0.2, -0.05, second),
            PasteStep.Default(0.15, -0.05));

        var paste = PcbPaste.For(model, stencil);
        // The pad is on the FIRST zone's level (100um), and nothing else, so the second zone's level and
        // the default are both empty and produce no content.
        var content = Assert.Single(paste.Layers);
        Assert.Equal("100um", content.Level!.ThicknessToken);
        Assert.Equal("T1.1", Assert.Single(content.Apertures).Source);
    }

    // ==== 3) PER-LEVEL EXPANSION — a thin level shrinks, a thick level grows ==================

    [Fact]
    public void EachLevelGrowsItsPadsByItsOwnExpansion_ThinSmallerThickLarger()
    {
        // Three identical Ø1.0 round pads in three zones: a THIN level (-0.1), a THICK level (+0.1), and a
        // DEFAULT (0). Each aperture is the pad grown by ITS level's expansion — π(0.4)², π(0.6)², π(0.5)²
        // by closed form (the same exact offset-of-a-disc oracle the flat stencil has), so thin < default
        // < thick.
        var layout = ThreePadBoard();
        var model = PcbCopperModel.FromLayout(layout);

        var stencil = new PasteStencil(
            PasteStep.For(0.10, -0.1, PasteLevelSelector.InRectangle(new(-12, -2), new(-8, 2))),  // A at (-10,0)
            PasteStep.For(0.20, +0.1, PasteLevelSelector.InRectangle(new(8, -2), new(12, 2))),    // C at (10,0)
            PasteStep.Default(0.15, 0.0));                                                        // B at (0,0)

        var paste = PcbPaste.For(model, stencil);

        double AreaOf(string token) =>
            paste.Layers.Single(l => l.Level!.ThicknessToken == token).Apertures.Single().Region.Area;

        double thin = AreaOf("100um"), thick = AreaOf("200um"), def = AreaOf("150um");

        // Exact by closed form — the aperture is the pad ± the level's expansion, a disc of radius 0.5 ± e.
        Assert.Equal(Math.PI * 0.4 * 0.4, thin, 9);
        Assert.Equal(Math.PI * 0.5 * 0.5, def, 9);
        Assert.Equal(Math.PI * 0.6 * 0.6, thick, 9);
        Assert.True(thin < def && def < thick, $"thin {thin:g6} < default {def:g6} < thick {thick:g6}");
    }

    // ==== 4) THE SMD-ONLY RULE survives — no THT pad, no via, on ANY level ====================

    [Fact]
    public void ThroughHolePadsAndVias_GetNoApertureOnAnyLevel()
    {
        // The rich board has a through-hole header (J1) and a via — a step stencil must NOT start pasting
        // them (the classic bug), on ANY level.
        var model = PcbCopperModel.FromLayout(RichBoard());
        var paste = PcbPaste.For(model, RichStencil());

        var everySource = paste.Layers.SelectMany(l => l.Apertures).Select(a => a.Source)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("J1.1", everySource);
        Assert.DoesNotContain("J1.2", everySource);       // no through-hole pad on any level
        Assert.DoesNotContain("via1", everySource);       // never a via
        Assert.All(everySource, s => Assert.DoesNotContain("J1", s));
    }

    // ==== 5) ONE GERBER PER LEVEL — the twin decoder + thickness in the file name =============

    [Fact]
    public void EachLevelIsItsOwnGerber_ThatRoundTrips_AndTheFileNameCarriesTheThickness()
    {
        var layout = RichBoard();
        var stencil = RichStencil();
        var model = PcbCopperModel.FromLayout(layout);
        var paste = PcbPaste.For(model, stencil);
        var fab = PcbGerberExport.Generate(layout, "stepbrd", stencil);

        // One paste Gerber per NON-EMPTY level: Top has thin + thick + default (3), Bottom has none.
        Assert.Equal(3, fab.PasteLayers.Count);
        Assert.All(fab.PasteLayers, l => Assert.Equal("Top", l.Layer));
        Assert.Equal(new[] { "100um", "150um", "200um" },
            fab.PasteLayers.Select(l => l.PasteLevelToken).OrderBy(t => t).ToArray());

        // Each level's Gerber round-trips: decode it back and compare to THAT level's apertures.
        foreach (var content in paste.Layers)
        {
            string token = content.Level!.ThicknessToken;
            string gerber = fab.PasteLayers.Single(l => l.Layer == content.Layer && l.PasteLevelToken == token).Gerber;
            var decoded = GerberReader.Read(gerber).Copper;
            AssertRegionsEqual(content.Apertures.Select(a => a.Region).ToList(), decoded, 1e-4, $"paste level {token}");
        }
    }

    [Fact]
    public void Write_EmitsOneFilePerLevel_NamedWithTheFoilThickness()
    {
        var stencil = RichStencil();
        string dir = Path.Combine(Path.GetTempPath(), "engrcad-steppaste-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = PcbGerberExport.Write(RichBoard(), dir, "stepbrd", stencil);

            // Three paste files (one per non-empty level on the top side), each carrying its foil thickness.
            Assert.Equal(3, result.PasteLayerCount);
            Assert.Contains(result.Files, f => f.EndsWith("-Top_Paste_100um.gbr"));
            Assert.Contains(result.Files, f => f.EndsWith("-Top_Paste_150um.gbr"));
            Assert.Contains(result.Files, f => f.EndsWith("-Top_Paste_200um.gbr"));
            // No un-tokenised paste file (that name belongs to the flat stencil), and no bottom paste.
            Assert.DoesNotContain(result.Files, f => f.EndsWith("-Top_Paste.gbr"));
            Assert.DoesNotContain(result.Files, f => f.EndsWith("-Bottom_Paste_150um.gbr"));

            // Every written paste Gerber re-reads, and the disk text IS the Generate() text (no drift).
            var output = PcbGerberExport.Generate(RichBoard(), "stepbrd", stencil);
            foreach (var f in result.Files.Where(f => f.Contains("_Paste_")))
            {
                string disk = File.ReadAllText(f);
                GerberReader.Read(disk);
                string token = f[(f.IndexOf("_Paste_", StringComparison.Ordinal) + "_Paste_".Length)..].Replace(".gbr", "");
                Assert.Equal(output.PasteLayers.Single(l => l.PasteLevelToken == token).Gerber, disk);
            }
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // ==== 6) DETERMINISM — byte-identical re-emission, stable level order =====================

    [Fact]
    public void StepPaste_IsDeterministic_AndTheLevelOrderIsStable()
    {
        var stencil = RichStencil();
        var a = PcbGerberExport.Generate(RichBoard(), "b", stencil);
        var b = PcbGerberExport.Generate(RichBoard(), "b", stencil);
        Assert.Equal(a.PasteLayers.Count, b.PasteLayers.Count);
        for (int i = 0; i < a.PasteLayers.Count; i++)
        {
            Assert.Equal(a.PasteLayers[i].Layer, b.PasteLayers[i].Layer);
            Assert.Equal(a.PasteLayers[i].PasteLevelToken, b.PasteLayers[i].PasteLevelToken);
            Assert.Equal(a.PasteLayers[i].Gerber, b.PasteLayers[i].Gerber);
        }
        // The level order follows the stencil's step order (thin, thick, default), stably.
        Assert.Equal(new[] { "100um", "200um", "150um" },
            a.PasteLayers.Select(l => l.PasteLevelToken).ToArray());
    }

    // ==== 7) an EMPTY LEVEL is allowed — it emits no file =====================================

    [Fact]
    public void ADeclaredLevelThatCoversNoPad_IsEmpty_AndEmitsNoFile()
    {
        // A zone level far off in a corner catches no pad — it is a legal empty level, and it produces no
        // content and no Gerber file (the number of files equals the number of NON-empty levels).
        var stencil = new PasteStencil(
            PasteStep.For(0.10, -0.05, PasteLevelSelector.InRectangle(new(100, 100), new(200, 200))),  // nothing here
            PasteStep.Default(0.15, -0.05));

        var paste = PcbPaste.For(PcbCopperModel.FromLayout(RichBoard()), stencil);
        // Only the default level carries pads; the empty zone level is absent from Layers.
        Assert.All(paste.Layers, l => Assert.Equal("150um", l.Level!.ThicknessToken));
        Assert.DoesNotContain(paste.Layers, l => l.Level!.ThicknessToken == "100um");

        var fab = PcbGerberExport.Generate(RichBoard(), "b", stencil);
        Assert.DoesNotContain(fab.PasteLayers, l => l.PasteLevelToken == "100um");
        Assert.All(fab.PasteLayers, l => Assert.Equal("150um", l.PasteLevelToken));
    }

    // ==== 8) REFUSALS — the stencil guards shown to FIRE ======================================

    [Fact]
    public void NonPositiveFoilThickness_IsRefusedByName()
    {
        var ex = Assert.Throws<ArgumentException>(() => new PasteStencil(
            PasteStep.For(0.0, -0.05, PasteLevelSelector.FinePitch(0.4)),
            PasteStep.Default(0.15, -0.05)));
        Assert.Contains("foil thickness must be positive", ex.Message);
    }

    [Fact]
    public void AStencilWithNoDefaultLevel_IsRefusedByName()
    {
        var ex = Assert.Throws<ArgumentException>(() => new PasteStencil(
            PasteStep.For(0.10, -0.05, PasteLevelSelector.FinePitch(0.4)),
            PasteStep.For(0.20, +0.05, PasteLevelSelector.Component("P1"))));
        Assert.Contains("DEFAULT level", ex.Message);
    }

    [Fact]
    public void TwoLevelsOfTheSameFoilThickness_AreRefusedByName_TheyWouldCollide()
    {
        var ex = Assert.Throws<ArgumentException>(() => new PasteStencil(
            PasteStep.For(0.10, -0.08, PasteLevelSelector.FinePitch(0.4)),
            PasteStep.Default(0.10, -0.05)));
        Assert.Contains("same foil thickness", ex.Message);
    }

    [Fact]
    public void ANonFiniteExpansion_IsRefusedByName()
    {
        var ex = Assert.Throws<ArgumentException>(() => new PasteStencil(
            PasteStep.Default(0.15, double.NaN)));
        Assert.Contains("expansion must be finite", ex.Message);
    }

    [Fact]
    public void AnEmptyStencil_IsRefusedByName()
    {
        var ex = Assert.Throws<ArgumentException>(() => new PasteStencil());
        Assert.Contains("at least one level", ex.Message);
    }

    [Fact]
    public void FinePitchThreshold_HasNoSilentDefault_AndRefusesANonPositiveThreshold()
    {
        // The fine-pitch heuristic's threshold is a REQUIRED engineering input (no silent default).
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PasteLevelSelector.FinePitch(0.0));
        Assert.Contains("threshold must be positive", ex.Message);
    }

    // ==== helpers ============================================================================

    private static IReadOnlyList<string> LevelSources(PcbPaste paste, string token) =>
        paste.Layers.Where(l => l.Level!.ThicknessToken == token)
            .SelectMany(l => l.Apertures).Select(a => a.Source).ToList();

    private static PasteStencil RichStencil() => new(
        PasteStep.For(0.10, -0.08, PasteLevelSelector.FinePitch(0.4)),   // thin: U1's 0.3 mm pads
        PasteStep.For(0.20, +0.05, PasteLevelSelector.Component("P1")),   // thick: P1's power pad
        PasteStep.Default(0.15, -0.05));                                 // default: R1's ordinary pads

    // A board with a fine-pitch QFN (U1, four 0.3 mm pads), a power pad (P1, one 3 mm pad), an ordinary
    // resistor (R1, two 0.6 mm pads), a through-hole header (J1) and a via — the spread of pads a step
    // stencil exists for, plus the SMD-only foils (the THT pads and the via).
    private static PcbLayout RichBoard()
    {
        PartDefinition Qfn() => new("QFN4", "U",
            [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive),
             new Pin("3", PinType.Passive), new Pin("4", PinType.Passive)],
            new Footprint("qfn4_fp",
            [
                Pad.Smd("1", new Vector2d(-0.3, -0.3), 0.3, 0.3),
                Pad.Smd("2", new Vector2d(0.3, -0.3), 0.3, 0.3),
                Pad.Smd("3", new Vector2d(0.3, 0.3), 0.3, 0.3),
                Pad.Smd("4", new Vector2d(-0.3, 0.3), 0.3, 0.3),
            ]));
        PartDefinition Power() => new("POW", "P",
            [new Pin("1", PinType.Power)],
            new Footprint("pow_fp", [Pad.Smd("1", new Vector2d(0, 0), 3.0, 3.0, PadShape.Rectangular)]));
        PartDefinition Res() => new("RES", "R",
            [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
            new Footprint("res_fp",
            [Pad.Smd("1", new Vector2d(-0.5, 0), 0.6, 0.6), Pad.Smd("2", new Vector2d(0.5, 0), 0.6, 0.6)]));
        PartDefinition Hdr() => new("HDR", "J",
            [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
            new Footprint("hdr_fp",
            [
                Pad.ThroughHole("1", new Vector2d(-1.27, 0), 1.6, 0.9),
                Pad.ThroughHole("2", new Vector2d(1.27, 0), 1.6, 0.9),
            ]));

        var sch = new Schematic("step-rich");
        var u = sch.Add("U1", Qfn());
        var p = sch.Add("P1", Power());
        var r = sch.Add("R1", Res());
        var j = sch.Add("J1", Hdr());
        sch.Stub("N1", u.Pin("1")); sch.Stub("N2", u.Pin("2"));
        sch.Stub("N3", u.Pin("3")); sch.Stub("N4", u.Pin("4"));
        sch.Connect("PWR", p.Pin("1"), r.Pin("1"));
        sch.Stub("NR2", r.Pin("2"));
        sch.Connect("GND", j.Pin("1"), j.Pin("2"));

        var layout = new PcbLayout(sch, new PcbBoard([new(-20, -20), new(20, -20), new(20, 20), new(-20, 20)], 1.6));
        layout.Place("U1", 10, 0);
        layout.Place("P1", -10, 0);
        layout.Place("R1", 0, 8);
        layout.Place("J1", 0, -8);
        layout.AddVia("GND", 15, 15, "Top", "Bottom", drill: 0.4, pad: 0.9);
        return layout;
    }

    private static PcbLayout RoundPadBoard()
    {
        var def = new PartDefinition("T1", "TP", [new Pin("1", PinType.Passive)],
            new Footprint("t1_fp", [Pad.Smd("1", new Vector2d(0, 0), 1.0, 1.0, PadShape.Round)]));
        var sch = new Schematic("round");
        var t = sch.Add("T1", def, "TP");
        sch.Stub("A", t.Pin("1"));
        var layout = new PcbLayout(sch, new PcbBoard([new(-5, -5), new(5, -5), new(5, 5), new(-5, 5)], 1.6));
        layout.Place("T1", 0, 0);
        return layout;
    }

    // Three identical Ø1.0 round pads at (-10,0), (0,0), (10,0) — one per level for the per-level-expansion
    // test.
    private static PcbLayout ThreePadBoard()
    {
        PartDefinition Tp(string name) => new(name, "TP", [new Pin("1", PinType.Passive)],
            new Footprint(name + "_fp", [Pad.Smd("1", new Vector2d(0, 0), 1.0, 1.0, PadShape.Round)]));
        var sch = new Schematic("three");
        var a = sch.Add("A", Tp("A"), "TP");
        var b = sch.Add("B", Tp("B"), "TP");
        var c = sch.Add("C", Tp("C"), "TP");
        sch.Stub("NA", a.Pin("1")); sch.Stub("NB", b.Pin("1")); sch.Stub("NC", c.Pin("1"));
        var layout = new PcbLayout(sch, new PcbBoard([new(-20, -10), new(20, -10), new(20, 10), new(-20, 10)], 1.6));
        layout.Place("A", -10, 0);
        layout.Place("B", 0, 0);
        layout.Place("C", 10, 0);
        return layout;
    }
}
