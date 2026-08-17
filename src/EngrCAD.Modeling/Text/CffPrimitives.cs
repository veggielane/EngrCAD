namespace EngrCAD.Modeling;

/// <summary>
/// The two container structures every PostScript-flavoured OpenType table is built
/// from: the INDEX (a counted array of variable-length items) and the DICT (a key/value
/// list whose operators follow their operands). <see cref="CffOutlines"/> and
/// <see cref="Cff2Outlines"/> read the same shapes — CFF2's only container change is a
/// 32-bit INDEX count — so they are parsed once here.
/// </summary>
internal static class CffPrimitives
{
    /// <summary>Two-byte (escaped) DICT operator key.</summary>
    public static int Op(int escape, int op) => escape * 100 + op;

    /// <summary>Reads an INDEX at <paramref name="at"/> (advanced past it): item spans
    /// as absolute offsets. Offsets inside an INDEX are 1-based from its data start.
    /// CFF1 counts items in 16 bits, CFF2 in 32.</summary>
    public static (int Start, int End)[] ReadIndex(ReadOnlySpan<byte> span, ref int at, bool count32 = false)
    {
        var reader = new FontReader(span, at);
        long declared = count32 ? reader.ReadUInt32() : reader.ReadUInt16();
        int headerSize = count32 ? 4 : 2;
        if (declared == 0)
        {
            at += headerSize;
            return [];
        }
        if (declared > int.MaxValue)
            throw new FontFormatException($"CFF INDEX declares {declared} items.");
        int count = (int)declared;

        int offSize = reader.ReadUInt8();
        if (offSize is < 1 or > 4)
            throw new FontFormatException($"CFF INDEX offSize is {offSize}; the format allows 1..4.");

        var offsets = new int[count + 1];
        for (int i = 0; i <= count; i++)
        {
            long value = 0;
            for (int b = 0; b < offSize; b++)
                value = (value << 8) | reader.ReadUInt8();
            if (value < 1 || value > int.MaxValue)
                throw new FontFormatException($"CFF INDEX offset {value} is out of range (offsets are 1-based).");
            offsets[i] = (int)value;
        }

        int dataStart = reader.Position;
        var items = new (int, int)[count];
        for (int i = 0; i < count; i++)
        {
            int start = dataStart + offsets[i] - 1;
            int end = dataStart + offsets[i + 1] - 1;
            if (end < start || end > span.Length)
                throw new FontFormatException($"CFF INDEX item {i} spans {start}..{end}, outside the {span.Length}-byte file.");
            items[i] = (start, end);
        }
        at = dataStart + offsets[count] - 1;
        return items;
    }

    public static void SkipIndex(ReadOnlySpan<byte> span, ref int at) => ReadIndex(span, ref at);

    /// <summary>Parses a DICT: operator -> operand list. DICT number encoding differs
    /// from charstrings (operators 29 and 30 exist here only).</summary>
    public static Dictionary<int, double[]> ParseDict(ReadOnlySpan<byte> span, int start, int end)
    {
        var dict = new Dictionary<int, double[]>();
        var operands = new List<double>();
        var reader = new FontReader(span, start);
        while (reader.Position < end)
        {
            int b0 = reader.ReadUInt8();
            switch (b0)
            {
                // Operators. CFF1 stops at 21 and leaves 22..27 reserved; CFF2 spends
                // three of them (22 vsindex, 23 blend, 24 vstore), and since none of the
                // reserved bytes is a valid OPERAND either, admitting them changes no
                // well-formed CFF1 parse.
                case <= 24:
                    int key = b0 == 12 ? Op(12, reader.ReadUInt8()) : b0;
                    dict[key] = [.. operands];
                    operands.Clear();
                    break;
                case 28:
                    operands.Add((short)reader.ReadUInt16());
                    break;
                case 29:
                    operands.Add((int)reader.ReadUInt32());
                    break;
                case 30:
                    operands.Add(ReadRealNumber(ref reader));
                    break;
                case >= 32 and <= 246:
                    operands.Add(b0 - 139);
                    break;
                case >= 247 and <= 250:
                    operands.Add((b0 - 247) * 256 + reader.ReadUInt8() + 108);
                    break;
                case >= 251 and <= 254:
                    operands.Add(-(b0 - 251) * 256 - reader.ReadUInt8() - 108);
                    break;
                default:
                    throw new FontFormatException($"CFF DICT byte 0x{b0:X2} at offset {reader.Position - 1} is not a valid operand or operator.");
            }
        }
        return dict;
    }

    /// <summary>DICT real numbers are packed BCD nibbles: digits, '.', exponents, a
    /// minus, and 0xF terminating.</summary>
    private static double ReadRealNumber(ref FontReader reader)
    {
        var text = new System.Text.StringBuilder();
        while (true)
        {
            int b = reader.ReadUInt8();
            for (int half = 0; half < 2; half++)
            {
                int nibble = half == 0 ? b >> 4 : b & 0xF;
                switch (nibble)
                {
                    case <= 9: text.Append((char)('0' + nibble)); break;
                    case 0xA: text.Append('.'); break;
                    case 0xB: text.Append('E'); break;
                    case 0xC: text.Append("E-"); break;
                    case 0xE: text.Append('-'); break;
                    case 0xF:
                        return double.TryParse(text.ToString(), System.Globalization.CultureInfo.InvariantCulture, out double value)
                            ? value
                            : throw new FontFormatException($"CFF DICT real number '{text}' does not parse.");
                    default:
                        throw new FontFormatException($"CFF DICT real number contains reserved nibble 0x{nibble:X}.");
                }
            }
        }
    }
}
