using System.Text;

namespace EngrCAD.Modeling.Tests.Text;

/// <summary>Which shape the synthetic CFF font is built in.</summary>
internal sealed record SyntheticCffFontOptions
{
    /// <summary>Build a CID-keyed font: ROS in the Top DICT, two font DICTs in
    /// FDArray (only the second carries local subrs), FDSelect mapping glyphs.</summary>
    public bool CidKeyed { get; init; }

    /// <summary>With <see cref="CidKeyed"/>: write FDSelect in format 0 (a byte per
    /// glyph) instead of format 3 (ranges).</summary>
    public bool FdSelectFormat0 { get; init; }

    /// <summary>Write a CharstringType operator with this value (default absent = 2).</summary>
    public int? CharstringType { get; init; }

    /// <summary>Replace the 'I' charstring with a legacy seac endchar (4 arguments).</summary>
    public bool SeacEndchar { get; init; }

    /// <summary>The seac base/accent CODES (Standard Encoding) when
    /// <see cref="SeacEndchar"/> is set. The defaults resolve through the identity
    /// (ISOAdobe) charset to the Curve and Wave glyphs: code 36 = SID 5 = GID 5 is the
    /// wave, 35 = SID 4 = GID 4 the curve.</summary>
    public int SeacBaseCode { get; init; } = 35;
    public int SeacAccentCode { get; init; } = 36;

    /// <summary>Writes a charset (op 15) in this format: null = none (the predefined
    /// ISOAdobe identity), 0 = one SID per glyph, 1 = a range. Format 0 REVERSES the
    /// identity (GID g carries SID 10−g), format 1 shifts it (SID = GID + 2) — both
    /// chosen so a resolution through the TABLE lands on different glyphs than the
    /// identity would, which is what proves the table was read.</summary>
    public int? CharsetFormat { get; init; }

    /// <summary>Inject an unsupported Type 2 extension operator (12 15) into 'I'.</summary>
    public bool ArithmeticOp { get; init; }
}

/// <summary>
/// A complete OpenType/CFF (<c>OTTO</c>) font assembled byte by byte — the PostScript
/// sibling of <see cref="SyntheticFont"/>, and like it, executable documentation of the
/// format we parse: CFF header, INDEX structures, Top/Private DICTs with fixed-width
/// operands, Type 2 charstrings, local and global subroutines, and (optionally) the
/// CID-keyed FDArray/FDSelect machinery.
/// <para>The glyphs exercise every charstring operator family and the two classic
/// decoding traps:</para>
/// <list type="bullet">
/// <item><description><c>space</c> — a width-only <c>endchar</c>.</description></item>
/// <item><description><c>'I'</c> — rectangle via <c>rmoveto</c> + alternating
/// <c>hlineto</c>, with a leading width operand.</description></item>
/// <item><description><c>'O'</c> — two contours (outer + counter), both wound the same
/// way, so classification must be by containment.</description></item>
/// <item><description><c>'C'</c> — <c>hstemhm</c> + implicit-vstem <c>hintmask</c>
/// declaring NINE stems, so the mask is TWO data bytes: skipping one garbles every
/// operator after it — CFF's cousin of the implied-midpoint trap — followed by an
/// <c>rrcurveto</c> arch whose midpoint is exactly representable.</description></item>
/// <item><description><c>'W'</c> — <c>hhcurveto</c> (odd leading form),
/// <c>vvcurveto</c>, and <c>hvcurveto</c> with the 5-argument last group.</description></item>
/// <item><description><c>'S'</c> — a local and a global subroutine call (bias 107),
/// with one coordinate written as a 255 (16.16 fixed) operand.</description></item>
/// <item><description><c>'F'</c> — <c>hflex</c> and <c>flex1</c>.</description></item>
/// </list>
/// </summary>
internal static class SyntheticCffFont
{
    public const int UnitsPerEm = 1000;
    public const int Ascender = 800;
    public const int Descender = -200;
    public const int LineGap = 100;
    public const int CapHeight = 700;
    public const string FamilyName = "EngrCAD Synthetic CFF";

    public const int NotdefGlyph = 0;
    public const int SpaceGlyph = 1;
    public const int RectGlyph = 2;
    public const int RingGlyph = 3;
    public const int CurveGlyph = 4;
    public const int WaveGlyph = 5;
    public const int SubrGlyph = 6;
    public const int FlexGlyph = 7;
    public const int GlyphCount = 8;

    /// <summary>Advance widths in font units, per glyph index (written to hmtx; the
    /// charstring width operands agree, as OpenType requires).</summary>
    public static readonly int[] Advances = [600, 500, 400, 800, 500, 400, 420, 700];

    /// <summary>Left side bearings in font units, per glyph index.</summary>
    public static readonly int[] Bearings = [0, 0, 100, 0, 0, 0, 0, 0];

    /// <summary>defaultWidthX / nominalWidthX written to the Private DICT; charstring
    /// width operands are deltas from the nominal.</summary>
    public const int NominalWidthX = 500;

    /// <summary>First glyph index that FDSelect maps to font DICT 1 (the one carrying
    /// local subrs) in the CID-keyed variant.</summary>
    public const int FirstFd1Glyph = SubrGlyph;

    public static byte[] Build(SyntheticCffFontOptions? options = null)
    {
        options ??= new SyntheticCffFontOptions();

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["CFF "] = BuildCff(options),
            ["cmap"] = Cmap(),
            ["head"] = Head(),
            ["hhea"] = Hhea(),
            ["hmtx"] = Hmtx(),
            ["maxp"] = Maxp(),
            ["name"] = Name(),
            ["OS/2"] = Os2(),
        };
        return Assemble(tables);
    }

    /// <summary>An otherwise complete OTTO font whose outline table is a MALFORMED
    /// <c>CFF2</c> (version 0, which no CFF2 is) — the by-name refusal path. Every other
    /// table is present, so the refusal has to come from the CFF2 reader rather than from
    /// a missing required table.</summary>
    public static byte[] BuildCff2Stub()
    {
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["CFF2"] = [0, 0, 0, 0],
            ["cmap"] = Cmap(),
            ["head"] = Head(),
            ["hhea"] = Hhea(),
            ["hmtx"] = Hmtx(),
            ["maxp"] = Maxp(),
        };
        return Assemble(tables);
    }

    // ---- sfnt container (OTTO) ----------------------------------------------

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        int count = tables.Count;
        var file = new Be();
        file.U32(0x4F54544F);                                        // 'OTTO': PostScript outlines
        file.U16(count);
        int entrySelector = (int)Math.Floor(Math.Log2(Math.Max(1, count)));
        int searchRange = 16 * (1 << entrySelector);
        file.U16(searchRange).U16(entrySelector).U16(count * 16 - searchRange);

        int offset = 12 + count * 16;
        foreach (var (tag, bytes) in tables)
        {
            file.Tag(tag).U32(0).U32(offset).U32(bytes.Length);
            offset += Align(bytes.Length, 4);
        }
        foreach (var bytes in tables.Values)
        {
            file.Bytes(bytes);
            file.PadTo(4);
        }
        return file.ToArray();
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    // ---- CFF table -----------------------------------------------------------

    private static byte[] BuildCff(SyntheticCffFontOptions options)
    {
        var charStrings = CharStringsIndex(options);
        var localSubrs = IndexOf(LocalSubr0());
        var globalSubrs = IndexOf(GlobalSubr0());
        var nameIndex = IndexOf(Encoding.ASCII.GetBytes("EngrCADSyntheticCFF"));
        var stringIndex = IndexOf();                                 // empty

        // Every DICT offset operand is written in the fixed 5-byte (29) encoding, so
        // DICT sizes do not depend on the values and one measuring pass suffices.
        byte[] Fixed(SyntheticCffFontOptions o, int charStringsAt, int privateSize, int privateAt,
                     int fdArrayAt, int fdSelectAt, int charsetAt = 0)
        {
            var cff = new Be();
            cff.U8(1).U8(0).U8(4).U8(4);                             // header: major, minor, hdrSize, offSize
            cff.Bytes(nameIndex);
            cff.Bytes(IndexOf(TopDict(o, charStringsAt, privateSize, privateAt, fdArrayAt, fdSelectAt, charsetAt)));
            cff.Bytes(stringIndex);
            cff.Bytes(globalSubrs);
            return cff.ToArray();
        }

        int prefix = Fixed(options, 0, 0, 0, 0, 0).Length;           // sizes are value-independent
        int charStringsAt = prefix;

        if (!options.CidKeyed)
        {
            // Subrs offset is relative to the Private DICT start; the local subr INDEX
            // sits immediately after it, so the offset is the dict's own (fixed) size.
            int privateDictSize = PrivateDict(subrsOffset: 0).Length;
            var privateDict = PrivateDict(subrsOffset: privateDictSize);
            int privateAt = charStringsAt + charStrings.Length;
            int charsetAt = privateAt + privateDict.Length + localSubrs.Length;
            var cff = new Be();
            cff.Bytes(Fixed(options, charStringsAt, privateDict.Length, privateAt, 0, 0, charsetAt));
            cff.Bytes(charStrings);
            cff.Bytes(privateDict);
            cff.Bytes(localSubrs);
            if (options.CharsetFormat is { } charsetFormat)
                cff.Bytes(Charset(charsetFormat));
            return cff.ToArray();
        }
        else
        {
            var privateNoSubrs = PrivateDict(subrsOffset: null);
            var privateWithSubrsSized = PrivateDict(subrsOffset: 0);
            int fdSelectAt = charStringsAt + charStrings.Length;
            var fdSelect = FdSelect(options.FdSelectFormat0);
            int fdArrayAt = fdSelectAt + fdSelect.Length;

            // FDArray: two font DICTs; sizes are value-independent (5-byte operands).
            byte[] FontDicts(int priv0At, int priv1At) => IndexOf(
                FontDict(privateNoSubrs.Length, priv0At),
                FontDict(privateWithSubrsSized.Length, priv1At));
            int fdArrayLength = FontDicts(0, 0).Length;
            int priv0At = fdArrayAt + fdArrayLength;
            int priv1At = priv0At + privateNoSubrs.Length;
            var privateWithSubrs = PrivateDict(subrsOffset: privateWithSubrsSized.Length);

            var cff = new Be();
            cff.Bytes(Fixed(options, charStringsAt, 0, 0, fdArrayAt, fdSelectAt));
            cff.Bytes(charStrings);
            cff.Bytes(fdSelect);
            cff.Bytes(FontDicts(priv0At, priv1At));
            cff.Bytes(privateNoSubrs);
            cff.Bytes(privateWithSubrs);
            cff.Bytes(localSubrs);
            return cff.ToArray();
        }
    }

    private static byte[] TopDict(SyntheticCffFontOptions options, int charStringsAt,
        int privateSize, int privateAt, int fdArrayAt, int fdSelectAt, int charsetAt = 0)
    {
        var dict = new Be();
        if (options.CharsetFormat is not null)
        {
            DictInt(dict, charsetAt);
            dict.U8(15);                                             // charset offset
        }
        if (options.CharstringType is { } type)
        {
            DictInt(dict, type);
            dict.U8(12).U8(6);                                       // CharstringType
        }
        if (options.CidKeyed)
        {
            DictInt(dict, 391);                                      // registry SID (arbitrary)
            DictInt(dict, 392);                                      // ordering SID
            DictInt(dict, 0);                                        // supplement
            dict.U8(12).U8(30);                                      // ROS -> CID-keyed
        }
        DictInt(dict, charStringsAt);
        dict.U8(17);                                                 // CharStrings
        if (options.CidKeyed)
        {
            DictInt(dict, fdArrayAt);
            dict.U8(12).U8(36);                                      // FDArray
            DictInt(dict, fdSelectAt);
            dict.U8(12).U8(37);                                      // FDSelect
        }
        else
        {
            DictInt(dict, privateSize);
            DictInt(dict, privateAt);
            dict.U8(18);                                             // Private: [size, offset]
        }
        return dict.ToArray();
    }

    private static byte[] FontDict(int privateSize, int privateAt)
    {
        var dict = new Be();
        DictInt(dict, privateSize);
        DictInt(dict, privateAt);
        dict.U8(18);                                                 // Private
        return dict.ToArray();
    }

    private static byte[] PrivateDict(int? subrsOffset)
    {
        var dict = new Be();
        DictInt(dict, NominalWidthX);
        dict.U8(20);                                                 // defaultWidthX
        DictInt(dict, NominalWidthX);
        dict.U8(21);                                                 // nominalWidthX
        if (subrsOffset is { } at)
        {
            DictInt(dict, at);
            dict.U8(19);                                             // Subrs (offset relative to Private DICT)
        }
        return dict.ToArray();
    }

    /// <summary>DICT integer in the fixed 5-byte encoding (operator 29), so DICT sizes
    /// never depend on operand values.</summary>
    private static void DictInt(Be dict, int value) => dict.U8(29).U32(value);

    private static byte[] FdSelect(bool format0)
    {
        var fd = new Be();
        if (format0)
        {
            fd.U8(0);
            for (int glyph = 0; glyph < GlyphCount; glyph++)
                fd.U8(glyph >= FirstFd1Glyph ? 1 : 0);
        }
        else
        {
            fd.U8(3).U16(2);                                         // format 3, two ranges
            fd.U16(0).U8(0);                                         // glyphs 0.. -> font DICT 0
            fd.U16(FirstFd1Glyph).U8(1);                             // glyphs FirstFd1Glyph.. -> font DICT 1
            fd.U16(GlyphCount);                                      // sentinel
        }
        return fd.ToArray();
    }

    // ---- INDEX ---------------------------------------------------------------

    private static byte[] IndexOf(params byte[][] items)
    {
        var index = new Be();
        index.U16(items.Length);
        if (items.Length == 0)
            return index.ToArray();

        int total = items.Sum(i => i.Length);
        int offSize = total + 1 <= 0xFF ? 1 : 2;
        index.U8(offSize);
        int offset = 1;                                              // offsets are 1-based
        foreach (var item in items)
        {
            WriteOffset(index, offset, offSize);
            offset += item.Length;
        }
        WriteOffset(index, offset, offSize);
        foreach (var item in items)
            index.Bytes(item);
        return index.ToArray();

        static void WriteOffset(Be index, int value, int offSize)
        {
            if (offSize == 2)
                index.U16(value);
            else
                index.U8(value);
        }
    }

    // ---- charstrings ---------------------------------------------------------

    /// <summary>Format 0: GID g carries SID 10−g (the identity REVERSED); format 1:
    /// one ascending range, SID = GID + 2. See the option's remarks.</summary>
    private static byte[] Charset(int format)
    {
        var cs = new Be();
        cs.U8((byte)format);
        if (format == 0)
        {
            for (int gid = 1; gid < GlyphCount; gid++)
                cs.U16(10 - gid);
        }
        else if (format == 1)
        {
            cs.U16(3).U8(GlyphCount - 2);                            // SIDs 3..9 for GIDs 1..7
        }
        else
        {
            throw new InvalidOperationException("only charset formats 0 and 1 are built here");
        }
        return cs.ToArray();
    }

    private static byte[] CharStringsIndex(SyntheticCffFontOptions options)
    {
        var glyphs = new byte[GlyphCount][];
        glyphs[NotdefGlyph] = Cs(cs => Op(cs, 14));                  // endchar, nothing drawn
        glyphs[SpaceGlyph] = Cs(cs =>
        {
            Num(cs, Advances[SpaceGlyph] - NominalWidthX);           // width-only endchar
            Op(cs, 14);
        });
        glyphs[RectGlyph] = options switch
        {
            { SeacEndchar: true } => Cs(cs =>
            {
                Num(cs, Advances[RectGlyph] - NominalWidthX);        // width, then seac
                Num(cs, 100); Num(cs, 50);                           // adx ady
                Num(cs, options.SeacBaseCode); Num(cs, options.SeacAccentCode);
                Op(cs, 14);                                          // seac-form endchar
            }),
            { ArithmeticOp: true } => Cs(cs =>
            {
                Num(cs, 1); Num(cs, 2);
                Op(cs, 12, 15);                                      // 'neg': unsupported extension
            }),
            _ => RectCharstring(),
        };
        glyphs[RingGlyph] = RingCharstring();
        glyphs[CurveGlyph] = CurveCharstring();
        glyphs[WaveGlyph] = WaveCharstring();
        glyphs[SubrGlyph] = SubrCharstring();
        glyphs[FlexGlyph] = FlexCharstring();
        return IndexOf(glyphs);
    }

    /// <summary>'I': 200x700 rectangle at x = 100 — width operand, rmoveto, and the
    /// alternating hlineto (h, v, h); the implicit closepath supplies the fourth side.
    /// Outline: (100,0) (300,0) (300,700) (100,700).</summary>
    private static byte[] RectCharstring() => Cs(cs =>
    {
        Num(cs, Advances[RectGlyph] - NominalWidthX);                // width (odd argument count)
        Num(cs, 100); Num(cs, 0); Op(cs, 21);                        // rmoveto -> (100, 0)
        Num(cs, 200); Num(cs, 700); Num(cs, -200); Op(cs, 6);        // hlineto h,v,h
        Op(cs, 14);                                                  // endchar
    });

    /// <summary>'O': outer 700x700 square and a 300x300 counter, BOTH wound
    /// counter-clockwise — hole classification must come from containment.</summary>
    private static byte[] RingCharstring() => Cs(cs =>
    {
        Num(cs, 0); Num(cs, 0); Op(cs, 21);                          // rmoveto -> (0, 0)
        Num(cs, 700); Num(cs, 700); Num(cs, -700); Op(cs, 6);        // hlineto: (700,0) (700,700) (0,700)
        Num(cs, 200); Num(cs, -500); Op(cs, 21);                     // rmoveto -> (200, 200)
        Num(cs, 300); Num(cs, 300); Num(cs, -300); Op(cs, 6);        // hlineto: (500,200) (500,500) (200,500)
        Op(cs, 14);
    });

    /// <summary>'C': hstemhm declares 5 stems, then hintmask's leftover arguments add
    /// 4 implicit vstems — 9 stems, so the mask is TWO data bytes (the counting trap).
    /// The outline is a cubic arch (0,0)-(400,0) with controls (0,400) and (400,400);
    /// its midpoint B(1/2) = (200, 300) is exactly representable, pinned at signed
    /// distance zero by the tests.</summary>
    private static byte[] CurveCharstring() => Cs(cs =>
    {
        Num(cs, Advances[CurveGlyph] - NominalWidthX);               // width
        for (int i = 0; i < 5; i++)                                  // 5 horizontal stems
        {
            Num(cs, i == 0 ? 0 : 100);
            Num(cs, 20);
        }
        Op(cs, 18);                                                  // hstemhm
        for (int i = 0; i < 4; i++)                                  // 4 implicit vertical stems
        {
            Num(cs, i == 0 ? 0 : 80);
            Num(cs, 20);
        }
        Op(cs, 19);                                                  // hintmask ...
        cs.U8(0xFF).U8(0x80);                                        // ... with (9+7)/8 = 2 data bytes
        Num(cs, 0); Num(cs, 0); Op(cs, 21);                          // rmoveto -> (0, 0)
        Num(cs, 0); Num(cs, 400); Num(cs, 400); Num(cs, 0);
        Num(cs, 0); Num(cs, -400); Op(cs, 8);                        // rrcurveto -> arch to (400, 0)
        Op(cs, 14);                                                  // implicit close back to (0, 0)
    });

    /// <summary>Expected decoded contour of 'C' (cubic: on, off, off, on).</summary>
    public static readonly (double X, double Y, bool On)[] CurvePoints =
        [(0, 0, true), (0, 400, false), (400, 400, false), (400, 0, true)];

    /// <summary>The arch's exactly representable midpoint, (P0 + 3P1 + 3P2 + P3)/8.</summary>
    public static readonly (double X, double Y) CurveMidpoint = (200, 300);

    /// <summary>'W': hhcurveto with the odd leading dy, vvcurveto, hvcurveto with the
    /// 5-argument final group, then a line to the baseline (implicit close).</summary>
    private static byte[] WaveCharstring() => Cs(cs =>
    {
        Num(cs, 0); Num(cs, 0); Op(cs, 21);                          // rmoveto -> (0, 0)
        Num(cs, 100); Num(cs, 50); Num(cs, 50); Num(cs, 0); Num(cs, 50);
        Op(cs, 27);                                                  // hhcurveto (odd): dy1 dxa dxb dyb dxc
        Num(cs, 50); Num(cs, 0); Num(cs, 50); Num(cs, 50);
        Op(cs, 26);                                                  // vvcurveto: dya dxb dyb dyc
        Num(cs, 50); Num(cs, 50); Num(cs, 50); Num(cs, 50); Num(cs, 25);
        Op(cs, 31);                                                  // hvcurveto with trailing dxf
        Num(cs, 0); Num(cs, -350); Op(cs, 5);                        // rlineto -> (275, 0)
        Op(cs, 14);
    });

    /// <summary>Expected decoded contour of 'W'.</summary>
    public static readonly (double X, double Y, bool On)[] WavePoints =
    [
        (0, 0, true),
        (50, 100, false), (100, 100, false), (150, 100, true),       // hhcurveto
        (150, 150, false), (150, 200, false), (150, 250, true),      // vvcurveto
        (200, 250, false), (250, 300, false), (275, 350, true),      // hvcurveto (+dxf)
        (275, 0, true),                                              // rlineto
    ];

    /// <summary>'S': a 300x700 rectangle whose right edge comes from a LOCAL subr and
    /// whose top edge from a GLOBAL subr (both index 0, bias 107 — operand -107), with
    /// the 300 written as a 16.16 fixed operand to exercise the 255 encoding.</summary>
    private static byte[] SubrCharstring() => Cs(cs =>
    {
        Num(cs, 0); Num(cs, 0); Op(cs, 21);                          // rmoveto -> (0, 0)
        NumFixed(cs, 300);                                           // 255-encoded 16.16
        Op(cs, 6);                                                   // hlineto -> (300, 0)
        Num(cs, -107); Op(cs, 10);                                   // callsubr 0
        Num(cs, -107); Op(cs, 29);                                   // callgsubr 0
        Op(cs, 14);                                                  // implicit close to (0, 0)
    });

    private static byte[] LocalSubr0() => Cs(cs =>
    {
        Num(cs, 700); Op(cs, 7);                                     // vlineto -> (300, 700)
        Op(cs, 11);                                                  // return
    });

    private static byte[] GlobalSubr0() => Cs(cs =>
    {
        Num(cs, -300); Op(cs, 6);                                    // hlineto -> (0, 700)
        Op(cs, 11);                                                  // return
    });

    /// <summary>Expected decoded contour of 'S' (a rectangle drawn across two subrs).</summary>
    public static readonly (double X, double Y, bool On)[] SubrPoints =
        [(0, 0, true), (300, 0, true), (300, 700, true), (0, 700, true)];

    /// <summary>'F': contour 1 is an hflex bump closed by lines; contour 2 is a flex1
    /// blob closed by the implicit closepath.</summary>
    private static byte[] FlexCharstring() => Cs(cs =>
    {
        Num(cs, 0); Num(cs, 0); Op(cs, 21);                          // rmoveto -> (0, 0)
        foreach (int arg in new[] { 100, 100, 100, 100, 100, 100, 100 })
            Num(cs, arg);
        Op(cs, 12, 34);                                              // hflex
        Num(cs, 0); Num(cs, 100); Op(cs, 5);                         // rlineto -> (600, 100)
        Num(cs, -600); Op(cs, 6);                                    // hlineto -> (0, 100); close to (0,0)
        Num(cs, 0); Num(cs, 100); Op(cs, 21);                        // rmoveto (pen-relative from (0,100)) -> (0, 200)
        foreach (int arg in new[] { 30, 50, 30, 0, 30, -50, 30, 50, 30, 0, 30 })
            Num(cs, arg);
        Op(cs, 12, 37);                                              // flex1 (|dx| > |dy| -> end y = start y)
        Num(cs, 0); Num(cs, -40); Op(cs, 5);                         // rlineto -> (180, 160); close to (0,200)
        Op(cs, 14);
    });

    /// <summary>Expected decoded contours of 'F'.</summary>
    public static readonly (double X, double Y, bool On)[][] FlexPoints =
    [
        [
            (0, 0, true),
            (100, 0, false), (200, 100, false), (300, 100, true),    // hflex, first half
            (400, 100, false), (500, 0, false), (600, 0, true),      // hflex, second half
            (600, 100, true), (0, 100, true),
        ],
        [
            (0, 200, true),
            (30, 250, false), (60, 250, false), (90, 200, true),     // flex1, first half
            (120, 250, false), (150, 250, false), (180, 200, true),  // flex1, second half (y snaps to start)
            (180, 160, true),
        ],
    ];

    // ---- charstring encoding helpers ----------------------------------------

    private static byte[] Cs(Action<Be> write)
    {
        var cs = new Be();
        write(cs);
        return cs.ToArray();
    }

    private static void Op(Be cs, int op) => cs.U8(op);

    private static void Op(Be cs, int escape, int op) => cs.U8(escape).U8(op);

    /// <summary>Minimal Type 2 integer encoding (1, 2, or 3 bytes).</summary>
    private static void Num(Be cs, int value)
    {
        if (value is >= -107 and <= 107)
        {
            cs.U8(value + 139);
        }
        else if (value is >= 108 and <= 1131)
        {
            int v = value - 108;
            cs.U8(247 + (v >> 8)).U8(v & 0xFF);
        }
        else if (value is >= -1131 and <= -108)
        {
            int v = -value - 108;
            cs.U8(251 + (v >> 8)).U8(v & 0xFF);
        }
        else
        {
            cs.U8(28).I16(value);
        }
    }

    /// <summary>The 5-byte 16.16 fixed-point encoding (operand byte 255).</summary>
    private static void NumFixed(Be cs, double value) => cs.U8(255).U32((long)Math.Round(value * 65536) & 0xFFFFFFFFL);

    // ---- the ordinary OpenType tables ---------------------------------------

    private static byte[] Head()
    {
        var head = new Be();
        head.U32(0x00010000).U32(0x00010000);
        head.U32(0).U32(0x5F0F3CF5);
        head.U16(0b1011).U16(UnitsPerEm);
        head.U32(0).U32(0).U32(0).U32(0);
        head.I16(0).I16(-200).I16(700).I16(700);                     // xMin, yMin, xMax, yMax
        head.U16(0).U16(8).I16(2);
        head.I16(0).I16(0);                                          // indexToLocFormat (meaningless without loca), glyphDataFormat
        return head.ToArray();
    }

    private static byte[] Maxp()
    {
        var maxp = new Be();
        maxp.U32(0x00005000).U16(GlyphCount);                        // version 0.5: the CFF flavour of maxp
        return maxp.ToArray();
    }

    private static byte[] Hhea()
    {
        var hhea = new Be();
        hhea.U32(0x00010000);
        hhea.I16(Ascender).I16(Descender).I16(LineGap);
        hhea.U16(800).I16(0).I16(800).I16(700);
        hhea.I16(1).I16(0).I16(0);
        hhea.I16(0).I16(0).I16(0).I16(0);
        hhea.I16(0).U16(GlyphCount);
        return hhea.ToArray();
    }

    private static byte[] Hmtx()
    {
        var hmtx = new Be();
        for (int i = 0; i < GlyphCount; i++)
            hmtx.U16(Advances[i]).I16(Bearings[i]);
        return hmtx.ToArray();
    }

    private static byte[] Cmap()
    {
        (int Code, int Glyph)[] mappings =
        [
            (' ', SpaceGlyph), ('C', CurveGlyph), ('F', FlexGlyph), ('I', RectGlyph),
            ('O', RingGlyph), ('S', SubrGlyph), ('W', WaveGlyph),
        ];
        int segments = mappings.Length + 1;

        var subtable = new Be();
        subtable.U16(4).U16(16 + segments * 8).U16(0);
        int entrySelector = (int)Math.Floor(Math.Log2(segments));
        int searchRange = 2 * (1 << entrySelector);
        subtable.U16(segments * 2).U16(searchRange).U16(entrySelector).U16(segments * 2 - searchRange);
        foreach (var (code, _) in mappings)
            subtable.U16(code);
        subtable.U16(0xFFFF);
        subtable.U16(0);
        foreach (var (code, _) in mappings)
            subtable.U16(code);
        subtable.U16(0xFFFF);
        foreach (var (code, glyph) in mappings)
            subtable.I16(glyph - code);
        subtable.I16(1);
        for (int i = 0; i < segments; i++)
            subtable.U16(0);
        var subtableBytes = subtable.ToArray();

        var cmap = new Be();
        cmap.U16(0).U16(1);
        cmap.U16(3).U16(1).U32(12);
        cmap.Bytes(subtableBytes);
        return cmap.ToArray();
    }

    private static byte[] Name()
    {
        var value = Encoding.BigEndianUnicode.GetBytes(FamilyName);
        var name = new Be();
        name.U16(0).U16(1).U16(6 + 12);
        name.U16(3).U16(1).U16(0x0409).U16(1).U16(value.Length).U16(0);
        name.Bytes(value);
        return name.ToArray();
    }

    private static byte[] Os2()
    {
        var os2 = new Be();
        os2.U16(4);
        while (os2.Count < 88)
            os2.U8(0);
        os2.I16(CapHeight);
        os2.I16(0).U16(0).U16(0).U16(0);
        return os2.ToArray();
    }
}
