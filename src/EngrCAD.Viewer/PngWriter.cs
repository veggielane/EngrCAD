using System.Buffers.Binary;
using System.IO.Compression;

namespace EngrCAD.Viewer;

/// <summary>
/// Minimal PNG encoder (8-bit RGBA, filter None, no interlace) so screenshots and
/// headless renders need no image-library dependency. Handles the two framebuffer
/// quirks in one place: OpenGL reads pixels bottom-up (<c>flipVertically</c>) and its
/// alpha channel is whatever compositing left behind (<c>forceOpaque</c>).
/// </summary>
internal static class PngWriter
{
    /// <summary>Encodes <paramref name="rgba"/> (4 bytes per pixel, row-major) as a PNG.</summary>
    /// <param name="width">Image width in pixels (positive).</param>
    /// <param name="height">Image height in pixels (positive).</param>
    /// <param name="rgba">Pixel data, exactly <c>width * height * 4</c> bytes.</param>
    /// <param name="flipVertically">Treat the input rows as bottom-up (OpenGL convention).</param>
    /// <param name="forceOpaque">Write every alpha byte as 255.</param>
    public static byte[] Encode(
        int width, int height, ReadOnlySpan<byte> rgba,
        bool flipVertically = false, bool forceOpaque = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        if (rgba.Length != width * height * 4)
            throw new ArgumentException(
                $"Expected {width * height * 4} bytes for {width}x{height} RGBA, got {rgba.Length}.", nameof(rgba));

        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]); // PNG signature
        WriteIhdr(output, width, height);
        WriteChunk(output, "IDAT",
            Compress(BuildRawScanlines(width, height, rgba, flipVertically, forceOpaque)));
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>The IHDR this encoder always writes: 8-bit RGBA, deflate, filter
    /// method 0, no interlace. Shared with <see cref="ApngWriter"/>, whose frames must
    /// match the IHDR bit-for-bit in format.</summary>
    internal static void WriteIhdr(MemoryStream output, int width, int height)
    {
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type: truecolor with alpha
        ihdr[10] = 0; // compression: deflate
        ihdr[11] = 0; // filter method 0
        ihdr[12] = 0; // no interlace
        WriteChunk(output, "IHDR", ihdr);
    }

    /// <summary>Raw image stream for the deflate payload: each scanline prefixed by the
    /// filter-type byte 0 (None), rows top-down.</summary>
    internal static byte[] BuildRawScanlines(
        int width, int height, ReadOnlySpan<byte> rgba, bool flipVertically, bool forceOpaque)
    {
        int stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int source = (flipVertically ? height - 1 - y : y) * stride;
            int destination = y * (stride + 1);
            raw[destination] = 0; // filter: None
            rgba.Slice(source, stride).CopyTo(raw.AsSpan(destination + 1, stride));
            if (forceOpaque)
            {
                for (int alpha = destination + 4; alpha <= destination + stride; alpha += 4)
                    raw[alpha] = 255;
            }
        }
        return raw;
    }

    /// <summary>One complete zlib datastream over <paramref name="raw"/> — the payload
    /// of an IDAT, and of an APNG frame's fdAT sequence (each frame's data is its own
    /// complete datastream per the APNG spec).</summary>
    internal static byte[] Compress(ReadOnlySpan<byte> raw)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw);
        return compressed.ToArray();
    }

    /// <summary>Encodes <paramref name="rgbaTopDown"/> (top row first) and writes it to
    /// <paramref name="path"/>, creating the directory if needed. The convenience form
    /// used by headless rendering, where rows are already top-down.</summary>
    public static void Write(string path, ReadOnlySpan<byte> rgbaTopDown, int width, int height)
    {
        var png = Encode(width, height, rgbaTopDown);
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, png);
    }

    internal static void WriteChunk(MemoryStream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word);

        Span<byte> typeBytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
            typeBytes[i] = (byte)type[i];
        output.Write(typeBytes);
        output.Write(data);

        uint crc = UpdateCrc(UpdateCrc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(word, crc);
        output.Write(word);
    }

    // CRC-32 (ISO 3309 / PNG) over the chunk type and data.
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

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
