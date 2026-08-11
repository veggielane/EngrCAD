using System.Reflection;
using EngrCAD.Core;
using EngrCAD.Ecad;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The named house-spec catalogue (<see cref="StandardFabSpecs"/>): a handful of common
/// <see cref="PcbFabricationSpec"/> presets a caller picks instead of typing the fields. Verified
/// the ECAD house way — the transcribed values are asserted in the datasheet form a human checks
/// (a re-typed formula agrees with its own mistake, so the number IS the transcription); a catalogue
/// spec is an ORDINARY spec (it round-trips through the layout file and its notes reach the fab
/// drawing through the seams that already exist); every entry is valid and fully populated (so a new
/// one cannot be added half-filled), and the catalogue enumeration covers every published entry.
/// </summary>
public sealed class StandardFabSpecsTests
{
    // A plain rectangular board — the fixture the drawing tests start from.
    private static PcbBoard PlainBoard() => new(
        [
            new Vector2d(-25, -20), new Vector2d(25, -20),
            new Vector2d(25, 20), new Vector2d(-25, 20),
        ],
        thickness: 1.6);

    // ==== 1. transcription — the datasheet values, per entry ================================

    // The nominal figures, asserted as the values a human checks (not a formula). A transcription
    // typo agrees with itself, so these literals ARE the transcription test.
    [Fact]
    public void EachEntry_TranscribesItsDatasheetValues()
    {
        var hasl = StandardFabSpecs.TwoLayerFr4Hasl;
        Assert.Equal("FR-4", hasl.BaseMaterial);
        Assert.Equal(1.6, hasl.FinishedThicknessMm);
        Assert.Equal(1.0, hasl.CopperWeightOz);
        Assert.Equal(PcbSurfaceFinish.HaslLeadFree, hasl.SurfaceFinish);
        Assert.Equal("Green", hasl.SolderMaskColour);
        Assert.Equal("White", hasl.SilkscreenColour);
        Assert.Equal(2, hasl.Ipc6012Class);
        Assert.Equal(0.15, hasl.MinTraceWidthMm);
        Assert.Equal(0.15, hasl.MinClearanceMm);

        var enig = StandardFabSpecs.TwoLayerFr4Enig;
        Assert.Equal("FR-4", enig.BaseMaterial);
        Assert.Equal(1.6, enig.FinishedThicknessMm);
        Assert.Equal(1.0, enig.CopperWeightOz);
        Assert.Equal(PcbSurfaceFinish.Enig, enig.SurfaceFinish);
        Assert.Equal("Green", enig.SolderMaskColour);
        Assert.Equal("White", enig.SilkscreenColour);
        Assert.Equal(2, enig.Ipc6012Class);
        Assert.Equal(0.15, enig.MinTraceWidthMm);
        Assert.Equal(0.15, enig.MinClearanceMm);

        var four = StandardFabSpecs.FourLayerFr4Enig;
        Assert.Equal("FR-4", four.BaseMaterial);
        Assert.Equal(1.6, four.FinishedThicknessMm);
        Assert.Equal(1.0, four.CopperWeightOz);
        Assert.Equal(PcbSurfaceFinish.Enig, four.SurfaceFinish);
        Assert.Equal("Green", four.SolderMaskColour);
        Assert.Equal("White", four.SilkscreenColour);
        Assert.Equal(3, four.Ipc6012Class);
        Assert.Equal(0.20, four.MinTraceWidthMm);
        Assert.Equal(0.20, four.MinClearanceMm);

        var flex = StandardFabSpecs.FlexPolyimideEnig;
        Assert.Equal("Polyimide (flex)", flex.BaseMaterial);
        Assert.Equal(0.1, flex.FinishedThicknessMm);
        Assert.Equal(0.5, flex.CopperWeightOz);
        Assert.Equal(PcbSurfaceFinish.Enig, flex.SurfaceFinish);
        Assert.Equal("Yellow", flex.SolderMaskColour);
        Assert.Equal("White", flex.SilkscreenColour);
        Assert.Equal(2, flex.Ipc6012Class);
        Assert.Equal(0.15, flex.MinTraceWidthMm);
        Assert.Equal(0.15, flex.MinClearanceMm);
    }

    // The two 2-layer entries differ ONLY in the finish — the single most common real distinction —
    // while the 4-layer entry is a genuinely different (class-3, wider-minimum) spec.
    [Fact]
    public void TheTwoLayerEntries_DifferOnlyInFinish_AndTheFourLayerIsDistinct()
    {
        var hasl = StandardFabSpecs.TwoLayerFr4Hasl;
        var enig = StandardFabSpecs.TwoLayerFr4Enig;
        Assert.Equal(enig, hasl with { SurfaceFinish = PcbSurfaceFinish.Enig });
        Assert.NotEqual(hasl, enig);
        Assert.NotEqual(enig, StandardFabSpecs.FourLayerFr4Enig);
    }

    // ==== 2. a catalogue entry is an ORDINARY spec =========================================

    // Passed to WithFabrication it persists write-only-when-stated, so save -> load -> save is a
    // byte-identical fixed point and every field comes back verbatim.
    [Fact]
    public void ACatalogueSpec_RoundTripsThroughTheLayoutFile()
    {
        var spec = StandardFabSpecs.FourLayerFr4Enig;
        var layout = PcbFixtures.Layout().WithFabrication(spec);

        string s1 = layout.Save();
        Assert.Contains("fabrication", s1);

        var loaded = PcbLayout.Load(s1, PcbFixtures.Library());
        Assert.Equal(s1, loaded.Save());   // byte-identical fixed point

        var r = loaded.Fabrication;
        Assert.NotNull(r);
        Assert.Equal(spec.BaseMaterial, r.BaseMaterial);
        Assert.Equal(spec.FinishedThicknessMm, r.FinishedThicknessMm);
        Assert.Equal(spec.CopperWeightOz, r.CopperWeightOz);
        Assert.Equal(spec.SurfaceFinish, r.SurfaceFinish);
        Assert.Equal(spec.SolderMaskColour, r.SolderMaskColour);
        Assert.Equal(spec.SilkscreenColour, r.SilkscreenColour);
        Assert.Equal(spec.Ipc6012Class, r.Ipc6012Class);
        Assert.Equal(spec.MinTraceWidthMm, r.MinTraceWidthMm);
        Assert.Equal(spec.MinClearanceMm, r.MinClearanceMm);
    }

    // Its notes reach the fabrication drawing through the existing PcbFabricationSheet seam — no new
    // drawing code. (The copper-weight note carries a micro sign, so it is checked by prefix.)
    [Fact]
    public void ACatalogueSpec_ProducesFabDrawingNotes()
    {
        var layout = new PcbLayout(new Schematic("house-demo"), PlainBoard())
            .WithFabrication(StandardFabSpecs.TwoLayerFr4Enig);
        var notes = new PcbFabricationSheet(layout).Compute().Notes;

        Assert.Contains("MATERIAL: FR-4.", notes);
        Assert.Contains("SURFACE FINISH: ENIG.", notes);
        Assert.Contains(notes, n => n.StartsWith("COPPER WEIGHT: 1 oz (35 "));
        Assert.Contains("SOLDER MASK COLOUR: GREEN.", notes);
        Assert.Contains("SILKSCREEN COLOUR: WHITE.", notes);
        Assert.Contains("FABRICATE TO IPC-6012 CLASS 2.", notes);
        Assert.Contains("MINIMUM TRACE WIDTH 0.15 mm.", notes);
        Assert.Contains("MINIMUM CLEARANCE 0.15 mm.", notes);
    }

    // Determinism: a catalogue-backed layout saves the same bytes twice (the catalogue entries are
    // compile-time constants, so nothing about the spec is order- or clock-dependent).
    [Fact]
    public void ACatalogueBackedLayout_SavesDeterministically()
    {
        var layout = PcbFixtures.Layout().WithFabrication(StandardFabSpecs.TwoLayerFr4Hasl);
        Assert.Equal(layout.Save(), layout.Save());
    }

    // ==== 3. validity ======================================================================

    // Every entry passes PcbFabricationSpec's own validation (WithFabrication validates and refuses a
    // bad one by name), states something, and claims an IPC class in {1, 2, 3}.
    [Fact]
    public void EveryCatalogueSpec_IsValid()
    {
        foreach (var (name, spec) in StandardFabSpecs.All)
        {
            // WithFabrication runs Validate; a bad value would throw here, naming the entry.
            var layout = new PcbLayout(new Schematic(name), PlainBoard()).WithFabrication(spec);
            Assert.Same(spec, layout.Fabrication);

            Assert.True(spec.StatesAnything, $"{name} states nothing");
            Assert.Contains(spec.Ipc6012Class, new int?[] { 1, 2, 3 });
        }
    }

    // Every entry's stated minimum trace/clearance MEETS the IPC class it claims — a house standard
    // must not contradict its own class (DrcRuleSet.CheckSpec is the cross-check).
    [Fact]
    public void EveryCatalogueSpec_ConformsToItsClaimedIpcClass()
    {
        foreach (var (name, spec) in StandardFabSpecs.All)
        {
            var check = DrcRuleSet.CheckSpec(spec);
            Assert.True(check.Conforms, $"{name}: {check.Summary}");
        }
    }

    // ==== 4. coverage claim ================================================================

    // A catalogue entry must be FULLY populated (nine core fields), so a new one cannot be added
    // half-filled: this enumerates the catalogue and asserts each states every core field.
    [Fact]
    public void EveryCatalogueEntry_IsFullyPopulated()
    {
        Assert.NotEmpty(StandardFabSpecs.All);
        foreach (var (name, spec) in StandardFabSpecs.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(spec.BaseMaterial), $"{name}: material");
            Assert.NotNull(spec.FinishedThicknessMm);
            Assert.NotNull(spec.CopperWeightOz);
            Assert.NotNull(spec.SurfaceFinish);
            Assert.False(string.IsNullOrWhiteSpace(spec.SolderMaskColour), $"{name}: mask colour");
            Assert.False(string.IsNullOrWhiteSpace(spec.SilkscreenColour), $"{name}: silk colour");
            Assert.NotNull(spec.Ipc6012Class);
            Assert.NotNull(spec.MinTraceWidthMm);
            Assert.NotNull(spec.MinClearanceMm);
        }
    }

    // The coverage is a CLAIM, not a fixture: reflect over every published catalogue property and
    // assert All lists exactly them — so a new entry that is not in All fails here, and All cannot
    // drift from the properties.
    [Fact]
    public void All_ListsExactlyThePublishedEntries()
    {
        var published = typeof(StandardFabSpecs)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(PcbFabricationSpec))
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToArray();

        var listed = StandardFabSpecs.All.Select(e => e.Name).OrderBy(n => n).ToArray();
        Assert.Equal(published, listed);

        // And each listed spec is the property it names (the pairing is not mislabelled).
        foreach (var (name, spec) in StandardFabSpecs.All)
        {
            var prop = typeof(StandardFabSpecs).GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(prop);
            Assert.Same(spec, prop!.GetValue(null));
        }
    }
}
