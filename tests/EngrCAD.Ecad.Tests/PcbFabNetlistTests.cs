using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The IPC-D-356A netlist rides in the fabrication package (opt-in) beside the Gerber set, so a board
/// house gets the net-compare deliverable with the rest of the fab output. With the flag off the Gerber
/// / drill files are byte-identical — the netlist adds a file, it does not change the copper.
/// </summary>
public sealed class PcbFabNetlistTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "engrcad-fab-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void IncludeNetlist_WritesTheIpcFileBesideTheGerbers()
    {
        var layout = PcbFixtures.Layout();
        string dir = TempDir();
        try
        {
            var result = PcbGerberExport.Write(layout, dir, "blinky", includeNetlist: true);

            Assert.True(result.NetlistWritten);
            string ipc = result.Files.Single(f => f.EndsWith("blinky.ipc"));
            Assert.True(File.Exists(ipc));

            // The written .ipc IS PcbIpc356.Write(layout) (no drift between disk and text), and it
            // parses back through the twin decoder.
            string text = File.ReadAllText(ipc);
            Assert.Equal(PcbIpc356.Write(layout), text);
            Assert.NotEmpty(PcbIpc356.Parse(text));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WithoutTheFlag_NoNetlistIsWrittenAndTheGerbersAreUnchanged()
    {
        var layout = PcbFixtures.Layout();
        string off = TempDir(), on = TempDir();
        try
        {
            var plain = PcbGerberExport.Write(layout, off, "blinky");
            var withNet = PcbGerberExport.Write(layout, on, "blinky", includeNetlist: true);

            Assert.False(plain.NetlistWritten);
            Assert.DoesNotContain(plain.Files, f => f.EndsWith(".ipc"));

            // The Gerber / drill files are exactly the same content whether or not the netlist rides
            // along — the flag adds a file, it does not touch the copper.
            Assert.Equal(plain.CopperLayerCount, withNet.CopperLayerCount);
            foreach (string p in plain.Files)
            {
                string name = Path.GetFileName(p);
                string other = Path.Combine(on, name);
                Assert.True(File.Exists(other), name);
                Assert.Equal(File.ReadAllText(p), File.ReadAllText(other));
            }
            // ...and the netlist run wrote exactly ONE more file (the .ipc).
            Assert.Equal(plain.Files.Count + 1, withNet.Files.Count);
        }
        finally
        {
            if (Directory.Exists(off)) Directory.Delete(off, recursive: true);
            if (Directory.Exists(on)) Directory.Delete(on, recursive: true);
        }
    }
}
