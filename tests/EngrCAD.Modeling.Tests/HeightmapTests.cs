using System.IO.Compression;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Heightmap terrain (OpenSCAD's <c>surface()</c>): exact prismatoid volumes for the
/// solid builder (planar tops triangulate exactly), the <c>.dat</c> text reader, and
/// the hand-rolled PNG reader against bytes assembled by the test itself — every scanline
/// filter exercised, grayscale AND colour (Rec. 709 luminance), and REAL CRC-32s so the
/// reader's own CRC check is exercised on every fixture and a deliberately corrupted chunk
/// is caught.
/// </summary>
public class HeightmapTests
{
    // ---- the solid ----------------------------------------------------------

    [Fact]
    public void FlatGrid_IsTheExactBox()
    {
        double[,] heights = { { 2, 2 }, { 2, 2 } };
        var mesh = Heightmap.Mesh(heights, cellSize: 3);

        Assert.True(mesh.IsClosed);
        Assert.Equal(3 * 3 * 2, mesh.Volume(), 9);
    }

    [Fact]
    public void PlanarRamp_HasTheExactPrismatoidVolume()
    {
        // z = 1 + x on a 2x2 footprint (cell 1, corner at origin): V = 2 * (2 + 2) = 8.
        double[,] heights =
        {
            { 1, 2, 3 },
            { 1, 2, 3 },
            { 1, 2, 3 },
        };
        var mesh = Heightmap.Mesh(heights, cellSize: 1, baseLevel: 0, centered: false);

        Assert.True(mesh.IsClosed);
        Assert.Equal(8, mesh.Volume(), 9);
    }

    [Fact]
    public void Peak_IsWhereTheGridSaysItIs()
    {
        double[,] heights =
        {
            { 1, 1, 1 },
            { 1, 5, 1 },
            { 1, 1, 1 },
        };
        var mesh = Heightmap.Mesh(heights, cellSize: 2, centered: true);

        Assert.True(mesh.IsClosed);
        var peak = mesh.ToIndexed().Positions.MaxBy(p => p.Z);
        Assert.Equal(0, peak.X, 9);
        Assert.Equal(0, peak.Y, 9);
        Assert.Equal(5, peak.Z, 9);
    }

    [Fact]
    public void Mesh_ValidatesItsInput()
    {
        Assert.Throws<ArgumentException>(() => Heightmap.Mesh(new double[1, 3], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Heightmap.Mesh(new double[,] { { 1, 1 }, { 1, 1 } }, 0));
        // A height AT the base is degenerate (zero-thickness wall), below it worse.
        Assert.Throws<ArgumentException>(() => Heightmap.Mesh(new double[,] { { 1, 0 }, { 1, 1 } }, 1));
        Assert.Throws<ArgumentException>(() => Heightmap.Mesh(new double[,] { { 1, double.NaN }, { 1, 1 } }, 1));
    }

    [Fact]
    public void ShapeHeightmap_IsAMeshSource_WithBooleansAndSdf()
    {
        double[,] heights =
        {
            { 1, 1, 1 },
            { 1, 4, 1 },
            { 1, 1, 1 },
        };
        var terrain = Shape.Heightmap(heights, cellSize: 2);

        // Mesh-backed: B-Rep honestly impossible, mesh and implicit available.
        Assert.False(terrain.Explain(TargetRep.Brep).IsConvertible);
        Assert.True(terrain.Explain(TargetRep.Implicit).IsConvertible);

        var sdf = terrain.ToImplicit();
        Assert.True(sdf.Evaluate((0, 0, 0.5)) < 0, "under the peak should be material");
        Assert.True(sdf.Evaluate((0, 0, 5)) > 0, "above the peak should be air");

        // A boolean against a primitive goes through the mesh route and stays closed.
        var trimmed = (terrain - Shape.Box(2, 2, 20)).ToMesh();
        Assert.True(trimmed.IsClosed);
        Assert.True(trimmed.Volume() > 0);
    }

    [Fact]
    public void ShapeHeightmap_ScalesNormalizedData()
    {
        double[,] normalized = { { 0.5, 0.5 }, { 0.5, 0.5 } };
        var mesh = Shape.Heightmap(normalized, cellSize: 2, heightScale: 10).ToMesh();

        Assert.Equal(2 * 2 * 5, mesh.Volume(), 9);
    }

    // ---- .dat ----------------------------------------------------------------

    [Fact]
    public void ReadDat_ParsesRowsSkippingCommentsAndBlanks()
    {
        var heights = Heightmap.ReadDat(new StringReader(
            """
            # a comment
            1 2 3

            4 5.5 6
            """));

        Assert.Equal(2, heights.GetLength(0));
        Assert.Equal(3, heights.GetLength(1));
        Assert.Equal(1, heights[0, 0]);
        Assert.Equal(5.5, heights[1, 1]);
    }

    [Fact]
    public void ReadDat_RejectsRaggedRowsAndGarbage()
    {
        Assert.Throws<FormatException>(() => Heightmap.ReadDat(new StringReader("1 2\n3")));
        Assert.Throws<FormatException>(() => Heightmap.ReadDat(new StringReader("1 x")));
        Assert.Throws<FormatException>(() => Heightmap.ReadDat(new StringReader("# only comments")));
    }

    // ---- PNG -----------------------------------------------------------------

    [Fact]
    public void ReadPng_8Bit_DecodesEveryScanlineFilter()
    {
        // 3x5 grayscale, one row per filter type (0 None, 1 Sub, 2 Up, 3 Average,
        // 4 Paeth) plus a None row to seed. Expectations are the RAW values below.
        byte[][] raw =
        [
            [10, 20, 30],
            [15, 25, 35],
            [20, 120, 220],
            [40, 90, 140],
            [50, 100, 200],
        ];
        byte[] filters = [0, 2, 1, 3, 4];
        var png = BuildPng(3, 5, 8, colorType: 0, raw, filters);

        var heights = Heightmap.ReadPng(png);
        Assert.Equal(5, heights.GetLength(0));
        Assert.Equal(3, heights.GetLength(1));
        for (int r = 0; r < 5; r++)
            for (int c = 0; c < 3; c++)
                Assert.Equal(raw[r][c] / 255.0, heights[r, c], 12);
    }

    [Fact]
    public void ReadPng_16Bit_KeepsThePrecision()
    {
        // Two 16-bit samples per row, big-endian.
        byte[][] raw =
        [
            [0x12, 0x34, 0xAB, 0xCD],
            [0x00, 0x01, 0xFF, 0xFF],
        ];
        var png = BuildPng(2, 2, 16, colorType: 0, raw, [0, 4]);

        var heights = Heightmap.ReadPng(png);
        Assert.Equal(0x1234 / 65535.0, heights[0, 0], 15);
        Assert.Equal(0xABCD / 65535.0, heights[0, 1], 15);
        Assert.Equal(0x0001 / 65535.0, heights[1, 0], 15);
        Assert.Equal(1.0, heights[1, 1], 15);
    }

    [Fact]
    public void ReadPng_GrayAlpha_IgnoresTheAlphaChannel()
    {
        byte[][] raw =
        [
            [100, 255, 200, 0],                            // (gray, alpha) x2
            [50, 128, 25, 128],
        ];
        var png = BuildPng(2, 2, 8, colorType: 4, raw, [0, 1]);

        var heights = Heightmap.ReadPng(png);
        Assert.Equal(100 / 255.0, heights[0, 0], 12);
        Assert.Equal(200 / 255.0, heights[0, 1], 12);
        Assert.Equal(50 / 255.0, heights[1, 0], 12);
        Assert.Equal(25 / 255.0, heights[1, 1], 12);
    }

    [Fact]
    public void ReadPng_RejectsWhatItCannotRepresent()
    {
        // Palette (color type 3) has no colour-to-height rule without a palette lookup.
        var palette = Assert.Throws<FormatException>(
            () => Heightmap.ReadPng(BuildPng(1, 1, 8, colorType: 3, [[7]], [0])));
        Assert.Contains("Palette", palette.Message);

        var depth = Assert.Throws<FormatException>(
            () => Heightmap.ReadPng(BuildPng(2, 1, 4, colorType: 0, [[0x12]], [0])));
        Assert.Contains("bit depth", depth.Message);

        Assert.Throws<FormatException>(() => Heightmap.ReadPng([1, 2, 3]));
    }

    /// <summary>
    /// A truecolor (RGB) pixel reads its Rec. 709 luminance. The oracle is arithmetic on
    /// values we chose: a pure grey reads its own value (weights sum to 1), a pure primary
    /// reads exactly its weight, and a mixed pixel reads the weighted sum — the documented
    /// rule and nothing inferred.
    /// </summary>
    [Fact]
    public void ReadPng_ColorRgb_UsesRec709Luminance()
    {
        // 3 pixels wide: pure white, pure red, a chosen mix (100, 150, 200).
        byte[][] raw =
        [
            [255, 255, 255,   255, 0, 0,   100, 150, 200],
        ];
        var png = BuildPng(3, 1, 8, colorType: 2, raw, [0]);

        var heights = Heightmap.ReadPng(png);
        Assert.Equal(1.0, heights[0, 0], 12);                                    // white
        Assert.Equal(0.2126, heights[0, 1], 12);                                 // pure red = its weight
        double mix = (0.2126 * 100 + 0.7152 * 150 + 0.0722 * 200) / 255.0;
        Assert.Equal(mix, heights[0, 2], 12);

        // A grey pixel (R=G=B) reads its own normalised value, since the weights sum to 1.
        var grey = Heightmap.ReadPng(BuildPng(1, 1, 8, colorType: 2, [[128, 128, 128]], [0]));
        Assert.Equal(128 / 255.0, grey[0, 0], 12);
    }

    /// <summary>Truecolor + alpha (color type 6) reads the same luminance and ignores the
    /// alpha sample — a fully transparent bright pixel is still bright terrain.</summary>
    [Fact]
    public void ReadPng_ColorRgba_IgnoresAlpha()
    {
        // (R, G, B, A) — alpha 0 and 255 on two identical colours must give one height.
        byte[][] raw =
        [
            [100, 150, 200, 0,   100, 150, 200, 255],
        ];
        var png = BuildPng(2, 1, 8, colorType: 6, raw, [0]);

        var heights = Heightmap.ReadPng(png);
        double expected = (0.2126 * 100 + 0.7152 * 150 + 0.0722 * 200) / 255.0;
        Assert.Equal(expected, heights[0, 0], 12);
        Assert.Equal(expected, heights[0, 1], 12);
    }

    /// <summary>
    /// A corrupted CRITICAL chunk is named by its CRC rather than inflated into wrong
    /// heights. The fixture is a valid PNG with real CRCs whose IHDR payload is then flipped
    /// — the CRC is checked BEFORE the header is parsed, so the file is refused loudly.
    /// </summary>
    [Fact]
    public void ReadPng_CorruptCriticalChunkCrc_IsNamed()
    {
        var png = BuildPng(2, 2, 8, colorType: 0, [[1, 2], [3, 4]], [0, 0]);

        // The IHDR payload starts at offset 16 (8 signature + 4 length + 4 type); flipping a
        // byte there invalidates the IHDR CRC without changing the chunk structure.
        Heightmap.ReadPng(png);                            // valid as built
        png[16] ^= 0xFF;

        var error = Assert.Throws<FormatException>(() => Heightmap.ReadPng(png));
        Assert.Contains("CRC", error.Message);
        Assert.Contains("IHDR", error.Message);
    }

    [Fact]
    public void ReadPng_FeedsShapeHeightmapEndToEnd()
    {
        byte[][] raw =
        [
            [64, 64, 64],
            [64, 255, 64],
            [64, 64, 64],
        ];
        var png = BuildPng(3, 3, 8, colorType: 0, raw, [0, 0, 0]);

        var mesh = Shape.Heightmap(Heightmap.ReadPng(png), cellSize: 5, heightScale: 20).ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(20.0, mesh.ToIndexed().Positions.Max(p => p.Z), 9);
    }

    // ---- a minimal PNG encoder (test-side, REAL CRC-32s — the reader verifies
    // them for critical chunks, and a real file from disk parses identically) --

    [Theory]
    [InlineData(8, 8, 8, 0)]      // every pass non-empty, gray 8
    [InlineData(9, 5, 16, 0)]     // odd sizes, gray 16
    [InlineData(3, 3, 8, 2)]      // truecolor, several passes empty
    [InlineData(1, 1, 8, 0)]      // one pixel: passes 2..7 empty
    [InlineData(2, 2, 8, 0)]      // passes 1, 6, 7 only
    public void ReadPng_Adam7_RecoversExactlyWhatTheNonInterlacedTwinReads(
        int width, int height, int bitDepth, int colorType)
    {
        // The twin oracle: the SAME raster written straight and interlaced must read
        // back as the SAME heights, bit for bit — and the interlaced encoder cycles
        // Up/Sub/Paeth filters WITHIN each pass, so a reader whose previous-row buffer
        // crossed pass boundaries (or read the image's own rows) fails loudly.
        int channels = colorType == 2 ? 3 : 1;
        int stride = width * (bitDepth / 8) * channels;
        var raw = new byte[height][];
        for (int y = 0; y < height; y++)
        {
            raw[y] = new byte[stride];
            for (int i = 0; i < stride; i++)
                raw[y][i] = (byte)(17 + 31 * y + 7 * i);   // deterministic, non-constant
        }

        var straightFilters = new byte[height];
        for (int y = 0; y < height; y++)
            straightFilters[y] = (byte)(y % 5);
        var straight = Heightmap.ReadPng(BuildPng(width, height, bitDepth, colorType, raw, straightFilters));
        var interlaced = Heightmap.ReadPng(BuildInterlacedPng(
            width, height, bitDepth, colorType, raw, filterCycle: [2, 1, 0, 4, 3]));

        Assert.Equal(straight.GetLength(0), interlaced.GetLength(0));
        Assert.Equal(straight.GetLength(1), interlaced.GetLength(1));
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                Assert.Equal(straight[y, x], interlaced[y, x]);
    }

    [Fact]
    public void ReadPng_AnUndefinedInterlaceMethod_IsRefusedByName()
    {
        // 0 and 1 exist; 2 is nothing, and guessing would decode garbage confidently.
        var png = BuildPng(2, 2, 8, colorType: 0, [[1, 2], [3, 4]], [0, 0], interlace: 2);
        var thrown = Assert.Throws<FormatException>(() => Heightmap.ReadPng(png));
        Assert.Contains("interlace", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildPng(int width, int height, int bitDepth, int colorType, byte[][] rawRows, byte[] filters, byte interlace = 0)
    {
        int channels = colorType switch { 2 => 3, 6 => 4, 4 => 2, _ => 1 };
        int bpp = (bitDepth / 8) * channels;
        int stride = width * bpp;

        // Filter each row per the spec (the encoder side of what the reader undoes).
        using var image = new MemoryStream();
        FilterRows(image, rawRows, filters, bpp, stride);

        using var idat = new MemoryStream();
        using (var deflate = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(image.ToArray());

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", header =>
        {
            WriteInt(header, width);
            WriteInt(header, height);
            header.WriteByte((byte)bitDepth);
            header.WriteByte((byte)colorType);
            header.WriteByte(0);
            header.WriteByte(0);
            header.WriteByte(interlace);
        });
        WriteChunk(png, "IDAT", chunk => idat.WriteTo(chunk));
        WriteChunk(png, "IEND", _ => { });
        return png.ToArray();
    }

    /// <summary>The encoder side of the reader's per-row unfilter, over ONE scanline
    /// stream: each row filtered against the previous row OF THIS STREAM — which for an
    /// Adam7 pass is the pass's own previous row, the property the interlace tests
    /// exist to prove the reader honours.</summary>
    private static void FilterRows(MemoryStream image, byte[][] rawRows, byte[] filters, int bpp, int stride)
    {
        var previous = new byte[stride];
        for (int r = 0; r < rawRows.Length; r++)
        {
            var raw = rawRows[r];
            var encoded = new byte[stride];
            for (int i = 0; i < stride; i++)
            {
                int left = i >= bpp ? raw[i - bpp] : 0;
                int up = previous[i];
                int upLeft = i >= bpp ? previous[i - bpp] : 0;
                encoded[i] = filters[r] switch
                {
                    0 => raw[i],
                    1 => (byte)(raw[i] - left),
                    2 => (byte)(raw[i] - up),
                    3 => (byte)(raw[i] - (left + up) / 2),
                    4 => (byte)(raw[i] - Paeth(left, up, upLeft)),
                    _ => throw new InvalidOperationException(),
                };
            }
            image.WriteByte(filters[r]);
            image.Write(encoded);
            if (raw.Length == stride)
                previous = raw;
        }
    }

    /// <summary>
    /// The Adam7 TWIN encoder: splits a full raster into the seven passes, filters each
    /// pass as its own scanline stream (cycling the stated filters within each pass, so
    /// Up/Sub rows prove the reader keeps pass streams separate), and writes one
    /// interlaced PNG. An empty pass — a small image — contributes no bytes at all,
    /// which is the other property the reader must honour.
    /// </summary>
    private static byte[] BuildInterlacedPng(
        int width, int height, int bitDepth, int colorType, byte[][] rawRows, byte[] filterCycle)
    {
        int channels = colorType switch { 2 => 3, 6 => 4, 4 => 2, _ => 1 };
        int bpp = (bitDepth / 8) * channels;
        (int X0, int Y0, int Dx, int Dy)[] passes =
        [
            (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4),
            (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2),
        ];
        using var image = new MemoryStream();
        foreach (var (x0, y0, dx, dy) in passes)
        {
            int passWidth = width > x0 ? (width - x0 + dx - 1) / dx : 0;
            int passHeight = height > y0 ? (height - y0 + dy - 1) / dy : 0;
            if (passWidth == 0 || passHeight == 0)
                continue;
            var passRows = new byte[passHeight][];
            var passFilters = new byte[passHeight];
            for (int row = 0; row < passHeight; row++)
            {
                var line = new byte[passWidth * bpp];
                for (int col = 0; col < passWidth; col++)
                {
                    int sy = y0 + row * dy, sx = x0 + col * dx;
                    Array.Copy(rawRows[sy], sx * bpp, line, col * bpp, bpp);
                }
                passRows[row] = line;
                passFilters[row] = filterCycle[row % filterCycle.Length];
            }
            FilterRows(image, passRows, passFilters, bpp, passWidth * bpp);
        }

        using var idat = new MemoryStream();
        using (var deflate = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(image.ToArray());
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", header =>
        {
            WriteInt(header, width);
            WriteInt(header, height);
            header.WriteByte((byte)bitDepth);
            header.WriteByte((byte)colorType);
            header.WriteByte(0);
            header.WriteByte(0);
            header.WriteByte(1);                           // Adam7
        });
        WriteChunk(png, "IDAT", chunk => idat.WriteTo(chunk));
        WriteChunk(png, "IEND", _ => { });
        return png.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(MemoryStream png, string type, Action<MemoryStream> payload)
    {
        using var body = new MemoryStream();
        payload(body);
        WriteInt(png, (int)body.Length);

        // The CRC is over the type bytes AND the payload, so build that span and hash it.
        using var crcInput = new MemoryStream();
        foreach (char c in type)
            crcInput.WriteByte((byte)c);
        body.WriteTo(crcInput);

        crcInput.Position = 0;
        crcInput.WriteTo(png);                             // type + payload into the file
        WriteInt(png, unchecked((int)Crc32(crcInput.ToArray())));
    }

    private static void WriteInt(MemoryStream stream, int value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFFu;
    }
}
