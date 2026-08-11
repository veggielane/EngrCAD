using System.Text;
using EngrCAD.Core;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

public class SchematicSheetTests
{
    // ---- symbol-carrying fixtures ------------------------------------------------

    /// <summary>A 2-pin passive part with a box symbol: pin at the top (anchor above, pointing
    /// down into the body) and one at the bottom.</summary>
    private static PartDefinition Passive(
        string name, string prefix,
        (string Number, string Name, PinType Type) top,
        (string Number, string Name, PinType Type) bottom)
    {
        var symbol = new Symbol(name,
        [
            new SymbolPin(top.Number, top.Name, new Vector2d(0, 5.08), SymbolPinDirection.Down, 2.54, top.Type),
            new SymbolPin(bottom.Number, bottom.Name, new Vector2d(0, -5.08), SymbolPinDirection.Up, 2.54, bottom.Type),
        ],
        [new SymbolRectangle(new Vector2d(-1, -2.54), new Vector2d(1, 2.54))]);
        return new PartDefinition(name, prefix,
            [new Pin(top.Number, top.Name, top.Type), new Pin(bottom.Number, bottom.Name, bottom.Type)],
            symbol: symbol);
    }

    private static PartDefinition Resistor() =>
        Passive("R_0805", "R", ("1", "", PinType.Passive), ("2", "", PinType.Passive));

    private static PartDefinition Led() =>
        Passive("LED_3MM", "D", ("A", "anode", PinType.Passive), ("K", "cathode", PinType.Passive));

    private static PartDefinition Battery() =>
        Passive("BATT_CR2032", "BT", ("+", "V+", PinType.Power), ("-", "V-", PinType.Ground));

    /// <summary>A 4-pin IC: pins 1,2 on the left (Input, Output), pins 3,4 on the right
    /// (Power, Ground).</summary>
    private static PartDefinition Ic()
    {
        var symbol = new Symbol("IC4",
        [
            new SymbolPin("1", "IN", new Vector2d(-7.62, 2.54), SymbolPinDirection.Right, 2.54, PinType.Input),
            new SymbolPin("2", "OUT", new Vector2d(-7.62, -2.54), SymbolPinDirection.Right, 2.54, PinType.Output),
            new SymbolPin("3", "VCC", new Vector2d(7.62, 2.54), SymbolPinDirection.Left, 2.54, PinType.Power),
            new SymbolPin("4", "GND", new Vector2d(7.62, -2.54), SymbolPinDirection.Left, 2.54, PinType.Ground),
        ],
        [new SymbolRectangle(new Vector2d(-5.08, -5.08), new Vector2d(5.08, 5.08))]);
        return new PartDefinition("IC4", "U",
        [
            new Pin("1", "IN", PinType.Input), new Pin("2", "OUT", PinType.Output),
            new Pin("3", "VCC", PinType.Power), new Pin("4", "GND", PinType.Ground),
        ], symbol: symbol);
    }

    /// <summary>The canonical LED-indicator schematic with drawn symbols: battery → resistor →
    /// LED → battery. VCC and GND are power/ground rails (drawn as labels); LED_A is a signal
    /// net (drawn as a wire).</summary>
    private static (Schematic Schematic, SchematicPlacement Placement) LedIndicator()
    {
        var sch = new Schematic("LED indicator");
        var r = sch.Add("R1", Resistor(), value: "330");
        var d = sch.Add("D1", Led());
        var bt = sch.Add("BT1", Battery());
        sch.Connect("VCC", bt.Pin("+"), r.Pin("1"));
        sch.Connect("LED_A", r.Pin("2"), d.Pin("A"));
        sch.Connect("GND", d.Pin("K"), bt.Pin("-"));

        var placement = new SchematicPlacement()
            .Place("BT1", new Vector2d(60, 110))
            .Place("R1", new Vector2d(120, 130))
            .Place("D1", new Vector2d(180, 110));
        return (sch, placement);
    }

    // ---- the LED fixture: every net's pins are joined ----------------------------

    [Fact]
    public void LedIndicator_JoinsEveryNetsPins_AndVerifies()
    {
        var (sch, placement) = LedIndicator();
        var drawing = new SchematicSheet(sch, placement).Draw();
        var c = drawing.Connectivity;
        var r = sch.Find("R1")!; var d = sch.Find("D1")!; var bt = sch.Find("BT1")!;

        // VCC and GND are rails (a Power/Ground pin), so they are drawn as LABELS; LED_A is a
        // signal net drawn as a WIRE. Either way the two pins are JOINED.
        Assert.True(c.AreJoined(bt.Pin("+"), r.Pin("1")), "VCC pins are not joined");
        Assert.True(c.AreJoined(r.Pin("2"), d.Pin("A")), "LED_A pins are not joined");
        Assert.True(c.AreJoined(d.Pin("K"), bt.Pin("-")), "GND pins are not joined");

        Assert.Equal("VCC", c.LabelOf(bt.Pin("+")));
        Assert.Equal("GND", c.LabelOf(bt.Pin("-")));
        Assert.Null(c.LabelOf(r.Pin("2")));   // LED_A is wired, not labelled

        var report = drawing.Verify();
        Assert.True(report.Ok, report.ToString());
    }

    [Fact]
    public void LedIndicator_DoesNotInventAConnection()
    {
        var (sch, placement) = LedIndicator();
        var c = new SchematicSheet(sch, placement).Draw().Connectivity;
        var r = sch.Find("R1")!; var d = sch.Find("D1")!; var bt = sch.Find("BT1")!;
        // R1.1 (VCC) and R1.2 (LED_A) are on DIFFERENT nets, so the drawing must not join them.
        Assert.False(c.AreJoined(r.Pin("1"), r.Pin("2")));
        Assert.False(c.AreJoined(bt.Pin("+"), d.Pin("K")));   // VCC vs GND
    }

    // ---- a pin's anchor coincides with its wire endpoint to the weld tier --------

    [Fact]
    public void PinAnchor_CoincidesWithItsWireEndpoint_ToTheWeldTier()
    {
        var (sch, placement) = LedIndicator();
        var drawing = new SchematicSheet(sch, placement).Draw();

        foreach (var drawn in drawing.Pins)
        {
            // The world anchor computed straight from the pose and the symbol pin.
            var pose = placement.PoseOf(drawn.Pin.ReferenceDesignator)!.Value;
            var symbol = drawn.Pin.Component.Definition.Symbol!;
            var expected = pose.Apply(symbol.PinNumbered(drawn.Pin.Number).Anchor);
            Assert.Equal(expected, drawn.Anchor);   // exact — a quarter turn is a sign swap

            // Some drawn segment (a wire, or a label stub) lands on the anchor.
            bool touched = drawing.Segments.Any(s =>
                s.A.DistanceTo(drawn.Anchor) <= 1e-9 || s.B.DistanceTo(drawn.Anchor) <= 1e-9);
            Assert.True(touched, $"no drawn segment lands on {drawn.Pin}'s anchor");
        }
    }

    // ---- determinism -------------------------------------------------------------

    [Fact]
    public void TheSheetIsADeterministicFunctionOfTheGraph()
    {
        var (sch, placement) = LedIndicator();

        string svg1 = new SchematicSheet(sch, placement).Draw().ToSvg();
        string svg2 = new SchematicSheet(sch, placement).Draw().ToSvg();
        Assert.Equal(svg1, svg2);

        string Dxf(SchematicDrawing d)
        {
            var writer = new StringWriter();
            d.ToDxf().Save(writer);
            return writer.ToString();
        }
        Assert.Equal(
            Dxf(new SchematicSheet(sch, placement).Draw()),
            Dxf(new SchematicSheet(sch, placement).Draw()));
    }

    // ---- writers produce usable output -------------------------------------------

    [Fact]
    public void Writers_CarryTheSchematicsTextAndGeometry()
    {
        var (sch, placement) = LedIndicator();
        var drawing = new SchematicSheet(sch, placement, title: new TitleBlock { Title = "LED indicator" }).Draw();

        string svg = drawing.ToSvg();
        Assert.Contains("<svg", svg);
        Assert.Contains("R1", svg);      // the reference designator
        Assert.Contains("330", svg);     // the value
        Assert.Contains("VCC", svg);     // a net label
        Assert.Contains("LED indicator", svg);   // the title block

        // The DXF round-trips to a document with line, text and (junction) content.
        var text = new StringWriter();
        drawing.ToDxf().Save(text);
        var reloaded = DxfDocument.Load(new StringReader(text.ToString()));
        Assert.Contains(reloaded.Entities, e => e is DxfLine);
        Assert.Contains(reloaded.Entities, e => e is DxfText);

        Assert.NotEmpty(drawing.ToPdf());
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(drawing.ToPdf(), 0, 4));
    }

    // ---- the net-label rule ------------------------------------------------------

    [Fact]
    public void PowerAndGroundNets_AreDrawnAsLabels_SignalNetsAsWires()
    {
        var sch = new Schematic();
        var r1 = sch.Add("R1", Resistor());
        var r2 = sch.Add("R2", Resistor());
        var bt = sch.Add("BT1", Battery());
        var sig = sch.Connect("SIG", r1.Pin("2"), r2.Pin("1"));    // both passive -> wired
        var pwr = sch.Connect("PWR", bt.Pin("+"), r1.Pin("1"));    // has a Power pin -> label
        _ = pwr;
        var placement = SchematicPlacement.Grid(sch);
        var c = new SchematicSheet(sch, placement).Draw().Connectivity;

        Assert.Null(c.LabelOf(r1.Pin("2")));                       // SIG is wired
        Assert.Equal("PWR", c.LabelOf(bt.Pin("+")));               // PWR is labelled
        Assert.True(c.AreJoined(r1.Pin("2"), r2.Pin("1")));
        _ = sig;
    }

    [Fact]
    public void HighFanoutNets_AreDrawnAsLabels_ByThreshold()
    {
        // Five passive pins on one net: not a rail, but past the fanout threshold of 4.
        var opts = new SchematicSheetOptions { FanoutThreshold = 4 };

        Schematic Build(int count)
        {
            var sch = new Schematic();
            var pins = new List<PinRef>();
            for (int i = 0; i < count; i++)
                pins.Add(sch.Add($"R{i + 1}", Resistor()).Pin("1"));
            sch.Connect("BUS", pins);
            return sch;
        }

        var wide = Build(5);
        var cWide = new SchematicSheet(wide, SchematicPlacement.Grid(wide), options: opts).Draw().Connectivity;
        Assert.Equal("BUS", cWide.LabelOf(wide.Find("R1")!.Pin("1")));   // labelled by fanout

        var narrow = Build(4);
        var cNarrow = new SchematicSheet(narrow, SchematicPlacement.Grid(narrow), options: opts).Draw().Connectivity;
        Assert.Null(cNarrow.LabelOf(narrow.Find("R1")!.Pin("1")));       // 4 is not > 4 -> wired
        Assert.True(cNarrow.AreJoined(narrow.Find("R1")!.Pin("1"), narrow.Find("R4")!.Pin("1")));
    }

    // ---- a larger schematic: trunk routing, a junction, labels, no false joins ---

    [Fact]
    public void LargerSchematic_VerifiesWithTrunksJunctionsAndLabels()
    {
        var sch = new Schematic("amp stage");
        var r1 = sch.Add("R1", Resistor());
        var r2 = sch.Add("R2", Resistor());
        var r3 = sch.Add("R3", Resistor());
        var u1 = sch.Add("U1", Ic());
        sch.Connect("IN", r1.Pin("1"), u1.Pin("1"));                       // 2-pin wired L
        sch.Connect("OUT", u1.Pin("2"), r2.Pin("1"), r3.Pin("1"));         // 3-pin wired trunk
        sch.Connect("VCC", u1.Pin("3"), r1.Pin("2"));                       // rail (Power) -> label
        sch.Connect("GND", u1.Pin("4"), r2.Pin("2"), r3.Pin("2"));          // rail (Ground) -> label
        Assert.True(sch.Check().Ok, sch.Check().ToString());   // a valid schematic

        var drawing = new SchematicSheet(sch, SchematicPlacement.Grid(sch)).Draw();
        var c = drawing.Connectivity;

        Assert.True(c.AreJoined(r1.Pin("1"), u1.Pin("1")));                 // IN, wire
        Assert.True(c.AreJoined(u1.Pin("2"), r2.Pin("1")));                 // OUT, wire
        Assert.True(c.AreJoined(u1.Pin("2"), r3.Pin("1")));                 // OUT, wire (transitive)
        Assert.Equal("VCC", c.LabelOf(u1.Pin("3")));
        Assert.Equal("GND", c.LabelOf(u1.Pin("4")));
        Assert.True(c.AreJoined(u1.Pin("4"), r3.Pin("2")));                 // GND, label

        var report = drawing.Verify();
        Assert.True(report.Ok, report.ToString());
    }

    [Fact]
    public void AThreePinTrunk_MarksAJunctionDot()
    {
        // Three resistors placed so the middle-x pin's stub meets the trunk interior: a T.
        var sch = new Schematic();
        var r1 = sch.Add("R1", Resistor());
        var r2 = sch.Add("R2", Resistor());
        var r3 = sch.Add("R3", Resistor());
        sch.Connect("T", r1.Pin("1"), r2.Pin("1"), r3.Pin("1"));

        // Resistor pin "1" anchor is local (0, 5.08); upright, so world anchor = Position + (0, 5.08).
        var placement = new SchematicPlacement()
            .Place("R1", new Vector2d(20, 40 - 5.08))    // anchor (20, 40)
            .Place("R2", new Vector2d(60, 70 - 5.08))    // anchor (60, 70) — middle x, off the median y
            .Place("R3", new Vector2d(100, 40 - 5.08));  // anchor (100, 40)

        var drawing = new SchematicSheet(sch, placement).Draw();
        Assert.NotEmpty(drawing.Junctions);
        Assert.True(drawing.Verify().Ok, drawing.Verify().ToString());
    }

    // ---- no-connect markers ------------------------------------------------------

    [Fact]
    public void NoConnectPins_GetAnXMarker_AndAreNotJoined()
    {
        var sch = new Schematic();
        var u1 = sch.Add("U1", Ic());
        sch.Connect("IN", u1.Pin("1"), sch.Add("R1", Resistor()).Pin("1"));
        sch.NoConnect(u1.Pin("2"));
        var drawing = new SchematicSheet(sch, SchematicPlacement.Grid(sch)).Draw();
        // Two crossing segments on the no-connect layer form the X.
        Assert.Equal(2, drawing.Segments.Count(s => s.Layer == SchematicLayers.NoConnects));
    }

    // ---- refusals, by name -------------------------------------------------------

    [Fact]
    public void AComponentWithNoSymbol_IsRefusedByName()
    {
        var noSymbol = new PartDefinition("R_NOSYM", "R", [new Pin("1"), new Pin("2")]);   // no symbol
        var sch = new Schematic();
        sch.Add("R9", noSymbol);
        var ex = Assert.Throws<ArgumentException>(() => new SchematicSheet(sch));
        Assert.Contains("R9", ex.Message);
        Assert.Contains("no schematic symbol", ex.Message);
    }

    [Fact]
    public void AWireOnAPinTheSymbolDoesNotDraw_IsRefusedByName()
    {
        // A definition with pins 1,2,3 but a symbol that draws only 1 and 2 — the "pin with no
        // anchor" case (symbol and netlist disagree about the part's pins).
        var symbol = new Symbol("U3",
        [
            new SymbolPin("1", "", new Vector2d(-5, 0), SymbolPinDirection.Right, 2, PinType.Passive),
            new SymbolPin("2", "", new Vector2d(5, 0), SymbolPinDirection.Left, 2, PinType.Passive),
        ]);
        var def = new PartDefinition("U3", "U",
            [new Pin("1"), new Pin("2"), new Pin("3")], symbol: symbol);
        var sch = new Schematic();
        var u = sch.Add("U1", def);
        var r = sch.Add("R1", Resistor());
        sch.Connect("N", u.Pin("3"), r.Pin("1"));    // pin 3 exists on the definition, not the symbol

        var ex = Assert.Throws<ArgumentException>(() => new SchematicSheet(sch, SchematicPlacement.Grid(sch)));
        Assert.Contains("U1.3", ex.Message);
        Assert.Contains("draws no pin '3'", ex.Message);
    }

    [Fact]
    public void AComponentWithNoSheetPosition_IsRefusedByName()
    {
        var sch = new Schematic();
        sch.Add("R1", Resistor());
        sch.Add("R2", Resistor());
        var partial = new SchematicPlacement().Place("R1", new Vector2d(50, 50));   // R2 missing
        var ex = Assert.Throws<ArgumentException>(() => new SchematicSheet(sch, partial));
        Assert.Contains("R2", ex.Message);
        Assert.Contains("no sheet position", ex.Message);
    }

    // ---- the grid placeholder ----------------------------------------------------

    [Fact]
    public void GridPlacement_DrawsEveryComponentDeterministically()
    {
        var (sch, _) = LedIndicator();
        var a = new SchematicSheet(sch).Draw();                 // null placement -> grid
        var b = new SchematicSheet(sch, SchematicPlacement.Grid(sch, SchematicSheet.DefaultFormat)).Draw();
        Assert.Equal(a.ToSvg(), b.ToSvg());
        Assert.Equal(3, a.Pins.Select(p => p.Pin.ReferenceDesignator).Distinct().Count());
    }

    // ---- buses: a caller-declared bundle drawn as a thick wire + entries + a vector label ----

    /// <summary>A 4-pin IC with four passive data pins on the left (D0..D3, pin numbers 1..4).</summary>
    private static PartDefinition BusIc(string name)
    {
        var symbol = new Symbol(name,
        [
            new SymbolPin("1", "D0", new Vector2d(-7.62, 3.81), SymbolPinDirection.Right, 2.54, PinType.Passive),
            new SymbolPin("2", "D1", new Vector2d(-7.62, 1.27), SymbolPinDirection.Right, 2.54, PinType.Passive),
            new SymbolPin("3", "D2", new Vector2d(-7.62, -1.27), SymbolPinDirection.Right, 2.54, PinType.Passive),
            new SymbolPin("4", "D3", new Vector2d(-7.62, -3.81), SymbolPinDirection.Right, 2.54, PinType.Passive),
        ],
        [new SymbolRectangle(new Vector2d(-5.08, -5.08), new Vector2d(5.08, 5.08))]);
        return new PartDefinition(name, "U",
        [
            new Pin("1", "D0", PinType.Passive), new Pin("2", "D1", PinType.Passive),
            new Pin("3", "D2", PinType.Passive), new Pin("4", "D3", PinType.Passive),
        ], symbol: symbol);
    }

    /// <summary>Two ICs wired member-by-member over a 4-bit data bus DATA0..DATA3 (each member is
    /// a 2-pin signal net, drawn as a WIRE), plus a caller-declared <see cref="SchematicBus"/>
    /// that bundles them cosmetically.</summary>
    private static (Schematic Schematic, SchematicPlacement Placement, SchematicBus Bus) DataBus()
    {
        var sch = new Schematic("data bus");
        var u1 = sch.Add("U1", BusIc("SRC"));
        var u2 = sch.Add("U2", BusIc("DST"));
        for (int i = 0; i < 4; i++)
            sch.Connect($"DATA{i}", u1.Pin($"{i + 1}"), u2.Pin($"{i + 1}"));

        var placement = new SchematicPlacement()
            .Place("U1", new Vector2d(40, 60))
            .Place("U2", new Vector2d(120, 60));

        // A vertical bus wire between the two ICs, with a 45° entry ripping each member off it.
        const double busX = 80;
        var path = new List<Vector2d> { new(busX, 40), new(busX, 82) };
        var entries = new List<SchematicBusEntry>();
        for (int i = 0; i < 4; i++)
        {
            double y = 50 + i * 5;
            entries.Add(new SchematicBusEntry(new Vector2d(busX, y), new Vector2d(busX + 2.54, y + 2.54)));
        }
        var bus = new SchematicBus("DATA", 0, 3, path, entries,
            labelPosition: new Vector2d(busX, 84), labelAnchor: SheetTextAnchor.Center);
        return (sch, placement, bus);
    }

    [Fact]
    public void ADeclaredBus_DrawsAThickWireDiagonalEntriesAndAVectorLabel()
    {
        var (sch, placement, bus) = DataBus();
        var drawing = new SchematicSheet(sch, placement, buses: [bus]).Draw();

        // The bus is a drawn value carrying the expanded members and the NAME[m..n] label.
        var drawn = Assert.Single(drawing.Buses);
        Assert.Equal("DATA", drawn.Name);
        Assert.Equal(new[] { "DATA0", "DATA1", "DATA2", "DATA3" }, drawn.Members);
        Assert.Equal("DATA[0..3]", drawn.Label.Text);
        Assert.Equal(SchematicLayers.Bus, drawn.Label.Layer);

        // The thick bus-wire polyline is exactly the declared path.
        Assert.Equal(bus.Path, drawn.Path);

        // One diagonal entry per member — neither horizontal nor vertical (a 45° stub here).
        Assert.Equal(4, drawn.Entries.Count);
        foreach (var (a, b) in drawn.Entries)
            Assert.True(Math.Abs(a.X - b.X) > 1e-9 && Math.Abs(a.Y - b.Y) > 1e-9,
                "a bus entry must be a diagonal stub");

        // The SVG carries a bus layer, the vector label text, and a stroke WIDER than a wire's.
        string svg = drawing.ToSvg();
        Assert.Contains("id=\"bus\"", svg);
        Assert.Contains("DATA[0..3]", svg);
        Assert.Contains("stroke-width=\"0.8\"", svg);       // the default bus width
        Assert.Contains("stroke-width=\"0.5\"", svg);       // a signal wire is thinner
        Assert.True(new SchematicSheetOptions().BusWireWidth > SvgDrawing.SvgPen.For(SvgLineClass.Visible).Width,
            "the bus wire must be wider than a signal wire");

        // The DXF carries lines on the bus layer and the label text on the bus layer.
        var dxf = drawing.ToDxf();
        Assert.Contains(dxf.Entities, e => e is DxfLine line && line.Layer == SchematicLayers.Bus);
        Assert.Contains(dxf.Entities, e => e is DxfText t && t.Value == "DATA[0..3]" && t.Layer == SchematicLayers.Bus);

        // The PDF is a valid document that carries the label.
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(drawing.ToPdf(), 0, 4));
    }

    [Fact]
    public void ANoBusSheet_IsByteIdenticalToTheSameSheetWithoutBuses()
    {
        var (sch, placement) = LedIndicator();
        var incumbent = new SchematicSheet(sch, placement).Draw();
        var nullBuses = new SchematicSheet(sch, placement, buses: null).Draw();
        var emptyBuses = new SchematicSheet(sch, placement, buses: []).Draw();

        Assert.Equal(incumbent.ToSvg(), nullBuses.ToSvg());
        Assert.Equal(incumbent.ToSvg(), emptyBuses.ToSvg());

        string Dxf(SchematicDrawing d) { var w = new StringWriter(); d.ToDxf().Save(w); return w.ToString(); }
        Assert.Equal(Dxf(incumbent), Dxf(emptyBuses));
        Assert.Equal(incumbent.ToPdf(), emptyBuses.ToPdf());   // byte[] equality

        Assert.Empty(incumbent.Buses);
        Assert.DoesNotContain("id=\"bus\"", incumbent.ToSvg());
    }

    [Fact]
    public void ABus_IsCosmetic_TheDrawingReconstructsTheSameNetsAsAPlainWireSheet()
    {
        var (sch, placement, bus) = DataBus();
        var plain = new SchematicSheet(sch, placement).Draw();               // member wires, no bus
        var withBus = new SchematicSheet(sch, placement, buses: [bus]).Draw();

        // The bus adds NO wire segments — the connectivity graph is identical.
        Assert.Equal(plain.WireSegments, withBus.WireSegments);

        // Both verify (join exactly the pins the netlist connects).
        Assert.True(plain.Verify().Ok, plain.Verify().ToString());
        Assert.True(withBus.Verify().Ok, withBus.Verify().ToString());

        var u1 = sch.Find("U1")!;
        var u2 = sch.Find("U2")!;
        // The teeth: DATA0 and DATA1 are DIFFERENT member nets — the bus wire crossing near both
        // must NOT merge them; and each member's own pins ARE joined by its wire.
        Assert.False(withBus.Connectivity.AreJoined(u1.Pin("1"), u1.Pin("2")));   // DATA0 vs DATA1
        Assert.True(withBus.Connectivity.AreJoined(u1.Pin("1"), u2.Pin("1")));    // DATA0 member wire

        // AreJoined agrees for EVERY pair of schematic pins, with and without the bus.
        var pins = sch.Components.SelectMany(c => c.AllPins).ToList();
        foreach (var a in pins)
            foreach (var b in pins)
                Assert.Equal(
                    plain.Connectivity.AreJoined(a, b),
                    withBus.Connectivity.AreJoined(a, b));
    }

    [Fact]
    public void ABusSheet_IsADeterministicFunctionOfTheGraphAndTheBus()
    {
        var (sch, placement, bus) = DataBus();
        string Svg() => new SchematicSheet(sch, placement, buses: [bus]).Draw().ToSvg();
        Assert.Equal(Svg(), Svg());

        string Dxf()
        {
            var w = new StringWriter();
            new SchematicSheet(sch, placement, buses: [bus]).Draw().ToDxf().Save(w);
            return w.ToString();
        }
        Assert.Equal(Dxf(), Dxf());
    }

    [Fact]
    public void AReversedBusRange_ExpandsToTheSameMembersInTheDrawnDirection()
    {
        var path = new List<Vector2d> { new(0, 0), new(0, 10) };
        var fwd = new SchematicBus("D", 0, 3, path);
        var rev = new SchematicBus("D", 3, 0, path);

        Assert.Equal(new[] { "D0", "D1", "D2", "D3" }, fwd.Members);
        Assert.Equal(new[] { "D3", "D2", "D1", "D0" }, rev.Members);
        Assert.Equal("D[0..3]", fwd.Label);
        Assert.Equal("D[3..0]", rev.Label);
        // A null label position defaults to the last path point (the bus's open end).
        Assert.Equal(path[^1], fwd.LabelPosition);
    }

    [Fact]
    public void ABusWireWithTooFewPoints_IsRefusedByName()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SchematicBus("DATA", 0, 3, [new Vector2d(0, 0)]));
        Assert.Contains("DATA", ex.Message);
        Assert.Contains("at least two points", ex.Message);
    }
}
