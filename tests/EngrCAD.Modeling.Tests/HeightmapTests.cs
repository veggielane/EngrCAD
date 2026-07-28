using System.IO.Compression;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Heightmap terrain (OpenSCAD's <c>surface()</c>): exact prismatoid volumes for the
/// solid builder (planar tops triangulate exactly), the <c>.dat</c> text reader, and
/// the hand-rolled grayscale PNG reader against bytes assembled by the test itself —
/// every scanline filter exercised, every expectation a value we wrote.
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
        var color = Assert.Throws<FormatException>(
            () => Heightmap.ReadPng(BuildPng(1, 1, 8, colorType: 2, [[1, 2, 3]], [0])));
        Assert.Contains("grayscale", color.Message);

        var depth = Assert.Throws<FormatException>(
            () => Heightmap.ReadPng(BuildPng(2, 1, 4, colorType: 0, [[0x12]], [0])));
        Assert.Contains("bit depth", depth.Message);

        Assert.Throws<FormatException>(() => Heightmap.ReadPng([1, 2, 3]));
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

    // ---- a minimal PNG encoder (test-side, dummy CRCs — the reader does not
    // verify them; a real file from disk parses identically) -------------------

    private static byte[] BuildPng(int width, int height, int bitDepth, int colorType, byte[][] rawRows, byte[] filters)
    {
        int bpp = (bitDepth / 8) * (colorType == 4 ? 2 : 1);
        int stride = width * bpp;

        // Filter each row per the spec (the encoder side of what the reader undoes).
        using var image = new MemoryStream();
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
            header.WriteByte(0);                           // not interlaced
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
        foreach (char c in type)
            png.WriteByte((byte)c);
        body.WriteTo(png);
        png.Write([0, 0, 0, 0]);                           // dummy CRC (unverified)
    }

    private static void WriteInt(MemoryStream stream, int value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}
