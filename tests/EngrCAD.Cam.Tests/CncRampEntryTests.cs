using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// Helical ramp entry: each pocket level is entered on a helix descending from the previous
/// (already cleared) level instead of plunging straight into material — the helix radius
/// under the tool radius (no core post), inside the measured room, one flat closing turn at
/// the level, with a plunge fallback where the pocket is too tight to helix.
/// </summary>
public class CncRampEntryTests
{
    private static Region2d Rect(double a, double b) => new(
        [new Vector2d(0, 0), new Vector2d(a, 0), new Vector2d(a, b), new Vector2d(0, b)]);

    [Fact]
    public void ARampedPocket_PlungesOnlyThroughAir()
    {
        // Depth 6 at StepDown 2: levels −2, −4, −6. With the ramp on, every stationary-XY
        // descending move must END at a level TOP (0, −2, −4) — cleared air — where the
        // plunge-only program's plunges end at the level BOTTOMS, in material.
        var region = Rect(40, 24);
        var tool = new MillTool(6, StepDown: 2);
        var ramped = CncMill.Pocket(region, tool, depth: 6, rampAngleDegrees: 3);
        var plunged = CncMill.Pocket(region, tool, depth: 6);

        static List<double> PlungeEnds(MillOperation op)
        {
            var decoded = GcodeReader.Read(CncGcodeWriter.Write([op]));
            return decoded.Moves
                .Where(m => !m.Rapid && m.XyLength == 0 && m.To.Z < m.From.Z && m.To.Z < 0)
                .Select(m => m.To.Z).Distinct().OrderBy(z => z).ToList();
        }

        Assert.Equal(new[] { -4.0, -2.0 }, PlungeEnds(ramped)); // air above −6 and −4…
        Assert.Equal(new[] { -6.0, -4.0, -2.0 }, PlungeEnds(plunged));
        // …and the deepest level's own bottom is reached only by the helix.
        Assert.DoesNotContain(-6.0, PlungeEnds(ramped));
    }

    [Fact]
    public void TheHelix_DescendsAtTheStatedAngle_AndClosesFlatAtTheLevel()
    {
        var region = Rect(40, 24);
        var tool = new MillTool(6, StepDown: 5);
        var op = CncMill.Pocket(region, tool, depth: 5, rampAngleDegrees: 3);

        // The level's pass LEADS with the helix: its first point sits at angle 0, so the
        // centre is one helix radius back along X, and the helix is the leading run of
        // points within that radius (the rings take over strictly outside it).
        var pass = op.Passes[0].Points;
        double rh = tool.Radius / 2;
        var centre = new Vector2d(pass[0].X - rh, pass[0].Y);
        var helix = pass.TakeWhile(p =>
            new Vector2d(p.X - centre.X, p.Y - centre.Y).Length <= rh + 1e-9).ToList();
        Assert.True(helix.Count >= 33, "the descent plus the flat closing turn");

        double expectedSlope = Math.Tan(3 * Math.PI / 180);
        var descending = helix.Zip(helix.Skip(1))
            .Where(pair => pair.Second.Z < pair.First.Z - 1e-12).ToList();
        foreach (var (a, b) in descending)
        {
            double run = new Vector2d(b.X - a.X, b.Y - a.Y).Length;
            Assert.InRange((a.Z - b.Z) / run, 0, expectedSlope + 1e-9);
        }
        Assert.Equal(-5, helix[^1].Z, 12);
        int flat = helix.Count(p => p.Z == -5);
        Assert.True(flat >= 17, "one full flat closing turn plus the handover");

        // No-gouge holds for the helix too: every point a tool radius off the wall.
        foreach (var p in op.Passes.SelectMany(x => x.Points))
            Assert.True(CncMill.DistanceToBoundary(
                region, new Vector2d(p.X, p.Y)) >= tool.Radius - 1e-9);
    }

    [Fact]
    public void RampZero_IsByteIdentical_AndATightSlotFallsBackToThePlunge()
    {
        var region = Rect(40, 24);
        var tool = new MillTool(6, StepDown: 2);
        Assert.Equal(
            CncGcodeWriter.Write([CncMill.Pocket(region, tool, 4)]),
            CncGcodeWriter.Write([CncMill.Pocket(region, tool, 4, rampAngleDegrees: 0)]));

        // A slot barely wider than the tool: the boundary pass has no room to helix, so the
        // entry stays a plunge (the honest fallback, not a gouged helix).
        var slot = Rect(40, 6.4);
        var slotOp = CncMill.Pocket(slot, tool, 2, rampAngleDegrees: 3);
        var decoded = GcodeReader.Read(CncGcodeWriter.Write([slotOp]));
        Assert.Contains(decoded.Moves, m =>
            !m.Rapid && m.XyLength == 0 && m.To.Z == -2 && m.From.Z > 0);

        Assert.Contains("rampAngleDegrees", Assert.Throws<ArgumentException>(() =>
            CncMill.Pocket(region, tool, 4, rampAngleDegrees: 60)).Message);
    }

    [Fact]
    public void TheCoverageOracle_SurvivesTheRamp()
    {
        // The helix footprint lies inside the pocket, so the opening identity is unchanged.
        var region = Rect(40, 20);
        var tool = new MillTool(6, StepDown: 2);
        var op = CncMill.Pocket(region, tool, depth: 2, rampAngleDegrees: 3);
        double r = tool.Radius;

        var opening = Region2dBoolean.UnionAll(
            [.. Region2dOffset.Offset(region, -r).SelectMany(s => Region2dOffset.Offset(s, r))]);
        double openingArea = opening.Sum(x => x.Area);
        // The helix retraces one polygon per turn, and EXACTLY coincident repeated
        // segments are the 2D arrangement's hostile case — a repeated segment adds no
        // footprint, so the stroke runs over the deduplicated segment set.
        var footprints = new List<Region2d>();
        var seen = new HashSet<(double, double, double, double)>();
        foreach (var pass in op.Passes)
        {
            int count = pass.Points.Count + (pass.IsClosed ? 1 : 0);
            for (int i = 1; i < count; i++)
            {
                var a = pass.Points[i - 1];
                var b = pass.Points[i % pass.Points.Count];
                if (a.X == b.X && a.Y == b.Y)
                    continue;
                var key = a.X < b.X || (a.X == b.X && a.Y < b.Y)
                    ? (a.X, a.Y, b.X, b.Y)
                    : (b.X, b.Y, a.X, a.Y);
                if (!seen.Add(key))
                    continue;
                footprints.AddRange(Region2dOffset.Stroke(
                    [new Vector2d(a.X, a.Y), new Vector2d(b.X, b.Y)], 2 * r, closed: false));
            }
        }
        Assert.Equal(openingArea,
            Region2dBoolean.UnionAll(footprints).Sum(x => x.Area), openingArea * 0.01);
    }
}
