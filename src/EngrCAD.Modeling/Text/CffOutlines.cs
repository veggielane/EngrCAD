using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// PostScript outlines from an OpenType <c>CFF </c> table — the other half of the
/// OpenType format family: <c>OTTO</c>-flavoured <c>.otf</c> fonts store glyphs as
/// Type 2 charstrings (cubic Béziers) instead of a <c>glyf</c> table (quadratics).
/// Hand-rolled and dependency free like the rest of <see cref="TrueTypeFont"/>; only
/// what outlines need is parsed — INDEX structures, the Top and Private DICTs, local
/// and global subroutines, and (for CID-keyed fonts) FDArray/FDSelect. The charset
/// table is deliberately not read: glyphs are addressed by index straight from
/// <c>cmap</c>, and charsets only name them.
/// <para>Contours come back as <see cref="GlyphContour"/>s with
/// <see cref="GlyphContour.IsCubic"/> set: off-curve points are <em>cubic</em> control
/// pairs, which <c>GlyphOutlines</c> maps onto <c>SketchBuilder.BezierTo</c> exactly —
/// no flattening, the same property TrueType quadratics have.</para>
/// <para><b>The hint subtlety.</b> Type 2 charstrings interleave hinting with the
/// outline, and <c>hintmask</c>/<c>cntrmask</c> are followed by one data byte per
/// eight declared stems — including stems declared <em>implicitly</em> by arguments
/// still on the stack when the mask operator appears. Miscounting stems misreads the
/// mask bytes as operators and garbles everything after; this is CFF's cousin of
/// TrueType's implied-midpoint trap, and the synthetic-font tests pin it with exact
/// decoded coordinates.</para>
/// <para><b>Rejected loudly</b> (message names the construct): Type 1 charstrings,
/// the legacy <c>seac</c> accent composition (endchar with 4 arguments), and the
/// Type 2 arithmetic/storage operators no real font uses.</para>
/// </summary>
internal sealed class CffOutlines
{
    /// <summary>Type 2 argument stack limit (Appendix B); enforced so a corrupt
    /// charstring fails with a named error instead of unbounded growth.</summary>
    private const int StackLimit = 48;

    /// <summary>Subroutine nesting limit (Appendix B).</summary>
    private const int MaxSubrDepth = 10;

    private readonly byte[] _data;
    private readonly (int Start, int End)[] _charStrings;   // absolute spans, per glyph
    private readonly (int Start, int End)[] _globalSubrs;
    private readonly (int Start, int End)[][] _localSubrsPerFd;
    private readonly byte[] _fdForGlyph;                    // all zero for non-CID fonts

    private CffOutlines(
        byte[] data,
        (int Start, int End)[] charStrings,
        (int Start, int End)[] globalSubrs,
        (int Start, int End)[][] localSubrsPerFd,
        byte[] fdForGlyph)
    {
        _data = data;
        _charStrings = charStrings;
        _globalSubrs = globalSubrs;
        _localSubrsPerFd = localSubrsPerFd;
        _fdForGlyph = fdForGlyph;
    }

    /// <summary>Number of charstrings in the font (must match <c>maxp.numGlyphs</c>;
    /// the caller validates).</summary>
    public int GlyphCount => _charStrings.Length;

    // ---- table parsing -------------------------------------------------------

    /// <summary>Parses the <c>CFF </c> table at <paramref name="offset"/> in
    /// <paramref name="data"/> (the whole font file, which the returned instance keeps
    /// a reference to).</summary>
    public static CffOutlines Read(byte[] data, int offset, int length)
    {
        var span = data.AsSpan();
        var reader = new FontReader(span, offset);

        // Header: major.minor version, header size, absolute-offset size.
        int major = reader.ReadUInt8();
        reader.Skip(1);                                     // minor
        int headerSize = reader.ReadUInt8();
        reader.Skip(1);                                     // offSize (unused: every offset we read carries its own size)
        if (major != 1)
            throw new FontFormatException($"CFF table version is {major}; only CFF 1 (Type 2 charstrings) is supported.");

        // The four fixed-order INDEXes.
        int at = offset + headerSize;
        SkipIndex(span, ref at);                            // Name INDEX
        var topDicts = ReadIndex(span, ref at);             // Top DICT INDEX
        SkipIndex(span, ref at);                            // String INDEX
        var globalSubrs = ReadIndex(span, ref at);          // Global Subr INDEX

        if (topDicts.Length == 0)
            throw new FontFormatException("CFF Top DICT INDEX is empty; the table describes no font.");
        var top = ParseDict(span, topDicts[0].Start, topDicts[0].End);

        if (top.TryGetValue(Op(12, 6), out var type) && (int)type[0] != 2)
            throw new FontFormatException(
                $"CFF CharstringType is {(int)type[0]}; only Type 2 charstrings are supported.");

        if (!top.TryGetValue(17, out var charStringsOp))
            throw new FontFormatException("CFF Top DICT has no CharStrings offset (operator 17).");
        int charStringsAt = offset + (int)charStringsOp[0];
        var charStrings = ReadIndex(span, ref charStringsAt);
        if (charStrings.Length == 0)
            throw new FontFormatException("CFF CharStrings INDEX is empty; the font contains no glyph outlines.");

        // Private DICT(s) -> local subrs. CID-keyed fonts (ROS present) hold them per
        // font DICT in FDArray, with FDSelect mapping each glyph to its dict.
        (int Start, int End)[][] localSubrsPerFd;
        byte[] fdForGlyph;
        if (top.ContainsKey(Op(12, 30)))                    // ROS: CID-keyed
        {
            if (!top.TryGetValue(Op(12, 36), out var fdArrayOp) || !top.TryGetValue(Op(12, 37), out var fdSelectOp))
                throw new FontFormatException("CID-keyed CFF is missing FDArray or FDSelect.");
            int fdArrayAt = offset + (int)fdArrayOp[0];
            var fontDicts = ReadIndex(span, ref fdArrayAt);
            localSubrsPerFd = new (int, int)[fontDicts.Length][];
            for (int i = 0; i < fontDicts.Length; i++)
            {
                var dict = ParseDict(span, fontDicts[i].Start, fontDicts[i].End);
                localSubrsPerFd[i] = ReadPrivateSubrs(span, offset, dict);
            }
            fdForGlyph = ReadFdSelect(span, offset + (int)fdSelectOp[0], charStrings.Length, fontDicts.Length);
        }
        else
        {
            localSubrsPerFd = [ReadPrivateSubrs(span, offset, top)];
            fdForGlyph = new byte[charStrings.Length];
        }

        _ = length;                                         // spans were bounds-checked by FontReader as they were read
        return new CffOutlines(data, charStrings, globalSubrs, localSubrsPerFd, fdForGlyph);
    }

    private static (int Start, int End)[] ReadPrivateSubrs(
        ReadOnlySpan<byte> span, int cffStart, Dictionary<int, double[]> dict)
    {
        if (!dict.TryGetValue(18, out var priv) || priv.Length < 2)
            return [];
        int size = (int)priv[0];
        int at = cffStart + (int)priv[1];
        var privateDict = ParseDict(span, at, at + size);
        if (!privateDict.TryGetValue(19, out var subrs))
            return [];
        int subrsAt = at + (int)subrs[0];                   // Subrs offset is relative to the Private DICT
        return ReadIndex(span, ref subrsAt);
    }

    private static byte[] ReadFdSelect(ReadOnlySpan<byte> span, int at, int glyphCount, int fdCount)
    {
        var reader = new FontReader(span, at);
        var fd = new byte[glyphCount];
        int format = reader.ReadUInt8();
        switch (format)
        {
            case 0:
                for (int i = 0; i < glyphCount; i++)
                    fd[i] = reader.ReadUInt8();
                break;
            case 3:
                int ranges = reader.ReadUInt16();
                int first = reader.ReadUInt16();
                for (int r = 0; r < ranges; r++)
                {
                    int fdIndex = reader.ReadUInt8();
                    int next = reader.ReadUInt16();         // first glyph of the next range (sentinel on the last)
                    if (first < 0 || next > glyphCount || next < first)
                        throw new FontFormatException($"CFF FDSelect range {r} covers glyphs {first}..{next - 1}, outside 0..{glyphCount - 1}.");
                    for (int g = first; g < next; g++)
                        fd[g] = (byte)fdIndex;
                    first = next;
                }
                break;
            default:
                throw new FontFormatException($"CFF FDSelect format {format} is not supported (formats 0 and 3 are).");
        }
        foreach (byte index in fd)
        {
            if (index >= fdCount)
                throw new FontFormatException($"CFF FDSelect maps a glyph to font DICT {index}, but FDArray has {fdCount}.");
        }
        return fd;
    }

    // ---- INDEX ---------------------------------------------------------------

    /// <summary>Reads an INDEX at <paramref name="at"/> (advanced past it): item spans
    /// as absolute offsets. Offsets inside an INDEX are 1-based from its data start.</summary>
    private static (int Start, int End)[] ReadIndex(ReadOnlySpan<byte> span, ref int at)
    {
        var reader = new FontReader(span, at);
        int count = reader.ReadUInt16();
        if (count == 0)
        {
            at += 2;
            return [];
        }
        int offSize = reader.ReadUInt8();
        if (offSize is < 1 or > 4)
            throw new FontFormatException($"CFF INDEX offSize is {offSize}; the format allows 1..4.");

        var offsets = new int[count + 1];
        for (int i = 0; i <= count; i++)
        {
            long value = 0;
            for (int b = 0; b < offSize; b++)
                value = (value << 8) | reader.ReadUInt8();
            if (value < 1 || value > int.MaxValue)
                throw new FontFormatException($"CFF INDEX offset {value} is out of range (offsets are 1-based).");
            offsets[i] = (int)value;
        }

        int dataStart = reader.Position;
        var items = new (int, int)[count];
        for (int i = 0; i < count; i++)
        {
            int start = dataStart + offsets[i] - 1;
            int end = dataStart + offsets[i + 1] - 1;
            if (end < start || end > span.Length)
                throw new FontFormatException($"CFF INDEX item {i} spans {start}..{end}, outside the {span.Length}-byte file.");
            items[i] = (start, end);
        }
        at = dataStart + offsets[count] - 1;
        return items;
    }

    private static void SkipIndex(ReadOnlySpan<byte> span, ref int at) => ReadIndex(span, ref at);

    // ---- DICT ----------------------------------------------------------------

    /// <summary>Two-byte (escaped) DICT operator key.</summary>
    private static int Op(int escape, int op) => escape * 100 + op;

    /// <summary>Parses a DICT: operator -> operand list. DICT number encoding differs
    /// from charstrings (operators 29 and 30 exist here only).</summary>
    private static Dictionary<int, double[]> ParseDict(ReadOnlySpan<byte> span, int start, int end)
    {
        var dict = new Dictionary<int, double[]>();
        var operands = new List<double>();
        var reader = new FontReader(span, start);
        while (reader.Position < end)
        {
            int b0 = reader.ReadUInt8();
            switch (b0)
            {
                case <= 21:                                  // operator
                    int key = b0 == 12 ? Op(12, reader.ReadUInt8()) : b0;
                    dict[key] = [.. operands];
                    operands.Clear();
                    break;
                case 28:
                    operands.Add((short)reader.ReadUInt16());
                    break;
                case 29:
                    operands.Add((int)reader.ReadUInt32());
                    break;
                case 30:
                    operands.Add(ReadRealNumber(ref reader));
                    break;
                case >= 32 and <= 246:
                    operands.Add(b0 - 139);
                    break;
                case >= 247 and <= 250:
                    operands.Add((b0 - 247) * 256 + reader.ReadUInt8() + 108);
                    break;
                case >= 251 and <= 254:
                    operands.Add(-(b0 - 251) * 256 - reader.ReadUInt8() - 108);
                    break;
                default:
                    throw new FontFormatException($"CFF DICT byte 0x{b0:X2} at offset {reader.Position - 1} is not a valid operand or operator.");
            }
        }
        return dict;
    }

    /// <summary>DICT real numbers are packed BCD nibbles: digits, '.', exponents, a
    /// minus, and 0xF terminating.</summary>
    private static double ReadRealNumber(ref FontReader reader)
    {
        var text = new System.Text.StringBuilder();
        while (true)
        {
            int b = reader.ReadUInt8();
            for (int half = 0; half < 2; half++)
            {
                int nibble = half == 0 ? b >> 4 : b & 0xF;
                switch (nibble)
                {
                    case <= 9: text.Append((char)('0' + nibble)); break;
                    case 0xA: text.Append('.'); break;
                    case 0xB: text.Append('E'); break;
                    case 0xC: text.Append("E-"); break;
                    case 0xE: text.Append('-'); break;
                    case 0xF:
                        return double.TryParse(text.ToString(), System.Globalization.CultureInfo.InvariantCulture, out double value)
                            ? value
                            : throw new FontFormatException($"CFF DICT real number '{text}' does not parse.");
                    default:
                        throw new FontFormatException($"CFF DICT real number contains reserved nibble 0x{nibble:X}.");
                }
            }
        }
    }

    // ---- Type 2 charstring interpreter --------------------------------------

    /// <summary>Decodes one glyph's outline. Contours are cubic
    /// (<see cref="GlyphContour.IsCubic"/>): on-curve anchors with off-curve control
    /// points in pairs, closing from the last point back to the first.</summary>
    public List<GlyphContour> ReadGlyph(int glyphIndex)
    {
        if ((uint)glyphIndex >= (uint)_charStrings.Length)
            throw new ArgumentOutOfRangeException(nameof(glyphIndex));
        var state = new Interpreter(this, _localSubrsPerFd[_fdForGlyph[glyphIndex]], glyphIndex);
        var (start, end) = _charStrings[glyphIndex];
        state.Run(start, end, depth: 0);
        state.FlushContour();
        return state.Contours;
    }

    /// <summary>Bias added to subroutine call operands (Type 2 §4.7): small fonts get
    /// small operands for the common subrs.</summary>
    private static int Bias(int subrCount) => subrCount < 1240 ? 107 : subrCount < 33900 ? 1131 : 32768;

    private sealed class Interpreter(CffOutlines font, (int Start, int End)[] localSubrs, int glyphIndex)
    {
        private readonly List<double> _stack = [];
        private double _x, _y;
        private int _stems;
        private bool _widthParsed;
        private bool _ended;
        private List<GlyphPoint>? _contour;

        public List<GlyphContour> Contours { get; } = [];

        public void Run(int start, int end, int depth)
        {
            if (depth > MaxSubrDepth)
                throw new FontFormatException($"Glyph {glyphIndex}: charstring subroutines nest deeper than {MaxSubrDepth} (cyclic call?).");

            var reader = new FontReader(font._data.AsSpan(), start);
            while (reader.Position < end && !_ended)
            {
                int b0 = reader.ReadUInt8();
                if (b0 >= 32 || b0 == 28)
                {
                    Push(ReadNumber(ref reader, b0));
                    continue;
                }
                switch (b0)
                {
                    case 1 or 3 or 18 or 23:                 // hstem / vstem / hstemhm / vstemhm
                        CountStems();
                        break;
                    case 19 or 20:                           // hintmask / cntrmask
                        CountStems();                        // arguments still on the stack are an implicit vstem list
                        reader.Skip((_stems + 7) / 8);       // one data byte per eight stems — THE counting subtlety
                        break;
                    case 21:                                 // rmoveto
                        StripWidth(expected: 2);
                        MoveTo(_x + Arg(0), _y + Arg(1));
                        break;
                    case 22:                                 // hmoveto
                        StripWidth(expected: 1);
                        MoveTo(_x + Arg(0), _y);
                        break;
                    case 4:                                  // vmoveto
                        StripWidth(expected: 1);
                        MoveTo(_x, _y + Arg(0));
                        break;
                    case 5:                                  // rlineto
                        for (int i = 0; i + 1 < _stack.Count; i += 2)
                            LineTo(_x + Arg(i), _y + Arg(i + 1));
                        _stack.Clear();
                        break;
                    case 6 or 7:                             // hlineto / vlineto (alternating)
                        AlternatingLines(startHorizontal: b0 == 6);
                        break;
                    case 8:                                  // rrcurveto
                        for (int i = 0; i + 5 < _stack.Count; i += 6)
                            RelativeCurve(i);
                        _stack.Clear();
                        break;
                    case 24:                                 // rcurveline: curves then one line
                    {
                        int i = 0;
                        for (; i + 7 < _stack.Count; i += 6)
                            RelativeCurve(i);
                        LineTo(_x + Arg(i), _y + Arg(i + 1));
                        _stack.Clear();
                        break;
                    }
                    case 25:                                 // rlinecurve: lines then one curve
                    {
                        int i = 0;
                        for (; _stack.Count - i > 6; i += 2)
                            LineTo(_x + Arg(i), _y + Arg(i + 1));
                        RelativeCurve(i);
                        _stack.Clear();
                        break;
                    }
                    case 26 or 27:                           // vvcurveto / hhcurveto
                        AxisAlignedCurves(vertical: b0 == 26);
                        break;
                    case 30 or 31:                           // vhcurveto / hvcurveto (alternating)
                        AlternatingCurves(startHorizontal: b0 == 31);
                        break;
                    case 10:                                 // callsubr
                        Call(localSubrs, "local", depth);
                        break;
                    case 29:                                 // callgsubr
                        Call(font._globalSubrs, "global", depth);
                        break;
                    case 11:                                 // return
                        return;
                    case 14:                                 // endchar
                        EndChar();
                        break;
                    case 12:
                        Escaped(reader.ReadUInt8());
                        break;
                    default:
                        throw new FontFormatException($"Glyph {glyphIndex}: charstring operator {b0} is not valid Type 2.");
                }
            }
        }

        public void FlushContour()
        {
            if (_contour is { Count: >= 2 })
                Contours.Add(new GlyphContour([.. _contour], isCubic: true));
            _contour = null;
        }

        // ---- numbers and the stack ------------------------------------------

        private static double ReadNumber(ref FontReader reader, int b0) => b0 switch
        {
            28 => (short)reader.ReadUInt16(),
            255 => (int)reader.ReadUInt32() / 65536.0,       // 16.16 fixed point (charstrings only)
            >= 32 and <= 246 => b0 - 139,
            >= 247 and <= 250 => (b0 - 247) * 256 + reader.ReadUInt8() + 108,
            >= 251 and <= 254 => -(b0 - 251) * 256 - reader.ReadUInt8() - 108,
            _ => throw new FontFormatException($"Charstring byte {b0} is not a number."),
        };

        private void Push(double value)
        {
            if (_stack.Count >= StackLimit)
                throw new FontFormatException($"Glyph {glyphIndex}: charstring exceeds the Type 2 argument stack limit of {StackLimit}.");
            _stack.Add(value);
        }

        private double Arg(int index) =>
            index < _stack.Count
                ? _stack[index]
                : throw new FontFormatException($"Glyph {glyphIndex}: charstring operator is missing arguments ({_stack.Count} on the stack).");

        /// <summary>The first stack-clearing operator of a charstring may carry one
        /// extra leading argument: the glyph's width (delta from nominalWidthX). The
        /// advance already comes from <c>hmtx</c> — OpenType requires the two to agree
        /// — so the width is stripped, not stored.</summary>
        private void StripWidth(int expected)
        {
            if (_widthParsed)
                return;
            _widthParsed = true;
            if (_stack.Count > expected)
                _stack.RemoveAt(0);
        }

        private void CountStems()
        {
            if (!_widthParsed)
            {
                _widthParsed = true;
                if (_stack.Count % 2 != 0)
                    _stack.RemoveAt(0);
            }
            _stems += _stack.Count / 2;
            _stack.Clear();
        }

        // ---- path construction ----------------------------------------------

        private void MoveTo(double x, double y)
        {
            FlushContour();
            _contour = [new GlyphPoint(new Vector2d(x, y), OnCurve: true)];
            _x = x;
            _y = y;
            _stack.Clear();
        }

        private void LineTo(double x, double y)
        {
            RequireContour().Add(new GlyphPoint(new Vector2d(x, y), OnCurve: true));
            _x = x;
            _y = y;
        }

        private void CurveTo(double c1X, double c1Y, double c2X, double c2Y, double x, double y)
        {
            var contour = RequireContour();
            contour.Add(new GlyphPoint(new Vector2d(c1X, c1Y), OnCurve: false));
            contour.Add(new GlyphPoint(new Vector2d(c2X, c2Y), OnCurve: false));
            contour.Add(new GlyphPoint(new Vector2d(x, y), OnCurve: true));
            _x = x;
            _y = y;
        }

        private List<GlyphPoint> RequireContour() =>
            _contour ?? throw new FontFormatException($"Glyph {glyphIndex}: charstring draws before any moveto.");

        /// <summary>One rrcurveto sextet at stack offset <paramref name="i"/>.</summary>
        private void RelativeCurve(int i)
        {
            double c1X = _x + Arg(i), c1Y = _y + Arg(i + 1);
            double c2X = c1X + Arg(i + 2), c2Y = c1Y + Arg(i + 3);
            CurveTo(c1X, c1Y, c2X, c2Y, c2X + Arg(i + 4), c2Y + Arg(i + 5));
        }

        private void AlternatingLines(bool startHorizontal)
        {
            bool horizontal = startHorizontal;
            for (int i = 0; i < _stack.Count; i++)
            {
                if (horizontal)
                    LineTo(_x + Arg(i), _y);
                else
                    LineTo(_x, _y + Arg(i));
                horizontal = !horizontal;
            }
            _stack.Clear();
        }

        /// <summary>hhcurveto / vvcurveto: runs of curves whose ends stay on one axis;
        /// an odd leading argument is the first curve's cross-axis start delta.</summary>
        private void AxisAlignedCurves(bool vertical)
        {
            int i = 0;
            double lead = 0;
            if (_stack.Count % 4 == 1)
            {
                lead = Arg(0);
                i = 1;
            }
            for (; i + 3 < _stack.Count; i += 4)
            {
                double c1X, c1Y;
                if (vertical)
                {
                    c1X = _x + lead;
                    c1Y = _y + Arg(i);
                }
                else
                {
                    c1X = _x + Arg(i);
                    c1Y = _y + lead;
                }
                double c2X = c1X + Arg(i + 1), c2Y = c1Y + Arg(i + 2);
                if (vertical)
                    CurveTo(c1X, c1Y, c2X, c2Y, c2X, c2Y + Arg(i + 3));
                else
                    CurveTo(c1X, c1Y, c2X, c2Y, c2X + Arg(i + 3), c2Y);
                lead = 0;
            }
            _stack.Clear();
        }

        /// <summary>hvcurveto / vhcurveto: groups of four alternating which axis the
        /// curve starts and ends on; the final group may carry a fifth argument for
        /// the otherwise-implied cross component of the last end point.</summary>
        private void AlternatingCurves(bool startHorizontal)
        {
            bool horizontal = startHorizontal;
            int i = 0;
            while (_stack.Count - i >= 4)
            {
                bool last = _stack.Count - i == 5;
                double c1X, c1Y;
                if (horizontal)
                {
                    c1X = _x + Arg(i);
                    c1Y = _y;
                }
                else
                {
                    c1X = _x;
                    c1Y = _y + Arg(i);
                }
                double c2X = c1X + Arg(i + 1), c2Y = c1Y + Arg(i + 2);
                double endX, endY;
                if (horizontal)
                {
                    endY = c2Y + Arg(i + 3);
                    endX = c2X + (last ? Arg(i + 4) : 0);
                }
                else
                {
                    endX = c2X + Arg(i + 3);
                    endY = c2Y + (last ? Arg(i + 4) : 0);
                }
                CurveTo(c1X, c1Y, c2X, c2Y, endX, endY);
                horizontal = !horizontal;
                i += 4;
            }
            _stack.Clear();
        }

        private void Call((int Start, int End)[] subrs, string kind, int depth)
        {
            if (_stack.Count == 0)
                throw new FontFormatException($"Glyph {glyphIndex}: call{(kind == "global" ? "gsubr" : "subr")} with an empty stack.");
            int index = (int)_stack[^1] + Bias(subrs.Length);
            _stack.RemoveAt(_stack.Count - 1);
            if ((uint)index >= (uint)subrs.Length)
                throw new FontFormatException($"Glyph {glyphIndex}: {kind} subroutine {index} does not exist ({subrs.Length} defined).");
            Run(subrs[index].Start, subrs[index].End, depth + 1);
        }

        private void EndChar()
        {
            if (!_widthParsed)
            {
                _widthParsed = true;
                if (_stack.Count is 1 or 5)
                    _stack.RemoveAt(0);
            }
            if (_stack.Count == 4)
                throw new FontFormatException(
                    $"Glyph {glyphIndex}: endchar carries 4 arguments — the deprecated 'seac' accent composition, which is not supported.");
            _stack.Clear();
            _ended = true;
        }

        private void Escaped(int op)
        {
            switch (op)
            {
                case 35:                                     // flex: two curves, then the fd hint depth
                    RelativeCurve(0);
                    RelativeCurve(6);
                    _stack.Clear();
                    break;
                case 34:                                     // hflex: both curves level with the start
                {
                    double y0 = _y;
                    double c1X = _x + Arg(0), c1Y = _y;
                    double c2X = c1X + Arg(1), c2Y = c1Y + Arg(2);
                    CurveTo(c1X, c1Y, c2X, c2Y, c2X + Arg(3), c2Y);
                    double c3X = _x + Arg(4), c3Y = _y;
                    double c4X = c3X + Arg(5), c4Y = y0;
                    CurveTo(c3X, c3Y, c4X, c4Y, c4X + Arg(6), y0);
                    _stack.Clear();
                    break;
                }
                case 36:                                     // hflex1: ends level with the start
                {
                    double y0 = _y;
                    double c1X = _x + Arg(0), c1Y = _y + Arg(1);
                    double c2X = c1X + Arg(2), c2Y = c1Y + Arg(3);
                    CurveTo(c1X, c1Y, c2X, c2Y, c2X + Arg(4), c2Y);
                    double c3X = _x + Arg(5), c3Y = _y;
                    double c4X = c3X + Arg(6), c4Y = c3Y + Arg(7);
                    CurveTo(c3X, c3Y, c4X, c4Y, c4X + Arg(8), y0);
                    _stack.Clear();
                    break;
                }
                case 37:                                     // flex1: the end's implied component closes the deltas
                {
                    double startX = _x, startY = _y;
                    double dX = Arg(0) + Arg(2) + Arg(4) + Arg(6) + Arg(8);
                    double dY = Arg(1) + Arg(3) + Arg(5) + Arg(7) + Arg(9);
                    RelativeCurve(0);
                    double c1X = _x + Arg(6), c1Y = _y + Arg(7);
                    double c2X = c1X + Arg(8), c2Y = c1Y + Arg(9);
                    if (Math.Abs(dX) > Math.Abs(dY))
                        CurveTo(c1X, c1Y, c2X, c2Y, c2X + Arg(10), startY);
                    else
                        CurveTo(c1X, c1Y, c2X, c2Y, startX, c2Y + Arg(10));
                    _stack.Clear();
                    break;
                }
                default:
                    throw new FontFormatException(
                        $"Glyph {glyphIndex}: charstring operator 12 {op} (arithmetic/storage extension) is not supported.");
            }
        }
    }
}
