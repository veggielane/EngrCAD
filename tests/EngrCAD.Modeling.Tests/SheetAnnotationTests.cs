using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The drafting layer: dimension layout math on a sheet. The load-bearing property
/// throughout is the SPLIT between the two unit systems — anchors and measured values
/// are in MODEL units, the drawn anatomy is in PAPER millimetres — because that is what
/// makes a dimension read the part while its arrowheads stay printable.
/// </summary>
public class SheetAnnotationTests
{
    private static readonly SheetStyle Style = SheetStyle.Default;

    /// <summary>A 1:2 view centred at (100, 100) on the sheet.</summary>
    private static Func<Vector2d, Vector2d> HalfScale => p => p * 0.5 + new Vector2d(100, 100);

    private static (List<(Vector2d A, Vector2d B)> Segments, List<SheetText> Texts) Build(
        SheetAnnotation annotation, Func<Vector2d, Vector2d>? map = null)
    {
        var segments = new List<(Vector2d A, Vector2d B)>();
        var texts = new List<SheetText>();
        annotation.Build(map ?? (p => p), Style, segments, texts);
        return (segments, texts);
    }

    // ---- what a dimension measures ----

    /// <summary>
    /// The value is the MODEL distance and the drawing scale cannot touch it. This is
    /// the whole reason anchors are in projected model coordinates rather than sheet
    /// millimetres: a dimension that shrank with the paper would be worse than no
    /// dimension.
    /// </summary>
    [Fact]
    public void ValueIsTheModelDistance_WhateverTheDrawingScale()
    {
        var dimension = new SheetLinearDimension(new Vector2d(-20, 0), new Vector2d(20, 0));
        Assert.Equal(40, dimension.Value, 12);
        Assert.Equal("40", dimension.Text);

        // Drawn at 1:2 the anatomy halves its POSITIONS but the text does not change.
        var (_, texts) = Build(dimension, HalfScale);
        Assert.Equal("40", Assert.Single(texts).Text);
    }

    /// <summary>The arrowheads are paper-sized: the same length whatever the view scale,
    /// because a 3 mm arrow is 3 mm on the printed sheet.</summary>
    [Fact]
    public void ArrowheadsAreSizedInPaperMillimetres()
    {
        var dimension = new SheetLinearDimension(new Vector2d(-20, 0), new Vector2d(20, 0), 10);
        var full = Build(dimension).Segments;
        var half = Build(dimension, HalfScale).Segments;

        // The arrow wings are the segments touching the dimension line's ends; their
        // length is set by the style alone.
        double expected = Math.Sqrt(
            Style.ArrowLength * Style.ArrowLength + Style.ArrowHalfWidth * Style.ArrowHalfWidth);
        double Wing(List<(Vector2d A, Vector2d B)> segments) =>
            segments.Min(s => (s.B - s.A).Length);

        Assert.Equal(expected, Wing(full), 9);
        Assert.Equal(expected, Wing(half), 9);
    }

    [Fact]
    public void HorizontalAndVerticalMeasureOneComponentEach()
    {
        var a = new Vector2d(0, 0);
        var b = new Vector2d(30, 40);
        Assert.Equal(50, new SheetLinearDimension(a, b).Value, 12);
        Assert.Equal(30, SheetLinearDimension.Horizontal(a, b).Value, 12);
        Assert.Equal(40, SheetLinearDimension.Vertical(a, b).Value, 12);
    }

    // ---- the classic anatomy ----

    /// <summary>
    /// Two extension lines, one dimension line, four arrowhead wings: seven segments and
    /// no more. Counting them is what catches a leaked duplicate or a missing leg.
    /// </summary>
    [Fact]
    public void LinearDimensionHasTheClassicAnatomy()
    {
        var dimension = new SheetLinearDimension(new Vector2d(0, 0), new Vector2d(40, 0), 10);
        var (segments, texts) = Build(dimension);

        Assert.Equal(7, segments.Count);
        Assert.Single(texts);

        // The dimension line itself sits one standoff above the anchors and spans them.
        var dimensionLine = segments.OrderByDescending(s => (s.B - s.A).Length).First();
        Assert.Equal(40, (dimensionLine.B - dimensionLine.A).Length, 9);
        Assert.Equal(10, dimensionLine.A.Y, 9);
        Assert.Equal(10, dimensionLine.B.Y, 9);

        // Extension lines leave a gap at the model and overshoot past the line.
        var extensions = segments.Where(s => Math.Abs(s.A.X - s.B.X) < 1e-9
                                          && Math.Abs(s.A.Y - s.B.Y) > 1e-9).ToList();
        Assert.Equal(2, extensions.Count);
        foreach (var extension in extensions)
        {
            Assert.Equal(Style.ExtensionGap, Math.Min(extension.A.Y, extension.B.Y), 9);
            Assert.Equal(10 + Style.ExtensionOvershoot, Math.Max(extension.A.Y, extension.B.Y), 9);
        }

        // Text stands off the dimension line by the gap plus half its own height.
        Assert.Equal(20, texts[0].Position.X, 9);
        Assert.True(texts[0].Position.Y > 10, "the value sits above its dimension line");
    }

    /// <summary>A negative standoff puts the dimension line on the other side, and the
    /// gap/overshoot follow it — the sign lives in the vector, not in a branch.</summary>
    [Fact]
    public void NegativeOffsetPlacesTheDimensionOnTheOtherSide()
    {
        var dimension = new SheetLinearDimension(new Vector2d(0, 0), new Vector2d(40, 0), -10);
        var (segments, texts) = Build(dimension);

        var dimensionLine = segments.OrderByDescending(s => (s.B - s.A).Length).First();
        Assert.Equal(-10, dimensionLine.A.Y, 9);
        Assert.True(texts[0].Position.Y < -10, "the value follows its dimension line");
    }

    [Fact]
    public void DefaultOffsetComesFromTheStyle()
    {
        var (segments, _) = Build(new SheetLinearDimension(new Vector2d(0, 0), new Vector2d(40, 0)));
        var dimensionLine = segments.OrderByDescending(s => (s.B - s.A).Length).First();
        Assert.Equal(Style.DefaultOffset, dimensionLine.A.Y, 9);
    }

    [Fact]
    public void LabelAndToleranceControlTheText()
    {
        Assert.Equal("40 ±0.1", new SheetLinearDimension(new Vector2d(0, 0), new Vector2d(40, 0))
        {
            Tolerance = ToleranceSpec.Symmetric(0.1),
        }.Text);
        Assert.Equal("SEE NOTE 3", new SheetLinearDimension(new Vector2d(0, 0), new Vector2d(40, 0))
        {
            Label = "SEE NOTE 3",
            Tolerance = ToleranceSpec.Symmetric(0.1),
        }.Text);
    }

    // ---- radial, angular, notes, balloons ----

    [Fact]
    public void RadialDimensionsCarryTheirPrefix()
    {
        Assert.Equal("R6", new SheetRadialDimension(Vector2d.Zero, 6).Text);
        Assert.Equal("⌀12", SheetRadialDimension.Diameter(Vector2d.Zero, 6).Text);
        Assert.Equal(6, new SheetRadialDimension(Vector2d.Zero, 6).Value, 12);
        Assert.Equal(12, SheetRadialDimension.Diameter(Vector2d.Zero, 6).Value, 12);
    }

    /// <summary>The arrow touches the circle at the requested angle and the leader runs
    /// outward from there, so the annotation points at the feature it names.</summary>
    [Fact]
    public void RadialLeaderStartsOnTheCircle()
    {
        var dimension = SheetRadialDimension.Diameter(new Vector2d(10, 10), 5, angleDegrees: 0);
        var (segments, texts) = Build(dimension);

        // The leader is the segment from the circle outward; its start is at (15, 10).
        Assert.Contains(segments, s =>
            Math.Abs(s.A.X - 15) < 1e-9 && Math.Abs(s.A.Y - 10) < 1e-9);
        Assert.Equal("⌀10", Assert.Single(texts).Text);
    }

    [Theory]
    [InlineData(1, 0, 0, 1, 90)]
    [InlineData(1, 0, 1, 1, 45)]
    [InlineData(1, 0, -1, 0, 180)]
    public void AngularDimensionMeasuresTheIncludedAngle(
        double ax, double ay, double bx, double by, double expected)
    {
        var dimension = new SheetAngularDimension(
            Vector2d.Zero, new Vector2d(ax, ay), new Vector2d(bx, by));
        Assert.Equal(expected, dimension.Value, 9);
        Assert.Equal($"{expected:0.###}°", dimension.Text + "°");
    }

    [Fact]
    public void AngularDimensionDrawsRaysAnArcAndArrowheads()
    {
        var dimension = new SheetAngularDimension(
            Vector2d.Zero, new Vector2d(20, 0), new Vector2d(0, 20));
        var (segments, texts) = Build(dimension);

        // Two extension rays + at least four arc chords + four arrow wings.
        Assert.True(segments.Count >= 2 + 4 + 4, $"only {segments.Count} segments");
        Assert.Contains("90", Assert.Single(texts).Text);
    }

    [Fact]
    public void BalloonDrawsItsCircleAndNumber()
    {
        var balloon = new SheetBalloon(new Vector2d(5, 5), new Vector2d(10, 10), "3");
        var (segments, texts) = Build(balloon);

        // Leader + two arrow wings + the circle's chords.
        Assert.Equal(3 + SheetBalloon.CircleSegments, segments.Count);
        Assert.Equal("3", Assert.Single(texts).Text);

        // Every circle chord is the same distance from the balloon's centre, which is
        // where the leader ends plus one radius along it.
        double radius = Style.TextHeight * SheetBalloon.DiameterRatio / 2;
        var centre = new Vector2d(5, 5) + new Vector2d(10, 10)
            + new Vector2d(10, 10).Normalized() * radius;
        var chords = segments.Skip(3).ToList();
        Assert.All(chords, s => Assert.Equal(radius, (s.A - centre).Length, 9));
    }

    [Fact]
    public void NoteDrawsALeaderATailAndItsText()
    {
        var note = new SheetNote(new Vector2d(0, 0), new Vector2d(10, 10), "DEBURR ALL EDGES");
        var (segments, texts) = Build(note);

        Assert.Equal(4, segments.Count);   // two arrow wings + leader + tail
        Assert.Equal("DEBURR ALL EDGES", Assert.Single(texts).Text);
        Assert.Equal(SheetTextAnchor.Left, texts[0].Anchor);
    }

    /// <summary>A leader leaning LEFT puts its tail and text on the left, so the text
    /// never runs back across the thing it points at.</summary>
    [Fact]
    public void LeftLeaningLeaderPutsItsTextOnTheLeft()
    {
        var note = new SheetNote(new Vector2d(0, 0), new Vector2d(-10, 10), "TYP");
        var (_, texts) = Build(note);
        Assert.Equal(SheetTextAnchor.Right, texts[0].Anchor);
        Assert.True(texts[0].Position.X < -10);
    }

    [Fact]
    public void MultiLineNotesStackDownward()
    {
        var note = new SheetNote(new Vector2d(0, 0), new Vector2d(10, 10), "M6 x 1\nTHRU");
        var (_, texts) = Build(note);
        Assert.Equal(2, texts.Count);
        Assert.Equal(Style.TextHeight * SheetStyle.LineSpacing,
            texts[0].Position.Y - texts[1].Position.Y, 9);
    }

    // ---- placement on a view ----

    /// <summary>Annotations ride the view's own placement, so moving or rescaling a view
    /// carries its dimensions with it — and the values do not change.</summary>
    [Fact]
    public void AnnotationsFollowTheirView()
    {
        var part = new Part("plate", Shape.Box(60, 40, 12));
        var view = new DrawingView(part, StandardViews.DirectionFor("front")!.Value)
        {
            Scale = 1,
            Center = new Vector2d(50, 50),
        };
        var dimension = view.Annotate(
            SheetLinearDimension.Horizontal(new Vector2d(-30, -6), new Vector2d(30, -6), -12));

        var atOne = view.Compute();
        Assert.Equal("60", atOne.Texts.Single(t => t.Text == "60").Text);
        double xAtOne = atOne.Texts.Single(t => t.Text == "60").Position.X;

        view.Scale = 0.5;
        view.Center = new Vector2d(150, 50);
        var atHalf = view.Compute();
        Assert.Equal(60, dimension.Value, 12);
        Assert.Equal(xAtOne + 100, atHalf.Texts.Single(t => t.Text == "60").Position.X, 6);
    }
}
