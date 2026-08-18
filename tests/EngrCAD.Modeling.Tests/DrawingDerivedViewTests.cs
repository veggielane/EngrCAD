using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Views derived from other views — a section marked on its parent, a detail blown up out of
/// one, a broken view with its middle elided.
///
/// <para>Each has ONE closed-form oracle, because a picture of a drawing proves nothing: a
/// detail at 2:1 draws exactly twice its parent's line length, a break shortens the view by
/// exactly the band it removed while its dimensions keep the part's TRUE lengths, and a
/// cutting line lands exactly on the plane the section was taken at.</para>
/// </summary>
public class DrawingDerivedViewTests
{
    private static Part Plate() =>
        new("plate", Shape.Box(80, 50, 10));

    private static DrawingView FrontOf(Part part, double scale = 1) =>
        new(part, StandardViews.DirectionFor("front")!.Value, "FRONT") { Scale = scale, Center = (100, 100) };

    private static double SheetLength(DrawingViewContent content)
    {
        double total = 0;
        foreach (var run in content.Runs)
            total += run.Length;
        return total;
    }

    // ---------------------------------------------------------------- detail views

    /// <summary>
    /// THE detail-view oracle. A detail whose disc contains the whole parent clips nothing, so
    /// its line work IS the parent's at another scale — and a uniform scale multiplies every
    /// length by exactly the magnification. Any drift here is a placement bug, not a tolerance.
    /// </summary>
    [Fact]
    public void ADetailAtTwiceTheScaleDrawsExactlyTwiceTheParentsLineLength()
    {
        var part = Plate();
        var front = FrontOf(part);
        var bounds = front.ContentBounds;
        double radius = bounds.Size.Length;   // comfortably contains the whole view

        var detail = DrawingView.DetailOf(
            front, new Vector2d(bounds.Center.X, bounds.Center.Y), radius, magnification: 2, "A");
        detail.Center = (250, 100);

        double parent = SheetLength(front.Compute());
        double blown = SheetLength(detail.Compute());
        Assert.True(parent > 0);
        Assert.Equal(2 * parent, blown, 9);
    }

    /// <summary>A detail SHARES its parent's projection — it is a clip of exactly the line work
    /// the parent shows, never a second answer to the same question.</summary>
    [Fact]
    public void ADetailSharesItsParentsProjection()
    {
        var front = FrontOf(Plate());
        var detail = DrawingView.DetailOf(front, (0, 0), 10, 2, "A");
        Assert.Same(front.Projected, detail.Projected);
    }

    /// <summary>Every point a detail draws is inside its own circle, and the clip really cut
    /// something (a clip that kept everything would pass the containment test vacuously).</summary>
    [Fact]
    public void ADetailDrawsNothingOutsideItsCircle()
    {
        var front = FrontOf(Plate());
        var clip = new ViewDetail(new Vector2d(30, 15), 12);
        var detail = DrawingView.DetailOf(front, clip.Centre, clip.Radius, 3, "A");

        var (runs, _) = detail.Content;
        Assert.NotEmpty(runs);
        foreach (var run in runs)
        {
            foreach (var p in run.Points)
                Assert.True((p - clip.Centre).Length <= clip.Radius + 1e-9, $"{p} is outside the detail circle");
        }
        // ... and it is a genuine clip: the parent draws strictly more than the detail.
        Assert.True(front.Content.Runs.Count > 0);
        Assert.True(SheetLength(front.Compute()) > 0);
        double clipped = 0;
        foreach (var run in runs)
            clipped += run.Length;
        double whole = 0;
        foreach (var run in front.Content.Runs)
            whole += run.Length;
        Assert.True(clipped < whole, "the detail clip removed nothing");
    }

    /// <summary>The parent gets the circle, on the symbol layer, at the detail's own centre and
    /// radius — the other half of the view-to-view reference.</summary>
    [Fact]
    public void ADetailPutsItsCircleOnTheParent()
    {
        var part = Plate();
        var sheet = new DrawingSheet(SheetFormat.A3);
        var front = FrontOf(part);
        var detail = DrawingView.DetailOf(front, new Vector2d(20, 10), 15, 2, "A");
        detail.Center = (300, 150);
        sheet.Add(front).Add(detail);

        var content = sheet.Compute();
        var circle = content.Lines.Where(l => l.Layer == SheetLayers.Symbol).ToList();
        Assert.NotEmpty(circle);

        // Every circle segment endpoint sits at the detail radius from the mapped centre
        // (scale 1, so a model radius is a sheet radius).
        var centre = front.Center + new Vector2d(20, 10) - new Vector2d(
            front.ContentBounds.Center.X, front.ContentBounds.Center.Y);
        // Exactly the circle's own chords have BOTH ends on it; the leader has one end on it
        // and one beyond, which is what makes this count the circle rather than the marker.
        int chords = circle.Count(l =>
            Math.Abs((l.A - centre).Length - 15) < 1e-9 && Math.Abs((l.B - centre).Length - 15) < 1e-9);
        Assert.Equal(ViewDetail.CircleSegments, chords);
        Assert.Contains(content.Texts, t => t.Text == "A" && t.Layer == SheetLayers.Symbol);
    }

    /// <summary>A detail of a SECTION view is refused by name — its hatched cut faces are
    /// regions, and clipping a region to a circle is a boolean this view does not do.</summary>
    [Fact]
    public void ADetailOfASectionViewIsRefusedByName()
    {
        var part = Plate();
        var section = new DrawingView(part, StandardViews.DirectionFor("front")!.Value, "SECTION")
        {
            SectionThrough = (0, 0, 0),
        };
        var error = Assert.Throws<InvalidOperationException>(
            () => DrawingView.DetailOf(section, (0, 0), 10, 2, "A"));
        Assert.Contains("section view", error.Message);
    }

    // ---------------------------------------------------------------- broken views

    /// <summary>
    /// THE broken-view oracle, in two exact halves. The drawn view is shorter by exactly the
    /// band removed; and a dimension spanning the break still MEASURES the part, because its
    /// value is read from its anchors and the break lives in the placement map.
    /// </summary>
    [Fact]
    public void ABreakShortensTheDrawingByExactlyWhatItRemovedAndTheDimensionStaysTrue()
    {
        var bar = new Part("bar", Shape.Box(400, 20, 10));
        var view = new DrawingView(bar, StandardViews.DirectionFor("front")!.Value, "FRONT")
        {
            Scale = 1, Center = (200, 150),
        };
        double full = view.ContentBounds.Size.X;
        Assert.Equal(400, full, 9);

        var split = ViewBreak.Between(BreakAxis.Horizontal, -120, 120, gap: 10);
        view.Break = split;
        Assert.Equal(240 - 10, split.Removed, 12);
        Assert.Equal(full - split.Removed, view.ContentBounds.Size.X, 9);

        // A dimension across the break reads the TRUE length, not the drawn one.
        var overall = SheetLinearDimension.Horizontal((-200, -10), (200, -10), -12);
        Assert.Equal(400, overall.Value, 12);

        // ... and its arrows land on the DRAWN view: the two extension lines are the shortened
        // span apart, so the drawing is honest about the length it shows AND about the one it
        // states.
        var segments = new List<(Vector2d A, Vector2d B)>();
        var texts = new List<SheetText>();
        view.Annotate(overall);
        var content = view.Compute();
        segments.AddRange(content.Dimensions);
        texts.AddRange(content.Texts);
        double drawnSpan = segments.Max(s => Math.Max(s.A.X, s.B.X)) - segments.Min(s => Math.Min(s.A.X, s.B.X));
        Assert.Equal(full - split.Removed, drawnSpan, 6);
        Assert.Contains(texts, t => t.Text == "400");
    }

    /// <summary>Nothing is drawn inside the removed band, and the map is monotone through it —
    /// so the two halves keep their order and neither overlaps the other.</summary>
    [Fact]
    public void ABrokenViewDrawsNothingInsideTheRemovedBand()
    {
        var bar = new Part("bar", Shape.Box(400, 20, 10));
        var view = new DrawingView(bar, StandardViews.DirectionFor("front")!.Value, "FRONT")
        {
            Scale = 1, Center = (200, 150),
            Break = ViewBreak.Between(BreakAxis.Horizontal, -120, 120, gap: 10),
        };

        // Content is already MAPPED, so the elided band is the drawn gap (From, From + Gap).
        foreach (var run in view.Content.Runs)
        {
            foreach (var p in run.Points)
                Assert.False(p.X > -120 + 1e-9 && p.X < -110 - 1e-9,
                    $"{p} sits inside the elided band");
        }

        // The map is monotone and continuous: it never reorders the part.
        var b = view.Break!;
        double previous = double.NegativeInfinity;
        for (double x = -200; x <= 200; x += 0.5)
        {
            double mapped = b.Map(new Vector2d(x, 0)).X;
            Assert.True(mapped >= previous - 1e-12, "the break map reordered the part");
            previous = mapped;
        }
    }

    /// <summary>A broken view draws its break lines, on the symbol layer, one per cut edge.</summary>
    [Fact]
    public void ABrokenViewDrawsTwoBreakLines()
    {
        var bar = new Part("bar", Shape.Box(400, 20, 10));
        var view = new DrawingView(bar, StandardViews.DirectionFor("front")!.Value, "FRONT")
        {
            Scale = 1, Center = (200, 150),
            Break = ViewBreak.Between(BreakAxis.Horizontal, -120, 120, gap: 10, teeth: 5),
        };
        var content = view.Compute();
        Assert.NotNull(content.Symbols);
        int segments = content.Symbols!.Count(s => s.Layer == SheetLayers.Symbol);
        Assert.Equal(2 * 5 * 2, segments);   // two lines, 2*teeth segments each
    }

    [Fact]
    public void AnInvertedBandOrANonPositiveGapIsRefusedByName()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ViewBreak.Between(BreakAxis.Horizontal, 10, 5, 2));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ViewBreak.Between(BreakAxis.Horizontal, -10, 10, 0));
    }

    // ---------------------------------------------------------------- cut-plane indication

    /// <summary>
    /// THE cutting-line oracle: the line the parent draws lies exactly on the plane the section
    /// was taken at. A section along +X through x = 12 shows on a TOP view as the line x = 12,
    /// so every drawn point's model x is 12 — read back through the parent's own placement.
    /// </summary>
    [Fact]
    public void TheCuttingLineLandsExactlyOnThePlaneTheSectionWasTakenAt()
    {
        var part = Plate();
        var sheet = new DrawingSheet(SheetFormat.A3);
        var top = new DrawingView(part, StandardViews.DirectionFor("top")!.Value, "TOP")
        {
            Scale = 1, Center = (120, 150),
        };
        var section = DrawingView.SectionOf(
            top, StandardViews.DirectionFor("right")!.Value, new Vector3d(12, 0, 0), "A");
        section.Center = (300, 150);
        sheet.Add(top).Add(section);

        var content = sheet.Compute();
        var cut = content.Lines.Where(l => l.Layer == SheetLayers.Section).ToList();
        Assert.NotEmpty(cut);

        // Map a sheet point back into the parent's model coordinates (uniform scale about the
        // view's own centre) and read its x.
        var origin = new Vector2d(top.ContentBounds.Center.X, top.ContentBounds.Center.Y);
        Vector2d ToModel(Vector2d p) => (p - top.Center) / top.Scale + origin;

        // The cutting LINE itself (the longest segment) runs along x = 12.
        var line = cut.OrderByDescending(l => (l.B - l.A).Length).First();
        Assert.Equal(12, ToModel(line.A).X, 9);
        Assert.Equal(12, ToModel(line.B).X, 9);

        // ... and it carries its letter at both ends.
        Assert.Equal(2, content.Texts.Count(t => t.Text == "A" && t.Layer == SheetLayers.Section));
        Assert.Equal("SECTION A-A", section.Label);
    }

    /// <summary>
    /// A section is marked on a view SQUARE to it — a plane projected along a direction that is
    /// not in it covers the whole sheet, so there is no line to draw. Refused at the CALL.
    /// </summary>
    [Fact]
    public void ASectionNotSquareToItsParentIsRefusedByName()
    {
        var part = Plate();
        var front = FrontOf(part);
        var error = Assert.Throws<InvalidOperationException>(() => DrawingView.SectionOf(
            front, StandardViews.DirectionFor("iso")!.Value, Vector3d.Zero, "A"));
        Assert.Contains("square", error.Message);
    }

    /// <summary>A derived view whose parent is BROKEN is refused by name: the mark would have to
    /// be drawn across the break in two pieces and no convention here says which.</summary>
    [Fact]
    public void AMarkOnABrokenParentIsRefusedByName()
    {
        var part = new Part("bar", Shape.Box(400, 20, 10));
        var top = new DrawingView(part, StandardViews.DirectionFor("top")!.Value, "TOP")
        {
            Scale = 0.5, Center = (120, 150),
        };
        var section = DrawingView.SectionOf(
            top, StandardViews.DirectionFor("right")!.Value, new Vector3d(0, 0, 0), "A");
        section.Center = (300, 150);
        top.Break = ViewBreak.Between(BreakAxis.Horizontal, -120, 120, 10);

        var sheet = new DrawingSheet(SheetFormat.A3).Add(top).Add(section);
        var error = Assert.Throws<InvalidOperationException>(() => sheet.Compute());
        Assert.Contains("broken view", error.Message);
    }

    // ---------------------------------------------------------------- determinism / writers

    /// <summary>Every new feature is a deterministic function of the sheet — a placement
    /// heuristic that depended on iteration order would show here.</summary>
    [Fact]
    public void ASheetWithEveryNewFeatureIsAByteIdenticalFunctionOfItself()
    {
        Assert.Equal(BuildRichSheet().ToSvg(), BuildRichSheet().ToSvg());
    }

    /// <summary>The three writers consume ONE `Compute()`, so a new primitive must reach all
    /// three — a cutting line's letter is in the SVG, the DXF and the PDF.</summary>
    [Fact]
    public void ANewFeatureReachesAllThreeWriters()
    {
        var sheet = BuildRichSheet();

        string svg = sheet.ToSvg();
        Assert.Contains("SECTION A-A", svg);
        Assert.Contains($"\"{SheetLayers.Symbol}\"", svg);

        var dxf = sheet.ToDxf();
        Assert.Contains(dxf.Entities.OfType<DxfText>(), t => t.Value == "A" && t.Layer == SheetLayers.Section);
        Assert.Contains(SheetLayers.Symbol, dxf.Layers);

        string pdf = System.Text.Encoding.ASCII.GetString(sheet.ToPdf());
        Assert.Contains("(SECTION A-A)", pdf);
    }

    private static DrawingSheet BuildRichSheet()
    {
        var top = SketchPlane.At((0, 0, 10), Vector3d.UnitX, Vector3d.UnitY);
        var body = Shape.Box(80, 50, 10)
            .Drill(HoleSpec.Simple(6), LocationSet.Polar(6, 25), depth: 12, top);
        var part = new Part("plate", body);

        var sheet = new DrawingSheet(SheetFormat.A3);
        var plan = new DrawingView(part, StandardViews.DirectionFor("top")!.Value, "TOP")
        {
            Scale = 1, Center = (110, 160),
        };
        var section = DrawingView.SectionOf(
            plan, StandardViews.DirectionFor("front")!.Value, new Vector3d(0, 0, 0), "A");
        section.Center = (300, 160);
        var detail = DrawingView.DetailOf(plan, new Vector2d(25, 0), 10, 2, "B");
        detail.Center = (300, 80);

        AutoDimension.Apply(plan);
        sheet.Add(plan).Add(section).Add(detail);
        sheet.Title = sheet.Title with { Title = "PLATE", DrawingNumber = "EC-9001" };
        return sheet;
    }
}
