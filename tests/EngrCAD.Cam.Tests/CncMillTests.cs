using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Cam;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// 2.5D CNC milling (CAM stage 2). The centrepiece oracle is the morphological OPENING: a
/// radius-r tool can reach exactly grow_r(shrink_r(region)), so the union of the passes' swept
/// footprints — the machined-stock simulation — must equal it, and a rectangular pocket's
/// unreachable corner residue is CLOSED FORM, (4 − π)·r². No-gouge is exact and point-by-point;
/// depth stepping is arithmetic; drilling expands to plain moves the twin decoder reads; tabs
/// lift exactly where stated; everything is deterministic byte-for-byte.
/// </summary>
public sealed class CncMillTests
{
    private static Region2d Rect(double a, double b) => new(
        [new Vector2d(0, 0), new Vector2d(a, 0), new Vector2d(a, b), new Vector2d(0, b)]);

    private static MillTool Tool(double d = 6) => new(d, StepDown: 2);

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
                var ab = b - a;
                double len2 = ab.X * ab.X + ab.Y * ab.Y;
                double t = len2 > 0
                    ? Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1)
                    : 0;
                var q = new Vector2d(a.X + t * ab.X, a.Y + t * ab.Y);
                best = Math.Min(best, (p - q).Length);
            }
        }
    }

    [Fact]
    public void ThePocket_CoversTheMorphologicalOpening_AndTheCornerResidueIsClosedForm()
    {
        // A 40×20 pocket with a Ø6 tool: the reachable area is the opening, and what the tool
        // CANNOT reach is the four corner pieces, each r² − πr²/4 — so the residue is exactly
        // (4 − π)·r² = 7.7256… mm².
        var region = Rect(40, 20);
        double r = 3;
        var opening = Region2dBoolean.UnionAll(
            [.. Region2dOffset.Offset(region, -r).SelectMany(s => Region2dOffset.Offset(s, r))]);
        double openingArea = opening.Sum(x => x.Area);
        Assert.Equal((4 - Math.PI) * r * r, region.Area - openingArea, 0.02);

        // The machined-stock simulation: every pass centreline stroked at the tool diameter,
        // unioned — it must cover the opening (and nothing outside the region).
        var pocket = CncMill.Pocket(region, Tool(), depth: 2);
        var footprints = new List<Region2d>();
        foreach (var pass in pocket.Passes)
        {
            var xy = pass.Points.Select(p => new Vector2d(p.X, p.Y)).ToList();
            footprints.AddRange(Region2dOffset.Stroke(xy, 2 * r, closed: pass.IsClosed));
        }
        double covered = Region2dBoolean.UnionAll(footprints).Sum(x => x.Area);
        Assert.Equal(openingArea, covered, openingArea * 0.01);

        // The exact no-gouge claim, point by point: every pass point at least the tool radius
        // from the region boundary (insetting guarantees it; the test measures it anyway).
        foreach (var pass in pocket.Passes)
            foreach (var p in pass.Points)
                Assert.True(
                    DistanceToBoundary(region, new Vector2d(p.X, p.Y)) >= r - 1e-9);
    }

    [Fact]
    public void DepthLevels_AreArithmetic_AndEveryLevelRepeatsTheRings()
    {
        // Depth 5 at StepDown 2: levels −2, −4, −5 — the last CLAMPED to the stated depth,
        // never accumulated past it.
        Assert.Equal([-2.0, -4.0, -5.0], CncMill.DepthLevels(5, 2));
        Assert.Equal([-4.0], CncMill.DepthLevels(4, 5));

        var pocket = CncMill.Pocket(Rect(30, 20), Tool(), depth: 5);
        var zs = pocket.Passes.Select(p => p.Points[0].Z).Distinct().ToList();
        Assert.Equal([-2.0, -4.0, -5.0], zs);
        // The same ring count at every level.
        var perLevel = pocket.Passes.GroupBy(p => p.Points[0].Z).Select(g => g.Count()).Distinct();
        Assert.Single(perLevel);
    }

    [Fact]
    public void AnIslandPocket_RidesTheHoleLoops_AndClearsThemByARadius()
    {
        // A pocket with a rectangular island: the offset's hole loops (the island grown by the
        // insets) are passes too, and every pass clears the ISLAND boundary by the radius as
        // exactly as it clears the outer wall.
        var region = new Region2d(
            [new Vector2d(0, 0), new Vector2d(40, 0), new Vector2d(40, 24), new Vector2d(0, 24)],
            [[new Vector2d(16, 8), new Vector2d(24, 8), new Vector2d(24, 16), new Vector2d(16, 16)]]);
        var pocket = CncMill.Pocket(region, Tool(), depth: 2);
        Assert.True(pocket.Passes.Count > 2);                // outer rings + island rings
        foreach (var pass in pocket.Passes)
            foreach (var p in pass.Points)
                Assert.True(DistanceToBoundary(region, new Vector2d(p.X, p.Y)) >= 3 - 1e-9);
    }

    [Fact]
    public void TheOutsideProfile_RunsAtExactlyOneRadius_WithTheRoundCornerBand()
    {
        // An outside profile of a 30×20 outline at r = 3, round joins: the path is the outline
        // grown by r, whose length is 2(a+b) + 2πr — the corner arcs are chorded (inscribed),
        // so the measured length sits just BELOW the closed form, never above.
        var profile = CncMill.Profile(Rect(30, 20), Tool(), depth: 4, ProfileSide.Outside);
        var final = profile.Passes.Where(p => p.Points[0].Z == -4).ToList();
        double length = final.Sum(p => p.CutLength);
        double exact = 2 * (30 + 20) + 2 * Math.PI * 3;
        Assert.InRange(length, exact * 0.995, exact + 1e-9);

        // And the minimum clearance from the outline is the tool radius (the corner arcs'
        // chords dip slightly inside the true offset circle, never inside the radius band).
        double min = double.PositiveInfinity;
        foreach (var pass in final)
            foreach (var p in pass.Points)
                min = Math.Min(min, DistanceToBoundary(Rect(30, 20), new Vector2d(p.X, p.Y)));
        // The corner arcs are chorded at the 1e-3 arc tolerance, so the minimum sits within a
        // sagitta below the exact radius, never below r − tolerance.
        Assert.InRange(min, 3.0 - 2e-3, 3.0 + 1e-9);
    }

    [Fact]
    public void Tabs_LiftTheFinalPassOnly_AtTheStatedHeight()
    {
        var profile = CncMill.Profile(
            Rect(30, 20), Tool(), depth: 6, ProfileSide.Outside,
            tabs: 3, tabHeight: 2, tabWidth: 8);

        // Levels −2, −4, −6: the first two are plain closed loops at constant z…
        var upper = profile.Passes.Where(p => p.Points.All(q => q.Z == p.Points[0].Z)).ToList();
        Assert.Equal(2, upper.Count);

        // …and the final pass rises to −6 + 2 = −4 exactly 3 times (one lift per tab).
        var final = profile.Passes.Single(p => p.Points.Any(q => q.Z != p.Points[0].Z));
        int lifts = 0;
        for (int i = 1; i < final.Points.Count; i++)
            if (final.Points[i].Z > final.Points[i - 1].Z)
                lifts++;
        Assert.Equal(3, lifts);
        Assert.Equal(-4.0, final.Points.Max(p => p.Z), 12);
        Assert.Equal(-6.0, final.Points.Min(p => p.Z), 12);
    }

    [Fact]
    public void Drilling_ExpandsToPeckMoves_TheTwinDecoderReads()
    {
        var drill = CncMill.Drill(
            [new Vector2d(5, 5), new Vector2d(15, 5)], Tool(3), depth: 10, peck: 4);
        string gcode = CncGcodeWriter.Write([drill]);
        var decoded = GcodeReader.Read(gcode);

        // Plain G0/G1 only — the FDM twin decoder reads a drill cycle with nothing new — and
        // each point pecks to −4, −8, −10 with retracts between.
        Assert.DoesNotContain("G81", gcode);
        Assert.DoesNotContain("G83", gcode);
        Assert.Equal(-10.0, decoded.Moves.Min(m => m.To.Z), 3);
        var plunges = decoded.Moves
            .Where(m => m.To.Z < m.From.Z && m.To.X == m.From.X && m.To.Y == m.From.Y)
            .Select(m => m.To.Z).ToList();
        Assert.Equal(6, plunges.Count);                      // three pecks per point
        Assert.Equal([-4.0, -8.0, -10.0, -4.0, -8.0, -10.0], plunges);

        // Plunges run at the plunge rate, never the cut feed.
        foreach (var m in decoded.Moves.Where(x => x.To.Z < x.From.Z && x.XyLength == 0))
            Assert.Equal(Tool(3).PlungeRate, m.Feed, 9);
    }

    [Fact]
    public void TheGcode_RoundTripsItsCutLength_ThroughTheDecoder()
    {
        var region = Rect(30, 20);
        var ops = new[]
        {
            CncMill.Pocket(region, Tool(), depth: 3),
            CncMill.Profile(region, Tool(), depth: 3, ProfileSide.Outside),
        };
        var decoded = GcodeReader.Read(CncGcodeWriter.Write(ops));

        // Cut moves are the NON-RAPID XY moves at the tools' feed rate (the Rapid flag is what
        // separates a G0 hop from a G1 cut — feed state persists across both, so the feed alone
        // cannot); their decoded total equals the operations' own cut length to formatting
        // precision.
        double decodedCut = decoded.Moves
            .Where(m => !m.Rapid && m.XyLength > 0 && m.Feed == Tool().FeedRate)
            .Sum(m => m.XyLength);
        double stated = ops.Sum(o => o.CutLength);
        Assert.Equal(stated, decodedCut, stated * 1e-3);
    }

    [Fact]
    public void TheProgram_IsDeterministic()
    {
        string Once()
        {
            var region = Rect(30, 20);
            return CncGcodeWriter.Write(
            [
                CncMill.Pocket(region, Tool(), depth: 5),
                CncMill.Profile(region, Tool(), depth: 5, ProfileSide.Outside, tabs: 2,
                    tabHeight: 1.5, tabWidth: 6),
                CncMill.Drill([new Vector2d(5, 5)], Tool(3), 8, peck: 3),
            ]);
        }
        Assert.Equal(Once(), Once());
    }

    [Fact]
    public void TheRefusals_NameTheirGeometry()
    {
        // A tool that does not fit the region at all.
        Assert.Contains("does not fit", Assert.Throws<ArgumentException>(() =>
            CncMill.Pocket(Rect(4, 4), Tool(10), 2)).Message);
        // Tabs without their numbers, and tabs that consume the outline.
        Assert.Contains("tab height", Assert.Throws<ArgumentException>(() =>
            CncMill.Profile(Rect(30, 20), Tool(), 4, ProfileSide.Outside, tabs: 2)).Message);
        Assert.Contains("consume", Assert.Throws<ArgumentException>(() =>
            CncMill.Profile(Rect(10, 10), Tool(), 4, ProfileSide.Outside,
                tabs: 4, tabHeight: 1, tabWidth: 30)).Message);
        // Bad tool numbers, bad depth, an empty drill.
        Assert.Throws<ArgumentException>(() => new MillTool(0).Validate());
        Assert.Throws<ArgumentException>(() => new MillTool(6, Stepover: 1.5).Validate());
        Assert.Throws<ArgumentException>(() => CncMill.Pocket(Rect(10, 10), Tool(), 0));
        Assert.Throws<ArgumentException>(() => CncMill.Drill([], Tool(3), 5));
    }
}
