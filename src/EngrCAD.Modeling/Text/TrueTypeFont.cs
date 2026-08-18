using System.Collections.Concurrent;
using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// An OpenType font read straight from its bytes — no third-party dependency, in the
/// spirit of the rest of the kernel (kernel projects pack to NuGet and stay dependency
/// free). Both outline flavours are supported: TrueType quadratics from a
/// <c>glyf</c> table (<c>.ttf</c>) and PostScript cubics from a <c>CFF </c> table
/// (<c>OTTO</c>-flavoured <c>.otf</c>, see <see cref="CffOutlines"/>). Only what
/// modeled text needs is parsed: <c>head</c>, <c>maxp</c>, <c>cmap</c> (formats 4 and
/// 12), <c>loca</c>/<c>glyf</c> (simple <em>and</em> composite glyphs) or
/// <c>CFF </c> (Type 2 charstrings, including CID-keyed fonts),
/// <c>hhea</c>/<c>hmtx</c>, plus optional <c>kern</c> (format 0), <c>name</c> and
/// <c>OS/2</c>. Hinting is skipped — a rasterization concern, and modeled text is
/// resolution independent.
/// <para><b>Variable fonts</b> are read in all three flavours: <c>fvar</c>/<c>avar</c>
/// declare the design space, <c>gvar</c> varies <c>glyf</c> outlines (with IUP and
/// phantom points), <c>CFF2</c> varies PostScript ones (with <c>blend</c>), and
/// <c>HVAR</c> varies advance widths. <see cref="WithVariation(ValueTuple{string, double}[])"/>
/// returns a font at a chosen point of that space and every consumer — <c>Shape.Text</c>,
/// text on a path, the layout engine — is untouched, because a varied font IS a font.
/// At the default coordinate the outlines are BIT-IDENTICAL to the un-instanced read.</para>
/// <para><b>Not supported:</b> TrueType Collections (<c>.ttc</c>), and the variation
/// tables that carry no outline or advance — <c>MVAR</c> (so <see cref="Ascender"/>,
/// <see cref="Descender"/> and a declared <see cref="CapHeight"/> stay the default
/// instance's), <c>VVAR</c>, <c>cvar</c> (hinting, which is skipped anyway), <c>STAT</c>
/// (a style-naming table) and feature variations in <c>GSUB</c>/<c>GPOS</c>. Side
/// bearings deliberately do not vary either: outlines carry absolute coordinates here,
/// so a bearing is metadata nothing places geometry by.</para>
/// <para>Glyph outlines are cached per index and the type is immutable after loading,
/// so one font instance can be shared across threads (e.g. <c>Scene.PreMesh</c>).</para>
/// </summary>
/// <example>
/// <code>
/// var font = TrueTypeFont.Load(@"C:\Windows\Fonts\arial.ttf");
/// var plate = Shape.Text("ENGRCAD", font, size: 8, height: 1.5);
///
/// var bold = TrueTypeFont.Load(@"C:\Windows\Fonts\bahnschrift.ttf").WithVariation(("wght", 700));
/// </code>
/// </example>
public sealed class TrueTypeFont
{
    // glyf simple-glyph point flags (OpenType spec, "Simple Glyph Description").
    private const byte FlagOnCurve = 0x01;
    private const byte FlagXShort = 0x02;
    private const byte FlagYShort = 0x04;
    private const byte FlagRepeat = 0x08;
    private const byte FlagXSameOrPositive = 0x10;
    private const byte FlagYSameOrPositive = 0x20;

    // glyf composite-glyph component flags.
    private const int CompArgsAreWords = 0x0001;
    private const int CompArgsAreXy = 0x0002;
    private const int CompHaveScale = 0x0008;
    private const int CompMoreComponents = 0x0020;
    private const int CompHaveXYScale = 0x0040;
    private const int CompHaveTwoByTwo = 0x0080;
    private const int CompScaledOffset = 0x0800;

    /// <summary>Composite nesting limit — also the cycle guard (a self-referencing
    /// component chain would otherwise recurse forever).</summary>
    private const int MaxCompositeDepth = 8;

    private readonly byte[] _data;
    private readonly IReadOnlyDictionary<string, TableRecord> _tables;
    private readonly int[]? _loca;                // GlyphCount + 1 offsets into glyf (glyf fonts only)
    private readonly int _glyfOffset;
    private readonly int _glyfLength;
    private readonly CffOutlines? _cff;           // PostScript outlines (OTTO fonts only)
    private readonly Cff2Outlines? _cff2;         // variable PostScript outlines
    private readonly int[] _advanceWidths;        // font units, per glyph
    private readonly int[] _leftSideBearings;
    private readonly CharacterMap _cmap;
    private readonly Dictionary<(int Left, int Right), double>? _kerning;
    private readonly GposKerning? _gpos;
    private readonly FontVariations? _variations; // fvar + avar: the design space
    private readonly GlyphVariationStore? _gvar;  // glyf outline deltas
    private readonly MetricsVariations? _hvar;    // advance-width deltas
    private readonly double _declaredCapHeight;   // OS/2 sCapHeight, 0 when the font states none
    private readonly ConcurrentDictionary<int, Glyph> _glyphs = new();

    /// <summary>Normalized axis coordinates this instance draws at. EMPTY for a font
    /// nobody has varied — which is what makes an un-instanced read do no delta work at
    /// all, rather than doing it and finding every scalar zero.</summary>
    private readonly double[] _coordinates = [];

    /// <summary>User-unit settings behind <see cref="_coordinates"/>, clamped to each
    /// axis's own range (so the report is what was DRAWN, and re-applying it is a fixed
    /// point).</summary>
    private readonly IReadOnlyDictionary<string, double> _variationSettings =
        new Dictionary<string, double>(StringComparer.Ordinal);

    private TrueTypeFont(byte[] data, IReadOnlyDictionary<string, TableRecord> tables)
    {
        _data = data;
        _tables = tables;
        var span = data.AsSpan();

        // ---- head: em square and the loca index width ----
        var head = new FontReader(span, Table(tables, "head").Offset);
        head.Skip(18);
        UnitsPerEm = head.ReadUInt16();
        if (UnitsPerEm is < 16 or > 16384)
            throw new FontFormatException($"head.unitsPerEm is {UnitsPerEm}; the format allows 16..16384.");
        head.Skip(30);                                   // created/modified/bbox/macStyle/lowestRecPPEM/fontDirectionHint
        int indexToLocFormat = head.ReadInt16();
        bool hasGlyf = tables.ContainsKey("glyf") || (!tables.ContainsKey("CFF ") && !tables.ContainsKey("CFF2"));
        if (hasGlyf && indexToLocFormat is not (0 or 1))
            throw new FontFormatException($"head.indexToLocFormat is {indexToLocFormat}; expected 0 (short) or 1 (long).");

        // ---- maxp: glyph count ----
        var maxp = new FontReader(span, Table(tables, "maxp").Offset + 4);
        GlyphCount = maxp.ReadUInt16();
        if (GlyphCount == 0)
            throw new FontFormatException("maxp.numGlyphs is 0; the font contains no glyphs.");

        // ---- hhea + hmtx: vertical metrics and advance widths ----
        var hhea = new FontReader(span, Table(tables, "hhea").Offset + 4);
        Ascender = hhea.ReadInt16();
        Descender = hhea.ReadInt16();
        LineGap = hhea.ReadInt16();
        hhea.Skip(24);                                   // advanceWidthMax .. metricDataFormat
        int metricCount = hhea.ReadUInt16();
        if (metricCount == 0 || metricCount > GlyphCount)
            throw new FontFormatException(
                $"hhea.numberOfHMetrics is {metricCount}, which is not in 1..{GlyphCount} (maxp.numGlyphs).");
        (_advanceWidths, _leftSideBearings) = ReadHorizontalMetrics(span, Table(tables, "hmtx"), metricCount, GlyphCount);

        // ---- outlines: glyf/loca (quadratic) or CFF (cubic) ----
        if (hasGlyf)
        {
            _loca = ReadLoca(span, Table(tables, "loca"), indexToLocFormat, GlyphCount);
            var glyf = Table(tables, "glyf");
            _glyfOffset = glyf.Offset;
            _glyfLength = glyf.Length;
        }
        else if (tables.ContainsKey("CFF "))
        {
            var cff = Table(tables, "CFF ");
            _cff = CffOutlines.Read(data, cff.Offset, cff.Length);
            if (_cff.GlyphCount != GlyphCount)
                throw new FontFormatException(
                    $"CFF CharStrings holds {_cff.GlyphCount} glyphs but maxp.numGlyphs is {GlyphCount}.");
        }
        else
        {
            var cff2 = Table(tables, "CFF2");
            _cff2 = Cff2Outlines.Read(data, cff2.Offset, cff2.Length);
            if (_cff2.GlyphCount != GlyphCount)
                throw new FontFormatException(
                    $"CFF2 CharStrings holds {_cff2.GlyphCount} glyphs but maxp.numGlyphs is {GlyphCount}.");
        }

        // ---- cmap: character -> glyph index ----
        _cmap = CharacterMap.Read(span, Table(tables, "cmap").Offset);

        // ---- optional tables ----
        _gpos = tables.TryGetValue("GPOS", out var gpos) ? GposKerning.Read(span, gpos.Offset) : null;
        _kerning = tables.TryGetValue("kern", out var kern) ? ReadKerning(span, kern) : null;
        var names = tables.TryGetValue("name", out var name) ? ReadNames(span, name) : [];
        FamilyName = names.GetValueOrDefault(16) ?? names.GetValueOrDefault(1) ?? "";

        // ---- variations: the design space, then the deltas over it ----
        if (tables.TryGetValue("fvar", out var fvar))
        {
            _variations = FontVariations.Read(span, fvar.Offset, fvar.Length,
                tables.TryGetValue("avar", out var avar) ? avar.Offset : null,
                nameId => names.GetValueOrDefault(nameId) ?? "");
        }
        if (_variations is not null)
        {
            if (tables.TryGetValue("gvar", out var gvar))
                _gvar = GlyphVariationStore.Read(data, gvar.Offset, _variations.Axes.Count, GlyphCount);
            if (tables.TryGetValue("HVAR", out var hvar))
                _hvar = MetricsVariations.Read(span, hvar.Offset);
            _variationSettings = DefaultSettings(_variations);
        }

        _declaredCapHeight = ReadDeclaredCapHeight(span, tables);
        CapHeight = MeasureCapHeight();
    }

    /// <summary>Clones a loaded font at another point of its design space: every parsed
    /// table is SHARED (they are immutable), only the coordinate and the glyph cache are
    /// new. A varied font is a font — which is what lets every consumer stay
    /// untouched.</summary>
    private TrueTypeFont(TrueTypeFont source, double[] coordinates, IReadOnlyDictionary<string, double> settings)
    {
        _data = source._data;
        _loca = source._loca;
        _glyfOffset = source._glyfOffset;
        _glyfLength = source._glyfLength;
        _cff = source._cff;
        _cff2 = source._cff2;
        _advanceWidths = source._advanceWidths;
        _leftSideBearings = source._leftSideBearings;
        _cmap = source._cmap;
        _kerning = source._kerning;
        _gpos = source._gpos;
        _variations = source._variations;
        _gvar = source._gvar;
        _hvar = source._hvar;
        _declaredCapHeight = source._declaredCapHeight;
        UnitsPerEm = source.UnitsPerEm;
        GlyphCount = source.GlyphCount;
        Ascender = source.Ascender;
        Descender = source.Descender;
        LineGap = source.LineGap;
        FamilyName = source.FamilyName;

        _coordinates = coordinates;
        _variationSettings = settings;
        CapHeight = MeasureCapHeight();
    }

    // ---- public surface ------------------------------------------------------

    /// <summary>Reads a <c>.ttf</c> or <c>.otf</c> file from disk.</summary>
    public static TrueTypeFont Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            return Load(File.ReadAllBytes(path));
        }
        catch (FontFormatException error)
        {
            throw new FontFormatException($"{path}: {error.Message}");
        }
    }

    /// <summary>Reads a font from memory (the bytes are copied, so the caller may reuse
    /// the buffer).</summary>
    public static TrueTypeFont Load(ReadOnlySpan<byte> data) => new(data.ToArray(), ReadTableDirectory(data));

    /// <summary>Design units per em — the outline coordinate system (commonly 1000 or
    /// 2048). Divide glyph coordinates by this to get em fractions.</summary>
    public int UnitsPerEm { get; }

    /// <summary>Number of glyphs in the font.</summary>
    public int GlyphCount { get; }

    /// <summary>Typographic ascender in font units (<c>hhea</c>).</summary>
    public double Ascender { get; }

    /// <summary>Typographic descender in font units (negative, <c>hhea</c>).</summary>
    public double Descender { get; }

    /// <summary>Recommended extra leading between lines, font units (<c>hhea</c>).</summary>
    public double LineGap { get; }

    /// <summary>
    /// Height of a flat capital above the baseline, font units: <c>OS/2.sCapHeight</c>
    /// when the font provides it, otherwise measured from the outline of 'H' (or 'X'),
    /// otherwise <see cref="Ascender"/>. Useful because engineering drawings specify
    /// letter height, not em size — see <see cref="EmSizeForCapHeight"/>.
    /// </summary>
    public double CapHeight { get; }

    /// <summary>Font family name from the <c>name</c> table (diagnostics; empty when
    /// the table is absent or unreadable).</summary>
    public string FamilyName { get; }

    /// <summary>True when the font supplies pair kerning — a <c>kern</c> feature in
    /// <c>GPOS</c> (where modern fonts keep it) or a usable format-0 legacy
    /// <c>kern</c> table.</summary>
    public bool HasKerning => _gpos is not null || _kerning is not null;

    /// <summary>True when outlines come from a PostScript table — <c>CFF </c> or its
    /// variable-font successor <c>CFF2</c> (cubic Béziers, <c>OTTO</c>-flavoured
    /// <c>.otf</c>); false for TrueType <c>glyf</c> quadratics. Either way the outlines
    /// map onto <see cref="Sketch"/> segments exactly — see
    /// <see cref="GlyphContour.IsCubic"/>.</summary>
    public bool HasPostScriptOutlines => _cff is not null || _cff2 is not null;

    /// <summary>
    /// The em size that renders flat capitals <paramref name="capHeight"/> tall —
    /// <c>size × CapHeight / UnitsPerEm = capHeight</c>. EngrCAD sizes text by em (the
    /// typographic convention, see <see cref="Shape.Text(string, TrueTypeFont, double, double, SketchPlane?, TextStyle?)"/>);
    /// this converts a drawing's letter height into that size.
    /// </summary>
    public double EmSizeForCapHeight(double capHeight)
    {
        if (capHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(capHeight));
        return capHeight * UnitsPerEm / CapHeight;
    }

    /// <summary>Maps a Unicode code point to a glyph index; false when the font has no
    /// glyph for it (callers usually fall back to glyph 0, <c>.notdef</c>).</summary>
    public bool TryGetGlyphIndex(int codePoint, out int glyphIndex)
    {
        glyphIndex = _cmap.GlyphIndex(codePoint);
        return glyphIndex > 0 && glyphIndex < GlyphCount;
    }

    /// <summary>The outline of the glyph at <paramref name="glyphIndex"/> (cached).</summary>
    public Glyph GetGlyph(int glyphIndex)
    {
        if ((uint)glyphIndex >= (uint)GlyphCount)
            throw new ArgumentOutOfRangeException(nameof(glyphIndex), glyphIndex,
                $"The font has {GlyphCount} glyphs.");
        return LoadGlyph(glyphIndex, depth: 0);
    }

    /// <summary>The outline for a character; false when the font has no glyph for it.</summary>
    public bool TryGetGlyph(char character, out Glyph glyph) => TryGetGlyph((int)character, out glyph);

    /// <summary>The outline for a Unicode code point; false when the font has no glyph
    /// for it.</summary>
    public bool TryGetGlyph(int codePoint, out Glyph glyph)
    {
        if (TryGetGlyphIndex(codePoint, out int index))
        {
            glyph = GetGlyph(index);
            return true;
        }
        glyph = null!;
        return false;
    }

    // ---- variable fonts ------------------------------------------------------

    /// <summary>True when the font declares <c>fvar</c> axes, i.e. when
    /// <see cref="WithVariation(ValueTuple{string, double}[])"/> has somewhere to
    /// go.</summary>
    public bool IsVariable => _variations is not null;

    /// <summary>The font's design axes in their own order; empty for a static
    /// font.</summary>
    public IReadOnlyList<VariationAxis> VariationAxes => _variations?.Axes ?? [];

    /// <summary>The named points of the design space the font itself ships ("Bold",
    /// "Condensed"); empty for a static font.</summary>
    public IReadOnlyList<NamedInstance> NamedInstances => _variations?.Instances ?? [];

    /// <summary>
    /// The user-unit coordinate this font instance draws at, per axis tag — the axis
    /// defaults for a variable font nobody has varied, and empty for a static one.
    /// Values are as CLAMPED to their axis range, so this reports what was drawn and
    /// feeding it back to <see cref="WithVariation(IReadOnlyDictionary{string, double})"/>
    /// is a fixed point.
    /// </summary>
    public IReadOnlyDictionary<string, double> Variation => _variationSettings;

    /// <summary>
    /// This font at another point of its design space —
    /// <c>font.WithVariation(("wght", 700), ("wdth", 75))</c>. Axes the caller says
    /// nothing about take their own default; a value outside an axis's range is CLAMPED
    /// (the specification's own rule, so no coordinate can be asked for and fail to
    /// draw); an axis the font does not have is refused BY NAME.
    /// <para>The result is an ordinary <see cref="TrueTypeFont"/> sharing this one's
    /// parsed tables, so it costs one glyph cache and no re-parsing, and every consumer
    /// — <c>Shape.Text</c>, <c>Shape.TextOnPath</c>, <c>TextFeature</c>, the layout
    /// engine — takes it with no change at all.</para>
    /// </summary>
    public TrueTypeFont WithVariation(params (string Axis, double Value)[] settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var map = new Dictionary<string, double>(settings.Length, StringComparer.Ordinal);
        foreach (var (axis, value) in settings)
            map[axis ?? throw new ArgumentException("An axis tag is null.", nameof(settings))] = value;
        return WithVariation(map);
    }

    /// <inheritdoc cref="WithVariation(ValueTuple{string, double}[])"/>
    public TrueTypeFont WithVariation(IReadOnlyDictionary<string, double> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_variations is not { } variations)
            throw new FontFormatException(
                $"'{FamilyName}' is not a variable font: it declares no 'fvar' axes, so there is no design space to move in.");
        if (variations.AvarRefusal is { } refusal)
            throw new FontFormatException(refusal);

        var resolved = DefaultSettings(variations);
        foreach (var (tag, value) in settings)
        {
            int axis = variations.IndexOf(tag);
            if (axis < 0)
                throw new FontFormatException(
                    $"'{FamilyName}' has no '{tag}' axis; it carries {string.Join(", ", variations.Axes.Select(a => a.Tag))}.");
            if (!double.IsFinite(value))
                throw new FontFormatException($"Axis '{tag}' was given {value}, which is not a finite coordinate.");
            resolved[tag] = Math.Clamp(value, variations.Axes[axis].Minimum, variations.Axes[axis].Maximum);
        }
        return new TrueTypeFont(this, variations.Normalize(resolved), resolved);
    }

    /// <summary>
    /// This font at one of its own named instances — <c>font.WithNamedInstance("Bold")</c>.
    /// A named instance is a coordinate the caller could have typed, so this is sugar
    /// over <see cref="WithVariation(IReadOnlyDictionary{string, double})"/> and shares
    /// every one of its rules; an unknown name is refused listing what the font does
    /// carry.
    /// </summary>
    public TrueTypeFont WithNamedInstance(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        foreach (var instance in NamedInstances)
        {
            if (string.Equals(instance.Name, name, StringComparison.OrdinalIgnoreCase))
                return WithVariation(instance.Coordinates);
        }
        throw new FontFormatException(NamedInstances.Count == 0
            ? $"'{FamilyName}' declares no named instances."
            : $"'{FamilyName}' has no named instance '{name}'; it carries {string.Join(", ", NamedInstances.Select(i => i.Name))}.");
    }

    private static Dictionary<string, double> DefaultSettings(FontVariations variations)
    {
        var defaults = new Dictionary<string, double>(variations.Axes.Count, StringComparer.Ordinal);
        foreach (var axis in variations.Axes)
            defaults[axis.Tag] = axis.Default;
        return defaults;
    }

    /// <summary>Kerning adjustment between two glyphs in font units (negative pulls
    /// them together); 0 when the font kerns nothing for the pair. A <c>kern</c>
    /// feature in <c>GPOS</c> takes precedence over the legacy <c>kern</c> table —
    /// the OpenType rule: when both exist, the legacy table is ignored entirely, not
    /// merged (see <see cref="GposKerning"/> for what is read).</summary>
    public double KerningBetween(int leftGlyphIndex, int rightGlyphIndex)
    {
        if (_gpos is not null)
            return _gpos.Kerning(leftGlyphIndex, rightGlyphIndex);
        return _kerning is not null && _kerning.TryGetValue((leftGlyphIndex, rightGlyphIndex), out double value) ? value : 0;
    }

    // ---- raw tables (the PDF subset embedder's seam) -------------------------
    //
    // A subset font is assembled from tables this reader has ALREADY located, so the
    // table directory is exposed rather than parsed a second time — the recurring "ask
    // the one rule, never restate it" lesson: a second directory walk could disagree
    // with this one about where 'glyf' begins and nothing would catch it.

    /// <summary>A copy of one sfnt table's bytes, or null when the font has no such
    /// table. Used by <c>PdfFontSubset</c> to carry <c>head</c>, <c>hhea</c> and
    /// <c>maxp</c> across verbatim (with the few fields a subset must restate).</summary>
    internal byte[]? RawTable(string tag) =>
        _tables.TryGetValue(tag, out var record) ? _data[record.Offset..(record.Offset + record.Length)] : null;

    /// <summary>One glyph's raw <c>glyf</c> record — empty for a blank glyph (the
    /// format's own encoding, equal <c>loca</c> offsets). Null for a CFF font, which
    /// has no <c>glyf</c> table at all.</summary>
    internal byte[]? RawGlyph(int glyphIndex)
    {
        if (_loca is null)
            return null;
        int start = _loca[glyphIndex], end = _loca[glyphIndex + 1];
        return end <= start ? [] : _data[(_glyfOffset + start)..(_glyfOffset + end)];
    }

    /// <summary>Advance width of one glyph in font units (<c>hmtx</c>).</summary>
    internal int AdvanceWidthUnits(int glyphIndex) => _advanceWidths[glyphIndex];

    /// <summary>Left side bearing of one glyph in font units (<c>hmtx</c>).</summary>
    internal int LeftSideBearingUnits(int glyphIndex) => _leftSideBearings[glyphIndex];

    /// <summary>
    /// The glyphs a composite glyph places directly (empty for a simple or blank glyph).
    /// A subset must carry a composite's components or its outline is lost, and the
    /// component indices are read here — beside <see cref="ReadCompositeGlyph"/>, which
    /// owns the same record layout — rather than re-derived by a second parser.
    /// </summary>
    internal IReadOnlyList<int> CompositeComponents(int glyphIndex)
    {
        if (_loca is null)
            return [];
        int start = _loca[glyphIndex], end = _loca[glyphIndex + 1];
        if (end <= start || end > _glyfLength)
            return [];
        var reader = new FontReader(_data.AsSpan(_glyfOffset, _glyfLength), start);
        if (reader.ReadInt16() >= 0)
            return [];                                   // simple glyph: no components
        reader.Skip(8);                                  // bounding box
        var components = new List<int>();
        int flags;
        do
        {
            flags = reader.ReadUInt16();
            components.Add(reader.ReadUInt16());
            reader.Skip((flags & CompArgsAreWords) != 0 ? 4 : 2);
            if ((flags & CompHaveScale) != 0)
                reader.Skip(2);
            else if ((flags & CompHaveXYScale) != 0)
                reader.Skip(4);
            else if ((flags & CompHaveTwoByTwo) != 0)
                reader.Skip(8);
        }
        while ((flags & CompMoreComponents) != 0);
        return components;
    }

    // ---- table directory -----------------------------------------------------

    private readonly record struct TableRecord(int Offset, int Length);

    private static Dictionary<string, TableRecord> ReadTableDirectory(ReadOnlySpan<byte> data)
    {
        var reader = new FontReader(data);
        uint version = reader.ReadUInt32();
        switch (version)
        {
            case 0x00010000:                             // TrueType outlines
            case 0x74727565:                             // 'true' (legacy Apple)
            case 0x4F54544F:                             // 'OTTO': OpenType with PostScript (CFF) outlines
                break;
            case 0x74746366:                             // 'ttcf'
                throw new FontFormatException(
                    "This is a TrueType Collection (.ttc). Extract the individual font you want; " +
                    "collections are not supported.");
            default:
                throw new FontFormatException(
                    $"Not a TrueType font: sfnt version 0x{version:X8} (expected 0x00010000).");
        }

        int tableCount = reader.ReadUInt16();
        reader.Skip(6);                                  // searchRange / entrySelector / rangeShift
        var tables = new Dictionary<string, TableRecord>(tableCount, StringComparer.Ordinal);
        for (int i = 0; i < tableCount; i++)
        {
            string tag = reader.ReadTag();
            reader.Skip(4);                              // checkSum
            int offset = (int)reader.ReadUInt32();
            int length = (int)reader.ReadUInt32();
            if (offset < 0 || length < 0 || (long)offset + length > data.Length)
                throw new FontFormatException(
                    $"Table '{tag}' (offset {offset}, length {length}) runs past the {data.Length}-byte file.");
            tables[tag] = new TableRecord(offset, length);
        }

        if (!tables.ContainsKey("glyf") && !tables.ContainsKey("CFF ") && !tables.ContainsKey("CFF2"))
            throw new FontFormatException(
                "The font has no 'glyf', 'CFF ' or 'CFF2' table; there are no outlines to read.");
        return tables;
    }

    private static TableRecord Table(IReadOnlyDictionary<string, TableRecord> tables, string tag) =>
        tables.TryGetValue(tag, out var record)
            ? record
            : throw new FontFormatException($"Required table '{tag}' is missing.");

    // ---- metric / offset tables ---------------------------------------------

    private static (int[] Advances, int[] Bearings) ReadHorizontalMetrics(
        ReadOnlySpan<byte> data, TableRecord hmtx, int metricCount, int glyphCount)
    {
        var advances = new int[glyphCount];
        var bearings = new int[glyphCount];
        var reader = new FontReader(data, hmtx.Offset);
        int lastAdvance = 0;
        for (int i = 0; i < metricCount; i++)
        {
            lastAdvance = reader.ReadUInt16();
            advances[i] = lastAdvance;
            bearings[i] = reader.ReadInt16();
        }
        // Monospaced tails: the remaining glyphs repeat the last advance and carry only
        // their own left side bearing.
        for (int i = metricCount; i < glyphCount; i++)
        {
            advances[i] = lastAdvance;
            bearings[i] = reader.ReadInt16();
        }
        return (advances, bearings);
    }

    private static int[] ReadLoca(ReadOnlySpan<byte> data, TableRecord loca, int indexToLocFormat, int glyphCount)
    {
        int entries = glyphCount + 1;
        int needed = indexToLocFormat == 0 ? entries * 2 : entries * 4;
        if (loca.Length < needed)
            throw new FontFormatException(
                $"loca is {loca.Length} bytes but {entries} entries in format {indexToLocFormat} need {needed}.");

        var offsets = new int[entries];
        var reader = new FontReader(data, loca.Offset);
        for (int i = 0; i < entries; i++)
            offsets[i] = indexToLocFormat == 0 ? reader.ReadUInt16() * 2 : (int)reader.ReadUInt32();
        return offsets;
    }

    // ---- glyf ----------------------------------------------------------------

    private Glyph LoadGlyph(int index, int depth)
    {
        if (_glyphs.TryGetValue(index, out var cached))
            return cached;
        var glyph = ReadGlyph(index, depth);
        _glyphs[index] = glyph;
        return glyph;
    }

    /// <summary>Left side bearing, advance width, top side bearing, advance height —
    /// the four points every glyph's point list is extended by so that a variable font
    /// can vary its METRICS with the machinery it varies its outline with.</summary>
    private const int PhantomPointCount = 4;

    private Glyph ReadGlyph(int index, int depth)
    {
        double advance = _advanceWidths[index];
        double bearing = _leftSideBearings[index];
        if (_cff2 is not null)
            return new Glyph(index, _cff2.ReadGlyph(index, _coordinates), advance + MetricsDelta(index), bearing);
        if (_cff is not null)
            return new Glyph(index, _cff.ReadGlyph(index), advance + MetricsDelta(index), bearing);

        int start = _loca![index], end = _loca[index + 1];

        // Equal offsets mean "no outline" — space and other blank glyphs. This is the
        // format's own encoding, not an error. A blank glyph still carries the four
        // phantom points, so its ADVANCE can vary even though nothing is drawn.
        if (end <= start)
            return new Glyph(index, [], advance + VaryPoints(index, new Vector2d[PhantomPointCount], []), bearing);
        if (end > _glyfLength)
            throw new FontFormatException(
                $"loca entry for glyph {index} ends at {end}, past the {_glyfLength}-byte glyf table.");

        var reader = new FontReader(_data.AsSpan(_glyfOffset, _glyfLength), start);
        int contourCount = reader.ReadInt16();
        reader.Skip(8);                                  // xMin/yMin/xMax/yMax — recomputed from points
        return contourCount >= 0
            ? ReadSimpleGlyph(ref reader, index, contourCount, advance, bearing)
            : ReadCompositeGlyph(ref reader, index, depth, advance, bearing);
    }

    /// <summary>The advance-width delta for a glyph whose outline carries no phantom
    /// points of its own (a PostScript one), which is <c>HVAR</c>'s job alone.</summary>
    private double MetricsDelta(int index) =>
        _coordinates.Length > 0 && _hvar is not null ? _hvar.AdvanceDelta(index, _coordinates) : 0;

    /// <summary>
    /// Moves a glyph's points to the current instance, and reports how far its advance
    /// width moves with them. <paramref name="points"/> carries the glyph's own points
    /// followed by <see cref="PhantomPointCount"/> phantoms (whose coordinates are never
    /// read — each is its own one-point contour); only the glyph's own points are
    /// written back.
    /// </summary>
    private double VaryPoints(int index, Vector2d[] points, IReadOnlyList<int> contourEnds)
    {
        if (_coordinates.Length == 0)
            return 0;

        int own = points.Length - PhantomPointCount;
        double phantomAdvance = 0;
        if (_gvar?.Deltas(index, points, contourEnds, _coordinates) is { } deltas)
        {
            for (int i = 0; i < own; i++)
                points[i] += deltas[i];
            // The first phantom is the left side bearing point and the second the
            // advance-width point, so the advance moves by exactly their difference.
            phantomAdvance = deltas[own + 1].X - deltas[own].X;
        }
        // HVAR SUPERSEDES the phantom points where a font carries it, and that is not a
        // preference: a font with HVAR is entitled to omit phantom deltas from gvar
        // altogether, which is the whole reason the table exists.
        return _hvar is not null ? _hvar.AdvanceDelta(index, _coordinates) : phantomAdvance;
    }

    private Glyph ReadSimpleGlyph(
        ref FontReader reader, int index, int contourCount, double advance, double bearing)
    {
        if (contourCount == 0)
            return new Glyph(index, [], advance + VaryPoints(index, new Vector2d[PhantomPointCount], []), bearing);

        var endPoints = new int[contourCount];
        for (int i = 0; i < contourCount; i++)
        {
            endPoints[i] = reader.ReadUInt16();
            if (i > 0 && endPoints[i] < endPoints[i - 1])
                throw new FontFormatException("glyf endPtsOfContours is not non-decreasing.");
        }
        int pointCount = endPoints[^1] + 1;
        reader.Skip(reader.ReadUInt16());                // hinting instructions

        // Flags, with the format's run-length compression (REPEAT_FLAG + a count byte).
        var flags = new byte[pointCount];
        for (int i = 0; i < pointCount;)
        {
            byte flag = reader.ReadUInt8();
            flags[i++] = flag;
            if ((flag & FlagRepeat) == 0)
                continue;
            int repeat = reader.ReadUInt8();
            if (repeat > pointCount - i)
                throw new FontFormatException(
                    $"glyf flag repeat of {repeat} overruns the {pointCount}-point contour set at point {i}.");
            for (int k = 0; k < repeat; k++)
                flags[i++] = flag;
        }

        // Coordinates are stored as deltas: one byte with an explicit sign bit, two
        // bytes signed, or "same as previous" (delta 0) — the pairing of the SHORT and
        // SAME_OR_POSITIVE bits decides which.
        var xs = ReadCoordinates(ref reader, flags, FlagXShort, FlagXSameOrPositive);
        var ys = ReadCoordinates(ref reader, flags, FlagYShort, FlagYSameOrPositive);

        var outline = new Vector2d[pointCount + PhantomPointCount];
        for (int i = 0; i < pointCount; i++)
            outline[i] = new Vector2d(xs[i], ys[i]);
        advance += VaryPoints(index, outline, endPoints);

        var contours = new List<GlyphContour>(contourCount);
        int from = 0;
        foreach (int last in endPoints)
        {
            int count = last - from + 1;
            if (count <= 0)
            {
                from = last + 1;
                continue;                                // empty contour: skip, not an error
            }
            var points = new GlyphPoint[count];
            for (int i = 0; i < count; i++)
                points[i] = new GlyphPoint(outline[from + i], (flags[from + i] & FlagOnCurve) != 0);
            contours.Add(new GlyphContour(points));
            from = last + 1;
        }
        return new Glyph(index, contours, advance, bearing);
    }

    private static int[] ReadCoordinates(ref FontReader reader, byte[] flags, byte shortBit, byte sameBit)
    {
        var values = new int[flags.Length];
        int value = 0;
        for (int i = 0; i < flags.Length; i++)
        {
            byte flag = flags[i];
            if ((flag & shortBit) != 0)
            {
                int delta = reader.ReadUInt8();
                value += (flag & sameBit) != 0 ? delta : -delta;
            }
            else if ((flag & sameBit) == 0)
            {
                value += reader.ReadInt16();
            }
            values[i] = value;
        }
        return values;
    }

    /// <summary>One placement inside a composite glyph, with its offset kept RAW — a
    /// variable font varies exactly that offset, and Apple's SCALED_COMPONENT_OFFSET
    /// puts it through the 2x2 afterwards.</summary>
    private readonly record struct Component(
        int Glyph, double Dx, double Dy, double A, double B, double C, double D, bool ScaledOffset);

    private Glyph ReadCompositeGlyph(
        ref FontReader reader, int index, int depth, double advance, double bearing)
    {
        if (depth >= MaxCompositeDepth)
            throw new FontFormatException(
                $"Composite glyph {index} nests deeper than {MaxCompositeDepth} levels (cyclic component reference?).");

        var components = new List<Component>();
        int flags;
        do
        {
            flags = reader.ReadUInt16();
            int component = reader.ReadUInt16();
            if ((flags & CompArgsAreXy) == 0)
                throw new FontFormatException(
                    $"Composite glyph {index} places component {component} by point matching " +
                    "(ARGS_ARE_XY_VALUES clear), which is not supported.");

            double dx, dy;
            if ((flags & CompArgsAreWords) != 0)
            {
                dx = reader.ReadInt16();
                dy = reader.ReadInt16();
            }
            else
            {
                dx = reader.ReadInt8();
                dy = reader.ReadInt8();
            }

            // Component transform [a b; c d] in font order (xscale, scale01, scale10, yscale):
            // x' = a·x + c·y + dx, y' = b·x + d·y + dy.
            double a = 1, b = 0, c = 0, d = 1;
            if ((flags & CompHaveScale) != 0)
            {
                a = d = reader.ReadF2Dot14();
            }
            else if ((flags & CompHaveXYScale) != 0)
            {
                a = reader.ReadF2Dot14();
                d = reader.ReadF2Dot14();
            }
            else if ((flags & CompHaveTwoByTwo) != 0)
            {
                a = reader.ReadF2Dot14();
                b = reader.ReadF2Dot14();
                c = reader.ReadF2Dot14();
                d = reader.ReadF2Dot14();
            }

            if (component == index)
                throw new FontFormatException($"Composite glyph {index} references itself.");
            if ((uint)component >= (uint)GlyphCount)
                throw new FontFormatException($"Composite glyph {index} references glyph {component}, past the {GlyphCount}-glyph font.");

            components.Add(new Component(component, dx, dy, a, b, c, d, (flags & CompScaledOffset) != 0));
        }
        while ((flags & CompMoreComponents) != 0);

        // A composite has no outline of its own, so what a variable font varies is the
        // COMPONENT OFFSETS: one point per component, each its own one-point contour, so
        // a component no tuple names simply does not move.
        var offsets = new Vector2d[components.Count + PhantomPointCount];
        var ends = new int[components.Count];
        for (int i = 0; i < components.Count; i++)
        {
            offsets[i] = new Vector2d(components[i].Dx, components[i].Dy);
            ends[i] = i;
        }
        advance += VaryPoints(index, offsets, ends);

        var contours = new List<GlyphContour>();
        for (int k = 0; k < components.Count; k++)
        {
            var (component, _, _, a, b, c, d, scaledOffset) = components[k];
            double dx = offsets[k].X, dy = offsets[k].Y;
            // Microsoft's default is an UNSCALED offset; only SCALED_COMPONENT_OFFSET
            // (Apple's convention) puts the offset through the 2x2.
            if (scaledOffset)
                (dx, dy) = (a * dx + c * dy, b * dx + d * dy);

            foreach (var contour in LoadGlyph(component, depth + 1).Contours)
            {
                var source = contour.Points;
                var points = new GlyphPoint[source.Count];
                for (int i = 0; i < source.Count; i++)
                {
                    var p = source[i].Position;
                    points[i] = new GlyphPoint(new Vector2d(a * p.X + c * p.Y + dx, b * p.X + d * p.Y + dy), source[i].OnCurve);
                }
                contours.Add(new GlyphContour(points));
            }
        }
        return new Glyph(index, contours, advance, bearing);
    }

    // ---- optional tables -----------------------------------------------------

    private static Dictionary<(int, int), double>? ReadKerning(ReadOnlySpan<byte> data, TableRecord kern)
    {
        var reader = new FontReader(data, kern.Offset);
        if (reader.ReadUInt16() != 0)
            return null;                                 // Apple's version-1.0 kern table: skipped, not an error
        int subtableCount = reader.ReadUInt16();
        var pairs = new Dictionary<(int, int), double>();

        int at = kern.Offset + 4;
        for (int t = 0; t < subtableCount; t++)
        {
            var sub = new FontReader(data, at);
            sub.Skip(2);                                 // subtable version
            int length = sub.ReadUInt16();
            int coverage = sub.ReadUInt16();
            if (length < 6)
                throw new FontFormatException($"kern subtable {t} declares length {length}; the header alone is 6 bytes.");

            // coverage: low byte = flags (bit0 horizontal, bit1 minimum, bit2 cross-stream,
            // bit3 override), high byte = format. Only plain horizontal format-0 pair
            // kerning is meaningful for laid-out outlines; the rest is skipped.
            bool usable = (coverage >> 8) == 0 && (coverage & 0b0111) == 0b0001;
            if (usable)
            {
                int pairCount = sub.ReadUInt16();
                sub.Skip(6);                             // searchRange / entrySelector / rangeShift
                for (int i = 0; i < pairCount; i++)
                {
                    int left = sub.ReadUInt16();
                    int right = sub.ReadUInt16();
                    pairs[(left, right)] = sub.ReadInt16();
                }
            }
            at += length;
        }
        return pairs.Count == 0 ? null : pairs;
    }

    /// <summary>Every <c>name</c>-table string by id, preferring the Windows platform's
    /// record and keeping the first of equal preference. The family name reads through
    /// it (id 16 first, then id 1) and so do the variation axis and instance names —
    /// one rule, so a font naming its axes in one platform's records and its family in
    /// another is read the same way for both.</summary>
    private static Dictionary<int, string> ReadNames(ReadOnlySpan<byte> data, TableRecord name)
    {
        var reader = new FontReader(data, name.Offset);
        reader.Skip(2);                                  // format
        int count = reader.ReadUInt16();
        int stringOffset = reader.ReadUInt16();

        var best = new Dictionary<int, string>();
        var bestScore = new Dictionary<int, int>();
        for (int i = 0; i < count; i++)
        {
            int platform = reader.ReadUInt16();
            reader.Skip(4);                              // encodingID / languageID
            int id = reader.ReadUInt16();
            int length = reader.ReadUInt16();
            int offset = reader.ReadUInt16();

            int score = platform == 3 ? 1 : 0;
            if (bestScore.TryGetValue(id, out int previous) && score <= previous)
                continue;
            int at = name.Offset + stringOffset + offset;
            if (at < 0 || length < 0 || at + length > data.Length)
                continue;                                // ignore a bad record rather than fail the whole load
            var bytes = data.Slice(at, length);
            // Windows (3) and Unicode (0) name strings are UTF-16BE; Macintosh (1) is
            // MacRoman, which agrees with Latin-1 over the ASCII range names use.
            string text = platform is 3 or 0 ? Encoding.BigEndianUnicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
            if (text.Length == 0)
                continue;
            best[id] = text;
            bestScore[id] = score;
        }
        return best;
    }

    /// <summary>OS/2 version 2 added sCapHeight at offset 88; 0 means the font states
    /// none. <c>MVAR</c> would vary it and is not read, so a DECLARED cap height is the
    /// default instance's whatever a font is varied to — while a MEASURED one follows
    /// the outline it was measured from.</summary>
    private static double ReadDeclaredCapHeight(
        ReadOnlySpan<byte> data, IReadOnlyDictionary<string, TableRecord> tables)
    {
        if (!tables.TryGetValue("OS/2", out var os2) || os2.Length < 90)
            return 0;
        var reader = new FontReader(data, os2.Offset);
        if (reader.ReadUInt16() < 2)
            return 0;
        reader.Position = os2.Offset + 88;
        double declared = reader.ReadInt16();
        return declared > 0 ? declared : 0;
    }

    private double MeasureCapHeight()
    {
        if (_declaredCapHeight > 0)
            return _declaredCapHeight;
        foreach (char probe in "HX")
        {
            if (TryGetGlyph(probe, out var glyph) && !glyph.IsEmpty && glyph.Bounds.Max.Y > 0)
                return glyph.Bounds.Max.Y;
        }
        return Ascender;
    }

    // ---- cmap ----------------------------------------------------------------

    /// <summary>Character code to glyph index. Formats 4 (BMP) and 12 (full Unicode)
    /// cover every modern font; the rest are legacy Mac encodings.</summary>
    private abstract class CharacterMap
    {
        public abstract int GlyphIndex(int codePoint);

        public static CharacterMap Read(ReadOnlySpan<byte> data, int offset)
        {
            var reader = new FontReader(data, offset);
            reader.Skip(2);                              // version
            int count = reader.ReadUInt16();

            var candidates = new List<(int Score, int Offset)>(count);
            for (int i = 0; i < count; i++)
            {
                int platform = reader.ReadUInt16();
                int encoding = reader.ReadUInt16();
                int subtable = offset + (int)reader.ReadUInt32();
                int score = (platform, encoding) switch
                {
                    (3, 10) => 5,                        // Windows, UCS-4
                    (0, 4) or (0, 6) => 4,               // Unicode, full repertoire
                    (3, 1) => 3,                         // Windows, BMP
                    (0, _) => 2,                         // Unicode, BMP
                    (3, 0) => 1,                         // Windows symbol (F000 plane)
                    _ => 0,
                };
                if (score > 0)
                    candidates.Add((score, subtable));
            }
            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

            var formatsSeen = new List<int>();
            foreach (var (score, subtable) in candidates)
            {
                var sub = new FontReader(data, subtable);
                int format = sub.ReadUInt16();
                switch (format)
                {
                    case 4:
                        return new Format4Map(data, subtable, symbol: score == 1);
                    case 12:
                        return new Format12Map(data, subtable);
                    default:
                        formatsSeen.Add(format);
                        break;
                }
            }
            throw new FontFormatException(
                formatsSeen.Count == 0
                    ? "cmap has no Unicode or Windows subtable; the font cannot be addressed by character."
                    : $"cmap has no format 4 or 12 subtable (found format(s) {string.Join(", ", formatsSeen.Distinct())}).");
        }
    }

    /// <summary>Format 4: segmented ranges over the Basic Multilingual Plane.</summary>
    private sealed class Format4Map : CharacterMap
    {
        private readonly int[] _end, _start, _delta, _rangeOffset;
        private readonly int[] _glyphIds;
        private readonly bool _symbol;

        public Format4Map(ReadOnlySpan<byte> data, int offset, bool symbol)
        {
            _symbol = symbol;
            var reader = new FontReader(data, offset);
            reader.Skip(2);                              // format
            int length = reader.ReadUInt16();
            reader.Skip(2);                              // language
            int segments = reader.ReadUInt16() / 2;
            if (segments == 0)
                throw new FontFormatException("cmap format 4 declares zero segments.");
            reader.Skip(6);                              // searchRange / entrySelector / rangeShift

            _end = ReadArray(ref reader, segments);
            reader.Skip(2);                              // reservedPad
            _start = ReadArray(ref reader, segments);
            _delta = ReadArray(ref reader, segments);
            _rangeOffset = ReadArray(ref reader, segments);

            // The trailing glyph id array is whatever is left of the subtable; the
            // idRangeOffset values are byte offsets INTO it measured from their own slot.
            int remaining = Math.Max(0, Math.Min(offset + length, data.Length) - reader.Position);
            _glyphIds = ReadArray(ref reader, remaining / 2);
        }

        private static int[] ReadArray(ref FontReader reader, int count)
        {
            var values = new int[count];
            for (int i = 0; i < count; i++)
                values[i] = reader.ReadUInt16();
            return values;
        }

        public override int GlyphIndex(int codePoint)
        {
            int glyph = Lookup(codePoint);
            // Symbol fonts map their glyphs into the private-use F000 block.
            if (glyph == 0 && _symbol && codePoint is >= 0x20 and <= 0xFF)
                glyph = Lookup(0xF000 | codePoint);
            return glyph;
        }

        private int Lookup(int codePoint)
        {
            if (codePoint is < 0 or > 0xFFFF)
                return 0;
            for (int i = 0; i < _end.Length; i++)
            {
                if (codePoint > _end[i])
                    continue;
                if (codePoint < _start[i])
                    return 0;
                if (_rangeOffset[i] == 0)
                    return (codePoint + _delta[i]) & 0xFFFF;
                // glyphIdArray index = idRangeOffset/2 + (c - startCode) - (segCount - i):
                // the offset is relative to the slot's own address, and the array begins
                // segCount slots later.
                int index = _rangeOffset[i] / 2 + (codePoint - _start[i]) - (_end.Length - i);
                if ((uint)index >= (uint)_glyphIds.Length)
                    return 0;
                int glyph = _glyphIds[index];
                return glyph == 0 ? 0 : (glyph + _delta[i]) & 0xFFFF;
            }
            return 0;
        }
    }

    /// <summary>Format 12: sorted 32-bit code ranges (full Unicode, including
    /// astral-plane code points).</summary>
    private sealed class Format12Map : CharacterMap
    {
        private readonly uint[] _startCode, _endCode, _startGlyph;

        public Format12Map(ReadOnlySpan<byte> data, int offset)
        {
            var reader = new FontReader(data, offset + 12);
            uint declared = reader.ReadUInt32();
            if (declared > (uint)((data.Length - reader.Position) / 12))
                throw new FontFormatException($"cmap format 12 declares {declared} groups, more than the subtable holds.");
            int groups = (int)declared;
            _startCode = new uint[groups];
            _endCode = new uint[groups];
            _startGlyph = new uint[groups];
            for (int i = 0; i < groups; i++)
            {
                _startCode[i] = reader.ReadUInt32();
                _endCode[i] = reader.ReadUInt32();
                _startGlyph[i] = reader.ReadUInt32();
            }
        }

        public override int GlyphIndex(int codePoint)
        {
            if (codePoint < 0)
                return 0;
            uint code = (uint)codePoint;
            int low = 0, high = _startCode.Length - 1;
            while (low <= high)
            {
                int mid = (low + high) / 2;
                if (code < _startCode[mid])
                    high = mid - 1;
                else if (code > _endCode[mid])
                    low = mid + 1;
                else
                    return (int)(_startGlyph[mid] + (code - _startCode[mid]));
            }
            return 0;
        }
    }
}
