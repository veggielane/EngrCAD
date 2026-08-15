using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// Sequential (complete-one-object) printing: ascending-height order, clearance checked
/// conservatively on the bounds, at most one over-gantry part and it prints last, and a
/// combined program whose handovers hop above everything completed — the decoder reading
/// the whole file with the per-part filament totals conserved.
/// </summary>
public class FdmSequentialTests
{
    private static PrinterProfile Profile() => new(
        NozzleDiameter: 0.8, LayerHeight: 0.4, WallCount: 1, InfillDensity: 0);

    [Fact]
    public void ThePlan_OrdersAscendingByHeight_WithTheTallPartLast()
    {
        var placed = new[]
        {
            Shape.Box(10, 10, 8).Translate(0, 0, 4),
            Shape.Box(10, 10, 2).Translate(40, 0, 1),
            Shape.Box(10, 10, 5).Translate(80, 0, 2.5),
        };
        var plan = FdmSequential.Plan(placed);
        Assert.Equal(new[] { 1, 2, 0 }, plan.Order);
        Assert.Equal(8, plan.Heights[0], 9);
        Assert.True(plan.MinPairClearance >= 30 - 1e-9);
    }

    [Fact]
    public void TheRefusals_NameThePairAndTheOverGantryParts()
    {
        // 10-wide boxes 20 apart centre-to-centre: a 10 gap against a 25 clearance.
        var close = new[]
        {
            Shape.Box(10, 10, 4).Translate(0, 0, 2),
            Shape.Box(10, 10, 4).Translate(20, 0, 2),
        };
        var refusal = Assert.Throws<ArgumentException>(() => FdmSequential.Plan(close));
        Assert.Contains("Parts 0 and 1", refusal.Message);
        Assert.Contains("10", refusal.Message);

        var twoTall = new[]
        {
            Shape.Box(10, 10, 40).Translate(0, 0, 20),
            Shape.Box(10, 10, 40).Translate(50, 0, 20),
        };
        Assert.Contains("only the LAST", Assert.Throws<ArgumentException>(() =>
            FdmSequential.Plan(twoTall)).Message);

        // ONE tall part is fine — ascending height puts it last with nothing arranged.
        var oneTall = new[]
        {
            Shape.Box(10, 10, 40).Translate(0, 0, 20),
            Shape.Box(10, 10, 4).Translate(50, 0, 2),
        };
        Assert.Equal(new[] { 1, 0 }, FdmSequential.Plan(oneTall).Order);
    }

    [Fact]
    public void TheCombinedProgram_ConservesFilament_AndDropsZExactlyAtTheHandover()
    {
        var placed = FdmPlating.Arrange(
            [Shape.Box(12, 12, 4), Shape.Box(12, 12, 6)], 120, 120, gap: 30);
        var print = FdmSequential.Slice(placed, Profile());
        string gcode = FdmSequential.WriteGcode(print);
        var decoded = GcodeReader.Read(gcode);

        // The decoder reads the whole file (G92 E0 handovers included) and the filament
        // total is the sum of the parts' own — the extrusion identity across the seam.
        double expected = print.Parts.Sum(p => p.FilamentUsed);
        Assert.Equal(expected, decoded.FilamentUsed, expected * 1e-6);

        // The deposition Z sequence rises through part 1, then DROPS back to the first
        // layer for part 2 — exactly once, at the handover.
        var zs = decoded.LayerZs;
        int drops = zs.Zip(zs.Skip(1)).Count(pair => pair.Second < pair.First);
        Assert.Equal(1, drops);
        Assert.Equal(print.Parts[0].Layers.Count + print.Parts[1].Layers.Count, zs.Count);
    }

    [Fact]
    public void TheHandover_HopsAboveTheCompletedPart_BeforeMovingAcross()
    {
        var placed = FdmPlating.Arrange(
            [Shape.Box(12, 12, 4), Shape.Box(12, 12, 6)], 120, 120, gap: 30);
        var print = FdmSequential.Slice(placed, Profile());
        var decoded = GcodeReader.Read(FdmSequential.WriteGcode(print));

        // Part 1 (printed first) is the shorter, 4 tall. Between part 1's LAST deposition
        // and part 2's FIRST, every XY travel must run at the hop height — above everything
        // completed — never at layer height (part 1's own initial approach from the G-code
        // origin is exempt: nothing is completed yet).
        double firstHeight = print.Parts[0].Layers[^1].Z;
        var moves = decoded.Moves;
        int lastOfFirst = -1, firstOfSecond = -1;
        for (int i = 0; i < moves.Count; i++)
            if (moves[i].Kind == GcodeMoveKind.Deposition)
            {
                if (lastOfFirst >= 0 && firstOfSecond < 0 && moves[i].To.Z < moves[lastOfFirst].To.Z)
                    firstOfSecond = i;
                if (firstOfSecond < 0)
                    lastOfFirst = i;
            }
        Assert.True(lastOfFirst > 0 && firstOfSecond > lastOfFirst);
        var crossing = moves.Skip(lastOfFirst + 1).Take(firstOfSecond - lastOfFirst - 1)
            .Where(m => m.Kind == GcodeMoveKind.Travel && m.XyLength > 1).ToList();
        Assert.NotEmpty(crossing);
        Assert.All(crossing, m =>
            Assert.True(m.From.Z >= firstHeight + 2 - 1e-9,
                $"a handover travel at z = {m.From.Z:0.###} under the hop"));
    }
}
