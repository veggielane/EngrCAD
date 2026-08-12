using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// IPC-D-356A CONDUCTOR records (op 378) — the opt-in conductor topology beside the access-point
/// netlist. The bar is the same twin-decoder round trip: a conductor written and read back reproduces
/// its net, layer, width and full centre-line path EXACTLY (the mutation with teeth — a writer that
/// dropped a midpoint would fail), the file is a byte fixed point, and — critically — with conductors
/// OFF the output is byte-identical to the access-point-only netlist, so the feature adds nothing a
/// caller did not ask for.
/// </summary>
public sealed class PcbIpc356ConductorTests
{
    private static PartDefinition Res2() => new(
        "R", "R",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
        new Footprint("R", [
            Pad.Smd("1", new Vector2d(-1.0, 0), 1.2, 1.4),
            Pad.Smd("2", new Vector2d(1.0, 0), 1.2, 1.4),
        ]));

    // A routed two-net board: VCC on a 3-point (bent) 0.25 mm trace, SIG on a 2-point 0.2 mm trace.
    private static PcbLayout Routed(string sig = "SIG")
    {
        var sch = new Schematic("cnd");
        var r = sch.Add("R1", Res2());
        var u = sch.Add("U1", Res2());
        sch.Connect("VCC", r.Pin("1"), u.Pin("1"));
        sch.Connect(sig, r.Pin("2"), u.Pin("2"));
        var board = PcbBoard.Rectangle(50, 40, 1.6);
        var layout = new PcbLayout(sch, board);
        layout.Place("R1", -10, 0);
        layout.Place("U1", 10, 0);
        string top = layout.Board.Stackup.Coppers[0].Name;
        layout.AddTrace("VCC", top, 0.25, [new Vector2d(-9, 0.5), new Vector2d(0, 4), new Vector2d(9, 0.5)]);
        layout.AddTrace(sig, top, 0.20, [new Vector2d(-9, -0.5), new Vector2d(9, -0.5)]);
        return layout;
    }

    private static void AssertSame(Ipc356Conductor a, Ipc356Conductor b)
    {
        Assert.Equal(a.Net, b.Net);
        Assert.Equal(a.Access, b.Access);
        Assert.Equal(a.WidthUm, b.WidthUm);
        Assert.Equal(a.Path, b.Path);   // Ipc356PathPoint is a value type → element-wise
    }

    // ==== 1) ComputeConductors: one per trace, right net/layer/width/path ========

    [Fact]
    public void ComputeConductors_IsOnePerTrace_WithNetLayerWidthAndPath()
    {
        var conductors = PcbIpc356.ComputeConductors(Routed());
        Assert.Equal(2, conductors.Count);

        var vcc = conductors.Single(c => c.Net == "VCC");
        Assert.Equal(1, vcc.Access);                 // a top-layer trace on a 2-layer board is layer 1
        Assert.Equal(250, vcc.WidthUm);              // 0.25 mm
        Assert.Equal(3, vcc.Path.Count);
        Assert.Equal(new Ipc356PathPoint(-9000, 500), vcc.Path[0]);
        Assert.Equal(new Ipc356PathPoint(0, 4000), vcc.Path[1]);

        var sig = conductors.Single(c => c.Net == "SIG");
        Assert.Equal(200, sig.WidthUm);
        Assert.Equal(2, sig.Path.Count);
    }

    // ==== 2) the twin-decoder round trip reproduces every conductor exactly ======

    [Fact]
    public void Conductors_RoundTripThroughWriteAndParse_Exactly()
    {
        var layout = Routed();
        var computed = PcbIpc356.ComputeConductors(layout);
        var parsed = PcbIpc356.ParseConductors(PcbIpc356.Write(layout, includeConductors: true));

        Assert.Equal(computed.Count, parsed.Count);
        foreach (var c in computed)
            AssertSame(c, parsed.Single(p => p.Net == c.Net));

        // The written file re-reads and re-writes byte for byte (a fixed point).
        string once = PcbIpc356.Write(layout, includeConductors: true);
        var file = PcbIpc356.ParseFile(once);
        string twice = PcbIpc356.Write(file.AccessPoints, file.Conductors, "cnd");
        Assert.Equal(once, twice);
    }

    // ==== 3) conductors OFF is byte-identical to the access-point netlist ========

    [Fact]
    public void WithConductorsOff_TheOutputIsByteIdenticalToTheAccessPointNetlist()
    {
        var layout = Routed();
        Assert.Equal(PcbIpc356.Write(layout), PcbIpc356.Write(layout, includeConductors: false));
        Assert.DoesNotContain("\n378 ", PcbIpc356.Write(layout));
        Assert.Contains("\n378 ", PcbIpc356.Write(layout, includeConductors: true));

        // And the ACCESS-POINT half is unchanged whether or not conductors ride along — a file with
        // conductors still parses its access points identically.
        var withoutC = PcbIpc356.Parse(PcbIpc356.Write(layout));
        var withC = PcbIpc356.ParseFile(PcbIpc356.Write(layout, includeConductors: true)).AccessPoints;
        Assert.Equal(withoutC.Count, withC.Count);
    }

    // ==== 4) an over-width net rides a 379 continuation and reconstructs =========

    [Fact]
    public void AnOverWidthConductorNet_RidesA379ContinuationAndReconstructs()
    {
        // A net longer than the 14-char fixed field.
        var layout = Routed(sig: "A_VERY_LONG_SIGNAL_NAME");
        string text = PcbIpc356.Write(layout, includeConductors: true);
        Assert.Contains("379 NA_VERY_LONG_SIGNAL_NAME", text);   // an N token, tag then value

        var parsed = PcbIpc356.ParseConductors(text);
        Assert.Contains(parsed, c => c.Net == "A_VERY_LONG_SIGNAL_NAME");
    }

    // ==== 5) determinism ========================================================

    [Fact]
    public void ConductorEmissionIsDeterministic()
    {
        var layout = Routed();
        Assert.Equal(
            PcbIpc356.Write(layout, includeConductors: true),
            PcbIpc356.Write(layout, includeConductors: true));
    }

    // ==== 6) malformed 378 records are refused by name ==========================

    [Theory]
    [InlineData("A01 W000200 X-009000 Y+000500", "fewer than two")]                       // one point
    [InlineData("A01 W000200 X-009000 Y+000500 X+009000", "mismatched")]                   // 2 X, 1 Y
    [InlineData("W000200 X-009000 Y+000500 X+009000 Y-000500", "access (A) or width")]     // no A
    [InlineData("A01 X-009000 Y+000500 X+009000 Y-000500", "access (A) or width")]         // no W
    [InlineData("A01 W000200 Z+000000 X-009000 Y+000500 X+009000 Y-000500", "unknown token")]
    public void AMalformedConductorRecord_IsRefusedByName(string tokens, string reason)
    {
        // Build the record with a correctly-padded 14-char net field, so only the token stream varies.
        string record = "378 " + "NET".PadRight(14) + " " + tokens;
        string file = "P  UNITS CUST 2\n" + record + "\n999\n";
        var ex = Assert.Throws<FormatException>(() => PcbIpc356.ParseConductors(file));
        Assert.Contains(reason, ex.Message);
    }
}
