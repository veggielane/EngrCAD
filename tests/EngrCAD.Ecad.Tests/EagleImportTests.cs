using System;
using System.Linq;
using EngrCAD.Core;
using EngrCAD.Ecad;
using Xunit;

namespace EngrCAD.Ecad.Tests;

public class EagleImportTests
{
    // ---- the library lists its devices --------------------------------------

    [Fact]
    public void Read_ListsEveryDevice_ByItsFullName()
    {
        var lib = EagleLibraryReader.Read(EagleFixtures.Library);
        var names = lib.Devices.Select(d => d.Name).ToArray();

        Assert.Contains("R-EU_R0805", names);
        Assert.Contains("IC-SOIC8", names);
        Assert.Contains("IC-DIL08", names);
        Assert.Contains("BAD-SOIC8", names);
        Assert.Contains("UNMAPPED-X", names);
        Assert.Contains("DUAL-X", names);

        // A device carries the deviceset's prefix and its package.
        var soic = lib.Devices.Single(d => d.Name == "IC-SOIC8");
        Assert.Equal("U", soic.Prefix);
        Assert.Equal("SOIC8", soic.Package);
        Assert.Equal("IC-", soic.DeviceSet);
    }

    // ---- the resistor: pins, footprint, symbol -------------------------------

    [Fact]
    public void Resistor_ArrivesWithPins_Footprint_AndSymbol()
    {
        var part = EagleLibraryReader.Load(EagleFixtures.Library, "R-EU_R0805");

        Assert.Equal("R-EU_R0805", part.Definition.Name);
        Assert.Equal("R", part.Definition.ReferencePrefix);
        Assert.NotNull(part.Definition.Footprint);
        Assert.NotNull(part.Definition.Symbol);
        Assert.Equal(2, part.Definition.Pins.Count);
        Assert.Equal(new[] { "1", "2" }, part.Definition.Pins.Select(p => p.Number));
        Assert.All(part.Definition.Pins, p => Assert.Equal(PinType.Passive, p.Type));
    }

    [Fact]
    public void Resistor_PadGeometry_MatchesTheFileExactly()
    {
        // Eagle stores coordinates in millimetres, so this is exact, not a tolerance.
        var footprint = EagleLibraryReader.Load(EagleFixtures.Library, "R-EU_R0805").Definition.Footprint!;
        Assert.Equal("R0805", footprint.Name);
        Assert.Equal(2, footprint.Pads.Count);

        var pad1 = footprint.Pads[0];
        Assert.Equal("1", pad1.Number);
        Assert.Equal(-0.9125, pad1.Center.X);
        Assert.Equal(0.0, pad1.Center.Y);
        Assert.Equal(1.025, pad1.Width);              // dx
        Assert.Equal(1.4, pad1.Height);               // dy
        Assert.Equal(PadShape.RoundedRectangle, pad1.Shape);   // roundness > 0
        Assert.Equal(PadKind.Smd, pad1.Kind);
        Assert.Equal(0.9125, footprint.Pads[1].Center.X);
    }

    [Fact]
    public void Resistor_Symbol_MatchesTheFile()
    {
        var symbol = EagleLibraryReader.Load(EagleFixtures.Library, "R-EU_R0805").Definition.Symbol!;

        var rect = Assert.IsType<SymbolRectangle>(Assert.Single(symbol.Graphics));
        Assert.Equal(new Vector2d(-1.016, -2.54), rect.Min);
        Assert.Equal(new Vector2d(1.016, 2.54), rect.Max);

        // Pin 1: rot R270 -> points DOWN; anchor above (where a wire lands), short length 2.54.
        var pin1 = symbol.PinNumbered("1");
        Assert.Equal(new Vector2d(0, 3.81), pin1.Anchor);
        Assert.Equal(SymbolPinDirection.Down, pin1.Direction);
        Assert.Equal(2.54, pin1.Length);              // "short" = 0.1 inch
        Assert.Equal(new Vector2d(0, 1.27), pin1.Inner);
        Assert.Equal(PinType.Passive, pin1.Type);

        var pin2 = symbol.PinNumbered("2");
        Assert.Equal(new Vector2d(0, -3.81), pin2.Anchor);
        Assert.Equal(SymbolPinDirection.Up, pin2.Direction);
    }

    // ---- the SOIC-8: the <connect> map unifies the three by pin NUMBER -------

    [Fact]
    public void Soic8_TheConnectMap_UnifiesSymbolFootprintAndNetlist()
    {
        var part = EagleLibraryReader.Load(EagleFixtures.Library, "IC-SOIC8");

        Assert.Equal("U", part.Definition.ReferencePrefix);
        Assert.Equal(8, part.Definition.Pins.Count);
        Assert.Equal(8, part.Definition.Symbol!.Pins.Count);
        Assert.Equal(8, part.Definition.Footprint!.Pads.Count);

        // The pin NUMBERS come from the <connect>'s pad, so the three lists share ONE set of
        // numbers 1..8 — the whole point of the deviceset mapping.
        var padNumbers = new[] { "1", "2", "3", "4", "5", "6", "7", "8" };
        Assert.Equal(padNumbers, part.Definition.Pins.Select(p => p.Number));
        Assert.Equal(padNumbers, part.Definition.Symbol.Pins.Select(p => p.Number).OrderBy(n => n));
        Assert.Equal(padNumbers, part.Definition.Footprint.Pads.Select(p => p.Number).OrderBy(n => n));

        // ... and the identity check agrees: symbol pin N == pad N == netlist pin N.
        Assert.True(part.Identity.Ok, part.Identity.ToString());
    }

    [Fact]
    public void Soic8_PinNamesAndTypes_MapFromTheEagleSymbol()
    {
        // The symbol pin's NAME becomes the netlist pin's name; the pad becomes its number.
        var pins = EagleLibraryReader.Load(EagleFixtures.Library, "IC-SOIC8").Definition.Pins;

        Assert.Equal("IN1", pins[0].Name);
        Assert.Equal(PinType.Input, pins[0].Type);      // direction "in"
        Assert.Equal("GND", pins[3].Name);
        Assert.Equal(PinType.Power, pins[3].Type);      // direction "pwr"
        Assert.Equal("OUT4", pins[4].Name);
        Assert.Equal(PinType.Output, pins[4].Type);     // direction "out"
        Assert.Equal("VCC", pins[7].Name);
        Assert.Equal(PinType.Power, pins[7].Type);
    }

    [Fact]
    public void Soic8_LeftAndRightPins_AnchorOnTheCorrectSide()
    {
        var symbol = EagleLibraryReader.Load(EagleFixtures.Library, "IC-SOIC8").Definition.Symbol!;

        // Left pin (pad 1 = IN1): anchor on the left, rot R0 -> points RIGHT into the body edge.
        var left = symbol.PinNumbered("1");
        Assert.Equal(new Vector2d(-5.08, 3.81), left.Anchor);
        Assert.Equal(SymbolPinDirection.Right, left.Direction);
        Assert.Equal(new Vector2d(-2.54, 3.81), left.Inner);

        // Right pin (pad 5 = OUT4): anchor on the right, rot R180 -> points LEFT.
        var right = symbol.PinNumbered("5");
        Assert.Equal(new Vector2d(5.08, -3.81), right.Anchor);
        Assert.Equal(SymbolPinDirection.Left, right.Direction);
        Assert.Equal(new Vector2d(2.54, -3.81), right.Inner);

        // The body rectangle and the "IC" text both survive.
        Assert.Contains(symbol.Graphics, g => g is SymbolRectangle);
        var text = Assert.IsType<SymbolText>(symbol.Graphics.First(g => g is SymbolText));
        Assert.Equal("IC", text.Value);
        Assert.Equal(1.27, text.Size);
    }

    // ---- the through-hole (DIL08) package: drill + shapes --------------------

    [Fact]
    public void Dil08_CarriesTheDrillsAndPadShapes()
    {
        var part = EagleLibraryReader.Load(EagleFixtures.Library, "IC-DIL08");
        var pads = part.Definition.Footprint!.Pads;
        Assert.Equal(8, pads.Count);

        Assert.All(pads, p => Assert.Equal(PadKind.ThroughHole, p.Kind));
        Assert.All(pads, p => Assert.Equal(0.8, p.DrillDiameter));

        // pad 1: octagon -> Round (no exact octagon); no diameter stated -> nominal drill + 0.5.
        Assert.Equal(PadShape.Round, pads[0].Shape);
        Assert.Equal(new Vector2d(-3.81, 3.81), pads[0].Center);
        Assert.Equal(1.3, pads[0].Width, 12);          // 0.8 drill + 0.5 nominal ring
        Assert.True(pads[0].IsDrilled);

        // pad 2: explicit diameter 1.5 carried exactly; round shape.
        Assert.Equal(PadShape.Round, pads[1].Shape);
        Assert.Equal(1.5, pads[1].Width);
        Assert.Equal(0.35, pads[1].AnnularRing, 12);   // (1.5 - 0.8) / 2

        Assert.Equal(PadShape.Rectangular, pads[3].Shape);   // square
        Assert.Equal(PadShape.Oval, pads[4].Shape);          // long
    }

    [Fact]
    public void Dil08_NamesWhatItApproximatedAndIgnored()
    {
        var part = EagleLibraryReader.Load(EagleFixtures.Library, "IC-DIL08");
        // The <hole> is not a pad and is named; octagon has no exact shape and is named; the
        // auto-sized pads' nominal diameters are named.
        Assert.Contains(part.Diagnostics, d => d.Contains("<hole>"));
        Assert.Contains(part.Diagnostics, d => d.Contains("octagon"));
        Assert.Contains(part.Diagnostics, d => d.Contains("nominal"));
    }

    // ---- the inconsistent device is REPORTED by name ------------------------

    [Fact]
    public void ADeviceWhosePackageDisagrees_ReportsTheOffendingPinsByName()
    {
        // BAD-SOIC8's package is missing pad "8" and carries a stray pad "99".
        var part = EagleLibraryReader.Load(EagleFixtures.Library, "BAD-SOIC8");

        Assert.False(part.Identity.Ok);
        Assert.Contains("8", part.Identity.PinsWithoutPad);        // pin 8 has a symbol pin but no pad
        Assert.Contains("8", part.Identity.SymbolPinsWithoutPad);
        Assert.Contains("99", part.Identity.PadsNotAPin);         // pad 99 is not a pin
        Assert.Contains(part.Identity.Messages, m => m.Contains("'8'"));
        Assert.Contains(part.Identity.Messages, m => m.Contains("'99'"));
    }

    // ---- refusals BY NAME ---------------------------------------------------

    [Fact]
    public void AnUnmappedSymbolPin_IsRefusedByName()
    {
        // UNMAPPED-X's symbol has pins A and B, but only A has a <connect>.
        var ex = Assert.Throws<FormatException>(() =>
            EagleLibraryReader.Load(EagleFixtures.Library, "UNMAPPED-X"));
        Assert.Contains("'B'", ex.Message);
        Assert.Contains("unmapped", ex.Message);
    }

    [Fact]
    public void AMultiGateDevice_IsRefusedByName()
    {
        var ex = Assert.Throws<FormatException>(() =>
            EagleLibraryReader.Load(EagleFixtures.Library, "DUAL-X"));
        Assert.Contains("gates", ex.Message);
    }

    [Fact]
    public void AnAbsentDevice_IsRefusedByName_ListingTheOnesItHas()
    {
        var lib = EagleLibraryReader.Read(EagleFixtures.Library);
        var ex = Assert.Throws<FormatException>(() => lib.Load("NO_SUCH_DEVICE"));
        Assert.Contains("NO_SUCH_DEVICE", ex.Message);
        Assert.Contains("IC-SOIC8", ex.Message);   // it lists what it does carry
    }

    [Fact]
    public void MalformedXml_IsRefusedByName()
    {
        var ex = Assert.Throws<FormatException>(() =>
            EagleLibraryReader.Read("<eagle><drawing><library></drawing></eagle>"));   // unclosed <library>
        Assert.Contains("malformed", ex.Message);
    }

    [Fact]
    public void AFileThatIsNotEagle_IsRefusedByName()
    {
        var ex = Assert.Throws<FormatException>(() =>
            EagleLibraryReader.Read("<kicad_symbol_lib/>"));
        Assert.Contains("eagle", ex.Message);
    }

    [Fact]
    public void ABoardFileRatherThanALibrary_IsRefusedByName()
    {
        var ex = Assert.Throws<FormatException>(() => EagleLibraryReader.Read(EagleFixtures.BoardFile));
        Assert.Contains("board", ex.Message);
        Assert.Contains("library", ex.Message);
    }

    // ---- a curved wire becomes a SymbolArc ----------------------------------

    [Fact]
    public void ACurvedWire_BecomesAnArc()
    {
        var symbol = EagleLibraryReader.Load(EagleFixtures.ArcLibrary, "ARCD").Definition.Symbol!;
        var arc = Assert.IsType<SymbolArc>(Assert.Single(symbol.Graphics));
        Assert.Equal(new Vector2d(-1, 0), arc.Start);
        Assert.Equal(new Vector2d(1, 0), arc.End);
        // A 90 CCW arc over the chord (-1,0)->(1,0) bulges up by sqrt(2) - 1.
        Assert.Equal(0.0, arc.Mid.X, 12);
        Assert.Equal(Math.Sqrt(2) - 1, arc.Mid.Y, 12);
    }

    // ---- determinism --------------------------------------------------------

    [Fact]
    public void Load_IsDeterministic()
    {
        var a = EagleLibraryReader.Load(EagleFixtures.Library, "IC-SOIC8");
        var b = EagleLibraryReader.Load(EagleFixtures.Library, "IC-SOIC8");
        Assert.Equal(SchematicWith(a.Definition).Save(), SchematicWith(b.Definition).Save());
    }

    // ---- a loaded component wires into a checkable schematic -----------------

    [Fact]
    public void ALoadedComponent_WiresIntoACheckableSchematic()
    {
        var r = EagleLibraryReader.Load(EagleFixtures.Library, "R-EU_R0805");
        var sch = new Schematic("one resistor");
        var r1 = sch.Add("R1", r.Definition, value: "330");
        sch.Stub("A", r1.Pin("1"));
        sch.Stub("B", r1.Pin("2"));
        Assert.True(sch.Check().Ok, sch.Check().ToString());
    }

    private static Schematic SchematicWith(PartDefinition definition)
    {
        var sch = new Schematic("t");
        sch.Add("X1", definition);
        return sch;
    }
}
