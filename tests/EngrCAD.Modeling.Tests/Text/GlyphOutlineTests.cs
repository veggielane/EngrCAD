using EngrCAD.Core;
using EngrCAD.Modeling.Text;
using Xunit;

namespace EngrCAD.Modeling.Tests.Text;

/// <summary>
/// Glyph contours to <see cref="Sketch"/>es, against the synthetic font's exactly known
/// outlines. Areas are analytic ground truth: rectangles multiply out, and a quadratic
/// Bézier encloses exactly two thirds of its control triangle over its chord — so a
/// mis-reconstructed implied on-curve point cannot pass.
/// </summary>
public class GlyphOutlineTests
{
    private static readonly TrueTypeFont Font = TrueTypeFont.Load(SyntheticFont.Build());

    /// <summary>Em size equal to unitsPerEm, so sketch units ARE font units.</summary>
    private const double UnitScale = SyntheticFont.UnitsPerEm;

    [Fact]
    public void Rectangle_BecomesOneSketchWithTheExactArea()
    {
        var sketches = TextOutlines.GlyphSketches(Font, 'I', UnitScale);

        var sketch = Assert.Single(sketches);
        Assert.Equal(200 * 700, sketch.Area(), 9);
        Assert.Equal(100, sketch.Bounds.Min.X, 9);
        Assert.Equal(0, sketch.Bounds.Min.Y, 9);
        Assert.Equal(300, sketch.Bounds.Max.X, 9);
        Assert.Equal(700, sketch.Bounds.Max.Y, 9);
    }

    [Fact]
    public void Counter_BecomesAHoleOfItsOutline_NotASecondSketch()
    {
        var sketches = TextOutlines.GlyphSketches(Font, 'O', UnitScale);

        // Outer 700x700 minus the 300x300 counter: one sketch, not two.
        var sketch = Assert.Single(sketches);
        Assert.Equal(700 * 700 - 300 * 300, sketch.Area(), 9);

        var region = sketch.ToRegion();
        Assert.True(region.SignedDistance(new Vector2d(100, 350)) < 0, "the ring wall should be material");
        Assert.True(region.SignedDistance(new Vector2d(350, 350)) > 0, "the counter should be empty");
        Assert.True(region.SignedDistance(new Vector2d(-50, 350)) > 0, "outside the glyph should be empty");
    }

    [Fact]
    public void HoleClassification_IgnoresContourOrientation()
    {
        // 'I' is wound counter-clockwise and 'O' clockwise-outer/counter-clockwise-inner
        // in the synthetic font: deliberately inconsistent, because real fonts are.
        // Both still come out right, which is only possible if classification is by
        // containment rather than by TrueType's winding convention.
        Assert.True(TextOutlines.GlyphSketches(Font, 'I', UnitScale)[0].Area() > 0);
        Assert.True(TextOutlines.GlyphSketches(Font, 'O', UnitScale)[0].Area() > 0);
    }

    [Fact]
    public void ImpliedOnCurvePoints_AreReconstructedBetweenOffCurvePairs()
    {
        var sketch = Assert.Single(TextOutlines.GlyphSketches(Font, 'C', UnitScale));

        // The exact enclosed area of two quadratics plus a closing line.
        Assert.Equal(SyntheticFont.CurveGlyphArea, sketch.Area(), 6);

        // (400, 200) is the midpoint of the two consecutive off-curve points, so it is
        // an on-curve point of the outline: the region's distance there must be zero.
        // Treating the pair as a single control point (the classic bug) would move the
        // curve well away from it.
        var region = sketch.ToRegion();
        Assert.Equal(0, region.SignedDistance(new Vector2d(400, 200)), 9);
        Assert.True(region.SignedDistance(new Vector2d(100, 200)) < 0, "the interior should be material");
    }

    [Fact]
    public void CompositeGlyph_YieldsOneSketchPerDisjointComponent()
    {
        var sketches = TextOutlines.GlyphSketches(Font, 'A', UnitScale);

        Assert.Equal(2, sketches.Count);
        Assert.Equal(200 * 700 + 100 * 350, sketches.Sum(s => s.Area()), 9);
    }

    [Fact]
    public void Size_ScalesOutlinesByEmFraction()
    {
        const double size = 10;
        var sketch = Assert.Single(TextOutlines.GlyphSketches(Font, 'I', size));

        double scale = size / SyntheticFont.UnitsPerEm;
        Assert.Equal(200 * 700 * scale * scale, sketch.Area(), 9);
        Assert.Equal(700 * scale, sketch.Bounds.Max.Y, 9);
    }

    [Fact]
    public void BlankGlyph_YieldsNoGeometry()
    {
        Assert.Empty(TextOutlines.GlyphSketches(Font, ' ', 10));
    }

    [Fact]
    public void MissingGlyph_IsReportedByName()
    {
        var error = Assert.Throws<ArgumentException>(() => TextOutlines.GlyphSketches(Font, 'Z', 10));

        Assert.Contains("'Z'", error.Message);
        Assert.Contains("U+005A", error.Message);
        Assert.Contains(SyntheticFont.FamilyName, error.Message);
    }

    [Fact]
    public void GlyphSketches_ValidatesItsArguments()
    {
        Assert.Throws<ArgumentNullException>(() => TextOutlines.GlyphSketches(null!, 'I', 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextOutlines.GlyphSketches(Font, 'I', 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextOutlines.GlyphSketches(Font, 'I', -1));
    }
}
