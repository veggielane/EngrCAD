using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests.Text;

/// <summary>
/// Text laid along a curve, and the vertical alignment of a text block. Every expected
/// number is arithmetic on the synthetic font's exactly-known metrics: em 1000, ascender
/// 800, descender -200, 'I' advance 400 with a 100-unit left bearing on a 200-wide,
/// 700-tall bar, 'O' advance 800, space 500. At size 10 the scale is 0.01.
/// </summary>
public class TextOnPathTests
{
    private static readonly TrueTypeFont Font = TrueTypeFont.Load(SyntheticFont.Build());
    private const double Size = 10;

    /// <summary>A counter-clockwise circle: its LEFT normal points at the centre, so
    /// lettering laid on it hangs inward.</summary>
    private static Curve2d Circle(double radius) => Sketch.Circle(radius).ToCurves()[0];

    /// <summary>A clockwise circle: the dial-face case, where the left normal points
    /// outward and the lettering stands on the rim.</summary>
    private static Curve2d ClockwiseCircle(double radius) =>
        new Arc2d(Vector2d.Zero, radius, 0, -2 * Math.PI);

    private static Curve2d Line(double length) => new Line2d(Vector2d.Zero, new Vector2d(length, 0));

    // ---- text on a path ------------------------------------------------------

    /// <summary>
    /// The oracle for "placed rigidly, not bent": a rotation preserves area exactly, so a
    /// glyph laid on a curve must enclose EXACTLY what the same glyph encloses upright.
    /// A warp that followed the curve's curvature would not — an outer stroke stretches
    /// and an inner one compresses — so this is the assertion a distortion cannot pass,
    /// where "the letters look right" would pass either.
    /// </summary>
    [Fact]
    public void OnAPath_PlacesEachGlyphRigidly_SoItsAreaIsUnchanged()
    {
        var upright = TextOutlines.Sketches("IOI", Font, Size);
        var curved = TextOutlines.SketchesOnPath("IOI", Font, Size, Circle(12));

        Assert.Equal(upright.Count, curved.Count);
        for (int i = 0; i < upright.Count; i++)
            Assert.Equal(upright[i].Area(), curved[i].Area(), 12);

        // And it really is curved rather than merely translated: the anchors of "IOI"
        // sit 2, 7.5 and 13.5 along (advances 4, 8, 4 with the I|O pair kerned by -0.5),
        // so on a radius-12 circle the run turns 11.5/12 rad from first to last.
        double turn = Delta(Angle(curved[2]) - Angle(curved[0]));
        Assert.Equal(11.5 / 12, turn, 6);
    }

    /// <summary>
    /// A straight path is the degenerate case, and it must reproduce ordinary layout —
    /// which is what shows the mid-advance anchoring cancels rather than shifting the run
    /// half a letter along.
    /// </summary>
    [Fact]
    public void OnAStraightPath_ReproducesOrdinaryLayout()
    {
        var straight = TextOutlines.Sketches("IOI", Font, Size);
        var onLine = TextOutlines.SketchesOnPath("IOI", Font, Size, Line(100));

        Assert.Equal(straight.Count, onLine.Count);
        for (int i = 0; i < straight.Count; i++)
        {
            Assert.Equal(straight[i].Bounds.Min.X, onLine[i].Bounds.Min.X, 9);
            Assert.Equal(straight[i].Bounds.Max.X, onLine[i].Bounds.Max.X, 9);
            Assert.Equal(straight[i].Bounds.Min.Y, onLine[i].Bounds.Min.Y, 9);
            Assert.Equal(straight[i].Bounds.Max.Y, onLine[i].Bounds.Max.Y, 9);
        }
    }

    /// <summary>
    /// Spacing is ARC LENGTH, not curve parameter. On a circle that is directly checkable:
    /// consecutive anchors subtend exactly advance/radius, whatever the parameterization.
    /// </summary>
    [Fact]
    public void OnAPath_SpacesGlyphsByArcLength()
    {
        const double radius = 20;
        var sketches = TextOutlines.SketchesOnPath("III", Font, Size, Circle(radius));

        Assert.Equal(3, sketches.Count);
        // 'I' advances 4 at this size, so each glyph's centre turns 4/20 rad from the last.
        double expected = 4.0 / radius;
        Assert.Equal(expected, Delta(Angle(sketches[1]) - Angle(sketches[0])), 6);
        Assert.Equal(expected, Delta(Angle(sketches[2]) - Angle(sketches[1])), 6);
    }

    /// <summary>
    /// Every glyph's baseline anchor sits ON the path — the letters ride the curve rather
    /// than a chord of it — and the run stands on the side the path's LEFT normal points
    /// to. Both windings are asserted, because "which side does the lettering go" is a
    /// convention a one-sided test would let drift: the same circle traced the other way
    /// must put the text on the other side, by exactly the same amount.
    /// </summary>
    [Fact]
    public void OnACircle_EveryGlyphSitsOnTheCircle_OnTheSideTheLeftNormalPointsTo()
    {
        const double radius = 15;

        // 'I' spans y in [0, 7] above its baseline and its ink centre sits exactly at its
        // advance midpoint (bearing 1 + half the 2-wide bar = 2 = half of the 4-unit
        // advance), so the anchor is radial and the ink centre lands 3.5 off the circle.
        foreach (var sketch in TextOutlines.SketchesOnPath("IIIIII", Font, Size, Circle(radius)))
            Assert.Equal(radius - 3.5, Radius(sketch), 6);      // CCW: normal points inward

        foreach (var sketch in TextOutlines.SketchesOnPath("IIIIII", Font, Size, ClockwiseCircle(radius)))
            Assert.Equal(radius + 3.5, Radius(sketch), 6);      // CW: normal points outward
    }

    /// <summary>A closed path is a ring: a run may cross the seam, and doing so must not
    /// change the spacing.</summary>
    [Fact]
    public void OnAClosedPath_ARunMayCrossTheSeam()
    {
        const double radius = 20;
        double circumference = 2 * Math.PI * radius;

        // Start 2 units before the seam so "III" (12 wide) straddles it.
        var straddling = TextOutlines.SketchesOnPath(
            "III", Font, Size, Circle(radius), startOffset: circumference - 2);

        Assert.Equal(3, straddling.Count);
        double expected = 4.0 / radius;
        Assert.Equal(expected, Delta(Angle(straddling[1]) - Angle(straddling[0])), 6);
        Assert.Equal(expected, Delta(Angle(straddling[2]) - Angle(straddling[1])), 6);

        foreach (var sketch in straddling)
            Assert.Equal(radius - 3.5, Radius(sketch), 6);
    }

    [Theory]
    [InlineData(TextAlign.Left, 0)]
    [InlineData(TextAlign.Center, -6)]
    [InlineData(TextAlign.Right, -12)]
    public void OnAPath_AlignPlacesTheRunRelativeToTheOffset(TextAlign align, double runStart)
    {
        // "III" is 12 wide; on a straight path the first glyph's ink starts one bearing
        // (1) past wherever the run begins.
        var sketches = TextOutlines.SketchesOnPath(
            "III", Font, Size, new Line2d(new Vector2d(-50, 0), new Vector2d(50, 0)),
            new TextStyle { Align = align }, startOffset: 50);

        Assert.Equal(-50 + 50 + runStart + 1, sketches[0].Bounds.Min.X, 6);
    }

    /// <summary>The block shift rides the same LEFT normal the glyphs stand on, so it
    /// moves the run further from the curve on whichever side it is standing.</summary>
    [Fact]
    public void OnAPath_VerticalAlignLiftsAlongTheNormal()
    {
        const double radius = 20;

        // Bottom puts y = 0 at the descender, so the whole run moves 2 further along the
        // normal at this size — inward on a CCW circle, outward on a CW one.
        var ccwBaseline = TextOutlines.SketchesOnPath("I", Font, Size, Circle(radius));
        var ccwBottom = TextOutlines.SketchesOnPath(
            "I", Font, Size, Circle(radius), new TextStyle { VerticalAlign = TextVerticalAlign.Bottom });
        Assert.Equal(Radius(ccwBaseline[0]) - 2, Radius(ccwBottom[0]), 6);

        var cwBaseline = TextOutlines.SketchesOnPath("I", Font, Size, ClockwiseCircle(radius));
        var cwBottom = TextOutlines.SketchesOnPath(
            "I", Font, Size, ClockwiseCircle(radius), new TextStyle { VerticalAlign = TextVerticalAlign.Bottom });
        Assert.Equal(Radius(cwBaseline[0]) + 2, Radius(cwBottom[0]), 6);
    }

    // ---- upright mode --------------------------------------------------------

    /// <summary>
    /// Upright reproduces ordinary layout on a straight horizontal path exactly as the
    /// rotated form does — on a straight path the two coincide (the tangent IS world +X),
    /// so this pins that upright is a strict superset of the incumbent behaviour on the
    /// degenerate case rather than a second layout.
    /// </summary>
    [Fact]
    public void Upright_OnAStraightPath_ReproducesOrdinaryLayout()
    {
        var straight = TextOutlines.Sketches("IOI", Font, Size);
        var onLine = TextOutlines.SketchesOnPath("IOI", Font, Size, Line(100), upright: true);

        Assert.Equal(straight.Count, onLine.Count);
        for (int i = 0; i < straight.Count; i++)
        {
            Assert.Equal(straight[i].Bounds.Min.X, onLine[i].Bounds.Min.X, 9);
            Assert.Equal(straight[i].Bounds.Max.X, onLine[i].Bounds.Max.X, 9);
            Assert.Equal(straight[i].Bounds.Min.Y, onLine[i].Bounds.Min.Y, 9);
            Assert.Equal(straight[i].Bounds.Max.Y, onLine[i].Bounds.Max.Y, 9);
        }
    }

    /// <summary>
    /// The claim with teeth: a glyph laid UPRIGHT on a curve keeps its axis-aligned
    /// footprint everywhere (the synthetic 'I' bar is 2 wide × 7 tall at this size),
    /// wherever it sits on the ring — where a rotated glyph's box would grow with the tilt.
    /// The rigid placement still preserves area, so both forms enclose the upright area.
    /// </summary>
    [Fact]
    public void Upright_KeepsEachGlyphAxisAligned_WhereverItSitsOnTheRing()
    {
        var upright = TextOutlines.Sketches("I", Font, Size);
        double area = upright[0].Area();

        var laid = TextOutlines.SketchesOnPath("IIIIIIII", Font, Size, Circle(20), upright: true);
        Assert.Equal(8, laid.Count);
        foreach (var sketch in laid)
        {
            Assert.Equal(2.0, sketch.Bounds.Size.X, 9);      // the bar's ink width, un-rotated
            Assert.Equal(7.0, sketch.Bounds.Size.Y, 9);      // the bar's ink height, un-rotated
            Assert.Equal(area, sketch.Area(), 12);           // rigid: area is unchanged
        }

        // And it really is upright rather than rotated: a glyph a quarter of the way round a
        // CCW circle would tilt ~90° if it followed the tangent, so its ROTATED box would be
        // ~7 wide. Upright keeps it 2 wide, which is the difference this mode exists for.
        var rotated = TextOutlines.SketchesOnPath("IIIIIIII", Font, Size, Circle(20));
        Assert.True(rotated.Max(s => s.Bounds.Size.X) > 5,
            "the rotated control must tilt some glyph wide, or the comparison proves nothing");
    }

    /// <summary>Upright still spaces by ARC LENGTH — each glyph's mid-advance BASELINE point
    /// sits ON the path, so consecutive anchors subtend advance/radius. Unlike the rotated
    /// form, the box centre is NOT the anchor here: an upright box reaches straight up from
    /// the baseline, so its centre sits 3.5 above the anchor in WORLD +Y (not radially),
    /// which is exactly the property that distinguishes upright from tilted.</summary>
    [Fact]
    public void Upright_SpacesGlyphsByArcLength_AndAnchorsOnThePath()
    {
        const double radius = 20;
        var sketches = TextOutlines.SketchesOnPath("III", Font, Size, Circle(radius), upright: true);

        Assert.Equal(3, sketches.Count);
        var a0 = Anchor(sketches[0]);
        var a1 = Anchor(sketches[1]);
        var a2 = Anchor(sketches[2]);

        double expected = 4.0 / radius;   // 'I' advances 4 at this size
        Assert.Equal(expected, Delta(Math.Atan2(a1.Y, a1.X) - Math.Atan2(a0.Y, a0.X)), 6);
        Assert.Equal(expected, Delta(Math.Atan2(a2.Y, a2.X) - Math.Atan2(a1.Y, a1.X)), 6);

        // Each anchor lies exactly on the circle the text was laid along.
        foreach (var a in new[] { a0, a1, a2 })
            Assert.Equal(radius, Math.Sqrt(a.X * a.X + a.Y * a.Y), 6);
    }

    /// <summary>End to end through the Shape API: upright extrudes to the same volume as
    /// upright text (a rigid translation preserves the exact section). The run is placed on
    /// the FLAT TOP of the circle, where upright 7-tall bars spaced 4 apart along a nearly
    /// horizontal arc stay disjoint — on the steep side they would legitimately overlap
    /// (they do not rotate away from one another, which is the trade upright makes).</summary>
    [Fact]
    public void ShapeTextOnPath_Upright_ExtrudesToTheSameVolumeAsUprightText()
    {
        const double radius = 40;
        double top = 2 * Math.PI * radius / 4;   // a quarter turn = the top of the circle
        var style = new TextStyle { Align = TextAlign.Center };
        var shape = Shape.TextOnPath("III", Font, Size, height: 3, Circle(radius), style: style,
                                     startOffset: top, upright: true);

        shape.ToBrep().Validate();
        var mesh = shape.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(3 * 14 * 3, mesh.Volume(), 9);   // three 200×700 bars, section 14, height 3
    }

    // ---- refusals ------------------------------------------------------------

    [Fact]
    public void OnAPath_MultiLineRefusesOnlyWhereNoExactOffsetExists()
    {
        // A bezier path has no exact offset; the refusal names the two kinds that do.
        var bezier = new BezierCurve2d(
            new Vector2d(0, 0), new Vector2d(30, 40), new Vector2d(70, -40), new Vector2d(100, 0));
        var error = Assert.Throws<ArgumentException>(
            () => TextOutlines.SketchesOnPath("I\nI", Font, Size, bezier));
        Assert.Contains("Line2d", error.Message);
        Assert.Contains("Arc2d", error.Message);
    }

    /// <summary>
    /// THE reduction: two lines on a straight horizontal path reproduce the ordinary
    /// two-line layout — line 1 rides the path offset one line-height DOWN, which for a
    /// horizontal line is exactly where ordinary layout puts its second baseline.
    /// </summary>
    [Fact]
    public void OnAStraightPath_TwoLinesReproduceOrdinaryLayout()
    {
        var straight = TextOutlines.Sketches("IOI\nOI", Font, Size);
        var onLine = TextOutlines.SketchesOnPath("IOI\nOI", Font, Size, Line(100));

        Assert.Equal(straight.Count, onLine.Count);
        for (int i = 0; i < straight.Count; i++)
        {
            Assert.Equal(straight[i].Bounds.Min.X, onLine[i].Bounds.Min.X, 9);
            Assert.Equal(straight[i].Bounds.Max.X, onLine[i].Bounds.Max.X, 9);
            Assert.Equal(straight[i].Bounds.Min.Y, onLine[i].Bounds.Min.Y, 9);
            Assert.Equal(straight[i].Bounds.Max.Y, onLine[i].Bounds.Max.Y, 9);
        }
    }

    /// <summary>
    /// On a counter-clockwise circle the left normal points INWARD (the recorded
    /// convention), so DOWN is outward: line 1's glyph anchors sit on the concentric
    /// circle of radius r + lineHeight, exactly, each line spaced by its OWN arc
    /// length; and rigid placement keeps every glyph's area, on both rings.
    /// </summary>
    [Fact]
    public void OnACircle_TheSecondLineRidesTheConcentricRing_Exactly()
    {
        const double radius = 40;
        double lineHeight = TextOutlines.LineHeight(Size);
        var oneLine = TextOutlines.SketchesOnPath("II", Font, Size, Circle(radius));
        var twoLines = TextOutlines.SketchesOnPath("II\nII", Font, Size, Circle(radius));

        // Line 0's sketches are bit-identical to the single-line run (the loop's
        // index-0 branch passes the original path object through untouched).
        Assert.True(twoLines.Count > oneLine.Count);
        for (int i = 0; i < oneLine.Count; i++)
        {
            Assert.Equal(oneLine[i].Bounds.Min.X, twoLines[i].Bounds.Min.X);
            Assert.Equal(oneLine[i].Bounds.Min.Y, twoLines[i].Bounds.Min.Y);
        }

        // Every line-1 glyph's ink centre sits EXACTLY a line-height farther from the
        // circle's centre than a line-0 glyph's: the glyph's up is the radial
        // direction, so the constant baseline-to-ink offset cancels in the DIFFERENCE
        // of the two rings — asserting the raw radius would book that offset as error.
        double Ring(Sketch sketch)
        {
            var b = sketch.Bounds;
            return new Vector2d((b.Min.X + b.Max.X) / 2, (b.Min.Y + b.Max.Y) / 2).Length;
        }
        double innerRing = Ring(twoLines[0]);
        for (int i = oneLine.Count; i < twoLines.Count; i++)
            Assert.Equal(innerRing + lineHeight, Ring(twoLines[i]), 6);

        // Rigid placement preserves every glyph's area on both rings.
        double area = TextOutlines.Sketches("I", Font, Size)[0].Area();
        foreach (var sketch in twoLines)
            Assert.Equal(area, sketch.Area(), 12);
    }

    [Fact]
    public void OnAClockwiseArc_AnInnerLinePastTheCentre_IsRefusedByName()
    {
        // Clockwise: left normal outward, so lines go INWARD; a drop past the radius
        // has no concentric arc to sit on.
        var error = Assert.Throws<ArgumentException>(() => TextOutlines.SketchesOnPath(
            "I\nI\nI", Font, Size, ClockwiseCircle(1.5 * TextOutlines.LineHeight(Size))));
        Assert.Contains("centre", error.Message);
    }

    [Fact]
    public void OnAPath_RefusesTextLongerThanThePath_NamingBothLengths()
    {
        // "IIIIII" is 24 wide; a Ø2 circle is 6.28 round.
        var error = Assert.Throws<ArgumentException>(
            () => TextOutlines.SketchesOnPath("IIIIII", Font, Size, Circle(1)));

        Assert.Contains("24", error.Message);
        Assert.Contains("6.28", error.Message);
    }

    [Fact]
    public void OnAnOpenPath_RefusesARunThatWouldOverhangAnEnd()
    {
        // The text fits the 30-long line, but not starting at 25.
        var error = Assert.Throws<ArgumentException>(
            () => TextOutlines.SketchesOnPath("III", Font, Size, Line(30), startOffset: 25));

        Assert.Contains("extrapolated", error.Message);

        // The same run on a CLOSED path of the same length is fine — a ring has no end.
        var wrapped = TextOutlines.SketchesOnPath(
            "III", Font, Size, Circle(30 / (2 * Math.PI)), startOffset: 25);
        Assert.Equal(3, wrapped.Count);
    }

    /// <summary>A run that exactly fills an open path is legal: the fit test carries the
    /// weld tier so an ulp of subtraction round-off cannot refuse it.</summary>
    [Fact]
    public void OnAnOpenPath_ARunThatExactlyFitsIsAccepted()
    {
        var sketches = TextOutlines.SketchesOnPath("III", Font, Size, Line(12));
        Assert.Equal(3, sketches.Count);

        var centred = TextOutlines.SketchesOnPath(
            "III", Font, Size, Line(12), new TextStyle { Align = TextAlign.Center }, startOffset: 6);
        Assert.Equal(3, centred.Count);
    }

    [Fact]
    public void OnAPath_ValidatesItsArguments()
    {
        Assert.Throws<ArgumentNullException>(() => TextOutlines.SketchesOnPath(null!, Font, Size, Circle(20)));
        Assert.Throws<ArgumentNullException>(() => TextOutlines.SketchesOnPath("I", null!, Size, Circle(20)));
        Assert.Throws<ArgumentNullException>(() => TextOutlines.SketchesOnPath("I", Font, Size, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextOutlines.SketchesOnPath("I", Font, 0, Circle(20)));
    }

    // ---- through the Shape API -----------------------------------------------

    /// <summary>
    /// End to end, and the volume is the assertion that matters: a rigid placement of an
    /// exact outline extrudes to EXACTLY the volume the same letters extrude to upright.
    /// The synthetic font's 'I' is a 200x700 bar = 14 square units of section at size 10.
    /// </summary>
    [Fact]
    public void ShapeTextOnPath_ExtrudesToTheSameVolumeAsUprightText()
    {
        var shape = Shape.TextOnPath("III", Font, Size, height: 3, ClockwiseCircle(40));

        shape.ToBrep().Validate();
        var mesh = shape.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(3 * 14 * 3, mesh.Volume(), 9);
    }

    [Fact]
    public void ShapeTextOnPath_ValidatesItsArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Shape.TextOnPath("I", Font, Size, height: 0, Circle(20)));
        Assert.Throws<ArgumentException>(
            () => Shape.TextOnPath("   ", Font, Size, height: 1, Circle(20)));
    }

    // ---- vertical alignment --------------------------------------------------

    /// <summary>
    /// Baseline (the default) must be bit-for-bit what it always was — the shift is not
    /// added at all rather than added as zero, which is what keeps the committed docs
    /// renders and every existing layout number untouched.
    /// </summary>
    [Fact]
    public void VerticalAlign_BaselineIsBitIdenticalToNoStyle()
    {
        var implicitly_ = TextOutlines.Sketches("IOI\nOII", Font, Size);
        var explicitly = TextOutlines.Sketches(
            "IOI\nOII", Font, Size, new TextStyle { VerticalAlign = TextVerticalAlign.Baseline });

        Assert.Equal(implicitly_.Count, explicitly.Count);
        for (int i = 0; i < implicitly_.Count; i++)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(implicitly_[i].Bounds.Min.Y),
                BitConverter.DoubleToInt64Bits(explicitly[i].Bounds.Min.Y));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(implicitly_[i].Bounds.Max.Y),
                BitConverter.DoubleToInt64Bits(explicitly[i].Bounds.Max.Y));
        }
    }

    /// <summary>One line: top = ascender (8), bottom = descender (-2). 'I' inks [0, 7].</summary>
    [Theory]
    [InlineData(TextVerticalAlign.Baseline, 0, 7)]
    [InlineData(TextVerticalAlign.Top, -8, -1)]
    [InlineData(TextVerticalAlign.Bottom, 2, 9)]
    [InlineData(TextVerticalAlign.Middle, -3, 4)]
    public void VerticalAlign_PositionsOneLineFromTheFontsMetrics(
        TextVerticalAlign align, double minY, double maxY)
    {
        var bounds = TextOutlines.Bounds("I", Font, Size, new TextStyle { VerticalAlign = align });

        Assert.Equal(minY, bounds.Min.Y, 9);
        Assert.Equal(maxY, bounds.Max.Y, 9);
    }

    /// <summary>Two lines: the block runs from ascender (8) down to the second baseline
    /// (-12) plus the descender (-2), i.e. -14.</summary>
    [Theory]
    [InlineData(TextVerticalAlign.Baseline, -12, 7)]
    [InlineData(TextVerticalAlign.Top, -20, -1)]
    [InlineData(TextVerticalAlign.Bottom, 2, 21)]
    [InlineData(TextVerticalAlign.Middle, -9, 10)]
    public void VerticalAlign_MeasuresTheWholeBlock(TextVerticalAlign align, double minY, double maxY)
    {
        var bounds = TextOutlines.Bounds("I\nI", Font, Size, new TextStyle { VerticalAlign = align });

        Assert.Equal(minY, bounds.Min.Y, 9);
        Assert.Equal(maxY, bounds.Max.Y, 9);
    }

    /// <summary>
    /// The reason the convention reads the FONT rather than the ink: two labels centred on
    /// one point must line up whatever letters they happen to contain. 'I' reaches the cap
    /// height and 'O' overshoots it, so an ink-measured centring would put them at
    /// different heights.
    /// </summary>
    [Fact]
    public void VerticalAlign_IsIndependentOfWhichLettersAreUsed()
    {
        var style = new TextStyle { VerticalAlign = TextVerticalAlign.Middle };

        var narrow = TextOutlines.Sketches("I", Font, Size, style);
        var wide = TextOutlines.Sketches("O", Font, Size, style);

        // Both glyphs sit on the same baseline: the block shift depends on the font's
        // ascender and descender and on the line count, never on which letters appear.
        // (Both happen to ink from the baseline up in this font, which is what makes the
        // comparison a direct one.)
        Assert.Equal(-3, narrow[0].Bounds.Min.Y, 9);
        Assert.Equal(-3, wide[0].Bounds.Min.Y, 9);

        // The claim with teeth is that the two differ in WIDTH and not in height: an
        // ink-measured centring would have moved the taller of the two.
        Assert.NotEqual(narrow[0].Bounds.Max.X, wide[0].Bounds.Max.X, 3);
        Assert.Equal(narrow[0].Bounds.Max.Y, wide[0].Bounds.Max.Y, 9);
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>The polar angle of a glyph's ink centre about the origin. Every glyph in
    /// these fixtures is a rectangle or a ring, both centrally symmetric, so an axis-aligned
    /// box of the rotated outline is still centred on the outline's own centre.</summary>
    private static double Angle(Sketch sketch) =>
        Math.Atan2(sketch.Bounds.Center.Y, sketch.Bounds.Center.X);

    /// <summary>The mid-advance BASELINE anchor of an UPRIGHT glyph: its box centre lies
    /// 3.5 above the baseline (the synthetic 'I' bar is 7 tall reaching up from y = 0), so
    /// dropping the centre by 3.5 in world +Y recovers the point that sits on the path.</summary>
    private static Vector2d Anchor(Sketch sketch) =>
        new(sketch.Bounds.Center.X, sketch.Bounds.Center.Y - 3.5);

    /// <summary>The distance of a glyph's ink centre from the origin.</summary>
    private static double Radius(Sketch sketch) =>
        Math.Sqrt(sketch.Bounds.Center.X * sketch.Bounds.Center.X
                + sketch.Bounds.Center.Y * sketch.Bounds.Center.Y);

    /// <summary>An angle difference reduced into (-π, π].</summary>
    private static double Delta(double radians)
    {
        while (radians <= -Math.PI) radians += 2 * Math.PI;
        while (radians > Math.PI) radians -= 2 * Math.PI;
        return radians;
    }
}
