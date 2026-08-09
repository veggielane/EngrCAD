using EngrCAD.Core;
using EngrCAD.Ecad;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad.Tests;

/// <summary>Reusable stage-2 part definitions (with bodies and board footprints), and a small
/// board/layout the placement and persistence tests share.</summary>
internal static class PcbFixtures
{
    /// <summary>A 2-pin SMD resistor with a body (modelled +Z out of the board, seated at
    /// z = 0) and a two-pad SMD footprint whose pad numbers match the pins.</summary>
    internal static PartDefinition SmdResistor() => new(
        "R_0805", "R",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
        new Footprint("R0805", [
            Pad.Smd("1", new Vector2d(-1.0, 0), 1.2, 1.4),
            Pad.Smd("2", new Vector2d(1.0, 0), 1.2, 1.4),
        ]),
        body: () => Shape.Box(2.0, 1.25, 0.5).Translate(0, 0, 0.25));

    /// <summary>A 2-pin through-hole part (a header): a body and a two-pad through-hole
    /// footprint that drills the board.</summary>
    internal static PartDefinition ThroughHoleHeader() => new(
        "HDR_1x2", "J",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
        new Footprint("HDR254", [
            Pad.ThroughHole("1", new Vector2d(-1.27, 0), pad: 1.6, drill: 0.9),
            Pad.ThroughHole("2", new Vector2d(1.27, 0), pad: 1.6, drill: 0.9),
        ]),
        body: () => Shape.Box(5.08, 2.54, 3.0).Translate(0, 0, 1.5));

    /// <summary>A clean two-part schematic: an SMD resistor and a through-hole header, wired.</summary>
    internal static Schematic Circuit()
    {
        var sch = new Schematic("blinky");
        var r = sch.Add("R1", SmdResistor(), "330");
        var j = sch.Add("J1", ThroughHoleHeader());
        sch.Connect("VCC", j.Pin("1"), r.Pin("1"));
        sch.Connect("SIG", r.Pin("2"), j.Pin("2"));
        return sch;
    }

    /// <summary>A 50 × 40 × 1.6 board with two Ø3 mounting holes.</summary>
    internal static PcbBoard Board() => new(
        [
            new Vector2d(-25, -20), new Vector2d(25, -20),
            new Vector2d(25, 20), new Vector2d(-25, 20),
        ],
        thickness: 1.6,
        holes: [
            new BoardHole(new Vector2d(-22, -17), 3.0, BoardHoleKind.Mounting),
            new BoardHole(new Vector2d(22, 17), 3.0, BoardHoleKind.Mounting),
        ]);

    /// <summary>A clean layout: the circuit placed on the board, R1 top and J1 top.</summary>
    internal static PcbLayout Layout()
    {
        var layout = new PcbLayout(Circuit(), Board());
        layout.Place("R1", 5, 0, rotationDegrees: 0, side: CopperSide.Top);
        layout.Place("J1", -8, 4, rotationDegrees: 90, side: CopperSide.Top);
        return layout;
    }

    internal static PartLibrary Library() => new PartLibrary()
        .Register("R_0805", () => Shape.Box(2.0, 1.25, 0.5).Translate(0, 0, 0.25))
        .Register("HDR_1x2", () => Shape.Box(5.08, 2.54, 3.0).Translate(0, 0, 1.5));
}
