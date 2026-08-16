using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// The flat/bull-nose waterline: the silhouette-above-the-tip-plane dilated by the tool —
/// exact against the mesh for a flat cutter, a banded conservative ladder for the bull-nose
/// corner (each band over-covers its slice: the contour stands off at least the true CL
/// distance, stock never gouge), with the 45°-cone three-sided oracle separating the banded
/// answer from both the exact standoff below it and the sharp envelope above it.
/// </summary>
public class CncFlatWaterlineTests
{
    private static double RadiusAt(MillOperation op, double tipZ)
    {
        var points = op.Passes.SelectMany(p => p.Points)
            .Where(p => Math.Abs(p.Z - tipZ) < 1e-9).ToList();
        Assert.NotEmpty(points);
        return points.Max(p => Math.Sqrt(p.X * p.X + p.Y * p.Y));
    }

    [Fact]
    public void AFlatWaterline_StandsOffAVerticalWall_ByExactlyTheToolRadius()
    {
        // A Ø12 boss: the collision region at any level is the boss silhouette grown by the
        // full tool radius, so the contour circle is R_boss + R_tool — one-sided against the
        // inscribed tessellation (low, never high).
        var boss = Shape.Cylinder(6, 10);
        var tool = new MillTool(8, StepDown: 5);
        var op = CncSurfacing.Waterline(boss, tool, cutter: MillCutter.FlatEnd(8));
        Assert.NotEmpty(op.Passes);

        // Origin-centred: the boss spans z −5..5; StepDown 5 puts tips at 0 and −5.
        foreach (var tipZ in new[] { 0.0, -5.0 })
        {
            double radius = RadiusAt(op, tipZ);
            Assert.True(radius <= 10 + 1e-9, "an inscribed mesh cannot push the tool out");
            Assert.InRange(radius, 10 - 0.05, 10 + 1e-9);
        }

        // A bull-nose against a vertical wall touches at its own equator — the same R.
        var bull = CncSurfacing.Waterline(boss, tool, cutter: MillCutter.BullNose(8, 1));
        Assert.InRange(RadiusAt(bull, -5), 10 - 0.05, 10 + 1e-9);
    }

    [Fact]
    public void TheConeOracle_BracketsTheBandedBullNose_FromBothSides()
    {
        // A 45° cone (apex up): at tip level z the flat contour is the cone's own radius at
        // z plus R — EXACT for a flat cutter, since the disc collides with exactly the
        // material above its plane. The bull-nose's banded ladder must land BETWEEN the
        // exact standoff a + r(√2 − 1) and the sharp envelope R: at K = 4 bands the closed
        // form is max_k(reach_k − h_k) = 3.661 for Ø8 r1, against exact 3.414 and sharp 4.
        var cone = Shape.Cone(10, 0, 10);                    // base r10 at z0 to apex at z10
        var tool = new MillTool(8, StepDown: 4);

        var flat = CncSurfacing.Waterline(cone, tool, cutter: MillCutter.FlatEnd(8));
        // Origin-centred: the cone spans z −5..5 (base r10 at −5, apex at +5), so the cone
        // radius at height z is 5 − z, and the first tip level is z = 1: radius 4.
        double flatRadius = RadiusAt(flat, 1);
        Assert.True(flatRadius <= 4 + 4 + 1e-9);
        Assert.InRange(flatRadius, 4 + 4 - 0.06, 4 + 4 + 1e-9);

        var bull = CncSurfacing.Waterline(cone, tool, cutter: MillCutter.BullNose(8, 1));
        double a = 3, r = 1;
        double exact = a + r * (Math.Sqrt(2) - 1);           // 3.414: the true 45° standoff
        double banded = 0;
        const int bands = 4;
        for (int k = 0; k < bands; k++)
        {
            double reach = a + Math.Sqrt(r * r - Math.Pow(r - r * (k + 1.0) / bands, 2));
            banded = Math.Max(banded, reach - r * k / bands);
        }
        Assert.Equal(3.6614, banded, 3);                     // the ladder's own closed form
        double bullRadius = RadiusAt(bull, 1);
        Assert.True(bullRadius >= 4 + exact - 0.06,
            "the banded contour must never stand closer than the true CL distance");
        Assert.True(bullRadius <= 4 + banded + 1e-9,
            "the banded contour is the ladder's own closed form, not the sharp envelope");
        Assert.True(bullRadius < 4 + 4 - 0.2,
            "the ladder measurably beats the sharp-cornered envelope");
    }

    [Fact]
    public void TheWaterline_IsDeterministic_AndCarriesEveryLevel()
    {
        var boss = Shape.Cylinder(6, 10);
        var tool = new MillTool(8, StepDown: 4);
        var a = CncSurfacing.Waterline(boss, tool, cutter: MillCutter.FlatEnd(8));
        var b = CncSurfacing.Waterline(boss, tool, cutter: MillCutter.FlatEnd(8));
        Assert.Equal(CncGcodeWriter.Write([a]), CncGcodeWriter.Write([b]));

        var levels = a.Passes.Select(p => p.Points[0].Z).Distinct().OrderBy(z => z).ToList();
        Assert.Equal(new[] { -5.0, -3.0, 1.0 }, levels);
    }
}
