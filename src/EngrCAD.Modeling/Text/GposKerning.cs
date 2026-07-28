namespace EngrCAD.Modeling;

/// <summary>
/// Pair kerning from the OpenType <c>GPOS</c> table — where modern fonts keep it (many
/// ship no legacy <c>kern</c> table at all). Only what horizontal pair kerning needs is
/// read: the <c>kern</c> features in the FeatureList, their lookups of type 2
/// (PairPos, formats 1 and 2) — unwrapped through type 9 Extension lookups — with both
/// Coverage formats and both ClassDef formats. The x-advance adjustment of the first
/// glyph is the kerning value; everything else GPOS can express (mark attachment,
/// cursive joining, contextual positioning) is out of scope for laying glyph outlines
/// on a baseline.
/// <para>Semantics: within a lookup the <em>first</em> subtable whose coverage holds
/// the left glyph decides (a format-2 class pair always decides once covered — class 0
/// is the catch-all); across lookups the adjustments <em>accumulate</em>, as GPOS
/// lookups apply in sequence. Per the OpenType spec, a font whose GPOS has a
/// <c>kern</c> feature ignores the legacy <c>kern</c> table entirely —
/// <see cref="TrueTypeFont.KerningBetween"/> implements that precedence.</para>
/// </summary>
internal sealed class GposKerning
{
    private readonly Subtable[][] _lookups;                 // per kern lookup, its PairPos subtables in order

    private GposKerning(Subtable[][] lookups) => _lookups = lookups;

    /// <summary>Parses the <c>GPOS</c> table; null when it carries no <c>kern</c>
    /// feature or no pair-positioning lookups (the caller then falls back to the
    /// legacy <c>kern</c> table).</summary>
    public static GposKerning? Read(ReadOnlySpan<byte> data, int offset)
    {
        var header = new FontReader(data, offset);
        int major = header.ReadUInt16();
        if (major != 1)
            return null;                                    // an unknown major version: fall back rather than misread
        header.Skip(2);                                     // minor (1.1 adds featureVariations, which we do not need)
        header.Skip(2);                                     // scriptListOffset — kern applies to every script we lay out
        int featureListAt = offset + header.ReadUInt16();
        int lookupListAt = offset + header.ReadUInt16();

        // FeatureList: every 'kern' feature's lookup indices, deduplicated, in order.
        var lookupIndices = new SortedSet<int>();
        var features = new FontReader(data, featureListAt);
        int featureCount = features.ReadUInt16();
        for (int i = 0; i < featureCount; i++)
        {
            string tag = features.ReadTag();
            int featureAt = featureListAt + features.ReadUInt16();
            if (tag != "kern")
                continue;
            var feature = new FontReader(data, featureAt);
            feature.Skip(2);                                // featureParamsOffset
            int count = feature.ReadUInt16();
            for (int k = 0; k < count; k++)
                lookupIndices.Add(feature.ReadUInt16());
        }
        if (lookupIndices.Count == 0)
            return null;

        var lookupList = new FontReader(data, lookupListAt);
        int lookupCount = lookupList.ReadUInt16();
        var lookupOffsets = new int[lookupCount];
        for (int i = 0; i < lookupCount; i++)
            lookupOffsets[i] = lookupListAt + lookupList.ReadUInt16();

        var lookups = new List<Subtable[]>();
        foreach (int index in lookupIndices)
        {
            if (index >= lookupCount)
                continue;                                   // a dangling index: skip the lookup, not the font
            var subtables = ReadLookup(data, lookupOffsets[index]);
            if (subtables.Length > 0)
                lookups.Add(subtables);
        }
        return lookups.Count == 0 ? null : new GposKerning([.. lookups]);
    }

    private static Subtable[] ReadLookup(ReadOnlySpan<byte> data, int at)
    {
        var lookup = new FontReader(data, at);
        int type = lookup.ReadUInt16();
        lookup.Skip(2);                                     // lookupFlag: filtering marks is irrelevant to pair kerning
        int subtableCount = lookup.ReadUInt16();

        var subtables = new List<Subtable>(subtableCount);
        for (int i = 0; i < subtableCount; i++)
        {
            int subtableAt = at + lookup.ReadUInt16();
            int effectiveType = type;
            if (effectiveType == 9)                         // Extension: a 32-bit springboard to the real subtable
            {
                var extension = new FontReader(data, subtableAt);
                if (extension.ReadUInt16() != 1)
                    continue;
                effectiveType = extension.ReadUInt16();
                subtableAt += (int)extension.ReadUInt32();
            }
            if (effectiveType != 2)
                continue;                                   // not pair positioning
            var subtable = ReadPairPos(data, subtableAt);
            if (subtable is not null)
                subtables.Add(subtable);
        }
        return [.. subtables];
    }

    private static Subtable? ReadPairPos(ReadOnlySpan<byte> data, int at)
    {
        var reader = new FontReader(data, at);
        int format = reader.ReadUInt16();
        int coverageAt = at + reader.ReadUInt16();
        int valueFormat1 = reader.ReadUInt16();
        int valueFormat2 = reader.ReadUInt16();
        // The kerning value is the first glyph's x-advance adjustment; its slot within
        // a ValueRecord is the count of set format bits below it, and the record's
        // length is the total count (all fields are uint16s).
        if ((valueFormat1 & 0x0004) == 0)
            return null;                                    // no x-advance for glyph 1: nothing to kern with
        int advanceSlot = System.Numerics.BitOperations.PopCount((uint)(valueFormat1 & 0x0003));
        int record1Length = System.Numerics.BitOperations.PopCount((uint)(valueFormat1 & 0x00FF));
        int record2Length = System.Numerics.BitOperations.PopCount((uint)(valueFormat2 & 0x00FF));
        var coverage = Coverage.Read(data, coverageAt);

        switch (format)
        {
            case 1:
            {
                int pairSetCount = reader.ReadUInt16();
                var seconds = new int[pairSetCount][];
                var advances = new short[pairSetCount][];
                for (int i = 0; i < pairSetCount; i++)
                {
                    var pairSet = new FontReader(data, at + reader.ReadUInt16());
                    int pairCount = pairSet.ReadUInt16();
                    seconds[i] = new int[pairCount];
                    advances[i] = new short[pairCount];
                    for (int p = 0; p < pairCount; p++)
                    {
                        seconds[i][p] = pairSet.ReadUInt16();
                        for (int slot = 0; slot < record1Length; slot++)
                        {
                            short value = pairSet.ReadInt16();
                            if (slot == advanceSlot)
                                advances[i][p] = value;
                        }
                        pairSet.Skip(record2Length * 2);
                    }
                }
                return new PairsSubtable(coverage, seconds, advances);
            }
            case 2:
            {
                var class1 = ClassDef.Read(data, at + reader.ReadUInt16());
                var class2 = ClassDef.Read(data, at + reader.ReadUInt16());
                int class1Count = reader.ReadUInt16();
                int class2Count = reader.ReadUInt16();
                var values = new short[class1Count * class2Count];
                for (int c = 0; c < values.Length; c++)
                {
                    for (int slot = 0; slot < record1Length; slot++)
                    {
                        short value = reader.ReadInt16();
                        if (slot == advanceSlot)
                            values[c] = value;
                    }
                    reader.Skip(record2Length * 2);
                }
                return new ClassSubtable(coverage, class1, class2, class1Count, class2Count, values);
            }
            default:
                return null;
        }
    }

    /// <summary>The accumulated x-advance adjustment for the pair, in font units
    /// (negative pulls the glyphs together); 0 when no lookup kerns them.</summary>
    public double Kerning(int leftGlyph, int rightGlyph)
    {
        double total = 0;
        foreach (var lookup in _lookups)
        {
            foreach (var subtable in lookup)
            {
                if (subtable.TryGet(leftGlyph, rightGlyph, out double value))
                {
                    total += value;                         // first matching subtable per lookup decides
                    break;
                }
            }
        }
        return total;
    }

    // ---- subtables -----------------------------------------------------------

    private abstract class Subtable
    {
        public abstract bool TryGet(int left, int right, out double value);
    }

    /// <summary>PairPos format 1: per-covered-glyph sorted pair lists.</summary>
    private sealed class PairsSubtable(Coverage coverage, int[][] seconds, short[][] advances) : Subtable
    {
        public override bool TryGet(int left, int right, out double value)
        {
            value = 0;
            if (!coverage.TryIndex(left, out int index) || index >= seconds.Length)
                return false;
            int found = Array.BinarySearch(seconds[index], right);
            if (found < 0)
                return false;
            value = advances[index][found];
            return true;
        }
    }

    /// <summary>PairPos format 2: class-pair matrix. Once the left glyph is covered
    /// the classes always resolve (class 0 is the catch-all), so coverage alone
    /// decides whether this subtable applies.</summary>
    private sealed class ClassSubtable(
        Coverage coverage, ClassDef class1, ClassDef class2,
        int class1Count, int class2Count, short[] values) : Subtable
    {
        public override bool TryGet(int left, int right, out double value)
        {
            value = 0;
            if (!coverage.TryIndex(left, out _))
                return false;
            int c1 = class1.ClassOf(left);
            int c2 = class2.ClassOf(right);
            if (c1 >= class1Count || c2 >= class2Count)
                return true;                                // covered, but outside the matrix: kerns by zero
            value = values[c1 * class2Count + c2];
            return true;
        }
    }

    /// <summary>Coverage table: which glyphs a subtable applies to, and their index
    /// into its data. Format 1 is a sorted glyph list, format 2 sorted ranges.</summary>
    private sealed class Coverage
    {
        private readonly int[] _glyphs;                     // format 1 (empty for format 2)
        private readonly (int Start, int End, int Index)[] _ranges;

        private Coverage(int[] glyphs, (int, int, int)[] ranges)
        {
            _glyphs = glyphs;
            _ranges = ranges;
        }

        public static Coverage Read(ReadOnlySpan<byte> data, int at)
        {
            var reader = new FontReader(data, at);
            int format = reader.ReadUInt16();
            int count = reader.ReadUInt16();
            switch (format)
            {
                case 1:
                {
                    var glyphs = new int[count];
                    for (int i = 0; i < count; i++)
                        glyphs[i] = reader.ReadUInt16();
                    return new Coverage(glyphs, []);
                }
                case 2:
                {
                    var ranges = new (int, int, int)[count];
                    for (int i = 0; i < count; i++)
                    {
                        int start = reader.ReadUInt16();
                        int end = reader.ReadUInt16();
                        int index = reader.ReadUInt16();
                        ranges[i] = (start, end, index);
                    }
                    return new Coverage([], ranges);
                }
                default:
                    throw new FontFormatException($"GPOS coverage format {format} is not valid (1 or 2).");
            }
        }

        public bool TryIndex(int glyph, out int index)
        {
            if (_glyphs.Length > 0)
            {
                index = Array.BinarySearch(_glyphs, glyph);
                return index >= 0;
            }
            foreach (var (start, end, first) in _ranges)
            {
                if (glyph >= start && glyph <= end)
                {
                    index = first + glyph - start;
                    return true;
                }
            }
            index = -1;
            return false;
        }
    }

    /// <summary>ClassDef table: glyph to class id, class 0 for anything unlisted.
    /// Format 1 is a run from a start glyph, format 2 explicit ranges.</summary>
    private sealed class ClassDef
    {
        private readonly int _startGlyph;
        private readonly int[] _classes;                    // format 1 (empty for format 2)
        private readonly (int Start, int End, int Class)[] _ranges;

        private ClassDef(int startGlyph, int[] classes, (int, int, int)[] ranges)
        {
            _startGlyph = startGlyph;
            _classes = classes;
            _ranges = ranges;
        }

        public static ClassDef Read(ReadOnlySpan<byte> data, int at)
        {
            var reader = new FontReader(data, at);
            int format = reader.ReadUInt16();
            switch (format)
            {
                case 1:
                {
                    int startGlyph = reader.ReadUInt16();
                    int count = reader.ReadUInt16();
                    var classes = new int[count];
                    for (int i = 0; i < count; i++)
                        classes[i] = reader.ReadUInt16();
                    return new ClassDef(startGlyph, classes, []);
                }
                case 2:
                {
                    int count = reader.ReadUInt16();
                    var ranges = new (int, int, int)[count];
                    for (int i = 0; i < count; i++)
                    {
                        int start = reader.ReadUInt16();
                        int end = reader.ReadUInt16();
                        int classId = reader.ReadUInt16();
                        ranges[i] = (start, end, classId);
                    }
                    return new ClassDef(0, [], ranges);
                }
                default:
                    throw new FontFormatException($"GPOS class definition format {format} is not valid (1 or 2).");
            }
        }

        public int ClassOf(int glyph)
        {
            if (_classes.Length > 0)
            {
                int index = glyph - _startGlyph;
                return index >= 0 && index < _classes.Length ? _classes[index] : 0;
            }
            foreach (var (start, end, classId) in _ranges)
            {
                if (glyph >= start && glyph <= end)
                    return classId;
            }
            return 0;
        }
    }
}
