using System.Globalization;
using System.IO.Compression;
using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// PDF export for drawing sheets — the third writer over the same content the SVG and
/// DXF writers consume, because what gets SENT to a manufacturer is a PDF.
///
/// <para><b>Dependency-free, uncompressed by default, ASCII.</b> The whole file is
/// hand-written (the TrueType/PNG/glTF/STEP tradition; a PDF content stream is plainer
/// than any of those) and its streams are NOT Flate-compressed unless
/// <see cref="Compress"/> asks: an uncompressed stream is legal PDF, a sheet's line work
/// is tens of kilobytes, and a text file can be read, diffed and asserted on directly —
/// the same argument that made <c>BrepArchive</c> a text format. The file also carries
/// <b>no /Info dictionary and no /ID</b>: both are optional per the spec, and their
/// natural values (a CreationDate, an MD5 salted with the clock) are exactly what would
/// break the byte-fixed-point property the tests pin — writing the same sheet twice
/// produces the same bytes.</para>
///
/// <para><b>Every option here is opt-in and leaves the default output byte for byte what
/// it was.</b> Stating no layer emits no optional-content machinery, leaving
/// <see cref="Font"/> alone emits no font program, and leaving <see cref="Compress"/>
/// alone emits the same plain stream — each pinned by a byte comparison rather than
/// argued.</para>
///
/// <para><b>PDF's page space is the sheet's own space, so there is no flip.</b> PDF user
/// space has its origin at the BOTTOM-LEFT with y up — precisely the sheet convention —
/// which retires the whole y-flip apparatus the SVG writer needs, text-outside-the-flip
/// machinery included. The one transform in the file is a single <c>cm</c> at the head
/// of the content stream mapping millimetres to points (<see cref="PointsPerMillimetre"/>,
/// the ONE 72/25.4 constant), so every coordinate in the stream is the model's own
/// millimetre value verbatim, and line widths, dash lengths and font sizes are stated in
/// millimetres too (they all follow user space).</para>
///
/// <para><b>Text is the standard-14 Helvetica by default, or an embedded subset.</b> The
/// viewer's 3D overlay letters with <c>StrokeFont</c>, but that lives in
/// EngrCAD.Viewer.Core, which Modeling cannot reference, and duplicating its glyph table
/// here would be exactly the two-copies drift <c>SheetStyle</c>'s ratio convention exists
/// to avoid. PDF's standard 14 need no embedding because every conforming reader carries
/// them, so the rule that a file naming a resource must define it is satisfied by the
/// /Font object naming /Helvetica — at the price of WinAnsi, which cannot spell the
/// drafting depth/counterbore/countersink symbols or any non-Latin text. Setting
/// <see cref="Font"/> to <see cref="PdfFont.Embed"/> carries a real TrueType font as a
/// subset instead (<c>/Identity-H</c> over glyph indices, so the encoding has no
/// repertoire at all) and the substitution below stops being needed.</para>
///
/// <para><b>Under the built-in Helvetica, characters outside WinAnsi are refused by name,
/// with one deliberate substitution.</b> Strings are encoded as WinAnsi bytes; a
/// character with no WinAnsi form throws naming the character rather than degrading to
/// <c>?</c>, because a dimension that silently lost its symbol is a wrong drawing (the
/// descriptor sanitization rule). The exception is U+2300, the drafting diameter sign
/// the dimension layer emits: WinAnsi lacks it, Helvetica carries U+00D8 (O with
/// stroke), and that is the standard typographic fallback on drawings. Under an
/// EMBEDDED font neither rule applies — the character is looked up in the font, U+2300
/// included, and a character the font has no glyph for is refused naming both.</para>
///
/// <para>The line-class vocabulary and pens are <see cref="SvgLineClass"/> and
/// <see cref="SvgDrawing.SvgPen"/> — one pen table serving both writers, so hidden
/// detail cannot dash differently in SVG and PDF.</para>
/// </summary>
public sealed class PdfDrawing
{
    /// <summary>
    /// Points per millimetre. PDF user space is the point (1/72 inch), the sheet is in
    /// millimetres, and this is the ONE place the 25.4/72 conversion lives: the MediaBox
    /// is stated in points and a single <c>cm</c> operator maps the rest of the file
    /// back to millimetres.
    /// </summary>
    public const double PointsPerMillimetre = 72 / 25.4;

    /// <summary>The drafting diameter sign (U+2300), the one character deliberately
    /// substituted (to O-stroke, U+00D8) rather than refused under the built-in
    /// Helvetica — see the class remarks. An embedded font carries it as itself.
    /// (Source files stay pure ASCII — escapes only, the Callouts.cs convention.)</summary>
    public const char DiameterSign = '\u2300';

    /// <summary>Default maximum deviation, in model units, when a sketch's arcs are
    /// approximated — a hundredth of a millimetre, an order under what a plotter
    /// resolves.</summary>
    public const double DefaultCurveTolerance = 0.01;

    /// <summary>Text fill — the SVG writer's own text colour, so the two agree.</summary>
    private const string TextFill = "#111111";

    private enum PathOp { Move, Line, Curve, Close }

    private readonly record struct PathStep(PathOp Op, Vector2d A, Vector2d B, Vector2d C);

    private sealed record Group(
        string? Layer, SvgLineClass LineClass, SvgDrawing.SvgPen Pen,
        List<List<PathStep>> Paths);

    /// <summary>An encoded run: the bytes a <c>Tj</c> shows, whether they are written as
    /// a hex string (embedded, 2-byte glyph indices) or a literal one (WinAnsi), and the
    /// advance width in ems, which is what anchoring needs.</summary>
    private readonly record struct Encoded(byte[] Bytes, bool Hex, double AdvanceEm);

    private sealed record TextRun(
        Vector2d Position, string Text, double Height, SheetTextAnchor Anchor,
        string? Layer, Encoded Encoded);

    private readonly List<Group> _groups = [];
    private readonly List<TextRun> _texts = [];
    private readonly List<string> _layers = [];
    private Aabb _bounds = Aabb.Empty;
    private PdfFont _font = PdfFont.Helvetica;

    /// <summary>Whitespace around the content when fitting to it, millimetres (default 5).</summary>
    public double Margin { get; set; } = 5;

    /// <summary>
    /// Fixes the page to a paper size in millimetres instead of sizing it to the
    /// content's bounds; the origin is the sheet's bottom-left corner, which is also
    /// PDF's own. Null (the default) fits the page to the content plus
    /// <see cref="Margin"/>.
    /// </summary>
    public (double Width, double Height)? Sheet { get; set; }

    /// <summary>
    /// Compress the content stream (and any embedded font program) with Flate.
    /// <b>Off by default</b>, because an uncompressed ASCII file is what makes a drawing
    /// revision diffable and what every committed assertion reads directly — the
    /// <c>BrepArchive</c> argument. Turn it on for a very large sheet.
    /// <para>The byte fixed point survives: zlib output is a deterministic function of
    /// the input at a fixed level and strategy, and the level here is pinned
    /// (<see cref="CompressionLevel.Optimal"/>) rather than left to a default that could
    /// move. The honest scope is a given build: the .NET deflate implementation is not a
    /// specified byte stream, so a runtime change can change the compressed bytes while
    /// leaving the CONTENT identical — which is why the tests assert the fixed point AND
    /// that inflating recovers the uncompressed writer's stream exactly.</para>
    /// </summary>
    public bool Compress { get; set; }

    /// <summary>
    /// The font runs are lettered with — <see cref="PdfFont.Helvetica"/> (the default,
    /// nothing embedded) or <see cref="PdfFont.Embed"/>.
    /// <para>Setting it RE-ENCODES every run already added, so the encoding refusal
    /// stays at the point a caller can act on and the property is order-independent. If
    /// a run cannot be encoded in the new font the change is REFUSED naming the run and
    /// the font is left as it was — all-or-nothing, so a drawing is never left half
    /// re-lettered.</para>
    /// </summary>
    public PdfFont Font
    {
        get => _font;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(value, _font))
                return;
            var reencoded = new Encoded[_texts.Count];
            for (int i = 0; i < _texts.Count; i++)
                reencoded[i] = Encode(value, _texts[i].Text);
            for (int i = 0; i < _texts.Count; i++)
                _texts[i] = _texts[i] with { Encoded = reencoded[i] };
            _font = value;
        }
    }

    /// <summary>The optional-content layer names, in the order they were first used —
    /// the order a reader's layer panel shows. Empty when nothing named a layer, which
    /// is what keeps a layer-free file byte-identical.</summary>
    public IReadOnlyList<string> Layers => _layers;

    /// <summary>Adds an open or closed polyline (drawing line work, hatch runs).</summary>
    public void AddPolyline(
        IReadOnlyList<Vector2d> points, bool closed = false,
        SvgLineClass lineClass = SvgLineClass.Visible, SvgDrawing.SvgPen? pen = null,
        string? layer = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
            return;
        var path = new List<PathStep>(points.Count + 1);
        for (int i = 0; i < points.Count; i++)
        {
            path.Add(new PathStep(i == 0 ? PathOp.Move : PathOp.Line, points[i], default, default));
            _bounds = _bounds.Union(new Vector3d(points[i].X, points[i].Y, 0));
        }
        if (closed)
            path.Add(new PathStep(PathOp.Close, default, default, default));
        GroupFor(layer, lineClass, pen).Paths.Add(path);
    }

    /// <summary>Adds loose segments (hatch, dimension and border line work).</summary>
    public void AddSegments(
        IEnumerable<(Vector2d A, Vector2d B)> segments,
        SvgLineClass lineClass = SvgLineClass.Thin, SvgDrawing.SvgPen? pen = null,
        string? layer = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        foreach (var (a, b) in segments)
            AddPolyline([a, b], closed: false, lineClass, pen, layer);
    }

    /// <summary>
    /// Adds a sketch's outline and holes as one stroked path.
    ///
    /// <para><b>What survives exactly and what does not is the deliverable, not a
    /// footnote.</b> PDF paths are lines and cubic Béziers, so a sketch's straight
    /// segments and its Béziers (a glyph outline, a lofted profile) are written
    /// EXACTLY — a quadratic having already elevated to a cubic losslessly on the way
    /// into the sketch — while a circular or elliptical arc has no exact PDF form at all
    /// and is approximated in the stated <paramref name="mode"/> to within
    /// <paramref name="tolerance"/>. The returned <see cref="PdfSketchReport"/> says
    /// which segments were which and measures the deviation from the construction, so a
    /// caller never has to guess (the <c>DxfCurveMode</c> honesty, one format over).</para>
    /// </summary>
    /// <param name="sketch">The closed loop; its <see cref="Sketch.Holes"/> follow as further subpaths.</param>
    /// <param name="mode">How arcs are carried — polylines or cubic Béziers.</param>
    /// <param name="tolerance">The largest deviation from the true arc, in model units.</param>
    /// <param name="lineClass">The pen class.</param>
    /// <param name="layer">Optional-content layer, or null for none.</param>
    /// <param name="pen">An explicit pen overriding the class's.</param>
    public PdfSketchReport Add(
        Sketch sketch, PdfCurveMode mode = PdfCurveMode.Flatten,
        double tolerance = DefaultCurveTolerance,
        SvgLineClass lineClass = SvgLineClass.Visible, string? layer = null,
        SvgDrawing.SvgPen? pen = null)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        if (!(tolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "The tolerance must be positive.");

        var path = new List<PathStep>();
        int exact = 0, approximated = 0;
        double deviation = 0;
        AppendSketchLoop(path, sketch, mode, tolerance, ref exact, ref approximated, ref deviation);
        foreach (var hole in sketch.Holes)
            AppendSketchLoop(path, hole, mode, tolerance, ref exact, ref approximated, ref deviation);

        GroupFor(layer, lineClass, pen).Paths.Add(path);
        _bounds = _bounds.Union(sketch.Bounds);
        return new PdfSketchReport(mode, exact, approximated, deviation);
    }

    /// <summary>
    /// Adds a run of text. <paramref name="height"/> is the CAP height in millimetres
    /// (converted to a font size by <see cref="SvgDrawing.CapHeightRatio"/> under the
    /// built-in Helvetica, and by the font's own cap height when one is embedded);
    /// <paramref name="anchor"/> is honoured by shifting the start point by the measured
    /// advance width, since PDF has no text-anchor. A character the current
    /// <see cref="Font"/> cannot carry is refused HERE, naming it, so a bad string fails
    /// at the add and never half-writes a file.
    /// </summary>
    public void AddText(
        in Vector2d position, string text, double height,
        SheetTextAnchor anchor = SheetTextAnchor.Left, string? layer = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            return;
        NoteLayer(layer);
        _texts.Add(new TextRun(position, text, height, anchor, layer, Encode(_font, text)));
        _bounds = _bounds.Union(new Vector3d(position.X, position.Y, 0));
    }

    // ------------------------------------------------------------------------ write

    /// <summary>The finished PDF file.</summary>
    public byte[] ToPdf()
    {
        if ((_groups.Count == 0 && _texts.Count == 0) || (_bounds.IsEmpty && Sheet is null))
            throw new InvalidOperationException("The drawing has no content; add line work or text first.");

        double minX, minY, width, height;
        if (Sheet is { } sheet)
        {
            minX = 0;
            minY = 0;
            width = sheet.Width;
            height = sheet.Height;
        }
        else
        {
            minX = _bounds.Min.X - Margin;
            minY = _bounds.Min.Y - Margin;
            width = _bounds.Max.X - _bounds.Min.X + 2 * Margin;
            height = _bounds.Max.Y - _bounds.Min.Y + 2 * Margin;
        }

        byte[] content = Encoding.ASCII.GetBytes(BuildContent(minX, minY));
        double k = PointsPerMillimetre;

        // ---- object numbering: a function of the CONTENT, so the xref stays stable ----
        // 1..5 are the fixed spine (catalog, pages, page, font, contents) and are exactly
        // what a plain drawing has always written; the optional pieces are appended in a
        // fixed order after them, so nothing an option adds can renumber what was there.
        var embedded = _font.Source is null ? null : BuildEmbeddedFont();
        int next = 6;
        int descendantNumber = 0, descriptorNumber = 0, fontFileNumber = 0, toUnicodeNumber = 0;
        if (embedded is not null)
        {
            descendantNumber = next++;
            descriptorNumber = next++;
            fontFileNumber = next++;
            toUnicodeNumber = next++;
        }
        int firstLayerNumber = next;
        next += _layers.Count;
        int objectCount = next - 1;

        var bodies = new List<byte[]>(objectCount);

        bodies.Add(Ascii(_layers.Count == 0
            ? "<< /Type /Catalog /Pages 2 0 R >>"
            : "<< /Type /Catalog /Pages 2 0 R /OCProperties " + OcProperties(firstLayerNumber) + " >>"));
        bodies.Add(Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"));
        bodies.Add(Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 "
            + Num(width * k) + " " + Num(height * k)
            + "] /Resources << /Font << /F1 4 0 R >>" + PropertiesResource(firstLayerNumber)
            + " >> /Contents 5 0 R >>"));
        bodies.Add(Ascii(embedded is null
            ? "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"
            : "<< /Type /Font /Subtype /Type0 /BaseFont /" + embedded.BaseFont
              + " /Encoding /Identity-H /DescendantFonts [" + descendantNumber + " 0 R] /ToUnicode "
              + toUnicodeNumber + " 0 R >>"));
        bodies.Add(StreamObject(content, "", Compress));

        if (embedded is not null)
        {
            bodies.Add(Ascii("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /" + embedded.BaseFont
                + " /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>"
                + " /FontDescriptor " + descriptorNumber + " 0 R /DW 1000 /W " + embedded.Widths
                + " /CIDToGIDMap /Identity >>"));
            bodies.Add(Ascii("<< /Type /FontDescriptor /FontName /" + embedded.BaseFont
                + " /Flags 4 /FontBBox " + embedded.BoundingBox + " /ItalicAngle 0 /Ascent "
                + Num(embedded.Ascent) + " /Descent " + Num(embedded.Descent) + " /CapHeight "
                + Num(embedded.CapHeight) + " /StemV 80 /FontFile2 " + fontFileNumber + " 0 R >>"));
            bodies.Add(StreamObject(embedded.Program,
                " /Length1 " + embedded.Program.Length.ToString(CultureInfo.InvariantCulture), Compress));
            bodies.Add(StreamObject(Encoding.ASCII.GetBytes(embedded.ToUnicode), "", Compress));
        }

        foreach (string layer in _layers)
            bodies.Add(Ascii("<< /Type /OCG /Name (" + EscapeName(layer) + ") >>"));

        // ---- the file ----
        using var file = new MemoryStream();
        var offsets = new long[objectCount + 1];
        void Write(string s) => file.Write(Encoding.ASCII.GetBytes(s));

        // A pure-ASCII file: the spec's binary-marker comment line exists for files that
        // carry binary data, and an uncompressed drawing never does — but a Flate stream
        // or an embedded font program does, so the marker is written exactly then.
        // Optional content is a PDF 1.5 feature, so a layered file says so; a file
        // that names no layer keeps the version it always declared.
        Write(_layers.Count == 0 ? "%PDF-1.4\n" : "%PDF-1.5\n");
        if (Compress || embedded is not null)
            file.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = file.Position;
            Write((i + 1).ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
            file.Write(bodies[i]);
            Write("\nendobj\n");
        }

        long xref = file.Position;
        Write("xref\n0 " + (objectCount + 1).ToString(CultureInfo.InvariantCulture) + "\n0000000000 65535 f \n");
        for (int i = 1; i <= objectCount; i++)
            Write(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write("trailer\n<< /Size " + (objectCount + 1).ToString(CultureInfo.InvariantCulture)
            + " /Root 1 0 R >>\nstartxref\n"
            + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");
        return file.ToArray();
    }

    /// <summary>Writes the PDF to a file.</summary>
    public void SaveFile(string path) => File.WriteAllBytes(path, ToPdf());

    /// <summary>
    /// The largest distance between a circular arc of the given radius and sweep and the
    /// cubic Bézier the <see cref="PdfCurveMode.Kappa"/> construction replaces it with —
    /// an EXACT closed form, not a bound.
    ///
    /// <para>Writing the arc from −θ/2 to θ/2 on the unit circle with control points at
    /// <c>k = (4/3)·tan(θ/4)</c> along the end tangents, the Bézier's squared radius
    /// collapses to <c>r²(u) = 1 + (4τ⁶/(1+τ²)²)·u²(1−4u²)²</c> with <c>τ = tan(θ/4)</c>
    /// and <c>u = t − ½</c>. Three things fall straight out of that form and are worth
    /// stating because they are what make the construction trustworthy: the error is
    /// never negative, so the cubic lies OUTSIDE the arc everywhere and never cuts inside
    /// it; it vanishes at both ends AND at the midpoint (which is what fixes k); and
    /// <c>u²(1−4u²)²</c> peaks at exactly 1/27, giving
    /// <c>r_max = √(1 + 4τ⁶/(27(1+τ²)²))</c>.</para>
    ///
    /// <para>Expanding for small θ gives <c>θ⁶/55296</c> per unit radius — <b>SIXTH</b>
    /// order, so halving the number of spans multiplies the error by 64 rather than by
    /// the 16 a fourth-order rule would give. At a quarter turn it reads
    /// 2.725e-4·r, the figure usually quoted for this construction.</para>
    /// </summary>
    public static double ArcCubicDeviation(double radius, double sweep)
    {
        double tau = Math.Tan(Math.Abs(sweep) / 4);
        double t2 = tau * tau;
        double t6 = t2 * t2 * t2;
        double denominator = (1 + t2) * (1 + t2);
        return radius * (Math.Sqrt(1 + 4 * t6 / (27 * denominator)) - 1);
    }

    // ------------------------------------------------------------------- content

    private string BuildContent(double minX, double minY)
    {
        double k = PointsPerMillimetre;
        var sb = new StringBuilder();

        // The one transform: millimetres to points, content shifted so the page origin
        // is (minX, minY). `0 - k * minX` rather than `-k * minX` so a zero offset is
        // +0 and never prints as "-0" (an exact-zero normalization, not a tolerance).
        sb.Append("q\n");
        sb.Append(Num(k)).Append(" 0 0 ").Append(Num(k)).Append(' ')
            .Append(Num(0 - k * minX)).Append(' ').Append(Num(0 - k * minY)).Append(" cm\n");

        foreach (var group in _groups)
        {
            sb.Append("q\n");
            BeginLayer(sb, group.Layer);
            var (r, g, b) = Rgb(group.Pen.Stroke);
            sb.Append(Num(r)).Append(' ').Append(Num(g)).Append(' ').Append(Num(b)).Append(" RG\n");
            sb.Append(Num(group.Pen.Width)).Append(" w\n");
            sb.Append("1 J\n1 j\n");   // round cap and join, the SVG presets' values
            if (group.Pen.DashArray is { } dash)
            {
                sb.Append('[');
                bool first = true;
                foreach (var part in dash.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!first)
                        sb.Append(' ');
                    sb.Append(Num(double.Parse(part, CultureInfo.InvariantCulture)));
                    first = false;
                }
                sb.Append("] 0 d\n");
            }
            foreach (var path in group.Paths)
            {
                foreach (var step in path)
                {
                    switch (step.Op)
                    {
                        case PathOp.Move:
                            sb.Append(Num(step.A.X)).Append(' ').Append(Num(step.A.Y)).Append(" m\n");
                            break;
                        case PathOp.Line:
                            sb.Append(Num(step.A.X)).Append(' ').Append(Num(step.A.Y)).Append(" l\n");
                            break;
                        case PathOp.Curve:
                            sb.Append(Num(step.A.X)).Append(' ').Append(Num(step.A.Y)).Append(' ')
                                .Append(Num(step.B.X)).Append(' ').Append(Num(step.B.Y)).Append(' ')
                                .Append(Num(step.C.X)).Append(' ').Append(Num(step.C.Y)).Append(" c\n");
                            break;
                        default:
                            sb.Append("h\n");
                            break;
                    }
                }
                sb.Append("S\n");
            }
            EndLayer(sb, group.Layer);
            sb.Append("Q\n");
        }

        // Text runs, in blocks of one layer each — with no layers anywhere that is one
        // block, exactly the shape a layer-free file has always had.
        for (int i = 0; i < _texts.Count;)
        {
            string? layer = _texts[i].Layer;
            int end = i;
            while (end < _texts.Count && _texts[end].Layer == layer)
                end++;

            sb.Append("q\n");
            BeginLayer(sb, layer);
            var (r, g, b) = Rgb(TextFill);
            sb.Append(Num(r)).Append(' ').Append(Num(g)).Append(' ').Append(Num(b)).Append(" rg\n");
            for (; i < end; i++)
            {
                var run = _texts[i];
                double size = run.Height / CapRatio;
                double x = run.Position.X - run.Anchor switch
                {
                    SheetTextAnchor.Center => run.Encoded.AdvanceEm * size / 2,
                    SheetTextAnchor.Right => run.Encoded.AdvanceEm * size,
                    _ => 0,
                };
                sb.Append("BT\n/F1 ").Append(Num(size)).Append(" Tf\n");
                sb.Append(Num(x)).Append(' ').Append(Num(run.Position.Y)).Append(" Td\n");
                AppendShown(sb, run.Encoded);
                sb.Append(" Tj\nET\n");
            }
            EndLayer(sb, layer);
            sb.Append("Q\n");
        }

        sb.Append("Q\n");
        return sb.ToString();
    }

    private void BeginLayer(StringBuilder sb, string? layer)
    {
        if (layer is not null)
            sb.Append("/OC /OC").Append(_layers.IndexOf(layer).ToString(CultureInfo.InvariantCulture)).Append(" BDC\n");
    }

    private static void EndLayer(StringBuilder sb, string? layer)
    {
        if (layer is not null)
            sb.Append("EMC\n");
    }

    // ---------------------------------------------------------------------- sketch

    private static void AppendSketchLoop(
        List<PathStep> path, Sketch loop, PdfCurveMode mode, double tolerance,
        ref int exact, ref int approximated, ref double deviation)
    {
        var segments = loop.Segments;
        path.Add(new PathStep(PathOp.Move, segments[0].Start, default, default));
        foreach (var segment in segments)
        {
            switch (segment)
            {
                case LineSeg line:
                    path.Add(new PathStep(PathOp.Line, line.End, default, default));
                    exact++;
                    break;
                case CubicSeg cubic:
                    // Exact: a cubic Bezier IS a PDF path operator, control point for
                    // control point (and a sketch's quadratics elevated to cubics
                    // losslessly on the way in).
                    path.Add(new PathStep(PathOp.Curve, cubic.Control1, cubic.Control2, cubic.P3));
                    exact++;
                    break;
                case ArcSeg arc:
                    deviation = Math.Max(deviation, AppendArc(
                        path, arc.Center, new Vector2d(arc.Radius, 0), new Vector2d(0, arc.Radius),
                        arc.StartAngle, arc.Sweep, arc.Radius, mode, tolerance));
                    approximated++;
                    break;
                case EllipseSeg ellipse:
                    deviation = Math.Max(deviation, AppendArc(
                        path, ellipse.Center, ellipse.SemiAxisX, ellipse.SemiAxisY,
                        ellipse.StartAngle, ellipse.Sweep,
                        Math.Max(ellipse.SemiAxisX.Length, ellipse.SemiAxisY.Length), mode, tolerance));
                    approximated++;
                    break;
                default:
                    throw new NotSupportedException($"No PDF form for a {segment.GetType().Name}.");
            }
        }
        path.Add(new PathStep(PathOp.Close, default, default, default));
    }

    /// <summary>
    /// One arc — circular or elliptical — appended in the stated mode, returning the
    /// deviation the construction actually carries. An ellipse is handled as the AFFINE
    /// IMAGE of the circular construction (its point is <c>C + A·cos a + B·sin a</c>, and
    /// a Bézier is an affine combination of its control points at every parameter, so
    /// mapping the control points IS mapping the curve); the deviation is then the
    /// circular figure carried through the LARGEST semi-axis, a bound rather than an
    /// equality because an affine map stretches the error anisotropically.
    /// </summary>
    private static double AppendArc(
        List<PathStep> path, Vector2d centre, Vector2d axisX, Vector2d axisY,
        double startAngle, double sweep, double scale, PdfCurveMode mode, double tolerance)
    {
        // Every span is at most a quarter turn, so a full circle is always at least four
        // pieces and the endpoint parameterization never degenerates; the tolerance then
        // refines from there.
        int spans = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2) - 1e-12));
        double Deviation(int n) => mode == PdfCurveMode.Kappa
            ? ArcCubicDeviation(scale, sweep / n)
            : scale * (1 - Math.Cos(Math.Abs(sweep) / (2 * n)));
        while (Deviation(spans) > tolerance && spans < 4096)
            spans++;

        double step = sweep / spans;
        Vector2d At(double a) => centre + axisX * Math.Cos(a) + axisY * Math.Sin(a);
        Vector2d Tangent(double a) => axisX * -Math.Sin(a) + axisY * Math.Cos(a);

        if (mode == PdfCurveMode.Kappa)
        {
            double k = 4.0 / 3 * Math.Tan(step / 4);
            for (int i = 0; i < spans; i++)
            {
                double a0 = startAngle + step * i, a1 = a0 + step;
                var p0 = At(a0);
                var p3 = At(a1);
                path.Add(new PathStep(PathOp.Curve, p0 + Tangent(a0) * k, p3 - Tangent(a1) * k, p3));
            }
        }
        else
        {
            for (int i = 1; i <= spans; i++)
                path.Add(new PathStep(PathOp.Line, At(startAngle + step * i), default, default));
        }
        return Deviation(spans);
    }

    // ------------------------------------------------------------------ encoding

    /// <summary>The cap-height-to-em ratio in force: the SVG writer's nominal 0.7 for
    /// the built-in Helvetica (one rule, two writers), or the embedded font's own
    /// measured cap height, which is the exact answer whenever there is a font to ask.</summary>
    private double CapRatio => _font.Source is { } source
        ? source.CapHeight / source.UnitsPerEm
        : SvgDrawing.CapHeightRatio;

    private static Encoded Encode(PdfFont font, string text) =>
        font.Source is { } source ? EncodeGlyphs(source, text) : EncodeWinAnsi(text);

    private static Encoded EncodeWinAnsi(string text)
    {
        var bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            if (!TryEncodeWinAnsi(text[i], out bytes[i]))
            {
                throw new NotSupportedException(
                    $"'{text[i]}' (U+{(int)text[i]:X4}) at position {i} of \"{text}\" has no WinAnsi " +
                    "encoding, so the standard-14 Helvetica cannot carry it. Reword the text, or set " +
                    "PdfDrawing.Font to an embedded font (PdfFont.Embed) that has the glyph.");
            }
        }
        return new Encoded(bytes, Hex: false, AdvanceEm(bytes));
    }

    /// <summary>
    /// A run as 2-byte glyph indices — what <c>/Identity-H</c> shows and what
    /// <c>/CIDToGIDMap /Identity</c> resolves back to the same glyph. The encoding has
    /// no repertoire of its own, so the only thing that can refuse is the FONT, which is
    /// exactly the honest failure: a character the font has no glyph for is named,
    /// rather than drawn as .notdef.
    /// </summary>
    private static Encoded EncodeGlyphs(TrueTypeFont font, string text)
    {
        var bytes = new byte[text.Length * 2];
        double advance = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (!font.TryGetGlyphIndex(text[i], out int glyph))
            {
                string name = font.FamilyName.Length > 0 ? $"'{font.FamilyName}'" : "the embedded font";
                throw new NotSupportedException(
                    $"'{text[i]}' (U+{(int)text[i]:X4}) at position {i} of \"{text}\" has no glyph in " +
                    $"{name}, so it cannot be drawn. Reword the text, or embed a font that carries it.");
            }
            bytes[i * 2] = (byte)(glyph >> 8);
            bytes[i * 2 + 1] = (byte)glyph;
            advance += font.AdvanceWidthUnits(glyph);
        }
        return new Encoded(bytes, Hex: true, advance / font.UnitsPerEm);
    }

    /// <summary>The run as it appears in the content stream: a hex string for glyph
    /// indices (two bytes each, and the natural spelling for a code that is not a
    /// character), a literal string for WinAnsi bytes — parens and backslash escaped,
    /// anything outside printable ASCII as an octal escape, so the file stays pure ASCII
    /// whatever the text carries.</summary>
    private static void AppendShown(StringBuilder sb, in Encoded encoded)
    {
        if (encoded.Hex)
        {
            sb.Append('<');
            foreach (byte b in encoded.Bytes)
                sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            sb.Append('>');
            return;
        }
        sb.Append('(');
        foreach (byte b in encoded.Bytes)
        {
            if (b is (byte)'(' or (byte)')' or (byte)'\\')
                sb.Append('\\').Append((char)b);
            else if (b < 0x20 || b > 0x7E)
                sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            else
                sb.Append((char)b);
        }
        sb.Append(')');
    }

    // ------------------------------------------------------------ embedded font

    private sealed record EmbeddedFont(
        string BaseFont, byte[] Program, string Widths, string BoundingBox,
        double Ascent, double Descent, double CapHeight, string ToUnicode);

    private EmbeddedFont BuildEmbeddedFont()
    {
        var font = _font.Source!;
        var used = new SortedDictionary<int, string>();
        var characters = new SortedDictionary<int, int>();
        foreach (var run in _texts)
        {
            foreach (char c in run.Text)
            {
                font.TryGetGlyphIndex(c, out int glyph);   // encoding already refused a miss
                used[glyph] = char.ToString(c);
                characters[c] = glyph;
            }
        }

        var subset = PdfFontSubset.Build(font, characters.Select(p => (p.Key, p.Value)));
        double scale = 1000.0 / font.UnitsPerEm;

        var widths = new StringBuilder("[");
        foreach (int glyph in used.Keys)
        {
            widths.Append(glyph.ToString(CultureInfo.InvariantCulture)).Append(" [")
                .Append(Num(font.AdvanceWidthUnits(glyph) * scale)).Append("] ");
        }
        widths.Length = Math.Max(1, widths.Length - 1);
        widths.Append(']');

        var head = font.RawTable("head")!;
        string box = "[" + Num(ReadI16(head, 36) * scale) + " " + Num(ReadI16(head, 38) * scale)
            + " " + Num(ReadI16(head, 40) * scale) + " " + Num(ReadI16(head, 42) * scale) + "]";

        return new EmbeddedFont(
            SubsetName(font, subset.Glyphs), subset.Data, widths.ToString(), box,
            font.Ascender * scale, font.Descender * scale, font.CapHeight * scale,
            ToUnicodeCMap(used));
    }

    /// <summary>
    /// The subset tag PDF conventionally prefixes an embedded subset's name with:
    /// six uppercase letters that must distinguish two different subsets of one font, so
    /// it is a FOLD of the kept glyph set (and the family name) rather than anything
    /// generated — a random or time-based tag would be the /Info problem again. The
    /// family part is sanitized rather than refused, because a PDF base font name is a
    /// label rather than a key anything resolves by.
    /// </summary>
    private static string SubsetName(TrueTypeFont font, IReadOnlyList<int> glyphs)
    {
        ulong hash = 14695981039346656037;   // FNV-1a offset basis
        void Mix(int value)
        {
            for (int i = 0; i < 4; i++)
            {
                hash ^= (byte)(value >> (i * 8));
                hash *= 1099511628211;
            }
        }
        foreach (char c in font.FamilyName)
            Mix(c);
        foreach (int glyph in glyphs)
            Mix(glyph);

        var tag = new StringBuilder(7);
        for (int i = 0; i < 6; i++)
        {
            tag.Append((char)('A' + (int)(hash % 26)));
            hash /= 26;
        }
        tag.Append('+');

        var name = new StringBuilder(tag.ToString());
        foreach (char c in font.FamilyName)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-')
                name.Append(c);
        }
        if (name.Length == tag.Length)
            name.Append("Font");
        return name.ToString();
    }

    /// <summary>
    /// The <c>/ToUnicode</c> CMap: glyph index back to the character it stands for. With
    /// <c>/Identity-H</c> a text string carries glyph indices, which mean nothing to a
    /// reader's copy or search — so without this the drawing would be a picture of words.
    /// Written in ascending glyph order, so it is a function of the glyph set.
    /// </summary>
    private static string ToUnicodeCMap(SortedDictionary<int, string> used)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

        var entries = used.ToList();
        for (int at = 0; at < entries.Count; at += 100)
        {
            int count = Math.Min(100, entries.Count - at);
            sb.Append(count.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
            for (int i = at; i < at + count; i++)
            {
                sb.Append('<').Append(entries[i].Key.ToString("X4", CultureInfo.InvariantCulture)).Append("> <");
                foreach (char c in entries[i].Value)
                    sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                sb.Append(">\n");
            }
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }

    private static int ReadI16(byte[] data, int at) => (short)((data[at] << 8) | data[at + 1]);

    // ---------------------------------------------------------------------- helpers

    private Group GroupFor(string? layer, SvgLineClass lineClass, SvgDrawing.SvgPen? pen)
    {
        NoteLayer(layer);
        var resolved = pen ?? SvgDrawing.SvgPen.For(lineClass);
        var existing = _groups.FirstOrDefault(
            g => g.Layer == layer && g.LineClass == lineClass && g.Pen == resolved);
        if (existing is null)
        {
            existing = new Group(layer, lineClass, resolved, []);
            _groups.Add(existing);
        }
        return existing;
    }

    /// <summary>Records a layer in FIRST-USE order — a deterministic function of the
    /// drawing, so the optional-content objects and their numbers are too.</summary>
    private void NoteLayer(string? layer)
    {
        if (layer is not null && !_layers.Contains(layer))
            _layers.Add(layer);
    }

    private string OcProperties(int firstLayerNumber)
    {
        var refs = new StringBuilder("[");
        for (int i = 0; i < _layers.Count; i++)
            refs.Append(i == 0 ? "" : " ").Append(firstLayerNumber + i).Append(" 0 R");
        refs.Append(']');
        return "<< /OCGs " + refs + " /D << /Order " + refs + " /ON " + refs + " >> >>";
    }

    private string PropertiesResource(int firstLayerNumber)
    {
        if (_layers.Count == 0)
            return "";
        var sb = new StringBuilder(" /Properties <<");
        for (int i = 0; i < _layers.Count; i++)
            sb.Append(" /OC").Append(i).Append(' ').Append(firstLayerNumber + i).Append(" 0 R");
        sb.Append(" >>");
        return sb.ToString();
    }

    /// <summary>A layer name inside a PDF literal string: parens and backslash escaped,
    /// anything outside printable ASCII as an octal escape over its UTF-8 bytes.</summary>
    private static string EscapeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (byte b in Encoding.UTF8.GetBytes(name))
        {
            if (b is (byte)'(' or (byte)')' or (byte)'\\')
                sb.Append('\\').Append((char)b);
            else if (b < 0x20 || b > 0x7E)
                sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            else
                sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary>One stream object's body. Compression is a pure re-spelling: the stream
    /// dictionary gains a filter and the bytes deflate, and inflating recovers this
    /// method's own input exactly (which is what the tests assert).</summary>
    private static byte[] StreamObject(byte[] data, string extra, bool compress)
    {
        byte[] payload = data;
        string filter = "";
        if (compress)
        {
            using var output = new MemoryStream();
            using (var deflate = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(data);
            payload = output.ToArray();
            filter = " /Filter /FlateDecode";
        }
        var head = Encoding.ASCII.GetBytes(
            "<< /Length " + payload.Length.ToString(CultureInfo.InvariantCulture)
            + filter + extra + " >>\nstream\n");
        var tail = Encoding.ASCII.GetBytes("\nendstream");
        var body = new byte[head.Length + payload.Length + tail.Length];
        head.CopyTo(body, 0);
        payload.CopyTo(body, head.Length);
        tail.CopyTo(body, head.Length + payload.Length);
        return body;
    }

    /// <summary>
    /// A double as a PDF number. PDF's grammar has no exponent form, so "R" (a bijection
    /// on finite doubles, the diffable spelling) serves except where it would use one —
    /// magnitudes below 1e-4, far under drawn precision — which fall back to fixed
    /// notation at a picometre on paper. A grammar constraint, not a tolerance.
    /// </summary>
    private static string Num(double value)
    {
        if (!double.IsFinite(value))
            throw new NotSupportedException($"A PDF number must be finite; got {value}.");
        if (value == 0)
            return "0";   // exact-zero semantic test: normalizes -0, which "R" prints signed
        string r = value.ToString("R", CultureInfo.InvariantCulture);
        if (!r.Contains('E') && !r.Contains('e'))
            return r;
        string fixedForm = value.ToString("F12", CultureInfo.InvariantCulture)
            .TrimEnd('0').TrimEnd('.');
        return fixedForm.Length == 0 || fixedForm == "-" ? "0" : fixedForm;
    }

    /// <summary>#rrggbb → PDF RGB components in [0, 1].</summary>
    private static (double R, double G, double B) Rgb(string hex)
    {
        int Channel(int at) => int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (Channel(1) / 255.0, Channel(3) / 255.0, Channel(5) / 255.0);
    }

    private static bool TryEncodeWinAnsi(char c, out byte b)
    {
        // WinAnsi (CP1252): ASCII (0x20..0x7E) and the Latin-1 upper half (0xA0..0xFF)
        // map to their own bytes; the 0x80..0x9F window holds the typographic extras
        // below, matched on CODE POINTS so the table reads as the WinAnsi spec does and
        // the source file stays pure ASCII (the Callouts.cs escapes-only convention).
        int code = c;
        if (code is >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFF)
        {
            b = (byte)code;
            return true;
        }
        b = code switch
        {
            0x20AC => 0x80,   // euro
            0x201A => 0x82,   // single low quote
            0x0192 => 0x83,   // florin
            0x201E => 0x84,   // double low quote
            0x2026 => 0x85,   // ellipsis
            0x2020 => 0x86,   // dagger
            0x2021 => 0x87,   // double dagger
            0x02C6 => 0x88,   // circumflex accent
            0x2030 => 0x89,   // per mille
            0x0160 => 0x8A,   // S caron
            0x2039 => 0x8B,   // single left guillemet
            0x0152 => 0x8C,   // OE ligature
            0x017D => 0x8E,   // Z caron
            0x2018 => 0x91,   // left single quote
            0x2019 => 0x92,   // right single quote
            0x201C => 0x93,   // left double quote
            0x201D => 0x94,   // right double quote
            0x2022 => 0x95,   // bullet
            0x2013 => 0x96,   // en dash
            0x2014 => 0x97,   // em dash
            0x02DC => 0x98,   // small tilde
            0x2122 => 0x99,   // trademark
            0x0161 => 0x9A,   // s caron
            0x203A => 0x9B,   // single right guillemet
            0x0153 => 0x9C,   // oe ligature
            0x017E => 0x9E,   // z caron
            0x0178 => 0x9F,   // Y diaeresis
            // The one deliberate substitution: the drafting diameter sign (U+2300) has
            // no WinAnsi form, O-stroke (0xD8) is its standard typographic stand-in on
            // drawings, and Helvetica carries it. Pinned by test. An EMBEDDED font never
            // reaches here and carries U+2300 as itself.
            DiameterSign => 0xD8,
            _ => 0,
        };
        return b != 0;
    }

    /// <summary>
    /// Advance width of an encoded run in ems (thousandths summed and divided), used
    /// only for anchoring. Widths are transcribed from the Adobe Helvetica AFM
    /// (verify-against-datasheet, the StandardHoles convention); bytes outside the
    /// table take Helvetica's figure width 556, degrading anchoring by a fraction of a
    /// glyph rather than refusing.
    /// </summary>
    private static double AdvanceEm(byte[] encoded)
    {
        int total = 0;
        foreach (byte b in encoded)
        {
            total += b is >= 0x20 and <= 0x7E
                ? AsciiWidths[b - 0x20]
                : b switch
                {
                    0xB0 => 400,   // degree
                    0xB1 => 584,   // plus-minus
                    0xD7 => 584,   // multiply
                    0xD8 => 778,   // O-stroke (the diameter substitution)
                    _ => 556,
                };
        }
        return total / 1000.0;
    }

    // Helvetica advance widths, 1/1000 em, characters 0x20..0x7E in order — transcribed
    // from the Adobe Helvetica AFM (verify-against-datasheet).
    private static readonly ushort[] AsciiWidths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, // space..slash
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556,                               // 0..9
        278, 278, 584, 584, 584, 556, 1015,                                             // :..@
        667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,      // A..O
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611,                          // P..Z
        278, 278, 278, 469, 556, 333,                                                   // [..`
        556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,      // a..o
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500,                          // p..z
        334, 260, 334, 584,                                                             // {..~
    ];
}
