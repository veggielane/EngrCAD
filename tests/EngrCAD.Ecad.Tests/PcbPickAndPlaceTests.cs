using EngrCAD.Core;
using EngrCAD.Ecad;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The assembly pick-and-place (centroid) export. ECAD fails plausibly, so the bar is closed forms and
/// identities, not eyeballing: the P&amp;P is a PROJECTION of the placements, so every field is exact by
/// construction (test 1); the bottom-side rotation is a mirror — a sign swap, never a <c>cos</c> — with
/// its exact value asserted (tests 2, 4); and the CSV survives the TWIN-DECODER round trip (test 5).
/// </summary>
public sealed class PcbPickAndPlaceTests
{
    // ---- fixtures -----------------------------------------------------------

    // A two-terminal SMD part whose footprint name is its package identifier.
    private static PartDefinition Res(string package) => new(
        "R_" + package, "R",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
        new Footprint(package,
            [Pad.Smd("1", new Vector2d(-0.5, 0), 0.6, 0.6), Pad.Smd("2", new Vector2d(0.5, 0), 0.6, 0.6)]));

    // A small board every fixture places onto.
    private static PcbBoard Board() => PcbBoard.Rectangle(40, 30, 1.6);

    // ---- 1. pose identity: the row IS the placement -------------------------

    [Fact]
    public void EachRowIsExactlyItsPlacement()
    {
        var sch = new Schematic("pnp-top");
        sch.Add("R1", Res("0805"), "330");
        sch.Add("R2", Res("0603"), "10k");
        sch.Add("U1", Res("SOIC8"));   // empty value
        var layout = new PcbLayout(sch, Board());
        layout.Place("R1", 3.5, -2.25, 90);
        layout.Place("R2", -7.0, 4.0, 180);
        layout.Place("U1", 12.25, 6.5, 270);   // all top-side

        var rows = PcbPickAndPlace.Compute(layout);

        Assert.Equal(layout.Placements.Count, rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var p = layout.Placements[i];
            var row = rows[i];
            // A pure projection: designator, X, Y, side and (for a top part) rotation are EXACT.
            Assert.Equal(p.Reference, row.Designator);
            Assert.Equal(p.X, row.X);
            Assert.Equal(p.Y, row.Y);
            Assert.Equal(p.RotationDegrees, row.Rotation);   // top → verbatim, exact
            Assert.Equal(p.Side, row.Side);
            Assert.Equal(sch.Find(p.Reference)!.Value, row.Value);
        }

        // Package = the footprint name; value = the component value (empty for U1).
        Assert.Equal("0805", rows[0].Package);
        Assert.Equal("330", rows[0].Value);
        Assert.Equal("", rows[2].Value);
    }

    // ---- 2. quarter-turn exactness: a sign swap, never a cos ----------------

    [Fact]
    public void QuarterTurnsAreExactTopAndBottom()
    {
        var sch = new Schematic("pnp-turns");
        foreach (var r in new[] { "R0", "R90", "R180", "R270" })
            sch.Add(r, Res("0805"));
        var layout = new PcbLayout(sch, Board());
        // Top verbatim; bottom mirrored (360 − rot) mod 360.
        layout.Place("R0", 0, 0, 0);
        layout.Place("R90", 1, 0, 90);
        layout.Place("R180", 2, 0, 180);
        layout.Place("R270", 3, 0, 270);

        var top = PcbPickAndPlace.Compute(layout);
        Assert.Equal(0.0, top[0].Rotation);
        Assert.Equal(90.0, top[1].Rotation);
        Assert.Equal(180.0, top[2].Rotation);
        Assert.Equal(270.0, top[3].Rotation);

        var bsch = new Schematic("pnp-turns-b");
        foreach (var r in new[] { "R0", "R90", "R180", "R270" })
            bsch.Add(r, Res("0805"));
        var blayout = new PcbLayout(bsch, Board());
        blayout.Place("R0", 0, 0, 0, CopperSide.Bottom);
        blayout.Place("R90", 1, 0, 90, CopperSide.Bottom);
        blayout.Place("R180", 2, 0, 180, CopperSide.Bottom);
        blayout.Place("R270", 3, 0, 270, CopperSide.Bottom);

        var bottom = PcbPickAndPlace.Compute(blayout);
        Assert.Equal(0.0, bottom[0].Rotation);     // 360-0   → 0
        Assert.Equal(270.0, bottom[1].Rotation);   // 360-90  → 270
        Assert.Equal(180.0, bottom[2].Rotation);   // 360-180 → 180
        Assert.Equal(90.0, bottom[3].Rotation);    // 360-270 → 90
    }

    // ---- 3. every placed component appears exactly once ---------------------

    [Fact]
    public void EveryPlacementAppearsExactlyOnce()
    {
        var sch = new Schematic("pnp-count");
        foreach (var r in new[] { "R1", "R2", "R3", "C1" })
            sch.Add(r, Res("0402"));
        var layout = new PcbLayout(sch, Board());
        layout.Place("R1", 1, 1);
        layout.Place("C1", 2, 2);
        layout.Place("R3", 3, 3);   // deliberately not in schematic-add order

        var rows = PcbPickAndPlace.Compute(layout);

        Assert.Equal(layout.Placements.Count, rows.Count);
        // The refdes are exactly the layout's own placements (a set, once each), in placement order.
        Assert.Equal(
            layout.Placements.Select(p => p.Reference).ToArray(),
            rows.Select(r => r.Designator).ToArray());
        Assert.Equal(rows.Count, rows.Select(r => r.Designator).Distinct().Count());
    }

    // ---- 4. side split + the mirrored bottom rotation -----------------------

    [Fact]
    public void SidesSplitAndBottomIsMirrored()
    {
        var sch = new Schematic("pnp-sides");
        sch.Add("R1", Res("0805"));
        sch.Add("R2", Res("0805"));
        var layout = new PcbLayout(sch, Board());
        layout.Place("R1", 1, 2, 90, CopperSide.Top);
        layout.Place("R2", 3, 4, 90, CopperSide.Bottom);

        var rows = PcbPickAndPlace.Compute(layout);
        var top = rows.Single(r => r.Designator == "R1");
        var bot = rows.Single(r => r.Designator == "R2");

        Assert.Equal(CopperSide.Top, top.Side);
        Assert.Equal(90.0, top.Rotation);            // top verbatim
        Assert.Equal(CopperSide.Bottom, bot.Side);
        Assert.Equal(270.0, bot.Rotation);           // mirror of 90

        // The CSV Side column reads the side text.
        var csv = PcbPickAndPlace.ToCsv(layout);
        var back = PcbPickAndPlace.ParseCsv(csv);
        Assert.Equal(CopperSide.Top, back.Single(r => r.Designator == "R1").Side);
        Assert.Equal(CopperSide.Bottom, back.Single(r => r.Designator == "R2").Side);
        Assert.Contains(",top,", csv);
        Assert.Contains(",bottom,", csv);
    }

    // ---- 5. the twin-decoder round trip -------------------------------------

    [Fact]
    public void CsvRoundTripsExactly()
    {
        var sch = new Schematic("pnp-round");
        sch.Add("R1", Res("0805"), "330");
        sch.Add("R2", Res("0603"), "10k, 5%");   // a comma in the value — CSV must quote it
        sch.Add("R3", Res("0402"), "1/4\"");     // a quote in the value
        var layout = new PcbLayout(sch, Board());
        layout.Place("R1", 3.5, -2.25, 90);
        layout.Place("R2", -7.125, 4.0625, 45, CopperSide.Bottom);
        layout.Place("R3", 12.25, 6.5, 270);

        var rows = PcbPickAndPlace.Compute(layout);
        var csv = PcbPickAndPlace.ToCsv(layout);
        var back = PcbPickAndPlace.ParseCsv(csv);

        Assert.Equal(rows.Count, back.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(rows[i].Designator, back[i].Designator);
            Assert.Equal(rows[i].X, back[i].X);            // exact, to the file's precision
            Assert.Equal(rows[i].Y, back[i].Y);
            Assert.Equal(rows[i].Rotation, back[i].Rotation);
            Assert.Equal(rows[i].Side, back[i].Side);
            Assert.Equal(rows[i].Value, back[i].Value);    // comma/quote survived the escaping
        }
    }

    // ---- 6. determinism -----------------------------------------------------

    [Fact]
    public void EmissionsAreByteIdentical()
    {
        var sch = new Schematic("pnp-det");
        sch.Add("R1", Res("0805"), "330");
        sch.Add("R2", Res("0603"), "10k");
        var layout = new PcbLayout(sch, Board());
        layout.Place("R1", 1.5, 2.5, 90);
        layout.Place("R2", -3.5, 4.5, 180, CopperSide.Bottom);

        Assert.Equal(PcbPickAndPlace.ToCsv(layout), PcbPickAndPlace.ToCsv(layout));
        Assert.Equal(PcbPickAndPlace.ToPos(layout), PcbPickAndPlace.ToPos(layout));

        // One Compute feeds both writers, so both carry the same rows in the same order.
        var pos = PcbPickAndPlace.ToPos(layout);
        Assert.True(pos.IndexOf("R1", StringComparison.Ordinal) < pos.IndexOf("R2", StringComparison.Ordinal));
        Assert.Contains("R1", pos);
        Assert.Contains("R2", pos);
    }

    // ---- 7. empty / no components -------------------------------------------

    [Fact]
    public void NoPlacementsWritesAHeaderOnlyFile()
    {
        var sch = new Schematic("pnp-empty");
        var layout = new PcbLayout(sch, Board());

        var rows = PcbPickAndPlace.Compute(layout);
        Assert.Empty(rows);

        var csv = PcbPickAndPlace.ToCsv(layout);
        Assert.Equal("Designator,X,Y,Rotation,Side,Value\n", csv);

        // The header-only file parses back to no rows (no throw).
        Assert.Empty(PcbPickAndPlace.ParseCsv(csv));

        // The .pos still writes a valid (header-only) file.
        var pos = PcbPickAndPlace.ToPos(layout);
        Assert.Contains("Ref", pos);
        Assert.Contains("Package", pos);
    }

    // ---- package fallback ---------------------------------------------------

    [Fact]
    public void PackageFallsBackToTheDefinitionNameWhenNoFootprint()
    {
        var sch = new Schematic("pnp-nofp");
        // A definition with NO footprint — its Package is the definition's type name.
        sch.Add("U1", new PartDefinition("ATmega328P", "U", [new Pin("1", PinType.Passive)]));
        sch.Add("R1", Res("0805"), "330");
        var layout = new PcbLayout(sch, Board());
        layout.Place("U1", 0, 0);
        layout.Place("R1", 5, 0);

        var rows = PcbPickAndPlace.Compute(layout);
        Assert.Equal("ATmega328P", rows.Single(r => r.Designator == "U1").Package);
        Assert.Equal("0805", rows.Single(r => r.Designator == "R1").Package);
    }

    // ---- the reader refuses malformed input BY NAME -------------------------

    [Fact]
    public void ParseRefusesMalformedInputByName()
    {
        const string header = "Designator,X,Y,Rotation,Side,Value\n";
        Assert.Throws<FormatException>(() => PcbPickAndPlace.ParseCsv(""));                       // no header
        Assert.Throws<FormatException>(() => PcbPickAndPlace.ParseCsv("Wrong,Header\nA,B\n"));    // wrong header
        Assert.Throws<FormatException>(() => PcbPickAndPlace.ParseCsv(header + "R1,1,2,3,top\n"));       // 5 fields
        Assert.Throws<FormatException>(() => PcbPickAndPlace.ParseCsv(header + "R1,x,2,3,top,330\n"));   // bad number
        Assert.Throws<FormatException>(() => PcbPickAndPlace.ParseCsv(header + "R1,1,2,3,edge,330\n"));  // bad side
        Assert.Throws<FormatException>(() => PcbPickAndPlace.ParseCsv(header + "\"unterminated"));       // open quote
    }

    // ---- the disk path writes both files and round-trips --------------------

    [Fact]
    public void WriteProducesBothFilesAndTheCsvRoundTrips()
    {
        var sch = new Schematic("pnp-write");
        sch.Add("R1", Res("0805"), "330");
        sch.Add("R2", Res("0603"), "10k");
        var layout = new PcbLayout(sch, Board());
        layout.Place("R1", 1, 2, 90);
        layout.Place("R2", 3, 4, 180, CopperSide.Bottom);

        string dir = Path.Combine(Path.GetTempPath(), "engrcad-pnp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = PcbPickAndPlace.Write(layout, dir);
            Assert.Equal(2, files.Count);
            Assert.All(files, f => Assert.True(File.Exists(f)));

            var csv = File.ReadAllText(files.Single(f => f.EndsWith("-pos.csv", StringComparison.Ordinal)));
            var back = PcbPickAndPlace.ParseCsv(csv);
            Assert.Equal(layout.Placements.Count, back.Count);
            Assert.Equal("R1", back[0].Designator);
            Assert.Equal(180.0, back[1].Rotation);   // R2 bottom, mirror of 180 → (360−180) mod 360 = 180
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
