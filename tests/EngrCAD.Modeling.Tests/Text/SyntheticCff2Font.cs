namespace EngrCAD.Modeling.Tests.Text;

/// <summary>
/// A complete OpenType/CFF2 VARIABLE font assembled byte by byte — the PostScript half
/// of the variable-font story, and the executable documentation of what CFF2 changes
/// against <see cref="SyntheticCffFont"/>: a bare Top DICT rather than an INDEX of one,
/// 32-bit INDEX counts, no Name/String INDEX and no charset, no width operand and no
/// <c>endchar</c>, an item variation store in the table, and the two operators that make
/// the outline a function of the design space.
/// <list type="bullet">
/// <item><description><c>'I'</c> — a rectangle whose two side deltas come from
/// <c>blend</c> over the DEFAULT variation store index (two regions, so each blend reads
/// two deltas: a reader that guesses the count misreads every operand after
/// it).</description></item>
/// <item><description><c>'O'</c> — <c>hstemhm</c> declaring five stems then a
/// <c>hintmask</c> whose leftover arguments add four implicit vstems: NINE stems, so the
/// mask is TWO data bytes. CFF2 has no width operand, so a reader that strips one counts
/// eight stems, reads one mask byte and garbles everything after.</description></item>
/// <item><description><c>'V'</c> — <c>vsindex</c> selecting an item variation data with
/// ONE region, so its blends read one delta each; a reader ignoring <c>vsindex</c> asks
/// for operands that are not there.</description></item>
/// <item><description><c>'S'</c> — a local and a global subroutine (bias 107) through
/// CFF2's 32-bit INDEXes.</description></item>
/// <item><description><c>'C'</c> — <c>rrcurveto</c>, so the shared curve arithmetic is
/// exercised through the CFF2 dialect.</description></item>
/// </list>
/// </summary>
internal static class SyntheticCff2Font
{
    public const int UnitsPerEm = 1000;
    public const int Ascender = 800;
    public const int Descender = -200;
    public const int LineGap = 100;
    public const int CapHeight = 700;
    public const string FamilyName = "EngrCAD Variable CFF2";

    public const int NotdefGlyph = 0;
    public const int RectGlyph = 1;
    public const int RingGlyph = 2;
    public const int VsIndexGlyph = 3;
    public const int SubrGlyph = 4;
    public const int CurveGlyph = 5;
    public const int GlyphCount = 6;

    public static readonly int[] Advances = [600, 400, 800, 500, 400, 500];
    public static readonly int[] Bearings = [0, 100, 0, 0, 0, 0];

    /// <summary>'I': the default half-width and the per-region deltas the blend adds.
    /// The width delta rides region 1 at zero, so only the weight axis widens it.</summary>
    public const int RectHalfWidth = 200;
    public const int RectWeightDelta = 60;
    public const int RectWidthDelta = 0;

    /// <summary>'V': one region only (the WIDTH axis), selected by <c>vsindex 1</c>.</summary>
    public const int VsIndexWidth = 300;
    public const int VsIndexDelta = 100;

    /// <summary>What <c>HVAR</c> adds to every glyph from <see cref="HvarMappedFrom"/> on —
    /// CFF2's only route to a varied advance, since a PostScript outline carries no
    /// phantom points.</summary>
    public const int HvarAdvanceDelta = 300;
    public const int HvarMappedFrom = RectGlyph;

    public static byte[] Build()
    {
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["CFF2"] = BuildCff2(),
            ["cmap"] = Cmap(),
            ["head"] = Head(),
            ["hhea"] = Hhea(),
            ["hmtx"] = Hmtx(),
            ["maxp"] = Maxp(),
            ["name"] = Sfnt.NameTable([(1, FamilyName),
                (SyntheticVariations.WeightNameId, SyntheticVariations.WeightName),
                (SyntheticVariations.WidthNameId, SyntheticVariations.WidthName),
                (SyntheticVariations.SemiboldNameId, SyntheticVariations.SemiboldInstance),
                (SyntheticVariations.CondensedNameId, SyntheticVariations.CondensedInstance),
                (SyntheticVariations.SemiboldPostScriptNameId, SyntheticVariations.SemiboldPostScriptName)]),
            ["OS/2"] = Os2(),
            ["fvar"] = SyntheticVariations.Fvar(),
            ["avar"] = SyntheticVariations.Avar(),
            ["HVAR"] = SyntheticVariations.Hvar(HvarAdvanceDelta, HvarMappedFrom),
        };
        return Sfnt.Assemble(tables, 0x4F54544F);                    // 'OTTO'
    }

    // ---- the CFF2 table -------------------------------------------------------

    private static byte[] BuildCff2()
    {
        var charStrings = CharStringsIndex();
        var globalSubrs = Index(GlobalSubr0());
        var localSubrs = Index(LocalSubr0());
        var store = VariationStore();

        // Every DICT offset rides the fixed 5-byte encoding, so sizes never depend on
        // values and one measuring pass suffices.
        int privateDictSize = PrivateDict(subrsOffset: 0).Length;
        var privateDict = PrivateDict(subrsOffset: privateDictSize);

        int topDictLength = TopDict(0, 0, 0).Length;
        int prefix = 5 + topDictLength + globalSubrs.Length;
        int charStringsAt = prefix;
        int fdArrayAt = charStringsAt + charStrings.Length;
        int fdArrayLength = Index(FontDict(privateDict.Length, 0)).Length;
        int privateAt = fdArrayAt + fdArrayLength;
        int vstoreAt = privateAt + privateDict.Length + localSubrs.Length;

        var cff = new Be();
        cff.U8(2).U8(0).U8(5).U16(topDictLength);                    // major, minor, headerSize, topDictLength
        cff.Bytes(TopDict(charStringsAt, fdArrayAt, vstoreAt));
        cff.Bytes(globalSubrs);
        cff.Bytes(charStrings);
        cff.Bytes(Index(FontDict(privateDict.Length, privateAt)));
        cff.Bytes(privateDict);
        cff.Bytes(localSubrs);
        cff.U16(store.Length);                                       // the vstore's own length prefix
        cff.Bytes(store);
        return cff.ToArray();
    }

    private static byte[] TopDict(int charStringsAt, int fdArrayAt, int vstoreAt)
    {
        var dict = new Be();
        DictInt(dict, charStringsAt);
        dict.U8(17);                                                 // CharStrings
        DictInt(dict, fdArrayAt);
        dict.U8(12).U8(36);                                          // FDArray
        DictInt(dict, vstoreAt);
        dict.U8(24);                                                 // vstore
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

    private static byte[] PrivateDict(int subrsOffset)
    {
        var dict = new Be();
        DictInt(dict, 0);
        dict.U8(22);                                                 // vsindex: item variation data 0 by default
        DictInt(dict, subrsOffset);
        dict.U8(19);                                                 // Subrs, relative to the Private DICT
        return dict.ToArray();
    }

    private static void DictInt(Be dict, int value) => dict.U8(29).U32(value);

    /// <summary>Two regions — one per axis — and two item variation data subtables: the
    /// default one blending over BOTH, and a second over the width region alone, which is
    /// what <c>vsindex</c> selects between. No delta rows: a CFF2 blend reads its deltas
    /// from the charstring and takes only the region SCALARS from the store.</summary>
    private static byte[] VariationStore() => SyntheticVariations.ItemVariationStore(
        regions:
        [
            [(0, 1, 1), (0, 0, 0)],                                  // region 0: the weight axis
            [(0, 0, 0), (0, 1, 1)],                                  // region 1: the width axis
        ],
        subtables: [[], []],
        regionsPerSubtable: [[0, 1], [1]]);

    // ---- CFF2 INDEX (32-bit count) --------------------------------------------

    private static byte[] Index(params byte[][] items)
    {
        var index = new Be();
        index.U32(items.Length);
        if (items.Length == 0)
            return index.ToArray();

        int total = items.Sum(i => i.Length);
        int offSize = total + 1 <= 0xFF ? 1 : 2;
        index.U8(offSize);
        int offset = 1;
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

    // ---- charstrings -----------------------------------------------------------

    private static byte[] CharStringsIndex()
    {
        var glyphs = new byte[GlyphCount][];
        glyphs[NotdefGlyph] = [];                                    // CFF2 has no endchar: a glyph ends at its data
        glyphs[RectGlyph] = RectCharstring();
        glyphs[RingGlyph] = RingCharstring();
        glyphs[VsIndexGlyph] = VsIndexCharstring();
        glyphs[SubrGlyph] = SubrCharstring();
        glyphs[CurveGlyph] = CurveCharstring();
        return Index(glyphs);
    }

    /// <summary>'I': a rectangle whose width blends over BOTH regions.</summary>
    private static byte[] RectCharstring() => Cs(cs =>
    {
        Num(cs, 100); Num(cs, 0); Op(cs, 21);                        // rmoveto -> (100, 0)
        Blend(cs, [RectHalfWidth], [[RectWeightDelta, RectWidthDelta]]);
        Num(cs, 0); Op(cs, 5);                                       // rlineto
        Num(cs, 0); Num(cs, 700); Op(cs, 5);
        Blend(cs, [-RectHalfWidth], [[-RectWeightDelta, -RectWidthDelta]]);
        Num(cs, 0); Op(cs, 5);                                       // implicit close back to (100, 0)
    });

    /// <summary>'O': the hintmask stem-counting trap, then two contours.</summary>
    private static byte[] RingCharstring() => Cs(cs =>
    {
        for (int i = 0; i < 5; i++)                                  // five horizontal stems
        {
            Num(cs, i == 0 ? 0 : 100);
            Num(cs, 20);
        }
        Op(cs, 18);                                                  // hstemhm
        for (int i = 0; i < 4; i++)                                  // four implicit vertical stems
        {
            Num(cs, i == 0 ? 0 : 80);
            Num(cs, 20);
        }
        Op(cs, 19);                                                  // hintmask ...
        cs.U8(0xFF).U8(0x80);                                        // ... with (9 + 7) / 8 = 2 data bytes
        Num(cs, 0); Num(cs, 0); Op(cs, 21);
        Num(cs, 700); Num(cs, 700); Num(cs, -700); Op(cs, 6);        // hlineto
        Num(cs, 200); Num(cs, -500); Op(cs, 21);
        Num(cs, 300); Num(cs, 300); Num(cs, -300); Op(cs, 6);
    });

    /// <summary>'V': <c>vsindex 1</c> selects the one-region item data, so every blend
    /// below reads ONE delta.</summary>
    private static byte[] VsIndexCharstring() => Cs(cs =>
    {
        Num(cs, 1); Op(cs, 15);                                      // vsindex 1
        Num(cs, 0); Num(cs, 0); Op(cs, 21);
        Blend(cs, [VsIndexWidth], [[VsIndexDelta]]);
        Num(cs, 0); Op(cs, 5);
        Num(cs, 0); Num(cs, 500); Op(cs, 5);
        Blend(cs, [-VsIndexWidth], [[-VsIndexDelta]]);
        Num(cs, 0); Op(cs, 5);
    });

    private static byte[] SubrCharstring() => Cs(cs =>
    {
        Num(cs, 0); Num(cs, 0); Op(cs, 21);
        Num(cs, 300); Op(cs, 6);                                     // hlineto -> (300, 0)
        Num(cs, -107); Op(cs, 10);                                   // callsubr 0 (bias 107)
        Num(cs, -107); Op(cs, 29);                                   // callgsubr 0
    });

    private static byte[] LocalSubr0() => Cs(cs =>
    {
        Num(cs, 700); Op(cs, 7);                                     // vlineto -> (300, 700)
        Op(cs, 11);                                                  // return
    });

    private static byte[] GlobalSubr0() => Cs(cs =>
    {
        Num(cs, -300); Op(cs, 6);                                    // hlineto -> (0, 700)
        Op(cs, 11);
    });

    private static byte[] CurveCharstring() => Cs(cs =>
    {
        Num(cs, 0); Num(cs, 0); Op(cs, 21);
        Num(cs, 0); Num(cs, 400); Num(cs, 400); Num(cs, 0);
        Num(cs, 0); Num(cs, -400); Op(cs, 8);                        // rrcurveto
    });

    /// <summary>Expected decoded contours at the DEFAULT instance.</summary>
    public static readonly (double X, double Y, bool On)[] SubrPoints =
        [(0, 0, true), (300, 0, true), (300, 700, true), (0, 700, true)];

    public static readonly (double X, double Y, bool On)[] CurvePoints =
        [(0, 0, true), (0, 400, false), (400, 400, false), (400, 0, true)];

    public static readonly (double X, double Y, bool On)[][] RingPoints =
    [
        [(0, 0, true), (700, 0, true), (700, 700, true), (0, 700, true)],
        [(200, 200, true), (500, 200, true), (500, 500, true), (200, 500, true)],
    ];

    // ---- charstring encoding ---------------------------------------------------

    private static byte[] Cs(Action<Be> write)
    {
        var cs = new Be();
        write(cs);
        return cs.ToArray();
    }

    private static void Op(Be cs, int op) => cs.U8(op);

    /// <summary>
    /// The <c>blend</c> operand layout: <c>n</c> default values, then <c>n × k</c> deltas
    /// (all of value 0's deltas, then all of value 1's), then the count <c>n</c>.
    /// </summary>
    private static void Blend(Be cs, int[] values, int[][] deltasPerValue)
    {
        foreach (int value in values)
            Num(cs, value);
        foreach (var deltas in deltasPerValue)
        {
            foreach (int delta in deltas)
                Num(cs, delta);
        }
        Num(cs, values.Length);
        Op(cs, 16);                                                  // blend
    }

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

    // ---- the ordinary tables ---------------------------------------------------

    private static byte[] Head()
    {
        var head = new Be();
        head.U32(0x00010000).U32(0x00010000);
        head.U32(0).U32(0x5F0F3CF5);
        head.U16(0b1011).U16(UnitsPerEm);
        head.U32(0).U32(0).U32(0).U32(0);
        head.I16(0).I16(-200).I16(700).I16(700);
        head.U16(0).U16(8).I16(2);
        head.I16(0).I16(0);
        return head.ToArray();
    }

    private static byte[] Maxp()
    {
        var maxp = new Be();
        maxp.U32(0x00005000).U16(GlyphCount);                        // version 0.5: the CFF flavour
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

    private static byte[] Cmap() => Sfnt.Format4Cmap(
    [
        ('C', CurveGlyph), ('I', RectGlyph), ('O', RingGlyph),
        ('S', SubrGlyph), ('V', VsIndexGlyph),
    ]);

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
