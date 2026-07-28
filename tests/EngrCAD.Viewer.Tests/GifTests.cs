using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// GIF export (<see cref="GifWriter"/>): container structure, the LZW round-trip
/// against an independently written decoder (including the 4096-entry table reset),
/// and the quantizer's exactness contract — an image with ≤256 distinct colours
/// reproduces bit-for-bit, because the median-cut PARTITION is the mapping.
/// </summary>
[Collection("offscreen-gl")]
public class GifTests
{
    // ---- an independent LZW decoder (GIF variant) ----

    private static byte[] DecodeLzw(byte[] block, int expectedPixels)
    {
        int at = 0;
        int minCodeSize = block[at++];
        // Concatenate sub-blocks.
        var data = new List<byte>();
        while (true)
        {
            int length = block[at++];
            if (length == 0)
                break;
            for (int i = 0; i < length; i++)
                data.Add(block[at++]);
        }

        int clear = 1 << minCodeSize, endOfInformation = clear + 1;
        int codeSize = minCodeSize + 1;
        var table = new List<byte[]>();
        void Reset()
        {
            table.Clear();
            for (int i = 0; i < clear; i++)
                table.Add([(byte)i]);
            table.Add([]);   // clear
            table.Add([]);   // EOI
            codeSize = minCodeSize + 1;
        }
        Reset();

        var output = new List<byte>(expectedPixels);
        int bitAt = 0;
        byte[]? previous = null;
        while (true)
        {
            int code = 0;
            for (int b = 0; b < codeSize; b++)
            {
                int index = bitAt + b;
                if ((data[index >> 3] >> (index & 7) & 1) != 0)
                    code |= 1 << b;
            }
            bitAt += codeSize;

            if (code == clear)
            {
                Reset();
                previous = null;
                continue;
            }
            if (code == endOfInformation)
                break;

            byte[] entry;
            if (code < table.Count)
            {
                entry = table[code];
                if (previous is not null)
                    table.Add([.. previous, entry[0]]);
            }
            else
            {
                // The one-ahead case: code == table.Count.
                Assert.NotNull(previous);
                entry = [.. previous!, previous[0]];
                table.Add(entry);
            }
            output.AddRange(entry);
            previous = entry;
            // Decoder grows when its next slot no longer fits the current width.
            if (table.Count > (1 << codeSize) - 1 && codeSize < 12)
                codeSize++;
        }
        return [.. output];
    }

    [Theory]
    [InlineData(64)]        // trivial
    [InlineData(5000)]      // multiple sub-blocks
    [InlineData(120000)]    // forces the 4096-entry table reset on noisy data
    public void LzwRoundTripsThroughAnIndependentDecoder(int pixels)
    {
        var indices = new byte[pixels];
        // Deterministic mix of runs (compressible) and noise (table-filling).
        uint state = 12345;
        for (int i = 0; i < pixels; i++)
        {
            state = state * 1664525 + 1013904223;
            indices[i] = (i / 7 % 3 == 0) ? (byte)(i / 50) : (byte)(state >> 24);
        }

        using var stream = new MemoryStream();
        GifWriter.WriteLzw(stream, indices, minCodeSize: 8);
        var decoded = DecodeLzw(stream.ToArray(), pixels);
        Assert.Equal(indices, decoded);
    }

    [Fact]
    public void QuantizerReproducesAFewColorImageExactly()
    {
        // 4 distinct colours: every median-cut box ends single-colour, so palette
        // lookup must reproduce the input bit-for-bit.
        const int w = 16, h = 16;
        var rgba = new byte[w * h * 4];
        (byte, byte, byte)[] colors = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (40, 40, 40)];
        for (int p = 0; p < w * h; p++)
        {
            var (r, g, b) = colors[p % 4];
            rgba[p * 4] = r;
            rgba[p * 4 + 1] = g;
            rgba[p * 4 + 2] = b;
            rgba[p * 4 + 3] = 255;
        }

        var (palette, indices) = GifWriter.Quantize(rgba, w * h);
        for (int p = 0; p < w * h; p++)
        {
            int i = indices[p] * 3;
            Assert.Equal(rgba[p * 4], palette[i]);
            Assert.Equal(rgba[p * 4 + 1], palette[i + 1]);
            Assert.Equal(rgba[p * 4 + 2], palette[i + 2]);
        }
    }

    [Fact]
    public void QuantizerKeepsAGradientWithinBoxError()
    {
        // A smooth 4096-colour gradient cannot be exact in 256 colours — THE honest
        // GIF caveat — but every pixel must stay within its box's spread.
        const int w = 64, h = 64;
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x;
                rgba[p * 4] = (byte)(x * 4);
                rgba[p * 4 + 1] = (byte)(y * 4);
                rgba[p * 4 + 2] = (byte)(255 - x * 2);
                rgba[p * 4 + 3] = 255;
            }
        }

        var (palette, indices) = GifWriter.Quantize(rgba, w * h);
        int worst = 0;
        for (int p = 0; p < w * h; p++)
        {
            int i = indices[p] * 3;
            worst = Math.Max(worst, Math.Abs(rgba[p * 4] - palette[i]));
            worst = Math.Max(worst, Math.Abs(rgba[p * 4 + 1] - palette[i + 1]));
            worst = Math.Max(worst, Math.Abs(rgba[p * 4 + 2] - palette[i + 2]));
        }
        Assert.True(worst <= 24, $"gradient quantization error {worst} exceeds the box bound");
    }

    [Fact]
    public void ContainerStructureIsAGif89aWithLoopAndPerFrameTables()
    {
        const int w = 8, h = 6;
        static byte[] Frame(byte seed)
        {
            var rgba = new byte[8 * 6 * 4];
            for (int i = 0; i < rgba.Length; i++)
                rgba[i] = (byte)(seed + i * 13);
            return rgba;
        }
        var gif = GifWriter.Encode(w, h, [Frame(0), Frame(90), Frame(180)], delayCentiseconds: 8);

        Assert.Equal("GIF89a"u8.ToArray(), gif.Take(6).ToArray());
        Assert.Equal(w, gif[6] | gif[7] << 8);
        Assert.Equal(h, gif[8] | gif[9] << 8);
        Assert.Equal(0x3B, gif[^1]);   // trailer

        string ascii = System.Text.Encoding.ASCII.GetString(gif);
        Assert.Contains("NETSCAPE2.0", ascii);

        // Three image descriptors (0x2C at the frame positions), three graphic
        // control extensions carrying the delay.
        int descriptors = gif.Count(b => b == 0x2C);
        Assert.True(descriptors >= 3, $"expected >= 3 image-descriptor bytes, found {descriptors}");
        int controls = 0;
        for (int i = 0; i + 7 < gif.Length; i++)
        {
            if (gif[i] == 0x21 && gif[i + 1] == 0xF9 && gif[i + 2] == 0x04)
            {
                controls++;
                Assert.Equal(8, gif[i + 4] | gif[i + 5] << 8);   // delay in cs
            }
        }
        Assert.Equal(3, controls);
    }

    [Fact]
    public void EncoderValidatesItsInput()
    {
        static byte[] Frame() => new byte[4 * 4 * 4];
        Assert.Throws<ArgumentException>(() => GifWriter.Encode(4, 4, [Frame()], 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => GifWriter.Encode(4, 4, [Frame(), Frame()], 1));
        Assert.Throws<ArgumentException>(() => GifWriter.Encode(4, 4, [Frame(), new byte[7]], 8));
    }

    // ---- end to end (GL) ----

    private static string? SkipReason =>
        OffscreenRenderer.IsAvailable ? null
        : $"no offscreen GL context on this machine: {OffscreenRenderer.UnavailableReason}";

    [SkippableFact]
    public void RenderGifProducesALoopingWireframeClip()
    {
        Skip.If(SkipReason is not null, SkipReason);

        var scene = new Scene();
        scene.Add(new Part("bracket", Shape.Box(4, 3, 1)));
        var animation = new Animation(durationSeconds: 1.5)
            .With(TurntableTrack.Around(scene));
        string path = Path.Combine(Path.GetTempPath(), $"engrcad-{Guid.NewGuid():N}.gif");
        try
        {
            // Wireframe: the style the docs recommend for GIF (a few flat colours,
            // no gradient banding to speak of).
            animation.RenderGif(scene, path, frames: 5, width: 120, height: 90,
                style: ViewStyle.Wireframe);
            var gif = File.ReadAllBytes(path);
            Assert.Equal("GIF89a"u8.ToArray(), gif.Take(6).ToArray());
            Assert.Contains("NETSCAPE2.0", System.Text.Encoding.ASCII.GetString(gif));
            Assert.Equal(0x3B, gif[^1]);
            // 1.5 s / 5 frames = 30 cs per frame.
            bool sawDelay = false;
            for (int i = 0; i + 7 < gif.Length; i++)
            {
                if (gif[i] == 0x21 && gif[i + 1] == 0xF9 && gif[i + 2] == 0x04)
                {
                    Assert.Equal(30, gif[i + 4] | gif[i + 5] << 8);
                    sawDelay = true;
                }
            }
            Assert.True(sawDelay);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
