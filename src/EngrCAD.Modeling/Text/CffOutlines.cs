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
/// the legacy <c>seac</c> accent composition is SUPPORTED (endchar with 4 arguments —
/// see <see cref="ReadGlyph"/>), and the
/// Type 2 arithmetic/storage operators no real font uses.</para>
/// </summary>
internal sealed class CffOutlines
{
    private readonly byte[] _data;
    private readonly (int Start, int End)[] _charStrings;   // absolute spans, per glyph
    private readonly (int Start, int End)[] _globalSubrs;
    private readonly (int Start, int End)[][] _localSubrsPerFd;
    private readonly byte[] _fdForGlyph;                    // all zero for non-CID fonts

    // GID for each SID, from the charset (op 15). Null when the charset is the
    // predefined ISOAdobe ordering (0), where SID == GID for every glyph the font has;
    // seac resolution then uses the identity. The predefined EXPERT charsets (1, 2)
    // leave _expertCharset set and seac refuses by name — no text font uses them.
    private readonly Dictionary<int, int>? _gidForSid;
    private readonly bool _expertCharset;

    private CffOutlines(
        byte[] data,
        (int Start, int End)[] charStrings,
        (int Start, int End)[] globalSubrs,
        (int Start, int End)[][] localSubrsPerFd,
        byte[] fdForGlyph,
        Dictionary<int, int>? gidForSid,
        bool expertCharset)
    {
        _data = data;
        _charStrings = charStrings;
        _globalSubrs = globalSubrs;
        _localSubrsPerFd = localSubrsPerFd;
        _fdForGlyph = fdForGlyph;
        _gidForSid = gidForSid;
        _expertCharset = expertCharset;
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
        CffPrimitives.SkipIndex(span, ref at);                            // Name INDEX
        var topDicts = CffPrimitives.ReadIndex(span, ref at);             // Top DICT INDEX
        CffPrimitives.SkipIndex(span, ref at);                            // String INDEX
        var globalSubrs = CffPrimitives.ReadIndex(span, ref at);          // Global Subr INDEX

        if (topDicts.Length == 0)
            throw new FontFormatException("CFF Top DICT INDEX is empty; the table describes no font.");
        var top = CffPrimitives.ParseDict(span, topDicts[0].Start, topDicts[0].End);

        if (top.TryGetValue(CffPrimitives.Op(12, 6), out var type) && (int)type[0] != 2)
            throw new FontFormatException(
                $"CFF CharstringType is {(int)type[0]}; only Type 2 charstrings are supported.");

        if (!top.TryGetValue(17, out var charStringsOp))
            throw new FontFormatException("CFF Top DICT has no CharStrings offset (operator 17).");
        int charStringsAt = offset + (int)charStringsOp[0];
        var charStrings = CffPrimitives.ReadIndex(span, ref charStringsAt);
        if (charStrings.Length == 0)
            throw new FontFormatException("CFF CharStrings INDEX is empty; the font contains no glyph outlines.");

        // Private DICT(s) -> local subrs. CID-keyed fonts (ROS present) hold them per
        // font DICT in FDArray, with FDSelect mapping each glyph to its dict.
        (int Start, int End)[][] localSubrsPerFd;
        byte[] fdForGlyph;
        if (top.ContainsKey(CffPrimitives.Op(12, 30)))                    // ROS: CID-keyed
        {
            if (!top.TryGetValue(CffPrimitives.Op(12, 36), out var fdArrayOp) || !top.TryGetValue(CffPrimitives.Op(12, 37), out var fdSelectOp))
                throw new FontFormatException("CID-keyed CFF is missing FDArray or FDSelect.");
            int fdArrayAt = offset + (int)fdArrayOp[0];
            var fontDicts = CffPrimitives.ReadIndex(span, ref fdArrayAt);
            localSubrsPerFd = new (int, int)[fontDicts.Length][];
            for (int i = 0; i < fontDicts.Length; i++)
            {
                var dict = CffPrimitives.ParseDict(span, fontDicts[i].Start, fontDicts[i].End);
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
        // Charset (op 15): 0/absent = ISOAdobe (SID == GID), 1/2 = the predefined
        // Expert charsets, an offset = an explicit table in format 0, 1 or 2.
        Dictionary<int, int>? gidForSid = null;
        bool expertCharset = false;
        if (top.TryGetValue(15, out var charsetOp) && (int)charsetOp[0] is not 0)
        {
            int charsetValue = (int)charsetOp[0];
            if (charsetValue is 1 or 2)
                expertCharset = true;
            else
                gidForSid = ReadCharset(span, offset + charsetValue, charStrings.Length);
        }

        return new CffOutlines(
            data, charStrings, globalSubrs, localSubrsPerFd, fdForGlyph, gidForSid, expertCharset);
    }

    private static (int Start, int End)[] ReadPrivateSubrs(
        ReadOnlySpan<byte> span, int cffStart, Dictionary<int, double[]> dict)
    {
        if (!dict.TryGetValue(18, out var priv) || priv.Length < 2)
            return [];
        int size = (int)priv[0];
        int at = cffStart + (int)priv[1];
        var privateDict = CffPrimitives.ParseDict(span, at, at + size);
        if (!privateDict.TryGetValue(19, out var subrs))
            return [];
        int subrsAt = at + (int)subrs[0];                   // Subrs offset is relative to the Private DICT
        return CffPrimitives.ReadIndex(span, ref subrsAt);
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

    /// <summary>Charset formats 0 (a SID per glyph), 1 and 2 (ranges of consecutive
    /// SIDs with 1- or 2-byte counts), inverted to SID → GID for seac resolution.
    /// Glyph 0 is .notdef (SID 0) and is not stored. The FIRST glyph carrying a SID
    /// wins on a duplicate, matching every other CFF consumer.</summary>
    private static Dictionary<int, int> ReadCharset(ReadOnlySpan<byte> span, int at, int glyphCount)
    {
        var reader = new FontReader(span, at);
        int format = reader.ReadUInt8();
        var gidForSid = new Dictionary<int, int> { [0] = 0 };
        int gid = 1;
        switch (format)
        {
            case 0:
                for (; gid < glyphCount; gid++)
                    gidForSid.TryAdd(reader.ReadUInt16(), gid);
                break;
            case 1 or 2:
                while (gid < glyphCount)
                {
                    int first = reader.ReadUInt16();
                    int left = format == 1 ? reader.ReadUInt8() : reader.ReadUInt16();
                    for (int k = 0; k <= left && gid < glyphCount; k++, gid++)
                        gidForSid.TryAdd(first + k, gid);
                }
                break;
            default:
                throw new FontFormatException($"CFF charset format {format} is not defined (0, 1 and 2 exist).");
        }
        return gidForSid;
    }

    /// <summary>
    /// Adobe Standard Encoding as code → SID, the table seac's bchar/achar codes are
    /// defined against (CFF spec Appendix B; ⚠ transcribed — verify against the
    /// datasheet). Codes 32..126 map to SIDs 1..95 sequentially; the high region is
    /// irregular; unmapped codes carry SID 0 (.notdef), which seac refuses by name.
    /// </summary>
    private static readonly ushort[] StandardEncodingSid = BuildStandardEncoding();

    private static ushort[] BuildStandardEncoding()
    {
        var sid = new ushort[256];
        for (int code = 32; code <= 126; code++)
            sid[code] = (ushort)(code - 31);
        (int Code, int Sid)[] high =
        [
            (161, 96), (162, 97), (163, 98), (164, 99), (165, 100), (166, 101), (167, 102),
            (168, 103), (169, 104), (170, 105), (171, 106), (172, 107), (173, 108), (174, 109),
            (175, 110), (177, 111), (178, 112), (179, 113), (180, 114), (182, 115), (183, 116),
            (184, 117), (185, 118), (186, 119), (187, 120), (188, 121), (189, 122), (191, 123),
            (193, 124), (194, 125), (195, 126), (196, 127), (197, 128), (198, 129), (199, 130),
            (200, 131), (202, 132), (203, 133), (205, 134), (206, 135), (207, 136), (208, 137),
            (225, 138), (227, 139), (232, 140), (233, 141), (234, 142), (235, 143),
            (241, 144), (245, 145), (248, 146), (249, 147), (250, 148), (251, 149),
        ];
        foreach (var (code, s) in high)
            sid[code] = (ushort)s;
        return sid;
    }

    /// <summary>A seac component CODE resolved to its glyph: Standard Encoding names
    /// the SID, the charset names the glyph. Every miss is named — seac is a claim
    /// about two specific glyphs, and guessing either draws the wrong letter.</summary>
    private int ResolveSeacComponent(int glyphIndex, int code, string role)
    {
        if ((uint)code > 255)
            throw new FontFormatException(
                $"Glyph {glyphIndex}: seac {role} code {code} is outside 0..255.");
        int sid = StandardEncodingSid[code];
        if (sid == 0)
            throw new FontFormatException(
                $"Glyph {glyphIndex}: seac {role} code {code} has no Standard Encoding entry.");
        if (_expertCharset)
            throw new FontFormatException(
                $"Glyph {glyphIndex}: seac against a predefined Expert charset is not supported.");
        if (_gidForSid is null)
        {
            // ISOAdobe predefined charset: SID == GID.
            if (sid < _charStrings.Length)
                return sid;
            throw new FontFormatException(
                $"Glyph {glyphIndex}: seac {role} SID {sid} is past the font's {_charStrings.Length} glyphs.");
        }
        if (_gidForSid.TryGetValue(sid, out int gid))
            return gid;
        throw new FontFormatException(
            $"Glyph {glyphIndex}: seac {role} SID {sid} is not in the font's charset.");
    }

    // ---- Type 2 charstring interpreter --------------------------------------

    /// <summary>Decodes one glyph's outline. Contours are cubic
    /// (<see cref="GlyphContour.IsCubic"/>): on-curve anchors with off-curve control
    /// points in pairs, closing from the last point back to the first.</summary>
    public List<GlyphContour> ReadGlyph(int glyphIndex) => ReadGlyph(glyphIndex, allowSeac: true);

    private List<GlyphContour> ReadGlyph(int glyphIndex, bool allowSeac)
    {
        if ((uint)glyphIndex >= (uint)_charStrings.Length)
            throw new ArgumentOutOfRangeException(nameof(glyphIndex));
        var state = new Interpreter(this, _localSubrsPerFd[_fdForGlyph[glyphIndex]], glyphIndex);
        var (start, end) = _charStrings[glyphIndex];
        state.Run(start, end, depth: 0);
        state.FlushContour();
        if (state.Seac is not { } seac)
            return state.Contours;

        // The deprecated endchar accent form: the glyph is its BASE character's
        // outline plus its ACCENT character's, the accent displaced by (adx, ady).
        // Type 2 carries no sidebearing operands, so adx is the displacement verbatim
        // (the Type 1 asb correction has nothing to correct here). A component that is
        // itself seac is forbidden by the spec and refused by name, which is also what
        // bounds the recursion at one level.
        if (!allowSeac)
            throw new FontFormatException(
                $"Glyph {glyphIndex}: a seac component is itself seac-composed, which Type 2 forbids.");
        int baseGlyph = ResolveSeacComponent(glyphIndex, seac.BaseCode, "base");
        int accentGlyph = ResolveSeacComponent(glyphIndex, seac.AccentCode, "accent");
        var contours = ReadGlyph(baseGlyph, allowSeac: false);
        var shift = new Vector2d(seac.Adx, seac.Ady);
        foreach (var contour in ReadGlyph(accentGlyph, allowSeac: false))
        {
            var moved = new GlyphPoint[contour.Points.Count];
            for (int i = 0; i < moved.Length; i++)
                moved[i] = contour.Points[i] with { Position = contour.Points[i].Position + shift };
            contours.Add(new GlyphContour(moved, contour.IsCubic));
        }
        return contours;
    }

    /// <summary>
    /// Type 2's own dialect over the shared <see cref="Type2Interpreter"/>: the leading
    /// WIDTH operand that a charstring's first stack-clearing operator may carry, and
    /// <c>endchar</c> — including the deprecated four-argument accent form. CFF2 has
    /// neither, which is the whole difference between the two interpreters.
    /// </summary>
    private sealed class Interpreter(CffOutlines font, (int Start, int End)[] localSubrs, int glyphIndex)
        : Type2Interpreter(font._data, localSubrs, font._globalSubrs, glyphIndex)
    {
        private bool _widthParsed;

        protected override string Dialect => "Type 2";

        /// <summary>The deprecated endchar accent form's arguments, when this glyph
        /// used it — the outer <see cref="ReadGlyph(int, bool)"/> composes.</summary>
        public (double Adx, double Ady, int BaseCode, int AccentCode)? Seac { get; private set; }

        protected override void BeforeMove(int expected) => StripWidth(expected);

        protected override void BeforeStems()
        {
            if (_widthParsed)
                return;
            _widthParsed = true;
            if (Stack.Count % 2 != 0)
                Stack.RemoveAt(0);
        }

        protected override bool Extend(int op, ref FontReader reader)
        {
            if (op != 14)                                // endchar
                return false;
            EndChar();
            return true;
        }

        /// <summary>The first stack-clearing operator of a charstring may carry one
        /// extra leading argument: the glyph's width (delta from nominalWidthX). The
        /// advance already comes from <c>hmtx</c> — OpenType requires the two to agree
        /// — so the width is stripped, not stored.</summary>
        private void StripWidth(int expected)
        {
            if (_widthParsed)
                return;
            _widthParsed = true;
            if (Stack.Count > expected)
                Stack.RemoveAt(0);
        }

        private void EndChar()
        {
            if (!_widthParsed)
            {
                _widthParsed = true;
                if (Stack.Count is 1 or 5)
                    Stack.RemoveAt(0);
            }
            if (Stack.Count == 4)
                Seac = (Arg(0), Arg(1), (int)Arg(2), (int)Arg(3));
            Stack.Clear();
            Ended = true;
        }
    }
}
