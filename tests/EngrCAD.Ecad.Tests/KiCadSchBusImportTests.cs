using System.Linq;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// BUS import for the whole-sheet KiCad <c>.kicad_sch</c> reader. A bus is a labelled bundle of
/// signal nets — <c>DATA[0..7]</c> is the eight nets DATA0..DATA7 — and an importer that MIS-EXPANDS
/// membership is a silent failure, so the bar is the membership PARTITION plus the mutation that
/// proves it bites: a ripped signal's net is its OWN local label, so relabelling a tap moves its pin
/// to a different member (a positional / membership-blind importer would pass the partition test and
/// fail the mutation). Range parsing (<c>[0..7]</c> and reversed <c>[7..0]</c>), no cross-contamination
/// of plain nets, and the refusals / reports round it out.
/// </summary>
public sealed class KiCadSchBusImportTests
{
    // ==== 1. membership expansion: the member nets reconstruct exactly =======

    [Fact]
    public void DataBus_ReconstructsTheFourMemberNets()
    {
        var read = KiCadSchReader.Read(KiCadSchBusFixtures.DataBusTwoSided);
        var sch = read.Schematic;

        // The bus-VECTOR label DATA[0..3] is NOT a signal net — it declares the member namespace.
        Assert.DoesNotContain(sch.Nets, n => n.Name == "DATA[0..3]");

        // Each member DATA_i joins its two taps { RA_i.1, RB_i.1 } — reconstructed from each ripped
        // wire's own DATA_i label.
        AssertNet(sch, "DATA0", ("RA0", "1"), ("RB0", "1"));
        AssertNet(sch, "DATA1", ("RA1", "1"), ("RB1", "1"));
        AssertNet(sch, "DATA2", ("RA2", "1"), ("RB2", "1"));
        AssertNet(sch, "DATA3", ("RA3", "1"), ("RB3", "1"));

        // Every tap validates as a bus member — no non-member diagnostic.
        Assert.DoesNotContain(read.Diagnostics, d => d.Contains("not a member"));

        var report = sch.Check();
        Assert.True(report.Ok, report.ToString());
    }

    // ==== 2. the mutation that bites: relabel a tap ==========================

    [Fact]
    public void RelabellingATap_MovesThePinToTheNewMember()
    {
        // Un-mutated: RA2.1 and RB2.1 share DATA2.
        var whole = KiCadSchReader.Read(KiCadSchBusFixtures.DataBusTwoSided).Schematic;
        Assert.True(SameNet(whole, ("RA2", "1"), ("RB2", "1")), "RA2.1 and RB2.1 should share DATA2");

        // Mutated: RA2's tap is relabelled DATA2 -> DATA5, so RA2.1 lands on DATA5 (its own label),
        // DATA2 loses it. A membership-blind (positional) importer would still put RA2 on DATA2.
        var read = KiCadSchReader.Read(KiCadSchBusFixtures.DataBusRelabelled);
        var broken = read.Schematic;
        Assert.False(SameNet(broken, ("RA2", "1"), ("RB2", "1")), "relabelling should split them");
        Assert.Equal("DATA5", NetOf(broken, "RA2", "1")!.Name);
        Assert.Equal("DATA2", NetOf(broken, "RB2", "1")!.Name);

        // And DATA5 is not a member of DATA[0..3] — reported by name.
        Assert.Contains(read.Diagnostics, d => d.Contains("not a member") && d.Contains("DATA5"));
    }

    // ==== 3. range parsing: forward and reversed expand to the same members ==

    [Fact]
    public void RangeParsing_ForwardAndReversed_ExpandToTheSameEightMembers()
    {
        foreach (var text in new[] { KiCadSchBusFixtures.VectorForward, KiCadSchBusFixtures.VectorReversed })
        {
            var read = KiCadSchReader.Read(text);
            var sch = read.Schematic;

            // NAME[0..7] and the reversed NAME[7..0] both declare exactly NAME0..NAME7, so all eight
            // taps validate (no non-member diagnostic) and the eight member nets exist.
            var members = Enumerable.Range(0, 8).Select(i => $"NAME{i}").ToArray();
            foreach (var m in members)
                Assert.Contains(sch.Nets, n => n.Name == m);
            Assert.DoesNotContain(read.Diagnostics, d => d.Contains("not a member"));
            Assert.DoesNotContain(sch.Nets, n => n.Name.StartsWith("NAME["));
        }
    }

    [Fact]
    public void AMemberOutsideTheRange_IsReportedByName()
    {
        // A tap labelled NAME8 ripped off a NAME[0..7] bus is not a declared member — reported by
        // name (which proves the upper bound is 7, not open-ended). Its net still forms from its label.
        var read = KiCadSchReader.Read(KiCadSchBusFixtures.VectorOutOfRange);
        Assert.Contains(read.Schematic.Nets, n => n.Name == "NAME8");
        Assert.Contains(read.Diagnostics, d => d.Contains("not a member") && d.Contains("NAME8"));
    }

    [Theory]
    [InlineData("DATA[]")]          // empty range
    [InlineData("DATA[a..b]")]      // non-integer bounds
    [InlineData("DATA[0..]")]       // missing upper bound
    [InlineData("DATA[..7]")]       // missing lower bound
    [InlineData("DATA[0..7..3]")]   // too many bounds
    public void AMalformedBusRange_IsRefusedByName(string busLabel)
    {
        var ex = Assert.Throws<FormatException>(
            () => KiCadSchReader.Read(KiCadSchBusFixtures.BadRangeSheet(busLabel)));
        Assert.Contains("bus", ex.Message);
        Assert.Contains(busLabel, ex.Message);
    }

    [Theory]
    [InlineData("DATA[0..7]")]
    [InlineData("DATA[7..0]")]
    public void AWellFormedBusRange_IsNotRefused(string busLabel) =>
        KiCadSchReader.Read(KiCadSchBusFixtures.BadRangeSheet(busLabel));   // no throw

    // ==== 4. buses do not cross-contaminate a plain signal net ===============

    [Fact]
    public void ABusDoesNotCrossContaminateAPlainNet()
    {
        var sch = KiCadSchReader.Read(KiCadSchBusFixtures.BusPlusPlainNet).Schematic;

        // The plain SIG net is exactly { RS1.1, RS2.1 } — untouched by the DATA bus beside it.
        AssertNet(sch, "SIG", ("RS1", "1"), ("RS2", "1"));
        Assert.False(SameNet(sch, ("RS1", "1"), ("RA0", "1")), "SIG must not merge with a bus member");
        Assert.Equal("SIG", NetOf(sch, "RS1", "1")!.Name);

        // And the bus members are still exactly what they were.
        AssertNet(sch, "DATA0", ("RA0", "1"), ("RB0", "1"));
    }

    // ==== 5. a dangling bus entry is reported (not thrown) ====================

    [Fact]
    public void ADanglingBusEntry_IsReported()
    {
        // The bus entry's bus side is on the bus, but its wire side touches no wire — nothing is
        // ripped off there, which is reported (never thrown — the readers-never-throw-on-dirty rule).
        var read = KiCadSchReader.Read(KiCadSchBusFixtures.DanglingBusEntry);
        Assert.Contains(read.Diagnostics, d => d.Contains("bus entry") && d.Contains("not connected to a wire"));
    }

    // ==== 6. determinism =====================================================

    [Fact]
    public void Read_OfABus_IsDeterministic()
    {
        var a = KiCadSchReader.Read(KiCadSchBusFixtures.DataBusTwoSided).Schematic.Save();
        var b = KiCadSchReader.Read(KiCadSchBusFixtures.DataBusTwoSided).Schematic.Save();
        Assert.Equal(a, b);
    }

    // ---- helpers (mirroring KiCadSchImportTests) ----------------------------

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
