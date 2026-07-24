using System.Buffers.Binary;
using System.IO.Compression;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The screenshot PNG encoder, verified structurally: signature, IHDR fields, the
/// zlib-decompressed IDAT scanlines (filter byte + raw rows), row flipping (OpenGL
/// reads bottom-up), and forced-opaque alpha.
/// </summary>
public class PngWriterTests
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Splits an encoded PNG into (type, data) chunks after validating the signature.</summary>
    private static List<(string Type, byte[] Data)> Chunks(byte[] png)
    {
        Assert.True(png.Length > 8);
        Assert.Equal(Signature, png[..8]);
        var chunks = new List<(string, byte[])>();
        int at = 8;
        while (at < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            chunks.Add((type, png.AsSpan(at + 8, length).ToArray()));
            at += 12 + length; // length + type + data + crc
        }
        Assert.Equal(png.Length, at); // chunks tile the file exactly
        return chunks;
    }

    private static byte[] Scanlines(byte[] png)
    {
        var idat = Chunks(png).Where(c => c.Type == "IDAT").SelectMany(c => c.Data).ToArray();
        using var decompressed = new MemoryStream();
        using (var zlib = new ZLibStream(new MemoryStream(idat), CompressionMode.Decompress))
            zlib.CopyTo(decompressed);
        return decompressed.ToArray();
    }

    [Fact]
    public void Encode_WritesValidStructure()
    {
        byte[] rgba =
        [
            255, 0, 0, 255,   0, 255, 0, 255,   // row 0: red, green
            0, 0, 255, 255,   255, 255, 255, 128, // row 1: blue, half-transparent white
        ];
        var png = PngWriter.Encode(2, 2, rgba);

        var chunks = Chunks(png);
        Assert.Equal("IHDR", chunks[0].Type);
        Assert.Equal("IEND", chunks[^1].Type);
        Assert.Contains(chunks, c => c.Type == "IDAT");

        var ihdr = chunks[0].Data;
        Assert.Equal(13, ihdr.Length);
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(ihdr));         // width
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(4))); // height
        Assert.Equal(8, ihdr[8]);  // bit depth
        Assert.Equal(6, ihdr[9]);  // truecolor + alpha

        // The empty IEND chunk has the well-known CRC AE 42 60 82.
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 }, png[^12..]);

        // Scanlines: filter byte 0 then the row, top-down, unmodified.
        byte[] expected =
        [
            0, 255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 0, 255, 255, 255, 255, 255, 128,
        ];
        Assert.Equal(expected, Scanlines(png));
    }

    [Fact]
    public void Encode_FlipVertically_ReversesRowOrder()
    {
        byte[] rgba =
        [
            1, 2, 3, 4,     // bottom row (OpenGL origin)
            5, 6, 7, 8,     // top row
        ];
        var png = PngWriter.Encode(1, 2, rgba, flipVertically: true);
        Assert.Equal(new byte[] { 0, 5, 6, 7, 8, 0, 1, 2, 3, 4 }, Scanlines(png));
    }

    [Fact]
    public void Encode_ForceOpaque_SetsEveryAlphaTo255()
    {
        byte[] rgba =
        [
            10, 20, 30, 0,   40, 50, 60, 77,
        ];
        var png = PngWriter.Encode(2, 1, rgba, forceOpaque: true);
        Assert.Equal(new byte[] { 0, 10, 20, 30, 255, 40, 50, 60, 255 }, Scanlines(png));
    }

    [Fact]
    public void Encode_RejectsBadInput()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PngWriter.Encode(0, 1, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => PngWriter.Encode(1, -1, new byte[4]));
        Assert.Throws<ArgumentException>(() => PngWriter.Encode(2, 2, new byte[15]));
    }

    [Fact]
    public void Encode_RoundTripsALargerImage()
    {
        // Deterministic pseudo-random 64x48 image survives compress/decompress intact.
        const int width = 64, height = 48;
        var rgba = new byte[width * height * 4];
        uint state = 12345;
        for (int i = 0; i < rgba.Length; i++)
        {
            state = state * 1664525 + 1013904223;
            rgba[i] = (byte)(state >> 24);
        }

        var png = PngWriter.Encode(width, height, rgba);
        var scanlines = Scanlines(png);
        Assert.Equal((width * 4 + 1) * height, scanlines.Length);
        for (int y = 0; y < height; y++)
        {
            Assert.Equal(0, scanlines[y * (width * 4 + 1)]);
            Assert.Equal(
                rgba.AsSpan(y * width * 4, width * 4).ToArray(),
                scanlines.AsSpan(y * (width * 4 + 1) + 1, width * 4).ToArray());
        }
    }
}
