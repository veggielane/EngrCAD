using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// Lead-in/out arcs: a profile is entered and left on a quarter arc TANGENT to the path at
/// the seam, on the side away from the material — so the plunge lands off the wall (the
/// dwell-mark reason the feature exists), the arc's construction identities hold exactly
/// (start point = P0 + n·R − d·R by the geometry, centre offset perpendicular to the
/// tangent), and a lead that cannot fit a small hole refuses by name.
/// </summary>
public class CncLeadArcTests
{
    private static Region2d Plate(double a = 40, double b = 30) => new(
        [new Vector2d(0, 0), new Vector2d(a, 0), new Vector2d(a, b), new Vector2d(0, b)]);

    [Fact]
    public void TheLeadIn_StartsOffTheWall_AtTheExactConstructionPoint()
    {
        var op = CncMill.Profile(Plate(), new MillTool(6), depth: 2, ProfileSide.Outside,
            leadRadius: 4);
        var pass = op.Passes[0];
        Assert.False(pass.IsClosed);

        // The loop's own seam is the 17th point (16 lead chords precede it); the exact
        // construction start is P0 + n·R − d·R with d the first cut direction and n the
        // away normal (right of travel for climb).
        var p0 = pass.Points[16];
        var d = (pass.Points[17] - p0).Normalized();
        var n = new Vector3d(d.Y, -d.X, 0);
        var expected = p0 + n * 4 - d * 4;
        Assert.Equal(0, (pass.Points[0] - expected).Length, 9);

        // Tangency at the seam: the last lead chord approaches along the cut direction to
        // the chord sampling's own grade (half a chord step of a 16-segment quarter).
        var approach = (p0 - pass.Points[15]).Normalized();
        Assert.True(approach.Dot(d) > 0.998,
            $"lead approaches at dot {approach.Dot(d):0.####} to the cut direction");

        // Every lead point stays a full tool radius clear of the part (no-gouge, and the
        // plunge point is off the wall by construction).
        var region = Plate();
        for (int i = 0; i < 16; i++)
            Assert.True(CncMill.DistanceToBoundary(
                region, new Vector2d(pass.Points[i].X, pass.Points[i].Y)) >= 3 - 1e-9);
        // The lead-out closes the loop first, then leaves: the closing point equals the seam.
        Assert.Equal(0, (pass.Points[^17] - p0).Length, 12);
    }

    [Fact]
    public void ZeroLeadRadius_IsByteIdentical_AndLeadsApplyAtEveryLevel()
    {
        var a = CncMill.Profile(Plate(), new MillTool(6), depth: 5, ProfileSide.Outside);
        var b = CncMill.Profile(Plate(), new MillTool(6), depth: 5, ProfileSide.Outside,
            leadRadius: 0);
        Assert.Equal(
            CncGcodeWriter.Write([a]),
            CncGcodeWriter.Write([b]));

        var led = CncMill.Profile(Plate(), new MillTool(6), depth: 5, ProfileSide.Outside,
            leadRadius: 3);
        // Two depth levels (StepDown default 3: −3, −5), each pass open with its own lead
        // at its own z.
        Assert.True(led.Passes.Count >= 2);
        foreach (var pass in led.Passes)
        {
            Assert.False(pass.IsClosed);
            Assert.Equal(pass.Points[0].Z, pass.Points[^1].Z);
        }
    }

    [Fact]
    public void ALeadThatCannotFitASmallHole_RefusesByName()
    {
        // A Ø13 hole cut inside by a Ø6 tool runs at radius 3.5; a 4 mm lead arc reaches
        // past the hole centre toward the far wall.
        var hole = new Region2d(
            [new Vector2d(-30, -30), new Vector2d(30, -30), new Vector2d(30, 30), new Vector2d(-30, 30)],
            [Enumerable.Range(0, 32).Select(i =>
            {
                double a = -2 * Math.PI * i / 32;
                return new Vector2d(6.5 * Math.Cos(a), 6.5 * Math.Sin(a));
            }).ToList()]);
        var message = Assert.Throws<ArgumentException>(() =>
            CncMill.Profile(hole, new MillTool(6), depth: 2, ProfileSide.Inside,
                leadRadius: 4)).Message;
        Assert.Contains("lead radius", message);
        Assert.Contains("does not fit", message);
    }

    [Fact]
    public void LeadsComposeWithTabs_AndBothDirectionsStayInTheWaste()
    {
        var tabbed = CncMill.Profile(Plate(), new MillTool(6), depth: 4, ProfileSide.Outside,
            tabs: 3, tabHeight: 1.5, tabWidth: 5, leadRadius: 3);
        var final = tabbed.Passes[^1];
        Assert.False(final.IsClosed);
        // The final pass still lifts for its tabs (points above the final depth) AND
        // carries its leads at the final depth.
        Assert.Contains(final.Points, q => q.Z > -4 + 1e-9);
        Assert.Equal(-4, final.Points[0].Z, 12);

        // Both cutting directions put the lead in the waste — away from material is a
        // spatial fact however the loop is wound.
        var region = Plate();
        foreach (var direction in new[] { MillDirection.Climb, MillDirection.Conventional })
        {
            var op = CncMill.Profile(region, new MillTool(6), depth: 2, ProfileSide.Outside,
                direction: direction, leadRadius: 4);
            var pass = op.Passes[0];
            for (int i = 0; i < 16; i++)
                Assert.True(CncMill.DistanceToBoundary(
                    region, new Vector2d(pass.Points[i].X, pass.Points[i].Y)) >= 3 - 1e-9);
        }
    }
}
