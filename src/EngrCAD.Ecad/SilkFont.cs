using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>
/// A single-stroke vector font for silkscreen text — the standard way a board's reference designators
/// and values are drawn, since a Gerber has no text primitive and a filled TrueType glyph would etch
/// badly on silk. Every glyph is a set of POLYLINES in a 0.6 × 1 box (x in [0, 0.6], baseline y = 0,
/// cap height y = 1), laid out monospace, so a string becomes line artwork the Gerber draws with a
/// round aperture (exactly as a trace draws) and the reader strokes back.
///
/// <para>The glyph shapes are the viewer's own stroke font (uppercase A-Z, digits and punctuation),
/// transcribed here rather than referenced because the ECAD side is kernel-tier and does not depend on
/// the viewer. v1 covers UPPERCASE letters, digits and <c>. - _ / +</c> — the set a reference
/// designator needs; a lowercase letter (in a value) advances as a blank, which is filed.</para>
/// </summary>
internal static class SilkFont
{
    /// <summary>Glyph box width (the height is 1).</summary>
    internal const double GlyphWidth = 0.6;

    /// <summary>Gap between glyph boxes (monospace advance = width + gap).</summary>
    internal const double GlyphSpacing = 0.3;

    /// <summary>Advance from one glyph origin to the next, in glyph-height units.</summary>
    internal const double Advance = GlyphWidth + GlyphSpacing;

    /// <summary>Width of a laid-out string in glyph-height units (text height 1); multiply by the text
    /// height for board sizes. Unknown characters still advance (they render as blanks).</summary>
    internal static double TextWidth(string text) =>
        text.Length == 0 ? 0 : text.Length * GlyphWidth + (text.Length - 1) * GlyphSpacing;

    /// <summary>The characters the font covers (test / enumeration aid).</summary>
    internal static IReadOnlyCollection<char> Characters => Strokes.Keys;

    /// <summary>
    /// Lays <paramref name="text"/> out as polylines in board-local 2D, starting at
    /// <paramref name="origin"/> (baseline-left) along <paramref name="xAxis"/> (unit) with the +90°
    /// perpendicular as up, at glyph height <paramref name="height"/>. Each returned list is one
    /// polyline (a stroke); a caller draws each as a Gerber line run.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<Vector2d>> Layout(
        string text, in Vector2d origin, in Vector2d xAxis, double height)
    {
        var up = xAxis.Perpendicular;   // +90°, so text reads left-to-right, upright
        var result = new List<IReadOnlyList<Vector2d>>();
        double x0 = 0;
        foreach (char c in text)
        {
            if (Strokes.TryGetValue(char.ToUpperInvariant(c), out var strokes))
            {
                foreach (double[] polyline in strokes)
                {
                    var pts = new List<Vector2d>(polyline.Length / 2);
                    for (int k = 0; k + 1 < polyline.Length; k += 2)
                    {
                        double gx = (x0 + polyline[k]) * height;
                        double gy = polyline[k + 1] * height;
                        pts.Add(origin + xAxis * gx + up * gy);
                    }
                    if (pts.Count >= 2)
                        result.Add(pts);
                }
            }
            x0 += Advance;
        }
        return result;
    }

    // Glyph table. Each entry is polylines of x,y pairs (two consecutive pairs make one segment).
    // Transcribed from EngrCAD.Viewer.Core's StrokeFont so silk lettering matches the viewer's — the
    // ECAD side cannot reference the viewer, so the data (not the assembly) travels. Lowercase is
    // folded to uppercase by the layout above (a refdes is uppercase); an uncovered character advances
    // as a blank.
    private static readonly Dictionary<char, double[][]> Strokes = new()
    {
        [' '] = [],

        // ---- letters ----
        ['A'] = [[0, 0, 0.3, 1, 0.6, 0], [0.14, 0.45, 0.46, 0.45]],
        ['B'] =
        [
            [0, 0, 0, 1, 0.45, 1, 0.6, 0.85, 0.6, 0.65, 0.45, 0.5, 0, 0.5],
            [0.45, 0.5, 0.6, 0.35, 0.6, 0.15, 0.45, 0, 0, 0],
        ],
        ['C'] = [[0.6, 0.82, 0.42, 1, 0.18, 1, 0, 0.82, 0, 0.18, 0.18, 0, 0.42, 0, 0.6, 0.18]],
        ['D'] = [[0, 0, 0, 1, 0.38, 1, 0.6, 0.78, 0.6, 0.22, 0.38, 0, 0, 0]],
        ['E'] = [[0.6, 1, 0, 1, 0, 0, 0.6, 0], [0, 0.5, 0.42, 0.5]],
        ['F'] = [[0.6, 1, 0, 1, 0, 0], [0, 0.5, 0.42, 0.5]],
        ['G'] =
        [
            [0.6, 0.82, 0.42, 1, 0.18, 1, 0, 0.82, 0, 0.18, 0.18, 0, 0.42, 0, 0.6, 0.18, 0.6, 0.42, 0.32, 0.42],
        ],
        ['H'] = [[0, 0, 0, 1], [0.6, 0, 0.6, 1], [0, 0.5, 0.6, 0.5]],
        ['I'] = [[0.3, 0, 0.3, 1], [0.12, 1, 0.48, 1], [0.12, 0, 0.48, 0]],
        ['J'] = [[0.6, 1, 0.6, 0.16, 0.44, 0, 0.16, 0, 0, 0.16]],
        ['K'] = [[0, 0, 0, 1], [0.6, 1, 0, 0.45], [0.22, 0.62, 0.6, 0]],
        ['L'] = [[0, 1, 0, 0, 0.6, 0]],
        ['M'] = [[0, 0, 0, 1, 0.3, 0.5, 0.6, 1, 0.6, 0]],
        ['N'] = [[0, 0, 0, 1, 0.6, 0, 0.6, 1]],
        ['O'] = [[0.18, 0, 0.42, 0, 0.6, 0.18, 0.6, 0.82, 0.42, 1, 0.18, 1, 0, 0.82, 0, 0.18, 0.18, 0]],
        ['P'] = [[0, 0, 0, 1, 0.45, 1, 0.6, 0.85, 0.6, 0.6, 0.45, 0.45, 0, 0.45]],
        ['Q'] =
        [
            [0.18, 0, 0.42, 0, 0.6, 0.18, 0.6, 0.82, 0.42, 1, 0.18, 1, 0, 0.82, 0, 0.18, 0.18, 0],
            [0.36, 0.24, 0.6, 0],
        ],
        ['R'] = [[0, 0, 0, 1, 0.45, 1, 0.6, 0.85, 0.6, 0.6, 0.45, 0.45, 0, 0.45], [0.3, 0.45, 0.6, 0]],
        ['S'] =
        [
            [0.6, 0.85, 0.45, 1, 0.15, 1, 0, 0.85, 0, 0.65, 0.15, 0.5, 0.45, 0.5, 0.6, 0.35, 0.6, 0.15, 0.45, 0, 0.15, 0, 0, 0.15],
        ],
        ['T'] = [[0, 1, 0.6, 1], [0.3, 1, 0.3, 0]],
        ['U'] = [[0, 1, 0, 0.18, 0.18, 0, 0.42, 0, 0.6, 0.18, 0.6, 1]],
        ['V'] = [[0, 1, 0.3, 0, 0.6, 1]],
        ['W'] = [[0, 1, 0.14, 0, 0.3, 0.6, 0.46, 0, 0.6, 1]],
        ['X'] = [[0, 0, 0.6, 1], [0, 1, 0.6, 0]],
        ['Y'] = [[0, 1, 0.3, 0.52, 0.6, 1], [0.3, 0.52, 0.3, 0]],
        ['Z'] = [[0, 1, 0.6, 1, 0, 0, 0.6, 0]],

        // ---- digits ----
        ['0'] =
        [
            [0.18, 0, 0.42, 0, 0.6, 0.18, 0.6, 0.82, 0.42, 1, 0.18, 1, 0, 0.82, 0, 0.18, 0.18, 0],
            [0.13, 0.2, 0.47, 0.8],
        ],
        ['1'] = [[0.12, 0.78, 0.34, 1, 0.34, 0], [0.12, 0, 0.52, 0]],
        ['2'] = [[0, 0.82, 0.18, 1, 0.42, 1, 0.6, 0.82, 0.6, 0.62, 0, 0.12, 0, 0, 0.6, 0]],
        ['3'] =
        [
            [0, 0.9, 0.15, 1, 0.45, 1, 0.6, 0.85, 0.6, 0.65, 0.45, 0.52, 0.22, 0.52],
            [0.45, 0.52, 0.6, 0.38, 0.6, 0.15, 0.45, 0, 0.15, 0, 0, 0.1],
        ],
        ['4'] = [[0.44, 0, 0.44, 1, 0, 0.32, 0.6, 0.32]],
        ['5'] = [[0.6, 1, 0, 1, 0, 0.56, 0.4, 0.56, 0.6, 0.4, 0.6, 0.16, 0.44, 0, 0.14, 0, 0, 0.12]],
        ['6'] =
        [
            [0.55, 0.9, 0.4, 1, 0.18, 1, 0, 0.8, 0, 0.18, 0.18, 0, 0.42, 0, 0.6, 0.18, 0.6, 0.34, 0.42, 0.5, 0, 0.47],
        ],
        ['7'] = [[0, 1, 0.6, 1, 0.22, 0]],
        ['8'] =
        [
            [0.16, 0.52, 0.02, 0.66, 0.02, 0.86, 0.16, 1, 0.44, 1, 0.58, 0.86, 0.58, 0.66, 0.44, 0.52, 0.16, 0.52],
            [0.16, 0.52, 0, 0.36, 0, 0.14, 0.16, 0, 0.44, 0, 0.6, 0.14, 0.6, 0.36, 0.44, 0.52],
        ],
        ['9'] =
        [
            [0.05, 0.1, 0.2, 0, 0.42, 0, 0.6, 0.2, 0.6, 0.82, 0.42, 1, 0.18, 1, 0, 0.82, 0, 0.66, 0.18, 0.5, 0.6, 0.53],
        ],

        // ---- punctuation ----
        ['.'] = [[0.24, 0, 0.36, 0, 0.36, 0.12, 0.24, 0.12, 0.24, 0]],
        ['-'] = [[0.1, 0.5, 0.5, 0.5]],
        ['_'] = [[0, 0, 0.6, 0]],
        ['+'] = [[0.06, 0.5, 0.54, 0.5], [0.3, 0.26, 0.3, 0.74]],
        ['/'] = [[0.06, 0, 0.54, 1]],
    };
}
