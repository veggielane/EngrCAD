using System.Linq;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// HIERARCHICAL / multi-sheet KiCad import (<see cref="KiCadSchReader.ReadProjectFrom"/>). The bar
/// is higher than usual — a hierarchical importer that MIS-STITCHES nets is a silent failure — so
/// the headline oracle is the CROSS-SHEET PARTITION reconstructed from geometry + name-matching,
/// alongside the mutation that proves the stitch bites (rename the sub-sheet's hierarchical label and
/// the parent/child net SPLITS), the local-vs-global scoping crux, multi-instance distinctness, and
/// the recursion / missing-file refusals. The flat single-sheet <see cref="KiCadSchReader.Read"/> is
/// asserted UNCHANGED by its own test file continuing to pass.
/// </summary>
public sealed class KiCadSchHierImportTests
{
    // ==== 1. cross-sheet stitch ==============================================

    [Fact]
    public void SheetPin_StitchesTheParentNetToTheChildHierarchicalLabel()
    {
        var read = KiCadSchReader.ReadProjectFrom(
            KiCadSchHierFixtures.Stitch.Root, KiCadSchHierFixtures.Stitch.Map);
        var sch = read.Schematic;

        // Two components, hierarchical refdes: the root's R1 and the sub-sheet's "sub/R2".
        Assert.Equal(new[] { "R1", "sub/R2" }, sch.Components.Select(c => c.ReferenceDesignator));
        Assert.Equal("stitch", read.SheetName);

        // The sheet pin "VOUT" ties the parent net (R1.2) to the sub-sheet's hierarchical_label
        // "VOUT" (sub/R2.1): they are ONE net named "VOUT".
        Assert.True(SameNet(sch, ("R1", "2"), ("sub/R2", "1")));
        AssertNet(sch, "VOUT", ("R1", "2"), ("sub/R2", "1"));

        // The flattened schematic is well-formed.
        var report = sch.Check();
        Assert.True(report.Ok, report.ToString());
        Assert.Equal(report.TotalPins, report.PinsCoveredOnce);
    }

    // ==== 2. the mutation that proves it bites ===============================

    [Fact]
    public void RenamingTheChildPort_SplitsTheCrossSheetNet()
    {
        var whole = KiCadSchReader.ReadProjectFrom(
            KiCadSchHierFixtures.Stitch.Root, KiCadSchHierFixtures.Stitch.Map).Schematic;
        var read = KiCadSchReader.ReadProjectFrom(
            KiCadSchHierFixtures.StitchBroken.Root, KiCadSchHierFixtures.StitchBroken.Map);
        var broken = read.Schematic;

        Assert.True(SameNet(whole, ("R1", "2"), ("sub/R2", "1")), "the port should stitch the two");
        // Renaming the sub's hierarchical_label off the parent's sheet-pin name breaks the stitch —
        // a name-blind stitcher would pass test 1 and fail this.
        Assert.False(SameNet(broken, ("R1", "2"), ("sub/R2", "1")), "renaming the port must split them");

        // The dangling port is REPORTED, not thrown (both directions of the mismatch).
        Assert.Contains(read.Diagnostics,
            d => d.Contains("VOUT_X") && d.Contains("dangling hierarchical port"));
        Assert.Contains(read.Diagnostics,
            d => d.Contains("Sheet pin 'VOUT'") && d.Contains("no matching hierarchical label"));
    }

    // ==== 3. local-label scoping (the crux) ==================================

    [Fact]
    public void LocalLabels_StayLocal_GlobalLabels_Span()
    {
        // Two sheets each carry a LOCAL "CLK" with no tie → the root's RA.2 and the sub's RC.2 are
        // on DIFFERENT nets (scoped by sheet), and their names are distinguished by path.
        var local = KiCadSchReader.ReadProjectFrom(
            KiCadSchHierFixtures.LocalClk.Root, KiCadSchHierFixtures.LocalClk.Map).Schematic;
        Assert.False(SameNet(local, ("RA", "2"), ("SubB/RC", "2")), "local 'CLK' must not cross sheets");
        Assert.Equal("CLK", NetOf(local, "RA", "2")!.Name);
        Assert.Equal("SubB/CLK", NetOf(local, "SubB/RC", "2")!.Name);
        Assert.True(local.Check().Ok, local.Check().ToString());

        // The SAME shape with GLOBAL "CLK" ties them into ONE net across the hierarchy.
        var global = KiCadSchReader.ReadProjectFrom(
            KiCadSchHierFixtures.GlobalClk.Root, KiCadSchHierFixtures.GlobalClk.Map).Schematic;
        Assert.True(SameNet(global, ("RA", "2"), ("SubB/RC", "2")), "global 'CLK' spans sheets");
        AssertNet(global, "CLK", ("RA", "2"), ("RB", "2"), ("SubB/RC", "2"), ("SubB/RD", "2"));
    }

    // ==== 4. multi-instance ==================================================

    [Fact]
    public void OneSubSheetPlacedTwice_YieldsDistinctInstancesAndNets()
    {
        var sch = KiCadSchReader.ReadProjectFrom(
            KiCadSchHierFixtures.Twice.Root, KiCadSchHierFixtures.Twice.Map).Schematic;

        // Four distinct components — the amp's RA/RB under each placement path.
        Assert.Equal(
            new[] { "Amp1/RA", "Amp1/RB", "Amp2/RA", "Amp2/RB" },
            sch.Components.Select(c => c.ReferenceDesignator));

        // Each instance's internal net joins its OWN two pins…
        Assert.True(SameNet(sch, ("Amp1/RA", "2"), ("Amp1/RB", "1")));
        Assert.True(SameNet(sch, ("Amp2/RA", "2"), ("Amp2/RB", "1")));
        // …and the two instances' internal nets are DISTINCT (no accidental cross-tie).
        Assert.False(SameNet(sch, ("Amp1/RA", "2"), ("Amp2/RA", "2")));
        Assert.Equal("Amp1/INT", NetOf(sch, "Amp1/RA", "2")!.Name);
        Assert.Equal("Amp2/INT", NetOf(sch, "Amp2/RA", "2")!.Name);

        Assert.True(sch.Check().Ok, sch.Check().ToString());
    }

    // ==== 5. recursion / missing file ========================================

    [Fact]
    public void ARecursiveSheetReference_IsRefusedByName()
    {
        var ex = Assert.Throws<FormatException>(() => KiCadSchReader.ReadProjectFrom(
            KiCadSchHierFixtures.SelfReferential.Root, KiCadSchHierFixtures.SelfReferential.Map));
        Assert.Contains("RECURSIVE", ex.Message);
        Assert.Contains("self.kicad_sch", ex.Message);
    }

    // ==== 5b. buses ACROSS sheets ============================================

    [Fact]
    public void ABusSheetPin_CarriesEachMemberAcrossTheSheetBoundary()
    {
        var read = KiCadSchReader.ReadProjectFrom(
            KiCadSchHierFixtures.BusStitch.Root, KiCadSchHierFixtures.BusStitch.Map);
        var sch = read.Schematic;

        // Member-by-member: DATA0 spans the boundary and DATA1 spans the boundary…
        Assert.True(SameNet(sch, ("RA0", "1"), ("bus/RB0", "1")), "DATA0 must span the boundary");
        Assert.True(SameNet(sch, ("RA1", "1"), ("bus/RB1", "1")), "DATA1 must span the boundary");
        // …and the two members stay DISTINCT nets (the stitch is per member, never a bundle short).
        Assert.False(SameNet(sch, ("RA0", "1"), ("RA1", "1")), "members must not short together");

        Assert.Equal("DATA0", NetOf(sch, "RA0", "1")!.Name);
        Assert.Equal("DATA1", NetOf(sch, "RA1", "1")!.Name);
        Assert.DoesNotContain(read.Diagnostics, d => d.Contains("not a member"));
        Assert.True(sch.Check().Ok, sch.Check().ToString());
    }

    [Fact]
    public void RenamingTheChildBusPort_SplitsTheMembers_AndReportsBothWays()
    {
        // A bundle stitcher that ignored the port NAME would pass the test above and fail this.
        var read = KiCadSchReader.ReadProjectFrom(
            KiCadSchHierFixtures.BusStitchBroken.Root, KiCadSchHierFixtures.BusStitchBroken.Map);
        var broken = read.Schematic;

        Assert.False(SameNet(broken, ("RA0", "1"), ("bus/RB0", "1")), "renaming must split the members");
        Assert.Contains(read.Diagnostics, d =>
            d.Contains("Bus sheet pin 'DATA[0..1]'") && d.Contains("no matching hierarchical bus label"));
        Assert.Contains(read.Diagnostics, d =>
            d.Contains("ADDR[0..1]") && d.Contains("dangling bus port"));
    }

    [Fact]
    public void AHierarchicalSheetWithABus_NowImports()
    {
        // A bus in a hierarchical project used to be refused by name; per-sheet buses now import.
        // The fixture is just a bus + its vector label, so it flattens to an empty, clean schematic.
        var read = KiCadSchReader.ReadProjectFrom(
            KiCadSchHierFixtures.RootWithBus.Root, KiCadSchHierFixtures.RootWithBus.Map);
        Assert.Empty(read.Schematic.Components);
        Assert.True(read.Schematic.Check().Ok, read.Schematic.Check().ToString());
    }

    [Fact]
    public void AMissingSubSheetFile_IsReported_NotThrown()
    {
        var read = KiCadSchReader.ReadProjectFrom(
            KiCadSchHierFixtures.MissingSub.Root, KiCadSchHierFixtures.MissingSub.Map);

        // The root's own component still imports; the subsheet is named as absent.
        Assert.Single(read.Schematic.Components);
        Assert.Contains(read.Diagnostics,
            d => d.Contains("gone.kicad_sch") && d.Contains("could not be resolved"));
        Assert.True(read.Schematic.Check().Ok, read.Schematic.Check().ToString());
    }

    // ==== 6. flat unchanged + determinism ====================================

    [Fact]
    public void AFlatRootThroughTheProjectReader_ReproducesTheFlatPartition()
    {
        // A single-sheet root routed through the project reader imports byte-identically to the
        // flat Read (no subsheets → one instance, so local/global/power all scope to that sheet).
        var flat = KiCadSchReader.Read(KiCadSchFixtures.Divider).Schematic;
        var project = KiCadSchReader.ReadProjectFrom(
            "divider.kicad_sch",
            new Dictionary<string, string> { ["divider.kicad_sch"] = KiCadSchFixtures.Divider })
            .Schematic;

        Assert.Equal(flat.Save(), project.Save());
    }

    [Fact]
    public void ReadProject_IsDeterministic()
    {
        var a = KiCadSchReader.ReadProjectFrom(
            KiCadSchHierFixtures.Stitch.Root, KiCadSchHierFixtures.Stitch.Map).Schematic.Save();
        var b = KiCadSchReader.ReadProjectFrom(
            KiCadSchHierFixtures.Stitch.Root, KiCadSchHierFixtures.Stitch.Map).Schematic.Save();
        Assert.Equal(a, b);
    }

    [Fact]
    public void AMissingRootKey_IsRefusedByName()
    {
        var ex = Assert.Throws<FormatException>(() => KiCadSchReader.ReadProjectFrom(
            "nope.kicad_sch", new Dictionary<string, string> { ["root.kicad_sch"] = "" }));
        Assert.Contains("nope.kicad_sch", ex.Message);
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
