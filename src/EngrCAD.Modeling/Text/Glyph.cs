using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// One point of a TrueType glyph contour, in font units (the em square, y up).
/// <see cref="OnCurve"/> points lie on the outline; off-curve points are quadratic
/// Bézier control points. Two consecutive off-curve points imply an on-curve point at
/// their midpoint — the format's classic space-saving trick, reconstructed by
/// <see cref="TextOutlines"/>.
/// </summary>
public readonly record struct GlyphPoint(Vector2d Position, bool OnCurve);

/// <summary>One closed contour of a glyph outline: the points exactly as the font
/// stores them (composite components already transformed into place).</summary>
public sealed class GlyphContour(IReadOnlyList<GlyphPoint> points)
{
    /// <summary>The contour's points in font units, in font order; the contour closes
    /// from the last point back to the first.</summary>
    public IReadOnlyList<GlyphPoint> Points { get; } = points;
}

/// <summary>
/// A parsed glyph outline in <em>font units</em> (divide by
/// <see cref="TrueTypeFont.UnitsPerEm"/> to get em fractions). The origin is on the
/// baseline at the glyph's pen position; +y is up.
/// <para>Contours are unclassified: which are outlines and which are counters (the
/// holes in O, A, 8) is decided by containment in <see cref="TextOutlines"/>, not
/// here.</para>
/// </summary>
public sealed class Glyph
{
    internal Glyph(int index, IReadOnlyList<GlyphContour> contours, double advanceWidth, double leftSideBearing)
    {
        Index = index;
        Contours = contours;
        AdvanceWidth = advanceWidth;
        LeftSideBearing = leftSideBearing;

        var bounds = Aabb.Empty;
        foreach (var contour in contours)
            foreach (var point in contour.Points)
                bounds = bounds.Union((point.Position.X, point.Position.Y, 0));
        Bounds = bounds;
    }

    /// <summary>The glyph's index in the font (not a character code).</summary>
    public int Index { get; }

    /// <summary>Closed contours in font units; empty for blank glyphs such as space.</summary>
    public IReadOnlyList<GlyphContour> Contours { get; }

    /// <summary>Pen advance for this glyph in font units (from <c>hmtx</c>).</summary>
    public double AdvanceWidth { get; }

    /// <summary>Distance from the pen position to the outline's left edge, font units.</summary>
    public double LeftSideBearing { get; }

    /// <summary>Control-hull bounds of the outline in font units (z = 0), conservative
    /// for curved contours in exactly the way <c>Sketch.Bounds</c> is for béziers.
    /// <see cref="Aabb.Empty"/> for blank glyphs.</summary>
    public Aabb Bounds { get; }

    /// <summary>True when the glyph draws nothing (space and friends).</summary>
    public bool IsEmpty => Contours.Count == 0;
}
