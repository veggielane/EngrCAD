using System.Linq;
using EngrCAD.Core;
using EngrCAD.Ecad;
using Xunit;

namespace EngrCAD.Ecad.Tests;

public class KiCadPersistenceTests
{
    private static Schematic WithLoadedPart(string symText, string? modText = null)
    {
        var part = ComponentLibrary.Read(symText, modText);
        var sch = new Schematic("loaded");
        sch.Add("X1", part.Definition);
        return sch;
    }

    // ---- a loaded symbol round-trips as a byte-identical fixed point ---------

    [Fact]
    public void ALoadedSymbol_SaveLoadSave_IsAByteIdenticalFixedPoint()
    {
        var s1 = WithLoadedPart(KiCadFixtures.Soic8Sym, KiCadFixtures.Soic8Mod).Save();
        var s2 = Schematic.Load(s1).Save();
        Assert.Equal(s1, s2);
        Assert.Contains("\"symbol\"", s1);   // the symbol travelled as data
    }

    [Fact]
    public void ALoadedSymbol_SurvivesTheRoundTrip_WithEveryPinAndGraphicIntact()
    {
        var before = ComponentLibrary.Read(KiCadFixtures.ResistorSym, KiCadFixtures.ResistorMod).Definition;
        var after = Schematic.Load(WithLoadedPart(KiCadFixtures.ResistorSym, KiCadFixtures.ResistorMod).Save())
            .Find("X1")!.Definition;

        Assert.NotNull(after.Symbol);
        Assert.Equal(before.Symbol!.Pins, after.Symbol!.Pins);   // SymbolPin is a record struct: value equality
        Assert.Equal(before.Symbol.Graphics.Count, after.Symbol.Graphics.Count);
        var rectBefore = (SymbolRectangle)before.Symbol.Graphics[0];
        var rectAfter = (SymbolRectangle)after.Symbol.Graphics[0];
        Assert.Equal(rectBefore.Min, rectAfter.Min);
        Assert.Equal(rectBefore.Max, rectAfter.Max);
    }

    [Fact]
    public void ALoadedSymbol_PinAnchorsAndDirections_RoundTripExactly()
    {
        var reloaded = Schematic.Load(WithLoadedPart(KiCadFixtures.ResistorSym, KiCadFixtures.ResistorMod).Save())
            .Find("X1")!.Definition.Symbol!;
        var pin1 = reloaded.PinNumbered("1");
        Assert.Equal(new Vector2d(0, 3.81), pin1.Anchor);
        Assert.Equal(SymbolPinDirection.Down, pin1.Direction);
        Assert.Equal(1.27, pin1.Length);
        Assert.Equal(PinType.Passive, pin1.Type);
    }

    [Fact]
    public void APartWithATextGraphic_RoundTrips()
    {
        // The SOIC symbol carries a text label; assert it survives byte-for-byte and by value.
        var s1 = WithLoadedPart(KiCadFixtures.Soic8Sym, KiCadFixtures.Soic8Mod).Save();
        Assert.Equal(s1, Schematic.Load(s1).Save());
        var reloaded = Schematic.Load(s1).Find("X1")!.Definition.Symbol!;
        var text = reloaded.Graphics.OfType<SymbolText>().Single();
        Assert.Equal("IC", text.Value);
        Assert.Equal(new Vector2d(0, 0), text.At);
    }

    // ---- a symbol-less definition serializes EXACTLY as before (additive) ----

    [Fact]
    public void ASymbolLessDefinition_SerializesByteIdenticallyToBefore()
    {
        // The stage-1 fixtures carry NO symbol; their JSON must be unchanged, and contain no
        // "symbol" key at all (write-only-when-stated).
        var json = Fixtures.LedIndicator().Save();
        Assert.DoesNotContain("\"symbol\"", json);
        Assert.Equal(json, Schematic.Load(json).Save());
    }

    [Fact]
    public void TheExistingFootprintConstruction_IsBitIdentical_AndUnchanged()
    {
        // The board side READS footprints; assert the stage-1 SMD footprint round-trips exactly
        // (no additive change to Footprint/Pad was needed to load KiCad, so nothing moved).
        var loaded = Schematic.Load(Fixtures.LedIndicator().Save());
        var footprint = loaded.Find("R1")!.Definition.Footprint!;
        Assert.Equal("R0805", footprint.Name);
        Assert.Equal(2, footprint.Pads.Count);
        Assert.Equal(new Vector2d(-1.0, 0), footprint.Pads[0].Center);
        Assert.Equal(1.2, footprint.Pads[0].Width);
        Assert.Equal(PadShape.Rectangular, footprint.Pads[0].Shape);
        Assert.Equal(PadKind.Smd, footprint.Pads[0].Kind);
        Assert.Equal(0.0, footprint.Pads[0].DrillDiameter);
    }

    // ---- the whole loaded part composes into a valid, checkable schematic ----

    [Fact]
    public void ALoadedComponent_WiresIntoACheckableSchematic()
    {
        var r = ComponentLibrary.Read(KiCadFixtures.ResistorSym, KiCadFixtures.ResistorMod);
        var sch = new Schematic("one resistor");
        var r1 = sch.Add("R1", r.Definition, value: "330");
        sch.Stub("A", r1.Pin("1"));
        sch.Stub("B", r1.Pin("2"));
        Assert.True(sch.Check().Ok, sch.Check().ToString());
    }
}
