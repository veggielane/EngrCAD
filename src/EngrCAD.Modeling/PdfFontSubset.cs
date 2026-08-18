namespace EngrCAD.Modeling;

/// <summary>
/// A TrueType font cut down to the glyphs a drawing actually uses, assembled as an sfnt
/// a PDF <c>FontFile2</c> stream carries.
///
/// <para><b>It is a deterministic function of the glyph set, and that is a requirement
/// rather than a nicety.</b> The PDF writer's defining property is that writing the same
/// sheet twice produces the same bytes, so an embedded font must too: the used glyphs
/// are visited in ascending index order, the tables are emitted in the sfnt's own sorted
/// tag order, and <c>head</c>'s <c>created</c> and <c>modified</c> dates — the /Info
/// problem wearing a font's clothes — are ZEROED rather than carried, since a font's own
/// timestamps are exactly the fields that would make two identical subsets differ.
/// (<c>checkSumAdjustment</c> IS computed, because it is a function of the subset's own
/// bytes and therefore deterministic.)</para>
///
/// <para><b>Glyph indices are kept, not renumbered.</b> A composite glyph places its
/// components BY INDEX, so renumbering would mean rewriting every composite record's
/// component fields — a second parse of the format, and the one place a subsetter
/// silently corrupts an accented glyph. Keeping the indices makes the composite records
/// carry over verbatim and makes the PDF side trivial too (<c>/CIDToGIDMap /Identity</c>:
/// the CID a text string carries IS the glyph index). The cost is stated rather than
/// hidden: <c>loca</c> and <c>hmtx</c> are sized by the LARGEST used glyph index rather
/// than by the count, so a subset reaching one high glyph pays a few kilobytes of table
/// for it. Renumbering with component patching is filed.</para>
///
/// <para>Composites are closed over: asking for 'Á' keeps the 'A' and the acute it
/// places, transitively, or the glyph would arrive empty.</para>
///
/// <para><b>CFF (OpenType/.otf) fonts are refused by name.</b> Their outlines live in a
/// <c>CFF </c> table whose subsetting means re-indexing charstrings, local and global
/// subroutines and (for CID-keyed fonts) FDArray/FDSelect — a separate project, and a
/// second embedding path in the PDF (<c>FontFile3</c>). A refusal naming the font beats
/// a plausible-looking font program a reader renders as blanks.</para>
/// </summary>
internal static class PdfFontSubset
{
    /// <summary>The sfnt tables the subset carries. A PDF CIDFontType2 never consults
    /// the font program's own <c>cmap</c> — glyph selection goes through
    /// <c>/CIDToGIDMap</c> (PDF 32000-1 §9.7.4.2) — but one is emitted anyway, for a
    /// reason that is a verification argument rather than a formatting one: WITH it the
    /// subset is a standalone TrueType font, so the kernel's own font READER can re-read
    /// it and every kept glyph's outline can be compared against the original's. A font
    /// program nothing can decode is a font program nothing can check.</summary>
    private static readonly string[] RequiredTables = ["cmap", "head", "hhea", "hmtx", "loca", "maxp"];

    /// <summary>The subset font's bytes plus the facts the PDF font dictionaries state.</summary>
    /// <param name="Data">The sfnt program for <c>FontFile2</c>.</param>
    /// <param name="Glyphs">The kept glyph indices, ascending (composite closure included).</param>
    /// <param name="MaxGlyph">The largest kept index; <c>loca</c>/<c>hmtx</c> run to it.</param>
    internal sealed record Result(byte[] Data, IReadOnlyList<int> Glyphs, int MaxGlyph);

    /// <summary>
    /// Builds the subset. <paramref name="characters"/> maps the code points the drawing
    /// used to their glyphs; it need not be sorted or unique and need not include
    /// composite components — both are resolved here, so the result is a function of the
    /// SET rather than of the order a caller happened to meet them in.
    /// </summary>
    internal static Result Build(TrueTypeFont font, IEnumerable<(int Code, int Glyph)> characters)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(characters);
        var codes = new SortedDictionary<int, int>();
        foreach (var (code, glyph) in characters)
        {
            if (code is >= 0 and <= 0xFFFF)
                codes[code] = glyph;   // last wins, deterministic in the caller's own order
        }
        var glyphs = codes.Values;
        if (font.HasPostScriptOutlines)
        {
            throw new NotSupportedException(
                $"'{Describe(font)}' stores its outlines in a PostScript 'CFF ' table, and only " +
                "TrueType 'glyf' outlines can be subset for embedding here (a CFF subset is a " +
                "separate FontFile3 path — re-indexed charstrings, subroutines and FDSelect). " +
                "Use a .ttf, or the built-in Helvetica.");
        }

        // Glyph 0 (.notdef) is always present: a PDF reader may show it for a CID the
        // subset does not carry, and a font without it is malformed.
        var kept = new SortedSet<int> { 0 };
        var pending = new Stack<int>();
        foreach (int glyph in glyphs)
        {
            if ((uint)glyph >= (uint)font.GlyphCount)
                throw new ArgumentOutOfRangeException(nameof(glyphs), glyph,
                    $"The font has {font.GlyphCount} glyphs.");
            if (kept.Add(glyph))
                pending.Push(glyph);
        }
        while (pending.Count > 0)
        {
            foreach (int component in font.CompositeComponents(pending.Pop()))
            {
                if (kept.Add(component))
                    pending.Push(component);
            }
        }

        int maxGlyph = kept.Max;
        int count = maxGlyph + 1;

        // ---- glyf + loca: kept glyphs verbatim, everything else a zero-length entry --
        var glyf = new List<byte>();
        var loca = new int[count + 1];
        for (int i = 0; i < count; i++)
        {
            loca[i] = glyf.Count;
            if (kept.Contains(i))
            {
                glyf.AddRange(font.RawGlyph(i) ?? []);
                while (glyf.Count % 4 != 0)
                    glyf.Add(0);
            }
        }
        loca[count] = glyf.Count;

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["cmap"] = Cmap(codes),
            ["glyf"] = [.. glyf],
            ["loca"] = LongLoca(loca),
            ["head"] = Head(font),
            ["hhea"] = Hhea(font, count),
            ["hmtx"] = Hmtx(font, count),
            ["maxp"] = Maxp(font, count),
        };
        foreach (string tag in RequiredTables)
        {
            if (!tables.ContainsKey(tag))
                throw new NotSupportedException($"'{Describe(font)}' has no '{tag}' table, so it cannot be embedded.");
        }

        return new Result(Assemble(tables), [.. kept], maxGlyph);
    }

    private static string Describe(TrueTypeFont font) =>
        font.FamilyName.Length > 0 ? font.FamilyName : "the font";

    // ------------------------------------------------------------------ tables

    /// <summary>
    /// A Windows/BMP <c>cmap</c> in format 4: one single-code segment per kept character
    /// plus the format's mandatory 0xFFFF terminator, with <c>idRangeOffset</c> zero so
    /// <c>idDelta</c> carries the mapping. Written in ascending code order (the codes
    /// arrive sorted), so the table is a function of the character set.
    /// </summary>
    private static byte[] Cmap(SortedDictionary<int, int> codes)
    {
        int segments = codes.Count + 1;
        int entrySelector = (int)Math.Floor(Math.Log2(segments));
        int searchRange = 2 * (1 << entrySelector);

        var subtable = new byte[16 + segments * 8];
        WriteU16(subtable, 0, 4);                        // format
        WriteU16(subtable, 2, subtable.Length);
        WriteU16(subtable, 4, 0);                        // language
        WriteU16(subtable, 6, segments * 2);
        WriteU16(subtable, 8, searchRange);
        WriteU16(subtable, 10, entrySelector);
        WriteU16(subtable, 12, segments * 2 - searchRange);

        int end = 14, start = end + segments * 2 + 2, delta = start + segments * 2;
        int i = 0;
        foreach (var (code, glyph) in codes)
        {
            WriteU16(subtable, end + i * 2, code);
            WriteU16(subtable, start + i * 2, code);
            WriteU16(subtable, delta + i * 2, (glyph - code) & 0xFFFF);
            i++;
        }
        WriteU16(subtable, end + i * 2, 0xFFFF);
        WriteU16(subtable, start + i * 2, 0xFFFF);
        WriteU16(subtable, delta + i * 2, 1);            // 0xFFFF + 1 wraps to glyph 0
        // idRangeOffset stays zero for every segment (the array is already zeroed).

        var cmap = new byte[12 + subtable.Length];
        WriteU16(cmap, 0, 0);                            // version
        WriteU16(cmap, 2, 1);                            // one encoding record
        WriteU16(cmap, 4, 3);                            // Windows
        WriteU16(cmap, 6, 1);                            // BMP
        WriteU32(cmap, 8, 12);
        subtable.CopyTo(cmap, 12);
        return cmap;
    }

    private static byte[] LongLoca(int[] offsets)
    {
        var bytes = new byte[offsets.Length * 4];
        for (int i = 0; i < offsets.Length; i++)
            WriteU32(bytes, i * 4, (uint)offsets[i]);
        return bytes;
    }

    /// <summary>
    /// <c>head</c> carried across with three fields restated: the checksum adjustment
    /// (recomputed over the finished file), the two date stamps (ZEROED — the fixed
    /// point), and <c>indexToLocFormat</c> (1, since the subset always writes long
    /// <c>loca</c>). Everything else — units per em, the bounding box, the flags — is
    /// the original font's and is what the PDF font descriptor then reads.
    /// </summary>
    private static byte[] Head(TrueTypeFont font)
    {
        var head = font.RawTable("head") ?? throw new NotSupportedException(
            $"'{Describe(font)}' has no 'head' table, so it cannot be embedded.");
        if (head.Length < 54)
            throw new NotSupportedException($"'{Describe(font)}' has a {head.Length}-byte 'head' table; 54 are needed.");
        WriteU32(head, 8, 0);                            // checkSumAdjustment: filled in by Assemble
        for (int i = 20; i < 36; i++)
            head[i] = 0;                                 // created + modified: the /Info problem, zeroed
        WriteU16(head, 50, 1);                           // indexToLocFormat: long
        return head;
    }

    private static byte[] Hhea(TrueTypeFont font, int count)
    {
        var hhea = font.RawTable("hhea") ?? throw new NotSupportedException(
            $"'{Describe(font)}' has no 'hhea' table, so it cannot be embedded.");
        if (hhea.Length < 36)
            throw new NotSupportedException($"'{Describe(font)}' has a {hhea.Length}-byte 'hhea' table; 36 are needed.");
        WriteU16(hhea, 34, count);                       // numberOfHMetrics
        return hhea;
    }

    private static byte[] Maxp(TrueTypeFont font, int count)
    {
        var maxp = font.RawTable("maxp") ?? throw new NotSupportedException(
            $"'{Describe(font)}' has no 'maxp' table, so it cannot be embedded.");
        if (maxp.Length < 6)
            throw new NotSupportedException($"'{Describe(font)}' has a {maxp.Length}-byte 'maxp' table; 6 are needed.");
        WriteU16(maxp, 4, count);                        // numGlyphs
        return maxp;
    }

    /// <summary>Full (advance, bearing) pairs for glyphs 0..count-1 — no monospaced
    /// tail, so the table is a plain function of the kept range.</summary>
    private static byte[] Hmtx(TrueTypeFont font, int count)
    {
        var bytes = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            WriteU16(bytes, i * 4, font.AdvanceWidthUnits(i));
            WriteU16(bytes, i * 4 + 2, font.LeftSideBearingUnits(i) & 0xFFFF);
        }
        return bytes;
    }

    // ---------------------------------------------------------------- container

    /// <summary>
    /// The sfnt container: version, the binary-search hints (a function of the table
    /// count), table records in sorted tag order, then 4-aligned table data. Every
    /// checksum is computed, and <c>head</c>'s <c>checkSumAdjustment</c> is patched last
    /// as <c>0xB1B0AFBA - checksum(whole file)</c> — a function of the bytes, so the
    /// output stays a fixed point.
    /// </summary>
    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        int count = tables.Count;
        int entrySelector = (int)Math.Floor(Math.Log2(count));
        int searchRange = 16 * (1 << entrySelector);

        int dataAt = 12 + count * 16;
        int total = dataAt;
        foreach (var bytes in tables.Values)
            total += Align4(bytes.Length);

        var file = new byte[total];
        WriteU32(file, 0, 0x00010000);                   // sfnt version: TrueType outlines
        WriteU16(file, 4, count);
        WriteU16(file, 6, searchRange);
        WriteU16(file, 8, entrySelector);
        WriteU16(file, 10, count * 16 - searchRange);

        int record = 12, at = dataAt, headAt = -1;
        foreach (var (tag, bytes) in tables)
        {
            for (int i = 0; i < 4; i++)
                file[record + i] = (byte)tag[i];
            Array.Copy(bytes, 0, file, at, bytes.Length);
            WriteU32(file, record + 4, Checksum(file, at, Align4(bytes.Length)));
            WriteU32(file, record + 8, (uint)at);
            WriteU32(file, record + 12, (uint)bytes.Length);
            if (tag == "head")
                headAt = at;
            record += 16;
            at += Align4(bytes.Length);
        }

        WriteU32(file, headAt + 8, unchecked(0xB1B0AFBAu - Checksum(file, 0, file.Length)));
        return file;
    }

    private static int Align4(int value) => (value + 3) & ~3;

    /// <summary>The sfnt checksum: the region read as big-endian 32-bit words and summed
    /// modulo 2^32 (the region is 4-aligned by construction).</summary>
    private static uint Checksum(byte[] data, int offset, int length)
    {
        uint sum = 0;
        for (int i = 0; i < length; i += 4)
            sum = unchecked(sum + ReadU32(data, offset + i));
        return sum;
    }

    private static uint ReadU32(byte[] data, int at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3];

    private static void WriteU32(byte[] data, int at, uint value)
    {
        data[at] = (byte)(value >> 24);
        data[at + 1] = (byte)(value >> 16);
        data[at + 2] = (byte)(value >> 8);
        data[at + 3] = (byte)value;
    }

    private static void WriteU16(byte[] data, int at, int value)
    {
        data[at] = (byte)(value >> 8);
        data[at + 1] = (byte)value;
    }
}
