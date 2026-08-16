using System.Buffers.Binary;
using System.IO.Compression;

namespace EngrCAD.Modeling;

/// <summary>
/// A minimal, dependency-free PNG reader for heightmaps — the reading counterpart of the
/// viewer's hand-rolled <c>PngWriter</c>, kept in Modeling because kernel projects take no
/// third-party dependencies (the TrueType reader precedent; inflate comes from the BCL's
/// <see cref="ZLibStream"/>). Handles what a heightmap needs: color type 0 (grayscale),
/// 4 (grayscale + alpha, alpha ignored), <b>2 (truecolor RGB) and 6 (truecolor + alpha)</b>
/// at bit depth 8 or 16, interlaced (Adam7) or not, with all five scanline filters (None/Sub/Up/
/// Average/Paeth) unapplied exactly.
/// <para><b>Colour → height rule.</b> A colour pixel becomes a height through its
/// <b>Rec. 709 relative luminance</b> — <c>Y = 0.2126·R + 0.7152·G + 0.0722·B</c>, the
/// physically-correct luminance from the sRGB/Rec. 709 primaries — normalised to 0..1 by
/// <c>2^depth − 1</c> exactly as a grayscale sample is; the alpha channel is ignored. This
/// is a documented DECISION (a colour image has no single "height" until one is named), so
/// it is stated rather than inferred: a pure grey pixel reads its own value (the weights
/// sum to 1), a pure red pixel reads 0.2126, a pure blue 0.0722.</para>
/// Everything else — <b>palette</b> (color type 3), 1/2/4-bit depths, Adam7 — is rejected
/// with a message naming the limitation, never silently mis-read. Chunk CRCs, when the
/// stream carries them, are VERIFIED (`CRC-32/ISO-HDLC`), so a corrupt chunk is named
/// rather than inflated into wrong heights.
/// </summary>
internal static class PngGrayReader
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static double[,] Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || !data[..8].SequenceEqual(Signature))
            throw new FormatException("Not a PNG file (bad signature).");

        int width = 0, height = 0, bitDepth = 0, colorType = 0;
        bool sawHeader = false;
        bool interlaced = false;
        using var compressed = new MemoryStream();

        int at = 8;
        while (at + 8 <= data.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data[at..]);
            if (length < 0 || at + 12 + length > data.Length)
                throw new FormatException($"PNG chunk at offset {at} is truncated.");
            var type = data.Slice(at + 4, 4);
            var payload = data.Slice(at + 8, length);

            // Verify the CRC of every CRITICAL chunk (IHDR/PLTE/IDAT/IEND — those with the
            // ancillary bit clear, i.e. an uppercase first letter). The spec permits skipping
            // it for ancillary chunks, which we ignore anyway, so a corrupt tEXt cannot fail a
            // decodable image while a corrupt IHDR or IDAT — the bytes that become heights — is
            // named rather than inflated into nonsense.
            if ((type[0] & 0x20) == 0)
            {
                uint stored = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(at + 8 + length, 4));
                uint actual = Crc32(data.Slice(at + 4, 4 + length));   // type + payload
                if (stored != actual)
                    throw new FormatException(
                        $"PNG chunk '{ChunkName(type)}' at offset {at} has a CRC mismatch " +
                        $"(stored {stored:X8}, computed {actual:X8}); the file is corrupt.");
            }

            if (type.SequenceEqual("IHDR"u8))
            {
                if (length != 13)
                    throw new FormatException($"PNG IHDR is {length} bytes; the format requires 13.");
                width = BinaryPrimitives.ReadInt32BigEndian(payload);
                height = BinaryPrimitives.ReadInt32BigEndian(payload[4..]);
                bitDepth = payload[8];
                colorType = payload[9];
                if (width <= 0 || height <= 0)
                    throw new FormatException($"PNG size {width}×{height} is not valid.");
                if (colorType is not (0 or 2 or 4 or 6))
                    throw new FormatException(
                        $"PNG color type {colorType} is not supported for heightmaps; use grayscale (0/4) " +
                        "or truecolor (2/6). Palette images (color type 3) would need a palette lookup this " +
                        "reader does not do — convert to grayscale or truecolor.");
                if (bitDepth is not (8 or 16))
                    throw new FormatException(
                        $"PNG bit depth {bitDepth} is not supported; use 8- or 16-bit samples.");
                interlaced = payload[12] == 1;
                if (payload[12] > 1)
                    throw new FormatException(
                        $"PNG interlace method {payload[12]} is undefined; 0 (none) and 1 (Adam7) exist.");
                sawHeader = true;
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                throw new FormatException(
                    "Palette PNGs (color type 3) are not supported for heightmaps; convert to grayscale or truecolor.");
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                compressed.Write(payload);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
            // Ancillary chunks (tEXt, gAMA, ...) are skipped.
            at += 12 + length;
        }
        if (!sawHeader)
            throw new FormatException("PNG has no IHDR chunk.");
        if (compressed.Length == 0)
            throw new FormatException("PNG has no image data (IDAT).");

        int bytesPerSample = bitDepth / 8;
        int samplesPerPixel = colorType switch
        {
            2 => 3,                                        // R G B
            6 => 4,                                        // R G B A (alpha ignored)
            4 => 2,                                        // gray + alpha (alpha ignored)
            _ => 1,                                        // grayscale
        };
        bool colour = colorType is 2 or 6;
        int stride = width * bytesPerSample * samplesPerPixel;

        // Inflate the zlib stream and unfilter scanline by scanline: each row is one
        // filter-type byte then the filtered bytes; filters reference the previous
        // byte at the same sample position (bpp back) and the previous row.
        compressed.Position = 0;
        using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);
        var previous = new byte[stride];
        var current = new byte[stride];
        int bpp = bytesPerSample * samplesPerPixel;
        double maxValue = (1 << bitDepth) - 1;

        var heights = new double[height, width];

        double HeightAt(byte[] row, int offset)
        {
            if (colour)
            {
                // Rec. 709 relative luminance from the three colour channels (alpha,
                // if present, sits at samples 3 and is not read). The weights sum to 1,
                // so a grey pixel R=G=B reads exactly its own normalised value.
                double r = Sample(row, offset, bitDepth);
                double g = Sample(row, offset + bytesPerSample, bitDepth);
                double b = Sample(row, offset + 2 * bytesPerSample, bitDepth);
                return (0.2126 * r + 0.7152 * g + 0.0722 * b) / maxValue;
            }
            return Sample(row, offset, bitDepth) / maxValue;
        }

        if (!interlaced)
        {
            Span<byte> filterByte = stackalloc byte[1];
            for (int row = 0; row < height; row++)
            {
                inflate.ReadExactly(filterByte);
                inflate.ReadExactly(current);
                Unfilter(filterByte[0], current, previous, bpp, stride, row);
                for (int col = 0; col < width; col++)
                    heights[row, col] = HeightAt(current, col * bpp);
                (previous, current) = (current, previous);
            }
            return heights;
        }

        // Adam7: seven passes, each its OWN scanline-filter stream at reduced
        // dimensions — a pass's rows filter against the previous row OF THE PASS, and a
        // pass with no pixels (a small image) contributes no bytes at all, not even
        // filter bytes. The recovered samples scatter onto the full-size grid at the
        // pass's own start/step.
        ReadOnlySpan<(int X0, int Y0, int Dx, int Dy)> passes =
        [
            (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4),
            (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2),
        ];
        Span<byte> passFilter = stackalloc byte[1];
        foreach (var (x0, y0, dx, dy) in passes)
        {
            int passWidth = width > x0 ? (width - x0 + dx - 1) / dx : 0;
            int passHeight = height > y0 ? (height - y0 + dy - 1) / dy : 0;
            if (passWidth == 0 || passHeight == 0)
                continue;
            int passStride = passWidth * bpp;
            var passPrevious = new byte[passStride];
            var passCurrent = new byte[passStride];
            for (int row = 0; row < passHeight; row++)
            {
                inflate.ReadExactly(passFilter);
                inflate.ReadExactly(passCurrent.AsSpan(0, passStride));
                Unfilter(passFilter[0], passCurrent, passPrevious, bpp, passStride, row);
                int y = y0 + row * dy;
                for (int col = 0; col < passWidth; col++)
                    heights[y, x0 + col * dx] = HeightAt(passCurrent, col * bpp);
                (passPrevious, passCurrent) = (passCurrent, passPrevious);
            }
        }
        return heights;
    }

    /// <summary>Undoes one scanline's filter in place — the five PNG filters over a row
    /// of <paramref name="count"/> bytes against the previous row of the same stream
    /// (for Adam7, the previous row of the PASS, which is why the caller owns the row
    /// buffers rather than this method assuming the image's).</summary>
    private static void Unfilter(byte filter, byte[] current, byte[] previous, int bpp, int count, int row)
    {
        switch (filter)
        {
            case 0:                                        // None
                break;
            case 1:                                        // Sub
                for (int i = bpp; i < count; i++)
                    current[i] += current[i - bpp];
                break;
            case 2:                                        // Up
                for (int i = 0; i < count; i++)
                    current[i] += previous[i];
                break;
            case 3:                                        // Average
                for (int i = 0; i < count; i++)
                    current[i] += (byte)(((i >= bpp ? current[i - bpp] : 0) + previous[i]) / 2);
                break;
            case 4:                                        // Paeth
                for (int i = 0; i < count; i++)
                {
                    int a = i >= bpp ? current[i - bpp] : 0;
                    int b = previous[i];
                    int c = i >= bpp ? previous[i - bpp] : 0;
                    current[i] += (byte)Paeth(a, b, c);
                }
                break;
            default:
                throw new FormatException($"PNG scanline {row} uses filter {filter}; 0..4 are defined.");
        }
    }

    /// <summary>One sample (8- or 16-bit, big-endian) read as an integer 0..2^depth−1.</summary>
    private static double Sample(byte[] row, int offset, int bitDepth) =>
        bitDepth == 8 ? row[offset] : (row[offset] << 8) | row[offset + 1];

    /// <summary>The four-letter chunk name as text, for a diagnostic.</summary>
    private static string ChunkName(ReadOnlySpan<byte> type) => System.Text.Encoding.ASCII.GetString(type);

    // ---- CRC-32/ISO-HDLC (the zlib/PNG polynomial) ---------------------------

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

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    /// <summary>The Paeth predictor: whichever of left/up/up-left is closest to
    /// a + b − c (exact integer arithmetic, ties in the specified a, b, c order).</summary>
    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
