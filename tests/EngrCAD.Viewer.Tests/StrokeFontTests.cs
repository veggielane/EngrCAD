using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// Stroke-font sanity: coverage (digits, A-Z, dimension symbols), glyph geometry
/// stays inside the normalized box, and layout math (advance, scaling, plane
/// mapping) behaves — no GL involved.
/// </summary>
public class StrokeFontTests
{
    // The characters annotations and callouts require, per the PMI feature spec.
    private static readonly string Required =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ.,-+/() " +
        "\u00B0\u2300\u00B1\u00D7\u21A7\u2334\u2335";

    [Fact]
    public void CoversDigitsLettersAndDimensionSymbols()
    {
        foreach (char c in Required)
            Assert.True(StrokeFont.TryGetStrokes(c, out _), $"missing glyph for '{c}' (U+{(int)c:X4})");
    }

    [Fact]
    public void EveryVisibleGlyphHasStrokes()
    {
        foreach (char c in StrokeFont.Characters)
        {
            Assert.True(StrokeFont.TryGetStrokes(c, out var strokes));
            if (c == ' ')
            {
                Assert.Empty(strokes);
                continue;
            }
            Assert.True(strokes.Length > 0, $"glyph '{c}' has no strokes");
            foreach (double[] polyline in strokes)
            {
                Assert.True(polyline.Length >= 4, $"glyph '{c}' has a degenerate polyline");
                Assert.True(polyline.Length % 2 == 0, $"glyph '{c}' has an odd coordinate count");
            }
        }
    }

    [Fact]
    public void GlyphCoordinatesStayInsideTheNormalizedBox()
    {
        foreach (char c in StrokeFont.Characters)
        {
            StrokeFont.TryGetStrokes(c, out var strokes);
            foreach (double[] polyline in strokes)
            {
                for (int k = 0; k + 1 < polyline.Length; k += 2)
                {
                    Assert.InRange(polyline[k], 0, StrokeFont.GlyphWidth);
                    Assert.InRange(polyline[k + 1], -0.2, 1.0);   // comma dips below baseline
                }
            }
        }
    }

    [Fact]
    public void TextWidth_IsMonospaceAdvance()
    {
        Assert.Equal(0.0, StrokeFont.TextWidth(""), 12);
        Assert.Equal(StrokeFont.GlyphWidth, StrokeFont.TextWidth("4"), 12);
        Assert.Equal(2 * StrokeFont.GlyphWidth + StrokeFont.GlyphSpacing, StrokeFont.TextWidth("40"), 12);
        // Unknown characters still advance (render blank, keep alignment).
        Assert.Equal(StrokeFont.TextWidth("ab"), StrokeFont.TextWidth("a?"), 12);
    }

    [Fact]
    public void AppendText_ScalesLinearlyWithHeight()
    {
        var small = new List<(Vector3d A, Vector3d B)>();
        var large = new List<(Vector3d A, Vector3d B)>();
        StrokeFont.AppendText(small, "40", Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ, 1.0);
        StrokeFont.AppendText(large, "40", Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ, 2.0);

        Assert.Equal(small.Count, large.Count);
        Assert.True(small.Count > 0);
        for (int i = 0; i < small.Count; i++)
        {
            double a = small[i].A.DistanceTo(small[i].B);
            double b = large[i].A.DistanceTo(large[i].B);
            Assert.Equal(2 * a, b, 1e-12);
        }
    }

    [Fact]
    public void AppendText_MapsIntoTheGivenPlane()
    {
        // Text laid out in the XY plane at z = 5 must stay in that plane and start at
        // the origin's x (baseline-left).
        var segments = new List<(Vector3d A, Vector3d B)>();
        var origin = new Vector3d(10, 20, 5);
        StrokeFont.AppendText(segments, "17", origin, Vector3d.UnitX, Vector3d.UnitY, 3.0);

        Assert.True(segments.Count > 0);
        foreach (var (a, b) in segments)
        {
            Assert.Equal(5.0, a.Z, 1e-12);
            Assert.Equal(5.0, b.Z, 1e-12);
            Assert.InRange(a.X, origin.X, origin.X + StrokeFont.TextWidth("17") * 3.0);
            Assert.InRange(a.Y, origin.Y - 0.6, origin.Y + 3.0);
        }
    }

    [Fact]
    public void AppendText_SkipsUnknownCharactersButAdvances()
    {
        var known = new List<(Vector3d A, Vector3d B)>();
        var withUnknown = new List<(Vector3d A, Vector3d B)>();
        StrokeFont.AppendText(known, "11", Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 1.0);
        StrokeFont.AppendText(withUnknown, "1?1", Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 1.0);

        // Same stroke count (the '?' contributes nothing) but the second '1' moved
        // one extra advance to the right.
        Assert.Equal(known.Count, withUnknown.Count);
        double knownMax = known.Max(s => Math.Max(s.A.X, s.B.X));
        double unknownMax = withUnknown.Max(s => Math.Max(s.A.X, s.B.X));
        Assert.Equal(StrokeFont.Advance, unknownMax - knownMax, 1e-12);
    }
}
