using System.Buffers.Binary;
using System.IO.Compression;

namespace EngrCAD.Modeling;

/// <summary>
/// A minimal, dependency-free grayscale PNG reader for heightmaps — the reading
/// counterpart of the viewer's hand-rolled <c>PngWriter</c>, kept in Modeling because
/// kernel projects take no third-party dependencies (the TrueType reader precedent;
/// inflate comes from the BCL's <see cref="ZLibStream"/>). Only what a heightmap
/// needs: color type 0 (grayscale) or 4 (grayscale + alpha, alpha ignored) at bit
/// depth 8 or 16, non-interlaced, with all five scanline filters (None/Sub/Up/
/// Average/Paeth) unapplied exactly. Everything else — color, palette, 1/2/4-bit
/// depths, Adam7 — is rejected with a message naming the limitation, never silently
/// mis-read. Chunk CRCs are not verified (a corrupt stream fails structurally).
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
        using var compressed = new MemoryStream();

        int at = 8;
        while (at + 8 <= data.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data[at..]);
            if (length < 0 || at + 12 + length > data.Length)
                throw new FormatException($"PNG chunk at offset {at} is truncated.");
            var type = data.Slice(at + 4, 4);
            var payload = data.Slice(at + 8, length);

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
                if (colorType is not (0 or 4))
                    throw new FormatException(
                        $"PNG color type {colorType} is not supported for heightmaps; convert to 8- or 16-bit " +
                        "grayscale (color type 0). Palette and RGB images would need a color-to-height rule this " +
                        "reader deliberately does not invent.");
                if (bitDepth is not (8 or 16))
                    throw new FormatException(
                        $"PNG bit depth {bitDepth} is not supported; use 8- or 16-bit grayscale.");
                if (payload[12] != 0)
                    throw new FormatException("Interlaced (Adam7) PNGs are not supported; save non-interlaced.");
                sawHeader = true;
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                throw new FormatException("Palette PNGs are not supported for heightmaps; convert to grayscale.");
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
        int samplesPerPixel = colorType == 4 ? 2 : 1;      // gray (+ alpha, ignored)
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
        Span<byte> filterByte = stackalloc byte[1];
        for (int row = 0; row < height; row++)
        {
            inflate.ReadExactly(filterByte);
            inflate.ReadExactly(current);
            switch (filterByte[0])
            {
                case 0:                                    // None
                    break;
                case 1:                                    // Sub
                    for (int i = bpp; i < stride; i++)
                        current[i] += current[i - bpp];
                    break;
                case 2:                                    // Up
                    for (int i = 0; i < stride; i++)
                        current[i] += previous[i];
                    break;
                case 3:                                    // Average
                    for (int i = 0; i < stride; i++)
                        current[i] += (byte)(((i >= bpp ? current[i - bpp] : 0) + previous[i]) / 2);
                    break;
                case 4:                                    // Paeth
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? current[i - bpp] : 0;
                        int b = previous[i];
                        int c = i >= bpp ? previous[i - bpp] : 0;
                        current[i] += (byte)Paeth(a, b, c);
                    }
                    break;
                default:
                    throw new FormatException($"PNG scanline {row} uses filter {filterByte[0]}; 0..4 are defined.");
            }

            for (int col = 0; col < width; col++)
            {
                int offset = col * bpp;
                int value = bitDepth == 8
                    ? current[offset]
                    : (current[offset] << 8) | current[offset + 1];
                heights[row, col] = value / maxValue;
            }
            (previous, current) = (current, previous);
        }
        return heights;
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
