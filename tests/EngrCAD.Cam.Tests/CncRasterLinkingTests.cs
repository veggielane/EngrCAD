using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// No-retract row linking: the serpentine rows merge into ONE pass, the connecting stretch
/// between a row's end and the next row's start sampled ON the cutter-location surface
/// through the same tipAt — the link carries exactly the fidelity a within-row chord does,
/// and there is no retract to take back.
/// </summary>
public class CncRasterLinkingTests
{
    [Fact]
    public void LinkedRows_AreOnePass_WithOnePlungeInsteadOfOnePerRow()
    {
        var dome = Shape.Sphere(10);
        var tool = new MillTool(8, StepDown: 2);
        var separate = CncSurfacing.Raster(dome, tool);
        var linked = CncSurfacing.Raster(dome, tool, linkRows: true);

        Assert.True(separate.Passes.Count > 1);
        Assert.Single(linked.Passes);

        // The row samples themselves are IDENTICAL — linking adds only the connectors.
        var rowPoints = separate.Passes.SelectMany(p => p.Points).ToHashSet();
        Assert.True(rowPoints.IsSubsetOf(linked.Passes[0].Points.ToHashSet()));

        // One entry in the whole program: every other pass's plunge is gone.
        static int Plunges(MillOperation op) => GcodeReader
            .Read(CncGcodeWriter.Write([op])).Moves
            .Count(m => !m.Rapid && m.XyLength == 0 && m.To.Z < m.From.Z);
        Assert.Equal(separate.Passes.Count, Plunges(separate));
        Assert.Equal(1, Plunges(linked));
    }

    [Fact]
    public void TheLinkSamples_RideTheCutterLocationSurface()
    {
        // Every linked point — connectors included — obeys the ball CL closed form on the
        // dome: tip = √((S+r)² − d²) − r where the tool reaches the sphere, one-sided
        // against the field route's own trace tolerance.
        var dome = Shape.Sphere(10);
        var tool = new MillTool(8, StepDown: 2);
        var linked = CncSurfacing.Raster(dome, tool, linkRows: true);

        foreach (var p in linked.Passes[0].Points)
        {
            double d = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            if (d > 13)
                continue;                                    // off the part: floor clamp
            double analytic = Math.Sqrt(14 * 14 - d * d) - 4;
            Assert.InRange(p.Z, analytic - 1e-3, analytic + 1e-3);
        }

        // Default off is byte-identical.
        Assert.Equal(
            CncGcodeWriter.Write([CncSurfacing.Raster(dome, tool)]),
            CncGcodeWriter.Write([CncSurfacing.Raster(dome, tool, linkRows: false)]));

        // And the mesh route links through the SAME rule.
        var flat = CncSurfacing.Raster(dome, tool,
            cutter: MillCutter.FlatEnd(8), linkRows: true);
        Assert.Single(flat.Passes);
    }
}
