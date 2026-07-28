using System.Buffers.Binary;
using System.IO.Compression;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// APNG export: the chunk structure (<c>acTL</c>/<c>fcTL</c>/<c>fdAT</c> in spec
/// order with one shared sequence counter), CRC validity, and — the part a structure
/// test cannot see — that a later frame's <c>fdAT</c> decompresses back to the exact
/// pixels that went in. The encoder-side tests need no GL; the end-to-end render test
/// joins the "offscreen-gl" collection.
/// </summary>
[Collection("offscreen-gl")]
public class ApngTests
{
    // ---- a minimal chunk parser (test-side, deliberately independent of the writer) ----

    private sealed record Chunk(string Type, byte[] Data, uint Crc, uint ComputedCrc);

    private static List<Chunk> Parse(byte[] png)
    {
        Assert.True(png.Length > 8, "too short for a PNG");
        Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], png.Take(8).ToArray());
        var chunks = new List<Chunk>();
        int at = 8;
        while (at < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, length).ToArray();
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length));
            chunks.Add(new Chunk(type, data, crc, Crc32(png.AsSpan(at + 4, 4 + length))));
            at += 12 + length;
        }
        return chunks;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    private static byte[] Frame(int width, int height, byte seed)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i++)
            rgba[i] = (byte)(seed + i * 31);
        return rgba;
    }

    // ---- structure ----

    [Fact]
    public void ChunkStructureFollowsTheApngSpec()
    {
        const int w = 5, h = 4;
        byte[][] frames = [Frame(w, h, 1), Frame(w, h, 2), Frame(w, h, 3)];
        var apng = ApngWriter.Encode(w, h, frames, delayNumerator: 40, delayDenominator: 1000, plays: 0);
        var chunks = Parse(apng);

        // Order: IHDR, acTL, fcTL, IDAT, (fcTL, fdAT) x2, IEND.
        Assert.Equal(
            ["IHDR", "acTL", "fcTL", "IDAT", "fcTL", "fdAT", "fcTL", "fdAT", "IEND"],
            chunks.Select(c => c.Type).ToArray());
        foreach (var chunk in chunks)
            Assert.Equal(chunk.ComputedCrc, chunk.Crc);

        // acTL: frame count + infinite plays, BEFORE the first IDAT.
        var actl = chunks[1].Data;
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(actl));
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(actl.AsSpan(4)));

        // One shared sequence counter across fcTL and fdAT, in file order: 0,1,2,3,4.
        int[] sequences =
        [
            BinaryPrimitives.ReadInt32BigEndian(chunks[2].Data),
            BinaryPrimitives.ReadInt32BigEndian(chunks[4].Data),
            BinaryPrimitives.ReadInt32BigEndian(chunks[5].Data),
            BinaryPrimitives.ReadInt32BigEndian(chunks[6].Data),
            BinaryPrimitives.ReadInt32BigEndian(chunks[7].Data),
        ];
        Assert.Equal([0, 1, 2, 3, 4], sequences);

        // Every fcTL: full-frame, zero offsets, the requested delay, dispose NONE,
        // blend SOURCE.
        foreach (var fctl in chunks.Where(c => c.Type == "fcTL"))
        {
            Assert.Equal(26, fctl.Data.Length);
            Assert.Equal(w, BinaryPrimitives.ReadInt32BigEndian(fctl.Data.AsSpan(4)));
            Assert.Equal(h, BinaryPrimitives.ReadInt32BigEndian(fctl.Data.AsSpan(8)));
            Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(fctl.Data.AsSpan(12)));
            Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(fctl.Data.AsSpan(16)));
            Assert.Equal(40, BinaryPrimitives.ReadUInt16BigEndian(fctl.Data.AsSpan(20)));
            Assert.Equal(1000, BinaryPrimitives.ReadUInt16BigEndian(fctl.Data.AsSpan(22)));
            Assert.Equal(0, fctl.Data[24]);
            Assert.Equal(0, fctl.Data[25]);
        }
    }

    [Fact]
    public void FirstFrameIsAPlainPngDefaultImage()
    {
        // A decoder with no APNG support must see a valid still: IHDR + IDAT of frame
        // 0. The IDAT here must equal what PngWriter alone would emit for frame 0.
        const int w = 3, h = 3;
        byte[][] frames = [Frame(w, h, 7), Frame(w, h, 9)];
        var apng = Parse(ApngWriter.Encode(w, h, frames, 50, 1000));
        var plain = Parse(PngWriter.Encode(w, h, frames[0]));
        Assert.Equal(
            plain.Single(c => c.Type == "IDAT").Data,
            apng.Single(c => c.Type == "IDAT").Data);
        Assert.Equal(plain.Single(c => c.Type == "IHDR").Data, apng[0].Data);
    }

    // ---- pixels ----

    [Fact]
    public void LaterFramesRoundTripThroughTheirFdatChunks()
    {
        const int w = 7, h = 5;
        byte[][] frames = [Frame(w, h, 10), Frame(w, h, 60), Frame(w, h, 200)];
        var chunks = Parse(ApngWriter.Encode(w, h, frames, 40, 1000));
        var fdats = chunks.Where(c => c.Type == "fdAT").ToList();
        Assert.Equal(2, fdats.Count);

        for (int f = 0; f < fdats.Count; f++)
        {
            // fdAT = 4-byte sequence + a complete zlib datastream of the scanlines.
            using var input = new MemoryStream(fdats[f].Data, 4, fdats[f].Data.Length - 4);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var raw = new MemoryStream();
            zlib.CopyTo(raw);
            var scanlines = raw.ToArray();
            Assert.Equal((w * 4 + 1) * h, scanlines.Length);

            var expected = frames[f + 1];
            for (int y = 0; y < h; y++)
            {
                Assert.Equal(0, scanlines[y * (w * 4 + 1)]);   // filter None
                for (int x = 0; x < w * 4; x++)
                    Assert.Equal(expected[y * w * 4 + x], scanlines[y * (w * 4 + 1) + 1 + x]);
            }
        }
    }

    [Fact]
    public void EncoderValidatesItsInput()
    {
        byte[][] one = [Frame(2, 2, 1)];
        Assert.Throws<ArgumentException>(() => ApngWriter.Encode(2, 2, one, 40, 1000));
        byte[][] wrong = [Frame(2, 2, 1), new byte[3]];
        Assert.Throws<ArgumentException>(() => ApngWriter.Encode(2, 2, wrong, 40, 1000));
        byte[][] two = [Frame(2, 2, 1), Frame(2, 2, 2)];
        Assert.Throws<ArgumentOutOfRangeException>(() => ApngWriter.Encode(2, 2, two, 0, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => ApngWriter.Encode(2, 2, two, 70000, 1000));
    }

    // ---- end to end (GL) ----

    private static string? SkipReason =>
        OffscreenRenderer.IsAvailable ? null
        : $"no offscreen GL context on this machine: {OffscreenRenderer.UnavailableReason}";

    private static Scene SmallScene()
    {
        var scene = new Scene();
        scene.Add(new Part("bracket", Shape.Box(4, 3, 1)));
        return scene;
    }

    [SkippableFact]
    public void RenderApngProducesAPlayableTurntable()
    {
        Skip.If(SkipReason is not null, SkipReason);

        var scene = SmallScene();
        var animation = new Animation(durationSeconds: 2)
            .With(TurntableTrack.Around(scene));
        string path = Path.Combine(Path.GetTempPath(), $"engrcad-{Guid.NewGuid():N}.png");
        try
        {
            animation.RenderApng(scene, path, frames: 6, width: 160, height: 120);
            var chunks = Parse(File.ReadAllBytes(path));
            Assert.Equal(6, BinaryPrimitives.ReadInt32BigEndian(
                chunks.Single(c => c.Type == "acTL").Data));
            Assert.Equal(5, chunks.Count(c => c.Type == "fdAT"));
            foreach (var chunk in chunks)
                Assert.Equal(chunk.ComputedCrc, chunk.Crc);
            // Delay: 2 s / 6 frames = 333 ms per frame.
            var fctl = chunks.First(c => c.Type == "fcTL").Data;
            Assert.Equal(333, BinaryPrimitives.ReadUInt16BigEndian(fctl.AsSpan(20)));
            Assert.Equal(1000, BinaryPrimitives.ReadUInt16BigEndian(fctl.AsSpan(22)));

            // The camera moved: frame 3's data differs from frame 0's.
            var idat = chunks.Single(c => c.Type == "IDAT").Data;
            var fdat = chunks.Where(c => c.Type == "fdAT").ElementAt(2).Data;
            Assert.NotEqual(Convert.ToHexString(idat),
                Convert.ToHexString(fdat.AsSpan(4).ToArray()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableFact]
    public void RenderFramesWritesTheFfmpegEscapeHatch()
    {
        Skip.If(SkipReason is not null, SkipReason);

        var scene = SmallScene();
        var animation = new Animation(durationSeconds: 1)
            .With(TurntableTrack.Around(scene));
        string directory = Path.Combine(Path.GetTempPath(), $"engrcad-{Guid.NewGuid():N}");
        try
        {
            var paths = animation.RenderFrames(scene, directory, frames: 3, width: 96, height: 64);
            Assert.Equal(3, paths.Count);
            Assert.Equal(Path.Combine(directory, "frame-0000.png"), paths[0]);
            foreach (string path in paths)
            {
                var chunks = Parse(File.ReadAllBytes(path));
                Assert.Equal("IHDR", chunks[0].Type);
                Assert.DoesNotContain(chunks, c => c.Type == "acTL");   // plain stills
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
