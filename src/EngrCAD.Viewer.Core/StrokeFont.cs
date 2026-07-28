using EngrCAD.Core;

namespace EngrCAD.Viewer;

/// <summary>
/// The viewer's stroke font: every glyph is a set of polylines in a 0.6 x 1 box
/// (x in [0, 0.6], baseline y = 0, cap height y = 1; the comma dips slightly below),
/// drawn as plain line segments through the existing line program — no textures, no
/// text renderer, CAD-legible at small sizes. Grown out of the view cube's label
/// lettering (which now uses this table) to cover digits, A-Z, and the symbols
/// dimensions and callouts need: <c>. , - + / ( )</c> and degree, diameter,
/// plus-minus, multiplication, depth, counterbore, and countersink signs.
/// <para>
/// Source stays pure ASCII: symbol glyphs are keyed by \u escapes and built from
/// coordinates (the GLSL-must-be-ASCII lesson applies to every string the GL stack
/// might ever see).
/// </para>
/// </summary>
public static class StrokeFont
{
    /// <summary>Glyph box width (the height is 1).</summary>
    public const double GlyphWidth = 0.6;

    /// <summary>Horizontal gap between glyph boxes (monospace advance = width + gap).</summary>
    public const double GlyphSpacing = 0.3;

    /// <summary>Advance from one glyph origin to the next, in glyph-height units.</summary>
    public const double Advance = GlyphWidth + GlyphSpacing;

    /// <summary>Width of a laid-out string in glyph-height units (text height 1);
    /// multiply by the text height for world/screen sizes. Unknown characters still
    /// advance (they render as blanks).</summary>
    public static double TextWidth(string text) =>
        text.Length == 0 ? 0 : text.Length * GlyphWidth + (text.Length - 1) * GlyphSpacing;

    /// <summary>The strokes of a character (polylines of x,y pairs in the glyph box);
    /// false for characters the font does not cover (callers advance past them).</summary>
    public static bool TryGetStrokes(char c, out double[][] strokes) =>
        Strokes.TryGetValue(c, out strokes!);

    /// <summary>Every character the font covers (test/enumeration aid).</summary>
    public static IReadOnlyCollection<char> Characters => Strokes.Keys;

    /// <summary>
    /// Lays <paramref name="text"/> out as line segments in the plane spanned by
    /// <paramref name="right"/>/<paramref name="up"/> (unit vectors), starting at
    /// <paramref name="origin"/> (baseline-left) with glyph height
    /// <paramref name="height"/>. Billboarded text passes the camera's screen-right
    /// and screen-up; flat labels (the view cube) pass the face's in-plane frame.
    /// </summary>
    public static void AppendText(
        List<(Vector3d A, Vector3d B)> segments, string text,
        in Vector3d origin, in Vector3d right, in Vector3d up, double height)
    {
        double x0 = 0;
        foreach (char c in text)
        {
            if (TryGetStrokes(c, out var strokes))
            {
                foreach (double[] polyline in strokes)
                {
                    for (int k = 0; k + 3 < polyline.Length; k += 2)
                    {
                        var a = origin + right * ((x0 + polyline[k]) * height) + up * (polyline[k + 1] * height);
                        var b = origin + right * ((x0 + polyline[k + 2]) * height) + up * (polyline[k + 3] * height);
                        segments.Add((a, b));
                    }
                }
            }
            x0 += Advance;
        }
    }

    // Glyph table. Letters A-Z (the cube's original letters kept stroke-identical),
    // digits, punctuation, and the dimension symbols. Each entry is polylines of
    // x,y pairs; two consecutive pairs make one segment.
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
        [','] = [[0.34, 0.12, 0.24, -0.14]],
        ['-'] = [[0.1, 0.5, 0.5, 0.5]],
        ['+'] = [[0.06, 0.5, 0.54, 0.5], [0.3, 0.26, 0.3, 0.74]],
        ['/'] = [[0.06, 0, 0.54, 1]],
        ['('] = [[0.44, 1, 0.28, 0.78, 0.22, 0.5, 0.28, 0.22, 0.44, 0]],
        [')'] = [[0.16, 1, 0.32, 0.78, 0.38, 0.5, 0.32, 0.22, 0.16, 0]],

        // ---- dimension symbols (keys kept as escapes; source stays ASCII) ----
        // degree sign: a small raised circle
        ['\u00B0'] = [[0.2, 0.72, 0.12, 0.8, 0.12, 0.92, 0.2, 1, 0.4, 1, 0.48, 0.92, 0.48, 0.8, 0.4, 0.72, 0.2, 0.72]],
        // plus-minus
        ['\u00B1'] = [[0.06, 0.62, 0.54, 0.62], [0.3, 0.38, 0.3, 0.86], [0.06, 0.12, 0.54, 0.12]],
        // multiplication sign
        ['\u00D7'] = [[0.1, 0.24, 0.5, 0.76], [0.1, 0.76, 0.5, 0.24]],
        // downwards arrow from bar (the depth symbol)
        ['\u21A7'] = [[0.1, 1, 0.5, 1], [0.3, 0.92, 0.3, 0], [0.12, 0.28, 0.3, 0], [0.48, 0.28, 0.3, 0]],
        // diameter sign: circle with a through slash
        ['\u2300'] =
        [
            [0.16, 0.18, 0.06, 0.32, 0.06, 0.62, 0.16, 0.76, 0.44, 0.76, 0.54, 0.62, 0.54, 0.32, 0.44, 0.18, 0.16, 0.18],
            [0, 0.06, 0.6, 0.88],
        ],
        // counterbore symbol (open-topped rectangle)
        ['\u2334'] = [[0.06, 0.6, 0.06, 0.12, 0.54, 0.12, 0.54, 0.6]],
        // countersink symbol (open V)
        ['\u2335'] = [[0.06, 0.72, 0.3, 0.12, 0.54, 0.72]],
    };
}
