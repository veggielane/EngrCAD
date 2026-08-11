using EngrCAD.Core;
using EngrCAD.Ecad;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The IPC-D-356A bare-board netlist export. ECAD fails plausibly (a netlist that mislabels an access
/// point is a silent fab failure), so the bar is the TWIN-DECODER round trip plus a NET RECONSTRUCTION:
/// the partition of component pads the file induces (which pads share a net) equals the board's OWN
/// partition — the same one the copper model / connectivity report — and a dropped or relabelled record
/// makes them differ (the mutation that proves the oracle bites).
/// </summary>
public sealed class PcbIpc356Tests
{
    // ---- fixtures -----------------------------------------------------------

    // A 2-pad SMD resistor, sized by a scale so the whole board can be built at any magnitude.
    private static PartDefinition Res2(double s) => new(
        "R_0805", "R",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
        new Footprint("R0805", [
            Pad.Smd("1", new Vector2d(-1.0 * s, 0), 1.2 * s, 1.4 * s),
            Pad.Smd("2", new Vector2d(1.0 * s, 0), 1.2 * s, 1.4 * s),
        ]));

    // A 2-pad through-hole header (drills the board).
    private static PartDefinition Hdr2(double s) => new(
        "HDR_1x2", "J",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
        new Footprint("HDR254", [
            Pad.ThroughHole("1", new Vector2d(-1.27 * s, 0), pad: 1.6 * s, drill: 0.9 * s),
            Pad.ThroughHole("2", new Vector2d(1.27 * s, 0), pad: 1.6 * s, drill: 0.9 * s),
        ]));

    // A 3-pad SMD part with a deliberately-unconnected third pin.
    private static PartDefinition Sot3(double s) => new(
        "SOT_23", "U",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive), new Pin("3", PinType.Passive)],
        new Footprint("SOT23", [
            Pad.Smd("1", new Vector2d(-0.95 * s, 0.5 * s), 0.6 * s, 0.6 * s),
            Pad.Smd("2", new Vector2d(-0.95 * s, -0.5 * s), 0.6 * s, 0.6 * s),
            Pad.Smd("3", new Vector2d(0.95 * s, 0), 0.6 * s, 0.6 * s),
        ]));

    // A layout at a given scale: R1 (SMD top), J1 (through-hole top), U1 (SMD bottom, one unconnected
    // pin), and a through via on GND. Everything scales together, so net reconstruction is scale-free.
    private static PcbLayout BuildLayout(double s = 1.0)
    {
        var sch = new Schematic("ipc-fixture");
        var r = sch.Add("R1", Res2(s), "330");
        var j = sch.Add("J1", Hdr2(s));
        var u = sch.Add("U1", Sot3(s));
        sch.Connect("VCC", j.Pin("1"), r.Pin("1"));
        sch.Connect("SIG", r.Pin("2"), u.Pin("2"));
        sch.Connect("GND", j.Pin("2"), u.Pin("1"));
        sch.NoConnect(u.Pin("3"));   // U1.3 → its own single-point net in the netlist

        var board = PcbBoard.Rectangle(50 * s, 40 * s, 1.6 * s);
        var layout = new PcbLayout(sch, board);
        layout.Place("R1", 5 * s, 0, 0, CopperSide.Top);
        layout.Place("J1", -8 * s, 4 * s, 90, CopperSide.Top);
        layout.Place("U1", 0, -10 * s, 0, CopperSide.Bottom);
        // A through via on GND. The drill is 1 mm so it stays representable at the 1e-3× scale (1 µm,
        // the file's quantum) — a smaller feature would round below the µm resolution.
        layout.AddVia("GND", 10 * s, 5 * s, "Top", "Bottom", drill: 1.0 * s, pad: 1.6 * s);
        return layout;
    }

    // The partition a set of access points induces over its COMPONENT pads: sets of "R1.1" grouped by
    // net name. Vias (no component reference) are excluded — the strong oracle is over terminals.
    private static HashSet<string> Partition(IEnumerable<Ipc356AccessPoint> points)
    {
        var byNet = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var p in points.Where(p => !p.IsVia))
        {
            if (!byNet.TryGetValue(p.Net, out var set))
                byNet[p.Net] = set = [];
            set.Add(p.PadName);
        }
        return byNet.Values.Select(s => string.Join(",", s)).ToHashSet(StringComparer.Ordinal);
    }

    // The board's OWN partition of component pads, read through the SAME copper model the DRC reads: a
    // named net is one class, and a null-net (unconnected / no-connect) pad is its own singleton.
    private static HashSet<string> ModelPartition(PcbLayout layout)
    {
        var model = PcbCopperModel.FromLayout(layout);
        var viaSources = new HashSet<string>(model.Vias.Select(v => v.Source), StringComparer.Ordinal);
        var padNet = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var f in model.Copper)
            if (!viaSources.Contains(f.Source) && !model.TraceSources.Contains(f.Source)
                && !model.PourSources.Contains(f.Source))
                padNet[f.Source] = f.Net;

        var byClass = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var (src, net) in padNet)
        {
            string key = net ?? ("\0null\0" + src);   // each null-net pad is its own class
            if (!byClass.TryGetValue(key, out var set))
                byClass[key] = set = [];
            set.Add(src);
        }
        return byClass.Values.Select(s => string.Join(",", s)).ToHashSet(StringComparer.Ordinal);
    }

    // ---- 1. projection identity: the record IS the pad / via ----------------

    [Fact]
    public void EachAccessPointProjectsItsPadOrVia()
    {
        var layout = BuildLayout();
        var points = PcbIpc356.Compute(layout);

        var padByName = layout.PlacedPads().ToDictionary(p => p.Name);
        var netOfPad = NetOfPad(layout);

        int pads = 0, vias = 0;
        foreach (var ap in points)
        {
            if (ap.IsVia)
            {
                vias++;
                var via = layout.PlacedVias().Single(v => v.Net == ap.Net);
                Assert.Equal(via.Center.X, ap.Xmm, 3);
                Assert.Equal(via.Center.Y, ap.Ymm, 3);
                Assert.Equal(via.DrillDiameter, ap.DrillMm, 3);
                Assert.True(ap.IsDrilled);
                Assert.Equal(0, ap.Access);   // a through via reaches both faces
                continue;
            }

            pads++;
            var pad = padByName[ap.PadName];
            Assert.Equal(pad.World.X, ap.Xmm, 3);          // pure projection, exact to file precision
            Assert.Equal(pad.World.Y, ap.Ymm, 3);
            // Net resolved through the copper model (null → its own synthetic single-point net).
            string? net = netOfPad[pad.Name];
            if (net is null)
                Assert.StartsWith("N/C-", ap.Net);
            else
                Assert.Equal(net, ap.Net);
            // Feature / drill / access per kind.
            if (pad.Kind == PadKind.ThroughHole)
            {
                Assert.Equal(Ipc356Feature.ThroughHolePad, ap.Feature);
                Assert.Equal(pad.DrillDiameter, ap.DrillMm, 3);
                Assert.Equal(0, ap.Access);                 // all layers
            }
            else
            {
                Assert.Equal(Ipc356Feature.SurfacePad, ap.Feature);
                Assert.Equal(0, ap.DrillUm);                // SMD pads carry no drill
            }
        }

        Assert.Equal(layout.PlacedPads().Count, pads);
        Assert.Equal(layout.PlacedVias().Count, vias);
    }

    // ---- 2. the strong oracle: net reconstruction + the mutation ------------

    [Fact]
    public void FileReconstructsTheBoardsOwnNetPartition()
    {
        var layout = BuildLayout();
        string text = PcbIpc356.Write(layout);
        var parsed = PcbIpc356.Parse(text);

        // The partition the file induces over component pads EQUALS the board's own partition.
        Assert.Equal(ModelPartition(layout), Partition(parsed));

        // Sanity: the named classes are exactly what the schematic wired, plus U1.3 as its own class.
        var classes = Partition(parsed);
        Assert.Contains("J1.1,R1.1", classes);   // VCC
        Assert.Contains("R1.2,U1.2", classes);   // SIG
        Assert.Contains("J1.2,U1.1", classes);   // GND
        Assert.Contains("U1.3", classes);        // the unconnected pin, its own single-point net

        // THE MUTATION: drop one component-pad record. A net's pad set shrinks, so the reconstructed
        // partition no longer equals the board's — the oracle bites.
        var dropped = parsed.Where(p => p.PadName != "R1.1").ToList();
        Assert.NotEqual(ModelPartition(layout), Partition(dropped));

        // THE MUTATION (relabel): move a pad onto another net. Two classes merge → partition differs.
        var relabelled = parsed
            .Select(p => p.PadName == "U1.3" ? p with { Net = "GND" } : p)
            .ToList();
        Assert.NotEqual(ModelPartition(layout), Partition(relabelled));
    }

    [Fact]
    public void TwoAccessPointsShareANetNameIffTheyShareANetOnTheBoard()
    {
        var layout = BuildLayout();
        var parsed = PcbIpc356.Parse(PcbIpc356.Write(layout));

        // A via on GND shares the GND net name with the GND pads (same net on the board).
        var gnd = parsed.Where(p => p.Net == "GND").ToList();
        Assert.Contains(gnd, p => p.IsVia);                              // the GND via is present
        Assert.Contains(gnd, p => p.PadName == "J1.2");                  // and the GND pads
        Assert.Contains(gnd, p => p.PadName == "U1.1");

        // Two unconnected pads would NOT share (each its own synthetic net) — here only U1.3 is
        // unconnected, so it is a singleton class with a distinct N/C name.
        Assert.Single(parsed, p => p.PadName == "U1.3");
        Assert.StartsWith("N/C-", parsed.Single(p => p.PadName == "U1.3").Net);
    }

    // ---- 3. completeness / count --------------------------------------------

    [Fact]
    public void EveryPadAndViaAppearsExactlyOnce()
    {
        var layout = BuildLayout();
        var parsed = PcbIpc356.Parse(PcbIpc356.Write(layout));

        int expected = layout.PlacedPads().Count + layout.PlacedVias().Count;
        Assert.Equal(expected, parsed.Count);

        // Each component pad appears once (keyed by its canonical name), each via once (keyed by xy).
        var padNames = parsed.Where(p => !p.IsVia).Select(p => p.PadName).ToList();
        Assert.Equal(padNames.Count, padNames.Distinct().Count());
        Assert.Equal(layout.PlacedPads().Count, padNames.Count);
        Assert.Equal(layout.PlacedVias().Count, parsed.Count(p => p.IsVia));
    }

    // ---- 4. drilled features carry their drill; SMD carry none --------------

    [Fact]
    public void DrilledFeaturesCarryADrillAndSmdPadsCarryNone()
    {
        var layout = BuildLayout();
        var parsed = PcbIpc356.Parse(PcbIpc356.Write(layout));

        foreach (var p in parsed)
        {
            if (p.Feature == Ipc356Feature.SurfacePad)
            {
                Assert.Equal(0, p.DrillUm);
                Assert.False(p.IsDrilled);
            }
            else   // through-hole pad or via
            {
                Assert.True(p.DrillUm > 0);
                Assert.True(p.IsDrilled);
            }
        }

        // The text itself: 327 records carry no D token, 317 records do.
        string text = PcbIpc356.Write(layout);
        foreach (var line in text.Split('\n').Where(l => l.StartsWith("327", StringComparison.Ordinal)))
            Assert.DoesNotContain(" D", line[3..]);
        foreach (var line in text.Split('\n').Where(l => l.StartsWith("317", StringComparison.Ordinal)))
            Assert.Contains(" D", line[3..]);
    }

    // ---- 5. determinism ------------------------------------------------------

    [Fact]
    public void TwoEmissionsAreByteIdentical()
    {
        var layout = BuildLayout();
        Assert.Equal(PcbIpc356.Write(layout), PcbIpc356.Write(layout));

        // And the file is a byte-identical fixed point through the twin decoder (proves the reader
        // recovers everything the writer needs — a wrong coordinate would break the fixed point).
        var pts = PcbIpc356.Compute(layout);
        string text = PcbIpc356.Write(pts);
        Assert.Equal(text, PcbIpc356.Write(PcbIpc356.Parse(text)));
    }

    // ---- 6. units / format round-trip + the 10^n scale tell -----------------

    [Fact]
    public void CoordinatesRoundTripToTheFilesPrecisionAndTheScaleIsSane()
    {
        var layout = BuildLayout();
        var written = PcbIpc356.Compute(layout);
        var read = PcbIpc356.Parse(PcbIpc356.Write(written));

        // Match by canonical identity and assert the micrometre integers are recovered EXACTLY.
        Assert.Equal(written.Count, read.Count);
        var readByKey = read.ToDictionary(Key);
        foreach (var w in written)
        {
            var r = readByKey[Key(w)];
            Assert.Equal(w.Xum, r.Xum);   // exact integer round trip
            Assert.Equal(w.Yum, r.Yum);
            Assert.Equal(w.DrillUm, r.DrillUm);
            Assert.Equal(w.Access, r.Access);
        }

        // The 10^n tell: a ~50 mm board's coordinates live in the 1e3–1e5 µm band. A wrong unit scale
        // (mm-integers, or nanometres) would put them three orders away — caught by a magnitude band.
        foreach (var p in written)
        {
            Assert.True(Math.Abs(p.Xmm) <= 30.0, $"X out of the mm band: {p.Xmm}");
            Assert.True(Math.Abs(p.Ymm) <= 25.0, $"Y out of the mm band: {p.Ymm}");
        }
        long maxUm = written.Max(p => Math.Max(Math.Abs(p.Xum), Math.Abs(p.Yum)));
        Assert.InRange(maxUm, 1_000L, 100_000L);   // µm, not mm (would be ~50) and not nm (~5e7)

        // The header declares the units (a file that does not say its scale is refused on read).
        Assert.Contains("P  UNITS CUST 2", PcbIpc356.Write(layout));
    }

    // ---- 7. scale invariance -------------------------------------------------

    [Fact]
    public void NetReconstructionIsScaleInvariant()
    {
        foreach (double s in new[] { 0.001, 1.0, 1000.0 })
        {
            var layout = BuildLayout(s);
            var parsed = PcbIpc356.Parse(PcbIpc356.Write(layout));
            // The partition (over names) is identical at every scale AND equals the board's own.
            Assert.Equal(ModelPartition(layout), Partition(parsed));
            // Coordinates still round-trip as an exact integer fixed point at every magnitude.
            var pts = PcbIpc356.Compute(layout);
            string text = PcbIpc356.Write(pts);
            Assert.Equal(text, PcbIpc356.Write(PcbIpc356.Parse(text)));
        }
    }

    // ---- 8. the access / layer convention -----------------------------------

    [Fact]
    public void AccessCodesFollowTheTopOneBottomTwoAllZeroConvention()
    {
        var layout = BuildLayout();   // 2-layer board
        var parsed = PcbIpc356.Parse(PcbIpc356.Write(layout));

        // Through-hole pads and the through via reach all layers → 0.
        Assert.All(parsed.Where(p => p.Feature == Ipc356Feature.ThroughHolePad), p => Assert.Equal(0, p.Access));
        Assert.All(parsed.Where(p => p.IsVia), p => Assert.Equal(0, p.Access));
        // R1 is SMD on the TOP layer → 1; U1 is SMD on the BOTTOM (layer 2 of 2) → 2.
        Assert.All(parsed.Where(p => p.RefDes == "R1"), p => Assert.Equal(1, p.Access));
        Assert.All(parsed.Where(p => p.RefDes == "U1"), p => Assert.Equal(2, p.Access));
    }

    [Fact]
    public void ABuriedViaTakesItsInnerLayersAccessNumberOnAMultilayerBoard()
    {
        // A 4-layer board: Top(0), In1(1), In2(2), Bottom(3).
        var stackup = PcbStackup.Layers(1.6, ("In1", 1.1), ("In2", 0.5));
        var board = PcbBoard.Rectangle(50, 40, 1.6, stackup);
        var sch = new Schematic("ipc-4layer");
        // A stub net so a single via can carry a valid (non-empty) net.
        var r = sch.Add("R1", Res2(1), "0");
        sch.Connect("SIG", r.Pin("1"), r.Pin("2"));
        var layout = new PcbLayout(sch, board);
        layout.Place("R1", 0, 0, 0, CopperSide.Top);
        layout.AddVia("SIG", 5, 5, "In1", "In2", drill: 0.3, pad: 0.6);   // buried inner→inner

        var parsed = PcbIpc356.Parse(PcbIpc356.Write(layout));
        var via = parsed.Single(p => p.IsVia);
        Assert.Equal(2, via.Access);   // top-most reached copper is In1 (layer number 2), not 0/all
    }

    // ---- 9. refusals by name (identities are refused, never sanitized) ------

    [Fact]
    public void AnIdentityTooLongOrCarryingWhitespaceIsRefusedByName()
    {
        // A net name over the 14-char field.
        var over = MakeSingleNetLayout("THIS_NET_NAME_IS_WAY_TOO_LONG");
        var ex1 = Assert.Throws<ArgumentException>(() => PcbIpc356.Write(over));
        Assert.Contains("net name", ex1.Message);

        // A net name carrying whitespace (a fixed-column record cannot spell it, and trimming would
        // change the identity).
        var space = MakeSingleNetLayout("A B");
        var ex2 = Assert.Throws<ArgumentException>(() => PcbIpc356.Write(space));
        Assert.Contains("whitespace", ex2.Message);
    }

    [Fact]
    public void ARealNetCollidingWithTheSyntheticNamespaceIsRefusedByName()
    {
        var collide = MakeSingleNetLayout("N/C-000001");
        var ex = Assert.Throws<ArgumentException>(() => PcbIpc356.Compute(collide));
        Assert.Contains("synthetic", ex.Message);
    }

    [Fact]
    public void ADrillBelowTheFileResolutionIsRefusedByName()
    {
        // A via drill of 0.4 µm rounds below the file's 1 µm quantum — it cannot be spelled as a
        // positive drill, so the writer refuses it rather than emit an unparseable D0.
        var sch = new Schematic("ipc-tiny");
        var r = sch.Add("R1", Res2(1), "0");
        sch.Connect("SIG", r.Pin("1"), r.Pin("2"));
        var layout = new PcbLayout(sch, PcbBoard.Rectangle(20, 10, 1.6));
        layout.Place("R1", 0, 0, 0, CopperSide.Top);
        layout.AddVia("SIG", 2, 2, "Top", "Bottom", drill: 0.0004, pad: 0.0008);

        var ex = Assert.Throws<ArgumentException>(() => PcbIpc356.Write(layout));
        Assert.Contains("resolution", ex.Message);
    }

    [Fact]
    public void ParseRefusesMalformedInputByName()
    {
        // Build the malformed cases by MUTATING real, correctly-aligned records the writer produced,
        // so a refusal fires for the reason under test rather than a column mis-parse.
        var lines = PcbIpc356.Write(BuildLayout()).Split('\n');
        string a317 = lines.First(l => l.StartsWith("317", StringComparison.Ordinal));
        string a327 = lines.First(l => l.StartsWith("327", StringComparison.Ordinal));
        const string units = "P  UNITS CUST 2\n";

        // No units record at all — the scale is unstated, refused.
        Assert.Throws<FormatException>(() => PcbIpc356.Parse(a317 + "\n999\n"));
        // A units code the writer never emits.
        Assert.Throws<FormatException>(() => PcbIpc356.Parse("P  UNITS CUST 0\n" + a317 + "\n999\n"));
        // An unknown operation code on an otherwise well-formed record.
        Assert.Throws<FormatException>(() => PcbIpc356.Parse(units + "378" + a317[3..] + "\n999\n"));
        // A record too short to carry its fixed identity columns.
        Assert.Throws<FormatException>(() => PcbIpc356.Parse(units + "317 short\n999\n"));
        // A drilled (317) record with its drill (D) token stripped.
        Assert.Throws<FormatException>(() =>
            PcbIpc356.Parse(units + a317[..a317.LastIndexOf(" D", StringComparison.Ordinal)] + "\n999\n"));
        // An SMD (327) record carrying a spurious drill (D) token.
        Assert.Throws<FormatException>(() => PcbIpc356.Parse(units + a327 + " D0900\n999\n"));
    }

    // ---- 10. empty board -----------------------------------------------------

    [Fact]
    public void AnEmptyBoardWritesAHeaderOnlyFileThatParsesToNothing()
    {
        var layout = new PcbLayout(new Schematic("empty"), PcbBoard.Rectangle(20, 10, 1.6));
        var pts = PcbIpc356.Compute(layout);
        Assert.Empty(pts);

        string text = PcbIpc356.Write(layout);
        Assert.Contains("P  UNITS CUST 2", text);
        Assert.Contains("999", text);
        Assert.Empty(PcbIpc356.Parse(text));
    }

    // ---- 11. the disk path writes a file that round-trips -------------------

    [Fact]
    public void WriteFileProducesAnIpcFileThatRoundTrips()
    {
        var layout = BuildLayout();
        string dir = Path.Combine(Path.GetTempPath(), "engrcad-ipc-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = PcbIpc356.WriteFile(layout, dir);
            Assert.True(File.Exists(path));
            Assert.EndsWith(".ipc", path);

            var parsed = PcbIpc356.Parse(File.ReadAllText(path));
            Assert.Equal(ModelPartition(layout), Partition(parsed));
            Assert.Equal(layout.PlacedPads().Count + layout.PlacedVias().Count, parsed.Count);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // ---- helpers ------------------------------------------------------------

    private static Dictionary<string, string?> NetOfPad(PcbLayout layout)
    {
        var model = PcbCopperModel.FromLayout(layout);
        var viaSources = new HashSet<string>(model.Vias.Select(v => v.Source), StringComparer.Ordinal);
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var f in model.Copper)
            if (!viaSources.Contains(f.Source) && !model.TraceSources.Contains(f.Source)
                && !model.PourSources.Contains(f.Source))
                map[f.Source] = f.Net;
        return map;
    }

    private static string Key(Ipc356AccessPoint p) =>
        p.IsVia ? $"VIA|{p.Net}|{p.Xum}|{p.Yum}" : p.PadName;

    // A one-resistor layout whose single wired net has a chosen name — for the identity-refusal tests.
    private static PcbLayout MakeSingleNetLayout(string netName)
    {
        var sch = new Schematic("ipc-refuse");
        var r = sch.Add("R1", Res2(1), "0");
        sch.Connect(netName, r.Pin("1"), r.Pin("2"));
        var layout = new PcbLayout(sch, PcbBoard.Rectangle(20, 10, 1.6));
        layout.Place("R1", 0, 0, 0, CopperSide.Top);
        return layout;
    }
}
