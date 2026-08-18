using System.Globalization;
using System.Text;
using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Modeling.Tests.Text;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The four PDF follow-ups — an embedded font subset, optional-content layers, opt-in
/// Flate, and sketch export — each verified the way the writer itself is: through the
/// independently written <see cref="PdfReadback"/> parser, against the byte fixed point,
/// and with the DEFAULT output pinned byte for byte so an option cannot move what a
/// caller who asked for nothing gets.
/// </summary>
public class PdfFollowUpTests
{
    // -------------------------------------------------------------------- fixtures

    /// <summary>The synthetic font: exact outlines, a composite glyph ('A' places 'I'
    /// twice) and a known advance table, so a subset can be checked against values
    /// rather than against "it looks like a font".</summary>
    private static readonly TrueTypeFont Synthetic = TrueTypeFont.Load(SyntheticFont.Build());

    /// <summary>A hole callout as the dimension layer emits it: the diameter sign, the
    /// depth arrow and the counterbore symbol - three characters WinAnsi has no form for
    /// (the diameter one only survives as its O-stroke stand-in). Escapes, not literals,
    /// so the source file stays pure ASCII (the Callouts.cs convention).</summary>
    private const string DraftingCallout = "\u23004.5 \u21A712 \u2334\u23008";

    /// <summary>
    /// An installed font carrying every character of <see cref="DraftingCallout"/>.
    /// <see cref="SystemFonts"/> deliberately finds ANY real font (Arial first) and Arial
    /// carries NONE of the drafting symbols, so this scans for one that does rather than
    /// measuring against a font that cannot answer the question - on Windows it is Segoe
    /// UI Symbol. Null when the machine has none, which is a skip and not a failure.
    /// </summary>
    private static readonly Lazy<TrueTypeFont?> DraftingSymbolFont = new(() =>
    {
        string[] directories = [@"C:\Windows\Fonts", "/usr/share/fonts", "/System/Library/Fonts"];
        foreach (string directory in directories.Where(Directory.Exists))
        {
            foreach (string path in Directory
                         .EnumerateFiles(directory, "*.ttf", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                try
                {
                    var font = TrueTypeFont.Load(path);
                    if (DraftingCallout.All(c => font.TryGetGlyphIndex(c, out _)))
                        return font;
                }
                catch (FontFormatException)
                {
                    // A font this reader declines (a collection, CFF2): keep looking.
                }
            }
        }
        return null;
    });

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

    private static DrawingSheet PlateSheet()
    {
        var plate = new Part("plate", Shape.Box(60, 40, 12)
            .Drill(HoleSpec.Simple(10), [new Vector2d(0, 0)], depth: 14,
                SketchPlane.At((0, 0, 6), Vector3d.UnitX, Vector3d.UnitY)));
        var sheet = DrawingSheet.StandardLayout(plate, SheetFormat.A4);
        sheet.Title = sheet.Title with { Title = "PLATE", DrawingNumber = "EC-0001" };
        return sheet;
    }

    // =================================================================== (a) fonts

    [Fact]
    public void EmbeddingAFontIsAByteFixedPoint_AndLeavingItAloneIsTheIncumbentFile()
    {
        var plain = TitleSheet().ToPdf();
        // An all-default options value is not merely equivalent to passing none, it is
        // the SAME BYTES — the property that makes every setting here safe to reach for.
        Assert.Equal(plain, TitleSheet().ToPdf(PdfSheetOptions.Default));
        Assert.Equal(plain, TitleSheet().ToPdf(new PdfSheetOptions()));

        // The synthetic font spells only " ACIO", so the embedded fixture letters with
        // that alphabet — the FONT decides what a drawing can say, which is the whole
        // point of the feature.
        PdfDrawing Embedded()
        {
            var pdf = new PdfDrawing { Sheet = (100, 60), Font = PdfFont.Embed(Synthetic) };
            pdf.AddPolyline([new Vector2d(10, 10), new Vector2d(90, 10)]);
            pdf.AddText(new Vector2d(10, 20), "AIO CIAO", 5);
            return pdf;
        }
        byte[] embedded = Embedded().ToPdf();
        Assert.Equal(embedded, Embedded().ToPdf());
        Assert.NotEqual(plain, embedded);

        // The subset carries no clock: the pieces whose natural values would move are
        // the font's own date stamps, and they are zeroed rather than carried.
        var program = PdfReadback.Parse(embedded).Font.Program!;
        var head = TableOf(program, "head");
        Assert.All(head[20..36], b => Assert.Equal(0, b));
    }

    /// <summary>
    /// The subset re-read by the FONT READER — which has never seen the subsetter — is
    /// the oracle a structural check cannot be: every kept glyph's outline and advance
    /// must come back exactly, and a composite must still place its component, which is
    /// what catches the classic subsetting failure (a renumbered or dropped component
    /// leaves a plausible font whose accented glyphs are blank).
    /// </summary>
    [Fact]
    public void TheSubsetFontReReadsWithEveryKeptGlyphIdenticalAndItsCompositeIntact()
    {
        var pdf = new PdfDrawing { Font = PdfFont.Embed(Synthetic) };
        pdf.AddText(new Vector2d(0, 0), "AIO", 5);
        var program = PdfReadback.Parse(pdf.ToPdf()).Font.Program!;
        var subset = TrueTypeFont.Load(program);

        // 'A' is the composite; the closure must have kept 'I', which it places twice.
        Assert.Contains(SyntheticFont.RectGlyph, Synthetic.CompositeComponents(SyntheticFont.CompositeGlyph));

        foreach (char c in "AIO")
        {
            Assert.True(Synthetic.TryGetGlyphIndex(c, out int glyph));
            var original = Synthetic.GetGlyph(glyph);
            var carried = subset.GetGlyph(glyph);          // indices are kept, so the SAME index
            Assert.Equal(original.Contours.Count, carried.Contours.Count);
            for (int i = 0; i < original.Contours.Count; i++)
            {
                var a = original.Contours[i].Points;
                var b = carried.Contours[i].Points;
                Assert.Equal(a.Count, b.Count);
                for (int k = 0; k < a.Count; k++)
                {
                    Assert.Equal(a[k].Position.X, b[k].Position.X);
                    Assert.Equal(a[k].Position.Y, b[k].Position.Y);
                    Assert.Equal(a[k].OnCurve, b[k].OnCurve);
                }
            }
            Assert.Equal(original.AdvanceWidth, carried.AdvanceWidth);
        }
        // 'A' really is composite in the subset too (three contours: its own two 'I'
        // placements), so the component survived rather than the glyph being flattened.
        Assert.True(Synthetic.TryGetGlyphIndex('A', out int composite));
        Assert.Equal(2, subset.GetGlyph(composite).Contours.Count);
    }

    /// <summary>
    /// A glyph nobody used is not carried — the point of a subset. Two forms of that show
    /// here and both are the design rather than an accident: the synthetic font's 'C' is
    /// past the largest kept index so the subset simply does not reach it, and 'O' (a
    /// LOWER index than the kept 'I' would need for a range to exclude it) is carried in
    /// the index range but blank, since keeping indices means the range is dense.
    /// </summary>
    [Fact]
    public void AnUnusedGlyphIsDroppedFromTheSubset()
    {
        var pdf = new PdfDrawing { Font = PdfFont.Embed(Synthetic) };
        pdf.AddText(new Vector2d(0, 0), "O", 5);
        var subset = TrueTypeFont.Load(PdfReadback.Parse(pdf.ToPdf()).Font.Program!);

        // 'C' is glyph 4, past the kept range, so the subset has no such glyph at all.
        Assert.True(Synthetic.TryGetGlyphIndex('C', out int beyond));
        Assert.False(Synthetic.GetGlyph(beyond).IsEmpty);   // the fixture carries the configuration
        Assert.True(beyond >= subset.GlyphCount);
        Assert.False(subset.TryGetGlyphIndex('C', out _));

        // 'I' is glyph 2, INSIDE the kept range, and comes back blank — carried as an
        // index, dropped as an outline, which is the cost of keeping glyph numbering.
        Assert.True(subset.GetGlyph(SyntheticFont.RectGlyph).IsEmpty);
        Assert.False(Synthetic.GetGlyph(SyntheticFont.RectGlyph).IsEmpty);

        // And what WAS used is intact.
        Assert.False(subset.GetGlyph(SyntheticFont.RingGlyph).IsEmpty);
    }

    [Fact]
    public void EmbeddedTextRoundTripsThroughTheFilesOwnToUnicodeMap()
    {
        var pdf = new PdfDrawing { Font = PdfFont.Embed(Synthetic) };
        pdf.AddText(new Vector2d(10, 20), "OIA", 3.5);
        var doc = PdfReadback.Parse(pdf.ToPdf());

        Assert.Equal("Type0", doc.Font.Subtype);
        // The reader decodes glyph indices back to characters through the CMap in the
        // FILE, which is exactly what a reader's copy-and-paste does.
        Assert.Equal("OIA", Assert.Single(doc.Texts).Value);

        // /W states each used glyph's advance in 1000-unit text space, from the font's
        // own hmtx: the synthetic 'I' is 400 units of a 1000-unit em.
        Assert.True(Synthetic.TryGetGlyphIndex('I', out int rect));
        Assert.Equal(SyntheticFont.Advances[rect] * 1000.0 / Synthetic.UnitsPerEm, doc.Font.Widths[rect], 9);
    }

    /// <summary>
    /// The headline: the drafting symbols WinAnsi refuses. A real system font carries
    /// them, so the depth/counterbore/countersink signs a hole callout emits reach the
    /// paper instead of being refused by name.
    /// </summary>
    [SkippableFact]
    public void TheDraftingSymbolsWinAnsiRefusesSurviveUnderAnEmbeddedFont()
    {
        Skip.If(DraftingSymbolFont.Value is null,
            "no installed font carries the drafting symbols (diameter, depth, counterbore)");
        var font = DraftingSymbolFont.Value!;
        const string callout = DraftingCallout;

        // Under the built-in Helvetica the depth sign is refused BY NAME (the incumbent
        // contract, still in force) ...
        var refusal = Assert.Throws<NotSupportedException>(
            () => new PdfDrawing().AddText(new Vector2d(0, 0), callout, 3.5));
        Assert.Contains("U+21A7", refusal.Message, StringComparison.Ordinal);

        // ... and under an embedded font it simply travels, U+2300 as ITSELF rather than
        // as the O-stroke stand-in.
        var pdf = new PdfDrawing { Font = PdfFont.Embed(font) };
        pdf.AddText(new Vector2d(0, 0), callout, 3.5);
        var doc = PdfReadback.Parse(pdf.ToPdf());
        Assert.Equal(callout, Assert.Single(doc.Texts).Value);
    }

    [Fact]
    public void SettingTheFontReEncodesEveryRun_AndARefusalLeavesTheDrawingAsItWas()
    {
        var pdf = new PdfDrawing();
        pdf.AddText(new Vector2d(0, 0), "IO", 5);        // legal in WinAnsi and in the font
        pdf.AddText(new Vector2d(0, 10), "Zebra", 5);    // WinAnsi yes, synthetic font no

        var refusal = Assert.Throws<NotSupportedException>(
            () => pdf.Font = PdfFont.Embed(Synthetic));
        Assert.Contains("Zebra", refusal.Message, StringComparison.Ordinal);

        // All-or-nothing: the font is untouched and the file is still the Helvetica one.
        Assert.Same(PdfFont.Helvetica, pdf.Font);
        Assert.Equal("Type1", PdfReadback.Parse(pdf.ToPdf()).Font.Subtype);
    }

    [SkippableFact]
    public void APostScriptFontIsRefusedByName()
    {
        Skip.If(SystemFonts.CffSkipReason is not null, SystemFonts.CffSkipReason);
        var pdf = new PdfDrawing { Font = PdfFont.Embed(SystemFonts.CffFont) };
        pdf.AddText(new Vector2d(0, 0), "A", 5);
        var refusal = Assert.Throws<NotSupportedException>(() => pdf.ToPdf());
        Assert.Contains("CFF", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSubsetTagIsAFunctionOfTheGlyphSet()
    {
        string Tag(string text)
        {
            var pdf = new PdfDrawing { Font = PdfFont.Embed(Synthetic) };
            pdf.AddText(new Vector2d(0, 0), text, 5);
            return PdfReadback.Parse(pdf.ToPdf()).Font.BaseFont;
        }

        // Same glyphs in another order and with repeats: the same subset, so the same tag.
        Assert.Equal(Tag("IO"), Tag("OIIO"));
        // A different glyph set is a different subset, and must not claim to be that one.
        Assert.NotEqual(Tag("IO"), Tag("IOC"));
        Assert.Matches("^[A-Z]{6}\\+", Tag("IO"));
    }

    // ================================================================== (b) layers

    [Fact]
    public void ASheetWithoutLayersIsByteIdentical_AndWithThemEveryClassGetsItsGroup()
    {
        var sheet = PlateSheet();
        byte[] plain = sheet.ToPdf();
        Assert.Equal(plain, sheet.ToPdf(new PdfSheetOptions { Layers = false }));

        byte[] layered = sheet.ToPdf(new PdfSheetOptions { Layers = true });
        Assert.NotEqual(plain, layered);
        Assert.Equal(layered, sheet.ToPdf(new PdfSheetOptions { Layers = true }));   // fixed point

        var doc = PdfReadback.Parse(layered);
        // The SAME SheetLayers names the SVG groups and the DXF layer table use.
        Assert.Contains(SheetLayers.Visible, doc.Layers);
        Assert.Contains(SheetLayers.Hidden, doc.Layers);
        Assert.Contains(SheetLayers.Border, doc.Layers);
        Assert.Contains(SheetLayers.TitleBlock, doc.Layers);

        // Optional content is a 1.5 feature and the file says so; a layer-free file
        // keeps the version it always declared.
        Assert.StartsWith("%PDF-1.5", Encoding.ASCII.GetString(layered[..8]), StringComparison.Ordinal);
        Assert.StartsWith("%PDF-1.4", Encoding.ASCII.GetString(plain[..8]), StringComparison.Ordinal);
    }

    /// <summary>
    /// The claim with teeth is not that groups exist but that the RIGHT line work is in
    /// each: every dashed stroke must be on the hidden layer and no other, which is
    /// what a reader toggling "hidden" acts on.
    /// </summary>
    [Fact]
    public void EveryStrokeLandsOnTheLayerItsClassNames()
    {
        var sheet = PlateSheet();
        var content = sheet.Compute();
        Assert.Contains(content.Runs, r => r.Visibility == EdgeVisibility.Hidden);   // the fixture carries it

        var doc = PdfReadback.Parse(sheet.ToPdf(new PdfSheetOptions { Layers = true }));
        Assert.All(doc.Strokes, s => Assert.NotNull(s.Layer));
        Assert.All(doc.Strokes.Where(s => s.Dash.Count > 0), s => Assert.Equal(SheetLayers.Hidden, s.Layer));
        Assert.All(doc.Strokes.Where(s => s.Layer == SheetLayers.Hidden), s => Assert.NotEmpty(s.Dash));

        // Text is on a layer too, and the title block's is the title-block layer.
        Assert.Equal(SheetLayers.TitleBlock, doc.Texts.Single(t => t.Value == "PLATE").Layer);
    }

    [Fact]
    public void APdfDrawingLayerCarriesItsNameAndItsOrderIsFirstUse()
    {
        var pdf = new PdfDrawing();
        pdf.AddPolyline([new Vector2d(0, 0), new Vector2d(10, 0)], layer: "outline");
        pdf.AddPolyline([new Vector2d(0, 5), new Vector2d(10, 5)], layer: "notes");
        pdf.AddPolyline([new Vector2d(0, 9), new Vector2d(10, 9)]);                 // no layer at all
        pdf.AddPolyline([new Vector2d(0, 1), new Vector2d(10, 1)], layer: "outline");

        Assert.Equal(["outline", "notes"], pdf.Layers);
        var doc = PdfReadback.Parse(pdf.ToPdf());
        Assert.Equal(["outline", "notes"], doc.Layers);
        // The two "outline" paths share ONE group, so they are emitted together at that
        // layer's first-use position rather than in the order they were added.
        string?[] expected = ["outline", "outline", "notes", null];
        Assert.Equal(expected, doc.Strokes.Select(s => s.Layer).ToArray());
    }

    // ============================================================= (c) compression

    [Fact]
    public void CompressionIsAPureReSpelling_TheInflatedStreamIsTheUncompressedOne()
    {
        var sheet = PlateSheet();
        byte[] plain = sheet.ToPdf();
        byte[] packed = sheet.ToPdf(new PdfSheetOptions { Compress = true });

        Assert.Equal(packed, sheet.ToPdf(new PdfSheetOptions { Compress = true }));   // fixed point
        Assert.True(packed.Length < plain.Length,
            $"compression made the file larger ({plain.Length} -> {packed.Length})");

        // The strong claim: inflating recovers the uncompressed writer's OWN stream,
        // byte for byte — so nothing about the drawing changed, only its spelling.
        Assert.Equal(PdfReadback.Parse(plain).Content, PdfReadback.Parse(packed).Content);

        // And the geometry still reads back the same through the decoder.
        var a = PdfReadback.Parse(plain);
        var b = PdfReadback.Parse(packed);
        Assert.Equal(a.Strokes.Count, b.Strokes.Count);
        Assert.Equal(
            a.Texts.Select(t => t.Value).ToArray(),
            b.Texts.Select(t => t.Value).ToArray());
    }

    [Fact]
    public void CompressionAndEmbeddingCompose()
    {
        // The synthetic font has only 'A', 'C', 'I', 'O' and space, so the drawing is
        // lettered with those alone.
        PdfDrawing Both()
        {
            var pdf = new PdfDrawing
            {
                Sheet = (100, 60),
                Compress = true,
                Font = PdfFont.Embed(Synthetic),
            };
            pdf.AddPolyline([new Vector2d(5, 5), new Vector2d(95, 5)], layer: "outline");
            pdf.AddText(new Vector2d(10, 20), "AIO", 5, layer: "notes");
            return pdf;
        }

        byte[] packed = Both().ToPdf();
        Assert.Equal(packed, Both().ToPdf());
        var doc = PdfReadback.Parse(packed);
        Assert.Equal("Type0", doc.Font.Subtype);
        Assert.Equal("AIO", Assert.Single(doc.Texts).Value);
        Assert.Equal(["outline", "notes"], doc.Layers);

        // The compressed font program still re-reads as a font.
        Assert.Equal(Synthetic.UnitsPerEm, TrueTypeFont.Load(doc.Font.Program!).UnitsPerEm);
    }

    // ================================================================= (d) sketches

    /// <summary>
    /// The closed form the Kappa mode's error rests on, checked against the curve the
    /// FILE carries rather than against the formula it came from: the deviation is
    /// measured by sampling the decoded cubic and comparing radii.
    /// </summary>
    [Fact]
    public void TheCubicArcDeviationMatchesItsClosedFormAndFallsAsTheSixthPower()
    {
        // The quarter-turn figure usually quoted for this construction, ~2.7e-4.
        Assert.Equal(2.7253e-4, PdfDrawing.ArcCubicDeviation(1, Math.PI / 2), 8);

        // The small-angle law the derivation gives, theta^6 / 55296, checked as a LIMIT
        // rather than restated: the ratio approaches 1 as the span shrinks.
        Assert.Equal(1.0, PdfDrawing.ArcCubicDeviation(1, 0.05) * 55296 / Math.Pow(0.05, 6), 3);

        // Sixth order: halving the number of spans (doubling the sweep) multiplies the
        // error by 64, not by the 16 a fourth-order rule would give.
        double[] sweeps = [Math.PI / 2, Math.PI / 4, Math.PI / 8, Math.PI / 16];
        var errors = sweeps.Select(s => PdfDrawing.ArcCubicDeviation(1, s)).ToArray();
        var ratios = Enumerable.Range(1, errors.Length - 1)
            .Select(i => errors[i - 1] / errors[i]).ToArray();
        // The law is ASYMPTOTIC, so the ratios APPROACH 64 rather than sitting on it —
        // 64.19 at a quarter turn, and inside a thousandth by the last pair. A 4th-order
        // rule would read 16 at every step, so the two are never confusable.
        Assert.All(ratios, r => Assert.InRange(r, 63.9, 64.3));
        Assert.Equal(64.0, ratios[^1], 2);

        // And it is the deviation the written curve really has: sample the decoded
        // cubic and measure its distance from the circle.
        const double radius = 20;
        var sketch = Sketch.Circle(radius);
        var pdf = new PdfDrawing();
        var report = pdf.Add(sketch, PdfCurveMode.Kappa, tolerance: 1);   // loose: one span per quarter
        var stroke = Assert.Single(PdfReadback.Parse(pdf.ToPdf()).Strokes);
        var subpath = Assert.Single(stroke.Subpaths);
        Assert.Equal(4, subpath.Curves.Count);                            // a circle is four cubics

        double predicted = PdfDrawing.ArcCubicDeviation(radius, Math.PI / 2);
        Assert.Equal(predicted, report.MaxDeviation, 12);

        // The derivation puts the extremum at u^2 = 1/12, i.e. t = 1/2 +/- 1/(2*sqrt(3)),
        // so that is where the curve is asked. A uniform scan is the WRONG instrument for
        // a maximum: sampled every 1/64 the same curve reads 0.005440 against the true
        // 0.005451, short by 0.2% purely because no sample lands on the peak — under, so
        // it would quietly flatter the construction rather than convict it.
        double peakT = 0.5 + 1 / (2 * Math.Sqrt(3));
        foreach (var (endIndex, c1, c2) in subpath.Curves)
        {
            var p0 = subpath.Points[endIndex - 1];
            var p3 = subpath.Points[endIndex];
            Vector2d At(double t)
            {
                double u = 1 - t;
                return p0 * (u * u * u) + c1 * (3 * u * u * t) + c2 * (3 * u * t * t) + p3 * (t * t * t);
            }

            // The circle is centred on the origin, so the radius IS the distance.
            Assert.Equal(predicted, At(peakT).Length - radius, 9);
            Assert.Equal(predicted, At(1 - peakT).Length - radius, 9);

            // Three exact statements the closed form makes: the ends and the MIDPOINT lie
            // on the arc (the midpoint is what fixes k at all), and nowhere does the cubic
            // cut INSIDE it — the error term is a square times a square, never negative.
            Assert.Equal(radius, At(0).Length, 9);
            Assert.Equal(radius, At(0.5).Length, 9);
            Assert.Equal(radius, At(1).Length, 9);
            for (int k = 0; k <= 64; k++)
            {
                double t = k / 64.0;
                double error = At(t).Length - radius;
                Assert.InRange(error, -1e-12, predicted + 1e-12);
            }
        }
    }

    [Fact]
    public void LinesAndBeziersAreExactInEitherMode()
    {
        // A rounded rectangle is lines and arcs; a beziered profile adds cubics.
        var sketch = Sketch.Start(0, 0)
            .LineTo(20, 0)
            .BezierTo((26, 4), (26, 12), (20, 16))
            .LineTo(0, 16)
            .Close();

        foreach (var mode in new[] { PdfCurveMode.Flatten, PdfCurveMode.Kappa })
        {
            var pdf = new PdfDrawing();
            var report = pdf.Add(sketch, mode);
            Assert.True(report.IsExact, $"{mode} approximated something in a sketch of lines and cubics");
            Assert.Equal(4, report.ExactSegments);
            Assert.Equal(0, report.MaxDeviation);

            // The cubic's control points arrive verbatim — the exactness claim, checked
            // rather than asserted from the segment count.
            var subpath = Assert.Single(Assert.Single(PdfReadback.Parse(pdf.ToPdf()).Strokes).Subpaths);
            var (_, c1, c2) = Assert.Single(subpath.Curves);
            Assert.Equal(26, c1.X);
            Assert.Equal(4, c1.Y);
            Assert.Equal(26, c2.X);
            Assert.Equal(12, c2.Y);
            Assert.True(subpath.Closed);
        }
    }

    [Fact]
    public void FlatteningHonoursItsStatedToleranceAndReportsWhatItSpent()
    {
        const double radius = 30;
        foreach (double tolerance in new[] { 0.5, 0.05, 0.005 })
        {
            var pdf = new PdfDrawing();
            var report = pdf.Add(Sketch.Circle(radius), PdfCurveMode.Flatten, tolerance);
            Assert.Equal(1, report.ApproximatedSegments);
            Assert.True(report.MaxDeviation <= tolerance);

            // Measured off the file: every chord midpoint is within the tolerance of
            // the circle, and the deviation reported is the one the polyline has.
            var subpath = Assert.Single(Assert.Single(PdfReadback.Parse(pdf.ToPdf()).Strokes).Subpaths);
            Assert.Empty(subpath.Curves);
            double worst = 0;
            for (int i = 1; i < subpath.Points.Count; i++)
                worst = Math.Max(worst, radius - ((subpath.Points[i - 1] + subpath.Points[i]) * 0.5).Length);
            Assert.Equal(report.MaxDeviation, worst, 9);
            Assert.True(worst <= tolerance);
        }
    }

    /// <summary>
    /// Kappa buys the same accuracy for far fewer path elements — the reason to offer
    /// it at all, measured rather than claimed.
    /// </summary>
    [Fact]
    public void KappaReachesTheSameToleranceWithFarFewerElements()
    {
        int Elements(PdfCurveMode mode)
        {
            var pdf = new PdfDrawing();
            pdf.Add(Sketch.Circle(30), mode, tolerance: 0.005);
            var subpath = Assert.Single(Assert.Single(PdfReadback.Parse(pdf.ToPdf()).Strokes).Subpaths);
            return subpath.Points.Count;
        }
        int flattened = Elements(PdfCurveMode.Flatten);
        int cubics = Elements(PdfCurveMode.Kappa);
        Assert.True(cubics * 8 < flattened,
            $"expected the cubic route to be far cheaper; got {cubics} against {flattened}");
    }

    [Fact]
    public void AnEllipticalArcIsTheAffineImageOfTheCircularConstruction()
    {
        // A rotated ellipse: neither semi-axis is an axis of the page, so a construction
        // that quietly assumed an axis-aligned one would miss.
        var sketch = Sketch.Start(10, 0)
            .EllipticalArcTo((-10, 0), 10, 4, rotationDegrees: 30)
            .Close();

        var pdf = new PdfDrawing();
        var report = pdf.Add(sketch, PdfCurveMode.Kappa, tolerance: 0.01);
        Assert.Equal(1, report.ApproximatedSegments);

        // Every decoded curve point lies on the ellipse to the reported deviation: the
        // region's own signed distance is the independent measure.
        var region = new SketchRegion(sketch);
        var subpath = Assert.Single(Assert.Single(PdfReadback.Parse(pdf.ToPdf()).Strokes).Subpaths);
        foreach (var (endIndex, c1, c2) in subpath.Curves)
        {
            var p0 = subpath.Points[endIndex - 1];
            var p3 = subpath.Points[endIndex];
            for (int k = 0; k <= 32; k++)
            {
                double t = k / 32.0, u = 1 - t;
                var point = p0 * (u * u * u) + c1 * (3 * u * u * t) + c2 * (3 * u * t * t) + p3 * (t * t * t);
                Assert.True(Math.Abs(region.SignedDistance(point)) <= report.MaxDeviation + 1e-9,
                    $"a control-mapped point sat {region.SignedDistance(point):G4} off the ellipse");
            }
        }
    }

    [Fact]
    public void ASketchAddIsDeterministicAndRefusesANonPositiveTolerance()
    {
        byte[] Write()
        {
            var pdf = new PdfDrawing();
            pdf.Add(Sketch.RoundedRectangle(40, 24, 6), PdfCurveMode.Kappa);
            return pdf.ToPdf();
        }
        Assert.Equal(Write(), Write());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PdfDrawing().Add(Sketch.Circle(5), PdfCurveMode.Kappa, tolerance: 0));
    }

    [Fact]
    public void ASketchWithHolesWritesEveryLoopAsItsOwnSubpath()
    {
        var sketch = Sketch.Rectangle(60, 40).WithHole(Sketch.Circle(6));
        var pdf = new PdfDrawing();
        pdf.Add(sketch, PdfCurveMode.Kappa);
        var stroke = Assert.Single(PdfReadback.Parse(pdf.ToPdf()).Strokes);
        Assert.Equal(2, stroke.Subpaths.Count);
        Assert.All(stroke.Subpaths, s => Assert.True(s.Closed));
        Assert.Equal(4, stroke.Subpaths[1].Curves.Count);   // the bore, as four cubics
    }

    // --------------------------------------------------------------------- helpers

    /// <summary>One sfnt table's bytes, located by the reader's OWN directory walk —
    /// the subsetter's output checked by something that never saw it.</summary>
    private static byte[] TableOf(byte[] font, string tag)
    {
        int count = (font[4] << 8) | font[5];
        for (int i = 0; i < count; i++)
        {
            int at = 12 + i * 16;
            string name = Encoding.ASCII.GetString(font, at, 4);
            if (name != tag)
                continue;
            int offset = (font[at + 8] << 24) | (font[at + 9] << 16) | (font[at + 10] << 8) | font[at + 11];
            int length = (font[at + 12] << 24) | (font[at + 13] << 16) | (font[at + 14] << 8) | font[at + 15];
            return font[offset..(offset + length)];
        }
        throw new InvalidOperationException($"the subset has no '{tag}' table");
    }
}
