using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// Holder collision over finished geometry: a plate carrying a 12-tall boss makes the
/// minimum stickout a CLOSED FORM — a floor point whose holder disc overlaps the boss needs
/// the full boss-top height above its own z, so MinimumStickout is exactly 12 — and the
/// collision set is LOCAL (only points within the holder radius of the boss footprint can
/// collide), which is what separates a real disc query from a bounds test.
/// </summary>
public class CncHolderTests
{
    // Plate top at z = 0 (x -50..50, y -30..30) with a SMALL boss (x 15..25, y -5..5,
    // z 0..12) well inside it — small and centred deliberately: the raster runs one grid
    // step past the part bounds and the ball's CL there dips BELOW the top (wrapping the
    // outer edge — exactly −1 at the corner), so a boss a rim point's disc can reach adds
    // that dip to the minimum stickout and the closed form stops being the boss height.
    private static Shape SteppedPart() =>
        Shape.Box(100, 60, 6).Translate(0, 0, -3)
            .Union(Shape.Box(10, 10, 12).Translate(20, 0, 6));

    private static MillOperation Finishing(Shape part) =>
        CncSurfacing.Raster(part, new MillTool(6), sampleStep: 2, name: "finish");

    [Fact]
    public void TheMinimumStickout_IsTheBossHeight_ClosedForm()
    {
        var part = SteppedPart();
        var op = Finishing(part);
        // Stickout 8 is 4 short of the boss: the disc over a floor point near the wall
        // needs the full 12 above z = 0.
        var report = CncHolder.Check(part, op, new ToolHolder(Diameter: 20, StickoutLength: 8));

        Assert.False(report.Ok);
        Assert.Equal(12.0, report.MinimumStickout, 6);
        Assert.True(report.PointsChecked > 0);

        // Every collision is LOCAL to the boss: the disc (radius 10) must overlap the boss
        // footprint (x >= 15), so no colliding point sits left of x = 5.
        Assert.All(report.Collisions, c => Assert.True(c.Point.X >= 5 - 1e-9,
            $"collision at x = {c.Point.X:0.###} is beyond the holder's reach of the boss"));
        // And the worst deficit is exactly the shortfall: 12 − (0 + 8) = 4 at a floor point.
        Assert.Equal(4.0, report.Collisions.Max(c => c.Deficit), 6);
    }

    [Fact]
    public void AStickoutAtTheMinimum_Clears_AndBelowFails()
    {
        var part = SteppedPart();
        var op = Finishing(part);

        // Zero clearance is resting contact, not a collision (the interference rule) —
        // so the reported minimum is itself a passing setup.
        Assert.True(CncHolder.Check(part, op, new ToolHolder(20, 12)).Ok);
        Assert.True(CncHolder.Check(part, op, new ToolHolder(20, 12.5)).Ok);
        Assert.False(CncHolder.Check(part, op, new ToolHolder(20, 11.5)).Ok);
    }

    [Fact]
    public void ANarrowerHolder_ShrinksTheCollisionBand_ByExactlyTheRadiusDifference()
    {
        var part = SteppedPart();
        var op = Finishing(part);
        var wide = CncHolder.Check(part, op, new ToolHolder(20, 8));
        var narrow = CncHolder.Check(part, op, new ToolHolder(12, 8));

        // A radius-6 disc reaches the boss only from x >= 9; radius 10 from x >= 5.
        Assert.All(narrow.Collisions, c => Assert.True(c.Point.X >= 9 - 1e-9));
        Assert.True(narrow.Collisions.Count < wide.Collisions.Count);
        // The minimum stickout is the boss height for BOTH — reach decides which points
        // collide, not how tall the obstacle is.
        Assert.Equal(12.0, narrow.MinimumStickout, 6);
    }

    [Fact]
    public void DrillingBelowTheSurface_NeedsStickoutOfDepthPlusRise()
    {
        // A drill pass descends below the top face, so the holder needs the hole depth as
        // well: required z at the hole is the plate top (0), the deepest point is −5, so
        // the minimum stickout is exactly 5 on a flat plate.
        var plate = Shape.Box(40, 30, 10).Translate(0, 0, -5);
        var drill = CncMill.Drill(
            [new Vector2d(0, 0)], new MillTool(4), depth: 5);
        var report = CncHolder.Check(plate, drill, new ToolHolder(16, 3));

        Assert.False(report.Ok);
        Assert.Equal(5.0, report.MinimumStickout, 6);
        Assert.True(CncHolder.Check(plate, drill, new ToolHolder(16, 5.01)).Ok);
    }

    [Fact]
    public void TheCheck_IsDeterministic_AndRefusesByName()
    {
        var part = SteppedPart();
        var op = Finishing(part);
        var a = CncHolder.Check(part, op, new ToolHolder(20, 8));
        var b = CncHolder.Check(part, op, new ToolHolder(20, 8));
        Assert.Equal(a.Collisions.Count, b.Collisions.Count);
        for (int i = 0; i < a.Collisions.Count; i++)
            Assert.Equal(a.Collisions[i], b.Collisions[i]);

        // A holder no wider than its cutter is a vacuous check, refused with the reason.
        Assert.Contains("flank", Assert.Throws<ArgumentException>(() =>
            CncHolder.Check(part, op, new ToolHolder(6, 8))).Message);
        Assert.Contains("StickoutLength", Assert.Throws<ArgumentException>(() =>
            CncHolder.Check(part, op, new ToolHolder(20, 0))).Message);
        var other = CncSurfacing.Raster(part, new MillTool(8), sampleStep: 4);
        Assert.Contains("one tool diameter", Assert.Throws<ArgumentException>(() =>
            CncHolder.Check(part, [op, other], new ToolHolder(20, 8))).Message);
    }
}
