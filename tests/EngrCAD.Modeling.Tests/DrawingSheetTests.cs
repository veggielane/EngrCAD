using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The drawing sheet: paper, layout, section hatching and the two writers. The
/// assertions are about SHEET COORDINATES and structure — where a view lands, what a
/// layer contains — rather than about pixels, because a drawing is a document and not a
/// picture.
/// </summary>
public class DrawingSheetTests
{
    private static Part Plate() => new("plate", Shape.Box(60, 40, 12)
        .Drill(HoleSpec.Simple(10), [new Vector2d(0, 0)], depth: 14,
            SketchPlane.At((0, 0, 6), Vector3d.UnitX, Vector3d.UnitY)));

    private static Scene SceneOf(params Part[] parts)
    {
        var scene = new Scene();
        foreach (var part in parts)
            scene.Add(part);
        return scene;
    }

    // ---- paper and scale ----

    [Fact]
    public void SheetFormatsAreLandscapeAndTurnOver()
    {
        Assert.Equal(420, SheetFormat.A3.Width);
        Assert.Equal(297, SheetFormat.A3.Height);
        Assert.Equal(297, SheetFormat.A3.Portrait.Width);
        Assert.Equal(420, SheetFormat.A3.Portrait.Height);
        // Landscape/Portrait are idempotent, so a caller can assert an orientation
        // without knowing which way the format started.
        Assert.Equal(SheetFormat.A3.Portrait, SheetFormat.A3.Portrait.Portrait);
        Assert.Equal(SheetFormat.A3, SheetFormat.A3.Landscape);
    }

    [Theory]
    [InlineData(1.7, 1)]
    [InlineData(0.9, 0.5)]
    [InlineData(0.21, 0.2)]
    [InlineData(7, 5)]
    [InlineData(1e-9, 1 / 1000.0)]
    public void FitTakesTheLargestStandardScaleThatFits(double maximum, double expected) =>
        Assert.Equal(expected, DrawingScales.Fit(maximum), 12);

    [Theory]
    [InlineData(1, "1:1")]
    [InlineData(0.5, "1:2")]
    [InlineData(0.1, "1:10")]
    [InlineData(2, "2:1")]
    public void ScalesFormatAsDrawingRatios(double ratio, string expected) =>
        Assert.Equal(expected, DrawingScales.Format(ratio));

    // ---- layout ----

    /// <summary>
    /// Third angle puts the top view ABOVE the front and the right view to the RIGHT;
    /// first angle mirrors both. Getting this backwards is the single most consequential
    /// mistake a drawing can make, so it is asserted as a relation and not a number.
    /// </summary>
    [Fact]
    public void ThirdAndFirstAngleMirrorEachOther()
    {
        var scene = SceneOf(Plate());
        var third = DrawingSheet.StandardLayout(scene, SheetFormat.A3, ProjectionAngle.Third);
        var first = DrawingSheet.StandardLayout(scene, SheetFormat.A3, ProjectionAngle.First);

        var (tf, tt, tr) = (third.Views[0], third.Views[1], third.Views[2]);
        Assert.True(tt.Center.Y > tf.Center.Y, "third angle: top view above the front");
        Assert.True(tr.Center.X > tf.Center.X, "third angle: right view to the right");
        Assert.Equal(tf.Center.X, tt.Center.X, 9);   // vertically aligned
        Assert.Equal(tf.Center.Y, tr.Center.Y, 9);   // horizontally aligned

        var (ff, ft, fr) = (first.Views[0], first.Views[1], first.Views[2]);
        Assert.True(ft.Center.Y < ff.Center.Y, "first angle: top view below the front");
        Assert.True(fr.Center.X < ff.Center.X, "first angle: right view to the left");
    }

    [Fact]
    public void StandardLayoutSharesOneScaleAndFitsTheDrawingArea()
    {
        var sheet = DrawingSheet.StandardLayout(SceneOf(Plate()), SheetFormat.A4);
        double scale = sheet.Views[0].Scale;
        Assert.All(sheet.Views, view => Assert.Equal(scale, view.Scale, 12));
        Assert.Contains(scale, DrawingScales.Standard);

        var area = sheet.DrawingArea;
        foreach (var view in sheet.Views)
        {
            var bounds = view.Compute().Bounds;
            Assert.InRange(bounds.Min.X, area.Min.X, area.Max.X);
            Assert.InRange(bounds.Max.X, area.Min.X, area.Max.X);
            Assert.InRange(bounds.Min.Y, area.Min.Y, area.Max.Y);
            Assert.InRange(bounds.Max.Y, area.Min.Y, area.Max.Y);
        }
    }

    /// <summary>A view's size on the sheet is its model extent times the scale — the
    /// property every layout calculation rests on.</summary>
    [Fact]
    public void ViewSizeIsModelExtentTimesScale()
    {
        var view = new DrawingView(Plate(), StandardViews.DirectionFor("front")!.Value)
        {
            Scale = 0.5,
            Center = new Vector2d(100, 100),
        };
        // The plate is 60 x 12 seen from the front.
        Assert.Equal(30, view.Size.X, 6);
        Assert.Equal(6, view.Size.Y, 6);

        var bounds = view.Compute().Bounds;
        Assert.Equal(100, bounds.Center.X, 6);
        Assert.Equal(100, bounds.Center.Y, 6);
    }

    /// <summary>The title block prints the sheet's OWN scale and projection angle, so
    /// they cannot contradict the views they label.</summary>
    [Fact]
    public void TitleBlockReportsTheLayoutsOwnScaleAndAngle()
    {
        var sheet = DrawingSheet.StandardLayout(SceneOf(Plate()), SheetFormat.A3, ProjectionAngle.First);
        sheet.Title = sheet.Title with { Title = "BEARING PLATE", DrawingNumber = "EC-0001" };
        var content = sheet.Compute();
        var texts = content.Texts.Select(t => t.Text).ToList();

        Assert.Contains("BEARING PLATE", texts);
        Assert.Contains("EC-0001", texts);
        Assert.Contains("FIRST ANGLE", texts);
        Assert.Contains($"SCALE {DrawingScales.Format(sheet.Views[0].Scale)}", texts);
    }

    // ---- sections and hatching ----

    /// <summary>
    /// Hatch belongs INSIDE the cut faces and nowhere else. The test walks every hatch
    /// segment's midpoint and asks the cut regions whether they contain it — a point
    /// query against the same regions the hatch was clipped to, so a hatch that leaked
    /// past a bore's rim would fail.
    /// </summary>
    [Fact]
    public void SectionHatchStaysInsideTheCutRegions()
    {
        var view = new DrawingView(Plate(), StandardViews.DirectionFor("front")!.Value, "SECTION A-A")
        {
            Scale = 1,
            Center = new Vector2d(100, 100),
            SectionThrough = new Vector3d(0, 0, 0),
        };
        var content = view.Compute();

        Assert.NotEmpty(content.CutRegions);
        Assert.NotEmpty(content.Hatch);
        foreach (var (a, b) in content.Hatch)
        {
            var mid = (a + b) * 0.5;
            Assert.True(
                content.CutRegions.Any(r => r.Contains(mid)),
                $"hatch segment midpoint ({mid.X:F3}, {mid.Y:F3}) is outside every cut region");
        }
    }

    /// <summary>The bore is a hole in the cut face, so no hatch may cross it: the
    /// strongest single statement that the even-odd clip respects hole loops.</summary>
    [Fact]
    public void SectionHatchSkipsTheBore()
    {
        var view = new DrawingView(Plate(), StandardViews.DirectionFor("front")!.Value)
        {
            Scale = 1,
            Center = Vector2d.Zero,
            SectionThrough = new Vector3d(0, 0, 0),
        };
        var content = view.Compute();
        // Sectioned on the mid-plane through the bore's axis, the cut face is the
        // plate's 60 x 12 rectangle with a 10-wide slot where the bore passes.
        foreach (var (a, b) in content.Hatch)
        {
            var mid = (a + b) * 0.5;
            Assert.False(Math.Abs(mid.X) < 4.9 && Math.Abs(mid.Y) < 5.9,
                $"hatch at ({mid.X:F3}, {mid.Y:F3}) crosses the bore");
        }
    }

    [Fact]
    public void AnOrdinaryViewHasNoCutFaceAndNoHatch()
    {
        var view = new DrawingView(Plate(), StandardViews.DirectionFor("front")!.Value)
        {
            Center = new Vector2d(100, 100),
        };
        var content = view.Compute();
        Assert.Empty(content.CutRegions);
        Assert.Empty(content.Hatch);
    }

    /// <summary>Hatch lines are anchored to the ORIGIN, not to each region's bounds, so
    /// two cut faces side by side share one continuous pattern rather than two that
    /// happen to meet at an angle.</summary>
    [Fact]
    public void HatchIsAnchoredToTheOriginAcrossRegions()
    {
        var left = new Region2d([new(0, 0), new(10, 0), new(10, 10), new(0, 10)]);
        var right = new Region2d([new(20, 0), new(30, 0), new(30, 10), new(20, 10)]);
        var together = SheetHatch.Fill([left, right], spacing: 3, angleDegrees: 45);
        var normal = new Vector2d(-Math.Sqrt(0.5), Math.Sqrt(0.5));

        foreach (var (a, _) in together)
        {
            double offset = a.Dot(normal) / 3;
            Assert.Equal(Math.Round(offset), offset, 9);
        }
    }

    // ---- export ----

    [Fact]
    public void SvgIsSheetSizedAndLayered()
    {
        var sheet = DrawingSheet.StandardLayout(SceneOf(Plate()), SheetFormat.A4);
        sheet.Title = sheet.Title with { Title = "PLATE" };
        string svg = sheet.ToSvg();

        Assert.Contains("width=\"297mm\"", svg);
        Assert.Contains("height=\"210mm\"", svg);
        Assert.Contains("viewBox=\"0 -210 297 210\"", svg);
        Assert.Contains($"id=\"{SheetLayers.Visible}\"", svg);
        Assert.Contains($"id=\"{SheetLayers.Border}\"", svg);
        Assert.Contains("PLATE", svg);
        // Hidden detail is dashed, which is the whole point of classifying it.
        Assert.Contains("stroke-dasharray", svg);
    }

    /// <summary>
    /// The DXF goes out and comes back through the landed reader: the layers survive,
    /// the geometry survives as polylines and lines, and TEXT round-trips with its
    /// justification. What cannot round-trip is stated by the reader itself
    /// (diagnostics), never guessed at.
    /// </summary>
    [Fact]
    public void DxfRoundTripsThroughTheReader()
    {
        var sheet = DrawingSheet.StandardLayout(SceneOf(Plate()), SheetFormat.A4);
        sheet.Title = sheet.Title with { Title = "PLATE", DrawingNumber = "EC-0002" };
        var written = sheet.ToDxf();

        using var buffer = new StringWriter();
        written.Save(buffer);
        var read = DxfDocument.Load(new StringReader(buffer.ToString()));

        Assert.Empty(read.Diagnostics);
        Assert.Equal(written.Entities.Count, read.Entities.Count);
        foreach (string layer in written.Layers)
            Assert.Contains(layer, read.Layers);

        var texts = read.Entities.OfType<DxfText>().ToList();
        Assert.Contains(texts, t => t.Value == "PLATE");
        Assert.Contains(texts, t => t.Value == "EC-0002");
        Assert.Contains(texts, t => t.Anchor == SheetTextAnchor.Right);
        Assert.All(texts, t => Assert.True(t.Height > 0));
    }

    /// <summary>A file that names a line type must define it, or every reader shows
    /// solid lines and the hidden/visible distinction is lost in transit.</summary>
    [Fact]
    public void DxfDefinesEveryLineTypeItsLayersName()
    {
        var sheet = DrawingSheet.StandardLayout(SceneOf(Plate()), SheetFormat.A4);
        using var buffer = new StringWriter();
        sheet.ToDxf().Save(buffer);
        string dxf = buffer.ToString();

        Assert.Contains("LTYPE", dxf);
        Assert.Contains(DxfLineTypes.Hidden.Name, dxf);
        Assert.Contains(DxfLineTypes.Continuous.Name, dxf);
        // The dash pattern itself, not just the name.
        foreach (double dash in DxfLineTypes.Hidden.Dashes)
            Assert.Contains(dash.ToString("R", System.Globalization.CultureInfo.InvariantCulture), dxf);
    }

    [Fact]
    public void HiddenLineWorkLandsOnTheHiddenLayer()
    {
        var sheet = DrawingSheet.StandardLayout(SceneOf(Plate()), SheetFormat.A3);
        var dxf = sheet.ToDxf();
        Assert.Contains(SheetLayers.Hidden, dxf.Layers);
        Assert.Equal(DxfLineTypes.Hidden.Name, dxf.LayerLineTypes[SheetLayers.Hidden]);
    }

    /// <summary>Same model, same sheet, same bytes. A drawing that changed between
    /// builds could not be a controlled document.</summary>
    [Fact]
    public void SheetOutputIsDeterministic()
    {
        var scene = SceneOf(Plate());
        string first = DrawingSheet.StandardLayout(scene, SheetFormat.A3).ToSvg();
        string second = DrawingSheet.StandardLayout(scene, SheetFormat.A3).ToSvg();
        Assert.Equal(first, second);
    }
}
