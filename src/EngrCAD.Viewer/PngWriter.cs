using System.Buffers.Binary;
using System.IO.Compression;

namespace EngrCAD.Viewer;

/// <summary>
/// Minimal dependency-free PNG encoder: 8-bit RGBA, no interlace, filter type 0 per
/// scanline, one zlib IDAT (System.IO.Compression.ZLibStream). Enough for headless
/// render output; not a general-purpose image library.
/// </summary>
internal static class PngWriter
{
    /// <summary>Writes <paramref name="rgbaTopDown"/> (width * height * 4 bytes, top row first) as a PNG.</summary>
    public static void Write(string path, ReadOnlySpan<byte> rgbaTopDown, int width, int height)
    {
        if (rgbaTopDown.Length != width * height * 4)
            throw new ArgumentException(
                $"Pixel buffer has {rgbaTopDown.Length} bytes; expected {width * height * 4} for {width}x{height} RGBA.");

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        stream.Write(signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // color type: RGBA
        // ihdr[10..12] = 0: deflate, adaptive filtering, no interlace.
        WriteChunk(stream, "IHDR"u8, ihdr);

        using var idat = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true))
        {
            int stride = width * 4;
            for (int y = 0; y < height; y++)
            {
                zlib.WriteByte(0); // filter type: none
                zlib.Write(rgbaTopDown.Slice(y * stride, stride));
            }
        }
        WriteChunk(stream, "IDAT"u8, idat.GetBuffer().AsSpan(0, (int)idat.Length));
        WriteChunk(stream, "IEND"u8, []);
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);

        // CRC covers the chunk type then the data, seeded and finalized with 0xFFFFFFFF.
        uint crc = Crc32(data, Crc32(type, 0xFFFFFFFFu)) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <summary>Running CRC-32 (ISO 3309, as PNG specifies); caller pre/post-conditions with 0xFFFFFFFF.</summary>
    private static uint Crc32(ReadOnlySpan<byte> data, uint seed)
    {
        uint c = seed;
        foreach (byte b in data)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c;
    }
}
