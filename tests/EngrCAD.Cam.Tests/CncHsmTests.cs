using EngrCAD.Cam;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// Trochoidal slotting. The campaign's own bar: the engagement angle is MEASURED from
/// the evolving stock — the tool-circle arc not yet covered by the path's swept prefix —
/// and bounded by the stated maximum, with a straight-line slot cut as the control that
/// proves the instrument (it must read the buried ~180°, or the bound means nothing).
/// </summary>
public class CncHsmTests
{
    private static readonly MillTool Tool = new(Diameter: 4);

    [Fact]
    public void Engagement_StaysUnderTheStatedBound_WhereAStraightSlotBuries()
    {
        var op = CncHsm.TrochoidalSlot(
            new Vector2d(0, 0), new Vector2d(20, 0), slotWidth: 10, Tool, depth: 2,
            maxEngagementDegrees: 60);
        var path = op.Passes[0].Points.Select(p => new Vector2d(p.X, p.Y)).ToList();

        // The bound is a claim about the TROCHOIDAL phase: a spiral-out's contact arc is
        // wide but SHALLOW (its bounded quantity is the radial step per turn — the chip
        // load — which is why entry feed reduction exists), so measurement starts one
        // full loop after the spiral first reaches the loop radius.
        double loopRadius = (10 - Tool.Diameter) / 2;
        int spiralSteps = Enumerable.Range(0, path.Count)
            .First(i => (path[i] - new Vector2d(0, 0)).Length >= loopRadius - 1e-9) + 36;
        double measured = MaxEngagementDegrees(path, Tool.Radius, firstMeasured: spiralSteps);
        Assert.True(measured <= 67,
            $"trochoidal engagement measured {measured:0.#}° against a 60° bound "
            + "(the 60-sample circle quantizes at 6°)");
        Assert.True(measured >= 35,
            $"the solved advance should USE the stated bound, not sit far under it "
            + $"(measured {measured:0.#}°)");

        // The control: a straight-line slot cut buries the tool's whole leading half.
        var line = Enumerable.Range(0, 81).Select(i => new Vector2d(i * 0.25, 0)).ToList();
        double buried = MaxEngagementDegrees(line, Tool.Radius, firstMeasured: 8);
        Assert.True(buried >= 150, $"the straight-slot control read only {buried:0.#}°");
    }

    [Fact]
    public void SweptSlot_IsTheStadium_AndNeverOvercuts()
    {
        var start = new Vector2d(-8, 0);
        var end = new Vector2d(12, 0);
        double width = 9;
        var op = CncHsm.TrochoidalSlot(start, end, width, Tool, depth: 2);
        var path = op.Passes[0].Points.Select(p => new Vector2d(p.X, p.Y)).ToList();

        // No-overcut, point by point: the loop centre stays within the loop radius of
        // the centre-line, so the tool edge stays within the slot's own wall.
        double loopRadius = (width - Tool.Diameter) / 2;
        foreach (var p in path)
            Assert.True(DistanceToSegment(start, end, p) <= loopRadius + 1e-9,
                $"path point {p} escapes the slot corridor");

        // Coverage: the swept footprint is the slot stadium, L·W + π(W/2)².
        var swept = Region2dBoolean.UnionAll(
            [.. Region2dOffset.Stroke(path, Tool.Diameter)]);
        double stadium = (end - start).Length * width + Math.PI * width * width / 4;
        double area = swept.Sum(r => r.Area);
        Assert.True(Math.Abs(area - stadium) < stadium * 0.02,
            $"swept {area:0.##} vs stadium {stadium:0.##}");
    }

    [Fact]
    public void DepthLevels_Determinism_AndTheStockRecordCompose()
    {
        var op = CncHsm.TrochoidalSlot(
            new Vector2d(-6, 0), new Vector2d(6, 0), slotWidth: 8, Tool, depth: 5);
        Assert.Equal([-2.0, -4.0, -5.0],
            op.Passes.Select(p => p.Points[0].Z).ToList());

        string first = CncGcodeWriter.Write([op]);
        var again = CncHsm.TrochoidalSlot(
            new Vector2d(-6, 0), new Vector2d(6, 0), slotWidth: 8, Tool, depth: 5);
        Assert.Equal(first, CncGcodeWriter.Write([again]));

        // NOT asserted: CncStock.Simulate of a trochoidal op. The swept union's boundary
        // carries a near-tangent scallop cusp per loop (circles of radius R + r whose
        // centres sit one small advance apart cross at a few degrees), which is the mesh
        // imprint boolean's hostile family — filed with the campaign rather than papered
        // over with a footprint-smoothing tolerance that measurably broke honest
        // fixtures when tried.
    }

    [Fact]
    public void UnusableSlots_RefuseByName()
    {
        Assert.Contains("slot width", Assert.Throws<ArgumentException>(() =>
            CncHsm.TrochoidalSlot(new Vector2d(0, 0), new Vector2d(10, 0), 4, Tool, 2)).Message);
        Assert.Contains("maxEngagementDegrees", Assert.Throws<ArgumentException>(() =>
            CncHsm.TrochoidalSlot(new Vector2d(0, 0), new Vector2d(10, 0), 8, Tool, 2,
                maxEngagementDegrees: 0)).Message);
        Assert.Contains("no length", Assert.Throws<ArgumentException>(() =>
            CncHsm.TrochoidalSlot(new Vector2d(1, 1), new Vector2d(1, 1), 8, Tool, 2)).Message);
    }

    /// <summary>The engagement instrument: at each path point, the fraction of the tool
    /// circle NOT yet covered by the swept prefix (a point is cut when it lies within
    /// the tool radius of an earlier path segment), as degrees of arc.</summary>
    private static double MaxEngagementDegrees(
        IReadOnlyList<Vector2d> path, double toolRadius, int firstMeasured = 1,
        int circleSamples = 60)
    {
        double worst = 0;
        for (int i = Math.Max(1, firstMeasured); i < path.Count; i++)
        {
            int inMaterial = 0;
            for (int k = 0; k < circleSamples; k++)
            {
                double a = 2 * Math.PI * k / circleSamples;
                var q = path[i] + new Vector2d(
                    toolRadius * Math.Cos(a), toolRadius * Math.Sin(a));
                // The plunge at path[0] bores its own disc before any segment is swept.
                bool cut = (q - path[0]).Length < toolRadius - 1e-9;
                for (int j = 1; j < i && !cut; j++)
                    cut = DistanceToSegment(path[j - 1], path[j], q) < toolRadius - 1e-9;
                if (!cut)
                    inMaterial++;
            }
            worst = Math.Max(worst, 360.0 * inMaterial / circleSamples);
        }
        return worst;
    }

    private static double DistanceToSegment(in Vector2d a, in Vector2d b, in Vector2d p)
    {
        var d = b - a;
        double len2 = d.Dot(d);
        double t = len2 > 0 ? Math.Clamp((p - a).Dot(d) / len2, 0, 1) : 0;
        return (p - (a + d * t)).Length;
    }
}
