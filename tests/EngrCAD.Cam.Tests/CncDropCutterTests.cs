using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// Flat and bull-nose cutter-location surfaces via the mesh drop-cutter, held to the APT
/// closed forms on a dome — including the FLAT SPOT a flat-bottomed tool leaves over an apex,
/// which is exact because the apex vertex sits under the disc where the bottom profile is
/// exactly zero — and to the mesh's own inscribed one-sidedness (the tessellation is inside
/// the sphere, so the drop can only sit LOW of the analytic height, never high).
/// </summary>
public class CncDropCutterTests
{
    private static double TipAt(MillOperation op, double x, double y)
    {
        foreach (var pass in op.Passes)
            foreach (var p in pass.Points)
                if (Math.Abs(p.X - x) < 1e-9 && Math.Abs(p.Y - y) < 1e-9)
                    return p.Z;
        throw new InvalidOperationException($"No raster sample at ({x}, {y}).");
    }

    [Fact]
    public void AFlatEndMill_LeavesTheFlatSpotADomeApexExactly()
    {
        var dome = Shape.Sphere(10);
        var tool = new MillTool(8, StepDown: 2);
        var op = CncSurfacing.Raster(dome, tool, cutter: MillCutter.FlatEnd(8));

        // The apex vertex lies under the flat disc (ρ ≤ a = 4) where the bottom profile is
        // exactly zero, so the tip height IS the apex height — an equality, not a band.
        Assert.Equal(10, TipAt(op, 0, 0), 12);
        Assert.Equal(10, TipAt(op, 2, 0), 12);
        Assert.Equal(10, TipAt(op, 0, 4), 12);

        // Past the disc the rim corner rides the dome: z = √(S² − (d − a)²), inscribed
        // one-sided (the tessellation is inside the sphere — low, never high).
        double analytic = Math.Sqrt(100 - (6 - 4) * (6 - 4));
        double z = TipAt(op, 6, 0);
        Assert.True(z <= analytic + 1e-9, "an inscribed mesh cannot lift the cutter");
        Assert.InRange(z, analytic - 0.08, analytic + 1e-9);

        // The ball-nose at the same offset sits BELOW the apex — the flat spot is the
        // capability a ball structurally does not have.
        var ball = CncSurfacing.Raster(dome, tool);
        Assert.True(TipAt(ball, 2, 0) < 10 - 0.1,
            "a ball-nose rolls off the apex where the flat bottom bridges it");
    }

    [Fact]
    public void ABullNose_MatchesTheAptClosedFormOnTheDome()
    {
        var dome = Shape.Sphere(10);
        var tool = new MillTool(8, StepDown: 2);
        var op = CncSurfacing.Raster(dome, tool, cutter: MillCutter.BullNose(8, 1));

        // Under the disc interior (d ≤ a = 3) the apex vertex reads profile 0: exact.
        Assert.Equal(10, TipAt(op, 2, 0), 12);
        // On the corner: tip = √((S+r)² − (d−a)²) − r, inscribed one-sided.
        double analytic = Math.Sqrt(121 - (6 - 3) * (6 - 3)) - 1;
        double z = TipAt(op, 6, 0);
        Assert.True(z <= analytic + 1e-9);
        Assert.InRange(z, analytic - 0.08, analytic + 1e-9);
    }

    [Fact]
    public void AFlatPlate_ReadsItsTopExactly_TheEdgeModeCarryingTheOverhang()
    {
        // Box top at z = 2; a flat Ø6 (a = 3) stays AT the top while any part of the disc is
        // over the plate — the top edge's contact at profile 0, the edge mode exactly — and
        // clamps to the part's bottom once the plate is out of reach.
        var plate = Shape.Box(20, 20, 4);
        var tool = new MillTool(6, StepDown: 2);
        var op = CncSurfacing.Raster(plate, tool, cutter: MillCutter.FlatEnd(6));

        Assert.Equal(2, TipAt(op, 0, 0), 12);
        Assert.Equal(2, TipAt(op, 10.5, 0), 12);             // 0.5 past the edge: edge mode
        Assert.Equal(2, TipAt(op, 12, 0), 12);               // 2 past: still under the disc
        // Every sample on the centre row reads the top EXACTLY — the raster grid extends one
        // radius past the part, which is precisely as far as the disc can still touch it.
        foreach (var pass in op.Passes)
            foreach (var p in pass.Points)
                if (p.Y == 0)
                    Assert.Equal(2, p.Z, 12);

        // Out of the tool's reach a triangle offers NO contact (the raster clamps such
        // samples to the part's own bottom; the grid never reaches one on a convex plate).
        Assert.True(double.IsNegativeInfinity(DropCutter.TriangleDrop(
            new Vector3d(-10, -10, 2), new Vector3d(10, -10, 2), new Vector3d(10, 10, 2),
            x: 13.5, y: 0, MillCutter.FlatEnd(6))));
    }

    [Fact]
    public void ABallThroughTheMeshRoute_AgreesWithTheExactFieldRoute()
    {
        // Two constructions of one surface: the SDF sphere trace and the mesh drop-cutter
        // with a ball cutter (disc radius 0) must agree to the tessellation's chord error.
        var dome = Shape.Sphere(10);
        var tool = new MillTool(8, StepDown: 2);
        var field = CncSurfacing.Raster(dome, tool);
        var mesh = DropCutter.Raster(dome, tool, MillCutter.BallNose(8), null, "raster");

        Assert.Equal(field.Passes.Count, mesh.Passes.Count);
        double worst = 0;
        for (int i = 0; i < field.Passes.Count; i++)
        {
            var a = field.Passes[i].Points;
            var b = mesh.Passes[i].Points;
            Assert.Equal(a.Count, b.Count);
            for (int j = 0; j < a.Count; j++)
            {
                Assert.Equal(a[j].X, b[j].X, 12);
                Assert.Equal(a[j].Y, b[j].Y, 12);
                // The mesh is inscribed, so its drop sits low — one-sided everywhere.
                Assert.True(b[j].Z <= a[j].Z + 1e-6);
                // The band is chord error AMPLIFIED by the CL surface's own slope
                // (dz/dd = −d/√((S+r)² − d²)), so near the silhouette the honest bound
                // diverges — the tight comparison stays where the slope is bounded.
                if (Math.Sqrt(a[j].X * a[j].X + a[j].Y * a[j].Y) <= 12)
                    worst = Math.Max(worst, a[j].Z - b[j].Z);
            }
        }
        Assert.True(worst < 0.08, $"chord-error band exceeded: {worst:0.####}");
    }

    [Fact]
    public void AStatedBallCutter_TakesTheExactRoute_ByteForByte()
    {
        var dome = Shape.Sphere(10);
        var tool = new MillTool(8, StepDown: 2);
        var plain = CncSurfacing.Raster(dome, tool);
        var stated = CncSurfacing.Raster(dome, tool, cutter: MillCutter.BallNose(8));
        Assert.Equal(
            CncGcodeWriter.Write([plain]),
            CncGcodeWriter.Write([stated]));
    }

    [Fact]
    public void TheRefusals_NameTheirReasons()
    {
        Assert.Contains("strictly in (0, R)", Assert.Throws<ArgumentException>(() =>
            MillCutter.BullNose(8, 4)).Message);
        Assert.Contains("strictly in (0, R)", Assert.Throws<ArgumentException>(() =>
            MillCutter.BullNose(8, 0)).Message);
        Assert.Contains("different diameters", Assert.Throws<ArgumentException>(() =>
            CncSurfacing.Raster(Shape.Sphere(5), new MillTool(8),
                cutter: MillCutter.FlatEnd(6))).Message);
        Assert.Contains("ball-nose only", Assert.Throws<ArgumentException>(() =>
            CncSurfacing.Waterline(Shape.Sphere(5), new MillTool(8),
                cutter: MillCutter.FlatEnd(8))).Message);
    }
}
