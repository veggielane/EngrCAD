namespace EngrCAD.Modeling;

/// <summary>
/// One design axis of a variable font (<c>fvar</c>): a four-character
/// <see cref="Tag"/> (the registered ones are <c>wght</c>, <c>wdth</c>, <c>ital</c>,
/// <c>slnt</c> and <c>opsz</c>; anything else is the foundry's own) and the range a
/// caller may set it over, in the axis's own USER units — weights 100..900, widths as
/// percentages, optical sizes in points.
/// <para>Values outside <see cref="Minimum"/>..<see cref="Maximum"/> are CLAMPED rather
/// than refused, which is the specification's own rule: normalization clamps first, so
/// there is no coordinate a font could be asked for and fail to draw.</para>
/// </summary>
/// <param name="Tag">The four-character axis tag, e.g. <c>wght</c>.</param>
/// <param name="Minimum">Smallest value the font was designed for, user units.</param>
/// <param name="Default">The value an un-instanced font draws at.</param>
/// <param name="Maximum">Largest value the font was designed for, user units.</param>
/// <param name="Name">Human-readable axis name from the <c>name</c> table (empty when
/// the font supplies none).</param>
/// <param name="Hidden">True when the font marks the axis as not for direct user
/// selection (fvar flags bit 0) — reported rather than dropped, since it is still a
/// legal axis to set.</param>
public sealed record VariationAxis(
    string Tag, double Minimum, double Default, double Maximum, string Name, bool Hidden);

/// <summary>
/// A named point in a variable font's design space (<c>fvar</c> instances) — "Bold",
/// "Condensed Light" — as the coordinates it stands for. A named instance is a
/// CONVENIENCE over <see cref="TrueTypeFont.WithVariation(ValueTuple{string, double}[])"/>
/// and nothing more: it names a coordinate the caller could have typed.
/// </summary>
public sealed class NamedInstance
{
    internal NamedInstance(string name, IReadOnlyDictionary<string, double> coordinates, string? postScriptName)
    {
        Name = name;
        Coordinates = coordinates;
        PostScriptName = postScriptName;
    }

    /// <summary>The instance's subfamily name from the <c>name</c> table.</summary>
    public string Name { get; }

    /// <summary>User-unit coordinate per axis tag.</summary>
    public IReadOnlyDictionary<string, double> Coordinates { get; }

    /// <summary>The instance's PostScript name when the font states one.</summary>
    public string? PostScriptName { get; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>
/// The design space of a variable font: the <c>fvar</c> axes and named instances, plus
/// the <c>avar</c> segment maps that warp a user coordinate on its way to the
/// normalized [-1, 1] the delta machinery works in.
/// <para><b>Normalization is piecewise linear about the DEFAULT</b>, which is what makes
/// the default instance exactly the un-instanced font: below the default the coordinate
/// runs -1..0 over min..default and above it 0..1 over default..max, so every axis
/// reads exactly 0 at its own default whatever its user range is (a weight axis
/// 100/400/900 is not symmetric, and a linear map over the whole range would put its
/// default at -0.25 rather than 0).</para>
/// </summary>
internal sealed class FontVariations
{
    /// <summary>Per axis, the <c>avar</c> segment map as (from, to) pairs in normalized
    /// coordinates, sorted by <c>from</c>; empty when the axis states none.</summary>
    private readonly (double From, double To)[][] _segmentMaps;

    private FontVariations(
        IReadOnlyList<VariationAxis> axes,
        IReadOnlyList<NamedInstance> instances,
        (double From, double To)[][] segmentMaps,
        string? avarRefusal)
    {
        Axes = axes;
        Instances = instances;
        _segmentMaps = segmentMaps;
        AvarRefusal = avarRefusal;
    }

    public IReadOnlyList<VariationAxis> Axes { get; }

    public IReadOnlyList<NamedInstance> Instances { get; }

    /// <summary>Non-null when the font carries an <c>avar</c> table this reader cannot
    /// honour (version 2's item-variation-store axis mapping). Reading the font still
    /// works — every axis is at its default there, where <c>avar</c> maps 0 to 0 by the
    /// format's own required entry — so the refusal is raised where it first matters,
    /// at <see cref="TrueTypeFont.WithVariation(ValueTuple{string, double}[])"/>.</summary>
    public string? AvarRefusal { get; }

    /// <summary>All axes at their default: the coordinate an un-instanced variable font
    /// draws at, and therefore exactly zero on every axis.</summary>
    public double[] DefaultCoordinates => new double[Axes.Count];

    /// <summary>Reads <c>fvar</c> (and <c>avar</c> when present); null when the font
    /// declares no axes.</summary>
    public static FontVariations? Read(ReadOnlySpan<byte> data, int fvarOffset, int fvarLength,
        int? avarOffset, Func<int, string> nameOf)
    {
        var reader = new FontReader(data, fvarOffset);
        int major = reader.ReadUInt16();
        reader.Skip(2);                                  // minorVersion
        if (major != 1)
            throw new FontFormatException($"fvar table version is {major}; only version 1 is defined.");
        int axesArrayOffset = reader.ReadUInt16();
        reader.Skip(2);                                  // reserved
        int axisCount = reader.ReadUInt16();
        int axisSize = reader.ReadUInt16();
        int instanceCount = reader.ReadUInt16();
        int instanceSize = reader.ReadUInt16();
        if (axisCount == 0)
            return null;                                 // a declared-but-empty fvar is a static font
        if (axisSize < 20)
            throw new FontFormatException($"fvar axisSize is {axisSize}; an axis record is 20 bytes.");
        if (instanceSize < axisCount * 4 + 4)
            throw new FontFormatException(
                $"fvar instanceSize is {instanceSize}, too small for {axisCount} axes.");
        if ((long)axesArrayOffset + (long)axisCount * axisSize + (long)instanceCount * instanceSize > fvarLength)
            throw new FontFormatException("fvar axis and instance arrays run past the table.");

        var axes = new VariationAxis[axisCount];
        for (int i = 0; i < axisCount; i++)
        {
            var axis = new FontReader(data, fvarOffset + axesArrayOffset + i * axisSize);
            string tag = axis.ReadTag();
            double minimum = axis.ReadFixed();
            double @default = axis.ReadFixed();
            double maximum = axis.ReadFixed();
            int flags = axis.ReadUInt16();
            int nameId = axis.ReadUInt16();
            if (!(minimum <= @default && @default <= maximum))
                throw new FontFormatException(
                    $"fvar axis '{tag}' has min/default/max {minimum}/{@default}/{maximum}, which is not ordered.");
            axes[i] = new VariationAxis(tag, minimum, @default, maximum, nameOf(nameId), (flags & 0x0001) != 0);
        }

        var instances = new List<NamedInstance>(instanceCount);
        int instancesAt = fvarOffset + axesArrayOffset + axisCount * axisSize;
        for (int i = 0; i < instanceCount; i++)
        {
            var instance = new FontReader(data, instancesAt + i * instanceSize);
            int subfamilyNameId = instance.ReadUInt16();
            instance.Skip(2);                            // flags
            var coordinates = new Dictionary<string, double>(axisCount, StringComparer.Ordinal);
            for (int a = 0; a < axisCount; a++)
                coordinates[axes[a].Tag] = instance.ReadFixed();
            string? postScript = instanceSize >= axisCount * 4 + 6 ? nameOf(instance.ReadUInt16()) : null;
            instances.Add(new NamedInstance(
                nameOf(subfamilyNameId), coordinates, string.IsNullOrEmpty(postScript) ? null : postScript));
        }

        var segmentMaps = new (double, double)[axisCount][];
        for (int i = 0; i < axisCount; i++)
            segmentMaps[i] = [];
        string? avarRefusal = null;
        if (avarOffset is { } avar)
            avarRefusal = ReadAvar(data, avar, axisCount, segmentMaps);

        return new FontVariations(axes, instances, segmentMaps, avarRefusal);
    }

    /// <summary>Reads the <c>avar</c> segment maps into <paramref name="segmentMaps"/>;
    /// returns a refusal message when the table is a version this reader cannot
    /// honour.</summary>
    private static string? ReadAvar(
        ReadOnlySpan<byte> data, int offset, int axisCount, (double From, double To)[][] segmentMaps)
    {
        var reader = new FontReader(data, offset);
        int major = reader.ReadUInt16();
        reader.Skip(2);                                  // minorVersion
        if (major != 1)
            return $"the font's 'avar' table is version {major}; only version 1 (segment maps) is read, " +
                   "so a non-default instance of this font would use unmapped axis coordinates.";
        reader.Skip(2);                                  // reserved
        int mapCount = reader.ReadUInt16();
        if (mapCount != axisCount)
            throw new FontFormatException(
                $"avar declares {mapCount} segment maps but fvar declares {axisCount} axes.");

        for (int i = 0; i < axisCount; i++)
        {
            int pairs = reader.ReadUInt16();
            var map = new (double From, double To)[pairs];
            for (int p = 0; p < pairs; p++)
                map[p] = (reader.ReadF2Dot14(), reader.ReadF2Dot14());
            for (int p = 1; p < pairs; p++)
            {
                if (map[p].From < map[p - 1].From)
                    throw new FontFormatException(
                        $"avar segment map for axis {i} is not sorted by fromCoordinate.");
            }
            // The format requires the identity entries -1, 0 and +1; a map without them
            // cannot be interpolated over its whole range, so it is ignored rather than
            // extrapolated (the specification's own remedy).
            segmentMaps[i] = pairs >= 3 ? map : [];
        }
        return null;
    }

    /// <summary>Index of the axis with this tag, or -1.</summary>
    public int IndexOf(string tag)
    {
        for (int i = 0; i < Axes.Count; i++)
        {
            if (string.Equals(Axes[i].Tag, tag, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// User coordinates to the normalized [-1, 1] the delta machinery works in: clamp
    /// to the axis range, map piecewise-linearly about the default, then warp through
    /// <c>avar</c>. An axis the caller says nothing about takes its default, which
    /// normalizes to exactly 0.
    /// </summary>
    public double[] Normalize(IReadOnlyDictionary<string, double> settings)
    {
        var coordinates = new double[Axes.Count];
        for (int i = 0; i < Axes.Count; i++)
        {
            var axis = Axes[i];
            if (!settings.TryGetValue(axis.Tag, out double value))
                continue;                                // default: exactly 0, whatever the range
            coordinates[i] = ApplySegmentMap(_segmentMaps[i], NormalizeAxis(axis, value));
        }
        return coordinates;
    }

    /// <summary>The specification's own piecewise-linear normalization, split at the
    /// default so it reads exactly 0 there.</summary>
    internal static double NormalizeAxis(VariationAxis axis, double value)
    {
        double clamped = Math.Clamp(value, axis.Minimum, axis.Maximum);
        if (clamped < axis.Default)
            return axis.Default > axis.Minimum ? -(axis.Default - clamped) / (axis.Default - axis.Minimum) : 0;
        if (clamped > axis.Default)
            return axis.Maximum > axis.Default ? (clamped - axis.Default) / (axis.Maximum - axis.Default) : 0;
        return 0;
    }

    /// <summary>Piecewise-linear interpolation through one <c>avar</c> segment map;
    /// the identity when the axis states none.</summary>
    internal static double ApplySegmentMap((double From, double To)[] map, double coordinate)
    {
        if (map.Length == 0)
            return coordinate;
        if (coordinate <= map[0].From)
            return map[0].To;
        for (int i = 1; i < map.Length; i++)
        {
            if (coordinate > map[i].From)
                continue;
            var (fromA, toA) = map[i - 1];
            var (fromB, toB) = map[i];
            if (fromB <= fromA)
                return toB;                              // a repeated key: take the later entry
            return toA + (toB - toA) * (coordinate - fromA) / (fromB - fromA);
        }
        return map[^1].To;
    }
}

/// <summary>
/// One region of a font's design space: per axis a (start, peak, end) triple in
/// normalized coordinates. A region contributes a SCALAR — how strongly its deltas
/// apply at a given instance — and the scalar is the PRODUCT over axes, so an axis whose
/// peak is exactly zero contributes exactly 1 rather than 0. That exact-zero clause is
/// the one that silently ruins everything: a weight-only region has peak 0 on the width
/// axis, and treating that as "the axis is off" zeroes every delta in the font.
/// </summary>
internal readonly struct VariationRegion(double[] start, double[] peak, double[] end)
{
    private readonly double[] _start = start;
    private readonly double[] _peak = peak;
    private readonly double[] _end = end;

    public int AxisCount => _peak.Length;

    public double Start(int axis) => _start[axis];

    public double Peak(int axis) => _peak[axis];

    public double End(int axis) => _end[axis];

    /// <summary>
    /// How strongly this region applies at <paramref name="coordinates"/> — 0 outside
    /// it, 1 at its peak, linearly ramped between. Three clauses are the specification's
    /// and each is load-bearing: a zero peak means the axis is not involved (factor 1),
    /// a region whose triple is not ordered is ignored rather than trusted, and a region
    /// straddling the default (start &lt; 0 &lt; end) is ignored, because OpenType
    /// splits such a region into two one-sided ones and a font that spells it anyway is
    /// asking for something the format does not define.
    /// </summary>
    public double Scalar(ReadOnlySpan<double> coordinates)
    {
        double scalar = 1;
        for (int axis = 0; axis < _peak.Length; axis++)
        {
            double peak = _peak[axis], lower = _start[axis], upper = _end[axis];
            if (peak == 0)
                continue;                                // THE exact-zero clause
            if (lower > peak || peak > upper)
                continue;
            if (lower < 0 && upper > 0)
                continue;
            double value = axis < coordinates.Length ? coordinates[axis] : 0;
            if (value == peak)
                continue;
            if (value <= lower || upper <= value)
                return 0;
            scalar *= value < peak
                ? (value - lower) / (peak - lower)
                : (upper - value) / (upper - peak);
        }
        return scalar;
    }
}

/// <summary>
/// An OpenType Item Variation Store: a shared list of <see cref="VariationRegion"/>s
/// plus numbered delta sets over them. It is the delta carrier for everything except
/// <c>gvar</c> — <c>HVAR</c>'s advance widths and <c>CFF2</c>'s <c>blend</c> operator
/// both read one — so it is parsed once here rather than in each consumer.
/// </summary>
internal sealed class ItemVariationStore
{
    private readonly VariationRegion[] _regions;
    private readonly ItemData[] _items;

    private ItemVariationStore(VariationRegion[] regions, ItemData[] items)
    {
        _regions = regions;
        _items = items;
    }

    /// <summary>Number of item variation data subtables (the outer index's range).</summary>
    public int ItemDataCount => _items.Length;

    /// <summary>Number of regions the item data at <paramref name="outerIndex"/> blends
    /// over — <c>CFF2</c>'s <c>blend</c> reads its operand count from this.</summary>
    public int RegionCount(int outerIndex) =>
        (uint)outerIndex < (uint)_items.Length
            ? _items[outerIndex].RegionIndexes.Length
            : throw new FontFormatException(
                $"Item variation data {outerIndex} does not exist ({_items.Length} defined).");

    public static ItemVariationStore Read(ReadOnlySpan<byte> data, int offset)
    {
        var reader = new FontReader(data, offset);
        int format = reader.ReadUInt16();
        if (format != 1)
            throw new FontFormatException($"Item variation store format is {format}; only format 1 is defined.");
        int regionListOffset = (int)reader.ReadUInt32();
        int dataCount = reader.ReadUInt16();
        var dataOffsets = new int[dataCount];
        for (int i = 0; i < dataCount; i++)
            dataOffsets[i] = (int)reader.ReadUInt32();

        var regionReader = new FontReader(data, offset + regionListOffset);
        int axisCount = regionReader.ReadUInt16();
        int regionCount = regionReader.ReadUInt16();
        var regions = new VariationRegion[regionCount];
        for (int r = 0; r < regionCount; r++)
        {
            var start = new double[axisCount];
            var peak = new double[axisCount];
            var end = new double[axisCount];
            for (int a = 0; a < axisCount; a++)
            {
                start[a] = regionReader.ReadF2Dot14();
                peak[a] = regionReader.ReadF2Dot14();
                end[a] = regionReader.ReadF2Dot14();
            }
            regions[r] = new VariationRegion(start, peak, end);
        }

        var items = new ItemData[dataCount];
        for (int i = 0; i < dataCount; i++)
            items[i] = ItemData.Read(data, offset + dataOffsets[i], regionCount);
        return new ItemVariationStore(regions, items);
    }

    /// <summary>The delta for one (outer, inner) index pair at
    /// <paramref name="coordinates"/>: the region scalars against that row's stored
    /// deltas.</summary>
    public double Delta(int outerIndex, int innerIndex, ReadOnlySpan<double> coordinates)
    {
        if ((uint)outerIndex >= (uint)_items.Length)
            throw new FontFormatException(
                $"Item variation data {outerIndex} does not exist ({_items.Length} defined).");
        var item = _items[outerIndex];
        if ((uint)innerIndex >= (uint)item.RowCount)
            throw new FontFormatException(
                $"Item variation data {outerIndex} has {item.RowCount} rows; row {innerIndex} was asked for.");

        double sum = 0;
        var indexes = item.RegionIndexes;
        for (int r = 0; r < indexes.Length; r++)
        {
            double scalar = _regions[indexes[r]].Scalar(coordinates);
            if (scalar != 0)
                sum += scalar * item.Deltas[innerIndex * indexes.Length + r];
        }
        return sum;
    }

    /// <summary>The scalars of one item data's regions, in its own region order —
    /// <c>CFF2</c>'s <c>blend</c> multiplies its operand deltas by exactly these.</summary>
    public double[] Scalars(int outerIndex, ReadOnlySpan<double> coordinates)
    {
        if ((uint)outerIndex >= (uint)_items.Length)
            throw new FontFormatException(
                $"Item variation data {outerIndex} does not exist ({_items.Length} defined).");
        var indexes = _items[outerIndex].RegionIndexes;
        var scalars = new double[indexes.Length];
        for (int r = 0; r < indexes.Length; r++)
            scalars[r] = _regions[indexes[r]].Scalar(coordinates);
        return scalars;
    }

    private readonly record struct ItemData(int RowCount, ushort[] RegionIndexes, int[] Deltas)
    {
        public static ItemData Read(ReadOnlySpan<byte> data, int offset, int regionCount)
        {
            var reader = new FontReader(data, offset);
            int itemCount = reader.ReadUInt16();
            int wordDeltaCount = reader.ReadUInt16();
            bool longWords = (wordDeltaCount & 0x8000) != 0;
            int wordCount = wordDeltaCount & 0x7FFF;
            int regionIndexCount = reader.ReadUInt16();
            if (wordCount > regionIndexCount)
                throw new FontFormatException(
                    $"Item variation data declares {wordCount} wide deltas over {regionIndexCount} regions.");
            var indexes = new ushort[regionIndexCount];
            for (int i = 0; i < regionIndexCount; i++)
            {
                indexes[i] = reader.ReadUInt16();
                if (indexes[i] >= regionCount)
                    throw new FontFormatException(
                        $"Item variation data references region {indexes[i]}, past the {regionCount} defined.");
            }

            var deltas = new int[itemCount * regionIndexCount];
            for (int row = 0; row < itemCount; row++)
            {
                for (int r = 0; r < regionIndexCount; r++)
                {
                    deltas[row * regionIndexCount + r] = r < wordCount
                        ? longWords ? (int)reader.ReadUInt32() : reader.ReadInt16()
                        : longWords ? reader.ReadInt16() : reader.ReadInt8();
                }
            }
            return new ItemData(itemCount, indexes, deltas);
        }
    }
}

/// <summary>
/// A <c>DeltaSetIndexMap</c>: glyph id (or any item id) to the (outer, inner) pair that
/// names its row in an <see cref="ItemVariationStore"/>. An id past the end of the map
/// takes the LAST entry, which is the format's own rule and is what lets a font whose
/// tail of glyphs share one delta set store one entry for all of them.
/// </summary>
internal sealed class DeltaSetIndexMap
{
    private readonly int[] _entries;
    private readonly int _innerBits;

    private DeltaSetIndexMap(int[] entries, int innerBits)
    {
        _entries = entries;
        _innerBits = innerBits;
    }

    public static DeltaSetIndexMap Read(ReadOnlySpan<byte> data, int offset)
    {
        var reader = new FontReader(data, offset);
        int format = reader.ReadUInt8();
        int entryFormat = reader.ReadUInt8();
        int mapCount = format switch
        {
            0 => reader.ReadUInt16(),
            1 => (int)reader.ReadUInt32(),
            _ => throw new FontFormatException($"DeltaSetIndexMap format {format} is not defined (0 and 1 are)."),
        };
        int innerBits = (entryFormat & 0x0F) + 1;
        int entrySize = ((entryFormat & 0x30) >> 4) + 1;

        var entries = new int[mapCount];
        for (int i = 0; i < mapCount; i++)
        {
            int value = 0;
            for (int b = 0; b < entrySize; b++)
                value = (value << 8) | reader.ReadUInt8();
            entries[i] = value;
        }
        return new DeltaSetIndexMap(entries, innerBits);
    }

    public (int Outer, int Inner) this[int id]
    {
        get
        {
            if (_entries.Length == 0)
                return (0, 0);
            int entry = _entries[Math.Min(Math.Max(id, 0), _entries.Length - 1)];
            return (entry >> _innerBits, entry & ((1 << _innerBits) - 1));
        }
    }
}

/// <summary>
/// <c>HVAR</c>: horizontal metrics variations — how a glyph's advance width changes
/// across the design space. It is the OTHER route to a varied advance, and which route
/// applies is not a preference: a font carrying <c>HVAR</c> is entitled to omit the
/// phantom-point deltas from <c>gvar</c> (that is the whole reason the table exists), so
/// ignoring it would silently lay a bold instance out at the light instance's spacing —
/// a defect no outline test can see.
/// </summary>
internal sealed class MetricsVariations
{
    private readonly ItemVariationStore _store;
    private readonly DeltaSetIndexMap? _advanceMap;

    private MetricsVariations(ItemVariationStore store, DeltaSetIndexMap? advanceMap)
    {
        _store = store;
        _advanceMap = advanceMap;
    }

    public static MetricsVariations Read(ReadOnlySpan<byte> data, int offset)
    {
        var reader = new FontReader(data, offset);
        int major = reader.ReadUInt16();
        reader.Skip(2);                                  // minorVersion
        if (major != 1)
            throw new FontFormatException($"HVAR table version is {major}; only version 1 is defined.");
        int storeOffset = (int)reader.ReadUInt32();
        int advanceMapOffset = (int)reader.ReadUInt32();
        // lsbMappingOffset and rsbMappingOffset follow; side bearings are metadata here
        // (outlines carry absolute coordinates), so they are deliberately not read.

        var store = ItemVariationStore.Read(data, offset + storeOffset);
        var advanceMap = advanceMapOffset != 0 ? DeltaSetIndexMap.Read(data, offset + advanceMapOffset) : null;
        return new MetricsVariations(store, advanceMap);
    }

    /// <summary>The advance-width delta for a glyph, font units. With no advance map the
    /// glyph id IS the inner index into item data 0 — the format's implicit mapping.</summary>
    public double AdvanceDelta(int glyphIndex, ReadOnlySpan<double> coordinates)
    {
        var (outer, inner) = _advanceMap is { } map ? map[glyphIndex] : (0, glyphIndex);
        return _store.Delta(outer, inner, coordinates);
    }
}
