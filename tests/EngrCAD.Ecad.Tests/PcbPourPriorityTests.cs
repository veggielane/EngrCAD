using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Copper-pour PRIORITY, the fix for overlapping pours. Two different-net pours whose outlines overlap
/// would short if both flooded the shared area; priority resolves it — the higher-priority pour fills
/// first and keeps its copper, and the lower-priority one is carved back by its own clearance around it.
/// The mutation with teeth: which net covers the shared area FLIPS with the priority, and NEITHER
/// configuration shorts. Ties break by declaration order; non-overlapping pours are unaffected; a
/// default (priority-0) pour writes no priority key (byte-identical persistence).
/// </summary>
public sealed class PcbPourPriorityTests
{
    private static PartDefinition Smd2() => new(
        "R2", "R",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
        new Footprint("R2", [
            Pad.Smd("1", new Vector2d(-1.0, 0), 1.0, 1.0, PadShape.Rectangular),
            Pad.Smd("2", new Vector2d(1.0, 0), 1.0, 1.0, PadShape.Rectangular),
        ]));

    private static PcbBoard Board() => new(
        [new Vector2d(-20, -15), new Vector2d(20, -15), new Vector2d(20, 15), new Vector2d(-20, 15)], 1.6);

    private static Vector2d[] Rect(double x0, double x1, double y0 = -13, double y1 = 13) =>
        [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];

    /// <summary>Two pours, GND over the LEFT+centre and VCC over the CENTRE+right (overlapping in the
    /// centre column), with a GND part in the left region and a VCC part in the right region so each
    /// pour reaches its own net. When <paramref name="disjoint"/> the two outlines do not overlap.</summary>
    private static PcbLayout Two(int gndPriority, int vccPriority, bool disjoint = false)
    {
        var sch = new Schematic("pp");
        var g = sch.Add("G", Smd2());
        var v = sch.Add("V", Smd2());
        sch.Connect("GND", g.Pin("1"), g.Pin("2"));
        sch.Connect("VCC", v.Pin("1"), v.Pin("2"));
        var layout = new PcbLayout(sch, Board());
        layout.Place("G", -15, 0);   // GND pads at (-16,0),(-14,0)
        layout.Place("V", 15, 0);    // VCC pads at (14,0),(16,0)

        var (gndOutline, vccOutline) = disjoint
            ? (Rect(-19, -2), Rect(2, 19))    // a 4 mm gap in the middle
            : (Rect(-19, 3), Rect(-3, 19));   // overlap in x ∈ [-3, 3]
        layout.AddPour(new CopperPour("GND", "Top", Outline: gndOutline, Priority: gndPriority));
        layout.AddPour(new CopperPour("VCC", "Top", Outline: vccOutline, Priority: vccPriority));
        return layout;
    }

    private static HashSet<string> NetsCovering(PcbCopperModel m, in Vector2d p, string layer = "Top")
    {
        var pt = p;
        return m.Copper
            .Where(f => f.Layer == layer && f.Net is not null && f.Region.Contains(pt))
            .Select(f => f.Net!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static double PourArea(PcbCopperModel m, string net, string layer = "Top") =>
        m.Copper.Where(f => f.Layer == layer && f.Net == net && f.Source.StartsWith("pour", StringComparison.Ordinal))
            .Sum(f => f.Region.Area);

    // ==== 1) priority decides the overlap winner, and neither config shorts =====

    [Fact]
    public void PriorityDecidesTheOverlapWinner_AndNeitherConfigShorts()
    {
        var mid = new Vector2d(0, 0);   // in the shared centre column

        var gndWins = PcbCopperModel.FromLayout(Two(gndPriority: 10, vccPriority: 0));
        var vccWins = PcbCopperModel.FromLayout(Two(gndPriority: 0, vccPriority: 10));

        // The centre belongs to whichever pour has the higher priority — and only to it.
        Assert.Equal(new HashSet<string> { "GND" }, NetsCovering(gndWins, mid));
        Assert.Equal(new HashSet<string> { "VCC" }, NetsCovering(vccWins, mid));

        // Whoever wins, the two pours never overlap — no short in EITHER configuration.
        Assert.Empty(PcbDrc.Check(gndWins).OfRule(DrcRule.Short));
        Assert.Empty(PcbDrc.Check(vccWins).OfRule(DrcRule.Short));
    }

    // ==== 2) each pour's exclusive region is always its own ======================

    [Fact]
    public void EachPoursExclusiveRegionIsAlwaysItsOwn_RegardlessOfPriority()
    {
        foreach (var (gp, vp) in new[] { (10, 0), (0, 10) })
        {
            var m = PcbCopperModel.FromLayout(Two(gp, vp));
            Assert.Contains("GND", NetsCovering(m, new Vector2d(-10, 0)));   // left-only region
            Assert.DoesNotContain("VCC", NetsCovering(m, new Vector2d(-10, 0)));
            Assert.Contains("VCC", NetsCovering(m, new Vector2d(10, 0)));    // right-only region
            Assert.DoesNotContain("GND", NetsCovering(m, new Vector2d(10, 0)));
        }
    }

    // ==== 3) equal priority breaks ties by declaration order ====================

    [Fact]
    public void EqualPriority_BreaksTiesByDeclarationOrder_FirstDeclaredWins()
    {
        // GND is declared first, so at equal priority it wins the overlap — and still no short.
        var m = PcbCopperModel.FromLayout(Two(gndPriority: 0, vccPriority: 0));
        Assert.Equal(new HashSet<string> { "GND" }, NetsCovering(m, new Vector2d(0, 0)));
        Assert.Empty(PcbDrc.Check(m).OfRule(DrcRule.Short));
    }

    // ==== 4) non-overlapping pours are unaffected by priority ===================

    [Fact]
    public void NonOverlappingPours_AreUnaffectedByPriority()
    {
        var a = PcbCopperModel.FromLayout(Two(gndPriority: 10, vccPriority: 0, disjoint: true));
        var b = PcbCopperModel.FromLayout(Two(gndPriority: 0, vccPriority: 10, disjoint: true));

        // With disjoint outlines there is nothing to carve, so each net keeps the SAME area whichever
        // pour has priority — swapping the priority moves no copper.
        Assert.Equal(PourArea(a, "GND"), PourArea(b, "GND"), 9);
        Assert.Equal(PourArea(a, "VCC"), PourArea(b, "VCC"), 9);
        Assert.True(PourArea(a, "GND") > 0 && PourArea(a, "VCC") > 0);
    }

    // ==== 5) determinism ========================================================

    [Fact]
    public void PourPriorityIsDeterministic()
    {
        var a = PcbCopperModel.FromLayout(Two(10, 0));
        var b = PcbCopperModel.FromLayout(Two(10, 0));
        Assert.Equal(PourArea(a, "GND"), PourArea(b, "GND"), 12);
        Assert.Equal(PourArea(a, "VCC"), PourArea(b, "VCC"), 12);
    }

    // ==== 6) persistence: priority round-trips; default writes no key ===========

    [Fact]
    public void PourPriorityRoundTrips_AndADefaultPourWritesNoKey()
    {
        // A default (priority-0) pour writes no "priority" key — byte-identical to a pre-priority file.
        var sch = new Schematic("d");
        var r = sch.Add("R", Smd2());
        sch.Connect("N", r.Pin("1"), r.Pin("2"));
        var def = new PcbLayout(sch, Board());
        def.Place("R", 0, 0);
        def.AddPour(new CopperPour("N", "Top"));
        Assert.DoesNotContain("priority", def.Save());

        // A stated priority rides in the file and is a save → load → save fixed point.
        var layout = Two(gndPriority: 7, vccPriority: 3);
        string once = layout.Save();
        Assert.Contains("priority", once);
        string twice = PcbLayout.Load(once).Save();
        Assert.Equal(once, twice);
        Assert.Equal(7, PcbLayout.Load(once).Pours[0].Priority);
        Assert.Equal(3, PcbLayout.Load(once).Pours[1].Priority);
    }
}
