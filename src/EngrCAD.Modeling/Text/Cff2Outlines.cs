namespace EngrCAD.Modeling;

/// <summary>
/// PostScript outlines from an OpenType <c>CFF2</c> table — the variable-font flavour of
/// <see cref="CffOutlines"/>. The container is a stripped-down CFF (no Name, String or
/// charset INDEX, no encodings, 32-bit INDEX counts, the Top DICT stored directly rather
/// than in an INDEX of one) and the charstrings are Type 2's, minus the leading width
/// operand and <c>endchar</c> and plus two operators that make the outline itself a
/// function of the design space:
/// <list type="bullet">
/// <item><description><c>blend</c> — <c>n</c> default values followed by
/// <c>n × k</c> deltas and the count <c>n</c>, replaced on the stack by the <c>n</c>
/// values the current instance calls for. The <c>k</c> is not in the charstring: it is
/// the region count of the item variation data the current <c>vsindex</c> names, so a
/// reader that guesses it misreads every operand after the first blend.</description></item>
/// <item><description><c>vsindex</c> — which item variation data (hence which region
/// list, hence which <c>k</c>) the blends that follow read.</description></item>
/// </list>
/// <para>Everything a CFF2 charstring shares with Type 2 — the curve operators, the
/// flex family, subroutine calls, the hintmask stem-counting subtlety — is the shared
/// <see cref="Type2Interpreter"/>, so the two dialects cannot drift.</para>
/// </summary>
internal sealed class Cff2Outlines
{
    /// <summary>CFF2 raises Type 2's 48-operand limit to hold a blend's deltas.</summary>
    private const int Cff2StackLimit = 513;

    private readonly byte[] _data;
    private readonly (int Start, int End)[] _charStrings;
    private readonly (int Start, int End)[] _globalSubrs;
    private readonly (int Start, int End)[][] _localSubrsPerFd;
    private readonly int[] _defaultVsIndexPerFd;
    private readonly byte[] _fdForGlyph;
    private readonly ItemVariationStore? _store;

    private Cff2Outlines(
        byte[] data,
        (int Start, int End)[] charStrings,
        (int Start, int End)[] globalSubrs,
        (int Start, int End)[][] localSubrsPerFd,
        int[] defaultVsIndexPerFd,
        byte[] fdForGlyph,
        ItemVariationStore? store)
    {
        _data = data;
        _charStrings = charStrings;
        _globalSubrs = globalSubrs;
        _localSubrsPerFd = localSubrsPerFd;
        _defaultVsIndexPerFd = defaultVsIndexPerFd;
        _fdForGlyph = fdForGlyph;
        _store = store;
    }

    /// <summary>Number of charstrings (the caller checks it against maxp).</summary>
    public int GlyphCount => _charStrings.Length;

    /// <summary>True when the table carries its own variation store, i.e. its glyphs
    /// can blend.</summary>
    public bool HasVariationStore => _store is not null;

    public static Cff2Outlines Read(byte[] data, int offset, int length)
    {
        var span = data.AsSpan();
        var reader = new FontReader(span, offset);
        int major = reader.ReadUInt8();
        // Checked before anything else is read: a table that is not CFF2 must be refused
        // by NAME rather than by a truncation error from reading its header as one.
        if (major != 2)
            throw new FontFormatException($"CFF2 table version is {major}; only CFF2 version 2 is defined.");
        reader.Skip(1);                                  // minorVersion
        int headerSize = reader.ReadUInt8();
        int topDictLength = reader.ReadUInt16();
        if (headerSize < 5 || headerSize + topDictLength > length)
            throw new FontFormatException(
                $"CFF2 header declares headerSize {headerSize} and a {topDictLength}-byte Top DICT, " +
                $"which do not fit the {length}-byte table.");

        int topAt = offset + headerSize;
        var top = CffPrimitives.ParseDict(span, topAt, topAt + topDictLength);

        int globalSubrsAt = topAt + topDictLength;
        var globalSubrs = CffPrimitives.ReadIndex(span, ref globalSubrsAt, count32: true);

        if (!top.TryGetValue(17, out var charStringsOp) || charStringsOp.Length == 0)
            throw new FontFormatException("CFF2 Top DICT has no CharStrings offset (operator 17).");
        int charStringsAt = offset + (int)charStringsOp[0];
        var charStrings = CffPrimitives.ReadIndex(span, ref charStringsAt, count32: true);
        if (charStrings.Length == 0)
            throw new FontFormatException("CFF2 CharStrings INDEX is empty; the font contains no glyph outlines.");

        ItemVariationStore? store = null;
        if (top.TryGetValue(24, out var vstoreOp) && vstoreOp.Length > 0)
        {
            // The vstore is length-prefixed so a consumer can skip it; the store itself
            // starts after that length.
            int vstoreAt = offset + (int)vstoreOp[0];
            var lengthReader = new FontReader(span, vstoreAt);
            _ = lengthReader.ReadUInt16();
            store = ItemVariationStore.Read(span, vstoreAt + 2);
        }

        // CFF2 always keeps its Private DICTs in FDArray, whether or not the font is
        // CID-keyed; FDSelect is optional and its absence means every glyph uses font
        // DICT 0.
        if (!top.TryGetValue(CffPrimitives.Op(12, 36), out var fdArrayOp) || fdArrayOp.Length == 0)
            throw new FontFormatException("CFF2 Top DICT has no FDArray (operator 12 36); CFF2 requires one.");
        int fdArrayAt = offset + (int)fdArrayOp[0];
        var fontDicts = CffPrimitives.ReadIndex(span, ref fdArrayAt, count32: true);
        if (fontDicts.Length == 0)
            throw new FontFormatException("CFF2 FDArray is empty; there are no Private DICTs.");

        var localSubrsPerFd = new (int, int)[fontDicts.Length][];
        var defaultVsIndexPerFd = new int[fontDicts.Length];
        for (int i = 0; i < fontDicts.Length; i++)
        {
            var dict = CffPrimitives.ParseDict(span, fontDicts[i].Start, fontDicts[i].End);
            (localSubrsPerFd[i], defaultVsIndexPerFd[i]) = ReadPrivate(span, offset, dict);
        }

        var fdForGlyph = new byte[charStrings.Length];
        if (top.TryGetValue(CffPrimitives.Op(12, 37), out var fdSelectOp) && fdSelectOp.Length > 0)
            fdForGlyph = ReadFdSelect(span, offset + (int)fdSelectOp[0], charStrings.Length, fontDicts.Length);

        return new Cff2Outlines(
            data, charStrings, globalSubrs, localSubrsPerFd, defaultVsIndexPerFd, fdForGlyph, store);
    }

    private static ((int Start, int End)[] Subrs, int VsIndex) ReadPrivate(
        ReadOnlySpan<byte> span, int cffStart, Dictionary<int, double[]> fontDict)
    {
        if (!fontDict.TryGetValue(18, out var priv) || priv.Length < 2)
            return ([], 0);
        int size = (int)priv[0];
        int at = cffStart + (int)priv[1];
        var privateDict = CffPrimitives.ParseDict(span, at, at + size);

        int vsIndex = privateDict.TryGetValue(22, out var vs) && vs.Length > 0 ? (int)vs[0] : 0;
        if (!privateDict.TryGetValue(19, out var subrs) || subrs.Length == 0)
            return ([], vsIndex);
        int subrsAt = at + (int)subrs[0];                // Subrs offset is relative to the Private DICT
        return (CffPrimitives.ReadIndex(span, ref subrsAt, count32: true), vsIndex);
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
            case 3 or 4:
            {
                bool wide = format == 4;                 // CFF2's 32-bit ranges, for very large fonts
                long ranges = wide ? reader.ReadUInt32() : reader.ReadUInt16();
                long first = wide ? reader.ReadUInt32() : reader.ReadUInt16();
                for (long r = 0; r < ranges; r++)
                {
                    int fdIndex = wide ? reader.ReadUInt16() : reader.ReadUInt8();
                    long next = wide ? reader.ReadUInt32() : reader.ReadUInt16();
                    if (first < 0 || next > glyphCount || next < first)
                        throw new FontFormatException(
                            $"CFF2 FDSelect range {r} covers glyphs {first}..{next - 1}, outside 0..{glyphCount - 1}.");
                    for (long g = first; g < next; g++)
                        fd[g] = (byte)fdIndex;
                    first = next;
                }
                break;
            }
            default:
                throw new FontFormatException($"CFF2 FDSelect format {format} is not supported (0, 3 and 4 are).");
        }
        foreach (byte index in fd)
        {
            if (index >= fdCount)
                throw new FontFormatException($"CFF2 FDSelect maps a glyph to font DICT {index}, but FDArray has {fdCount}.");
        }
        return fd;
    }

    /// <summary>Decodes one glyph's outline at the given normalized instance. An empty
    /// coordinate span is the default instance, where every region's scalar is zero and
    /// a blend returns its own default values.</summary>
    public List<GlyphContour> ReadGlyph(int glyphIndex, ReadOnlySpan<double> coordinates)
    {
        if ((uint)glyphIndex >= (uint)_charStrings.Length)
            throw new ArgumentOutOfRangeException(nameof(glyphIndex));
        int fd = _fdForGlyph[glyphIndex];
        var interpreter = new Interpreter(
            _data, _localSubrsPerFd[fd], _globalSubrs, glyphIndex, _store, _defaultVsIndexPerFd[fd], coordinates);
        var (start, end) = _charStrings[glyphIndex];
        interpreter.Run(start, end, depth: 0);
        interpreter.FlushContour();
        return interpreter.Contours;
    }

    /// <summary>CFF2's dialect over the shared machine: no width, no <c>endchar</c>,
    /// and the two variation operators.</summary>
    private sealed class Interpreter : Type2Interpreter
    {
        private readonly ItemVariationStore? _store;
        private readonly double[] _coordinates;
        private double[]? _scalars;
        private int _vsIndex;

        public Interpreter(
            byte[] data, (int Start, int End)[] localSubrs, (int Start, int End)[] globalSubrs,
            int glyphIndex, ItemVariationStore? store, int defaultVsIndex, ReadOnlySpan<double> coordinates)
            : base(data, localSubrs, globalSubrs, glyphIndex)
        {
            _store = store;
            _coordinates = coordinates.ToArray();
            _vsIndex = defaultVsIndex;
        }

        protected override string Dialect => "CFF2";

        protected override int StackLimit => Cff2StackLimit;

        protected override bool Extend(int op, ref FontReader reader)
        {
            switch (op)
            {
                case 15:                                 // vsindex
                    SetVariationStoreIndex();
                    return true;
                case 16:                                 // blend
                    Blend();
                    return true;
                default:
                    return false;
            }
        }

        private void SetVariationStoreIndex()
        {
            if (Stack.Count == 0)
                throw new FontFormatException($"Glyph {GlyphIndex}: vsindex with an empty stack.");
            if (_scalars is not null)
                throw new FontFormatException(
                    $"Glyph {GlyphIndex}: vsindex appears after a blend, which CFF2 forbids " +
                    "(every blend of a charstring reads one variation store index).");
            _vsIndex = (int)Stack[^1];
            Stack.Clear();
        }

        /// <summary>
        /// <c>blend</c>: the top operand is the value count <c>n</c>; below it sit
        /// <c>n × k</c> deltas and below those the <c>n</c> default values. All of them
        /// are replaced by the <c>n</c> blended values, so the operator that follows sees
        /// exactly the argument list a static font would have written.
        /// </summary>
        private void Blend()
        {
            if (Stack.Count == 0)
                throw new FontFormatException($"Glyph {GlyphIndex}: blend with an empty stack.");
            int n = (int)Stack[^1];
            Stack.RemoveAt(Stack.Count - 1);
            if (n < 0)
                throw new FontFormatException($"Glyph {GlyphIndex}: blend declares {n} values.");

            var scalars = _scalars ??= Scalars();
            int k = scalars.Length;
            int needed = n * (k + 1);
            if (Stack.Count < needed)
                throw new FontFormatException(
                    $"Glyph {GlyphIndex}: blend of {n} values over {k} regions needs {needed} operands, " +
                    $"but {Stack.Count} are on the stack.");

            int baseAt = Stack.Count - needed;
            for (int i = 0; i < n; i++)
            {
                double value = Stack[baseAt + i];
                for (int r = 0; r < k; r++)
                {
                    double scalar = scalars[r];
                    if (scalar != 0)
                        value += scalar * Stack[baseAt + n + i * k + r];
                }
                Stack[baseAt + i] = value;
            }
            Stack.RemoveRange(baseAt + n, n * k);
        }

        private double[] Scalars()
        {
            if (_store is null)
                throw new FontFormatException(
                    $"Glyph {GlyphIndex}: the charstring blends, but the CFF2 table carries no variation store.");
            return _store.Scalars(_vsIndex, _coordinates);
        }
    }
}
