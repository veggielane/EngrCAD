using System.Linq;
using EngrCAD.Core;
using EngrCAD.Ecad;
using Xunit;

namespace EngrCAD.Ecad.Tests;

public class KiCadImportTests
{
    // ---- the resistor: pins round-trip, symbol primitives, exact geometry ----

    [Fact]
    public void Resistor_ArrivesWithPins_Footprint_AndSymbol()
    {
        var part = ComponentLibrary.Read(KiCadFixtures.ResistorSym, KiCadFixtures.ResistorMod);

        Assert.Equal("R_0805", part.Definition.Name);
        Assert.Equal("R", part.Definition.ReferencePrefix);
        Assert.NotNull(part.Definition.Footprint);
        Assert.NotNull(part.Definition.Symbol);
        Assert.Equal(2, part.Definition.Pins.Count);
        Assert.Equal(new[] { "1", "2" }, part.Definition.Pins.Select(p => p.Number));
        // "~" pin names map to empty.
        Assert.All(part.Definition.Pins, p => Assert.Equal("", p.Name));
        Assert.All(part.Definition.Pins, p => Assert.Equal(PinType.Passive, p.Type));
    }

    [Fact]
    public void Resistor_PadGeometry_MatchesTheFileExactly()
    {
        // The coordinates are STATED in the file, so this is exact, not a tolerance.
        var footprint = KiCadFootprintReader.Read(KiCadFixtures.ResistorMod).Footprint;
        Assert.Equal("R_0805_2012Metric", footprint.Name);
        Assert.Equal(2, footprint.Pads.Count);

        var pad1 = footprint.Pads[0];
        Assert.Equal("1", pad1.Number);
        Assert.Equal(-0.9125, pad1.Center.X);
        Assert.Equal(0.0, pad1.Center.Y);
        Assert.Equal(1.025, pad1.Width);
        Assert.Equal(1.4, pad1.Height);
        Assert.Equal(PadShape.RoundedRectangle, pad1.Shape);
        Assert.Equal(PadKind.Smd, pad1.Kind);
        Assert.Equal(0.9125, footprint.Pads[1].Center.X);
    }

    [Fact]
    public void Resistor_Symbol_MatchesTheFile()
    {
        var symbol = KiCadSymbolReader.Read(KiCadFixtures.ResistorSym).Symbol;

        // one body rectangle, its two extreme corners.
        var rect = Assert.IsType<SymbolRectangle>(Assert.Single(symbol.Graphics));
        Assert.Equal(new Vector2d(-1.016, -2.54), rect.Min);
        Assert.Equal(new Vector2d(1.016, 2.54), rect.Max);

        // pin 1: anchor above the body, pointing down, length 1.27, meeting the body at y=2.54.
        var pin1 = symbol.PinNumbered("1");
        Assert.Equal(new Vector2d(0, 3.81), pin1.Anchor);   // where a wire lands
        Assert.Equal(SymbolPinDirection.Down, pin1.Direction);
        Assert.Equal(1.27, pin1.Length);
        Assert.Equal(new Vector2d(0, 2.54), pin1.Inner);
        Assert.Equal(PinType.Passive, pin1.Type);

        var pin2 = symbol.PinNumbered("2");
        Assert.Equal(new Vector2d(0, -3.81), pin2.Anchor);
        Assert.Equal(SymbolPinDirection.Up, pin2.Direction);
    }

    [Fact]
    public void Resistor_ReferencesItsFootprintByName()
    {
        var symbol = KiCadSymbolReader.Read(KiCadFixtures.ResistorSym);
        Assert.Equal("Resistor_SMD:R_0805_2012Metric", symbol.FootprintName);
    }

    // ---- the SOIC-8: eight pins, the identity holds -------------------------

    [Fact]
    public void Soic8_ArrivesWithEightPins_AllThreeRepresentationsAgree()
    {
        var part = ComponentLibrary.Read(KiCadFixtures.Soic8Sym, KiCadFixtures.Soic8Mod);

        Assert.Equal("U", part.Definition.ReferencePrefix);
        Assert.Equal(8, part.Definition.Pins.Count);
        Assert.Equal(8, part.Definition.Symbol!.Pins.Count);
        Assert.Equal(8, part.Definition.Footprint!.Pads.Count);
        Assert.Equal(
            new[] { "1", "2", "3", "4", "5", "6", "7", "8" },
            part.Definition.Pins.Select(p => p.Number));

        // The one-declaration identity: symbol pin N == pad N == netlist pin N.
        Assert.True(part.Identity.Ok, part.Identity.ToString());
    }

    [Fact]
    public void Soic8_PinNamesAndTypes_MapFromKiCad()
    {
        var part = ComponentLibrary.Read(KiCadFixtures.Soic8Sym, KiCadFixtures.Soic8Mod);
        var pins = part.Definition.Pins;

        Assert.Equal("IN1", pins[0].Name);
        Assert.Equal(PinType.Input, pins[0].Type);
        Assert.Equal("GND", pins[3].Name);
        Assert.Equal(PinType.Power, pins[3].Type);   // power_in -> Power
        Assert.Equal("OUT4", pins[4].Name);
        Assert.Equal(PinType.Output, pins[4].Type);
        Assert.Equal("VCC", pins[7].Name);
        Assert.Equal(PinType.Power, pins[7].Type);
    }

    [Fact]
    public void Soic8_LeftAndRightPins_AnchorOnTheCorrectSide()
    {
        var symbol = KiCadSymbolReader.Read(KiCadFixtures.Soic8Sym).Symbol;

        // Left pin 1: anchor on the left (x=-7.62), pointing RIGHT into the body edge x=-5.08.
        var left = symbol.PinNumbered("1");
        Assert.Equal(new Vector2d(-7.62, 3.81), left.Anchor);
        Assert.Equal(SymbolPinDirection.Right, left.Direction);
        Assert.Equal(new Vector2d(-5.08, 3.81), left.Inner);

        // Right pin 5: anchor on the right (x=7.62), pointing LEFT into the body edge x=5.08.
        var right = symbol.PinNumbered("5");
        Assert.Equal(new Vector2d(7.62, -3.81), right.Anchor);
        Assert.Equal(SymbolPinDirection.Left, right.Direction);
        Assert.Equal(new Vector2d(5.08, -3.81), right.Inner);

        // The body rectangle and the "IC" text label both survive.
        Assert.Contains(symbol.Graphics, g => g is SymbolRectangle);
        var text = Assert.IsType<SymbolText>(symbol.Graphics.First(g => g is SymbolText));
        Assert.Equal("IC", text.Value);
        Assert.Equal(1.27, text.Size);
    }

    // ---- the mismatch fixture is REPORTED by name ---------------------------

    [Fact]
    public void Soic8_WithAWrongFootprint_ReportsTheOffendingPinsByName()
    {
        // The footprint is missing pad "8" and carries an extra pad "99".
        var part = ComponentLibrary.Read(KiCadFixtures.Soic8Sym, KiCadFixtures.Soic8ModMissingPad8);

        Assert.False(part.Identity.Ok);
        Assert.Contains("8", part.Identity.PinsWithoutPad);        // pin 8 has a symbol pin but no pad
        Assert.Contains("8", part.Identity.SymbolPinsWithoutPad);
        Assert.Contains("99", part.Identity.PadsNotAPin);         // pad 99 is not a pin
        Assert.Contains(part.Identity.Messages, m => m.Contains("'8'"));
        Assert.Contains(part.Identity.Messages, m => m.Contains("'99'"));
    }

    // ---- the through-hole footprint: drill carried, pin-1 rectangular --------

    [Fact]
    public void ThroughHoleConnector_CarriesTheDrillAndPadKind()
    {
        var footprint = KiCadFootprintReader.Read(KiCadFixtures.ConnectorMod).Footprint;
        var pad1 = footprint.Pads[0];
        Assert.Equal(PadKind.ThroughHole, pad1.Kind);
        Assert.Equal(PadShape.Rectangular, pad1.Shape);   // pin-1 rect convention
        Assert.Equal(1.0, pad1.DrillDiameter);
        Assert.True(pad1.IsDrilled);
        Assert.Equal(0.35, pad1.AnnularRing, 12);         // (1.7 - 1.0) / 2
        Assert.Equal(PadShape.Round, footprint.Pads[1].Shape);
    }

    // ---- diagnostics NAME what the reader could not carry -------------------

    [Fact]
    public void ExoticSymbol_NamesTheGraphicsAndPinFeaturesItIgnored()
    {
        var symbol = KiCadSymbolReader.Read(KiCadFixtures.ExoticSym);

        // The bezier graphic and the alternate pin function are ignored WITH a named note.
        Assert.Contains(symbol.Diagnostics, d => d.Contains("bezier"));
        Assert.Contains(symbol.Diagnostics, d => d.Contains("alternate"));
        // no_connect has no exact PinType — noted, mapped to Unspecified.
        Assert.Contains(symbol.Diagnostics, d => d.Contains("no_connect"));
        Assert.Equal(PinType.Unspecified, symbol.Symbol.PinNumbered("1").Type);
        // The bezier drew no graphic we model; the two pins still arrived.
        Assert.Empty(symbol.Symbol.Graphics);
        Assert.Equal(2, symbol.Symbol.Pins.Count);
    }

    // ---- determinism --------------------------------------------------------

    [Fact]
    public void Read_IsDeterministic()
    {
        var a = ComponentLibrary.Read(KiCadFixtures.Soic8Sym, KiCadFixtures.Soic8Mod);
        var b = ComponentLibrary.Read(KiCadFixtures.Soic8Sym, KiCadFixtures.Soic8Mod);

        // Two reads produce identical structure — asserted via the byte-exact save.
        var s1 = SchematicWith(a.Definition).Save();
        var s2 = SchematicWith(b.Definition).Save();
        Assert.Equal(s1, s2);
    }

    // ---- malformed input is refused BY NAME ---------------------------------

    [Fact]
    public void NotASymbolLibrary_IsRefusedByName()
    {
        var ex = Assert.Throws<FormatException>(() => KiCadSymbolReader.Read(KiCadFixtures.ResistorMod));
        Assert.Contains("kicad_symbol_lib", ex.Message);
    }

    [Fact]
    public void NotAFootprint_IsRefusedByName()
    {
        var ex = Assert.Throws<FormatException>(() => KiCadFootprintReader.Read(KiCadFixtures.ResistorSym));
        Assert.Contains("footprint", ex.Message);
    }

    [Fact]
    public void AnAbsentNamedSymbol_IsRefusedByName()
    {
        var ex = Assert.Throws<FormatException>(() =>
            KiCadSymbolReader.Read(KiCadFixtures.ResistorSym, "NO_SUCH_SYMBOL"));
        Assert.Contains("NO_SUCH_SYMBOL", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("(kicad_symbol_lib")]                 // unbalanced '('
    [InlineData("(kicad_symbol_lib \"unterminated")]  // unterminated string
    [InlineData("not-a-list")]                        // top-level atom
    public void MalformedSExpression_IsRefusedByName(string text) =>
        Assert.Throws<FormatException>(() => KiCadSymbolReader.Read(text));

    private static Schematic SchematicWith(PartDefinition definition)
    {
        var sch = new Schematic("t");
        sch.Add("X1", definition);
        return sch;
    }
}
