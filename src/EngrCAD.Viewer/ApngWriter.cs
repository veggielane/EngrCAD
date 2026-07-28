using System.Buffers.Binary;

namespace EngrCAD.Viewer;

/// <summary>
/// Animated PNG encoder: three chunk types (<c>acTL</c>/<c>fcTL</c>/<c>fdAT</c>) over
/// the machinery <see cref="PngWriter"/> already has — dependency-free, lossless, full
/// colour, which is why APNG is this codebase's FIRST animation format (a shaded CAD
/// render is mostly smooth gradients, exactly what GIF's 256 colours band on).
/// <para>Every frame is a full-size replace (x = y = 0, dispose NONE, blend SOURCE),
/// and each frame's data is its own complete zlib datastream per the spec. The first
/// frame is the PNG default image (its <c>fcTL</c> precedes <c>IDAT</c>), so a viewer
/// with no APNG support shows frame 0 as an ordinary still — and the file is served as
/// <c>.png</c> because it IS one.</para>
/// </summary>
internal static class ApngWriter
{
    /// <summary>
    /// Encodes frames (each <c>width * height * 4</c> RGBA bytes, top row first) as an
    /// APNG. <paramref name="delayNumerator"/>/<paramref name="delayDenominator"/> is
    /// the per-frame delay in seconds; <paramref name="plays"/> 0 loops forever.
    /// </summary>
    public static byte[] Encode(
        int width, int height, IReadOnlyList<byte[]> frames,
        int delayNumerator, int delayDenominator, int plays = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count < 2)
            throw new ArgumentException("An animation needs at least two frames.", nameof(frames));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delayNumerator, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delayDenominator, 0);
        if (delayNumerator > ushort.MaxValue || delayDenominator > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(delayNumerator),
                "APNG frame delays are 16-bit rationals; reduce the fraction.");
        ArgumentOutOfRangeException.ThrowIfNegative(plays);
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].Length != width * height * 4)
                throw new ArgumentException(
                    $"Frame {i}: expected {width * height * 4} bytes for {width}x{height} RGBA, " +
                    $"got {frames[i].Length}.", nameof(frames));
        }

        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]); // PNG signature
        PngWriter.WriteIhdr(output, width, height);

        // acTL must precede the first IDAT — that is what makes the file animated.
        Span<byte> actl = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(actl, frames.Count);
        BinaryPrimitives.WriteInt32BigEndian(actl[4..], plays);
        PngWriter.WriteChunk(output, "acTL", actl);

        // One shared sequence counter across fcTL and fdAT chunks, in file order.
        int sequence = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            WriteFrameControl(output, ref sequence, width, height, delayNumerator, delayDenominator);
            byte[] compressed = PngWriter.Compress(
                PngWriter.BuildRawScanlines(width, height, frames[i], flipVertically: false, forceOpaque: false));
            if (i == 0)
            {
                // The first frame IS the default image: plain IDAT, no sequence number.
                PngWriter.WriteChunk(output, "IDAT", compressed);
            }
            else
            {
                var fdat = new byte[4 + compressed.Length];
                BinaryPrimitives.WriteInt32BigEndian(fdat, sequence++);
                compressed.CopyTo(fdat.AsSpan(4));
                PngWriter.WriteChunk(output, "fdAT", fdat);
            }
        }

        PngWriter.WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>Writes <paramref name="path"/> as an APNG (creating the directory),
    /// the same convenience shape as <see cref="PngWriter.Write"/>.</summary>
    public static void Write(
        string path, IReadOnlyList<byte[]> frames, int width, int height,
        int delayNumerator, int delayDenominator, int plays = 0)
    {
        var apng = Encode(width, height, frames, delayNumerator, delayDenominator, plays);
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, apng);
    }

    private static void WriteFrameControl(
        MemoryStream output, ref int sequence, int width, int height, int delayNum, int delayDen)
    {
        Span<byte> fctl = stackalloc byte[26];
        BinaryPrimitives.WriteInt32BigEndian(fctl, sequence++);
        BinaryPrimitives.WriteInt32BigEndian(fctl[4..], width);
        BinaryPrimitives.WriteInt32BigEndian(fctl[8..], height);
        BinaryPrimitives.WriteInt32BigEndian(fctl[12..], 0);   // x offset
        BinaryPrimitives.WriteInt32BigEndian(fctl[16..], 0);   // y offset
        BinaryPrimitives.WriteUInt16BigEndian(fctl[20..], (ushort)delayNum);
        BinaryPrimitives.WriteUInt16BigEndian(fctl[22..], (ushort)delayDen);
        fctl[24] = 0;   // dispose_op: NONE (next frame replaces everything anyway)
        fctl[25] = 0;   // blend_op: SOURCE (full-frame replace, no compositing)
        PngWriter.WriteChunk(output, "fcTL", fctl);
    }
}
