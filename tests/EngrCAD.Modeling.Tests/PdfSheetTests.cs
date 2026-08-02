using System.Globalization;
using System.Text;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The PDF sheet writer, verified the way the OFF and LZW writers are: a written file is
/// re-read by an independently written parser (<see cref="PdfReadback"/>) and the
/// reconstruction compared against the writer's input — a structural check alone is the
/// recorded trap. The byte fixed point is the other half: a PDF's natural metadata
/// (CreationDate, /ID) is exactly what breaks it, so its absence is pinned, not assumed.
/// </summary>
public class PdfSheetTests
{
    // ------------------------------------------------------------------- fixtures

    private static DrawingSheet TitleSheet()
    {
        var sheet = new DrawingSheet(SheetFormat.A4);
        sheet.Title = sheet.Title with
        {
            Title = "PDF FIXTURE",
            DrawingNumber = "EC-9001",
            Author = "ENGRCAD",
        };
        return sheet;
    }

    private static Part Plate() => new("plate", Shape.Box(60, 40, 12)
        .Drill(HoleSpec.Simple(10), [new Vector2d(0, 0)], depth: 14,
            SketchPlane.At((0, 0, 6), Vector3d.UnitX, Vector3d.UnitY)));

    /// <summary>A front view of a top-drilled plate: the bore is behind material, so the
    /// sheet is guaranteed to carry HIDDEN runs, plus one dimension for the furniture
    /// layer.</summary>
    private static DrawingSheet PlateSheet()
    {
        var sheet = new DrawingSheet(SheetFormat.A4);
        var front = new DrawingView(Plate(), StandardViews.DirectionFor("front")!.Value, "FRONT")
        {
            Scale = 1,
            Center = new Vector2d(140, 120),
        };
        front.Annotate(SheetLinearDimension.Horizontal((-30, -6), (30, -6), -12));
        sheet.Add(front);
        sheet.Title = sheet.Title with { Title = "PLATE", DrawingNumber = "EC-9002" };
        return sheet;
    }

    /// <summary>A section view through a counterbored housing: guaranteed CUT runs and
    /// hatch, plus a diameter dimension so the drafting text carries the diameter
    /// sign.</summary>
    private static DrawingSheet SectionSheet()
    {
        var top = SketchPlane.At((0, 0, 9), Vector3d.UnitX, Vector3d.UnitY);
        var housing = Shape.Cylinder(30, 18).Translate(0, 0, 9)
            .Drill(HoleSpec.Counterbore(8, 15, 6), [new Vector2d(0, 0)], depth: 20, top);
        var part = new Part("housing", housing);

        var sheet = new DrawingSheet(SheetFormat.A4);
        var section = new DrawingView(part, StandardViews.DirectionFor("front")!.Value, "SECTION A-A")
        {
            Scale = 1,
            Center = new Vector2d(148, 105),
            SectionThrough = new Vector3d(0, 0, 0),
        };
        section.Annotate(SheetRadialDimension.Diameter((0, 18), 4, 60));
        sheet.Add(section);
        return sheet;
    }

    /// <summary>Exact comparison, deliberately: "R" formatting is a bijection on finite
    /// doubles, so a coordinate must come back BIT-identical — a tolerance here would
    /// hide a formatting regression.</summary>
    private static bool SamePolyline(IReadOnlyList<Vector2d> a, IReadOnlyList<Vector2d> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].X.Equals(b[i].X) || !a[i].Y.Equals(b[i].Y))
                return false;
        }
        return true;
    }

    private static PdfReadback.Stroke StrokeContaining(
        PdfReadback.Document doc, IReadOnlyList<Vector2d> polyline) =>
        doc.Strokes.First(s => s.Subpaths.Any(sp => SamePolyline(sp.Points, polyline)));

    private static double[] DashOf(SvgLineClass lineClass) =>
        (SvgDrawing.SvgPen.For(lineClass).DashArray ?? "")
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(v => double.Parse(v, CultureInfo.InvariantCulture))
        .ToArray();

    // ------------------------------------------------------------ the byte fixed point

    [Fact]
    public void WritingTheSameSheet_IsAByteFixedPoint_WithNoTimestampAndNoId()
    {
        var sheet = TitleSheet();
        byte[] first = sheet.ToPdf();
        Assert.Equal(first, sheet.ToPdf());

        // A rebuilt identical sheet writes identical bytes: the file is a function of
        // the model, not of the session.
        Assert.Equal(first, TitleSheet().ToPdf());

        string text = Encoding.ASCII.GetString(first);
        Assert.StartsWith("%PDF-1.4\n", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        // The metadata that would break the fixed point is ABSENT by design (both are
        // optional per the spec), pinned here so it cannot creep back in.
        Assert.DoesNotContain("/CreationDate", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Info", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/ID", text, StringComparison.Ordinal);
    }

    // --------------------------------------------------------- structure and transform

    [Fact]
    public void MediaBoxIsThePaperInPoints_AndTheOneTransformIsMillimetresToPoints()
    {
        var doc = PdfReadback.Parse(TitleSheet().ToPdf());
        double k = PdfDrawing.PointsPerMillimetre;

        // Exact: the writer emits "R"-formatted products of the same doubles.
        Assert.Equal([0, 0, 297 * k, 210 * k], doc.MediaBox);

        // Every stroke was drawn under the ONE mm-to-points matrix — no flip anywhere,
        // because PDF's origin is already the sheet's bottom-left.
        Assert.NotEmpty(doc.Strokes);
        Assert.All(doc.Strokes, s => Assert.Equal([k, 0, 0, k, 0, 0], s.Ctm));
    }

    [Fact]
    public void FitToContentPagePlacesItsOriginAtTheContentCorner()
    {
        var pdf = new PdfDrawing { Margin = 5 };
        pdf.AddPolyline([new Vector2d(0, 0), new Vector2d(10, 10)]);
        var doc = PdfReadback.Parse(pdf.ToPdf());
        double k = PdfDrawing.PointsPerMillimetre;

        Assert.Equal([0, 0, 20 * k, 20 * k], doc.MediaBox);
        // Content is shifted by the margin: (0,0) in the model lands 5 mm into the page.
        var stroke = Assert.Single(doc.Strokes);
        Assert.Equal([k, 0, 0, k, 5 * k, 5 * k], stroke.Ctm);
    }

    [Fact]
    public void AnEmptyDrawingRefuses() =>
        Assert.Throws<InvalidOperationException>(() => new PdfDrawing().ToPdf());

    // ------------------------------------------------------------------- line work

    [Fact]
    public void EveryRunHatchAndFurnitureSegmentRoundTripsBitExactly()
    {
        var sheet = PlateSheet();
        var content = sheet.Compute();
        var doc = PdfReadback.Parse(sheet.ToPdf());

        var expected = content.Runs
            .Where(r => r.Points.Count >= 2)
            .Select(r => r.Points)
            .Concat(content.Hatch.Select(h => (IReadOnlyList<Vector2d>)new[] { h.A, h.B }))
            .Concat(content.Lines.Select(l => (IReadOnlyList<Vector2d>)new[] { l.A, l.B }))
            .ToList();
        var parsed = doc.Strokes.SelectMany(s => s.Subpaths).Select(sp => sp.Points).ToList();

        Assert.Equal(expected.Count, parsed.Count);
        foreach (var polyline in expected)
            Assert.Contains(parsed, p => SamePolyline(p, polyline));
    }

    [Fact]
    public void HiddenAndSectionRunsCarryTheirClassDashArrays()
    {
        // The fixtures must still CARRY the configurations they exist to test.
        var plate = PlateSheet();
        var plateContent = plate.Compute();
        var hidden = plateContent.Runs.First(r => r.Visibility == EdgeVisibility.Hidden);
        var visible = plateContent.Runs.First(
            r => r.Visibility == EdgeVisibility.Visible && r.Source != EdgeSource.Cut);

        var plateDoc = PdfReadback.Parse(plate.ToPdf());
        var hiddenStroke = StrokeContaining(plateDoc, hidden.Points);
        Assert.Equal(DashOf(SvgLineClass.Hidden), hiddenStroke.Dash);
        Assert.Equal(SvgDrawing.SvgPen.For(SvgLineClass.Hidden).Width, hiddenStroke.LineWidth);
        Assert.Equal(0x66 / 255.0, hiddenStroke.Color.R);   // the hidden pen's #666666

        var visibleStroke = StrokeContaining(plateDoc, visible.Points);
        Assert.Empty(visibleStroke.Dash);

        var sectionSheet = SectionSheet();
        var sectionContent = sectionSheet.Compute();
        Assert.NotEmpty(sectionContent.Hatch);
        var cut = sectionContent.Runs.First(
            r => r.Source == EdgeSource.Cut && r.Visibility == EdgeVisibility.Visible);

        // A cut BOUNDARY is drawn solid at visible weight — the same rule the SVG writer
        // applies (the docs table: cut boundary is solid; the DXF layer, not the pen,
        // carries the distinction). The PDF mirrors the SVG's appearance exactly.
        var sectionDoc = PdfReadback.Parse(sectionSheet.ToPdf());
        var cutStroke = StrokeContaining(sectionDoc, cut.Points);
        Assert.Empty(cutStroke.Dash);
        Assert.Equal(SvgDrawing.SvgPen.For(SvgLineClass.Visible).Width, cutStroke.LineWidth);
    }

    /// <summary>The Section (dash-dot) class itself, exercised at the PdfDrawing level —
    /// the sheet path never reaches it (cut boundaries are solid), but the class must
    /// still spell its pattern correctly for callers who use it.</summary>
    [Fact]
    public void TheSectionClassCarriesItsDashDotPattern()
    {
        var pdf = new PdfDrawing();
        pdf.AddPolyline([new Vector2d(0, 0), new Vector2d(50, 0)], lineClass: SvgLineClass.Section);
        var doc = PdfReadback.Parse(pdf.ToPdf());
        Assert.Equal(DashOf(SvgLineClass.Section), Assert.Single(doc.Strokes).Dash);
    }

    // ------------------------------------------------------------------------ text

    [Fact]
    public void SheetTextArrivesDecodedAtItsStatedSize()
    {
        var doc = PdfReadback.Parse(TitleSheet().ToPdf());

        var title = doc.Texts.Single(t => t.Value == "PDF FIXTURE");
        Assert.Equal(SheetLettering.TitleHeight / SvgDrawing.CapHeightRatio, title.FontSize, 12);

        // The sheet's own facts (scale, projection) travel too.
        Assert.Contains(doc.Texts, t => t.Value == "SCALE 1:1");
        Assert.Contains(doc.Texts, t => t.Value == "THIRD ANGLE");
    }

    [Fact]
    public void AnchoredTextShiftsByTheMeasuredHelveticaAdvance()
    {
        var pdf = new PdfDrawing();
        pdf.AddText(new Vector2d(10, 20), "HH", 3.5);
        pdf.AddText(new Vector2d(50, 20), "HH", 3.5, SheetTextAnchor.Right);
        pdf.AddText(new Vector2d(50, 40), "HH", 3.5, SheetTextAnchor.Center);
        var doc = PdfReadback.Parse(pdf.ToPdf());

        double size = 3.5 / SvgDrawing.CapHeightRatio;
        double width = 2 * (722 / 1000.0) * size;   // 'H' is 722/1000 em in the Helvetica AFM
        Assert.Equal(10, doc.Texts[0].Position.X, 12);
        Assert.Equal(50 - width, doc.Texts[1].Position.X, 12);
        Assert.Equal(50 - width / 2, doc.Texts[2].Position.X, 12);
        Assert.All(doc.Texts, t => Assert.Equal(size, t.FontSize, 12));
    }

    [Fact]
    public void TheDiameterSignBecomesOStroke_AndOtherNonWinAnsiTextRefusesByName()
    {
        var pdf = new PdfDrawing();
        pdf.AddText(new Vector2d(0, 0), PdfDrawing.DiameterSign + "8", 3.5);
        var doc = PdfReadback.Parse(pdf.ToPdf());
        // The one deliberate substitution: U+2300 has no WinAnsi form; O-stroke (U+00D8)
        // is the drafting fallback and Helvetica carries it.
        Assert.Equal(((char)0xD8) + "8", Assert.Single(doc.Texts).Value);

        // Anything else outside WinAnsi refuses AT THE ADD, naming the character —
        // never a silent '?' in a manufacturer's drawing.
        char counterbore = (char)0x2334;
        var refusal = Assert.Throws<NotSupportedException>(
            () => pdf.AddText(new Vector2d(0, 0), counterbore.ToString(), 3.5));
        Assert.Contains("U+2334", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASheetDiameterDimensionSurvivesIntoThePdf()
    {
        var doc = PdfReadback.Parse(SectionSheet().ToPdf());
        // SheetRadialDimension.Diameter letters with U+2300, which the writer carries
        // as O-stroke — the substitution reaching the file through the real sheet path.
        Assert.Contains(doc.Texts, t => t.Value.Contains((char)0xD8));
    }
}
