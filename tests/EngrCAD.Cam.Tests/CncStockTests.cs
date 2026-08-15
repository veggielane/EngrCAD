using EngrCAD.Cam;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// The machined-stock simulation. The oracle with teeth is the DRILL identity: the bore
/// footprint is an inscribed 32-gon, polyhedral booleans are exact, so a drilled state's
/// volume is a closed form to round-off — where the pocket's is the stage-2 opening
/// measurement (stroked footprints are chorded, so that claim is a band, honestly).
/// </summary>
public class CncStockTests
{
    private static readonly MillTool Tool = new(Diameter: 3);

    [Fact]
    public void DrilledStock_MatchesTheInscribedNgonPrismExactly()
    {
        // Stock top at z = 0 (the 2.5D convention); two holes pecked to depth 4.
        var stock = Shape.Box(20, 20, 10).Translate(0, 0, -5);
        var drill = CncMill.Drill([new Vector2d(-5, 0), new Vector2d(5, 0)], Tool, depth: 4);

        var states = CncStock.Simulate(stock, [drill], states: 3);
        double r = Tool.Radius;
        double ngon = 32 / 2.0 * r * r * Math.Sin(2 * Math.PI / 32);
        double expected = 20 * 20 * 10 - 2 * ngon * 4;
        Assert.Equal(expected, states[^1].Shape.ToMesh().Volume(), expected * 1e-9);
    }

    [Fact]
    public void PocketStock_RemovesTheOpeningTimesTheDepth()
    {
        var stock = Shape.Box(20, 16, 8).Translate(0, 0, -4);
        var region = new Region2d([
            new Vector2d(-6, -4), new Vector2d(6, -4), new Vector2d(6, 4), new Vector2d(-6, 4)]);
        var pocket = CncMill.Pocket(region, Tool, depth: 3);

        var states = CncStock.Simulate(stock, [pocket], states: 5);

        // State 0 is the stock BY REFERENCE; volumes never increase as the cut advances.
        Assert.Same(stock, states[0].Shape);
        double previous = double.PositiveInfinity;
        foreach (var state in states)
        {
            double volume = state.Shape.ToMesh().Volume();
            Assert.True(volume <= previous + 1e-9,
                $"the stock grew from {previous:0.###} to {volume:0.###} at fraction {state.Fraction}");
            previous = volume;
        }

        // The final removal is the morphological opening's area times the depth: the
        // reachable area is region − (4 − π)r² in the corners (the stage-2 closed form).
        double r = Tool.Radius;
        double opening = region.Area - (4 - Math.PI) * r * r;
        double expected = 20 * 16 * 8 - opening * 3;
        double final = states[^1].Shape.ToMesh().Volume();
        Assert.True(Math.Abs(final - expected) < expected * 0.02,
            $"final stock {final:0.##} vs opening prediction {expected:0.##}");

        // A mid-cut state sits strictly between untouched and finished.
        double mid = states[2].Shape.ToMesh().Volume();
        Assert.InRange(mid, final + 1, 20 * 16 * 8 - 1);
    }

    [Fact]
    public void TabbedProfile_KeepsTheTabMaterial()
    {
        var stock = Shape.Box(30, 24, 6).Translate(0, 0, -3);
        var outline = new Region2d([
            new Vector2d(-8, -6), new Vector2d(8, -6), new Vector2d(8, 6), new Vector2d(-8, 6)]);
        var withTabs = CncMill.Profile(outline, Tool, depth: 6, ProfileSide.Outside,
            tabs: 3, tabHeight: 2, tabWidth: 4);
        var without = CncMill.Profile(outline, Tool, depth: 6, ProfileSide.Outside);

        double kept = CncStock.Simulate(stock, [withTabs], states: 2)[^1].Shape.ToMesh().Volume();
        double freed = CncStock.Simulate(stock, [without], states: 2)[^1].Shape.ToMesh().Volume();
        Assert.True(kept > freed + 1,
            $"tabs must leave material standing (with {kept:0.##} vs without {freed:0.##})");
    }

    [Fact]
    public void Simulation_IsDeterministic()
    {
        var stock = Shape.Box(20, 16, 8).Translate(0, 0, -4);
        var region = new Region2d([
            new Vector2d(-5, -3), new Vector2d(5, -3), new Vector2d(5, 3), new Vector2d(-5, 3)]);
        var ops = new[] { CncMill.Pocket(region, Tool, depth: 2) };

        var first = CncStock.Simulate(stock, ops, states: 4);
        var second = CncStock.Simulate(stock, ops, states: 4);
        for (int i = 0; i < first.Count; i++)
        {
            var a = first[i].Shape.ToMesh();
            var b = second[i].Shape.ToMesh();
            Assert.Equal(a.VertexCount, b.VertexCount);
            Assert.Equal(a.Volume(), b.Volume()); // bit-equal, not a tolerance
        }
    }

    [Fact]
    public void SurfacingPasses_AreRefusedByName()
    {
        var dome = Shape.Box(24, 24, 6) | Shape.Sphere(6).Translate(0, 0, 3);
        var raster = CncSurfacing.Raster(dome, Tool, sampleStep: 2);
        var stock = Shape.Box(24, 24, 12);
        var e = Assert.Throws<ArgumentException>(() => CncStock.Simulate(stock, [raster]));
        Assert.Contains("simultaneously", e.Message);

        Assert.Contains("states", Assert.Throws<ArgumentException>(
            () => CncStock.Simulate(stock, [], states: 1)).Message);
    }
}
