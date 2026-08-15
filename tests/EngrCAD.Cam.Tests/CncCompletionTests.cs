using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// Climb/conventional direction (derived: material on the LEFT of travel = climb for an M3
/// right-hand cutter), canned G81/G83 drilling cycles (the twin decoder expands them under
/// Fanuc semantics), and the ⚠ feeds-and-speeds tool library.
/// </summary>
public class CncCompletionTests
{
    private static Region2d Rect(double a, double b) => new(
        [new Vector2d(0, 0), new Vector2d(a, 0), new Vector2d(a, b), new Vector2d(0, b)]);

    private static double SignedArea(IReadOnlyList<Vector3d> loop)
    {
        double a = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            var p = loop[i];
            var q = loop[(i + 1) % loop.Count];
            a += p.X * q.Y - q.X * p.Y;
        }
        return a / 2;
    }

    // ---- climb / conventional ------------------------------------------------------------

    [Fact]
    public void APocketClimbs_ClockwiseRings_AndConventionalIsTheExactReversal()
    {
        var region = Rect(30, 20);
        var tool = new MillTool(6);
        var climb = CncMill.Pocket(region, tool, 3);         // Climb is the default
        var conventional = CncMill.Pocket(region, tool, 3,
            direction: MillDirection.Conventional);

        // Cutting inside the outline the material lies BEYOND each ring, so climb walks it
        // clockwise (material left) — every closed pass negative, conventional positive.
        Assert.All(climb.Passes.Where(p => p.IsClosed),
            p => Assert.True(SignedArea(p.Points) < 0, "climb pocket rings run clockwise"));
        Assert.All(conventional.Passes.Where(p => p.IsClosed),
            p => Assert.True(SignedArea(p.Points) > 0, "conventional pocket rings run CCW"));

        // The direction changes traversal ONLY: same passes, same starts, same cut length.
        Assert.Equal(climb.Passes.Count, conventional.Passes.Count);
        Assert.Equal(climb.CutLength, conventional.CutLength, 9);
        for (int i = 0; i < climb.Passes.Count; i++)
        {
            Assert.Equal(climb.Passes[i].Points[0], conventional.Passes[i].Points[0]);
            Assert.Equal(
                climb.Passes[i].Points.OrderBy(p => (p.X, p.Y, p.Z)),
                conventional.Passes[i].Points.OrderBy(p => (p.X, p.Y, p.Z)));
        }
    }

    [Fact]
    public void AProfileClimbs_CcwOutside_CwInside()
    {
        var region = Rect(30, 20);
        var tool = new MillTool(6);

        // Outside: the part is INSIDE the loop, so climb keeps the material left = CCW.
        var outside = CncMill.Profile(region, tool, 3, ProfileSide.Outside);
        Assert.All(outside.Passes.Where(p => p.IsClosed),
            p => Assert.True(SignedArea(p.Points) > 0, "outside climb runs CCW"));

        // Inside: the pocket relationship — material beyond the loop, climb = CW.
        var inside = CncMill.Profile(region, tool, 3, ProfileSide.Inside);
        Assert.All(inside.Passes.Where(p => p.IsClosed),
            p => Assert.True(SignedArea(p.Points) < 0, "inside climb runs CW"));
    }

    [Fact]
    public void AnIslandPocket_OrientsOuterAndIslandRingsOppositely()
    {
        // A plate with an island: climb walks outer-derived rings CW (material beyond) and
        // island-derived rings CCW (material within) — under conventional the whole multiset
        // of signed areas is exactly negated.
        var island = Enumerable.Range(0, 16).Select(i =>
        {
            double t = 2 * Math.PI * i / 16;
            return new Vector2d(20 + 6 * Math.Cos(t), 15 + 6 * Math.Sin(t));
        }).Reverse().ToList();
        var region = new Region2d(
            [new Vector2d(0, 0), new Vector2d(40, 0), new Vector2d(40, 30), new Vector2d(0, 30)],
            [island]);
        var tool = new MillTool(4);
        var climb = CncMill.Pocket(region, tool, 2);
        var conventional = CncMill.Pocket(region, tool, 2,
            direction: MillDirection.Conventional);

        var climbAreas = climb.Passes.Where(p => p.IsClosed)
            .Select(p => SignedArea(p.Points)).OrderBy(a => a).ToList();
        var conventionalAreas = conventional.Passes.Where(p => p.IsClosed)
            .Select(p => -SignedArea(p.Points)).OrderBy(a => a).ToList();
        Assert.Contains(climbAreas, a => a < 0);
        Assert.Contains(climbAreas, a => a > 0);             // the island rings flip the sign
        Assert.Equal(climbAreas.Count, conventionalAreas.Count);
        for (int i = 0; i < climbAreas.Count; i++)           // reversal reorders the shoelace
            Assert.Equal(climbAreas[i], conventionalAreas[i], 9);   // sum: equal to round-off
    }

    // ---- canned drilling cycles ----------------------------------------------------------

    [Fact]
    public void PeckedDrilling_EmitsG83_AndDecodesToTheSameHoles()
    {
        var points = new List<Vector2d> { new(0, 0), new(15, 0), new(15, 12) };
        var tool = new MillTool(4, PlungeRate: 90);
        var op = CncMill.Drill(points, tool, depth: 5, peck: 2);

        string canned = CncGcodeWriter.Write([op], cannedDrilling: true);
        Assert.Contains("G98", canned);
        Assert.Contains("G83", canned);
        Assert.Contains("Q2", canned);
        Assert.Contains("G80", canned);
        Assert.DoesNotContain("G81", canned);

        var decoded = GcodeReader.Read(canned);
        var expanded = GcodeReader.Read(CncGcodeWriter.Write([op]));

        // Same holes: every site reached, the same final depth, through either spelling.
        foreach (var route in new[] { decoded, expanded })
            foreach (var site in points)
            {
                var atSite = route.Moves.Where(m =>
                    m.To.X == site.X && m.To.Y == site.Y).ToList();
                Assert.NotEmpty(atSite);
                Assert.Equal(-5, atSite.Min(m => m.To.Z), 12);
            }

        // The canned bites descend by exactly Q from the R plane (Fanuc semantics — R above
        // the expanded twin's stock-top ladder, conservative, never a deeper bite), each feed
        // move non-rapid and each retract a rapid back to R.
        var feeds = decoded.Moves.Where(m =>
            !m.Rapid && m.To.X == 0 && m.To.Y == 0 && m.To.Z < m.From.Z).ToList();
        Assert.Equal(0.5 - 2, feeds[0].To.Z, 12);
        Assert.Equal(0.5 - 4, feeds[1].To.Z, 12);
        Assert.Equal(-5, feeds[2].To.Z, 12);
        Assert.All(feeds, m => Assert.Equal(90, m.Feed));
        var retracts = decoded.Moves.Where(m =>
            m.Rapid && m.To.X == 0 && m.To.Y == 0 && m.To.Z == 0.5 && m.From.Z < 0).ToList();
        Assert.Equal(2, retracts.Count);
    }

    [Fact]
    public void ASinglePlunge_EmitsG81_AndReturnsToTheInitialLevel()
    {
        var op = CncMill.Drill([new Vector2d(3, 4)], new MillTool(4), depth: 6);
        string canned = CncGcodeWriter.Write([op], safeZ: 7, cannedDrilling: true);
        Assert.Contains("G81", canned);
        Assert.DoesNotContain("Q", canned.Split('\n').First(l => l.Contains("G81")));

        var decoded = GcodeReader.Read(canned);
        var feed = decoded.Moves.Single(m => !m.Rapid && m.To.Z == -6);
        Assert.Equal(0.5, feed.From.Z, 12);                  // fed from R, not from safe Z
        // G98: the cycle returns to the initial level (the safe height the writer set).
        var back = decoded.Moves.First(m => m.From.Z == -6);
        Assert.True(back.Rapid);
        Assert.Equal(7, back.To.Z, 12);
    }

    [Fact]
    public void CannedOff_IsByteIdentical_AndAnIrregularPassFallsBack()
    {
        var op = CncMill.Drill([new Vector2d(0, 0), new Vector2d(9, 9)],
            new MillTool(4), depth: 5, peck: 2);
        Assert.Equal(CncGcodeWriter.Write([op]), CncGcodeWriter.Write([op], 5, false));
        Assert.DoesNotContain("G83", CncGcodeWriter.Write([op]));

        // A same-XY pass whose bites are NOT the uniform ladder cannot be spelled as one
        // cycle — it falls back to expanded moves (sound in the accept direction).
        var irregular = new MillOperation("odd", new MillTool(4), [new MillPass(
            [new Vector3d(2, 2, -1), new Vector3d(2, 2, 0.5), new Vector3d(2, 2, -4.7)],
            IsClosed: false)]);
        string text = CncGcodeWriter.Write([irregular], cannedDrilling: true);
        Assert.DoesNotContain("G83", text);
        Assert.DoesNotContain("G81", text);
        Assert.Equal(-4.7, GcodeReader.Read(text).Moves.Min(m => m.To.Z), 12);
    }

    [Fact]
    public void TheDecoder_RunsModalCycles_AndRefusesAGuessedDepthByName()
    {
        // A bare X line while a cycle is active re-executes it at the new site — the real
        // Fanuc modal form, which a writer other than ours is free to emit.
        var modal = GcodeReader.Read(
            "G21\nG90\nG98\nG81 X0 Y0 Z-3 R0.5 F100\nX10\nG80\n");
        Assert.Equal(2, modal.Moves.Count(m => !m.Rapid && m.To.Z == -3));
        Assert.Contains(modal.Moves, m => m.To.X == 10 && m.To.Z == -3);

        Assert.Contains("Z depth", Assert.Throws<FormatException>(() =>
            GcodeReader.Read("G81 X0 Y0 R0.5\n")).Message);
        Assert.Contains("R retract", Assert.Throws<FormatException>(() =>
            GcodeReader.Read("G81 X0 Y0 Z-3\n")).Message);
        Assert.Contains("Q", Assert.Throws<FormatException>(() =>
            GcodeReader.Read("G83 X0 Y0 Z-3 R0.5\n")).Message);
    }

    [Fact]
    public void AMixedOperation_KeepsItsMillingPassesUnchangedAroundTheCycles()
    {
        var region = Rect(20, 14);
        var tool = new MillTool(5);
        var ops = new[]
        {
            CncMill.Pocket(region, tool, 2),
            CncMill.Drill([new Vector2d(0, 0)], tool, 4, peck: 1.5),
        };
        var canned = GcodeReader.Read(CncGcodeWriter.Write(ops, cannedDrilling: true));
        var expanded = GcodeReader.Read(CncGcodeWriter.Write(ops));
        // The milling content is untouched by the drill spelling: identical XY cut length.
        double CutLength(GcodeProgram p) => p.Moves
            .Where(m => !m.Rapid && m.XyLength > 0).Sum(m => m.XyLength);
        Assert.Equal(CutLength(expanded), CutLength(canned), 9);
        Assert.Equal(-4, canned.Moves.Min(m => m.To.Z), 12);
    }

    // ---- the feeds-and-speeds library ----------------------------------------------------

    [Fact]
    public void TheCatalogue_TranscribesInDatasheetForm_AndAllListsEveryEntry()
    {
        // ⚠ transcription tests in the chart's own units (Vc, m/min) — a re-typed formula
        // agrees with its own mistake; the value IS the transcription.
        Assert.Equal(250, MillMaterials.Aluminum6061.SurfaceSpeed);
        Assert.Equal(100, MillMaterials.MildSteel.SurfaceSpeed);
        Assert.Equal(60, MillMaterials.Stainless304.SurfaceSpeed);
        Assert.Equal(300, MillMaterials.Acetal.SurfaceSpeed);

        // The coverage claim: All lists exactly the published static entries.
        var published = typeof(MillMaterials)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(MillMaterial))
            .Select(f => (MillMaterial)f.GetValue(null)!)
            .ToHashSet();
        Assert.Equal(published, MillMaterials.All.ToHashSet());
    }

    [Fact]
    public void Suggest_IsTheTwoChartIdentities_AndTheCapPreservesChipLoad()
    {
        // rpm = 1000·Vc/(π·D); feed = rpm × flutes × (D × chip fraction).
        var tool = CncToolLibrary.Suggest(MillMaterials.Aluminum6061, diameter: 6);
        Assert.Equal(1000 * 250 / (Math.PI * 6), tool.SpindleRpm, 9);
        Assert.Equal(tool.SpindleRpm * 2 * (6.0 / 150), tool.FeedRate, 9);
        Assert.Equal(3, tool.StepDown, 12);

        // A small tool asks for more rpm than the spindle has: the cap holds the CHIP LOAD,
        // so the feed drops in proportion — feed/(rpm·flutes) is the same number either way.
        var free = CncToolLibrary.Suggest(MillMaterials.Aluminum6061, 2, maxRpm: 1e6);
        var capped = CncToolLibrary.Suggest(MillMaterials.Aluminum6061, 2, maxRpm: 24000);
        Assert.True(capped.SpindleRpm < free.SpindleRpm);
        Assert.Equal(24000, capped.SpindleRpm);
        Assert.Equal(
            free.FeedRate / (free.SpindleRpm * 2),
            capped.FeedRate / (capped.SpindleRpm * 2), 12);

        // Steel runs slower than aluminium in BOTH numbers at the same diameter.
        var steel = CncToolLibrary.Suggest(MillMaterials.MildSteel, 6);
        var alu = CncToolLibrary.Suggest(MillMaterials.Aluminum6061, 6);
        Assert.True(steel.SpindleRpm < alu.SpindleRpm);
        Assert.True(steel.FeedRate < alu.FeedRate);

        Assert.Contains("diameter", Assert.Throws<ArgumentException>(() =>
            CncToolLibrary.Suggest(MillMaterials.Brass, 0)).Message);
        Assert.Contains("flute", Assert.Throws<ArgumentException>(() =>
            CncToolLibrary.Suggest(MillMaterials.Brass, 6, flutes: 0)).Message);
    }
}
