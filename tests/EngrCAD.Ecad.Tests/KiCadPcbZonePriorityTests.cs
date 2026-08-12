using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// A KiCad <c>.kicad_pcb</c> zone carries <c>(priority N)</c>, which maps straight onto
/// <see cref="CopperPour.Priority"/> — so an imported board with two overlapping zones resolves the
/// overlap the same way KiCad drew it (the higher-priority zone wins, no short). A zone with no
/// priority imports as the default 0.
/// </summary>
public sealed class KiCadPcbZonePriorityTests
{
    // A board with a GND zone (priority 2, left+centre) and a VCC zone (priority 1, centre+right)
    // overlapping in x ∈ [12, 18], each with one same-net pad so both pours connect.
    private const string TwoZoneBoard = """
(kicad_pcb
  (version 20221018)
  (generator pcbnew)
  (general (thickness 1.6))
  (paper "A4")
  (layers
    (0 "F.Cu" signal)
    (31 "B.Cu" signal)
    (44 "Edge.Cuts" user)
  )
  (setup)
  (net 0 "")
  (net 1 "GND")
  (net 2 "VCC")

  (footprint "G" (layer "F.Cu")
    (at 5 12 0)
    (property "Reference" "G1" (at 0 -1.5 0) (layer "F.SilkS"))
    (pad "1" smd rect (at 0 0) (size 1 1) (layers "F.Cu" "F.Mask") (net 1 "GND"))
  )
  (footprint "V" (layer "F.Cu")
    (at 25 12 0)
    (property "Reference" "V1" (at 0 -1.5 0) (layer "F.SilkS"))
    (pad "1" smd rect (at 0 0) (size 1 1) (layers "F.Cu" "F.Mask") (net 2 "VCC"))
  )

  (gr_line (start 0 0) (end 30 0) (stroke (width 0.1) (type solid)) (layer "Edge.Cuts"))
  (gr_line (start 30 0) (end 30 24) (stroke (width 0.1) (type solid)) (layer "Edge.Cuts"))
  (gr_line (start 30 24) (end 0 24) (stroke (width 0.1) (type solid)) (layer "Edge.Cuts"))
  (gr_line (start 0 24) (end 0 0) (stroke (width 0.1) (type solid)) (layer "Edge.Cuts"))

  (zone (net 1) (net_name "GND") (layer "F.Cu") (priority 2)
    (polygon (pts (xy 0 2) (xy 18 2) (xy 18 22) (xy 0 22)))
  )
  (zone (net 2) (net_name "VCC") (layer "F.Cu") (priority 1)
    (polygon (pts (xy 12 2) (xy 30 2) (xy 30 22) (xy 12 22)))
  )
)
""";

    [Fact]
    public void ZonePriority_IsImportedAndResolvesTheOverlap()
    {
        var pcb = KiCadPcbReader.Read(TwoZoneBoard);
        Assert.Equal(2, pcb.ZoneCount);

        // The priorities came straight off the file.
        Assert.Equal(2, pcb.Layout.Pours.Single(p => p.Net == "GND").Priority);
        Assert.Equal(1, pcb.Layout.Pours.Single(p => p.Net == "VCC").Priority);

        // The overlap (x ∈ [12, 18]) belongs to the higher-priority GND zone, and only to it.
        var model = PcbCopperModel.FromLayout(pcb.Layout);
        var mid = new Vector2d(15, 12);
        var nets = model.Copper
            .Where(f => f.Layer == "F.Cu" && f.Net is not null && f.Region.Contains(mid))
            .Select(f => f.Net!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string> { "GND" }, nets);

        // And the two zones do not short (the lower one is carved back by its clearance).
        Assert.Empty(PcbDrc.Check(pcb.Layout).OfRule(DrcRule.Short));
    }

    [Fact]
    public void AZoneWithNoPriority_ImportsAsTheDefaultZero()
    {
        var pcb = KiCadPcbReader.Read(KiCadPcbFixtures.Board);
        Assert.Equal(0, pcb.Layout.Pours.Single().Priority);
    }
}
