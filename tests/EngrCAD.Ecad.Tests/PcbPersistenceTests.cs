using System.Text.RegularExpressions;
using EngrCAD.Core;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

public class PcbPersistenceTests
{
    private static PcbLayout RichLayout()
    {
        var board = new PcbBoard(
            [
                new Vector2d(-25, -20), new Vector2d(25, -20),
                new Vector2d(25, 20), new Vector2d(-25, 20),
            ],
            thickness: 1.6,
            stackup: PcbStackup.Layers(1.6, ("In1", 1.0)),
            holes: [new BoardHole(new Vector2d(-22, -17), 3.0, BoardHoleKind.Mounting)],
            keepOuts: [new KeepOut("K1", KeepOutKind.Via,
                [new Vector2d(0, 0), new Vector2d(4, 0), new Vector2d(4, 4), new Vector2d(0, 4)])]);
        var layout = new PcbLayout(PcbFixtures.Circuit(), board);
        layout.Place("R1", 5, 0, 0, CopperSide.Top);
        layout.Place("J1", -8, 4, 90, CopperSide.Bottom);   // rotation and a bottom side
        return layout;
    }

    // ---- the layout is a byte-identical save->load->save fixed point ---------

    [Fact]
    public void LayoutSaveLoadSave_IsAByteIdenticalFixedPoint()
    {
        var library = PcbFixtures.Library();
        var s1 = RichLayout().Save();
        var s2 = PcbLayout.Load(s1, library).Save();
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void SimpleLayoutSaveLoadSave_IsAByteIdenticalFixedPoint()
    {
        var s1 = PcbFixtures.Layout().Save();
        var s2 = PcbLayout.Load(s1, PcbFixtures.Library()).Save();
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void RoundTrip_PreservesTheBoardPlacementsAndKeepOuts()
    {
        var loaded = PcbLayout.Load(RichLayout().Save(), PcbFixtures.Library());

        Assert.Equal(1.6, loaded.Board.Thickness);
        Assert.Equal(4, loaded.Board.OutlinePoints.Count);
        Assert.Equal(3, loaded.Board.Stackup.Coppers.Count);   // Top, In1, Bottom
        Assert.Single(loaded.Board.Holes);
        Assert.Single(loaded.Board.KeepOuts);
        Assert.Equal(KeepOutKind.Via, loaded.Board.KeepOuts[0].Kind);

        Assert.Equal(2, loaded.Placements.Count);
        var j1 = loaded.Placements.Single(p => p.Reference == "J1");
        Assert.Equal(CopperSide.Bottom, j1.Side);
        Assert.Equal(90, j1.RotationDegrees);

        // The embedded schematic survives and still checks out.
        Assert.True(loaded.Schematic.Check().Ok);
    }

    [Fact]
    public void LoadedLayout_ReattachesBodiesFromTheLibrary_AndBuildsAnAssembly()
    {
        var loaded = PcbLayout.Load(RichLayout().Save(), PcbFixtures.Library());
        var instances = loaded.ToAssembly().Flatten();
        Assert.Contains(instances, i => i.Path.EndsWith("/R1"));
        Assert.Contains(instances, i => i.Path.EndsWith("/J1"));
    }

    // ---- the default board frame / two-layer stackup are write-only-when-stated

    [Fact]
    public void DefaultTwoLayerStackupAndIdentityFrame_AreOmitted()
    {
        var json = PcbFixtures.Layout().Save();   // a plain two-layer board at world XY
        Assert.DoesNotContain("\"stackup\"", json);
        Assert.DoesNotContain("\"boardFrame\"", json);
    }

    // ---- stage-1 pad byte compatibility -------------------------------------

    [Fact]
    public void Stage1SmdFootprint_SavesWithoutKindOrDrill_ByteCompatible()
    {
        // A stage-1 schematic (all SMD pads, no drill) writes exactly as it did before stage 2.
        var json = Fixtures.LedIndicator().Save();
        Assert.DoesNotContain("\"kind\"", json);
        Assert.DoesNotContain("\"drill\"", json);

        // And it is still a byte fixed point.
        Assert.Equal(json, Schematic.Load(json).Save());
    }

    [Fact]
    public void ThroughHolePad_RoundTripsWithItsDrill()
    {
        var def = new PartDefinition("HDR", "J",
            [new Pin("1"), new Pin("2")],
            new Footprint("HDR254", [
                Pad.ThroughHole("1", new Vector2d(-1.27, 0), 1.6, 0.9),
                Pad.ThroughHole("2", new Vector2d(1.27, 0), 1.6, 0.9),
            ]));
        var sch = new Schematic();
        sch.Add("J1", def);

        var json = sch.Save();
        Assert.Contains("\"kind\": \"ThroughHole\"", json);
        Assert.Contains("\"drill\": 0.9", json);

        var loaded = Schematic.Load(json);
        var pad = loaded.Find("J1")!.Definition.Footprint!.Pads[0];
        Assert.Equal(PadKind.ThroughHole, pad.Kind);
        Assert.Equal(0.9, pad.DrillDiameter);
        Assert.Equal((1.6 - 0.9) / 2, pad.AnnularRing, 12);

        Assert.Equal(json, loaded.Save());   // byte fixed point with the new fields
    }

    // ---- structural refusals by name ----------------------------------------

    [Fact]
    public void UnknownFormatOrVersion_IsRefusedByName()
    {
        var json = PcbFixtures.Layout().Save();
        Assert.Throws<FormatException>(() =>
            PcbLayout.Load(json.Replace("\"engrcad-pcb\"", "\"nope\"")));
        Assert.Throws<FormatException>(() =>
            PcbLayout.Load(json.Replace("\"version\": 1", "\"version\": 42")));
    }

    [Fact]
    public void PlacementNamingAMissingComponent_IsRefusedByName()
    {
        // Inject a placement of a component the (embedded) schematic does not contain.
        var json = PcbFixtures.Layout().Save().Replace(
            "\"placements\": [",
            "\"placements\": [ { \"ref\": \"GHOST\", \"x\": 0, \"y\": 0 },");
        var ex = Assert.Throws<FormatException>(() => PcbLayout.Load(json, PcbFixtures.Library()));
        Assert.Contains("GHOST", ex.Message);
    }

    // ---- files ---------------------------------------------------------------

    [Fact]
    public void SaveFile_LoadFile_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pcb-{Guid.NewGuid():N}.json");
        try
        {
            PcbFixtures.Layout().SaveFile(path);
            var loaded = PcbLayout.LoadFile(path, PcbFixtures.Library());
            Assert.Equal(File.ReadAllText(path), loaded.Save());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
