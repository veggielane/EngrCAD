using System.Text;
using EngrCAD.Core;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The board FABRICATION-REQUIREMENTS spec (<see cref="PcbFabricationSpec"/>): the fab-package
/// fields the geometry cannot carry (material, finished thickness, copper weight, surface finish,
/// mask/silk colours, IPC-6012 class, minimum trace/clearance, free-form notes). Verified the
/// ECAD house way — the fab drawing reads it WRITE-ONLY-WHEN-STATED (a stated field prints its
/// note, an unstated one is omitted and never invented, and no spec leaves the drawing
/// byte-identical), persistence is byte-identical when absent and a save→load→save fixed point
/// when present, and a bad stated value is refused BY NAME.
/// </summary>
public sealed class PcbFabricationSpecTests
{
    // A plain rectangular board of a given thickness, with no spec — the fixture the drawing tests
    // start from. The thickness is a parameter so the finished-thickness OVERRIDE can be seen (the
    // spec's finished thickness differing from the modelled plate thickness).
    private static PcbBoard PlainBoard(double thickness) => new(
        [
            new Vector2d(-25, -20), new Vector2d(25, -20),
            new Vector2d(25, 20), new Vector2d(-25, 20),
        ],
        thickness);

    private static IReadOnlyList<string> Notes(PcbLayout layout) =>
        new PcbFabricationSheet(layout).Compute().Notes;

    // ==== 1. write-only-when-stated in the drawing =========================================

    [Fact]
    public void FabDrawing_PrintsExactlyTheStatedFabricationFields()
    {
        // A 1.5 mm modelled plate but a stated 1.6 mm FINISHED thickness: the note must read the
        // STATED value (proving the spec is read, not the geometry).
        var board = PlainBoard(1.5);
        var layout = new PcbLayout(new Schematic("fab-demo"), board)
            .WithFabrication(new PcbFabricationSpec
            {
                BaseMaterial = "FR-4",
                FinishedThicknessMm = 1.6,
                CopperWeightOz = 1,
                SurfaceFinish = PcbSurfaceFinish.Enig,
                Ipc6012Class = 2,
            });
        var notes = Notes(layout);

        // The stated finished thickness OVERRIDES the modelled 1.5 mm plate.
        Assert.Contains("FINISHED BOARD THICKNESS 1.6 mm.", notes);
        Assert.Contains("MATERIAL: FR-4.", notes);
        Assert.Contains("SURFACE FINISH: ENIG.", notes);
        Assert.Contains("FABRICATE TO IPC-6012 CLASS 2.", notes);
        Assert.Contains("COPPER WEIGHT: 1 oz (35 \u00B5m).", notes);

        // Nothing unstated is invented: no colour, no minimum trace/clearance, no free note.
        Assert.DoesNotContain(notes, n => n.Contains("COLOUR"));
        Assert.DoesNotContain(notes, n => n.Contains("MINIMUM"));
        // The modelled 1.5 mm thickness was replaced, not printed alongside — exactly one
        // FINISHED BOARD THICKNESS note.
        Assert.Single(notes, n => n.StartsWith("FINISHED BOARD THICKNESS"));
    }

    [Fact]
    public void FabDrawing_PrintsColoursMinTraceClearanceAndFreeNotes()
    {
        var layout = new PcbLayout(new Schematic("fab-demo"), PlainBoard(1.6))
            .WithFabrication(new PcbFabricationSpec
            {
                SolderMaskColour = "Black",
                SilkscreenColour = "White",
                MinTraceWidthMm = 0.15,
                MinClearanceMm = 0.2,
                SurfaceFinish = PcbSurfaceFinish.Other,
                SurfaceFinishOther = "ENEPIG",
                Notes = ["IMPEDANCE CONTROL: 50 OHM SINGLE-ENDED.", "MATERIAL UL 94V-0."],
            });
        var notes = Notes(layout);

        Assert.Contains("SOLDER MASK COLOUR: BLACK.", notes);
        Assert.Contains("SILKSCREEN COLOUR: WHITE.", notes);
        Assert.Contains("MINIMUM TRACE WIDTH 0.15 mm.", notes);
        Assert.Contains("MINIMUM CLEARANCE 0.2 mm.", notes);
        Assert.Contains("SURFACE FINISH: ENEPIG.", notes);   // the 'Other' finish, named
        Assert.Contains("IMPEDANCE CONTROL: 50 OHM SINGLE-ENDED.", notes);   // free notes verbatim
        Assert.Contains("MATERIAL UL 94V-0.", notes);
    }

    [Fact]
    public void FabDrawing_NoSpec_IsByteIdentical_AndAnEmptySpecAddsNothing()
    {
        var board = PlainBoard(1.5);   // one shared board, so the two drawings differ only by the spec
        var noSpec = new PcbLayout(new Schematic("fab-demo"), board);
        var emptySpec = new PcbLayout(new Schematic("fab-demo"), board)
            .WithFabrication(PcbFabricationSpec.Default);

        var a = new PcbFabricationSheet(noSpec).Compute();
        var b = new PcbFabricationSheet(emptySpec).Compute();

        // An empty (states-nothing) spec adds no notes and moves nothing — identical notes AND SVG.
        Assert.Equal(a.Notes, b.Notes);
        Assert.Equal(a.ToSvg(), b.ToSvg());

        // With no spec the thickness note is the MODELLED plate thickness (byte-identical to before
        // the spec existed) and none of the fab-spec fields appears.
        Assert.Contains("FINISHED BOARD THICKNESS 1.5 mm.", a.Notes);
        Assert.DoesNotContain(a.Notes, n => n.Contains("MATERIAL"));
        Assert.DoesNotContain(a.Notes, n => n.Contains("SURFACE FINISH"));
        Assert.DoesNotContain(a.Notes, n => n.Contains("COPPER WEIGHT"));
        Assert.DoesNotContain(a.Notes, n => n.Contains("IPC-6012"));
    }

    [Fact]
    public void FabDrawing_WithACopperWeightNote_StillExportsAValidPdf()
    {
        // The copper-weight note carries a micro sign (in WinAnsi), so ToPdf must not refuse it.
        var layout = new PcbLayout(new Schematic("fab-demo"), PlainBoard(1.6))
            .WithFabrication(new PcbFabricationSpec { CopperWeightOz = 2 });
        var drawing = new PcbFabricationSheet(layout).Compute();
        Assert.Contains("COPPER WEIGHT: 2 oz (70 \u00B5m).", drawing.Notes);

        byte[] pdf = drawing.ToPdf();
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
    }

    // ==== 2. persistence ===================================================================

    [Fact]
    public void Persistence_NoSpec_IsByteIdenticalToAPreSpecFile()
    {
        var layout = PcbFixtures.Layout();   // states no fabrication spec
        string json = layout.Save();

        // A layout that states no spec writes NO fabrication key — byte-identical to a pre-spec file.
        Assert.DoesNotContain("fabrication", json);
        // And it is still a fixed point (nothing was added on the round trip).
        Assert.Equal(json, PcbLayout.Load(json, PcbFixtures.Library()).Save());
    }

    [Fact]
    public void Persistence_WithSpec_IsAByteIdenticalFixedPoint_AndFieldsRoundTripExactly()
    {
        var spec = new PcbFabricationSpec
        {
            BaseMaterial = "FR-4",
            FinishedThicknessMm = 1.6,
            CopperWeightOz = 2,
            SurfaceFinish = PcbSurfaceFinish.Other,
            SurfaceFinishOther = "Hard gold",
            SolderMaskColour = "Green",
            SilkscreenColour = "White",
            Ipc6012Class = 3,
            MinTraceWidthMm = 0.127,
            MinClearanceMm = 0.15,
            Notes = ["FABRICATE PER IPC-6012 CLASS 3.", "50 OHM CONTROLLED IMPEDANCE."],
        };
        var layout = PcbFixtures.Layout().WithFabrication(spec);

        string s1 = layout.Save();
        Assert.Contains("fabrication", s1);

        var loaded = PcbLayout.Load(s1, PcbFixtures.Library());
        string s2 = loaded.Save();
        Assert.Equal(s1, s2);   // save -> load -> save byte-identical fixed point

        // The stored numerics/strings/enum come back verbatim (no re-derivation).
        var r = loaded.Fabrication;
        Assert.NotNull(r);
        Assert.Equal("FR-4", r.BaseMaterial);
        Assert.Equal(1.6, r.FinishedThicknessMm);
        Assert.Equal(2.0, r.CopperWeightOz);
        Assert.Equal(PcbSurfaceFinish.Other, r.SurfaceFinish);
        Assert.Equal("Hard gold", r.SurfaceFinishOther);
        Assert.Equal("Green", r.SolderMaskColour);
        Assert.Equal("White", r.SilkscreenColour);
        Assert.Equal(3, r.Ipc6012Class);
        Assert.Equal(0.127, r.MinTraceWidthMm);
        Assert.Equal(0.15, r.MinClearanceMm);
        Assert.Equal(spec.Notes, r.Notes);
    }

    [Fact]
    public void Persistence_APartialSpec_RoundTripsAndDropsNothingItStated()
    {
        // A spec that states only a couple of fields (a family-table row) — write-only-when-stated
        // inside the object, so the unstated fields stay null on the way back.
        var layout = PcbFixtures.Layout()
            .WithFabrication(new PcbFabricationSpec { BaseMaterial = "FR-4", Ipc6012Class = 2 });

        var loaded = PcbLayout.Load(layout.Save(), PcbFixtures.Library());
        var r = loaded.Fabrication;
        Assert.NotNull(r);
        Assert.Equal("FR-4", r.BaseMaterial);
        Assert.Equal(2, r.Ipc6012Class);
        Assert.Null(r.FinishedThicknessMm);
        Assert.Null(r.SurfaceFinish);
        Assert.Empty(r.Notes);
        Assert.Equal(layout.Save(), loaded.Save());   // fixed point
    }

    [Fact]
    public void Save_IsDeterministic()
    {
        var layout = PcbFixtures.Layout().WithFabrication(new PcbFabricationSpec
        {
            BaseMaterial = "FR-4", Ipc6012Class = 2, Notes = ["A NOTE."],
        });
        Assert.Equal(layout.Save(), layout.Save());
    }

    // ==== 3. validation ====================================================================

    [Fact]
    public void Validation_RefusesEveryBadStatedValueByName()
    {
        var layout = new PcbLayout(new Schematic("v"), PlainBoard(1.6));

        Assert.Contains("finished board thickness", Refused(new PcbFabricationSpec { FinishedThicknessMm = 0 }, layout));
        Assert.Contains("finished board thickness", Refused(new PcbFabricationSpec { FinishedThicknessMm = double.NaN }, layout));
        Assert.Contains("copper weight", Refused(new PcbFabricationSpec { CopperWeightOz = -1 }, layout));
        Assert.Contains("minimum trace width", Refused(new PcbFabricationSpec { MinTraceWidthMm = 0 }, layout));
        Assert.Contains("minimum clearance", Refused(new PcbFabricationSpec { MinClearanceMm = double.PositiveInfinity }, layout));
        Assert.Contains("IPC-6012 class", Refused(new PcbFabricationSpec { Ipc6012Class = 4 }, layout));
        Assert.Contains("IPC-6012 class", Refused(new PcbFabricationSpec { Ipc6012Class = 0 }, layout));
        Assert.Contains("Other", Refused(new PcbFabricationSpec { SurfaceFinish = PcbSurfaceFinish.Other }, layout));

        // The bad spec was rejected BEFORE it was recorded — the layout still states no spec.
        Assert.Null(layout.Fabrication);
    }

    private static string Refused(PcbFabricationSpec spec, PcbLayout layout) =>
        Assert.Throws<ArgumentException>(() => layout.WithFabrication(spec)).Message;

    [Fact]
    public void DefaultSpec_IsValid_StatesNothing_AndProducesNoFabNotes()
    {
        Assert.False(PcbFabricationSpec.Default.StatesAnything);

        var layout = new PcbLayout(new Schematic("v"), PlainBoard(1.6))
            .WithFabrication(PcbFabricationSpec.Default);   // valid — does not throw
        Assert.NotNull(layout.Fabrication);
        Assert.False(layout.Fabrication.StatesAnything);

        var notes = Notes(layout);
        Assert.DoesNotContain(notes, n =>
            n.Contains("MATERIAL") || n.Contains("SURFACE FINISH") || n.Contains("COPPER WEIGHT")
            || n.Contains("IPC-6012") || n.Contains("COLOUR") || n.Contains("MINIMUM"));
    }

    [Fact]
    public void StatesAnything_IsTrueForAnyStatedField_IncludingOnlyNotes()
    {
        Assert.True(new PcbFabricationSpec { BaseMaterial = "FR-4" }.StatesAnything);
        Assert.True(new PcbFabricationSpec { Ipc6012Class = 2 }.StatesAnything);
        Assert.True(new PcbFabricationSpec { Notes = ["X"] }.StatesAnything);
        Assert.False(new PcbFabricationSpec().StatesAnything);
    }

    [Fact]
    public void ValidStatedValues_AreAccepted()
    {
        // Positive/finite values, a valid class, and a named 'Other' finish all pass Validate.
        var layout = new PcbLayout(new Schematic("v"), PlainBoard(1.6))
            .WithFabrication(new PcbFabricationSpec
            {
                FinishedThicknessMm = 0.8,
                CopperWeightOz = 0.5,
                MinTraceWidthMm = 0.1,
                MinClearanceMm = 0.1,
                Ipc6012Class = 1,
                SurfaceFinish = PcbSurfaceFinish.Other,
                SurfaceFinishOther = "ENEPIG",
            });
        Assert.NotNull(layout.Fabrication);
    }
}
