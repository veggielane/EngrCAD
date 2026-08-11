using System.Linq;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Ecad;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Whole-board KiCad <c>.kicad_pcb</c> import. The bar is higher than usual — an import that connects
/// the wrong pads is a silent failure — so the HEADLINE oracle is NET CONNECTIVITY: the pads' own net
/// tags reconstruct what KiCad intended, and <see cref="PcbConnectivity"/> confirms the imported copper
/// (tracks / vias / zone) actually joins them. Alongside it: a DRC-clean import (a real board is
/// clean) with a known-violation fixture FOUND, exact pad geometry from the file's mm coordinates, and
/// the imported copper round-tripping to Gerber and back.
/// </summary>
public sealed class KiCadPcbImportTests
{
    // A rule set for a board carrying a copper POUR with thermal reliefs: the relief spokes meet the
    // flood at ~78° corners, so the acid-trap threshold sits below that (the standard choice for a
    // board with 45° routing and pours). Every other length is the nominal default.
    private static readonly DrcRuleSet Rules = DrcRuleSet.Default with { MinAcuteAngleDegrees = 45 };

    // ==== the headline: net connectivity matches KiCad's intent ==============

    [Fact]
    public void CleanBoard_EveryNetConnects_AsKiCadIntended()
    {
        var pcb = KiCadPcbReader.Read(KiCadPcbFixtures.Board);
        var conn = pcb.Layout.Connectivity();

        // VCC: R1.1 -> J1.1 joined by a track. SIG: R1.2 -> C1.1 joined by two tracks. GND: C1.2 <-> J1.2
        // joined by the ground zone. All three carry two pads and are fully connected.
        Assert.True(conn.Of("VCC").IsConnected, conn.Of("VCC").ToString());
        Assert.True(conn.Of("SIG").IsConnected, conn.Of("SIG").ToString());
        Assert.True(conn.Of("GND").IsConnected, conn.Of("GND").ToString());
        Assert.Equal(2, conn.Of("VCC").PadCount);
        Assert.Equal(2, conn.Of("SIG").PadCount);
        Assert.Equal(2, conn.Of("GND").PadCount);

        // The whole board is fully routed — nothing is a ratsnest.
        Assert.Empty(conn.Unrouted);
    }

    [Fact]
    public void GroundZone_JoinsEveryGndPad_AndRemovingItLeavesGndUnrouted()
    {
        // The zone is the ONLY copper joining GND's two pads (C1.2 an F.Cu SMD land, J1.2 a
        // through-hole). Remove it and GND falls into two islands — the mutation that proves the zone
        // is what connects them (the connectivity check has teeth).
        var withZone = KiCadPcbReader.Read(KiCadPcbFixtures.Board);
        Assert.Equal(1, withZone.ZoneCount);
        Assert.True(withZone.Layout.Connectivity().Of("GND").IsConnected);

        var noZone = KiCadPcbReader.Read(KiCadPcbFixtures.BoardNoZone);
        Assert.Equal(0, noZone.ZoneCount);
        var gnd = noZone.Layout.Connectivity().Of("GND");
        Assert.False(gnd.IsConnected);
        Assert.Equal(2, gnd.ComponentCount);
        Assert.Contains("GND", noZone.Layout.Connectivity().Unrouted);

        // The signal nets stay routed either way (they are joined by tracks, not the zone).
        Assert.True(noZone.Layout.Connectivity().Of("VCC").IsConnected);
        Assert.True(noZone.Layout.Connectivity().Of("SIG").IsConnected);
    }

    // ==== DRC: a real board is clean; a known violation is FOUND ==============

    [Fact]
    public void CleanBoard_PassesTheDrc()
    {
        var pcb = KiCadPcbReader.Read(KiCadPcbFixtures.Board);
        var report = PcbDrc.Check(pcb.Layout, Rules);

        Assert.True(report.Ok,
            "the imported board should be DRC-clean; violations: "
            + string.Join(" | ", report.Violations.Select(v => v.ToString())));
        // Being fully routed, the ratsnest is empty too.
        Assert.Empty(report.Ratsnest);
    }

    [Fact]
    public void ShortedBoard_TheDrcFindsTheShort()
    {
        // Two SMD pads on nets A and B overlap — a copper short.
        var pcb = KiCadPcbReader.Read(KiCadPcbFixtures.ShortedBoard);
        var report = PcbDrc.Check(pcb.Layout, DrcRuleSet.Default);

        Assert.False(report.Ok);
        Assert.True(report.Has(DrcRule.Short) || report.Has(DrcRule.Clearance),
            "expected a short/clearance violation between nets A and B");
        Assert.Contains(report.Violations, v => v.Message.Contains("'A'") && v.Message.Contains("'B'"));
    }

    // ==== exact pad geometry from the file's mm coordinates ==================

    [Fact]
    public void PadCentres_AreExactFromTheFileCoordinates()
    {
        var pcb = KiCadPcbReader.Read(KiCadPcbFixtures.Board);
        var pads = pcb.Layout.PlacedPads();

        // R1 at (6, 6, 0): pad "1" local (-0.9125, 0) -> (5.0875, 6); pad "2" -> (6.9125, 6).
        AssertPad(pads, "R1", "1", 5.0875, 6.0, PadKind.Smd, drill: 0);
        AssertPad(pads, "R1", "2", 6.9125, 6.0, PadKind.Smd, drill: 0);
        // C1 at (24, 6, 0).
        AssertPad(pads, "C1", "1", 23.0875, 6.0, PadKind.Smd, drill: 0);
        AssertPad(pads, "C1", "2", 24.9125, 6.0, PadKind.Smd, drill: 0);
        // J1 at (15, 18, 0): through-hole, drill 1.0. pad "1" local (-1.27,0) -> (13.73, 18).
        AssertPad(pads, "J1", "1", 13.73, 18.0, PadKind.ThroughHole, drill: 1.0);
        AssertPad(pads, "J1", "2", 16.27, 18.0, PadKind.ThroughHole, drill: 1.0);
    }

    [Fact]
    public void RotatedFootprint_PadCentreFollowsTheRotation()
    {
        // R1 at (10, 10, 90): pad "1" local (-1, 0) rotated +90° CCW -> (0, -1) -> world (10, 9).
        var pcb = KiCadPcbReader.Read(KiCadPcbFixtures.RotatedFootprintBoard);
        var pad = pcb.Layout.PlacedPads().Single(p => p.Reference == "R1" && p.PadNumber == "1");
        Assert.Equal(10.0, pad.World.X, 9);
        Assert.Equal(9.0, pad.World.Y, 9);
    }

    // ==== the board: thickness, stackup, outline, footprints =================

    [Fact]
    public void Board_CarriesThicknessStackupOutlineAndFootprints()
    {
        var pcb = KiCadPcbReader.Read(KiCadPcbFixtures.Board);

        Assert.Equal("demo-board", pcb.BoardName);
        Assert.Equal(1.6, pcb.Layout.Board.Thickness);

        // The copper stackup is F.Cu (top) and B.Cu (bottom) — the two .Cu layers, top-most first.
        var coppers = pcb.Layout.Board.Stackup.Coppers;
        Assert.Equal(new[] { "F.Cu", "B.Cu" }, coppers.Select(c => c.Name));
        Assert.Equal("F.Cu", pcb.Layout.Board.Stackup.Top.Name);
        Assert.Equal("B.Cu", pcb.Layout.Board.Stackup.Bottom.Name);

        // The 30 x 24 outline (exact area) from the four Edge.Cuts lines.
        Assert.Equal(30.0 * 24.0, pcb.Layout.Board.OutlineArea(), 6);

        // Three footprints, all top-side, at their file positions.
        Assert.Equal(3, pcb.FootprintCount);
        var placements = pcb.Layout.Placements.ToDictionary(p => p.Reference);
        Assert.All(placements.Values, p => Assert.Equal(CopperSide.Top, p.Side));
        Assert.Equal((6.0, 6.0), (placements["R1"].X, placements["R1"].Y));
        Assert.Equal((15.0, 18.0), (placements["J1"].X, placements["J1"].Y));

        // Three tracks, one via, one zone, three nets (GND/VCC/SIG carry pads; "" carries none).
        Assert.Equal(3, pcb.TrackCount);
        Assert.Equal(1, pcb.ViaCount);
        Assert.Equal(1, pcb.ZoneCount);
        Assert.Equal(3, pcb.NetCount);   // GND, VCC, SIG (net 0 "" attaches to nothing)
    }

    [Fact]
    public void BottomFootprintAndArcTrack_AreCovered()
    {
        var pcb = KiCadPcbReader.Read(KiCadPcbFixtures.ArcAndBottomBoard);

        // The two footprints sit on the bottom side (their layer is B.Cu).
        Assert.All(pcb.Layout.Placements, p => Assert.Equal(CopperSide.Bottom, p.Side));

        // The arc track flattened to a polyline (more than 2 points) and joined the two pads on N.
        Assert.Equal(1, pcb.TrackCount);
        Assert.True(pcb.Layout.Traces[0].Points.Count > 2);
        Assert.True(pcb.Layout.Connectivity().Of("N").IsConnected);
        Assert.Contains(pcb.Diagnostics, d => d.Contains("arc track"));
    }

    // ==== the imported copper round-trips to Gerber and back =================

    [Fact]
    public void ImportedCopper_RoundTripsToGerber()
    {
        var pcb = KiCadPcbReader.Read(KiCadPcbFixtures.Board);
        var model = PcbCopperModel.FromLayout(pcb.Layout);
        var output = PcbGerberExport.Generate(pcb.Layout, "demo");

        // Each copper layer's Gerber decodes back to copper equal — by AREA and by SYMMETRIC
        // DIFFERENCE (the strong form) — to the copper model's copper on that layer.
        foreach (var layerFile in output.CopperLayers)
        {
            var decoded = GerberReader.Read(layerFile.Gerber).Copper;
            var modelUnion = CurvedRegion2dBoolean.UnionAll(
                [.. model.Copper.Where(f => f.Layer == layerFile.Layer).Select(f => f.Region)]);

            double modelArea = modelUnion.Sum(r => r.Area);
            double decodedArea = decoded.Sum(r => r.Area);
            double reference = Math.Max(modelArea, 1e-30);
            Assert.True(Math.Abs(modelArea - decodedArea) <= 1e-4 * reference,
                $"layer '{layerFile.Layer}': copper area {decodedArea:g9} recovered, model {modelArea:g9}");

            double symmetric =
                CurvedRegion2dBoolean.Difference(modelUnion, decoded).Sum(r => r.Area)
                + CurvedRegion2dBoolean.Difference(decoded, modelUnion).Sum(r => r.Area);
            Assert.True(symmetric <= 1e-4 * reference,
                $"layer '{layerFile.Layer}': recovered copper differs by {symmetric:g9}");
        }

        // The drill hits (the two through-hole pads + the via) recover exactly.
        var drills = ExcellonReader.Read(output.Drill);
        Assert.Equal(model.Drills.Count, drills.Count);
        Assert.Equal(3, model.Drills.Count);   // J1.1, J1.2, the GND via
    }

    // ==== determinism ========================================================

    [Fact]
    public void Read_IsDeterministic()
    {
        var a = PcbGerberExport.Generate(KiCadPcbReader.Read(KiCadPcbFixtures.Board).Layout, "demo");
        var b = PcbGerberExport.Generate(KiCadPcbReader.Read(KiCadPcbFixtures.Board).Layout, "demo");
        for (int i = 0; i < a.CopperLayers.Count; i++)
            Assert.Equal(a.CopperLayers[i].Gerber, b.CopperLayers[i].Gerber);
        Assert.Equal(a.OutlineGerber, b.OutlineGerber);
        Assert.Equal(a.Drill, b.Drill);
    }

    // ==== refusals BY NAME ===================================================

    [Fact]
    public void NotAKiCadBoard_IsRefusedByName()
    {
        var ex = Assert.Throws<FormatException>(() => KiCadPcbReader.Read(KiCadFixtures.ResistorSym));
        Assert.Contains("kicad_pcb", ex.Message);
    }

    [Fact]
    public void AFootprintHandedToTheBoardReader_IsRefusedByName()
    {
        var ex = Assert.Throws<FormatException>(() => KiCadPcbReader.Read(KiCadFixtures.ResistorMod));
        Assert.Contains("kicad_pcb", ex.Message);
        Assert.Contains("footprint", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("(kicad_pcb")]                 // unbalanced '('
    [InlineData("(kicad_pcb \"unterminated")]  // unterminated string
    [InlineData("not-a-list")]                 // top-level atom
    public void MalformedSExpression_IsRefusedByName(string text) =>
        Assert.Throws<FormatException>(() => KiCadPcbReader.Read(text));

    // ==== the component reader stays bit-identical (nothing shared moved) =====

    [Fact]
    public void ComponentReader_IsUnchanged()
    {
        // The whole-board reader is a NEW reading path; it touches neither KiCadFootprintReader nor
        // SExpr, so the component-load path is bit-identical. Re-assert the resistor's exact geometry
        // (the same numbers KiCadImportTests pins).
        var footprint = KiCadFootprintReader.Read(KiCadFixtures.ResistorMod).Footprint;
        Assert.Equal("R_0805_2012Metric", footprint.Name);
        Assert.Equal(2, footprint.Pads.Count);
        Assert.Equal(-0.9125, footprint.Pads[0].Center.X);
        Assert.Equal(1.025, footprint.Pads[0].Width);
        Assert.Equal(PadShape.RoundedRectangle, footprint.Pads[0].Shape);
    }

    // ---- helpers ------------------------------------------------------------

    private static void AssertPad(
        IReadOnlyList<PlacedPad> pads, string reference, string number,
        double x, double y, PadKind kind, double drill)
    {
        var pad = pads.Single(p => p.Reference == reference && p.PadNumber == number);
        Assert.Equal(x, pad.World.X, 9);
        Assert.Equal(y, pad.World.Y, 9);
        Assert.Equal(kind, pad.Kind);
        Assert.Equal(drill, pad.DrillDiameter, 9);
    }
}
