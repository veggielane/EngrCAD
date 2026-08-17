namespace EngrCAD.Modeling.Tests.Text;

/// <summary>
/// The variation tables both synthetic variable fonts share — <c>fvar</c> (the design
/// space), <c>avar</c> (the axis warp) and <c>HVAR</c> (advance-width deltas over an
/// item variation store) — assembled byte by byte like every other synthetic font here.
/// <para><b>Every coordinate in the design is an exact binary fraction</b>, which is the
/// point: <c>fvar</c> stores 16.16 and <c>avar</c>/<c>gvar</c> store F2Dot14, so a test
/// coordinate that is not a multiple of 1/16384 comes back a few ulps off and every
/// hand-computed delta becomes an approximate comparison. The test weight normalizes to
/// exactly 0.5, the <c>avar</c> map takes 0.5 to exactly 0.75, and the test width
/// normalizes to exactly 0.5 — so every expected coordinate below is exact.</para>
/// </summary>
internal static class SyntheticVariations
{
    public const string WeightTag = "wght";
    public const string WidthTag = "wdth";

    public const int WeightMinimum = 100, WeightDefault = 400, WeightMaximum = 900;
    public const int WidthMinimum = 50, WidthDefault = 100, WidthMaximum = 200;

    /// <summary>The weight the tests instance at: (650 − 400) / (900 − 400) = 0.5
    /// exactly.</summary>
    public const int TestWeight = 650;

    /// <summary>The width the tests instance at: (150 − 100) / (200 − 100) = 0.5
    /// exactly.</summary>
    public const int TestWidth = 150;

    /// <summary>What <see cref="TestWeight"/> normalizes to BEFORE <c>avar</c>.</summary>
    public const double TestWeightUnmapped = 0.5;

    /// <summary>What it normalizes to AFTER <c>avar</c> — the map's own key, so the
    /// difference between the two is what proves the segment map ran.</summary>
    public const double TestWeightNormalized = 0.75;

    public const double TestWidthNormalized = 0.5;

    /// <summary>Name ids the synthetic fonts write their axis and instance names
    /// under.</summary>
    public const int WeightNameId = 256, WidthNameId = 257;
    public const int SemiboldNameId = 258, CondensedNameId = 259, SemiboldPostScriptNameId = 260;

    public const string WeightName = "Weight";
    public const string WidthName = "Width";

    /// <summary>A named instance at exactly <see cref="TestWeight"/>, so
    /// <c>WithNamedInstance</c> and <c>WithVariation</c> can be compared as an
    /// EQUALITY rather than as an approximation.</summary>
    public const string SemiboldInstance = "Semibold";
    public const string CondensedInstance = "Condensed";
    public const string SemiboldPostScriptName = "EngrCADVariable-Semibold";

    /// <summary><c>fvar</c>: two axes and two named instances.</summary>
    public static byte[] Fvar(bool hiddenWidthAxis = false)
    {
        var fvar = new Be();
        fvar.U16(1).U16(0);                                          // version 1.0
        fvar.U16(16).U16(2);                                         // axesArrayOffset, reserved
        fvar.U16(2).U16(20);                                         // axisCount, axisSize
        fvar.U16(2).U16(2 * 4 + 6);                                  // instanceCount, instanceSize (+ postScriptNameID)

        Axis(fvar, WeightTag, WeightMinimum, WeightDefault, WeightMaximum, flags: 0, WeightNameId);
        Axis(fvar, WidthTag, WidthMinimum, WidthDefault, WidthMaximum, hiddenWidthAxis ? 1 : 0, WidthNameId);

        Instance(fvar, SemiboldNameId, TestWeight, WidthDefault, SemiboldPostScriptNameId);
        Instance(fvar, CondensedNameId, WeightDefault, WidthMinimum, 0);
        return fvar.ToArray();

        static void Axis(Be fvar, string tag, int minimum, int @default, int maximum, int flags, int nameId)
        {
            fvar.Tag(tag).U32(Fixed(minimum)).U32(Fixed(@default)).U32(Fixed(maximum));
            fvar.U16(flags).U16(nameId);
        }

        static void Instance(Be fvar, int nameId, int weight, int width, int postScriptNameId)
        {
            fvar.U16(nameId).U16(0);                                 // subfamilyNameID, flags
            fvar.U32(Fixed(weight)).U32(Fixed(width));
            fvar.U16(postScriptNameId);
        }
    }

    /// <summary>
    /// <c>avar</c>: the weight axis is warped so that 0.5 becomes 0.75 while the width
    /// axis carries the identity — so a font read WITHOUT this table lands its weight
    /// deltas at 0.5 and the difference is the measurement that the map was applied.
    /// </summary>
    public static byte[] Avar()
    {
        var avar = new Be();
        avar.U16(1).U16(0).U16(0);                                   // version 1.0, reserved
        avar.U16(2);                                                 // one segment map per axis

        avar.U16(4);                                                 // weight: four positions
        F2Dot14Pair(avar, -1, -1);
        F2Dot14Pair(avar, 0, 0);
        F2Dot14Pair(avar, TestWeightUnmapped, TestWeightNormalized);
        F2Dot14Pair(avar, 1, 1);

        avar.U16(3);                                                 // width: the identity, spelled out
        F2Dot14Pair(avar, -1, -1);
        F2Dot14Pair(avar, 0, 0);
        F2Dot14Pair(avar, 1, 1);
        return avar.ToArray();

        static void F2Dot14Pair(Be avar, double from, double to) => avar.I16(F2Dot14(from)).I16(F2Dot14(to));
    }

    /// <summary>A version-2 <c>avar</c> header, whose item-variation-store axis mapping
    /// this reader does not honour — the font still LOADS (every axis is at its default,
    /// where the map is the identity by the format's own required entry) and varying it
    /// refuses by name.</summary>
    public static byte[] AvarVersion2()
    {
        var avar = new Be();
        avar.U16(2).U16(0).U16(0);
        avar.U16(2);
        avar.U16(3);
        avar.I16(F2Dot14(-1)).I16(F2Dot14(-1));
        avar.I16(0).I16(0);
        avar.I16(F2Dot14(1)).I16(F2Dot14(1));
        avar.U16(3);
        avar.I16(F2Dot14(-1)).I16(F2Dot14(-1));
        avar.I16(0).I16(0);
        avar.I16(F2Dot14(1)).I16(F2Dot14(1));
        return avar.ToArray();
    }

    /// <summary>
    /// <c>HVAR</c> over an item variation store with ONE region (the weight axis) and
    /// two delta rows, plus a <c>DeltaSetIndexMap</c> that names row 0 for the first two
    /// glyphs and row 1 for the rest — the map is deliberately SHORTER than the glyph
    /// count so the format's "an id past the end takes the last entry" rule is under
    /// test.
    /// </summary>
    /// <param name="advanceDelta">The stored delta of row 1, font units at the weight
    /// axis's own extreme.</param>
    /// <param name="mappedFrom">First glyph the map sends to row 1.</param>
    public static byte[] Hvar(int advanceDelta, int mappedFrom)
    {
        // DeltaSetIndexMap: one byte per entry, one inner-index bit.
        var map = new Be();
        map.U8(0).U8(0x00);                                          // format 0, entryFormat: 1 inner bit, 1-byte entries
        map.U16(mappedFrom + 1);                                     // mapCount: ids past it take the last entry
        for (int glyph = 0; glyph <= mappedFrom; glyph++)
            map.U8(glyph < mappedFrom ? 0 : 1);                      // outer 0, inner 0 or 1
        var mapBytes = map.ToArray();

        var store = ItemVariationStore(
            regions: [[(0, 1, 1), (0, 0, 0)]],                       // one region: the weight axis
            rows: [[0], [advanceDelta]]);

        var hvar = new Be();
        hvar.U16(1).U16(0);                                          // version 1.0
        const int headerSize = 4 + 4 * 4;
        hvar.U32(headerSize);                                        // itemVariationStoreOffset
        hvar.U32(headerSize + store.Length);                         // advanceWidthMappingOffset
        hvar.U32(0).U32(0);                                          // lsbMapping, rsbMapping: absent
        hvar.Bytes(store);
        hvar.Bytes(mapBytes);
        return hvar.ToArray();
    }

    /// <summary>
    /// An OpenType Item Variation Store: a region list plus one item variation data per
    /// row-set. Deltas are written as 16-bit words throughout (wordDeltaCount = the
    /// region count), which is what a font does when any delta needs the range.
    /// </summary>
    /// <param name="regions">Per region, per axis, a (start, peak, end) triple.</param>
    /// <param name="rows">Per item variation data... here one subtable per element of
    /// the outer array is NOT what is meant: <paramref name="rows"/> is the delta rows of
    /// ONE subtable, each row carrying one delta per region.</param>
    public static byte[] ItemVariationStore((double Start, double Peak, double End)[][] regions, int[][] rows)
        => ItemVariationStore(regions, [rows], [Enumerable.Range(0, regions.Length).ToArray()]);

    /// <summary>The general form: several item variation data subtables, each naming its
    /// own subset of the shared region list — which is what <c>vsindex</c> selects
    /// between in a CFF2 charstring.</summary>
    public static byte[] ItemVariationStore(
        (double Start, double Peak, double End)[][] regions, int[][][] subtables, int[][] regionsPerSubtable)
    {
        var regionList = new Be();
        int axisCount = regions.Length == 0 ? 0 : regions[0].Length;
        regionList.U16(axisCount).U16(regions.Length);
        foreach (var region in regions)
        {
            foreach (var (start, peak, end) in region)
                regionList.I16(F2Dot14(start)).I16(F2Dot14(peak)).I16(F2Dot14(end));
        }
        var regionBytes = regionList.ToArray();

        var dataBytes = new byte[subtables.Length][];
        for (int s = 0; s < subtables.Length; s++)
        {
            var rows = subtables[s];
            var indexes = regionsPerSubtable[s];
            var data = new Be();
            data.U16(rows.Length).U16(indexes.Length).U16(indexes.Length);
            foreach (int index in indexes)
                data.U16(index);
            foreach (var row in rows)
            {
                foreach (int delta in row)
                    data.I16(delta);
            }
            dataBytes[s] = data.ToArray();
        }

        var store = new Be();
        int headerSize = 2 + 4 + 2 + 4 * subtables.Length;
        store.U16(1);                                                // format 1
        store.U32(headerSize + dataBytes.Sum(d => d.Length));        // variationRegionListOffset (after the data)
        store.U16(subtables.Length);
        int at = headerSize;
        foreach (var data in dataBytes)
        {
            store.U32(at);
            at += data.Length;
        }
        foreach (var data in dataBytes)
            store.Bytes(data);
        store.Bytes(regionBytes);
        return store.ToArray();
    }

    /// <summary>16.16 fixed point, as <c>fvar</c> stores axis and instance
    /// coordinates.</summary>
    public static long Fixed(double value) => (long)Math.Round(value * 65536) & 0xFFFFFFFFL;

    /// <summary>F2Dot14, as every normalized coordinate is stored.</summary>
    public static int F2Dot14(double value) => (int)Math.Round(value * 16384) & 0xFFFF;

    // ---- packed gvar encodings ------------------------------------------------

    /// <summary>Packed point numbers: a count then runs of deltas from the previous
    /// number. A count of zero means ALL points, which is spelled by
    /// <c>AllPoints</c>.</summary>
    public static void WritePointNumbers(Be data, int[] numbers)
    {
        if (numbers.Length < 128)
            data.U8(numbers.Length);
        else
            data.U8(0x80 | (numbers.Length >> 8)).U8(numbers.Length & 0xFF);

        int previous = 0;
        for (int i = 0; i < numbers.Length;)
        {
            int run = Math.Min(128, numbers.Length - i);
            data.U8(run - 1);                                        // high bit clear: one-byte deltas
            for (int k = 0; k < run; k++)
            {
                data.U8(numbers[i + k] - previous);
                previous = numbers[i + k];
            }
            i += run;
        }
    }

    /// <summary>Packed deltas: zero runs, byte runs and word runs — all three, chosen
    /// the way a real compiler would, so the reader's three branches are exercised by
    /// ordinary data rather than by a special fixture.</summary>
    public static void WriteDeltas(Be data, int[] deltas)
    {
        for (int i = 0; i < deltas.Length;)
        {
            int run = 1;
            if (deltas[i] == 0)
            {
                while (i + run < deltas.Length && deltas[i + run] == 0 && run < 64)
                    run++;
                data.U8(0x80 | (run - 1));
                i += run;
                continue;
            }
            bool words = deltas[i] is < -128 or > 127;
            while (i + run < deltas.Length && deltas[i + run] != 0 && run < 64
                   && (deltas[i + run] is < -128 or > 127) == words)
                run++;
            data.U8((words ? 0x40 : 0x00) | (run - 1));
            for (int k = 0; k < run; k++)
            {
                if (words)
                    data.I16(deltas[i + k]);
                else
                    data.U8(deltas[i + k] & 0xFF);
            }
            i += run;
        }
    }
}
