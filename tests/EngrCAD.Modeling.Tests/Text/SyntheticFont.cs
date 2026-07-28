using System.Text;

namespace EngrCAD.Modeling.Tests.Text;

/// <summary>Which optional pieces the synthetic font is built with.</summary>
internal sealed record SyntheticFontOptions
{
    /// <summary>Include <c>kern</c>, <c>name</c> and <c>OS/2</c> (all optional).</summary>
    public bool OptionalTables { get; init; } = true;

    /// <summary>Write <c>loca</c> in the 32-bit (long) format instead of the 16-bit one.</summary>
    public bool LongLoca { get; init; }

    /// <summary>Omit this table entirely (to test required-table diagnostics).</summary>
    public string? OmitTable { get; init; }
}

/// <summary>
/// A complete TrueType font assembled byte by byte in memory — no font file is
/// committed to the repository (licensing), and hand-writing the tables doubles as
/// executable documentation of the format we parse.
/// <para>The five glyphs are chosen to exercise every branch of the reader and of the
/// outline→<c>Sketch</c> conversion:</para>
/// <list type="bullet">
/// <item><description><c>space</c> — a zero-length <c>loca</c> entry (blank glyph).</description></item>
/// <item><description><c>'I'</c> — one all-on-curve rectangle, wound <b>counter-clockwise</b>
/// (deliberately against TrueType's outer-contours-are-clockwise convention, so the
/// hole classifier cannot be relying on orientation).</description></item>
/// <item><description><c>'O'</c> — a clockwise outer square with a counter-clockwise inner
/// square: one counter (hole).</description></item>
/// <item><description><c>'C'</c> — two consecutive off-curve points, which imply an
/// on-curve point at their midpoint: the classic TrueType subtlety.</description></item>
/// <item><description><c>'A'</c> — a composite glyph placing <c>'I'</c> twice, once
/// translated and once translated <i>and</i> scaled (F2Dot14).</description></item>
/// </list>
/// Coordinates are written with the format's real compression (short vectors, the
/// same-as-previous bit, and REPEAT flag runs), so the reader's decompression is under
/// test rather than a simplified encoding.
/// </summary>
internal static class SyntheticFont
{
    public const int UnitsPerEm = 1000;
    public const int Ascender = 800;
    public const int Descender = -200;
    public const int LineGap = 100;
    public const int CapHeight = 700;
    public const string FamilyName = "EngrCAD Synthetic";

    public const int NotdefGlyph = 0;
    public const int SpaceGlyph = 1;
    public const int RectGlyph = 2;
    public const int RingGlyph = 3;
    public const int CurveGlyph = 4;
    public const int CompositeGlyph = 5;
    public const int GlyphCount = 6;

    /// <summary>Advance widths in font units, per glyph index.</summary>
    public static readonly int[] Advances = [600, 500, 400, 800, 500, 900];

    /// <summary>Left side bearings in font units, per glyph index.</summary>
    public static readonly int[] Bearings = [0, 0, 100, 0, 0, 100];

    /// <summary>Kerning pair written into the <c>kern</c> table: 'I' followed by 'O'.</summary>
    public static readonly (int Left, int Right, int Value) KernPair = (RectGlyph, RingGlyph, -50);

    /// <summary>One outline point as the font stores it.</summary>
    public readonly record struct Pt(int X, int Y, bool On);

    // 'I': 200 x 700 rectangle at x = 100, wound CCW (against the TrueType convention).
    public static readonly Pt[][] RectContours =
    [
        [new(100, 0, true), new(300, 0, true), new(300, 700, true), new(100, 700, true)],
    ];

    // 'O': 700 x 700 outer square (CW) with a 300 x 300 counter (CCW) — one hole.
    public static readonly Pt[][] RingContours =
    [
        [new(0, 0, true), new(0, 700, true), new(700, 700, true), new(700, 0, true)],
        [new(200, 200, true), new(500, 200, true), new(500, 500, true), new(200, 500, true)],
    ];

    // 'C': on, off, off, on. The two consecutive off-curve points imply an on-curve
    // point at (400, 200); the contour closes with a straight line back to (0, 0).
    public static readonly Pt[][] CurveContours =
    [
        [new(0, 0, true), new(400, 0, false), new(400, 400, false), new(0, 400, true)],
    ];

    /// <summary>Exact area of the <c>'C'</c> contour in font units squared: the chord
    /// polygon (80000) plus 2/3 of each control triangle (40000 each) — quadratic
    /// Béziers enclose exactly two thirds of their control triangle over the chord.</summary>
    public const double CurveGlyphArea = 400000.0 / 3.0;

    /// <summary>Second component placement of the composite <c>'A'</c>: half scale,
    /// offset (500, 100).</summary>
    public const double CompositeScale = 0.5;
    public const int CompositeOffsetX = 500;
    public const int CompositeOffsetY = 100;

    public static byte[] Build(SyntheticFontOptions? options = null)
    {
        options ??= new SyntheticFontOptions();

        var glyphs = new byte[GlyphCount][];
        glyphs[NotdefGlyph] = [];                                    // blank: zero-length loca entry
        glyphs[SpaceGlyph] = [];
        glyphs[RectGlyph] = SimpleGlyph(RectContours);
        glyphs[RingGlyph] = SimpleGlyph(RingContours);
        glyphs[CurveGlyph] = SimpleGlyph(CurveContours);
        glyphs[CompositeGlyph] = CompositeGlyphBytes();

        var glyf = new Be();
        var offsets = new List<int>(GlyphCount + 1);
        foreach (var glyph in glyphs)
        {
            offsets.Add(glyf.Count);
            glyf.Bytes(glyph);
            glyf.PadTo(4);                                           // short loca stores offset/2, so keep them even
        }
        offsets.Add(glyf.Count);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["head"] = Head(options.LongLoca),
            ["maxp"] = Maxp(),
            ["hhea"] = Hhea(),
            ["hmtx"] = Hmtx(),
            ["loca"] = Loca(offsets, options.LongLoca),
            ["glyf"] = glyf.ToArray(),
            ["cmap"] = Cmap(),
        };
        if (options.OptionalTables)
        {
            tables["kern"] = Kern();
            tables["name"] = Name();
            tables["OS/2"] = Os2();
        }
        if (options.OmitTable is not null)
            tables.Remove(options.OmitTable);

        return Assemble(tables);
    }

    // ---- sfnt container ------------------------------------------------------

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        int count = tables.Count;
        var file = new Be();
        file.U32(0x00010000);                                        // sfnt version: TrueType outlines
        file.U16(count);
        int entrySelector = (int)Math.Floor(Math.Log2(Math.Max(1, count)));
        int searchRange = 16 * (1 << entrySelector);
        file.U16(searchRange).U16(entrySelector).U16(count * 16 - searchRange);

        int offset = 12 + count * 16;
        foreach (var (tag, bytes) in tables)
        {
            file.Tag(tag).U32(0).U32(offset).U32(bytes.Length);      // checksum 0: the reader skips it
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

    // ---- tables --------------------------------------------------------------

    private static byte[] Head(bool longLoca)
    {
        var head = new Be();
        head.U32(0x00010000).U32(0x00010000);                        // version, fontRevision
        head.U32(0).U32(0x5F0F3CF5);                                 // checkSumAdjustment, magicNumber
        head.U16(0b1011).U16(UnitsPerEm);                            // flags, unitsPerEm  (offsets 16, 18)
        head.U32(0).U32(0).U32(0).U32(0);                            // created, modified (offsets 20..35)
        head.I16(0).I16(-200).I16(900).I16(800);                     // xMin, yMin, xMax, yMax
        head.U16(0).U16(8).I16(2);                                   // macStyle, lowestRecPPEM, fontDirectionHint
        head.I16(longLoca ? 1 : 0).I16(0);                           // indexToLocFormat (offset 50), glyphDataFormat
        return head.ToArray();                                       // 54 bytes
    }

    private static byte[] Maxp()
    {
        var maxp = new Be();
        maxp.U32(0x00010000).U16(GlyphCount);
        for (int i = 0; i < 13; i++)                                 // maxPoints .. maxComponentDepth
            maxp.U16(i == 12 ? 2 : 16);
        return maxp.ToArray();                                       // 32 bytes
    }

    private static byte[] Hhea()
    {
        var hhea = new Be();
        hhea.U32(0x00010000);
        hhea.I16(Ascender).I16(Descender).I16(LineGap);
        hhea.U16(900).I16(0).I16(900).I16(800);                      // advanceWidthMax, minLSB, minRSB, xMaxExtent
        hhea.I16(1).I16(0).I16(0);                                   // caretSlopeRise/Run/Offset
        hhea.I16(0).I16(0).I16(0).I16(0);                            // reserved
        hhea.I16(0).U16(GlyphCount);                                 // metricDataFormat, numberOfHMetrics (offset 34)
        return hhea.ToArray();                                       // 36 bytes
    }

    private static byte[] Hmtx()
    {
        var hmtx = new Be();
        for (int i = 0; i < GlyphCount; i++)
            hmtx.U16(Advances[i]).I16(Bearings[i]);
        return hmtx.ToArray();
    }

    private static byte[] Loca(List<int> offsets, bool longLoca)
    {
        var loca = new Be();
        foreach (int offset in offsets)
        {
            if (longLoca)
                loca.U32(offset);
            else
                loca.U16(offset / 2);
        }
        return loca.ToArray();
    }

    private static byte[] Cmap()
    {
        // Format 4 with one single-code segment per character plus the mandatory
        // 0xFFFF terminator; idRangeOffset stays 0 so idDelta carries the mapping.
        (int Code, int Glyph)[] mappings =
        [
            (' ', SpaceGlyph), ('A', CompositeGlyph), ('C', CurveGlyph),
            ('I', RectGlyph), ('O', RingGlyph),
        ];
        int segments = mappings.Length + 1;

        var subtable = new Be();
        subtable.U16(4).U16(16 + segments * 8).U16(0);               // format, length, language
        int entrySelector = (int)Math.Floor(Math.Log2(segments));
        int searchRange = 2 * (1 << entrySelector);
        subtable.U16(segments * 2).U16(searchRange).U16(entrySelector).U16(segments * 2 - searchRange);
        foreach (var (code, _) in mappings)
            subtable.U16(code);
        subtable.U16(0xFFFF);                                        // endCode terminator
        subtable.U16(0);                                             // reservedPad
        foreach (var (code, _) in mappings)
            subtable.U16(code);
        subtable.U16(0xFFFF);                                        // startCode terminator
        foreach (var (code, glyph) in mappings)
            subtable.I16(glyph - code);                              // idDelta
        subtable.I16(1);                                             // terminator: 0xFFFF + 1 -> glyph 0
        for (int i = 0; i < segments; i++)
            subtable.U16(0);                                         // idRangeOffset
        var subtableBytes = subtable.ToArray();

        var cmap = new Be();
        cmap.U16(0).U16(1);                                          // version, numTables
        cmap.U16(3).U16(1).U32(12);                                  // Windows, BMP, subtable at offset 12
        cmap.Bytes(subtableBytes);
        return cmap.ToArray();
    }

    private static byte[] Kern()
    {
        var pairs = new Be();
        pairs.U16(1).U16(6).U16(0).U16(0);                           // nPairs, searchRange, entrySelector, rangeShift
        pairs.U16(KernPair.Left).U16(KernPair.Right).I16(KernPair.Value);
        var pairBytes = pairs.ToArray();

        var kern = new Be();
        kern.U16(0).U16(1);                                          // version 0 (Microsoft), one subtable
        kern.U16(0).U16(6 + pairBytes.Length).U16(0x0001);           // version, length, coverage: format 0, horizontal
        kern.Bytes(pairBytes);
        return kern.ToArray();
    }

    private static byte[] Name()
    {
        var value = Encoding.BigEndianUnicode.GetBytes(FamilyName);
        var name = new Be();
        name.U16(0).U16(1).U16(6 + 12);                              // format, count, stringOffset
        name.U16(3).U16(1).U16(0x0409).U16(1).U16(value.Length).U16(0);
        name.Bytes(value);
        return name.ToArray();
    }

    private static byte[] Os2()
    {
        var os2 = new Be();
        os2.U16(4);                                                  // version 4 (>= 2 means sCapHeight is present)
        while (os2.Count < 88)
            os2.U8(0);
        os2.I16(CapHeight);                                          // sCapHeight at offset 88
        os2.I16(0).U16(0).U16(0).U16(0);                             // sxHeight-adjacent tail (unused by the reader)
        return os2.ToArray();
    }

    // ---- glyf ----------------------------------------------------------------

    private static byte[] SimpleGlyph(Pt[][] contours)
    {
        var points = contours.SelectMany(c => c).ToArray();
        var glyph = new Be();
        glyph.I16(contours.Length);
        glyph.I16(points.Min(p => p.X)).I16(points.Min(p => p.Y))
             .I16(points.Max(p => p.X)).I16(points.Max(p => p.Y));

        int end = -1;
        foreach (var contour in contours)
        {
            end += contour.Length;
            glyph.U16(end);
        }
        glyph.U16(0);                                                // instructionLength: no hinting

        // Flags, with the SHORT / SAME bits chosen exactly as a real compiler would.
        var flags = new byte[points.Length];
        int previousX = 0, previousY = 0;
        for (int i = 0; i < points.Length; i++)
        {
            byte flag = points[i].On ? (byte)0x01 : (byte)0x00;
            flag |= CoordinateFlag(points[i].X - previousX, 0x02, 0x10);
            flag |= CoordinateFlag(points[i].Y - previousY, 0x04, 0x20);
            flags[i] = flag;
            previousX = points[i].X;
            previousY = points[i].Y;
        }

        // Run-length compression: a flag repeated n times becomes flag|REPEAT, n-1.
        for (int i = 0; i < flags.Length;)
        {
            byte flag = flags[i];
            int run = 1;
            while (i + run < flags.Length && flags[i + run] == flag && run < 256)
                run++;
            if (run >= 2)
                glyph.U8(flag | 0x08).U8(run - 1);
            else
                glyph.U8(flag);
            i += run;
        }

        WriteCoordinates(glyph, points.Select(p => p.X), 0x02, 0x10);
        WriteCoordinates(glyph, points.Select(p => p.Y), 0x04, 0x20);
        return glyph.ToArray();

        static byte CoordinateFlag(int delta, byte shortBit, byte sameBit) => delta switch
        {
            0 => sameBit,                                            // "same as previous": no bytes at all
            > 0 and <= 255 => (byte)(shortBit | sameBit),            // one byte, positive
            < 0 and >= -255 => shortBit,                             // one byte, negative
            _ => 0,                                                  // signed 16-bit delta
        };

        static void WriteCoordinates(Be glyph, IEnumerable<int> values, byte shortBit, byte sameBit)
        {
            int previous = 0;
            foreach (int value in values)
            {
                int delta = value - previous;
                byte flag = CoordinateFlag(delta, shortBit, sameBit);
                if ((flag & shortBit) != 0)
                    glyph.U8(Math.Abs(delta));
                else if ((flag & sameBit) == 0)
                    glyph.I16(delta);
                previous = value;
            }
        }
    }

    private static byte[] CompositeGlyphBytes()
    {
        const int argsAreWords = 0x0001, argsAreXy = 0x0002, haveScale = 0x0008, moreComponents = 0x0020;
        var glyph = new Be();
        glyph.I16(-1);                                               // composite
        glyph.I16(100).I16(0).I16(650).I16(700);                     // bbox

        glyph.U16(argsAreWords | argsAreXy | moreComponents).U16(RectGlyph).I16(0).I16(0);
        glyph.U16(argsAreWords | argsAreXy | haveScale).U16(RectGlyph)
             .I16(CompositeOffsetX).I16(CompositeOffsetY)
             .I16((int)Math.Round(CompositeScale * 16384));          // F2Dot14
        return glyph.ToArray();
    }

}

/// <summary>Big-endian byte builder shared by the synthetic font assemblers
/// (<see cref="SyntheticFont"/> and <see cref="SyntheticCffFont"/>).</summary>
internal sealed class Be
{
    private readonly List<byte> _bytes = [];

    public int Count => _bytes.Count;

    public Be U8(int value)
    {
        _bytes.Add((byte)value);
        return this;
    }

    public Be U16(int value)
    {
        _bytes.Add((byte)(value >> 8));
        _bytes.Add((byte)value);
        return this;
    }

    public Be I16(int value) => U16(value & 0xFFFF);

    public Be U32(long value)
    {
        _bytes.Add((byte)(value >> 24));
        _bytes.Add((byte)(value >> 16));
        _bytes.Add((byte)(value >> 8));
        _bytes.Add((byte)value);
        return this;
    }

    public Be Tag(string tag)
    {
        foreach (char c in tag)
            _bytes.Add((byte)c);
        return this;
    }

    public Be Bytes(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
            _bytes.Add(b);
        return this;
    }

    public void PadTo(int alignment)
    {
        while (_bytes.Count % alignment != 0)
            _bytes.Add(0);
    }

    public byte[] ToArray() => [.. _bytes];
}
