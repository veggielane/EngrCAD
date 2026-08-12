using System.Linq;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Whole-sheet KiCad <c>.kicad_sch</c> import. The bar is higher than usual — an importer that
/// mislabels nets is a SILENT failure — so the headline oracle is CONNECTIVITY RECONSTRUCTED FROM
/// GEOMETRY: the reader has no listed netlist to copy; it works out which pins share a net from the
/// wires, junctions, labels and power symbols. A mis-read wire would show up as a net joining the
/// wrong pins. Alongside it: the mutation that proves the reader reads geometry (move a wire off a
/// pin → the net splits), the junction rule (a crossing needs a junction), label equivalence,
/// no_connect, symbol/pin identity, and the refusals.
/// </summary>
public sealed class KiCadSchImportTests
{
    // ==== 1. connectivity reconstructed exactly (and Check passes) ===========

    [Fact]
    public void Divider_ReconstructsExactlyTheIntendedPartition()
    {
        var sch = KiCadSchReader.Read(KiCadSchFixtures.Divider).Schematic;

        // Three components (the two power symbols name nets, they are not components).
        Assert.Equal(3, sch.Components.Count);
        Assert.Equal(new[] { "R1", "R2", "C1" }, sch.Components.Select(c => c.ReferenceDesignator));

        // VCC = { R1.1, C1.1 } — the pins the VCC power symbol reaches through the wires.
        AssertNet(sch, "VCC", ("R1", "1"), ("C1", "1"));
        // MID = { R1.2, R2.1 } — named by the local label on the wire between them.
        AssertNet(sch, "MID", ("R1", "2"), ("R2", "1"));
        // GND = { R2.2, C1.2 } — named by the GND power symbol.
        AssertNet(sch, "GND", ("R2", "2"), ("C1", "2"));

        // The counting identity holds and there is nothing floating — Check() passes.
        var report = sch.Check();
        Assert.True(report.Ok, report.ToString());
        Assert.Equal(report.TotalPins, report.PinsCoveredOnce);
    }

    [Fact]
    public void Divider_TheValuesAndSheetNameCarry()
    {
        var read = KiCadSchReader.Read(KiCadSchFixtures.Divider);
        Assert.Equal("divider", read.SheetName);
        Assert.Equal("10k", read.Schematic.Find("R1")!.Value);
        Assert.Equal("20k", read.Schematic.Find("R2")!.Value);
        Assert.Equal("100nF", read.Schematic.Find("C1")!.Value);
    }

    // ==== 2. the mutation that bites =========================================

    [Fact]
    public void MovingAWireOffAPin_SplitsTheNet()
    {
        // In the divider, R1.2 and R2.1 share the MID net. Move the MID wire's far end off R2.1
        // and the two must fall onto DIFFERENT nets — a listed-netlist importer (one that ignored
        // the wire geometry) would pass the first test and fail this.
        var whole = KiCadSchReader.Read(KiCadSchFixtures.Divider).Schematic;
        var broken = KiCadSchReader.Read(KiCadSchFixtures.DividerBrokenWire).Schematic;

        Assert.True(SameNet(whole, ("R1", "2"), ("R2", "1")), "R1.2 and R2.1 should share MID");
        Assert.False(SameNet(broken, ("R1", "2"), ("R2", "1")), "moving the wire should split them");

        // The disconnected pin is REPORTED, not thrown.
        var read = KiCadSchReader.Read(KiCadSchFixtures.DividerBrokenWire);
        Assert.Contains(read.Diagnostics, d => d.Contains("R2.1") && d.Contains("not connected"));
    }

    // ==== 3. label equivalence ===============================================

    [Fact]
    public void SameLabel_IsOneNet_DifferentLabels_AreTwo()
    {
        var sch = KiCadSchReader.Read(KiCadSchFixtures.LabelEquivalence).Schematic;

        // RA.1 and RB.1 carry the SAME label "FOO" (on two separate stubs) — one net.
        Assert.True(SameNet(sch, ("RA", "1"), ("RB", "1")));
        Assert.Equal("FOO", NetOf(sch, "RA", "1")!.Name);
        AssertNet(sch, "FOO", ("RA", "1"), ("RB", "1"));

        // RC.1 carries "BAR" — a DIFFERENT net.
        Assert.Equal("BAR", NetOf(sch, "RC", "1")!.Name);
        Assert.False(SameNet(sch, ("RA", "1"), ("RC", "1")));

        Assert.True(sch.Check().Ok, sch.Check().ToString());
    }

    // ==== 4. the junction rule ===============================================

    [Fact]
    public void CrossingWithoutAJunction_IsTwoNets()
    {
        var sch = KiCadSchReader.Read(KiCadSchFixtures.Crossing).Schematic;

        // The two wires cross mid-segment with no junction: RA.1—RB.1 is one net, RC.2—RD.1 is
        // another, and the crossing does NOT join them (the schematic convention).
        Assert.True(SameNet(sch, ("RA", "1"), ("RB", "1")));
        Assert.True(SameNet(sch, ("RC", "2"), ("RD", "1")));
        Assert.False(SameNet(sch, ("RA", "1"), ("RC", "2")));
    }

    [Fact]
    public void CrossingWithAJunction_IsOneNet()
    {
        var sch = KiCadSchReader.Read(KiCadSchFixtures.CrossingWithJunction).Schematic;

        // A junction at the crossing joins BOTH wires — all four pins are now one net.
        Assert.True(SameNet(sch, ("RA", "1"), ("RC", "2")));
        AssertNet(sch, NetOf(sch, "RA", "1")!.Name, ("RA", "1"), ("RB", "1"), ("RC", "2"), ("RD", "1"));
    }

    // ==== 5. no_connect ======================================================

    [Fact]
    public void NoConnect_MarksAPinUnconnected_NotOnASignalNet()
    {
        var sch = KiCadSchReader.Read(KiCadSchFixtures.NoConnectSheet).Schematic;

        // RA.1—RB.1 is a real signal net; the bottom pins are their own NoConnect state.
        Assert.Equal(NetKind.Signal, NetOf(sch, "RA", "1")!.Kind);
        Assert.Equal(NetKind.NoConnect, NetOf(sch, "RA", "2")!.Kind);
        Assert.Equal(NetKind.NoConnect, NetOf(sch, "RB", "2")!.Kind);

        // A no-connect pin is on NO signal net.
        Assert.DoesNotContain(sch.ToNetlist().SignalNets.SelectMany(n => n.Pins),
            p => p.ReferenceDesignator == "RA" && p.Number == "2");

        Assert.True(sch.Check().Ok, sch.Check().ToString());
    }

    // ==== 6. symbol / pin identity ===========================================

    [Fact]
    public void ImportedComponents_HavePinsMatchingTheirLibSymbol()
    {
        var sch = KiCadSchReader.Read(KiCadSchFixtures.Divider).Schematic;

        // Every component's pins come from its lib_symbol's pins, so the symbol == netlist pin
        // identity holds by NUMBER — a downstream board/netlist is well-formed.
        foreach (var component in sch.Components)
        {
            var identity = PinIdentity.Check(component.Definition);
            Assert.True(identity.Ok, $"{component.ReferenceDesignator}: {identity}");
            Assert.NotNull(component.Definition.Symbol);
            Assert.Equal(component.Definition.Pins.Select(p => p.Number),
                component.Definition.Symbol!.PinNumbers);
        }

        // Two Device:R components SHARE one interned definition (the lib_id is written once).
        Assert.Same(sch.Find("R1")!.Definition, sch.Find("R2")!.Definition);
    }

    // ==== 7. refusals + determinism ==========================================

    [Fact]
    public void ABoardHandedToTheSchematicReader_IsRefusedByName()
    {
        var ex = Assert.Throws<FormatException>(() => KiCadSchReader.Read(KiCadPcbFixtures.Board));
        Assert.Contains("kicad_sch", ex.Message);
    }

    [Fact]
    public void ASymbolLibraryHandedHere_IsRefusedByName()
    {
        var ex = Assert.Throws<FormatException>(() => KiCadSchReader.Read(KiCadFixtures.ResistorSym));
        Assert.Contains("kicad_sch", ex.Message);
    }

    [Fact]
    public void AnAnonymousBusGroup_IsImported_AndItsLabelIsNotANet()
    {
        // Bus VECTORS (NAME[m..n]) and anonymous bus GROUPS ({A B …}) are both supported now (see
        // KiCadSchBusImportTests): the group label declares its member namespace, so it is NOT a signal
        // net. (A named bus ALIAS and a NESTED group stay out of scope.)
        var sch = KiCadSchReader.Read(KiCadSchFixtures.BusGroupSheet).Schematic;
        Assert.DoesNotContain(sch.Nets, n => n.Name.Contains('{'));
    }

    [Fact]
    public void HierarchicalSheets_AreRefusedByName()
    {
        var ex = Assert.Throws<FormatException>(
            () => KiCadSchReader.Read(KiCadSchFixtures.HierarchicalSheet));
        Assert.Contains("hierarchical sheet", ex.Message);
        Assert.Contains("sheet", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("(kicad_sch")]                 // unbalanced '('
    [InlineData("(kicad_sch \"unterminated")]  // unterminated string
    [InlineData("not-a-list")]                 // top-level atom
    public void MalformedSExpression_IsRefusedByName(string text) =>
        Assert.Throws<FormatException>(() => KiCadSchReader.Read(text));

    [Fact]
    public void Read_IsDeterministic()
    {
        // Two reads of one file produce structurally identical graphs — asserted through the
        // document serializer (a byte-identical save is the strong form of "structurally equal").
        var a = KiCadSchReader.Read(KiCadSchFixtures.Divider).Schematic.Save();
        var b = KiCadSchReader.Read(KiCadSchFixtures.Divider).Schematic.Save();
        Assert.Equal(a, b);
    }

    // ==== the .kicad_sym component reader stays bit-identical =================

    [Fact]
    public void ComponentReader_IsUnchanged()
    {
        // The schematic reader shares KiCadSymbolReader.ParseSymbolList, but the .kicad_sym path is
        // unchanged — re-assert the resistor's exact symbol (the numbers KiCadImportTests pins).
        var symbol = KiCadSymbolReader.Read(KiCadFixtures.ResistorSym);
        Assert.Equal("R_0805", symbol.Symbol.Name);
        Assert.Equal("R", symbol.ReferencePrefix);
        Assert.Equal(2, symbol.Pins.Count);
        Assert.Equal(new[] { "1", "2" }, symbol.Symbol.PinNumbers);
        Assert.Equal(new Core.Vector2d(0, 3.81), symbol.Symbol.PinNumbered("1").Anchor);
    }

    // ---- helpers ------------------------------------------------------------

    private static Net? NetOf(Schematic sch, string refDes, string number) =>
        sch.ToNetlist().NetOf(sch.Find(refDes)!.Pin(number));

    private static bool SameNet(Schematic sch, (string Ref, string Pin) a, (string Ref, string Pin) b)
    {
        var netlist = sch.ToNetlist();
        var na = netlist.NetOf(sch.Find(a.Ref)!.Pin(a.Pin));
        var nb = netlist.NetOf(sch.Find(b.Ref)!.Pin(b.Pin));
        return na is not null && ReferenceEquals(na, nb);
    }

    private static void AssertNet(Schematic sch, string name, params (string Ref, string Pin)[] pins)
    {
        var net = sch.Nets.SingleOrDefault(n => n.Name == name);
        Assert.NotNull(net);
        var expected = pins.Select(p => $"{p.Ref}.{p.Pin}").OrderBy(s => s).ToArray();
        var actual = net!.Pins.Select(p => p.ToString()).OrderBy(s => s).ToArray();
        Assert.Equal(expected, actual);
    }
}
