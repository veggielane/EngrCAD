using System.Text.Json.Nodes;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The Gerber job file (<c>.gbrjob</c>) — the JSON manifest of a fab package. The oracle is that it is
/// an HONEST manifest (every file it lists was actually written, with the right <c>FileFunction</c>), a
/// deterministic byte fixed point (no clock/GUID salt), and opt-in (off = no job file, Gerbers
/// unchanged).
/// </summary>
public sealed class PcbGerberJobFileTests
{
    private static PartDefinition Res() => new(
        "R", "R",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
        new Footprint("R", [Pad.Smd("1", new Vector2d(-1, 0), 1.2, 1.2), Pad.Smd("2", new Vector2d(1, 0), 1.2, 1.2)]));

    private static PcbLayout Board()
    {
        var sch = new Schematic("job");
        var r = sch.Add("R1", Res());
        var u = sch.Add("U1", Res());
        sch.Connect("VCC", r.Pin("1"), u.Pin("1"));
        sch.Connect("GND", r.Pin("2"), u.Pin("2"));
        var layout = new PcbLayout(sch, PcbBoard.Rectangle(40, 20, 1.6));
        layout.Place("R1", -10, 0);
        layout.Place("U1", 10, 0);
        layout.WithMask(PcbMaskSettings.Default);
        layout.WithSilkscreen(PcbSilkscreenSettings.Default);
        layout.WithPaste(PcbPasteSettings.Default);
        layout.WithFabrication(new PcbFabricationSpec { SurfaceFinish = PcbSurfaceFinish.Enig });
        return layout;
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "engrcad-job-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TheJobFileIsAnHonestManifestWithTheRightFileFunctions()
    {
        string dir = TempDir();
        try
        {
            var result = PcbGerberExport.Write(Board(), dir, "brd", includeJobFile: true);
            Assert.True(result.JobFileWritten);

            string jobPath = result.Files.Single(f => f.EndsWith(".gbrjob"));
            var job = JsonNode.Parse(File.ReadAllText(jobPath))!;

            var general = job["GeneralSpecs"]!;
            Assert.Equal("brd", general["ProjectId"]!["Name"]!.GetValue<string>());
            Assert.Equal(2, general["LayerNumber"]!.GetValue<int>());
            Assert.Equal(40.0, general["Size"]!["X"]!.GetValue<double>(), 6);
            Assert.Equal(20.0, general["Size"]!["Y"]!.GetValue<double>(), 6);
            Assert.Equal("ENIG", general["Finish"]!.GetValue<string>());

            // Every listed file was actually written, and the roles are right.
            var files = job["FilesAttributes"]!.AsArray();
            var byPath = files.ToDictionary(
                f => f!["Path"]!.GetValue<string>(), f => f!["FileFunction"]!.GetValue<string>());
            foreach (var path in byPath.Keys)
                Assert.True(File.Exists(Path.Combine(dir, path)), path);

            Assert.Equal("Copper,L1,Top", byPath["brd-Top.gbr"]);
            Assert.Equal("Copper,L2,Bot", byPath["brd-Bottom.gbr"]);
            Assert.Equal("Profile,NP", byPath["brd-Edge_Cuts.gbr"]);
            Assert.Equal("Soldermask,Top", byPath["brd-Top_Mask.gbr"]);
            Assert.Equal("Legend,Top", byPath["brd-Top_Silkscreen.gbr"]);
            Assert.Equal("SolderPaste,Top", byPath["brd-Top_Paste.gbr"]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void OffWritesNoJobFile_AndTheGerbersAreUnchanged()
    {
        string off = TempDir(), on = TempDir();
        try
        {
            var plain = PcbGerberExport.Write(Board(), off, "brd");
            var withJob = PcbGerberExport.Write(Board(), on, "brd", includeJobFile: true);

            Assert.False(plain.JobFileWritten);
            Assert.DoesNotContain(plain.Files, f => f.EndsWith(".gbrjob"));
            Assert.Equal(plain.Files.Count + 1, withJob.Files.Count);   // exactly one more file

            // Every Gerber / drill file is byte-identical whether or not the job file rides along.
            foreach (string p in plain.Files)
            {
                string other = Path.Combine(on, Path.GetFileName(p));
                Assert.True(File.Exists(other), Path.GetFileName(p));
                Assert.Equal(File.ReadAllText(p), File.ReadAllText(other));
            }
        }
        finally
        {
            if (Directory.Exists(off)) Directory.Delete(off, recursive: true);
            if (Directory.Exists(on)) Directory.Delete(on, recursive: true);
        }
    }

    [Fact]
    public void TheJobFileIsDeterministic_NoClockOrGuidSalt()
    {
        // Built twice, the JSON is byte-identical (no CreationDate / GUID).
        string j1 = PcbGerberJobFile_ForBoard();
        string j2 = PcbGerberJobFile_ForBoard();
        Assert.Equal(j1, j2);
        Assert.DoesNotContain("CreationDate", j1);
        Assert.DoesNotContain("GUID", j1);
    }

    private static string PcbGerberJobFile_ForBoard()
    {
        string dir = TempDir();
        try
        {
            var result = PcbGerberExport.Write(Board(), dir, "brd", includeJobFile: true);
            return File.ReadAllText(result.Files.Single(f => f.EndsWith(".gbrjob")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
