using System.Buffers.Binary;

namespace EngrCAD.Modeling;

/// <summary>
/// Thrown when font bytes are not a font we can read: wrong container, an outline
/// format we do not support (PostScript/CFF), a missing required table, or truncated
/// data. The message always names the concrete problem.
/// </summary>
public sealed class FontFormatException(string message) : Exception(message);

/// <summary>
/// Big-endian cursor over sfnt bytes — the only place raw font data is decoded.
/// Every read is bounds-checked and reports the offending offset, so a truncated or
/// mis-parsed font fails with a <see cref="FontFormatException"/> naming the position
/// rather than an <see cref="IndexOutOfRangeException"/> from deep inside a table
/// reader. All font values are big-endian regardless of machine byte order.
/// </summary>
internal ref struct FontReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public FontReader(ReadOnlySpan<byte> data, int position = 0)
    {
        _data = data;
        _position = Bounds(data, position, 0);
    }

    public int Position
    {
        readonly get => _position;
        set => _position = Bounds(_data, value, 0);
    }

    public void Skip(int count) => _position = Bounds(_data, _position, count);

    public byte ReadUInt8()
    {
        int at = _position;
        _position = Bounds(_data, at, 1);
        return _data[at];
    }

    public sbyte ReadInt8() => (sbyte)ReadUInt8();

    public ushort ReadUInt16()
    {
        int at = _position;
        _position = Bounds(_data, at, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(_data[at..]);
    }

    public short ReadInt16() => (short)ReadUInt16();

    public uint ReadUInt32()
    {
        int at = _position;
        _position = Bounds(_data, at, 4);
        return BinaryPrimitives.ReadUInt32BigEndian(_data[at..]);
    }

    /// <summary>F2Dot14 fixed point (composite-glyph scale factors, and every
    /// normalized variation coordinate): a signed 16-bit value with 2 integer bits and
    /// 14 fraction bits.</summary>
    public double ReadF2Dot14() => ReadInt16() / 16384.0;

    /// <summary>Fixed 16.16 (the <c>fvar</c> axis and instance coordinates): a signed
    /// 32-bit value with 16 fraction bits.</summary>
    public double ReadFixed() => (int)ReadUInt32() / 65536.0;

    /// <summary>A 4-byte table tag as ASCII (e.g. <c>glyf</c>); non-printable bytes
    /// become <c>?</c> so error messages stay readable.</summary>
    public string ReadTag()
    {
        Span<char> tag = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            byte b = ReadUInt8();
            tag[i] = b is >= 0x20 and < 0x7F ? (char)b : '?';
        }
        return new string(tag);
    }

    private static int Bounds(ReadOnlySpan<byte> data, int at, int count)
    {
        if (at < 0 || count < 0 || at > data.Length - count)
            throw new FontFormatException(
                $"Font data is truncated: {count} byte(s) requested at offset {at}, but the file is {data.Length} bytes.");
        return at + count;
    }
}
