using EngrCAD.Ecad;
using Xunit;

namespace EngrCAD.Ecad.Tests;

public class SchematicModelTests
{
    // ---- the object graph IS the netlist ------------------------------------

    [Fact]
    public void CleanSchematic_PassesEveryCheck()
    {
        var report = Fixtures.LedIndicator().Check();

        Assert.True(report.Ok, report.ToString());
        Assert.Empty(report.UnassignedPins);
        Assert.Empty(report.MultiplyAssignedPins);
        Assert.Empty(report.FloatingNets);
        Assert.Empty(report.EmptyNets);
    }

    [Fact]
    public void CountingIdentity_HoldsAndPartitionsEveryPin()
    {
        var report = Fixtures.LedIndicator().Check();

        // Three two-terminal parts = six terminals, each covered exactly once.
        Assert.Equal(6, report.TotalPins);
        Assert.Equal(6, report.PinsCoveredOnce);
        Assert.True(report.CountingIdentityHolds);

        // Every pin is exactly one of covered-once / unassigned / over-assigned.
        Assert.Equal(
            report.TotalPins,
            report.PinsCoveredOnce + report.UnassignedPins.Count + report.MultiplyAssignedPins.Count);
    }

    // ---- the counting-identity guard must FIRE, naming the offender ---------

    [Fact]
    public void FloatingPin_IsNamed_AndBreaksTheCountingIdentity()
    {
        var sch = new Schematic();
        var r = sch.Add("R1", Fixtures.Resistor());
        var d = sch.Add("D1", Fixtures.Led());
        sch.Connect("N1", r.Pin("1"), d.Pin("A"));
        // R1.2, D1.K left on no net.

        var report = sch.Check();

        Assert.False(report.Ok);
        Assert.False(report.CountingIdentityHolds);
        Assert.Contains("R1.2", report.UnassignedPins);
        Assert.Contains("D1.K", report.UnassignedPins);
        Assert.Equal(2, report.PinsCoveredOnce);   // only R1.1 and D1.A
        Assert.Contains(report.Messages, m => m.Contains("R1.2") && m.Contains("no net"));
    }

    [Fact]
    public void PinOnTwoNets_IsNamed_AndBreaksTheCountingIdentity()
    {
        var sch = new Schematic();
        var r = sch.Add("R1", Fixtures.Resistor());
        var d = sch.Add("D1", Fixtures.Led());
        var bt = sch.Add("BT1", Fixtures.Battery());
        sch.Connect("VCC", r.Pin("1"), bt.Pin("+"));
        // R1.1 wired again on a SECOND net — the classic short.
        sch.Connect("GND", r.Pin("1"), bt.Pin("-"));
        sch.Connect("SIG", r.Pin("2"), d.Pin("A"));
        sch.Connect("RET", d.Pin("K"), bt.Pin("+"));

        var report = sch.Check();

        Assert.False(report.Ok);
        Assert.False(report.CountingIdentityHolds);
        Assert.Contains(report.MultiplyAssignedPins, m => m.Contains("R1.1"));
        Assert.Contains(report.MultiplyAssignedPins, m => m.Contains("VCC") && m.Contains("GND"));
    }

    [Fact]
    public void FloatingNet_IsNamed()
    {
        var sch = new Schematic();
        var r = sch.Add("R1", Fixtures.Resistor());
        var d = sch.Add("D1", Fixtures.Led());
        sch.Connect("SIG", r.Pin("1"), d.Pin("A"));
        sch.Connect("LONE", r.Pin("2"));   // one-pin signal net = floating
        sch.NoConnect(d.Pin("K"));

        var report = sch.Check();

        Assert.False(report.Ok);
        Assert.Contains(report.FloatingNets, n => n.Contains("LONE") && n.Contains("R1.2"));
        // The counting identity still holds — every pin is covered — the fault is the net.
        Assert.True(report.CountingIdentityHolds);
    }

    [Fact]
    public void EmptyNet_IsNamed()
    {
        var sch = new Schematic();
        sch.Add("R1", Fixtures.Resistor());
        sch.Connect("SPARE");   // a signal net with no pins at all

        var report = sch.Check();

        Assert.False(report.Ok);
        Assert.Contains("SPARE", report.EmptyNets);
    }

    // ---- NoConnect and Stub are first-class exemptions -----------------------

    [Fact]
    public void NoConnectPin_CountsAsCovered_AndIsNotFloating()
    {
        var sch = new Schematic();
        var u = sch.Add("U1", Fixtures.OpAmp());
        sch.Connect("N1", u.Pin("1"), u.Pin("2"));
        sch.Connect("N2", u.Pin("3"), u.Pin("4"));
        sch.Connect("N3", u.Pin("6"), u.Pin("7"));
        sch.Stub("T8", u.Pin("8"));       // the odd terminal, a deliberate stub
        sch.NoConnect(u.Pin("5"));         // pin 5 is genuinely NC

        var report = sch.Check();

        // Everything is accounted for: pin 5 is covered by a NoConnect net (not unassigned),
        // and neither the NoConnect net nor the stub is flagged floating.
        Assert.True(report.Ok, report.ToString());
        Assert.DoesNotContain(report.UnassignedPins, p => p == "U1.5");
        Assert.DoesNotContain(report.FloatingNets, n => n.Contains("NC"));
        Assert.Equal(8, report.PinsCoveredOnce);   // all eight terminals covered exactly once

        // The no-connect terminal is on a NoConnect-kind net in the derived view.
        var nc = sch.ToNetlist().NetOf(u.Pin("5"));
        Assert.NotNull(nc);
        Assert.Equal(NetKind.NoConnect, nc!.Kind);
    }

    [Fact]
    public void StubNet_IsExemptFromTheFloatingCheck()
    {
        var sch = new Schematic();
        var r = sch.Add("R1", Fixtures.Resistor());
        var tp = sch.Add("TP1", new PartDefinition("TESTPOINT", "TP", [new Pin("1")]));
        sch.Connect("SIG", r.Pin("1"), r.Pin("2"));
        sch.Stub("VOUT_TEST", tp.Pin("1"));   // deliberately single-pin

        var report = sch.Check();

        Assert.True(report.Ok, report.ToString());
        Assert.Empty(report.FloatingNets);
        Assert.Equal(3, report.TotalPins);
        Assert.Equal(3, report.PinsCoveredOnce);
    }

    // ---- fluent-API guards ---------------------------------------------------

    [Fact]
    public void DuplicateReferenceDesignator_IsRefusedByName()
    {
        var sch = new Schematic();
        sch.Add("R1", Fixtures.Resistor());
        var ex = Assert.Throws<ArgumentException>(() => sch.Add("R1", Fixtures.Resistor()));
        Assert.Contains("R1", ex.Message);
    }

    [Fact]
    public void PinFromAnotherSchematic_IsRefusedByName()
    {
        var a = new Schematic("A");
        var b = new Schematic("B");
        var ra = a.Add("R1", Fixtures.Resistor());
        b.Add("R1", Fixtures.Resistor());
        // Wiring A's R1 pin into B's net must be refused — the graphs are separate.
        var ex = Assert.Throws<ArgumentException>(() => b.Connect("N", ra.Pin("1")));
        Assert.Contains("not in this schematic", ex.Message);
    }

    [Fact]
    public void UnknownPinNumber_IsRefusedByName()
    {
        var sch = new Schematic();
        var r = sch.Add("R1", Fixtures.Resistor());
        var ex = Assert.Throws<ArgumentException>(() => r.Pin("99"));
        Assert.Contains("R1", ex.Message);
        Assert.Contains("99", ex.Message);
    }

    [Fact]
    public void SignalNetCannotShareANoConnectName()
    {
        var sch = new Schematic();
        var r = sch.Add("R1", Fixtures.Resistor());
        var nc = sch.NoConnect(r.Pin("1"));   // creates "NC1"
        var ex = Assert.Throws<ArgumentException>(() => sch.Connect(nc.Name, r.Pin("2")));
        Assert.Contains(nc.Name, ex.Message);
    }

    [Fact]
    public void ConnectExtendsANetOfTheSameName()
    {
        var sch = new Schematic();
        var r1 = sch.Add("R1", Fixtures.Resistor());
        var r2 = sch.Add("R2", Fixtures.Resistor());
        var r3 = sch.Add("R3", Fixtures.Resistor());
        sch.Connect("VCC", r1.Pin("1"));
        sch.Connect("VCC", r2.Pin("1"));
        sch.Connect("VCC", r3.Pin("1"));   // three calls, one rail

        var vcc = Assert.Single(sch.Nets, n => n.Name == "VCC");
        Assert.Equal(3, vcc.Pins.Count);
    }

    // ---- the derived netlist view -------------------------------------------

    [Fact]
    public void Netlist_IsADerivedProjection_NotCachedState()
    {
        var sch = Fixtures.LedIndicator();
        var before = sch.ToNetlist();
        Assert.Equal(3, before.SignalNets.Count());

        // Adding a net to the graph and re-asking gives the new state — there is no stale copy.
        var r = sch.Find("R1")!;
        var d = sch.Find("D1")!;
        // (already fully connected; add a stub to prove the view tracks the graph)
        sch.Stub("TP", r.Pin("1"));   // R1.1 now on two nets, but that is a Check concern
        var after = sch.ToNetlist();
        Assert.Equal(4, after.SignalNets.Count());
        Assert.Equal("VCC", after.NetOf(r.Pin("1"))!.Name);   // first net wins in the view
    }

    [Fact]
    public void NetlistText_ListsComponentsAndNets()
    {
        var text = Fixtures.LedIndicator().ToNetlist().ToText();

        Assert.Contains("LED indicator", text);
        Assert.Contains("R1", text);
        Assert.Contains("R_0805", text);
        Assert.Contains("VCC", text);
        Assert.Contains("BT1.+", text);
        Assert.Contains("= 330", text);
    }
}
