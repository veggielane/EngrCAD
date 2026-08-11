using EngrCAD.Core;
using EngrCAD.Ecad;
using Xunit;

namespace EngrCAD.Ecad.Tests;

public class EaglePersistenceTests
{
    private static Schematic WithEaglePart(string deviceName)
    {
        var part = EagleLibraryReader.Load(EagleFixtures.Library, deviceName);
        var sch = new Schematic("loaded");
        sch.Add("X1", part.Definition);
        return sch;
    }

    // ---- an Eagle-loaded symbol round-trips as a byte-identical fixed point --

    [Fact]
    public void AnEagleLoadedSymbol_SaveLoadSave_IsAByteIdenticalFixedPoint()
    {
        var s1 = WithEaglePart("IC-SOIC8").Save();
        var s2 = Schematic.Load(s1).Save();
        Assert.Equal(s1, s2);
        Assert.Contains("\"symbol\"", s1);   // the symbol travelled as data
    }

    [Fact]
    public void AnEagleLoadedSymbol_SurvivesTheRoundTrip_WithEveryPinAndGraphicIntact()
    {
        var before = EagleLibraryReader.Load(EagleFixtures.Library, "R-EU_R0805").Definition;
        var after = Schematic.Load(WithEaglePart("R-EU_R0805").Save()).Find("X1")!.Definition;

        Assert.NotNull(after.Symbol);
        Assert.Equal(before.Symbol!.Pins, after.Symbol!.Pins);   // SymbolPin is a record struct
        Assert.Equal(before.Symbol.Graphics.Count, after.Symbol.Graphics.Count);
        var rectBefore = (SymbolRectangle)before.Symbol.Graphics[0];
        var rectAfter = (SymbolRectangle)after.Symbol.Graphics[0];
        Assert.Equal(rectBefore.Min, rectAfter.Min);
        Assert.Equal(rectBefore.Max, rectAfter.Max);
    }

    [Fact]
    public void AnEagleLoadedSymbol_PinAnchorsAndDirections_RoundTripExactly()
    {
        var reloaded = Schematic.Load(WithEaglePart("R-EU_R0805").Save()).Find("X1")!.Definition.Symbol!;
        var pin1 = reloaded.PinNumbered("1");
        Assert.Equal(new Vector2d(0, 3.81), pin1.Anchor);
        Assert.Equal(SymbolPinDirection.Down, pin1.Direction);
        Assert.Equal(2.54, pin1.Length);
        Assert.Equal(PinType.Passive, pin1.Type);
    }

    [Fact]
    public void AThroughHolePart_RoundTripsWithItsDrills()
    {
        // The through-hole footprint (DIL08) round-trips its Kind/Drill (write-only-when-stated).
        var s1 = WithEaglePart("IC-DIL08").Save();
        Assert.Equal(s1, Schematic.Load(s1).Save());
        var footprint = Schematic.Load(s1).Find("X1")!.Definition.Footprint!;
        Assert.Equal(PadKind.ThroughHole, footprint.Pads[0].Kind);
        Assert.Equal(0.8, footprint.Pads[0].DrillDiameter);
    }

    // ---- the KiCad path stays BIT-IDENTICAL (no shared code moved) ------------

    [Fact]
    public void TheKiCadLoadPath_IsUnchanged_ByTheEagleReader()
    {
        // Nothing in Symbol/Footprint/PartDefinition/PinIdentity/SchematicFile changed to add the
        // Eagle reader, so a KiCad-loaded part serializes exactly as it did before, and is still a
        // save->load->save fixed point.
        var part = ComponentLibrary.Read(KiCadFixtures.Soic8Sym, KiCadFixtures.Soic8Mod);
        var sch = new Schematic("kicad");
        sch.Add("U1", part.Definition);
        var json = sch.Save();
        Assert.Equal(json, Schematic.Load(json).Save());
        Assert.True(part.Identity.Ok, part.Identity.ToString());
    }
}
