using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Separate top / bottom pick-and-place files. Populating each side of a board is a different machine
/// setup, so <see cref="PcbPickAndPlace.WriteBySide"/> drops one CSV + <c>.pos</c> pair per POPULATED
/// side. The split is a PARTITION of the same <see cref="PcbPickAndPlace.Compute"/> rows filtered by
/// side, so the union of the two side files' parsed rows equals the combined file's, pose for pose — and
/// a side with no components gets no file.
/// </summary>
public sealed class PcbPnpBySideTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "engrcad-pnp-" + Guid.NewGuid().ToString("N"));

    private static PartDefinition Res() => new(
        "R", "R",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
        new Footprint("R", [Pad.Smd("1", new Vector2d(-0.5, 0), 0.6, 0.6), Pad.Smd("2", new Vector2d(0.5, 0), 0.6, 0.6)]));

    // Two components on top, one on the bottom.
    private static PcbLayout Mixed()
    {
        var sch = new Schematic("mixed");
        sch.Add("T1", Res());
        sch.Add("T2", Res());
        sch.Add("B1", Res());
        var layout = new PcbLayout(sch, PcbBoard.Rectangle(40, 30, 1.6));
        layout.Place("T1", -8, 0, 0, CopperSide.Top);
        layout.Place("T2", 8, 0, 90, CopperSide.Top);
        layout.Place("B1", 0, 5, 45, CopperSide.Bottom);
        return layout;
    }

    [Fact]
    public void WriteBySide_SplitsTopAndBottomIntoSeparateFiles_AndPartitionsTheRows()
    {
        var layout = Mixed();
        string dir = TempDir();
        try
        {
            var files = PcbPickAndPlace.WriteBySide(layout, dir, "mixed");

            // A CSV + .pos pair per populated side (both sides here) — four files, top pair first.
            Assert.Equal(4, files.Count);
            string topCsv = files.Single(f => f.EndsWith("mixed-top-pos.csv"));
            string botCsv = files.Single(f => f.EndsWith("mixed-bottom-pos.csv"));
            Assert.Contains(files, f => f.EndsWith("mixed-top.pos"));
            Assert.Contains(files, f => f.EndsWith("mixed-bottom.pos"));

            var top = PcbPickAndPlace.ParseCsv(File.ReadAllText(topCsv));
            var bot = PcbPickAndPlace.ParseCsv(File.ReadAllText(botCsv));

            // Each side file carries ONLY its own side.
            Assert.All(top, r => Assert.Equal(CopperSide.Top, r.Side));
            Assert.All(bot, r => Assert.Equal(CopperSide.Bottom, r.Side));
            Assert.Equal(new[] { "T1", "T2" }, top.Select(r => r.Designator).ToArray());
            Assert.Equal(new[] { "B1" }, bot.Select(r => r.Designator).ToArray());

            // The union of the side files IS the combined file, pose for pose (the partition oracle).
            var combined = PcbPickAndPlace.ParseCsv(PcbPickAndPlace.ToCsv(layout));
            var union = top.Concat(bot).OrderBy(r => r.Designator).ToList();
            var expected = combined.OrderBy(r => r.Designator).ToList();
            Assert.Equal(expected.Count, union.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Designator, union[i].Designator);
                Assert.Equal(expected[i].X, union[i].X, 9);
                Assert.Equal(expected[i].Y, union[i].Y, 9);
                Assert.Equal(expected[i].Rotation, union[i].Rotation, 9);
                Assert.Equal(expected[i].Side, union[i].Side);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ASingleSidedBoard_WritesOnlyOnePair()
    {
        var sch = new Schematic("top");
        sch.Add("T1", Res());
        var layout = new PcbLayout(sch, PcbBoard.Rectangle(40, 30, 1.6));
        layout.Place("T1", 0, 0, 0, CopperSide.Top);

        string dir = TempDir();
        try
        {
            var files = PcbPickAndPlace.WriteBySide(layout, dir, "single");
            Assert.Equal(2, files.Count);   // one CSV + one .pos, top only — no bottom stub
            Assert.Contains(files, f => f.EndsWith("single-top-pos.csv"));
            Assert.Contains(files, f => f.EndsWith("single-top.pos"));
            Assert.DoesNotContain(files, f => f.Contains("bottom"));
            Assert.False(File.Exists(Path.Combine(dir, "single-bottom-pos.csv")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EachSideFileRowIsByteIdenticalToTheCombinedFilesRow()
    {
        // The split is a filter, not a re-projection: a side file's CSV data lines appear verbatim in the
        // combined CSV, so no pose can drift between the two forms.
        var layout = Mixed();
        string dir = TempDir();
        try
        {
            PcbPickAndPlace.WriteBySide(layout, dir, "mixed");
            string topCsv = File.ReadAllText(Path.Combine(dir, "mixed-top-pos.csv"));
            string combined = PcbPickAndPlace.ToCsv(layout);

            foreach (var line in topCsv.Split('\n').Skip(1).Where(l => l.Length > 0))
                Assert.Contains(line, combined);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
