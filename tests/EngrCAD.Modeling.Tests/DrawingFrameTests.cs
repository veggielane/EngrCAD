using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The shared drawing frame — the paper, border and title block both the mechanical
/// <see cref="DrawingSheet"/> and the ECAD schematic sheet draw from. The two oracles are the
/// extraction's whole point: (1) the frame is ONE pure function, so two sheets given the same
/// options cannot disagree, and (2) the extraction is additive — a mechanical sheet's
/// furniture is exactly its frame's output, so existing drawings stay byte-identical.
/// </summary>
public class DrawingFrameTests
{
    private static TitleBlock FullTitle() => new()
    {
        Title = "MOUNTING BRACKET", DrawingNumber = "EC-1042", Material = "AL 6082-T6",
        Finish = "ANODISED", Author = "EngrCAD", Date = "2026-08-11", Revision = "B", Company = "ENGRCAD",
    };

    // ---- (1) the frame is one pure function ----

    [Fact]
    public void FrameComputeIsDeterministic()
    {
        var a = new DrawingFrame { Format = SheetFormat.A3, Title = FullTitle() };
        var b = new DrawingFrame { Format = SheetFormat.A3, Title = FullTitle() };
        AssertFrameEqual(a.Compute(), b.Compute());
    }

    /// <summary>
    /// THE shared-frame oracle: a mechanical sheet and a schematic sheet reconfigured to the
    /// SAME paper, the SAME title-block fields and the SAME frame options (layout and layer
    /// names) produce byte-identical border and title-block geometry — they cannot disagree
    /// because it is one function.
    /// </summary>
    [Fact]
    public void TwoSheetsWithTheSameFrameOptionsCannotDisagree()
    {
        var title = FullTitle();
        // The mechanical sheet's frame (engineering layout, mechanical layers) and a plain
        // frame (schematic layout, its own layers) — different looks by default.
        var mech = new DrawingSheet(SheetFormat.A4) { Title = title }.Frame();
        var other = new DrawingFrame
        {
            Format = SheetFormat.A4, Title = title,
            Layout = new SchematicTitleBlock(2.0), BorderLayer = "border", TitleBlockLayer = "titleblock",
        };

        // Reconfigure the mechanical frame to the OTHER's options. Now both carry identical
        // parameters, so the one function produces identical geometry.
        var reconfigured = mech with
        {
            Layout = new SchematicTitleBlock(2.0), BorderLayer = "border", TitleBlockLayer = "titleblock",
        };
        AssertFrameEqual(reconfigured.Compute(), other.Compute());

        // And the geometry genuinely CHANGED with the options (the two layouts differ), so the
        // equality above is not vacuous.
        Assert.NotEqual(mech.Compute().Texts.Count, reconfigured.Compute().Texts.Count);
    }

    // ---- (2) additive extraction: the sheet's furniture IS the frame's output ----

    /// <summary>The mechanical sheet draws its border and title block from the shared frame and
    /// nowhere else — so if the frame is right, the sheet is, and existing output cannot drift
    /// from the frame.</summary>
    [Fact]
    public void MechanicalSheetFurnitureIsExactlyTheFrame()
    {
        var scene = new Scene();
        scene.Add(new Part("plate", Shape.Box(60, 40, 12)));
        var sheet = DrawingSheet.StandardLayout(scene, SheetFormat.A3, ProjectionAngle.First);
        sheet.Title = FullTitle();

        var content = sheet.Compute();
        var frame = sheet.Frame().Compute();

        // Every border/title-block segment and title-block text in the sheet is the frame's,
        // in the frame's order.
        var furnitureLines = content.Lines
            .Where(l => l.Layer is SheetLayers.Border or SheetLayers.TitleBlock).ToList();
        Assert.Equal(frame.Lines, furnitureLines);
        var furnitureTexts = content.Texts.Where(t => t.Layer == SheetLayers.TitleBlock).ToList();
        Assert.Equal(frame.Texts, furnitureTexts);

        // The engineering block prints the sheet's own scale and projection angle.
        Assert.Contains(frame.Texts, t => t.Text == "FIRST ANGLE");
        Assert.Contains(frame.Texts, t => t.Text.StartsWith("SCALE "));
        Assert.Contains(frame.Texts, t => t.Text == "MOUNTING BRACKET");
    }

    /// <summary>The border is four segments (bottom, right, top, left) at the margin, on the
    /// given border layer — the geometry both sheets share.</summary>
    [Fact]
    public void BorderIsTheMarginRectangle()
    {
        var frame = new DrawingFrame { Format = SheetFormat.A4 };   // 297 x 210, margin 10
        var border = frame.Border;
        Assert.Equal(new Vector3d(10, 10, 0), border.Min);
        Assert.Equal(new Vector3d(287, 200, 0), border.Max);

        var lines = frame.Compute().Lines.Where(l => l.Layer == SheetLayers.Border).ToList();
        Assert.Equal(4, lines.Count);
        Assert.Contains(lines, l => l.A == new Vector2d(10, 10) && l.B == new Vector2d(287, 10));   // bottom
    }

    // ---- (3) standards: opt-in, closed-form, off by default is byte-identical ----

    [Fact]
    public void StandardsOffLeavesTheFrameByteIdentical()
    {
        var title = FullTitle();
        var plain = new DrawingFrame { Format = SheetFormat.A3, Title = title }.Compute();
        var explicitNone = new DrawingFrame
        {
            Format = SheetFormat.A3, Title = title, Standards = FrameStandards.None,
        }.Compute();
        AssertFrameEqual(plain, explicitNone);

        // Nothing on the border layer beyond the four border segments.
        Assert.Equal(4, plain.Lines.Count(l => l.Layer == SheetLayers.Border));
        Assert.DoesNotContain(plain.Texts, t => t.Layer == SheetLayers.Border);
    }

    /// <summary>The ISO 5457 zone grid puts a number in each of <c>cols</c> columns (top and
    /// bottom) and a letter in each of <c>rows</c> rows (both sides), with dividing lines and
    /// four centring marks — all closed-form counts, and all in the margin band (never in the
    /// drawing area).</summary>
    [Fact]
    public void Iso5457ZoneGridHasTheExpectedCountsAndCentringMarks()
    {
        var frame = new DrawingFrame { Format = SheetFormat.A3, Standards = FrameStandards.Iso5457 };
        var geom = frame.Compute();
        var border = frame.Border;   // (10,10)-(410,287)

        int cols = FrameStandards.Iso5457.Columns(border.Size.X);   // round(400/50) = 8
        int rows = FrameStandards.Iso5457.Rows(border.Size.Y);      // round(277/50) = 6
        Assert.Equal(8, cols);
        Assert.Equal(6, rows);

        // Column numbers appear in BOTH the top and bottom bands; row letters in BOTH side
        // bands — 2*cols + 2*rows label texts, all on the border layer.
        var labels = geom.Texts.Where(t => t.Layer == SheetLayers.Border).ToList();
        Assert.Equal(2 * cols + 2 * rows, labels.Count);
        Assert.Equal(2, labels.Count(t => t.Text == "1"));     // column 1, top and bottom
        Assert.Equal(2, labels.Count(t => t.Text == "A"));     // top row letter, both sides
        Assert.Equal(2, labels.Count(t => t.Text == "8"));     // last column

        // ISO 5457 omits I and O — a 6-row sheet letters A B C D E F (never touching I/O here,
        // but the sequence is asserted so the skip cannot silently rot on a taller sheet).
        Assert.Contains(labels, t => t.Text == "F");
        Assert.DoesNotContain(labels, t => t.Text == "I");

        // Four centring marks, at the middle of each side, crossing the border into the margin.
        double cx = (border.Min.X + border.Max.X) / 2;   // 210
        double cy = (border.Min.Y + border.Max.Y) / 2;   // 148.5
        var borderLines = geom.Lines.Where(l => l.Layer == SheetLayers.Border).ToList();
        Assert.Contains(borderLines, l =>
            l.A == new Vector2d(cx, SheetFormat.A3.Height) && l.B == new Vector2d(cx, border.Max.Y - 5));
        Assert.Contains(borderLines, l =>
            l.A == new Vector2d(0, cy) && l.B == new Vector2d(border.Min.X + 5, cy));

        // Every added border-layer segment and label stays outside the drawing area (in the
        // margin) or is a centring mark reaching only 5 mm in — nothing crosses the block area.
        foreach (var t in labels)
        {
            bool inMarginBand =
                t.Position.X < border.Min.X || t.Position.X > border.Max.X ||
                t.Position.Y < border.Min.Y || t.Position.Y > border.Max.Y;
            Assert.True(inMarginBand, $"zone label {t.Text} at {t.Position} is inside the border");
        }
    }

    // ---- (4) the paper-size table ----

    [Fact]
    public void PaperTableCarriesAIsoBAndAnsiSizesLandscape()
    {
        // ISO 216 A and B and ANSI A-E, all landscape (width > height), in one table.
        Assert.Contains(SheetFormat.A0, SheetFormat.All);
        Assert.Contains(SheetFormat.B4, SheetFormat.All);
        Assert.Contains(SheetFormat.AnsiE, SheetFormat.All);
        Assert.All(SheetFormat.All, f => Assert.True(f.Width >= f.Height, $"{f.Name} is not landscape"));

        // ANSI millimetres are exact (25.4 mm/in): ANSI B is 17 x 11 in.
        Assert.Equal(17 * 25.4, SheetFormat.AnsiB.Width, 9);
        Assert.Equal(11 * 25.4, SheetFormat.AnsiB.Height, 9);
        // ISO B4 is 250 x 353 mm; landscape turns it over.
        Assert.Equal(353, SheetFormat.B4.Width);
        Assert.Equal(250, SheetFormat.B4.Height);

        // A frame on any of them still frames itself (border inside the paper).
        foreach (var format in SheetFormat.All)
        {
            var border = new DrawingFrame { Format = format }.Border;
            Assert.True(border.Min.X > 0 && border.Max.X < format.Width);
            Assert.True(border.Min.Y > 0 && border.Max.Y < format.Height);
        }
    }

    // ---- helper ----

    private static void AssertFrameEqual(FrameGeometry a, FrameGeometry b)
    {
        Assert.Equal(a.Lines, b.Lines);
        Assert.Equal(a.Texts, b.Texts);
    }
}
