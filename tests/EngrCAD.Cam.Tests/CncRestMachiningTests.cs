using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// Rest machining: the corner residues a rough tool leaves are cleared by a smaller finish
/// tool whose centre may stand in already-cleared space but never within its radius of the
/// wall. The oracle is the module's own: the COMBINED rough+rest footprint equals the
/// finish tool's morphological opening, and a rectangle's residue ladder is closed form —
/// (4−π)R₁² after roughing, (4−π)r₂² after the rest pass.
/// </summary>
public class CncRestMachiningTests
{
    private static Region2d Rect(double a, double b) => new(
        [new Vector2d(0, 0), new Vector2d(a, 0), new Vector2d(a, b), new Vector2d(0, b)]);

    private static double DistanceToBoundary(Region2d region, Vector2d p)
    {
        double best = double.PositiveInfinity;
        Visit(region.Outer);
        foreach (var hole in region.Holes)
            Visit(hole);
        return best;

        void Visit(IReadOnlyList<Vector2d> loop)
        {
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                var e = b - a;
                double len2 = e.LengthSquared;
                double t = len2 > 0 ? Math.Clamp((p - a).Dot(e) / len2, 0, 1) : 0;
                best = Math.Min(best, (p - (a + e * t)).Length);
            }
        }
    }

    private static double FootprintArea(IEnumerable<MillPass> passes, double diameter)
    {
        var footprints = new List<Region2d>();
        foreach (var pass in passes)
        {
            var xy = pass.Points.Select(p => new Vector2d(p.X, p.Y)).ToList();
            footprints.AddRange(Region2dOffset.Stroke(xy, diameter, closed: pass.IsClosed));
        }
        return Region2dBoolean.UnionAll(footprints).Sum(x => x.Area);
    }

    [Fact]
    public void TheRestPass_TakesTheCombinedCoverageToTheFinishToolsOwnOpening()
    {
        // A 40×24 pocket: Ø12 roughing leaves (4−π)·36 ≈ 30.9 mm² in the corners; the Ø3
        // rest pass takes the combined coverage to the Ø3 opening, whose own residue is
        // (4−π)·2.25 ≈ 1.93 mm² — a 16× improvement the closed forms state exactly.
        var region = Rect(40, 24);
        var rough = new MillTool(12, StepDown: 3);
        var finish = new MillTool(3, StepDown: 3);

        var roughOp = CncMill.Pocket(region, rough, depth: 3);
        var restOp = CncMill.PocketRest(region, rough, finish, depth: 3);
        Assert.NotEmpty(restOp.Passes);

        double finishOpening = Region2dBoolean.UnionAll([.. Region2dOffset
            .Offset(region, -finish.Radius)
            .SelectMany(s => Region2dOffset.Offset(s, finish.Radius))]).Sum(x => x.Area);
        Assert.Equal((4 - Math.PI) * finish.Radius * finish.Radius,
            region.Area - finishOpening, 0.02);

        // One depth level of each, stroked and unioned: the combined footprint IS the
        // finish opening (the rest pass reaches everything the small tool can).
        var oneLevel = roughOp.Passes.Where(p => p.Points[0].Z == -3)
            .Concat(restOp.Passes.Where(p => p.Points[0].Z == -3)).ToList();
        var footprints = new List<Region2d>();
        foreach (var pass in roughOp.Passes.Where(p => p.Points[0].Z == -3))
            footprints.AddRange(Region2dOffset.Stroke(
                pass.Points.Select(p => new Vector2d(p.X, p.Y)).ToList(),
                rough.Diameter, closed: pass.IsClosed));
        foreach (var pass in restOp.Passes.Where(p => p.Points[0].Z == -3))
            footprints.AddRange(Region2dOffset.Stroke(
                pass.Points.Select(p => new Vector2d(p.X, p.Y)).ToList(),
                finish.Diameter, closed: pass.IsClosed));
        double covered = Region2dBoolean.UnionAll(footprints).Sum(x => x.Area);
        Assert.Equal(finishOpening, covered, finishOpening * 0.01);

        // And the ladder in closed form: what remains uncovered of the REGION is the
        // finish tool's own corner residue.
        Assert.Equal((4 - Math.PI) * finish.Radius * finish.Radius,
            region.Area - covered, 0.05);
    }

    [Fact]
    public void TheRestPass_NeverGougesTheWall_ThoughItsCentreStandsInClearedSpace()
    {
        var region = Rect(40, 24);
        var restOp = CncMill.PocketRest(
            region, new MillTool(12), new MillTool(3), depth: 2);

        // Point by point against the ORIGINAL boundary: at least the finish radius off.
        foreach (var pass in restOp.Passes)
            foreach (var p in pass.Points)
                Assert.True(
                    DistanceToBoundary(region, new Vector2d(p.X, p.Y)) >= 1.5 - 1e-9);

        // And the whole point of rest machining: some centre stands OUTSIDE the residue,
        // in space the rough tool already cleared (the residue is smaller than the tool).
        var opening = Region2dBoolean.UnionAll([.. Region2dOffset
            .Offset(region, -6.0).SelectMany(s => Region2dOffset.Offset(s, 6.0))]);
        Assert.Contains(restOp.Passes.SelectMany(p => p.Points), p =>
            opening.Any(o => o.Contains(new Vector2d(p.X, p.Y))));
    }

    [Fact]
    public void ARegionTheRoughToolFullyReaches_YieldsAnHonestEmptyRestPass()
    {
        // A rounded rectangle whose corner radius exceeds the rough tool's: the opening IS
        // the region, the residue is empty, and the rest op carries no passes.
        var rounded = Sketch.RoundedRectangle(40, 24, 8).ToRegions()[0];
        var restOp = CncMill.PocketRest(rounded, new MillTool(12), new MillTool(3), 2);
        Assert.Empty(restOp.Passes);

        Assert.Contains("smaller than", Assert.Throws<ArgumentException>(() =>
            CncMill.PocketRest(rounded, new MillTool(6), new MillTool(6), 2)).Message);
    }

    [Fact]
    public void TheRestPass_IsDeterministic()
    {
        var region = Rect(40, 24);
        var a = CncMill.PocketRest(region, new MillTool(12), new MillTool(3), 3);
        var b = CncMill.PocketRest(region, new MillTool(12), new MillTool(3), 3);
        Assert.Equal(
            CncGcodeWriter.Write([a]),
            CncGcodeWriter.Write([b]));
    }
}
