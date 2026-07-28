namespace EngrCAD.Viewer;

/// <summary>
/// Animated GIF encoder: median-cut quantization to a 256-colour local palette per
/// frame + the GIF-variant LZW, dependency-free. GIF is what pastes everywhere, and
/// that is its ONLY advantage here: 256 colours with no alpha means a shaded render
/// with a background gradient and ambient occlusion <b>will band visibly</b>, and
/// dithering (deliberately not done) would fight the clean look — prefer
/// <see cref="ApngWriter"/> for quality, and flat-shaded or wireframe styles when a
/// GIF is required. That honesty lives in the docs too, not just here.
/// <para>Quantization detail worth keeping: the median-cut PARTITION is the pixel
/// mapping — every distinct colour lands in exactly one box, whose palette entry is
/// the box's weighted average — so no nearest-palette search exists to disagree with
/// the split, and an image with ≤256 distinct colours reproduces exactly (every box
/// ends single-colour).</para>
/// </summary>
internal static class GifWriter
{
    /// <summary>Encodes frames (RGBA top-down, alpha ignored — GIF has none) as a
    /// looping animated GIF. <paramref name="delayCentiseconds"/> per frame (browsers
    /// clamp below 2 to a sluggish 10, so 2 is the floor here).</summary>
    public static byte[] Encode(
        int width, int height, IReadOnlyList<byte[]> frames, int delayCentiseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count < 2)
            throw new ArgumentException("An animation needs at least two frames.", nameof(frames));
        ArgumentOutOfRangeException.ThrowIfLessThan(delayCentiseconds, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(delayCentiseconds, ushort.MaxValue);
        foreach (var frame in frames)
        {
            if (frame.Length != width * height * 4)
                throw new ArgumentException(
                    $"Expected {width * height * 4} bytes for {width}x{height} RGBA frames.", nameof(frames));
        }

        using var output = new MemoryStream();
        output.Write("GIF89a"u8);

        // Logical screen descriptor: no global colour table (each frame carries its
        // own local one — palettes drift as the model turns).
        WriteU16(output, width);
        WriteU16(output, height);
        output.WriteByte(0x70);   // no GCT; colour resolution 8 bits
        output.WriteByte(0);      // background colour index (no GCT: ignored)
        output.WriteByte(0);      // pixel aspect ratio: none

        // NETSCAPE2.0 application extension: loop forever.
        output.Write([0x21, 0xFF, 0x0B]);
        output.Write("NETSCAPE2.0"u8);
        output.Write([0x03, 0x01, 0x00, 0x00, 0x00]);

        foreach (var frame in frames)
        {
            // Graphic control: disposal "leave in place" (every frame is full-size,
            // so the next covers it), no transparency.
            output.Write([0x21, 0xF9, 0x04, 0x04]);
            WriteU16(output, delayCentiseconds);
            output.Write([0x00, 0x00]);

            var (palette, indices) = Quantize(frame, width * height);

            // Image descriptor: full frame, local colour table of 256 entries.
            output.WriteByte(0x2C);
            WriteU16(output, 0);
            WriteU16(output, 0);
            WriteU16(output, width);
            WriteU16(output, height);
            output.WriteByte(0x87);   // local table, 2^(7+1) = 256 entries
            output.Write(palette);

            WriteLzw(output, indices, minCodeSize: 8);
        }

        output.WriteByte(0x3B);   // trailer
        return output.ToArray();
    }

    /// <summary>Writes <paramref name="path"/> (creating the directory).</summary>
    public static void Write(
        string path, IReadOnlyList<byte[]> frames, int width, int height, int delayCentiseconds)
    {
        var gif = Encode(width, height, frames, delayCentiseconds);
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, gif);
    }

    private static void WriteU16(MemoryStream output, int value)
    {
        output.WriteByte((byte)(value & 0xFF));
        output.WriteByte((byte)(value >> 8 & 0xFF));
    }

    // ---- median-cut quantization ----

    /// <summary>A box of distinct colours (an index range into the working arrays)
    /// awaiting its palette slot.</summary>
    private record struct Box(int Start, int Count);

    /// <summary>Quantizes one RGBA frame to (768-byte palette, per-pixel indices).
    /// Internal for tests.</summary>
    internal static (byte[] Palette, byte[] Indices) Quantize(ReadOnlySpan<byte> rgba, int pixelCount)
    {
        // Distinct colours with counts — median cut runs over these, not raw pixels.
        var histogram = new Dictionary<int, int>();
        for (int p = 0; p < pixelCount; p++)
        {
            int packed = rgba[p * 4] << 16 | rgba[p * 4 + 1] << 8 | rgba[p * 4 + 2];
            histogram.TryGetValue(packed, out int count);
            histogram[packed] = count + 1;
        }

        var colors = new int[histogram.Count];
        var counts = new int[histogram.Count];
        int at = 0;
        foreach (var (color, count) in histogram)
        {
            colors[at] = color;
            counts[at] = count;
            at++;
        }

        // Median cut: repeatedly split the box with the largest channel range at the
        // weighted median of its longest axis, until 256 boxes (or nothing splittable).
        var boxes = new List<Box> { new(0, colors.Length) };
        while (boxes.Count < 256)
        {
            int widest = -1, widestRange = 0, widestAxis = 0;
            for (int b = 0; b < boxes.Count; b++)
            {
                if (boxes[b].Count < 2)
                    continue;
                var (range, axis) = LongestAxis(colors, boxes[b]);
                if (range > widestRange)
                {
                    widestRange = range;
                    widestAxis = axis;
                    widest = b;
                }
            }
            if (widest < 0)
                break;   // every box is a single colour: the image had <= 256 of them

            var box = boxes[widest];
            SortByChannel(colors, counts, box, widestAxis);
            int half = WeightedMedian(counts, box);
            boxes[widest] = new Box(box.Start, half - box.Start);
            boxes.Add(new Box(half, box.Start + box.Count - half));
        }

        // Palette = each box's count-weighted average; THE PARTITION IS THE MAPPING —
        // a colour's index is the box it fell into, so no nearest search can disagree
        // with the split.
        var palette = new byte[768];
        var indexOf = new Dictionary<int, byte>(colors.Length);
        for (int b = 0; b < boxes.Count; b++)
        {
            long r = 0, g = 0, bl = 0, total = 0;
            for (int i = boxes[b].Start; i < boxes[b].Start + boxes[b].Count; i++)
            {
                long weight = counts[i];
                r += (colors[i] >> 16 & 0xFF) * weight;
                g += (colors[i] >> 8 & 0xFF) * weight;
                bl += (colors[i] & 0xFF) * weight;
                total += weight;
                indexOf[colors[i]] = (byte)b;
            }
            palette[b * 3] = (byte)(r / total);
            palette[b * 3 + 1] = (byte)(g / total);
            palette[b * 3 + 2] = (byte)(bl / total);
        }

        var indices = new byte[pixelCount];
        for (int p = 0; p < pixelCount; p++)
            indices[p] = indexOf[rgba[p * 4] << 16 | rgba[p * 4 + 1] << 8 | rgba[p * 4 + 2]];
        return (palette, indices);
    }

    private static (int Range, int Axis) LongestAxis(int[] colors, in Box box)
    {
        int minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
        for (int i = box.Start; i < box.Start + box.Count; i++)
        {
            int r = colors[i] >> 16 & 0xFF, g = colors[i] >> 8 & 0xFF, b = colors[i] & 0xFF;
            minR = Math.Min(minR, r);
            maxR = Math.Max(maxR, r);
            minG = Math.Min(minG, g);
            maxG = Math.Max(maxG, g);
            minB = Math.Min(minB, b);
            maxB = Math.Max(maxB, b);
        }
        int rangeR = maxR - minR, rangeG = maxG - minG, rangeB = maxB - minB;
        // Ties resolve green > red > blue (the eye's luminance order).
        return rangeG >= rangeR && rangeG >= rangeB ? (rangeG, 1)
            : rangeR >= rangeB ? (rangeR, 0)
            : (rangeB, 2);
    }

    private static void SortByChannel(int[] colors, int[] counts, in Box box, int axis)
    {
        int shift = axis switch { 0 => 16, 1 => 8, _ => 0 };
        // Sort a permutation under the channel key, then apply it to both slices —
        // Array.Sort can co-sort one items array, and the pair here is two.
        var keys = new int[box.Count];
        var order = new int[box.Count];
        for (int i = 0; i < box.Count; i++)
        {
            keys[i] = colors[box.Start + i] >> shift & 0xFF;
            order[i] = box.Start + i;
        }
        Array.Sort(keys, order);
        var sortedColors = new int[box.Count];
        var sortedCounts = new int[box.Count];
        for (int i = 0; i < box.Count; i++)
        {
            sortedColors[i] = colors[order[i]];
            sortedCounts[i] = counts[order[i]];
        }
        sortedColors.CopyTo(colors, box.Start);
        sortedCounts.CopyTo(counts, box.Start);
    }

    /// <summary>The split point: the first index past the weighted median (at least one
    /// element on each side).</summary>
    private static int WeightedMedian(int[] counts, in Box box)
    {
        long total = 0;
        for (int i = box.Start; i < box.Start + box.Count; i++)
            total += counts[i];
        long seen = 0;
        for (int i = box.Start; i < box.Start + box.Count - 1; i++)
        {
            seen += counts[i];
            if (seen * 2 >= total)
                return i + 1;
        }
        return box.Start + box.Count - 1;
    }

    // ---- GIF-variant LZW ----

    /// <summary>Encodes the index stream (GIFLIB's compress structure: emit-then-grow,
    /// clear at 4096). Internal for the round-trip test, which decodes with an
    /// independently written decoder.</summary>
    internal static void WriteLzw(MemoryStream output, byte[] indices, int minCodeSize)
    {
        output.WriteByte((byte)minCodeSize);
        var packer = new BitPacker(output);
        int clear = 1 << minCodeSize;
        int endOfInformation = clear + 1;
        var table = new Dictionary<(int Prefix, byte Next), int>();
        int nextCode = endOfInformation + 1;
        int codeSize = minCodeSize + 1;

        void Emit(int code)
        {
            packer.Write(code, codeSize);
            // Grow AFTER emitting, when the next free code no longer fits — the
            // decoder defines entries one step behind, which is what keeps it in sync.
            if (nextCode > (1 << codeSize) - 1 && codeSize < 12)
                codeSize++;
        }

        Emit(clear);
        int prefix = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            byte next = indices[i];
            if (table.TryGetValue((prefix, next), out int code))
            {
                prefix = code;
                continue;
            }
            Emit(prefix);
            if (nextCode < 4096)
            {
                table[(prefix, next)] = nextCode++;
            }
            else
            {
                Emit(clear);
                table.Clear();
                nextCode = endOfInformation + 1;
                codeSize = minCodeSize + 1;
            }
            prefix = next;
        }
        Emit(prefix);
        Emit(endOfInformation);
        packer.Flush();
        output.WriteByte(0);   // block terminator
    }

    /// <summary>LSB-first bit packing into 255-byte GIF sub-blocks.</summary>
    private sealed class BitPacker(MemoryStream output)
    {
        private readonly List<byte> _block = new(255);
        private int _bits;
        private int _bitCount;

        public void Write(int code, int codeSize)
        {
            _bits |= code << _bitCount;
            _bitCount += codeSize;
            while (_bitCount >= 8)
            {
                Push((byte)(_bits & 0xFF));
                _bits >>= 8;
                _bitCount -= 8;
            }
        }

        public void Flush()
        {
            if (_bitCount > 0)
                Push((byte)(_bits & 0xFF));
            _bits = 0;
            _bitCount = 0;
            if (_block.Count > 0)
            {
                output.WriteByte((byte)_block.Count);
                output.Write(_block.ToArray());
                _block.Clear();
            }
        }

        private void Push(byte value)
        {
            _block.Add(value);
            if (_block.Count == 255)
            {
                output.WriteByte(255);
                output.Write(_block.ToArray());
                _block.Clear();
            }
        }
    }
}
