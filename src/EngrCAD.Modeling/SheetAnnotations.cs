using EngrCAD.Core;

namespace EngrCAD.Modeling;

// The drafting layer: dimensions, notes and balloons placed on a drawing view.
//
// This is the 2D twin of the 3D PMI in Annotations.cs, and deliberately not a port of
// it: a 3D dimension is billboarded and sized in SCREEN pixels because it must stay
// legible while the camera moves, whereas a sheet dimension is sized in PAPER
// millimetres because it will be printed. What the two share is the ANATOMY — gap,
// overshoot, arrowhead proportions, text standoff — and SheetStyle keeps those as the
// same ratios the viewer's AnnotationGeometry uses, so the two look like one product.

/// <summary>
/// The sheet's drafting style: one text height, and every other length derived from it
/// by the SAME ratios the viewer's 3D annotation overlay uses.
///
/// <para>The default text height is 3.5 mm, ISO 3098's second size and the one general
/// engineering drawings are lettered at. Everything else is a multiple of it, and the
/// multiples are exactly <c>AnnotationGeometry</c>'s pixel constants divided by its
/// 12-pixel text height — restating them as independent millimetre values is how two
/// front ends drift, so they are written here as ratios with their origin named. (A
/// test in the viewer's suite asserts the two agree; it reads both, rather than
/// re-typing either.)</para>
/// </summary>
public sealed record SheetStyle
{
    /// <summary>Cap height of dimension and note text, sheet mm (ISO 3098).</summary>
    public double TextHeight { get; init; } = 3.5;

    // Ratios to the text height, taken from AnnotationGeometry's pixel constants
    // (ArrowLengthPx 10 / TextHeightPx 12, and so on).

    /// <summary>Arrowhead length / text height.</summary>
    public const double ArrowLengthRatio = 10 / 12.0;

    /// <summary>Arrowhead half-width / text height.</summary>
    public const double ArrowHalfWidthRatio = 3.5 / 12.0;

    /// <summary>Model-to-extension-line gap / text height.</summary>
    public const double ExtensionGapRatio = 4 / 12.0;

    /// <summary>Extension line past the dimension line / text height.</summary>
    public const double ExtensionOvershootRatio = 6 / 12.0;

    /// <summary>Line-to-text gap / text height.</summary>
    public const double TextGapRatio = 4 / 12.0;

    /// <summary>Default dimension-line standoff / text height.</summary>
    public const double DefaultOffsetRatio = 40 / 12.0;

    /// <summary>Leader length / text height.</summary>
    public const double LeaderLengthRatio = 36 / 12.0;

    /// <summary>Leader tail length / text height.</summary>
    public const double TailLengthRatio = 14 / 12.0;

    /// <summary>Box padding around text / text height.</summary>
    public const double BoxPaddingRatio = 4 / 12.0;

    /// <summary>Baseline-to-baseline spacing, in text heights.</summary>
    public const double LineSpacing = 1.5;

    /// <summary>Arc chord step of an angular dimension (5 degrees per segment).</summary>
    public const double ArcStepRadians = Math.PI / 36;

    public double ArrowLength => TextHeight * ArrowLengthRatio;
    public double ArrowHalfWidth => TextHeight * ArrowHalfWidthRatio;
    public double ExtensionGap => TextHeight * ExtensionGapRatio;
    public double ExtensionOvershoot => TextHeight * ExtensionOvershootRatio;
    public double TextGap => TextHeight * TextGapRatio;
    public double DefaultOffset => TextHeight * DefaultOffsetRatio;
    public double LeaderLength => TextHeight * LeaderLengthRatio;
    public double TailLength => TextHeight * TailLengthRatio;
    public double BoxPadding => TextHeight * BoxPaddingRatio;

    /// <summary>The style every sheet starts with.</summary>
    public static SheetStyle Default { get; } = new();
}

/// <summary>
/// Lettering on the sheet's FURNITURE — the title block and view labels — as opposed to
/// on its dimensions. Separate from <see cref="SheetStyle"/> because it is a property of
/// the paper rather than of a view: two views at different drawing scales still share
/// one title block, and a per-view style must not be able to change it.
/// </summary>
public static class SheetLettering
{
    /// <summary>Title-block field text height, sheet mm (ISO 3098).</summary>
    public const double TextHeight = 3.5;

    /// <summary>Field LABEL height — the small caption above a value.</summary>
    public const double SmallTextHeight = 2.5;

    /// <summary>The drawing title's height.</summary>
    public const double TitleHeight = 5;

    /// <summary>A view's label height.</summary>
    public const double LabelHeight = 5;

    /// <summary>Gap between a view's line work and its label.</summary>
    public const double LabelGap = 8;

    /// <summary>Inset of title-block text from the block's frame.</summary>
    public const double TitleBlockPadding = 3;
}

/// <summary>
/// Base class for a dimension or note placed on a <see cref="DrawingView"/>.
///
/// <para><b>Anchors are in the view's own projected MODEL coordinates</b> (the same
/// space <see cref="DrawingView.Projected"/> reports), never in sheet millimetres.
/// That is what lets a dimension measure the part rather than the paper: the value is
/// read from the anchors directly, and the view maps them onto the sheet afterwards.
/// Everything about the drawn ANATOMY — arrow size, text height, standoff — is in sheet
/// millimetres instead, because those are properties of the printed page and must not
/// change with the drawing scale.</para>
/// </summary>
public abstract class SheetAnnotation
{
    /// <summary>Text override; without one a dimension formats its measured value.</summary>
    public string? Label { get; init; }

    /// <summary>Tolerance suffix appended to a measured value (ignored under
    /// <see cref="Label"/>, which already controls the whole text).</summary>
    public ToleranceSpec? Tolerance { get; init; }

    /// <summary>Placement standoff in SHEET mm; 0 takes the style's default.</summary>
    public double Offset { get; set; }

    /// <summary>The measured quantity in model units (0 for notes).</summary>
    public virtual double Value => 0;

    /// <summary>The text this annotation shows: the label override, else the formatted
    /// value with any tolerance suffix. A radial dimension overrides it to add its R or
    /// diameter prefix.</summary>
    public virtual string Text => Label ?? Annotation.Format(Value) + (Tolerance?.Suffix ?? "");

    /// <summary>
    /// Builds the annotation's line work and text.
    /// </summary>
    /// <param name="toSheet">Maps a view-local model point onto the sheet.</param>
    /// <param name="style">Paper-millimetre sizes.</param>
    /// <param name="segments">Line work is appended here.</param>
    /// <param name="texts">Text is appended here.</param>
    public abstract void Build(
        Func<Vector2d, Vector2d> toSheet, SheetStyle style,
        List<(Vector2d A, Vector2d B)> segments, List<SheetText> texts);

    /// <summary>A V arrowhead: tip at <paramref name="tip"/>, wings trailing along
    /// <paramref name="direction"/>.</summary>
    private protected static void Arrowhead(
        List<(Vector2d A, Vector2d B)> segments, in Vector2d tip, in Vector2d direction, SheetStyle style)
    {
        var back = tip + direction * style.ArrowLength;
        var wing = new Vector2d(-direction.Y, direction.X) * style.ArrowHalfWidth;
        segments.Add((tip, back + wing));
        segments.Add((tip, back - wing));
    }

    /// <summary>Multi-line text stacked downward from a centre point.</summary>
    private protected static void CenteredText(
        List<SheetText> texts, in Vector2d center, string text, SheetStyle style, double rotation = 0)
    {
        string[] lines = text.Split('\n');
        double step = style.TextHeight * SheetStyle.LineSpacing;
        double blockHeight = style.TextHeight + (lines.Length - 1) * step;
        for (int i = 0; i < lines.Length; i++)
        {
            var position = center + new Vector2d(0, blockHeight / 2 - style.TextHeight - i * step);
            texts.Add(new SheetText(
                position, lines[i], style.TextHeight, SheetTextAnchor.Center, SheetLayers.Dimensions));
        }
        _ = rotation;
    }
}

/// <summary>
/// The classic linear dimension: extension lines from the two anchors, a dimension line
/// between them with inward arrowheads, and the measured value centred above it.
/// Aligned by default (the dimension line runs parallel to the measured span);
/// <see cref="Horizontal"/> and <see cref="Vertical"/> give the ordinate flavours.
/// </summary>
public sealed class SheetLinearDimension : SheetAnnotation
{
    private readonly Vector2d _a;
    private readonly Vector2d _b;
    private readonly Vector2d? _direction;

    /// <summary>Between two points in the view's projected model coordinates.</summary>
    /// <param name="a">First anchor.</param>
    /// <param name="b">Second anchor.</param>
    /// <param name="offset">Standoff in SHEET mm; positive is to the left of a→b.</param>
    public SheetLinearDimension(in Vector2d a, in Vector2d b, double offset = 0)
    {
        _a = a;
        _b = b;
        Offset = offset;
    }

    private SheetLinearDimension(in Vector2d a, in Vector2d b, in Vector2d direction, double offset)
        : this(a, b, offset)
    {
        _direction = direction;
    }

    /// <summary>Measures only the x separation, dimension line horizontal.</summary>
    public static SheetLinearDimension Horizontal(in Vector2d a, in Vector2d b, double offset = 0) =>
        new(a, b, new Vector2d(1, 0), offset);

    /// <summary>Measures only the y separation, dimension line vertical.</summary>
    public static SheetLinearDimension Vertical(in Vector2d a, in Vector2d b, double offset = 0) =>
        new(a, b, new Vector2d(0, 1), offset);

    /// <inheritdoc/>
    public override double Value => _direction is { } d
        ? Math.Abs((_b - _a).Dot(d))
        : (_b - _a).Length;

    /// <inheritdoc/>
    public override void Build(
        Func<Vector2d, Vector2d> toSheet, SheetStyle style,
        List<(Vector2d A, Vector2d B)> segments, List<SheetText> texts)
    {
        ArgumentNullException.ThrowIfNull(toSheet);
        ArgumentNullException.ThrowIfNull(style);
        var a = toSheet(_a);
        var b = toSheet(_b);
        var along = _direction ?? (b - a);
        if (!along.TryNormalize(EngrCAD.Core.Tolerance.Default, out var measure))
        {
            CenteredText(texts, a, Text, style);
            return;
        }

        // A horizontal or vertical dimension measures ALONG its own direction, so the
        // extension lines run from each anchor to the dimension line's own level: the
        // anchors keep their own perpendicular positions and the dimension line does
        // not.
        var perpendicular = new Vector2d(-measure.Y, measure.X);
        double standoff = Offset != 0 ? Offset : style.DefaultOffset;
        // A negative standoff simply places the line on the other side; the sign lives
        // in the vector rather than in a branch.
        double baseline = Math.Max(a.Dot(perpendicular), b.Dot(perpendicular));
        var a2 = a + perpendicular * (baseline - a.Dot(perpendicular) + standoff);
        var b2 = b + perpendicular * (baseline - b.Dot(perpendicular) + standoff);
        var side = standoff >= 0 ? perpendicular : -perpendicular;

        segments.Add((a + side * style.ExtensionGap, a2 + side * style.ExtensionOvershoot));
        segments.Add((b + side * style.ExtensionGap, b2 + side * style.ExtensionOvershoot));
        segments.Add((a2, b2));
        Arrowhead(segments, a2, measure, style);
        Arrowhead(segments, b2, -measure, style);

        var mid = (a2 + b2) * 0.5;
        CenteredText(texts, mid + side * (style.TextGap + style.TextHeight * 0.5), Text, style);
    }
}

/// <summary>
/// A radius or diameter dimension on a circular feature: an arrow touching the circle
/// pointing at its centre, a leader out to a short horizontal tail, and the value with
/// its R or diameter prefix.
/// </summary>
public sealed class SheetRadialDimension : SheetAnnotation
{
    private readonly Vector2d _center;
    private readonly double _radius;
    private readonly double _angle;
    private readonly bool _diameter;

    /// <param name="center">Circle centre, view-local model coordinates.</param>
    /// <param name="radius">Circle radius, model units.</param>
    /// <param name="angleDegrees">Where on the circle the arrow touches.</param>
    /// <param name="diameter">Dimension the diameter rather than the radius.</param>
    public SheetRadialDimension(
        in Vector2d center, double radius, double angleDegrees = 45, bool diameter = false)
    {
        _center = center;
        _radius = radius > 0
            ? radius
            : throw new ArgumentOutOfRangeException(nameof(radius), "A radius must be positive.");
        _angle = angleDegrees * Math.PI / 180;
        _diameter = diameter;
    }

    /// <summary>Diameter form, the usual one for a hole.</summary>
    public static SheetRadialDimension Diameter(in Vector2d center, double radius, double angleDegrees = 45) =>
        new(center, radius, angleDegrees, diameter: true);

    /// <inheritdoc/>
    public override double Value => _diameter ? 2 * _radius : _radius;

    /// <summary>"R12" or the diameter sign and the value; a label overrides it. The
    /// diameter sign is a unicode ESCAPE so this file stays pure ASCII, the same rule
    /// <c>HoleCallout</c> follows.</summary>
    public override string Text => Label
        ?? (_diameter ? "\u2300" : "R") + Annotation.Format(Value) + (Tolerance?.Suffix ?? "");

    /// <inheritdoc/>
    public override void Build(
        Func<Vector2d, Vector2d> toSheet, SheetStyle style,
        List<(Vector2d A, Vector2d B)> segments, List<SheetText> texts)
    {
        ArgumentNullException.ThrowIfNull(toSheet);
        ArgumentNullException.ThrowIfNull(style);
        var radial = new Vector2d(Math.Cos(_angle), Math.Sin(_angle));
        var center = toSheet(_center);
        var onCircle = toSheet(_center + radial * _radius);
        if (!(onCircle - center).TryNormalize(EngrCAD.Core.Tolerance.Default, out var outward))
            outward = radial;

        Arrowhead(segments, onCircle, outward, style);
        double leader = Offset != 0 ? Offset : style.LeaderLength;
        var elbow = onCircle + outward * leader;
        segments.Add((onCircle, elbow));

        double side = outward.X < 0 ? -1 : 1;
        var tail = elbow + new Vector2d(side * style.TailLength, 0);
        segments.Add((elbow, tail));
        texts.Add(new SheetText(
            tail + new Vector2d(side * style.TextGap, -style.TextHeight * 0.5),
            Text, style.TextHeight,
            side > 0 ? SheetTextAnchor.Left : SheetTextAnchor.Right, SheetLayers.Dimensions));
    }
}

/// <summary>
/// An angular dimension: extension rays from a vertex through two points, an arc
/// between them with arrowheads, and the angle in degrees outside the arc's midpoint.
/// </summary>
public sealed class SheetAngularDimension : SheetAnnotation
{
    private readonly Vector2d _vertex;
    private readonly Vector2d _a;
    private readonly Vector2d _b;

    public SheetAngularDimension(in Vector2d vertex, in Vector2d a, in Vector2d b)
    {
        _vertex = vertex;
        _a = a;
        _b = b;
    }

    /// <summary>The included angle in DEGREES (an angle is dimensionless, so unlike
    /// every other annotation here its value does not change with the drawing scale).</summary>
    public override double Value
    {
        get
        {
            var da = (_a - _vertex).Normalized(EngrCAD.Core.Tolerance.Default);
            var db = (_b - _vertex).Normalized(EngrCAD.Core.Tolerance.Default);
            return Math.Acos(Math.Clamp(da.Dot(db), -1, 1)) * 180 / Math.PI;
        }
    }

    /// <inheritdoc/>
    public override void Build(
        Func<Vector2d, Vector2d> toSheet, SheetStyle style,
        List<(Vector2d A, Vector2d B)> segments, List<SheetText> texts)
    {
        ArgumentNullException.ThrowIfNull(toSheet);
        ArgumentNullException.ThrowIfNull(style);
        var vertex = toSheet(_vertex);
        var a = toSheet(_a);
        var b = toSheet(_b);
        if (!(a - vertex).TryNormalize(EngrCAD.Core.Tolerance.Default, out var dirA)
            || !(b - vertex).TryNormalize(EngrCAD.Core.Tolerance.Default, out var dirB))
        {
            CenteredText(texts, vertex, Text + "\u00B0", style);
            return;
        }

        double startAngle = Math.Atan2(dirA.Y, dirA.X);
        double sweep = Math.Atan2(dirB.Y, dirB.X) - startAngle;
        // Take the SHORT way round: the included angle is what a drafter dimensions.
        while (sweep > Math.PI)
            sweep -= 2 * Math.PI;
        while (sweep < -Math.PI)
            sweep += 2 * Math.PI;

        double radius = Offset != 0
            ? Offset
            : Math.Min((a - vertex).Length, (b - vertex).Length) * 0.75;
        segments.Add((vertex + dirA * style.ExtensionGap, vertex + dirA * (radius + style.ExtensionOvershoot)));
        segments.Add((vertex + dirB * style.ExtensionGap, vertex + dirB * (radius + style.ExtensionOvershoot)));

        int steps = Math.Max(4, (int)Math.Ceiling(Math.Abs(sweep) / SheetStyle.ArcStepRadians));
        var previous = vertex + dirA * radius;
        for (int i = 1; i <= steps; i++)
        {
            double angle = startAngle + sweep * i / steps;
            var point = vertex + new Vector2d(Math.Cos(angle), Math.Sin(angle)) * radius;
            segments.Add((previous, point));
            previous = point;
        }

        // Arrowheads tangent to the arc at both ends.
        var tangentA = new Vector2d(-dirA.Y, dirA.X) * Math.Sign(sweep);
        var tangentB = new Vector2d(-dirB.Y, dirB.X) * Math.Sign(sweep);
        Arrowhead(segments, vertex + dirA * radius, tangentA, style);
        Arrowhead(segments, vertex + dirB * radius, -tangentB, style);

        double midAngle = startAngle + sweep * 0.5;
        var midDir = new Vector2d(Math.Cos(midAngle), Math.Sin(midAngle));
        CenteredText(
            texts, vertex + midDir * (radius + style.TextGap + style.TextHeight * 0.7),
            Text + "\u00B0", style);
    }
}

/// <summary>A leader note: an arrow at a point, a leader to a short tail, and free text.</summary>
public sealed class SheetNote : SheetAnnotation
{
    private readonly Vector2d _anchor;
    private readonly Vector2d _leader;

    /// <param name="anchor">What the note points at, view-local model coordinates.</param>
    /// <param name="leader">Leader vector in SHEET mm; zero takes an up-right default.</param>
    /// <param name="text">The note.</param>
    public SheetNote(in Vector2d anchor, in Vector2d leader, string text)
    {
        _anchor = anchor;
        _leader = leader;
        Label = text ?? throw new ArgumentNullException(nameof(text));
    }

    /// <inheritdoc/>
    public override void Build(
        Func<Vector2d, Vector2d> toSheet, SheetStyle style,
        List<(Vector2d A, Vector2d B)> segments, List<SheetText> texts)
    {
        ArgumentNullException.ThrowIfNull(toSheet);
        ArgumentNullException.ThrowIfNull(style);
        var anchor = toSheet(_anchor);
        var leader = _leader.LengthSquared > 0
            ? _leader
            : new Vector2d(1, 1).Normalized(EngrCAD.Core.Tolerance.Default) * style.LeaderLength;
        var elbow = anchor + leader;
        Arrowhead(segments, anchor, leader.Normalized(EngrCAD.Core.Tolerance.Default), style);
        segments.Add((anchor, elbow));

        double side = leader.X < 0 ? -1 : 1;
        var tail = elbow + new Vector2d(side * style.TailLength, 0);
        segments.Add((elbow, tail));

        string[] lines = Text.Split('\n');
        double step = style.TextHeight * SheetStyle.LineSpacing;
        for (int i = 0; i < lines.Length; i++)
        {
            texts.Add(new SheetText(
                tail + new Vector2d(side * style.TextGap, -style.TextHeight * 0.5 - i * step),
                lines[i], style.TextHeight,
                side > 0 ? SheetTextAnchor.Left : SheetTextAnchor.Right, SheetLayers.Dimensions));
        }
    }
}

/// <summary>
/// A BOM balloon: a circled item number on a leader. The circle's diameter is fixed in
/// sheet millimetres so every balloon on a drawing is the same size whatever it labels.
/// </summary>
public sealed class SheetBalloon : SheetAnnotation
{
    private readonly Vector2d _anchor;
    private readonly Vector2d _leader;

    public SheetBalloon(in Vector2d anchor, in Vector2d leader, string item)
    {
        _anchor = anchor;
        _leader = leader;
        Label = item ?? throw new ArgumentNullException(nameof(item));
    }

    /// <summary>Balloon circle diameter in text heights (ISO 7573's balloons are about
    /// twice the lettering height).</summary>
    public const double DiameterRatio = 2.4;

    /// <summary>Segments approximating the balloon circle.</summary>
    public const int CircleSegments = 32;

    /// <inheritdoc/>
    public override void Build(
        Func<Vector2d, Vector2d> toSheet, SheetStyle style,
        List<(Vector2d A, Vector2d B)> segments, List<SheetText> texts)
    {
        ArgumentNullException.ThrowIfNull(toSheet);
        ArgumentNullException.ThrowIfNull(style);
        var anchor = toSheet(_anchor);
        var leader = _leader.LengthSquared > 0
            ? _leader
            : new Vector2d(1, 1).Normalized(EngrCAD.Core.Tolerance.Default) * style.LeaderLength;
        double radius = style.TextHeight * DiameterRatio / 2;
        var centre = anchor + leader + leader.Normalized(EngrCAD.Core.Tolerance.Default) * radius;

        Arrowhead(segments, anchor, leader.Normalized(EngrCAD.Core.Tolerance.Default), style);
        segments.Add((anchor, anchor + leader));

        var previous = centre + new Vector2d(radius, 0);
        for (int i = 1; i <= CircleSegments; i++)
        {
            double angle = 2 * Math.PI * i / CircleSegments;
            var point = centre + new Vector2d(Math.Cos(angle), Math.Sin(angle)) * radius;
            segments.Add((previous, point));
            previous = point;
        }
        texts.Add(new SheetText(
            centre - new Vector2d(0, style.TextHeight * 0.5), Text, style.TextHeight,
            SheetTextAnchor.Center, SheetLayers.Dimensions));
    }
}
