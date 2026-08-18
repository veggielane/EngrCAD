using System.Text;

namespace EngrCAD.Modeling.Tests.Text;

/// <summary>Which pieces the synthetic variable font is built with.</summary>
internal sealed record SyntheticVariableFontOptions
{
    /// <summary>Include the <c>avar</c> segment maps. WITHOUT them the test weight
    /// normalizes to 0.5 instead of 0.75, so every expected coordinate moves — which is
    /// the measurement that the map was applied.</summary>
    public bool Avar { get; init; } = true;

    /// <summary>Write a version-2 <c>avar</c> instead: the font loads, and varying it
    /// refuses by name.</summary>
    public bool AvarVersion2 { get; init; }

    /// <summary>Include <c>gvar</c> (the outline and phantom-point deltas).</summary>
    public bool Gvar { get; init; } = true;

    /// <summary>Include <c>HVAR</c>. Its advance deltas deliberately DISAGREE with the
    /// phantom-point ones, so which route wins is observable.</summary>
    public bool Hvar { get; init; }

    /// <summary>Write <c>gvar</c>'s glyph offsets in the 32-bit form.</summary>
    public bool LongGvarOffsets { get; init; }

    /// <summary>Omit <c>fvar</c> — a static font that still carries <c>gvar</c>, which a
    /// reader must ignore rather than apply.</summary>
    public bool OmitFvar { get; init; }

    /// <summary>Mark the width axis hidden (fvar flags bit 0).</summary>
    public bool HiddenWidthAxis { get; init; }
}

/// <summary>
/// A complete TrueType VARIABLE font assembled byte by byte — <c>fvar</c>, <c>avar</c>,
/// <c>gvar</c> and <c>HVAR</c> over the <c>glyf</c> outlines of
/// <see cref="SyntheticFont"/>'s sibling. Every glyph is chosen to pin one decoding rule
/// with an exactly hand-computable answer:
/// <list type="bullet">
/// <item><description><c>space</c> — a blank glyph whose only tuple moves the ADVANCE
/// phantom point: a glyph that draws nothing can still lay out differently.</description></item>
/// <item><description><c>'I'</c> — a rectangle varied through a SHARED tuple over ALL
/// points, with a peak of exactly zero on the width axis: an axis whose peak is zero
/// contributes a factor of ONE, so this glyph must not move when only the width
/// changes.</description></item>
/// <item><description><c>'B'</c> — six points of which TWO are touched: the IUP fixture.
/// Its inferred deltas (20 and 40 over a 0..60 ramp) differ from both the "no delta" and
/// the "nearest neighbour's delta" answers, and one gap exercises the outside-the-range
/// clause where a point is TRANSLATED rather than extrapolated.</description></item>
/// <item><description><c>'C'</c> — two tuples over SHARED point numbers, one with a peak
/// on both axes (a genuine PRODUCT scalar, 0.375) and one INTERMEDIATE (start/peak/end,
/// evaluated on its falling flank).</description></item>
/// <item><description><c>'A'</c> — a composite whose tuple moves the SECOND component's
/// offset and the advance phantom, leaving the first component alone: a composite's
/// points are its component placements, each its own one-point contour.</description></item>
/// </list>
/// </summary>
internal static class SyntheticVariableFont
{
    public const int UnitsPerEm = 1000;
    public const int Ascender = 800;
    public const int Descender = -200;
    public const int LineGap = 100;
    public const int CapHeight = 700;
    public const string FamilyName = "EngrCAD Variable";

    public const int NotdefGlyph = 0;
    public const int SpaceGlyph = 1;
    public const int RectGlyph = 2;
    public const int IupGlyph = 3;
    public const int ProductGlyph = 4;
    public const int CompositeGlyph = 5;
    public const int GlyphCount = 6;

    /// <summary>Advance widths in font units, per glyph index (the default instance's).</summary>
    public static readonly int[] Advances = [600, 500, 400, 800, 500, 900];

    /// <summary>Left side bearings in font units, per glyph index.</summary>
    public static readonly int[] Bearings = [0, 0, 100, 0, 0, 100];

    /// <summary>One outline point as the font stores it.</summary>
    public readonly record struct Pt(int X, int Y, bool On);

    // ---- the default-instance outlines ---------------------------------------

    public static readonly Pt[][] RectContours =
    [
        [new(100, 0, true), new(300, 0, true), new(300, 700, true), new(100, 700, true)],
    ];

    public static readonly Pt[][] IupContours =
    [
        [
            new(0, 0, true), new(100, 0, true), new(200, 0, true),
            new(300, 0, true), new(300, 500, true), new(0, 500, true),
        ],
    ];

    public static readonly Pt[][] ProductContours =
    [
        [new(0, 0, true), new(200, 0, true), new(200, 400, true), new(0, 400, true)],
    ];

    /// <summary>Second component placement of the composite: the same rectangle again at
    /// this offset (the first sits at the origin).</summary>
    public const int CompositeOffsetX = 500;
    public const int CompositeOffsetY = 100;

    // ---- the stored deltas ---------------------------------------------------

    /// <summary>'I': the rectangle's two sides move apart by 50 each and the advance
    /// grows by 100, at the weight axis's own extreme.</summary>
    public static readonly int[] RectDeltaX = [-50, 50, 50, -50];
    public const int RectPhantomAdvanceDelta = 100;

    /// <summary>'B': only points 0 and 3 are touched — the rest are inferred.</summary>
    public static readonly int[] IupTouchedPoints = [0, 3];
    public static readonly int[] IupTouchedDeltaX = [0, 60];
    public static readonly int[] IupTouchedDeltaY = [10, 10];

    /// <summary>The deltas IUP must infer for all six points, at the tuple's own peak.
    /// Points 1 and 2 interpolate along x (20 and 40 over the 0..60 ramp); point 4 sits
    /// at the far anchor's own coordinate and is TRANSLATED by it; point 5 sits at the
    /// near anchor's. In y the two anchors share a coordinate AND a delta, so the whole
    /// contour translates by 10.</summary>
    public static readonly int[] IupExpectedDeltaX = [0, 20, 40, 60, 60, 0];
    public static readonly int[] IupExpectedDeltaY = [10, 10, 10, 10, 10, 10];

    /// <summary>'C': two tuples over the same four points.</summary>
    public static readonly int[] ProductDeltaX = [0, 100, 100, 0];
    public static readonly int[] IntermediateDeltaX = [0, 200, 200, 0];

    /// <summary>'A': the second component's offset and the advance phantom.</summary>
    public const int CompositeComponentDeltaX = 30;
    public const int CompositeComponentDeltaY = 40;
    public const int CompositePhantomAdvanceDelta = 150;

    /// <summary>'space': a blank glyph whose advance still varies.</summary>
    public const int SpacePhantomAdvanceDelta = 200;

    /// <summary>What <c>HVAR</c> says instead, for every glyph from
    /// <see cref="HvarMappedFrom"/> on — and it says NOTHING for the glyphs before it,
    /// which is what makes the precedence observable on <c>space</c>.</summary>
    public const int HvarAdvanceDelta = 300;
    public const int HvarMappedFrom = RectGlyph;

    public static byte[] Build(SyntheticVariableFontOptions? options = null)
    {
        options ??= new SyntheticVariableFontOptions();

        var glyphs = new byte[GlyphCount][];
        glyphs[NotdefGlyph] = [];
        glyphs[SpaceGlyph] = [];
        glyphs[RectGlyph] = SimpleGlyph(RectContours);
        glyphs[IupGlyph] = SimpleGlyph(IupContours);
        glyphs[ProductGlyph] = SimpleGlyph(ProductContours);
        glyphs[CompositeGlyph] = CompositeGlyphBytes();

        var glyf = new Be();
        var offsets = new List<int>(GlyphCount + 1);
        foreach (var glyph in glyphs)
        {
            offsets.Add(glyf.Count);
            glyf.Bytes(glyph);
            glyf.PadTo(4);
        }
        offsets.Add(glyf.Count);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["head"] = Head(),
            ["maxp"] = Maxp(),
            ["hhea"] = Hhea(),
            ["hmtx"] = Hmtx(),
            ["loca"] = Loca(offsets),
            ["glyf"] = glyf.ToArray(),
            ["cmap"] = Cmap(),
            ["name"] = Name(),
            ["OS/2"] = Os2(),
        };
        if (!options.OmitFvar)
            tables["fvar"] = SyntheticVariations.Fvar(options.HiddenWidthAxis);
        if (options.AvarVersion2)
            tables["avar"] = SyntheticVariations.AvarVersion2();
        else if (options.Avar)
            tables["avar"] = SyntheticVariations.Avar();
        if (options.Gvar)
            tables["gvar"] = Gvar(options.LongGvarOffsets);
        if (options.Hvar)
            tables["HVAR"] = SyntheticVariations.Hvar(HvarAdvanceDelta, HvarMappedFrom);

        return Sfnt.Assemble(tables, 0x00010000);
    }

    // ---- gvar ----------------------------------------------------------------

    /// <summary>One tuple variation of one glyph.</summary>
    private sealed record VarTuple(
        double[]? EmbeddedPeak,
        int SharedTupleIndex,
        (double[] Start, double[] End)? Intermediate,
        int[]? PrivatePoints,
        int[] DeltaX,
        int[] DeltaY);

    /// <summary>The shared tuple list: one entry, the weight axis at its extreme —
    /// which <c>'I'</c> names by index instead of embedding.</summary>
    private static readonly double[][] SharedTuples = [[1, 0]];

    private static byte[] Gvar(bool longOffsets)
    {
        var data = new byte[GlyphCount][];
        data[NotdefGlyph] = [];
        data[SpaceGlyph] = GlyphVariationData(
            [new VarTuple([1, 0], 0, null, [1], [SpacePhantomAdvanceDelta], [0])], sharedPoints: null);

        // 'I': the shared tuple, ALL points (four outline points plus four phantoms).
        int[] rectX = [.. RectDeltaX, 0, RectPhantomAdvanceDelta, 0, 0];
        int[] rectY = new int[rectX.Length];
        data[RectGlyph] = GlyphVariationData(
            [new VarTuple(null, 0, null, null, rectX, rectY)], sharedPoints: null);

        data[IupGlyph] = GlyphVariationData(
            [new VarTuple([0, 1], 0, null, IupTouchedPoints, IupTouchedDeltaX, IupTouchedDeltaY)],
            sharedPoints: null);

        // 'C': two tuples over one SHARED point-number list.
        data[ProductGlyph] = GlyphVariationData(
        [
            new VarTuple([1, 1], 0, null, null, ProductDeltaX, new int[4]),
            new VarTuple([0.5, 0], 0, ([0, 0], [1, 0]), null, IntermediateDeltaX, new int[4]),
        ], sharedPoints: [0, 1, 2, 3]);

        // 'A': component 1 (index 1) and the advance phantom (index 2 + 1).
        data[CompositeGlyph] = GlyphVariationData(
        [
            new VarTuple([1, 0], 0, null, [1, 3],
                [CompositeComponentDeltaX, CompositePhantomAdvanceDelta],
                [CompositeComponentDeltaY, 0]),
        ], sharedPoints: null);

        var array = new Be();
        var dataOffsets = new List<int>(GlyphCount + 1);
        foreach (var glyph in data)
        {
            dataOffsets.Add(array.Count);
            array.Bytes(glyph);
            array.PadTo(2);                                          // short offsets store byte/2
        }
        dataOffsets.Add(array.Count);

        int headerSize = 20 + (GlyphCount + 1) * (longOffsets ? 4 : 2);
        int sharedTuplesOffset = headerSize;
        int dataArrayOffset = sharedTuplesOffset + SharedTuples.Length * 2 * 2;

        var gvar = new Be();
        gvar.U16(1).U16(0);                                          // version 1.0
        gvar.U16(2);                                                 // axisCount
        gvar.U16(SharedTuples.Length).U32(sharedTuplesOffset);
        gvar.U16(GlyphCount).U16(longOffsets ? 1 : 0).U32(dataArrayOffset);
        foreach (int offset in dataOffsets)
        {
            if (longOffsets)
                gvar.U32(offset);
            else
                gvar.U16(offset / 2);
        }
        foreach (var tuple in SharedTuples)
        {
            foreach (double value in tuple)
                gvar.I16(SyntheticVariations.F2Dot14(value));
        }
        gvar.Bytes(array.ToArray());
        return gvar.ToArray();
    }

    private static byte[] GlyphVariationData(VarTuple[] tuples, int[]? sharedPoints)
    {
        // Each tuple's serialized block: its private point numbers (when it has them)
        // followed by the packed x then y deltas.
        var blocks = new byte[tuples.Length][];
        for (int t = 0; t < tuples.Length; t++)
        {
            var block = new Be();
            if (tuples[t].PrivatePoints is { } points)
                SyntheticVariations.WritePointNumbers(block, points);
            SyntheticVariations.WriteDeltas(block, tuples[t].DeltaX);
            SyntheticVariations.WriteDeltas(block, tuples[t].DeltaY);
            blocks[t] = block.ToArray();
        }

        var headers = new Be();
        foreach (var (tuple, block) in tuples.Zip(blocks))
        {
            int index = tuple.EmbeddedPeak is not null ? 0x8000 : tuple.SharedTupleIndex;
            if (tuple.Intermediate is not null)
                index |= 0x4000;
            if (tuple.PrivatePoints is not null)
                index |= 0x2000;
            headers.U16(block.Length).U16(index);
            if (tuple.EmbeddedPeak is { } peak)
            {
                foreach (double value in peak)
                    headers.I16(SyntheticVariations.F2Dot14(value));
            }
            if (tuple.Intermediate is { } intermediate)
            {
                foreach (double value in intermediate.Start)
                    headers.I16(SyntheticVariations.F2Dot14(value));
                foreach (double value in intermediate.End)
                    headers.I16(SyntheticVariations.F2Dot14(value));
            }
        }
        var headerBytes = headers.ToArray();

        var shared = new Be();
        if (sharedPoints is not null)
            SyntheticVariations.WritePointNumbers(shared, sharedPoints);
        var sharedBytes = shared.ToArray();

        var glyph = new Be();
        int count = tuples.Length | (sharedPoints is not null ? 0x8000 : 0);
        glyph.U16(count).U16(4 + headerBytes.Length);                // tupleVariationCount, dataOffset
        glyph.Bytes(headerBytes);
        glyph.Bytes(sharedBytes);
        foreach (var block in blocks)
            glyph.Bytes(block);
        return glyph.ToArray();
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
        glyph.U16(0);                                                // no hinting

        foreach (var point in points)
            glyph.U8(point.On ? 0x01 : 0x00);                        // plain flags: no repeat runs
        WriteCoordinates(glyph, points.Select(p => p.X));
        WriteCoordinates(glyph, points.Select(p => p.Y));
        return glyph.ToArray();

        static void WriteCoordinates(Be glyph, IEnumerable<int> values)
        {
            int previous = 0;
            foreach (int value in values)
            {
                glyph.I16(value - previous);                         // signed 16-bit deltas throughout
                previous = value;
            }
        }
    }

    private static byte[] CompositeGlyphBytes()
    {
        const int argsAreWords = 0x0001, argsAreXy = 0x0002, moreComponents = 0x0020;
        var glyph = new Be();
        glyph.I16(-1);
        glyph.I16(0).I16(0).I16(800).I16(800);
        glyph.U16(argsAreWords | argsAreXy | moreComponents).U16(RectGlyph).I16(0).I16(0);
        glyph.U16(argsAreWords | argsAreXy).U16(RectGlyph).I16(CompositeOffsetX).I16(CompositeOffsetY);
        return glyph.ToArray();
    }

    // ---- the ordinary tables --------------------------------------------------

    private static byte[] Head()
    {
        var head = new Be();
        head.U32(0x00010000).U32(0x00010000);
        head.U32(0).U32(0x5F0F3CF5);
        head.U16(0b1011).U16(UnitsPerEm);
        head.U32(0).U32(0).U32(0).U32(0);
        head.I16(0).I16(-200).I16(900).I16(800);
        head.U16(0).U16(8).I16(2);
        head.I16(0).I16(0);                                          // short loca
        return head.ToArray();
    }

    private static byte[] Maxp()
    {
        var maxp = new Be();
        maxp.U32(0x00010000).U16(GlyphCount);
        for (int i = 0; i < 13; i++)
            maxp.U16(i == 12 ? 2 : 16);
        return maxp.ToArray();
    }

    private static byte[] Hhea()
    {
        var hhea = new Be();
        hhea.U32(0x00010000);
        hhea.I16(Ascender).I16(Descender).I16(LineGap);
        hhea.U16(900).I16(0).I16(900).I16(800);
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

    private static byte[] Loca(List<int> offsets)
    {
        var loca = new Be();
        foreach (int offset in offsets)
            loca.U16(offset / 2);
        return loca.ToArray();
    }

    private static byte[] Cmap()
    {
        (int Code, int Glyph)[] mappings =
        [
            (' ', SpaceGlyph), ('A', CompositeGlyph), ('B', IupGlyph),
            ('C', ProductGlyph), ('I', RectGlyph),
        ];
        return Sfnt.Format4Cmap(mappings);
    }

    private static byte[] Name()
    {
        (int Id, string Value)[] names =
        [
            (1, FamilyName),
            (SyntheticVariations.WeightNameId, SyntheticVariations.WeightName),
            (SyntheticVariations.WidthNameId, SyntheticVariations.WidthName),
            (SyntheticVariations.SemiboldNameId, SyntheticVariations.SemiboldInstance),
            (SyntheticVariations.CondensedNameId, SyntheticVariations.CondensedInstance),
            (SyntheticVariations.SemiboldPostScriptNameId, SyntheticVariations.SemiboldPostScriptName),
        ];
        return Sfnt.NameTable(names);
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

/// <summary>The sfnt container pieces the variable-font fixtures share.</summary>
internal static class Sfnt
{
    public static byte[] Assemble(SortedDictionary<string, byte[]> tables, long version)
    {
        int count = tables.Count;
        var file = new Be();
        file.U32(version);
        file.U16(count);
        int entrySelector = (int)Math.Floor(Math.Log2(Math.Max(1, count)));
        int searchRange = 16 * (1 << entrySelector);
        file.U16(searchRange).U16(entrySelector).U16(count * 16 - searchRange);

        int offset = 12 + count * 16;
        foreach (var (tag, bytes) in tables)
        {
            file.Tag(tag).U32(0).U32(offset).U32(bytes.Length);
            offset += (bytes.Length + 3) / 4 * 4;
        }
        foreach (var bytes in tables.Values)
        {
            file.Bytes(bytes);
            file.PadTo(4);
        }
        return file.ToArray();
    }

    /// <summary>A format-4 cmap with one single-code segment per mapping.</summary>
    public static byte[] Format4Cmap((int Code, int Glyph)[] mappings)
    {
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

        var cmap = new Be();
        cmap.U16(0).U16(1);
        cmap.U16(3).U16(1).U32(12);
        cmap.Bytes(subtable.ToArray());
        return cmap.ToArray();
    }

    /// <summary>A <c>name</c> table carrying several ids as Windows/BMP/en-US
    /// strings.</summary>
    public static byte[] NameTable((int Id, string Value)[] names)
    {
        var strings = new Be();
        var records = new List<(int Id, int Offset, int Length)>();
        foreach (var (id, value) in names)
        {
            var bytes = Encoding.BigEndianUnicode.GetBytes(value);
            records.Add((id, strings.Count, bytes.Length));
            strings.Bytes(bytes);
        }

        var name = new Be();
        name.U16(0).U16(names.Length).U16(6 + names.Length * 12);
        foreach (var (id, offset, length) in records)
            name.U16(3).U16(1).U16(0x0409).U16(id).U16(length).U16(offset);
        name.Bytes(strings.ToArray());
        return name.ToArray();
    }
}
