using System.Globalization;
using System.IO.Compression;
using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// An independently written minimal PDF reader — the OFF/LZW twin-decoder precedent: the
/// oracle for a file format is to DECODE the file, not to inspect its text, because a
/// structural check passes a wrong matrix, a wrong offset and a wrong encoding all at
/// once. It shares nothing with the writer: the xref table is walked and every in-use
/// offset verified to point at its object, the object graph is followed from /Root to
/// the page, and the content stream is tokenized operator by operator, reconstructing
/// stroked paths (with the graphics state that stroked them, CTM included) and text
/// runs.
///
/// <para>It decodes the optional pieces too, so each is verified rather than merely
/// present: a <c>/FlateDecode</c> stream is INFLATED (and the test compares the result
/// against the uncompressed writer's own stream, so compression is proved to be a pure
/// re-spelling), marked content puts every stroke and run in its optional-content GROUP
/// by name, and a run shown as glyph indices under an embedded font is decoded back to
/// characters through the file's OWN <c>/ToUnicode</c> CMap — which is exactly what a
/// reader's copy-and-paste does, so "the text survived" means what it says.</para>
/// </summary>
internal static class PdfReadback
{
    /// <summary>One stroked path: its subpaths, and the graphics state that stroked it.
    /// The CTM is [a b c d e f]; coordinates are RAW (as written), so a test compares
    /// them against the writer's input and asserts the CTM separately.</summary>
    public sealed record Stroke(
        IReadOnlyList<Subpath> Subpaths,
        IReadOnlyList<double> Ctm,
        double LineWidth,
        IReadOnlyList<double> Dash,
        (double R, double G, double B) Color,
        string? Layer);

    /// <summary>One subpath. <see cref="Points"/> is the anchor sequence — the
    /// <c>m</c> point and the endpoint of every following <c>l</c> or <c>c</c> — so a
    /// polyline reads exactly as it was written; <see cref="Curves"/> names the cubics
    /// among them by the index of the anchor each ENDS at, with their two control
    /// points, so a Bezier's exactness is checkable rather than merely its endpoints'.</summary>
    public sealed record Subpath(
        IReadOnlyList<Vector2d> Points, bool Closed,
        IReadOnlyList<(int EndIndex, Vector2d C1, Vector2d C2)> Curves);

    /// <summary>One shown text run, position raw (as written).</summary>
    public sealed record TextRun(Vector2d Position, double FontSize, string Value, string? Layer);

    /// <summary>What the file says about the font resource /F1.</summary>
    /// <param name="Subtype">/Type1 for the built-in Helvetica, /Type0 for an embedded subset.</param>
    /// <param name="BaseFont">The base font name (subset tag included, when embedded).</param>
    /// <param name="Program">The embedded font program (FontFile2), or null.</param>
    /// <param name="Widths">Glyph index to width in 1000-unit text space (/W), empty when not embedded.</param>
    /// <param name="ToUnicode">Glyph index to the text it stands for, from the /ToUnicode CMap.</param>
    public sealed record FontInfo(
        string Subtype, string BaseFont, byte[]? Program,
        IReadOnlyDictionary<int, double> Widths,
        IReadOnlyDictionary<int, string> ToUnicode);

    public sealed record Document(
        IReadOnlyList<double> MediaBox,
        IReadOnlyList<Stroke> Strokes,
        IReadOnlyList<TextRun> Texts,
        FontInfo Font,
        IReadOnlyList<string> Layers,
        byte[] Content);

    // --------------------------------------------------------------------- file level

    public static Document Parse(byte[] pdf)
    {
        // Latin-1 is a byte-preserving view, so string indices ARE byte offsets.
        string text = Encoding.Latin1.GetString(pdf);
        if (!text.StartsWith("%PDF-", StringComparison.Ordinal))
            throw new InvalidOperationException("Not a PDF: missing %PDF- header.");

        int startxref = text.LastIndexOf("startxref", StringComparison.Ordinal);
        if (startxref < 0)
            throw new InvalidOperationException("No startxref.");
        int p = startxref + "startxref".Length;
        long xrefPos = (long)ReadNumber(text, ref p);

        // The xref table: offsets, each verified to point at its own object header.
        p = (int)xrefPos;
        Expect(text, ref p, "xref");
        int first = (int)ReadNumber(text, ref p);
        int count = (int)ReadNumber(text, ref p);
        if (first != 0)
            throw new InvalidOperationException("Expected the xref subsection to start at object 0.");
        var offsets = new long[count];
        for (int i = 0; i < count; i++)
        {
            long offset = (long)ReadNumber(text, ref p);
            _ = ReadNumber(text, ref p);   // generation
            SkipWhitespace(text, ref p);
            char kind = text[p++];
            offsets[i] = offset;
            if (kind == 'n')
            {
                string header = i.ToString(CultureInfo.InvariantCulture) + " 0 obj";
                if (offset + header.Length > text.Length
                    || !text.AsSpan((int)offset, header.Length).SequenceEqual(header))
                {
                    throw new InvalidOperationException(
                        $"xref entry {i} points at offset {offset}, which does not hold '{header}'.");
                }
            }
            else if (kind != 'f')
            {
                throw new InvalidOperationException($"xref entry {i} is neither in use nor free ('{kind}').");
            }
        }

        Expect(text, ref p, "trailer");
        var trailer = (Dictionary<string, object>)ParseValue(text, ref p);
        if ((int)Math.Round((double)trailer["Size"]) != count)
            throw new InvalidOperationException("trailer /Size disagrees with the xref count.");

        object GetObject(PdfRef reference)
        {
            int at = (int)offsets[reference.Number];
            _ = ReadNumber(text, ref at);   // object number
            _ = ReadNumber(text, ref at);   // generation
            Expect(text, ref at, "obj");
            return ParseValue(text, ref at);
        }

        // A stream object's DECODED payload: exactly /Length bytes after the stream
        // keyword's end of line, inflated when the dictionary declares /FlateDecode.
        byte[] GetStream(PdfRef reference, out Dictionary<string, object> dict)
        {
            int at = (int)offsets[reference.Number];
            _ = ReadNumber(text, ref at);
            _ = ReadNumber(text, ref at);
            Expect(text, ref at, "obj");
            dict = (Dictionary<string, object>)ParseValue(text, ref at);
            Expect(text, ref at, "stream");
            if (text[at] == '\r')
                at++;
            if (text[at] != '\n')
                throw new InvalidOperationException("The stream keyword must be followed by an end of line.");
            at++;
            int length = (int)Math.Round((double)dict["Length"]);
            var raw = new byte[length];
            Array.Copy(pdf, at, raw, 0, length);
            int after = at + length;
            SkipWhitespace(text, ref after);
            Expect(text, ref after, "endstream");

            if (!dict.TryGetValue("Filter", out object? filter))
                return raw;
            if (filter is not string name || name != "FlateDecode")
                throw new InvalidOperationException($"Unsupported stream filter '{filter}'.");
            using var input = new MemoryStream(raw);
            using var inflate = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflate.CopyTo(output);
            return output.ToArray();
        }

        var root = (Dictionary<string, object>)GetObject((PdfRef)trailer["Root"]);
        var pages = (Dictionary<string, object>)GetObject((PdfRef)root["Pages"]);
        var kids = (List<object>)pages["Kids"];
        var page = (Dictionary<string, object>)GetObject((PdfRef)kids[0]);
        var mediaBox = ((List<object>)page["MediaBox"]).Select(v => (double)v).ToList();
        var resources = (Dictionary<string, object>)page["Resources"];

        // ---- optional content: the catalog's declared order, and the page's aliases ----
        var layers = new List<string>();
        if (root.TryGetValue("OCProperties", out object? oc))
        {
            var properties = (Dictionary<string, object>)oc;
            var display = (Dictionary<string, object>)properties["D"];
            foreach (object reference in (List<object>)display["Order"])
                layers.Add(LayerName(((Dictionary<string, object>)GetObject((PdfRef)reference))["Name"]));
            if (((List<object>)properties["OCGs"]).Count != layers.Count)
                throw new InvalidOperationException("/OCGs and /D /Order disagree about the layer count.");
        }
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        if (resources.TryGetValue("Properties", out object? propertyMap))
        {
            foreach (var (alias, reference) in (Dictionary<string, object>)propertyMap)
                aliases[alias] = LayerName(((Dictionary<string, object>)GetObject((PdfRef)reference))["Name"]);
        }

        var fonts = (Dictionary<string, object>)resources["Font"];
        if (!fonts.TryGetValue("F1", out object? fontReference))
            throw new InvalidOperationException("The page has no /F1 font.");
        var font = ReadFont((Dictionary<string, object>)GetObject((PdfRef)fontReference), GetObject, GetStream);

        byte[] content = GetStream((PdfRef)page["Contents"], out _);
        var (strokes, texts) = ParseContent(Encoding.Latin1.GetString(content), aliases, font.ToUnicode);
        return new Document(mediaBox, strokes, texts, font, layers, content);
    }

    private static FontInfo ReadFont(
        Dictionary<string, object> font,
        Func<PdfRef, object> getObject,
        PdfStreamReader getStream)
    {
        string subtype = (string)font["Subtype"];
        string baseFont = (string)font["BaseFont"];
        if (subtype != "Type0")
            return new FontInfo(subtype, baseFont, null, new Dictionary<int, double>(), new Dictionary<int, string>());

        var descendant = (Dictionary<string, object>)getObject(
            (PdfRef)((List<object>)font["DescendantFonts"])[0]);
        if ((string)descendant["Subtype"] != "CIDFontType2")
            throw new InvalidOperationException($"Unexpected descendant font subtype '{descendant["Subtype"]}'.");
        if ((string)descendant["CIDToGIDMap"] != "Identity")
            throw new InvalidOperationException("Only /CIDToGIDMap /Identity is understood.");

        // /W is [ cid [w] cid [w] ... ] in this writer's spelling; the array form
        // "first last w" is not emitted and is refused rather than guessed at.
        var widths = new Dictionary<int, double>();
        var w = (List<object>)descendant["W"];
        for (int i = 0; i < w.Count; i += 2)
        {
            int cid = (int)Math.Round((double)w[i]);
            widths[cid] = (double)((List<object>)w[i + 1])[0];
        }

        var descriptor = (Dictionary<string, object>)getObject((PdfRef)descendant["FontDescriptor"]);
        byte[] program = getStream((PdfRef)descriptor["FontFile2"], out var programDict);
        if ((int)Math.Round((double)programDict["Length1"]) != program.Length)
            throw new InvalidOperationException("/Length1 disagrees with the decoded font program's length.");

        var toUnicode = ParseToUnicode(
            Encoding.Latin1.GetString(getStream((PdfRef)font["ToUnicode"], out _)));
        return new FontInfo(subtype, baseFont, program, widths, toUnicode);
    }

    /// <summary>An optional-content group's /Name is a PDF literal STRING (UTF-8 bytes),
    /// not a name token — so it is decoded here rather than cast.</summary>
    private static string LayerName(object value) => Encoding.UTF8.GetString((byte[])value);

    private delegate byte[] PdfStreamReader(PdfRef reference, out Dictionary<string, object> dict);

    /// <summary>The <c>bfchar</c> entries of a ToUnicode CMap — the reader's own scan,
    /// so a test comparing decoded text against the source string is checking the file
    /// rather than the writer's bookkeeping.</summary>
    private static Dictionary<int, string> ParseToUnicode(string cmap)
    {
        var map = new Dictionary<int, string>();
        int at = 0;
        while (true)
        {
            int begin = cmap.IndexOf("beginbfchar", at, StringComparison.Ordinal);
            if (begin < 0)
                return map;
            int end = cmap.IndexOf("endbfchar", begin, StringComparison.Ordinal);
            if (end < 0)
                throw new InvalidOperationException("A beginbfchar block is not closed.");
            int p = begin + "beginbfchar".Length;
            while (true)
            {
                SkipWhitespace(cmap, ref p);
                if (p >= end)
                    break;
                var code = (byte[])ReadHexString(cmap, ref p);
                SkipWhitespace(cmap, ref p);
                var value = (byte[])ReadHexString(cmap, ref p);
                map[(code[0] << 8) | code[1]] = Encoding.BigEndianUnicode.GetString(value);
            }
            at = end + "endbfchar".Length;
        }
    }

    // ---------------------------------------------------------------- content stream

    private static (List<Stroke> Strokes, List<TextRun> Texts) ParseContent(
        string content, IReadOnlyDictionary<string, string> layerAliases,
        IReadOnlyDictionary<int, string> toUnicode)
    {
        var strokes = new List<Stroke>();
        var texts = new List<TextRun>();
        var operands = new List<object>();

        // Graphics state: CTM [a b c d e f], width, dash, stroke colour.
        var ctm = new double[] { 1, 0, 0, 1, 0, 0 };
        double width = 1;
        var dash = new List<double>();
        var color = (R: 0.0, G: 0.0, B: 0.0);
        var stack = new Stack<(double[] Ctm, double Width, List<double> Dash, (double, double, double) Color)>();

        // Marked content is its own stack — a BDC may not straddle a q/Q, and this
        // reader checks that by unwinding them independently.
        var marked = new Stack<string>();

        var subpaths = new List<(List<Vector2d> Points, bool Closed,
            List<(int EndIndex, Vector2d C1, Vector2d C2)> Curves)>();
        double textX = 0, textY = 0, fontSize = 0;

        int p = 0;
        while (true)
        {
            SkipWhitespace(content, ref p);
            if (p >= content.Length)
                break;
            char c = content[p];
            if (c == '/' || c == '[' || c == '(' || c == '<' || char.IsAsciiDigit(c) || c is '-' or '+' or '.')
            {
                operands.Add(ParseValue(content, ref p));
                continue;
            }

            string op = ReadKeyword(content, ref p);
            switch (op)
            {
                case "q":
                    stack.Push(((double[])ctm.Clone(), width, [.. dash], color));
                    break;
                case "Q":
                    (ctm, width, dash, color) = stack.Pop();
                    break;
                case "cm":
                    ctm = Concat(Numbers(operands, 6), ctm);
                    break;
                case "w":
                    width = (double)operands[^1];
                    break;
                case "d":
                    dash = ((List<object>)operands[^2]).Select(v => (double)v).ToList();
                    break;
                case "RG":
                {
                    var rgb = Numbers(operands, 3);
                    color = (rgb[0], rgb[1], rgb[2]);
                    break;
                }
                case "BDC":
                {
                    // "/OC /OCn BDC": the tag, then the page-resource alias of the group.
                    if ((string)operands[^2] != "OC")
                        throw new InvalidOperationException($"Unexpected marked-content tag '{operands[^2]}'.");
                    string alias = (string)operands[^1];
                    if (!layerAliases.TryGetValue(alias, out string? layer))
                        throw new InvalidOperationException($"/{alias} is not in the page's /Properties.");
                    marked.Push(layer);
                    break;
                }
                case "EMC":
                    marked.Pop();
                    break;
                case "m":
                {
                    var xy = Numbers(operands, 2);
                    subpaths.Add(([new Vector2d(xy[0], xy[1])], false, []));
                    break;
                }
                case "l":
                {
                    var xy = Numbers(operands, 2);
                    subpaths[^1].Points.Add(new Vector2d(xy[0], xy[1]));
                    break;
                }
                case "c":
                {
                    var v = Numbers(operands, 6);
                    var points = subpaths[^1].Points;
                    subpaths[^1].Curves.Add(
                        (points.Count, new Vector2d(v[0], v[1]), new Vector2d(v[2], v[3])));
                    points.Add(new Vector2d(v[4], v[5]));
                    break;
                }
                case "h":
                    subpaths[^1] = (subpaths[^1].Points, true, subpaths[^1].Curves);
                    break;
                case "S":
                    strokes.Add(new Stroke(
                        subpaths.Select(s => new Subpath(s.Points, s.Closed, s.Curves)).ToList(),
                        [.. ctm], width, [.. dash], color,
                        marked.Count == 0 ? null : marked.Peek()));
                    subpaths = [];
                    break;
                case "BT":
                    textX = 0;
                    textY = 0;
                    break;
                case "Tf":
                    fontSize = (double)operands[^1];
                    break;
                case "Td":
                {
                    var xy = Numbers(operands, 2);
                    textX += xy[0];
                    textY += xy[1];
                    break;
                }
                case "Tj":
                    texts.Add(new TextRun(
                        new Vector2d(textX, textY), fontSize,
                        Decode((byte[])operands[^1], toUnicode),
                        marked.Count == 0 ? null : marked.Peek()));
                    break;
                case "ET":
                case "rg":
                case "J":
                case "j":
                    break;   // recognized, nothing this reader needs from them
                default:
                    throw new InvalidOperationException($"Unrecognized content operator '{op}'.");
            }
            operands.Clear();
        }
        if (marked.Count != 0)
            throw new InvalidOperationException($"{marked.Count} marked-content block(s) left open.");
        return (strokes, texts);
    }

    /// <summary>A shown string as text: 2-byte glyph indices through the file's own
    /// ToUnicode CMap when the font is embedded, WinAnsi bytes otherwise.</summary>
    private static string Decode(byte[] bytes, IReadOnlyDictionary<int, string> toUnicode)
    {
        if (toUnicode.Count == 0)
            return DecodeWinAnsi(bytes);
        if (bytes.Length % 2 != 0)
            throw new InvalidOperationException("An Identity-H string must be an even number of bytes.");
        var sb = new StringBuilder(bytes.Length / 2);
        for (int i = 0; i < bytes.Length; i += 2)
        {
            int cid = (bytes[i] << 8) | bytes[i + 1];
            if (!toUnicode.TryGetValue(cid, out string? value))
                throw new InvalidOperationException($"Glyph {cid} has no /ToUnicode entry.");
            sb.Append(value);
        }
        return sb.ToString();
    }

    private static double[] Numbers(List<object> operands, int count)
    {
        var values = new double[count];
        for (int i = 0; i < count; i++)
            values[i] = (double)operands[operands.Count - count + i];
        return values;
    }

    /// <summary>CTM' = m x CTM (PDF's cm concatenation; row-vector convention).</summary>
    private static double[] Concat(double[] m, double[] c) =>
    [
        m[0] * c[0] + m[1] * c[2],
        m[0] * c[1] + m[1] * c[3],
        m[2] * c[0] + m[3] * c[2],
        m[2] * c[1] + m[3] * c[3],
        m[4] * c[0] + m[5] * c[2] + c[4],
        m[4] * c[1] + m[5] * c[3] + c[5],
    ];

    // ------------------------------------------------------------------- tokenizing

    private sealed record PdfRef(int Number);

    private static object ParseValue(string text, ref int p)
    {
        SkipWhitespace(text, ref p);
        if (Peek(text, p, "<<"))
        {
            p += 2;
            var dict = new Dictionary<string, object>();
            while (true)
            {
                SkipWhitespace(text, ref p);
                if (Peek(text, p, ">>"))
                {
                    p += 2;
                    return dict;
                }
                if (text[p] != '/')
                    throw new InvalidOperationException($"Expected a name key at {p}.");
                string key = ReadName(text, ref p);
                dict[key] = ParseValue(text, ref p);
            }
        }
        if (text[p] == '<')
            return ReadHexString(text, ref p);
        if (text[p] == '[')
        {
            p++;
            var array = new List<object>();
            while (true)
            {
                SkipWhitespace(text, ref p);
                if (text[p] == ']')
                {
                    p++;
                    return array;
                }
                array.Add(ParseValue(text, ref p));
            }
        }
        if (text[p] == '/')
            return ReadName(text, ref p);
        if (text[p] == '(')
            return ReadLiteralString(text, ref p);
        if (char.IsAsciiDigit(text[p]) || text[p] is '-' or '+' or '.')
        {
            int save = p;
            double number = ReadNumber(text, ref p);
            // "n g R" is an indirect reference; anything else backtracks to the number.
            if (number >= 0 && number == Math.Floor(number))
            {
                int q = p;
                SkipWhitespace(text, ref q);
                int genStart = q;
                while (q < text.Length && char.IsAsciiDigit(text[q]))
                    q++;
                if (q > genStart)
                {
                    int r = q;
                    SkipWhitespace(text, ref r);
                    if (r < text.Length && text[r] == 'R'
                        && (r + 1 >= text.Length || IsDelimiter(text[r + 1])))
                    {
                        p = r + 1;
                        return new PdfRef((int)number);
                    }
                }
            }
            p = save;
            return ReadNumber(text, ref p);
        }
        string keyword = ReadKeyword(text, ref p);
        return keyword switch
        {
            "true" => true,
            "false" => false,
            "null" => null!,
            _ => throw new InvalidOperationException($"Unexpected token '{keyword}'."),
        };
    }

    private static string ReadName(string text, ref int p)
    {
        p++;   // '/'
        int start = p;
        while (p < text.Length && !IsDelimiter(text[p]))
            p++;
        return text[start..p];
    }

    private static object ReadHexString(string text, ref int p)
    {
        p++;   // '<'
        var bytes = new List<byte>();
        int digits = 0, value = 0;
        while (true)
        {
            char c = text[p++];
            if (c == '>')
            {
                if (digits == 1)
                    bytes.Add((byte)(value << 4));   // the spec pads an odd trailing digit
                return bytes.ToArray();
            }
            if (char.IsWhiteSpace(c))
                continue;
            int digit = Convert.ToInt32(c.ToString(), 16);
            value = digits == 0 ? digit : (value << 4) | digit;
            if (++digits == 2)
            {
                bytes.Add((byte)value);
                digits = 0;
                value = 0;
            }
        }
    }

    private static byte[] ReadLiteralString(string text, ref int p)
    {
        p++;   // '('
        var bytes = new List<byte>();
        int depth = 1;
        while (true)
        {
            char c = text[p++];
            if (c == '\\')
            {
                char e = text[p++];
                if (e is >= '0' and <= '7')
                {
                    int value = e - '0';
                    for (int i = 0; i < 2 && text[p] is >= '0' and <= '7'; i++)
                        value = value * 8 + (text[p++] - '0');
                    bytes.Add((byte)value);
                }
                else
                {
                    bytes.Add(e switch
                    {
                        'n' => (byte)'\n', 'r' => (byte)'\r', 't' => (byte)'\t',
                        'b' => (byte)'\b', 'f' => (byte)'\f',
                        _ => (byte)e,   // \( \) \\ and any other escaped literal
                    });
                }
            }
            else if (c == '(')
            {
                depth++;
                bytes.Add((byte)c);
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                    return [.. bytes];
                bytes.Add((byte)c);
            }
            else
            {
                bytes.Add((byte)c);
            }
        }
    }

    private static double ReadNumber(string text, ref int p)
    {
        SkipWhitespace(text, ref p);
        int start = p;
        while (p < text.Length && (char.IsAsciiDigit(text[p]) || text[p] is '-' or '+' or '.'))
            p++;
        if (p == start)
            throw new InvalidOperationException($"Expected a number at {start}.");
        return double.Parse(text[start..p], CultureInfo.InvariantCulture);
    }

    private static string ReadKeyword(string text, ref int p)
    {
        int start = p;
        while (p < text.Length && !IsDelimiter(text[p]))
            p++;
        if (p == start)
            throw new InvalidOperationException($"Expected a keyword at {start}, found '{text[p]}'.");
        return text[start..p];
    }

    private static void Expect(string text, ref int p, string keyword)
    {
        SkipWhitespace(text, ref p);
        if (!Peek(text, p, keyword))
            throw new InvalidOperationException($"Expected '{keyword}' at {p}.");
        p += keyword.Length;
    }

    private static bool Peek(string text, int p, string expected) =>
        p + expected.Length <= text.Length
        && text.AsSpan(p, expected.Length).SequenceEqual(expected);

    private static void SkipWhitespace(string text, ref int p)
    {
        while (p < text.Length)
        {
            char c = text[p];
            if (c is ' ' or '\t' or '\r' or '\n' or '\f' or '\0')
            {
                p++;
            }
            else if (c == '%')
            {
                while (p < text.Length && text[p] is not '\n' and not '\r')
                    p++;
            }
            else
            {
                break;
            }
        }
    }

    private static bool IsDelimiter(char c) =>
        c is ' ' or '\t' or '\r' or '\n' or '\f' or '\0'
            or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

    /// <summary>The reader's OWN WinAnsi (CP1252) decode — independent of the writer's
    /// encode table, which is the point of a twin decoder. Code points, not literals,
    /// so the source file stays pure ASCII.</summary>
    private static string DecodeWinAnsi(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            sb.Append((char)(b switch
            {
                0x80 => 0x20AC, 0x82 => 0x201A, 0x83 => 0x0192, 0x84 => 0x201E,
                0x85 => 0x2026, 0x86 => 0x2020, 0x87 => 0x2021, 0x88 => 0x02C6,
                0x89 => 0x2030, 0x8A => 0x0160, 0x8B => 0x2039, 0x8C => 0x0152,
                0x8E => 0x017D, 0x91 => 0x2018, 0x92 => 0x2019, 0x93 => 0x201C,
                0x94 => 0x201D, 0x95 => 0x2022, 0x96 => 0x2013, 0x97 => 0x2014,
                0x98 => 0x02DC, 0x99 => 0x2122, 0x9A => 0x0161, 0x9B => 0x203A,
                0x9C => 0x0153, 0x9E => 0x017E, 0x9F => 0x0178,
                _ => b,
            }));
        }
        return sb.ToString();
    }
}
