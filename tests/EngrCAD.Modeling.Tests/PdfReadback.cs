using System.Globalization;
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
/// stroked polylines (with the graphics state that stroked them, CTM included) and text
/// runs (decoded through the reader's OWN WinAnsi table).
/// </summary>
internal static class PdfReadback
{
    /// <summary>One stroked path: its subpaths, and the graphics state that stroked it.
    /// The CTM is [a b c d e f]; coordinates are RAW (as written), so a test compares
    /// them against the writer's input and asserts the CTM separately.</summary>
    public sealed record Stroke(
        IReadOnlyList<(IReadOnlyList<Vector2d> Points, bool Closed)> Subpaths,
        IReadOnlyList<double> Ctm,
        double LineWidth,
        IReadOnlyList<double> Dash,
        (double R, double G, double B) Color);

    /// <summary>One shown text run, position raw (as written).</summary>
    public sealed record TextRun(Vector2d Position, double FontSize, string Value);

    public sealed record Document(
        IReadOnlyList<double> MediaBox,
        IReadOnlyList<Stroke> Strokes,
        IReadOnlyList<TextRun> Texts);

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

        var root = (Dictionary<string, object>)GetObject((PdfRef)trailer["Root"]);
        var pages = (Dictionary<string, object>)GetObject((PdfRef)root["Pages"]);
        var kids = (List<object>)pages["Kids"];
        var page = (Dictionary<string, object>)GetObject((PdfRef)kids[0]);
        var mediaBox = ((List<object>)page["MediaBox"]).Select(v => (double)v).ToList();

        // The content stream: dict, then exactly /Length bytes after the stream keyword's EOL.
        int contentAt = (int)offsets[((PdfRef)page["Contents"]).Number];
        _ = ReadNumber(text, ref contentAt);
        _ = ReadNumber(text, ref contentAt);
        Expect(text, ref contentAt, "obj");
        var streamDict = (Dictionary<string, object>)ParseValue(text, ref contentAt);
        Expect(text, ref contentAt, "stream");
        if (text[contentAt] == '\r')
            contentAt++;
        if (text[contentAt] != '\n')
            throw new InvalidOperationException("The stream keyword must be followed by an end of line.");
        contentAt++;
        int length = (int)Math.Round((double)streamDict["Length"]);
        string content = text.Substring(contentAt, length);
        int after = contentAt + length;
        SkipWhitespace(text, ref after);
        Expect(text, ref after, "endstream");

        var (strokes, texts) = ParseContent(content);
        return new Document(mediaBox, strokes, texts);
    }

    // ---------------------------------------------------------------- content stream

    private static (List<Stroke> Strokes, List<TextRun> Texts) ParseContent(string content)
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

        var subpaths = new List<(List<Vector2d> Points, bool Closed)>();
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
                case "m":
                {
                    var xy = Numbers(operands, 2);
                    subpaths.Add(([new Vector2d(xy[0], xy[1])], false));
                    break;
                }
                case "l":
                {
                    var xy = Numbers(operands, 2);
                    subpaths[^1].Points.Add(new Vector2d(xy[0], xy[1]));
                    break;
                }
                case "h":
                    subpaths[^1] = (subpaths[^1].Points, true);
                    break;
                case "S":
                    strokes.Add(new Stroke(
                        subpaths.Select(s => ((IReadOnlyList<Vector2d>)s.Points, s.Closed)).ToList(),
                        [.. ctm], width, [.. dash], color));
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
                        new Vector2d(textX, textY), fontSize, DecodeWinAnsi((byte[])operands[^1])));
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
        return (strokes, texts);
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
