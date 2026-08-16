using System.Globalization;
using EngrCAD.Core;
using EngrCAD.Mesh;
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
//
// LOG-SCALE fields. A producer that publishes base-10 logarithms declares it in the
// field's own units string -- "log10(cycles)", the convention FatigueResults
// established -- and the legend READS that declaration (TryLogUnits): tick labels print
// the anti-logged values ("1E+05 cycles", not "5.1 log10(cycles)") and, where the range
// spans at least two decades, ticks sit on the integer decades. Nothing about the
// COLOURS changes -- linear colour over log values IS log-colour -- and nothing is
// applied silently: the transform being rendered is the one the field itself states, so
// this is typesetting a declaration, not a second FieldRange.SymmetricAboutZero.
// FieldDisplay.LogScale is the COMPLEMENTARY spelling, for a field carrying RAW
// decade-spanning values: there the colours log the values, and the legend converts
// the raw range to log10 and runs this file's own decade-tick arithmetic, so the two
// spellings share one tick builder and cannot drift. A display wants exactly one of
// the two -- the units string says the values are already logged, the flag says the
// colours should log them.

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
    /// (min, quarters, max). Log-scale displays whose range spans at least two decades
    /// use decade ticks instead — see <see cref="TickMarks"/>.</summary>
    public const int Ticks = 5;

    /// <summary>Most interior decade ticks a log-scale bar carries. Beyond it every
    /// n-th decade is kept — at the bar's fixed height more labels than this collide.</summary>
    public const int MaxLogDecadeTicks = 6;

    /// <summary>Clearance, in DIPs along the bar, an interior decade tick must keep from
    /// each END tick. The ends always print the range's true min and max (a legend that
    /// hides its endpoints lies about its range), so a decade landing on top of one
    /// would overlap its label — at <see cref="TextHeightDip"/>-tall text, 12 DIPs is
    /// one label height plus a small gap.</summary>
    public const double LogEndClearanceDip = 12;

    /// <summary>Height, in DIPs, of the NO-VALUE swatch drawn below the bar when the
    /// displayed field carries a value with no colour position (NaN, or non-positive
    /// under a log-scale display) — see <see cref="HasNoValue"/>.</summary>
    public const double NoValueSwatchDip = 10;

    /// <summary>Gap, in DIPs, between the bar's bottom and the NO-VALUE swatch.</summary>
    public const double NoValueGapDip = 6;

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
        in ResolvedFieldDisplay display, double widthPx, double heightPx, double pixelScale = 1) =>
        Build([display], widthPx, heightPx, pixelScale);

    /// <summary>Vertical gap, in DIPs, between STACKED legends — room for the lower
    /// bar's title plus clearance, so two scales never read as one.</summary>
    public const double StackGapDip = 34;

    /// <summary>
    /// Lays out one legend per DISTINCT display, stacked top-to-bottom in list order and
    /// centred as a group — several visible parts on genuinely different scales each get
    /// their own bar, because one bar over two scales is a legend that lies. Everything
    /// is appended into ONE geometry (more bands, more frame, more labels), so the front
    /// ends draw a stack with zero change — the NO-VALUE swatch's trick, one level up.
    /// As many legends as fit vertically are kept, first-come (the caller passes draw
    /// order); a single display reproduces the incumbent centred layout bit for bit.
    /// </summary>
    public static FieldLegendGeometry Build(
        IReadOnlyList<ResolvedFieldDisplay> displays,
        double widthPx, double heightPx, double pixelScale = 1)
    {
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pixelScale, 0);
        if (displays.Count == 0 || !Fits(widthPx, heightPx, pixelScale))
            return FieldLegendGeometry.Empty;

        double barHeight = BarHeightDip * pixelScale;
        double stackGap = StackGapDip * pixelScale;

        // How many bars fit: the group needs n bars + (n-1) gaps + the same 40-DIP
        // breathing room Fits reserves for one. At least one always fits (Fits said so).
        int count = Math.Max(1, Math.Min(displays.Count,
            (int)Math.Floor((heightPx - 40 * pixelScale + stackGap) / (barHeight + stackGap))));

        double groupHeight = count * barHeight + (count - 1) * stackGap;
        double groupBottom = (heightPx - groupHeight) / 2;

        var bandVertices = new List<float>();
        var bandColors = new List<(float R, float G, float B)>();
        var frame = new List<float>();
        var labels = new List<(Vector3d A, Vector3d B)>();
        for (int k = 0; k < count; k++)
        {
            // List order reads top to bottom, the way a caller's draw order reads.
            double y0 = groupBottom + (count - 1 - k) * (barHeight + stackGap);
            AppendLegend(displays[k], y0, widthPx, pixelScale,
                bandVertices, bandColors, frame, labels);
        }
        return new FieldLegendGeometry(
            [.. bandVertices], [.. bandColors], [.. frame], Flatten(labels),
            Projection(widthPx, heightPx));
    }

    private static void AppendLegend(
        in ResolvedFieldDisplay display, double y0, double widthPx, double pixelScale,
        List<float> bandVertices, List<(float R, float G, float B)> bandColors,
        List<float> frame, List<(Vector3d A, Vector3d B)> labels)
    {
        double barWidth = BarWidthDip * pixelScale;
        double barHeight = BarHeightDip * pixelScale;
        double margin = MarginDip * pixelScale;
        double textHeight = TextHeightDip * pixelScale;
        double labelGap = LabelGapDip * pixelScale;
        double tick = TickLengthDip * pixelScale;

        double x0 = margin;
        double x1 = margin + barWidth;
        double y1 = y0 + barHeight;

        for (int b = 0; b < Bands; b++)
        {
            double lo = y0 + barHeight * b / Bands;
            double hi = y0 + barHeight * (b + 1) / Bands;
            // The band's colour is its MIDPOINT's, so the ramp is symmetric about the
            // bar's ends: the bottom band shows the map at 1/(2N) rather than at 0,
            // which is what the values inside it actually map to.
            bandColors.Add(ColorMaps.Sample(display.ColorMap, (b + 0.5) / Bands));
            AppendQuad(bandVertices, x0, lo, x1, hi);
        }

        frame.EnsureCapacity(frame.Count + (4 + Ticks) * 6);
        AppendSegment(frame, x0, y0, x1, y0);
        AppendSegment(frame, x1, y0, x1, y1);
        AppendSegment(frame, x1, y1, x0, y1);
        AppendSegment(frame, x0, y1, x0, y0);

        // A field carrying a value with no colour position gets one extra "band": a
        // grey NO-VALUE swatch below the bar. Appended as a band because both front
        // ends draw bands generically from these arrays (BandCount x VerticesPerBand),
        // so the swatch costs no front-end change at all; absent, the arrays are
        // bit-identical to what they always were.
        if (HasNoValue(display))
        {
            double swTop = y0 - NoValueGapDip * pixelScale;
            double swBottom = swTop - NoValueSwatchDip * pixelScale;
            bandColors.Add(ColorMaps.NoValueColor);
            AppendQuad(bandVertices, x0, swBottom, x1, swTop);
            AppendSegment(frame, x0, swBottom, x1, swBottom);
            AppendSegment(frame, x1, swBottom, x1, swTop);
            AppendSegment(frame, x1, swTop, x0, swTop);
            AppendSegment(frame, x0, swTop, x0, swBottom);
            StrokeFont.AppendText(labels, "NO VALUE",
                new Vector3d(x1 + tick + labelGap, (swBottom + swTop) / 2 - textHeight / 2, 0),
                Vector3d.UnitX, Vector3d.UnitY, textHeight);
        }
        foreach (var (f, label) in TickMarks(display))
        {
            double y = y0 + barHeight * f;
            AppendSegment(frame, x1, y, x1 + tick, y);
            StrokeFont.AppendText(labels, label,
                new Vector3d(x1 + tick + labelGap, y - textHeight / 2, 0),
                Vector3d.UnitX, Vector3d.UnitY, textHeight);
        }

        // Title above the bar. Uppercased because the stroke font has no lowercase
        // glyphs — an unmapped character advances as a blank, so a lowercase title would
        // silently come out as gaps.
        StrokeFont.AppendText(labels, Title(display),
            new Vector3d(x0, y1 + textHeight * 0.9, 0), Vector3d.UnitX, Vector3d.UnitY, textHeight);
    }

    private static void AppendQuad(List<float> vertices, double x0, double y0, double x1, double y1)
    {
        // The same two triangles Quad writes, appended — identical corner order and the
        // identical double->float narrowing, which is what keeps a one-display build
        // bit-identical to the incumbent fixed-array path.
        Span<(double X, double Y)> corners =
        [
            (x0, y0), (x1, y0), (x1, y1),
            (x0, y0), (x1, y1), (x0, y1),
        ];
        foreach (var (x, y) in corners)
        {
            vertices.Add((float)x);
            vertices.Add((float)y);
            vertices.Add(0);
        }
    }

    /// <summary>
    /// Whether the displayed field carries a value with NO colour position — NaN (the
    /// VTU "no value" convention: an infinite fatigue life, a part with no data in a
    /// merged export), or a non-positive value under a log-scale display, which maps
    /// through NaN to the same place. Such values paint
    /// <see cref="ColorMaps.NoValueColor"/>, and the legend earns its swatch exactly
    /// when one exists; a field without any leaves the legend bit-identical.
    /// </summary>
    public static bool HasNoValue(in ResolvedFieldDisplay display)
    {
        var field = display.Field;
        for (int i = 0; i < field.Count; i++)
        {
            double v = field.ScalarAt(i);
            if (double.IsNaN(v) || (display.LogScale && !(v > 0)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The tick marks the bar carries for a display: fraction along the bar (0 = bottom,
    /// 1 = top) and the printed label, bottom to top.
    /// <para>A linear display gets <see cref="Ticks"/> evenly spaced ticks labelled with
    /// their values. A LOG-SCALE display (the field's units declare
    /// <c>log10(…)</c> — see <see cref="TryLogUnits"/>) labels ticks with the
    /// ANTI-LOGGED values, and places them on the integer decades when the range spans
    /// at least two of them: round powers of ten are the whole point of a log legend,
    /// while an interval under two decades may hold as few as NO interior decades —
    /// too few ticks to describe a range — so it falls back to the even spacing with
    /// anti-logged labels. The two END ticks always print the range's true (anti-logged) min and
    /// max, and an interior decade within <see cref="LogEndClearanceDip"/> of an end is
    /// dropped so the labels cannot overlap.</para>
    /// <para>Tick POSITIONS are honest either way: a decade tick at
    /// <c>log10 = k</c> sits at the linear position of the value k, which is exactly
    /// where the colour for k is — the colour mapping stays linear over the log values,
    /// which is what log-colour means.</para>
    /// </summary>
    public static (double Fraction, string Label)[] TickMarks(in ResolvedFieldDisplay display)
    {
        var range = display.Range;
        bool log = TryLogUnits(display.Field.Units, out _);

        // The first-class LogScale flag carries RAW values; converting its range to
        // log10 makes it EXACTLY the units-declared case's arithmetic (whose values are
        // already logged), so the two spellings share one decade-tick builder and print
        // the same ticks for the same data.
        if (display.LogScale && range.Min > 0)
        {
            range = new FieldRange(Math.Log10(range.Min), Math.Log10(range.Max));
            log = true;
        }

        if (log && range.Span >= 2)
        {
            var ticks = new List<(double, string)> { (0, Format(Math.Pow(10, range.Min))) };
            int first = (int)Math.Ceiling(range.Min);
            int last = (int)Math.Floor(range.Max);
            int step = Math.Max(1, (int)Math.Ceiling((last - first + 1) / (double)MaxLogDecadeTicks));
            double clearance = LogEndClearanceDip / BarHeightDip;
            for (int k = first; k <= last; k += step)
            {
                double f = (k - range.Min) / range.Span;
                if (f >= clearance && f <= 1 - clearance)
                    ticks.Add((f, Format(Math.Pow(10, k))));
            }
            ticks.Add((1, Format(Math.Pow(10, range.Max))));
            return [.. ticks];
        }

        var even = new (double, string)[Ticks];
        for (int t = 0; t < Ticks; t++)
        {
            double f = Ticks == 1 ? 0 : (double)t / (Ticks - 1);
            double value = range.Min + range.Span * f;
            even[t] = (f, Format(log ? Math.Pow(10, value) : value));
        }
        return even;
    }

    /// <summary>
    /// Whether a field's units string declares its values to be base-10 logarithms —
    /// the <c>"log10(cycles)"</c> convention <c>FatigueResults</c> established — and if
    /// so, of what (<paramref name="baseUnits"/> = the inner units, e.g. "cycles").
    /// <para>The units string is the ONE declaration of the transform (it already
    /// round-trips through the document format), and the legend renders what it says:
    /// anti-logged tick labels and a title in the base units. A separate boolean flag
    /// beside it would be a second spelling of the same fact, free to drift.</para>
    /// </summary>
    public static bool TryLogUnits(string? units, out string baseUnits)
    {
        baseUnits = string.Empty;
        const string prefix = "log10(";
        if (units is null
            || !units.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !units.EndsWith(')'))
        {
            return false;
        }
        string inner = units[prefix.Length..^1].Trim();
        if (inner.Length == 0)
            return false;
        baseUnits = inner;
        return true;
    }

    /// <summary>
    /// The legend's title: the field's name and units, plus the deformation scale when
    /// the shape is displaced (a deformed plot whose exaggeration is not stated is a
    /// picture of a shape that does not exist). Uppercased for the stroke font.
    /// <para>A log-scale field (units <c>log10(X)</c>) titles as
    /// <c>NAME [X, LOG SCALE]</c>: the ticks print anti-logged values in X, so a title
    /// still saying <c>log10(X)</c> would make a "1E+05" tick read as 10 to the
    /// 100000th; "LOG SCALE" states the spacing.</para>
    /// </summary>
    public static string Title(in ResolvedFieldDisplay display)
    {
        string title = TryLogUnits(display.Field.Units, out string baseUnits)
            ? $"{display.Field.Name} [{baseUnits}, LOG SCALE]"
            : display.LogScale
                ? (display.Field.Units.Length == 0
                    ? $"{display.Field.Name} [LOG SCALE]"
                    : $"{display.Field.Name} [{display.Field.Units}, LOG SCALE]")
                : display.Label;
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
