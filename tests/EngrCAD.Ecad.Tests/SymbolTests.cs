using EngrCAD.Core;
using EngrCAD.Ecad;
using Xunit;

namespace EngrCAD.Ecad.Tests;

public class SymbolTests
{
    // ---- SymbolPin: the anchor is where a wire lands, direction points into body -

    [Fact]
    public void SymbolPin_Inner_IsAnchorPlusLengthAlongDirection()
    {
        // A KiCad resistor pin 1: anchor above the body, pointing DOWN into it.
        var pin = new SymbolPin("1", "", new Vector2d(0, 3.81), SymbolPinDirection.Down, 1.27,
            PinType.Passive);
        Assert.Equal(new Vector2d(0, 3.81), pin.Anchor);   // the wire lands here
        Assert.Equal(new Vector2d(0, 2.54), pin.Inner);    // meets the body here
    }

    [Theory]
    [InlineData(0, SymbolPinDirection.Right)]
    [InlineData(90, SymbolPinDirection.Up)]
    [InlineData(180, SymbolPinDirection.Left)]
    [InlineData(270, SymbolPinDirection.Down)]
    [InlineData(360, SymbolPinDirection.Right)]
    [InlineData(-90, SymbolPinDirection.Down)]
    public void Direction_FromDegrees_MatchesKiCadAngle(double degrees, SymbolPinDirection expected) =>
        Assert.Equal(expected, SymbolPinDirectionExtensions.FromDegrees(degrees));

    [Theory]
    [InlineData(SymbolPinDirection.Right, 1, 0)]
    [InlineData(SymbolPinDirection.Up, 0, 1)]
    [InlineData(SymbolPinDirection.Left, -1, 0)]
    [InlineData(SymbolPinDirection.Down, 0, -1)]
    public void Direction_ToVector_IsTheUnitVector(SymbolPinDirection dir, double x, double y) =>
        Assert.Equal(new Vector2d(x, y), dir.ToVector());

    // ---- Symbol: construction, lookup, duplicate refusal --------------------

    [Fact]
    public void Symbol_LooksUpPinsByNumber_AndReportsTheSet()
    {
        var symbol = new Symbol("S",
            [
                new SymbolPin("1", "A", new Vector2d(-5, 0), SymbolPinDirection.Right, 2, PinType.Passive),
                new SymbolPin("2", "K", new Vector2d(5, 0), SymbolPinDirection.Left, 2, PinType.Passive),
            ],
            [new SymbolRectangle(new Vector2d(-2, -2), new Vector2d(2, 2))]);

        Assert.Equal(new[] { "1", "2" }, symbol.PinNumbers);
        Assert.True(symbol.HasPin("1"));
        Assert.Equal("K", symbol.PinNumbered("2").Name);
        Assert.Single(symbol.Graphics);
    }

    [Fact]
    public void Symbol_RefusesADuplicatePinNumber()
    {
        var ex = Assert.Throws<ArgumentException>(() => new Symbol("S",
        [
            new SymbolPin("1", "", default, SymbolPinDirection.Right, 1, PinType.Passive),
            new SymbolPin("1", "", default, SymbolPinDirection.Left, 1, PinType.Passive),
        ]));
        Assert.Contains("two pins numbered '1'", ex.Message);
    }

    [Fact]
    public void Symbol_RefusesAnEmptyName() =>
        Assert.Throws<ArgumentException>(() => new Symbol("", []));

    // ---- PartDefinition gains an optional Symbol (additive) -----------------

    [Fact]
    public void PartDefinition_WithoutASymbol_IsUnchanged()
    {
        var d = new PartDefinition("R", "R", [new Pin("1"), new Pin("2")]);
        Assert.Null(d.Symbol);   // the incumbent 3-arg construction is byte-for-byte unchanged
    }

    [Fact]
    public void PartDefinition_CarriesTheSymbol()
    {
        var symbol = new Symbol("R",
        [
            new SymbolPin("1", "", new Vector2d(0, 3.81), SymbolPinDirection.Down, 1.27, PinType.Passive),
            new SymbolPin("2", "", new Vector2d(0, -3.81), SymbolPinDirection.Up, 1.27, PinType.Passive),
        ]);
        var d = new PartDefinition("R", "R", [new Pin("1"), new Pin("2")], symbol: symbol);
        Assert.Same(symbol, d.Symbol);
    }

    // ---- PinIdentity: the symbol == footprint == netlist identity -----------

    [Fact]
    public void PinIdentity_Holds_WhenAllThreeAgree()
    {
        var d = new PartDefinition("R", "R",
            [new Pin("1"), new Pin("2")],
            footprint: new Footprint("F",
            [
                new Pad("1", new Vector2d(-1, 0), 1, 1, PadShape.Rectangular),
                new Pad("2", new Vector2d(1, 0), 1, 1, PadShape.Rectangular),
            ]),
            symbol: new Symbol("R",
            [
                new SymbolPin("1", "", default, SymbolPinDirection.Down, 1, PinType.Passive),
                new SymbolPin("2", "", default, SymbolPinDirection.Up, 1, PinType.Passive),
            ]));

        var report = PinIdentity.Check(d);
        Assert.True(report.Ok, report.ToString());
    }

    [Fact]
    public void PinIdentity_NamesASymbolPinWithNoPad_AndAPadWithNoPin()
    {
        // Pins 1,2,3. Symbol draws 1,2,3. Footprint has pads 1,2 and an extra 9.
        var d = new PartDefinition("U", "U",
            [new Pin("1"), new Pin("2"), new Pin("3")],
            footprint: new Footprint("F",
            [
                new Pad("1", default, 1, 1, PadShape.Rectangular),
                new Pad("2", default, 1, 1, PadShape.Rectangular),
                new Pad("9", default, 1, 1, PadShape.Rectangular),
            ]),
            symbol: new Symbol("U",
            [
                new SymbolPin("1", "", default, SymbolPinDirection.Right, 1, PinType.Passive),
                new SymbolPin("2", "", default, SymbolPinDirection.Right, 1, PinType.Passive),
                new SymbolPin("3", "", default, SymbolPinDirection.Right, 1, PinType.Passive),
            ]));

        var report = PinIdentity.Check(d);
        Assert.False(report.Ok);
        // Pin "3" has a symbol pin but no pad.
        Assert.Contains("3", report.PinsWithoutPad);
        Assert.Contains("3", report.SymbolPinsWithoutPad);
        // Pad "9" is not a pin at all.
        Assert.Contains("9", report.PadsNotAPin);
        Assert.Contains("9", report.PadsWithoutSymbolPin);
        // The messages name the offending numbers.
        Assert.Contains(report.Messages, m => m.Contains("'3'") && m.Contains("pad"));
        Assert.Contains(report.Messages, m => m.Contains("'9'"));
    }

    [Fact]
    public void PinIdentity_NamesAPinWithNeitherSymbolNorPad()
    {
        var d = new PartDefinition("U", "U",
            [new Pin("1"), new Pin("2"), new Pin("7")],
            footprint: new Footprint("F",
            [
                new Pad("1", default, 1, 1, PadShape.Rectangular),
                new Pad("2", default, 1, 1, PadShape.Rectangular),
            ]),
            symbol: new Symbol("U",
            [
                new SymbolPin("1", "", default, SymbolPinDirection.Right, 1, PinType.Passive),
                new SymbolPin("2", "", default, SymbolPinDirection.Right, 1, PinType.Passive),
            ]));

        var report = PinIdentity.Check(d);
        Assert.False(report.Ok);
        Assert.Contains("7", report.PinsWithoutSymbolPin);
        Assert.Contains("7", report.PinsWithoutPad);
    }

    [Fact]
    public void PinIdentity_OnlyChecksThePresentRepresentations()
    {
        // Pins + symbol, no footprint: the footprint cross-checks are vacuous, identity holds.
        var d = new PartDefinition("R", "R",
            [new Pin("1"), new Pin("2")],
            symbol: new Symbol("R",
            [
                new SymbolPin("1", "", default, SymbolPinDirection.Down, 1, PinType.Passive),
                new SymbolPin("2", "", default, SymbolPinDirection.Up, 1, PinType.Passive),
            ]));

        var report = PinIdentity.Check(d);
        Assert.True(report.Ok);
        Assert.Empty(report.PinsWithoutPad);        // no footprint -> not cross-checked
        Assert.Empty(report.SymbolPinsWithoutPad);
    }
}
