using EngrCAD.Ecad;
using Xunit;

namespace EngrCAD.Ecad.Tests;

public class IdfTests
{
    private const string SampleEmn = """
        .HEADER
        BOARD_FILE 3.0 "sample" 2020/06/01.10:00:00 1
        "demo board" MM
        .END_HEADER
        .BOARD_OUTLINE ECAD
        1.6
        0 0.0 0.0 0.0
        0 60.0 0.0 0.0
        0 60.0 40.0 0.0
        0 0.0 40.0 0.0
        0 0.0 0.0 0.0
        .END_BOARD_OUTLINE
        .DRILLED_HOLES
        3.0 5.0 5.0 PTH BOARD MTG ECAD
        3.0 55.0 35.0 PTH BOARD MTG ECAD
        0.4 30.0 20.0 PTH BOARD VIA ECAD
        .END_DRILLED_HOLES
        .PLACEMENT
        "R0805" "330R" R1
        20.0 15.0 0.0 0.0 TOP PLACED
        "SOT23" "BC847" Q1
        40.0 25.0 0.0 90.0 BOTTOM PLACED
        .END_PLACEMENT
        .VIA_KEEPOUT ECAD
        0 10.0 10.0 0.0
        0 15.0 10.0 0.0
        0 15.0 15.0 0.0
        0 10.0 15.0 0.0
        0 10.0 10.0 0.0
        .END_VIA_KEEPOUT
        """;

    // ---- import --------------------------------------------------------------

    [Fact]
    public void Read_ImportsTheOutlineHolesPlacementsAndKeepOut()
    {
        var import = IdfReader.Read(SampleEmn);

        Assert.Equal("demo board", import.BoardName);
        Assert.Equal(IdfUnits.Millimetres, import.Units);

        Assert.Equal(1.6, import.Board.Thickness);
        Assert.Equal(4, import.Board.OutlinePoints.Count);   // closing point dropped
        Assert.Equal(2400.0, import.Board.OutlineArea(), 6); // 60 × 40

        Assert.Equal(3, import.Board.Holes.Count);
        Assert.Equal(2, import.Board.Holes.Count(h => h.Kind == BoardHoleKind.Mounting));
        Assert.Single(import.Board.Holes, h => h.Kind == BoardHoleKind.Via);

        Assert.Equal(2, import.Placements.Count);
        var r1 = import.Placements.Single(p => p.Reference == "R1");
        Assert.Equal(20.0, r1.X);
        Assert.Equal(15.0, r1.Y);
        Assert.Equal(CopperSide.Top, r1.Side);
        var q1 = import.Placements.Single(p => p.Reference == "Q1");
        Assert.Equal(90.0, q1.RotationDegrees);
        Assert.Equal(CopperSide.Bottom, q1.Side);

        Assert.Single(import.Board.KeepOuts);
        Assert.Equal(KeepOutKind.Via, import.Board.KeepOuts[0].Kind);
        Assert.Equal(4, import.Board.KeepOuts[0].Polygon.Count);
    }

    [Fact]
    public void ToLayout_SynthesizesASchematicAndPlacesTheComponents()
    {
        var layout = IdfReader.Read(SampleEmn).ToLayout();

        Assert.Equal(2, layout.Placements.Count);
        Assert.NotNull(layout.Schematic.Find("R1"));
        Assert.NotNull(layout.Schematic.Find("Q1"));
        // The synthesized definitions are named by package, data-only (no footprint).
        Assert.Equal("R0805", layout.Schematic.Find("R1")!.Definition.Name);

        // Honest: IDF carries no connectivity, so the identity check reports the missing
        // footprints rather than pretending the pins resolve to copper.
        var report = layout.Check();
        Assert.False(report.Ok);
        Assert.Contains(report.MissingFootprints, s => s.StartsWith("R1"));
    }

    // ---- the round trip is a byte fixed point -------------------------------

    [Fact]
    public void ReadWriteReadWrite_IsAByteFixedPoint()
    {
        var import1 = IdfReader.Read(SampleEmn);
        string text1 = IdfWriter.Write(import1);
        var import2 = IdfReader.Read(text1);
        string text2 = IdfWriter.Write(import2);

        // Byte-identical from the first canonical write on.
        Assert.Equal(text1, text2);

        // And the geometry the file carries is preserved exactly.
        Assert.Equal(import1.Board.OutlinePoints, import2.Board.OutlinePoints);
        Assert.Equal(import1.Board.Holes, import2.Board.Holes);
        Assert.Equal(import1.Placements, import2.Placements);
        Assert.Equal(import1.Board.KeepOuts[0].Polygon, import2.Board.KeepOuts[0].Polygon);
    }

    // ---- units are honoured -------------------------------------------------

    [Fact]
    public void ThouUnits_AreScaledToMillimetres_AndRecordedInDiagnostics()
    {
        // A 2000 × 1000 mil board (50.8 × 25.4 mm), 62-mil thick (1.5748 mm).
        string thou = """
            .HEADER
            BOARD_FILE 3.0 "sample" 2020/06/01.10:00:00 1
            "imperial" THOU
            .END_HEADER
            .BOARD_OUTLINE ECAD
            62.0
            0 0.0 0.0 0.0
            0 2000.0 0.0 0.0
            0 2000.0 1000.0 0.0
            0 0.0 1000.0 0.0
            .END_BOARD_OUTLINE
            .PLACEMENT
            "R0805" "" R1
            1000.0 500.0 0.0 0.0 TOP PLACED
            .END_PLACEMENT
            """;
        var import = IdfReader.Read(thou);

        Assert.Equal(IdfUnits.Thou, import.Units);
        Assert.Equal(62.0 * 0.0254, import.Board.Thickness, 9);
        Assert.Equal(2000.0 * 1000.0 * 0.0254 * 0.0254, import.Board.OutlineArea(), 6);
        Assert.Equal(1000.0 * 0.0254, import.Placements[0].X, 9);
        Assert.Contains(import.Diagnostics, d => d.Contains("THOU") && d.Contains("0.0254"));
    }

    // ---- malformed structure refused by name --------------------------------

    [Fact]
    public void MissingHeader_IsRefusedByName()
    {
        string noHeader = """
            .BOARD_OUTLINE ECAD
            1.6
            0 0.0 0.0 0.0
            .END_BOARD_OUTLINE
            """;
        var ex = Assert.Throws<FormatException>(() => IdfReader.Read(noHeader));
        Assert.Contains("HEADER", ex.Message);
    }

    [Fact]
    public void UnclosedSection_IsRefusedByName()
    {
        string unclosed = """
            .HEADER
            BOARD_FILE 3.0 "x" 2020/01/01.00:00:00 1
            "b" MM
            .END_HEADER
            .BOARD_OUTLINE ECAD
            1.6
            0 0.0 0.0 0.0
            """;
        var ex = Assert.Throws<FormatException>(() => IdfReader.Read(unclosed));
        Assert.Contains("BOARD_OUTLINE", ex.Message);
        Assert.Contains("never closed", ex.Message);
    }

    [Fact]
    public void MismatchedEnd_IsRefusedByName()
    {
        string mismatched = """
            .HEADER
            BOARD_FILE 3.0 "x" 2020/01/01.00:00:00 1
            "b" MM
            .END_BOARD_OUTLINE
            """;
        Assert.Throws<FormatException>(() => IdfReader.Read(mismatched));
    }

    [Fact]
    public void UnknownUnit_IsRefusedByName()
    {
        string badUnit = SampleEmn.Replace("\"demo board\" MM", "\"demo board\" FURLONGS");
        var ex = Assert.Throws<FormatException>(() => IdfReader.Read(badUnit));
        Assert.Contains("FURLONGS", ex.Message);
    }

    [Fact]
    public void MalformedPlacement_IsRefusedByName()
    {
        // A placement head line with too few fields.
        string bad = SampleEmn.Replace("\"R0805\" \"330R\" R1", "\"R0805\" R1");
        Assert.Throws<FormatException>(() => IdfReader.Read(bad));
    }
}
