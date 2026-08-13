using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Whole KiCad board EXPORT (<see cref="KiCadPcbWriter"/>). The oracle is the writer's own twin —
/// <see cref="KiCadPcbReader"/> reads back what it writes — so "the exported board is the same
/// board" is asserted through the reader (net partition, placements, pad centres, copper, DRC),
/// and <c>write → read → write</c> is a BYTE fixed point (the writer numbers nets in the reader's
/// own pad-encounter order to earn it). Refusals are by name for geometry the format cannot spell
/// without lying; what the format does not carry (a fab spec, mask/silk/paste, teardrops) is
/// reported, never silently dropped.
/// </summary>
public sealed class KiCadPcbWriterTests
{
    /// <summary>A routed native layout: the shared two-part circuit on a hole-less board, a
    /// routed SIG trace, a via, and a VCC pour with a custom clearance and priority.</summary>
    private static PcbLayout Routed()
    {
        var board = new PcbBoard(
            [new Vector2d(-25, -20), new Vector2d(25, -20), new Vector2d(25, 20), new Vector2d(-25, 20)],
            thickness: 1.6);
        var layout = new PcbLayout(PcbFixtures.Circuit(), board);
        layout.Place("R1", 5, 0, rotationDegrees: 0, side: CopperSide.Top);
        layout.Place("J1", -8, 4, rotationDegrees: 90, side: CopperSide.Top);
        layout.AddTrace("SIG", "Top", 0.3,
            [new Vector2d(6, 0), new Vector2d(-4, 0), new Vector2d(-4, 5.27), new Vector2d(-8, 5.27)]);
        layout.AddTrace("SIG", "Top", 0.3, [new Vector2d(6, 0), new Vector2d(6, -5)]);
        layout.AddVia("SIG", 6, -5, "Top", "Bottom", drill: 0.4, pad: 0.8);
        layout.AddTrace("SIG", "Bottom", 0.3, [new Vector2d(6, -5), new Vector2d(0, -5)]);
        layout.AddPour(new CopperPour("VCC", "Top",
            [new Vector2d(-20, 1), new Vector2d(-4, 1), new Vector2d(-4, 18), new Vector2d(-20, 18)],
            Clearance: 0.3, Priority: 2));
        return layout;
    }

    /// <summary>The net partition as one canonical string, so equality is content, not
    /// dictionary insertion order.</summary>
    private static string NetPartition(PcbLayout layout) =>
        string.Join(";", layout.Schematic.Nets
            .Where(n => n.Kind != NetKind.NoConnect)
            .OrderBy(n => n.Name, StringComparer.Ordinal)
            .Select(n => $"{n.Name}=" + string.Join(",",
                n.Pins.Select(p => $"{p.ReferenceDesignator}.{p.Number}").Order())));

    [Fact]
    public void TheExportedBoard_ReadsBack_AsTheSameBoard()
    {
        var layout = Routed();
        var pcb = KiCadPcbReader.Read(KiCadPcbWriter.Write(layout));
        var back = pcb.Layout;

        // The net PARTITION survives — same nets joining the same pads.
        Assert.Equal(NetPartition(layout), NetPartition(back));

        // Placements pose-for-pose, and every pad centre exact.
        foreach (var p in layout.Placements)
        {
            var q = back.Placements.Single(x => x.Reference == p.Reference);
            Assert.Equal(p.X, q.X);
            Assert.Equal(p.Y, q.Y);
            Assert.Equal(p.RotationDegrees, q.RotationDegrees);
            Assert.Equal(p.Side, q.Side);
        }
        foreach (var pad in layout.PlacedPads())
        {
            var q = back.PlacedPads().Single(x => x.Name == pad.Name);
            Assert.Equal(pad.World.X, q.World.X, 12);
            Assert.Equal(pad.World.Y, q.World.Y, 12);
            Assert.Equal(pad.Kind, q.Kind);
        }

        // The component VALUE travels (the reader learned the Value property for this).
        Assert.Equal("330", back.Schematic.Find("R1")!.Value);

        // EngrCAD-native layer names mapped to KiCad's: "Top" copper is F.Cu on the way back.
        Assert.All(back.Traces.Where(t => t.Net == "SIG" && t.Points[0].Y >= -1),
            t => Assert.Equal("F.Cu", t.Layer));

        // The via, chord-for-chord traces, and the pour with its custom clearance + priority.
        var via = Assert.Single(back.Vias);
        Assert.Equal(0.4, via.DrillDiameter);
        Assert.Equal(0.8, via.PadDiameter);
        Assert.Equal("SIG", via.Net);
        Assert.Equal(
            layout.Traces.Sum(t => t.Points.Count - 1),
            back.Traces.Sum(t => t.Points.Count - 1));
        var pour = Assert.Single(back.Pours);
        Assert.Equal("VCC", pour.Net);
        Assert.Equal(2, pour.Priority);
        Assert.Equal(0.3, pour.Clearance);
        Assert.Equal(4, pour.Outline!.Count);

        // And the copper still JOINS what the schematic declares.
        Assert.True(back.Connectivity().Of("SIG").IsConnected);

        // Same manufacturability verdict as the original.
        var rules = DrcRuleSet.Default with { MinAcuteAngleDegrees = 45 };
        Assert.Equal(PcbDrc.Check(layout, rules).Ok, PcbDrc.Check(back, rules).Ok);
    }

    [Fact]
    public void WriteReadWrite_IsAByteFixedPoint_AndDeterministic()
    {
        var layout = Routed();
        string once = KiCadPcbWriter.Write(layout);
        Assert.Equal(once, KiCadPcbWriter.Write(Routed()));                       // deterministic
        string twice = KiCadPcbWriter.Write(KiCadPcbReader.Read(once).Layout);
        Assert.Equal(once, twice);                                               // fixed point
    }

    [Fact]
    public void AnImportedKiCadBoard_ReExports_Stably()
    {
        // A real KiCad fixture: import → export keeps the file's own layer names VERBATIM, the
        // re-import agrees about the nets, and the exported form is its own fixed point.
        var imported = KiCadPcbReader.Read(KiCadPcbFixtures.Board).Layout;
        string exported = KiCadPcbWriter.Write(imported);
        Assert.Contains("\"F.Cu\"", exported);
        var back = KiCadPcbReader.Read(exported).Layout;
        Assert.Equal(NetPartition(imported), NetPartition(back));
        Assert.Equal(imported.Placements.Count, back.Placements.Count);
        Assert.Equal(exported, KiCadPcbWriter.Write(back));
    }

    [Fact]
    public void AFourLayerBoard_NamesItsInnerLayers()
    {
        var board = new PcbBoard(
            [new Vector2d(0, 0), new Vector2d(30, 0), new Vector2d(30, 20), new Vector2d(0, 20)],
            1.6, PcbStackup.Layers(1.6, ("Mid1", 1.1), ("Mid2", 0.5)));
        var sch = new Schematic("four");
        sch.Add("R1", PcbFixtures.SmdResistor(), "1k");
        var layout = new PcbLayout(sch, board);
        layout.Place("R1", 15, 10, 0);

        string text = KiCadPcbWriter.Write(layout);
        Assert.Contains("\"In1.Cu\"", text);
        Assert.Contains("\"In2.Cu\"", text);
        var back = KiCadPcbReader.Read(text).Layout;
        Assert.Equal(4, back.Board.Stackup.Coppers.Count);
        Assert.Equal("F.Cu", back.Board.Stackup.Top.Name);
        Assert.Equal("B.Cu", back.Board.Stackup.Bottom.Name);
    }

    [Fact]
    public void WhatTheFormatDoesNotCarry_IsReported_AndFreeHolesRefuseByName()
    {
        var layout = Routed();
        layout.WithFabrication(StandardFabSpecs.TwoLayerFr4Hasl);
        KiCadPcbWriter.Write(layout, out var diagnostics);
        Assert.Contains(diagnostics, d => d.Contains("fabrication"));
        Assert.Contains(diagnostics, d => d.Contains("re-fills"));

        // The shared fixture board carries free mounting holes — the KiCad idiom (an NPTH
        // footprint pad) would re-import as a PLATED pad, a silent copper change, so it refuses.
        var holes = Assert.Throws<ArgumentException>(
            () => KiCadPcbWriter.Write(PcbFixtures.Layout()));
        Assert.Contains("hole", holes.Message);
    }
}
