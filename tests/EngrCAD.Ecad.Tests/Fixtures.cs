using EngrCAD.Core;
using EngrCAD.Ecad;

namespace EngrCAD.Ecad.Tests;

/// <summary>Reusable part definitions and a canonical little schematic for the tests.</summary>
internal static class Fixtures
{
    internal static PartDefinition Resistor() => new(
        "R_0805", "R",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
        new Footprint("R0805", [
            new Pad("1", new Vector2d(-1.0, 0), 1.2, 1.4, PadShape.Rectangular),
            new Pad("2", new Vector2d(1.0, 0), 1.2, 1.4, PadShape.RoundedRectangle),
        ]));

    internal static PartDefinition Led() => new(
        "LED_3MM", "D",
        [new Pin("A", "anode", PinType.Passive), new Pin("K", "cathode", PinType.Passive)]);

    internal static PartDefinition Battery() => new(
        "BATT_CR2032", "BT",
        [new Pin("+", "V+", PinType.Power), new Pin("-", "V-", PinType.Ground)]);

    /// <summary>An 8-pin op-amp with a genuine no-connect pin (5).</summary>
    internal static PartDefinition OpAmp() => new(
        "OPAMP_SO8", "U",
        [
            new Pin("1", "OUTA", PinType.Output),
            new Pin("2", "INA-", PinType.Input),
            new Pin("3", "INA+", PinType.Input),
            new Pin("4", "V-", PinType.Ground),
            new Pin("5", "NC", PinType.Unspecified),
            new Pin("6", "OUTB", PinType.Output),
            new Pin("7", "INB-", PinType.Input),
            new Pin("8", "V+", PinType.Power),
        ]);

    /// <summary>A clean LED-indicator schematic: battery → resistor → LED → battery.</summary>
    internal static Schematic LedIndicator()
    {
        var sch = new Schematic("LED indicator");
        var r = sch.Add("R1", Resistor(), value: "330");
        var d = sch.Add("D1", Led());
        var bt = sch.Add("BT1", Battery());
        sch.Connect("VCC", bt.Pin("+"), r.Pin("1"));
        sch.Connect("LED_A", r.Pin("2"), d.Pin("A"));
        sch.Connect("GND", d.Pin("K"), bt.Pin("-"));
        return sch;
    }
}
