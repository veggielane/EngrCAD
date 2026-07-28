using System.Globalization;
using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

// The colour-bar legend for field display: a screen-space widget built the way the view
// cube is built -- pure geometry here (no GL, unit-testable, shared by every front end),
// GL/JS drawing in each front end. Stroke-font labels through the existing line program,
// so no text renderer and no new shader.
//
// Two layout decisions worth stating. It sits on the LEFT edge because the view cube
// owns the top-right and the meshing progress panel the bottom centre; and the bar is
// drawn as a run of flat-coloured BANDS rather than an interpolated gradient, because
// the line program is flat-colour and a legend of discrete steps is arguably more
// readable anyway -- a value lands in a band you can point at.

/// <summary>
/// The legend's geometry for one frame: the colour bar as flat-coloured bands, the
/// outline and tick marks, and the stroke-font labels — all in framebuffer pixel
/// coordinates with the origin at the bottom-left, to be drawn through
/// <see cref="Projection"/>.
/// </summary>
/// <param name="BandVertices">Triangle vertices for the bar, xyz per vertex,
/// <see cref="FieldLegend.VerticesPerBand"/> consecutive vertices per band (drawn one
/// band at a time so each gets its own colour, exactly as the view cube draws its
/// faces).</param>
/// <param name="BandColors">One colour per band, sampled from the display's map.</param>
/// <param name="FrameVertices">Line vertices: the bar's outline and its tick marks.</param>
/// <param name="LabelVertices">Line vertices: the tick numbers and the title.</param>
/// <param name="Projection">Pixel coordinates → clip space for the whole
/// framebuffer.</param>
public sealed record FieldLegendGeometry(
    float[] BandVertices,
    (float R, float G, float B)[] BandColors,
    float[] FrameVertices,
    float[] LabelVertices,
    Matrix4d Projection)
{
    /// <summary>How many bands the bar has (<see cref="BandColors"/>' length).</summary>
    public int BandCount => BandColors.Length;

    /// <summary>Vertices in <see cref="FrameVertices"/>.</summary>
    public int FrameVertexCount => FrameVertices.Length / 3;

    /// <summary>Vertices in <see cref="LabelVertices"/>.</summary>
    public int LabelVertexCount => LabelVertices.Length / 3;

    /// <summary>Nothing to draw (the viewport is too small for the widget).</summary>
    public static readonly FieldLegendGeometry Empty =
        new([], [], [], [], Matrix4d.Identity);

    /// <summary>True when the widget was laid out (false when it did not fit).</summary>
    public bool HasContent => BandColors.Length > 0;
}

/// <summary>
/// Builds the field-display colour bar. Pure geometry — the desktop window, the headless
/// pass and the browser client all draw THIS, so a legend cannot say one thing in one
/// front end and another in the next.
/// </summary>
public static class FieldLegend
{
    /// <summary>Bar width in device-independent pixels.</summary>
    public const double BarWidthDip = 18;

    /// <summary>Bar height in device-independent pixels.</summary>
    public const double BarHeightDip = 170;

    /// <summary>Gap between the viewport's left edge and the bar, in DIPs.</summary>
    public const double MarginDip = 16;

    /// <summary>Stroke-font text height in DIPs.</summary>
    public const double TextHeightDip = 9;

    /// <summary>Gap between the bar and its tick labels, in DIPs.</summary>
    public const double LabelGapDip = 6;

    /// <summary>Tick-mark length in DIPs.</summary>
    public const double TickLengthDip = 4;

    /// <summary>Colour bands in the bar. 32 steps reads as a smooth ramp at this size
    /// while keeping each band pointable — and it is what lets a flat-colour program
    /// draw a colour map at all.</summary>
    public const int Bands = 32;

    /// <summary>Vertices per band: two triangles.</summary>
    public const int VerticesPerBand = 6;

    /// <summary>Labelled ticks, evenly spaced from the bottom of the bar to the top
    /// (min, quarters, max).</summary>
    public const int Ticks = 5;

    /// <summary>The bar's outline and tick colour — the dim chrome grey the rest of the
    /// viewport furniture uses.</summary>
    public static readonly (float R, float G, float B) FrameColor = (0.55f, 0.58f, 0.62f);

    /// <summary>Label colour: bright enough to read over the dark background.</summary>
    public static readonly (float R, float G, float B) LabelColor = (0.86f, 0.88f, 0.92f);

    /// <summary>
    /// The minimum framebuffer size the widget needs. Below it
    /// <see cref="Build"/> returns <see cref="FieldLegendGeometry.Empty"/> rather than
    /// drawing a legend nobody can read — the view cube's too-small guard, applied to a
    /// different shape.
    /// </summary>
    public static bool Fits(double widthPx, double heightPx, double pixelScale) =>
        widthPx >= (MarginDip + BarWidthDip + LabelGapDip + 60) * pixelScale
        && heightPx >= (BarHeightDip + 40) * pixelScale;

    /// <summary>
    /// Lays the legend out for a framebuffer of <paramref name="widthPx"/> ×
    /// <paramref name="heightPx"/> device pixels (<paramref name="pixelScale"/> device
    /// pixels per DIP — the window's DPI scaling, the offscreen pass's supersample
    /// factor, the browser's device-pixel ratio; the same correction all three already
    /// make for point sprites and annotation text).
    /// </summary>
    public static FieldLegendGeometry Build(
        in ResolvedFieldDisplay display, double widthPx, double heightPx, double pixelScale = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pixelScale, 0);
        if (!Fits(widthPx, heightPx, pixelScale))
            return FieldLegendGeometry.Empty;

        double barWidth = BarWidthDip * pixelScale;
        double barHeight = BarHeightDip * pixelScale;
        double margin = MarginDip * pixelScale;
        double textHeight = TextHeightDip * pixelScale;
        double labelGap = LabelGapDip * pixelScale;
        double tick = TickLengthDip * pixelScale;

        double x0 = margin;
        double x1 = margin + barWidth;
        double y0 = (heightPx - barHeight) / 2;
        double y1 = y0 + barHeight;

        var bandVertices = new float[Bands * VerticesPerBand * 3];
        var bandColors = new (float R, float G, float B)[Bands];
        for (int b = 0; b < Bands; b++)
        {
            double lo = y0 + barHeight * b / Bands;
            double hi = y0 + barHeight * (b + 1) / Bands;
            // The band's colour is its MIDPOINT's, so the ramp is symmetric about the
            // bar's ends: the bottom band shows the map at 1/(2N) rather than at 0,
            // which is what the values inside it actually map to.
            bandColors[b] = ColorMaps.Sample(display.ColorMap, (b + 0.5) / Bands);
            int at = b * VerticesPerBand * 3;
            Quad(bandVertices, at, x0, lo, x1, hi);
        }

        var frame = new List<float>();
        AppendSegment(frame, x0, y0, x1, y0);
        AppendSegment(frame, x1, y0, x1, y1);
        AppendSegment(frame, x1, y1, x0, y1);
        AppendSegment(frame, x0, y1, x0, y0);

        var labels = new List<(Vector3d A, Vector3d B)>();
        for (int t = 0; t < Ticks; t++)
        {
            double f = Ticks == 1 ? 0 : (double)t / (Ticks - 1);
            double y = y0 + barHeight * f;
            AppendSegment(frame, x1, y, x1 + tick, y);
            double value = display.Range.Min + display.Range.Span * f;
            StrokeFont.AppendText(labels, Format(value),
                new Vector3d(x1 + tick + labelGap, y - textHeight / 2, 0),
                Vector3d.UnitX, Vector3d.UnitY, textHeight);
        }

        // Title above the bar. Uppercased because the stroke font has no lowercase
        // glyphs — an unmapped character advances as a blank, so a lowercase title would
        // silently come out as gaps.
        StrokeFont.AppendText(labels, Title(display),
            new Vector3d(x0, y1 + textHeight * 0.9, 0), Vector3d.UnitX, Vector3d.UnitY, textHeight);

        return new FieldLegendGeometry(
            bandVertices, bandColors, [.. frame], Flatten(labels), Projection(widthPx, heightPx));
    }

    /// <summary>
    /// The legend's title: the field's name and units, plus the deformation scale when
    /// the shape is displaced (a deformed plot whose exaggeration is not stated is a
    /// picture of a shape that does not exist). Uppercased for the stroke font.
    /// </summary>
    public static string Title(in ResolvedFieldDisplay display)
    {
        string title = display.Label;
        if (display.Deform is not null && display.DeformScale != 0)
        {
            // Plain "X" rather than the multiplication sign: this string is uppercased
            // for the stroke font anyway, and keeping it ASCII keeps the source file
            // ASCII -- the rule every string that can reach the GL stack follows.
            title += $" ({Format(display.DeformScale)}X DEFORMED)";
        }
        return title.ToUpperInvariant();
    }

    /// <summary>Tick-label formatting: four significant digits, invariant, so the
    /// legend reads the same in every locale.</summary>
    public static string Format(double value) =>
        value.ToString("G4", CultureInfo.InvariantCulture).ToUpperInvariant();

    /// <summary>
    /// Framebuffer pixel coordinates (origin bottom-left, matching GL's viewport) to
    /// clip space. Column-vector convention, like every other matrix here; z maps to 0,
    /// since the widget is drawn with the depth test off.
    /// </summary>
    public static Matrix4d Projection(double widthPx, double heightPx) => new(
        2 / Math.Max(widthPx, 1), 0, 0, -1,
        0, 2 / Math.Max(heightPx, 1), 0, -1,
        0, 0, 0, 0,
        0, 0, 0, 1);

    private static void Quad(float[] vertices, int at, double x0, double y0, double x1, double y1)
    {
        Span<(double X, double Y)> corners =
        [
            (x0, y0), (x1, y0), (x1, y1),
            (x0, y0), (x1, y1), (x0, y1),
        ];
        for (int i = 0; i < corners.Length; i++)
        {
            vertices[at + i * 3] = (float)corners[i].X;
            vertices[at + i * 3 + 1] = (float)corners[i].Y;
            vertices[at + i * 3 + 2] = 0;
        }
    }

    private static void AppendSegment(List<float> vertices, double x0, double y0, double x1, double y1)
    {
        vertices.Add((float)x0);
        vertices.Add((float)y0);
        vertices.Add(0);
        vertices.Add((float)x1);
        vertices.Add((float)y1);
        vertices.Add(0);
    }

    private static float[] Flatten(List<(Vector3d A, Vector3d B)> segments)
    {
        var vertices = new float[segments.Count * 6];
        for (int i = 0; i < segments.Count; i++)
        {
            var (a, b) = segments[i];
            vertices[i * 6] = (float)a.X;
            vertices[i * 6 + 1] = (float)a.Y;
            vertices[i * 6 + 2] = 0;
            vertices[i * 6 + 3] = (float)b.X;
            vertices[i * 6 + 4] = (float)b.Y;
            vertices[i * 6 + 5] = 0;
        }
        return vertices;
    }
}
