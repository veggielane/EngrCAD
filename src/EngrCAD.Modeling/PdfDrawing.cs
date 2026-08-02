using System.Globalization;
using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// PDF export for drawing sheets — the third writer over the same content the SVG and
/// DXF writers consume, because what gets SENT to a manufacturer is a PDF.
///
/// <para><b>Dependency-free, uncompressed, ASCII.</b> The whole file is hand-written
/// (the TrueType/PNG/glTF/STEP tradition; a PDF content stream is plainer than any of
/// those) and its streams are deliberately NOT Flate-compressed: an uncompressed stream
/// is legal PDF, a sheet's line work is tens of kilobytes, and a text file can be read,
/// diffed and asserted on directly — the same argument that made <c>BrepArchive</c> a
/// text format. The file also carries <b>no /Info dictionary and no /ID</b>: both are
/// optional per the spec, and their natural values (a CreationDate, an MD5 salted with
/// the clock) are exactly what would break the byte-fixed-point property the tests
/// pin — writing the same sheet twice produces the same bytes.</para>
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
/// <para><b>Text is the standard-14 Helvetica, not the stroke font.</b> The viewer's 3D
/// overlay letters with <c>StrokeFont</c>, but that lives in EngrCAD.Viewer.Core, which
/// Modeling cannot reference, and duplicating its glyph table here would be exactly the
/// two-copies drift <c>SheetStyle</c>'s ratio convention exists to avoid. The SVG sheet
/// writer's actual precedent is real text in a system Helvetica stack, and PDF has the
/// same thing built in: the standard 14 fonts need no embedding because every conforming
/// reader carries them, so the rule that a file naming a resource must define it is
/// satisfied by the /Font object naming /Helvetica. Embedding a real font (full Unicode,
/// the drafting symbols WinAnsi lacks) is the filed follow-up.</para>
///
/// <para><b>Characters outside WinAnsi are refused by name, with one deliberate
/// substitution.</b> Strings are encoded as WinAnsi bytes; a character with no WinAnsi
/// form throws naming the character rather than degrading to <c>?</c>, because a
/// dimension that silently lost its symbol is a wrong drawing (the descriptor
/// sanitization rule). The exception is U+2300, the drafting diameter sign the
/// dimension layer emits: WinAnsi lacks it, Helvetica carries U+00D8 (O with stroke),
/// and that is the standard typographic fallback on drawings — so the diameter sign
/// becomes O-stroke, documented and pinned by test rather than discovered.</para>
///
/// <para>The line-class vocabulary and pens are <see cref="SvgLineClass"/> and
/// <see cref="SvgDrawing.SvgPen"/> — one pen table serving both writers, so hidden
/// detail cannot dash differently in SVG and PDF. There is deliberately no
/// <c>Add(Sketch)</c>: PDF paths are lines and cubic Béziers only, a circular arc has no
/// exact PDF form, and an overload that silently flattened one would break the
/// written-exactly contract the sketch writers keep — a caller who wants a profile in a
/// PDF flattens it at a tolerance they state.</para>
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
    /// substituted (to O-stroke, U+00D8) rather than refused — see the class remarks.
    /// (Source files stay pure ASCII — escapes only, the Callouts.cs convention.)</summary>
    public const char DiameterSign = '\u2300';

    /// <summary>Text fill — the SVG writer's own text colour, so the two agree.</summary>
    private const string TextFill = "#111111";

    private sealed record Group(
        SvgLineClass LineClass, SvgDrawing.SvgPen Pen,
        List<(Vector2d[] Points, bool Closed)> Paths);

    private sealed record TextRun(Vector2d Position, byte[] Encoded, double Height, SheetTextAnchor Anchor);

    private readonly List<Group> _groups = [];
    private readonly List<TextRun> _texts = [];
    private Aabb _bounds = Aabb.Empty;

    /// <summary>Whitespace around the content when fitting to it, millimetres (default 5).</summary>
    public double Margin { get; set; } = 5;

    /// <summary>
    /// Fixes the page to a paper size in millimetres instead of sizing it to the
    /// content's bounds; the origin is the sheet's bottom-left corner, which is also
    /// PDF's own. Null (the default) fits the page to the content plus
    /// <see cref="Margin"/>.
    /// </summary>
    public (double Width, double Height)? Sheet { get; set; }

    /// <summary>Adds an open or closed polyline (drawing line work, hatch runs).</summary>
    public void AddPolyline(
        IReadOnlyList<Vector2d> points, bool closed = false,
        SvgLineClass lineClass = SvgLineClass.Visible, SvgDrawing.SvgPen? pen = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
            return;
        foreach (var point in points)
            _bounds = _bounds.Union(new Vector3d(point.X, point.Y, 0));
        GroupFor(lineClass, pen).Paths.Add(([.. points], closed));
    }

    /// <summary>Adds loose segments (hatch, dimension and border line work).</summary>
    public void AddSegments(
        IEnumerable<(Vector2d A, Vector2d B)> segments,
        SvgLineClass lineClass = SvgLineClass.Thin, SvgDrawing.SvgPen? pen = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        foreach (var (a, b) in segments)
            AddPolyline([a, b], closed: false, lineClass, pen);
    }

    /// <summary>
    /// Adds a run of text. <paramref name="height"/> is the CAP height in millimetres
    /// (converted to a font size by <see cref="SvgDrawing.CapHeightRatio"/>, the same
    /// rule the SVG writer applies); <paramref name="anchor"/> is honoured by shifting
    /// the start point by the measured advance width, since PDF has no text-anchor.
    /// Characters with no WinAnsi form are refused HERE, naming the character, so a bad
    /// string fails at the add and never half-writes a file.
    /// </summary>
    public void AddText(
        in Vector2d position, string text, double height,
        SheetTextAnchor anchor = SheetTextAnchor.Left)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            return;
        _texts.Add(new TextRun(position, EncodeWinAnsi(text), height, anchor));
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

        using var file = new MemoryStream();
        var offsets = new long[6];
        void Write(string s) => file.Write(Encoding.ASCII.GetBytes(s));

        // A pure-ASCII file: the spec's binary-marker comment line exists for files that
        // carry binary data, which this one never does, and its absence keeps the file
        // greppable end to end.
        Write("%PDF-1.4\n");
        offsets[1] = file.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = file.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = file.Position;
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 "
            + Num(width * k) + " " + Num(height * k)
            + "] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
        offsets[4] = file.Position;
        Write("4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        offsets[5] = file.Position;
        Write("5 0 obj\n<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n");
        file.Write(content);
        Write("\nendstream\nendobj\n");

        long xref = file.Position;
        Write("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++)
            Write(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n"
            + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");
        return file.ToArray();
    }

    /// <summary>Writes the PDF to a file.</summary>
    public void SaveFile(string path) => File.WriteAllBytes(path, ToPdf());

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
            foreach (var (points, closed) in group.Paths)
            {
                for (int i = 0; i < points.Length; i++)
                {
                    sb.Append(Num(points[i].X)).Append(' ').Append(Num(points[i].Y))
                        .Append(i == 0 ? " m\n" : " l\n");
                }
                if (closed)
                    sb.Append("h\n");
                sb.Append("S\n");
            }
            sb.Append("Q\n");
        }

        if (_texts.Count > 0)
        {
            sb.Append("q\n");
            var (r, g, b) = Rgb(TextFill);
            sb.Append(Num(r)).Append(' ').Append(Num(g)).Append(' ').Append(Num(b)).Append(" rg\n");
            foreach (var run in _texts)
            {
                double size = run.Height / SvgDrawing.CapHeightRatio;
                double x = run.Position.X - run.Anchor switch
                {
                    SheetTextAnchor.Center => AdvanceEm(run.Encoded) * size / 2,
                    SheetTextAnchor.Right => AdvanceEm(run.Encoded) * size,
                    _ => 0,
                };
                sb.Append("BT\n/F1 ").Append(Num(size)).Append(" Tf\n");
                sb.Append(Num(x)).Append(' ').Append(Num(run.Position.Y)).Append(" Td\n");
                AppendEscaped(sb, run.Encoded);
                sb.Append(" Tj\nET\n");
            }
            sb.Append("Q\n");
        }

        sb.Append("Q\n");
        return sb.ToString();
    }

    // ---------------------------------------------------------------------- helpers

    private Group GroupFor(SvgLineClass lineClass, SvgDrawing.SvgPen? pen)
    {
        var resolved = pen ?? SvgDrawing.SvgPen.For(lineClass);
        var existing = _groups.FirstOrDefault(g => g.LineClass == lineClass && g.Pen == resolved);
        if (existing is null)
        {
            existing = new Group(lineClass, resolved, []);
            _groups.Add(existing);
        }
        return existing;
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

    /// <summary>The run as a PDF literal string over its WinAnsi bytes — parens and
    /// backslash escaped, anything outside printable ASCII as an octal escape, so the
    /// file stays pure ASCII whatever the text carries.</summary>
    private static void AppendEscaped(StringBuilder sb, byte[] encoded)
    {
        sb.Append('(');
        foreach (byte b in encoded)
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

    private static byte[] EncodeWinAnsi(string text)
    {
        var bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            if (!TryEncodeWinAnsi(text[i], out bytes[i]))
            {
                throw new NotSupportedException(
                    $"'{text[i]}' (U+{(int)text[i]:X4}) at position {i} of \"{text}\" has no WinAnsi " +
                    "encoding, so the standard-14 Helvetica cannot carry it. Reword the text, or wait " +
                    "for embedded-font support (the filed follow-up).");
            }
        }
        return bytes;
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
            // drawings, and Helvetica carries it. Pinned by test.
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
