using System.Globalization;
using System.Xml.Linq;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class VtuWriterTests
{
    private static XElement WriteAndParse(HalfEdgeMesh mesh, params MeshField[] fields)
    {
        using var writer = new StringWriter();
        VtuWriter.Write(mesh, fields, writer);
        return XDocument.Parse(writer.ToString()).Root!;
    }

    private static double[] Numbers(XElement array) =>
        [.. array.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t switch
            {
                "NaN" => double.NaN,
                "Infinity" => double.PositiveInfinity,
                "-Infinity" => double.NegativeInfinity,
                _ => double.Parse(t, CultureInfo.InvariantCulture),
            })];

    private static XElement Array(XElement root, string container, string name) =>
        root.Descendants(container).Single().Elements("DataArray")
            .Single(e => (string?)e.Attribute("Name") == name);

    [Fact]
    public void Write_HasTheVtkUnstructuredGridSkeleton()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        var root = WriteAndParse(box);

        // The shape a ParaView/VTK XML reader requires: VTKFile[type,version,byte_order]
        // > UnstructuredGrid > Piece[NumberOfPoints,NumberOfCells] > Points + Cells.
        Assert.Equal("VTKFile", root.Name.LocalName);
        Assert.Equal("UnstructuredGrid", (string?)root.Attribute("type"));
        Assert.Equal("1.0", (string?)root.Attribute("version"));
        Assert.Equal("LittleEndian", (string?)root.Attribute("byte_order"));

        var grid = Assert.Single(root.Elements("UnstructuredGrid"));
        var piece = Assert.Single(grid.Elements("Piece"));
        Assert.Equal("8", (string?)piece.Attribute("NumberOfPoints"));
        Assert.Equal("6", (string?)piece.Attribute("NumberOfCells"));

        var points = Assert.Single(piece.Elements("Points")).Elements("DataArray").Single();
        Assert.Equal("Float64", (string?)points.Attribute("type"));
        Assert.Equal("3", (string?)points.Attribute("NumberOfComponents"));
        Assert.Equal("ascii", (string?)points.Attribute("format"));
        Assert.Equal(24, Numbers(points).Length);

        var cells = Assert.Single(piece.Elements("Cells"));
        Assert.Equal(
            ["connectivity", "offsets", "types"],
            cells.Elements("DataArray").Select(e => (string)e.Attribute("Name")!).ToArray());
        // No results attached: no PointData element at all (rather than an empty one).
        Assert.Empty(piece.Elements("PointData"));
    }

    [Fact]
    public void Write_OffsetsAreThePrefixSumsAndTypesMatchTheFaceSizes()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        var root = WriteAndParse(box);

        double[] offsets = Numbers(Array(root, "Cells", "offsets"));
        double[] types = Numbers(Array(root, "Cells", "types"));
        double[] connectivity = Numbers(Array(root, "Cells", "connectivity"));

        // A box's six faces are quads: offsets 4, 8, ..., 24 and cell type 9 (VTK_QUAD).
        Assert.Equal([4, 8, 12, 16, 20, 24], offsets);
        Assert.Equal(24, connectivity.Length);
        Assert.All(types, t => Assert.Equal((double)(int)VtkCellType.Quad, t));
        Assert.All(connectivity, i => Assert.InRange(i, 0, 7));
    }

    [Fact]
    public void Write_TriangulatedMeshUsesTheTriangleCellType()
    {
        var sphere = MeshPrimitives.UvSphere(1, 8, 4);
        var root = WriteAndParse(sphere);
        double[] types = Numbers(Array(root, "Cells", "types"));

        // A UV sphere's pole rings are triangles and its bands quads — both dedicated
        // types, never the general polygon.
        Assert.All(types, t => Assert.Contains(
            (int)t, new[] { (int)VtkCellType.Triangle, (int)VtkCellType.Quad }));
        Assert.Contains((double)(int)VtkCellType.Triangle, types);
    }

    [Fact]
    public void Write_PointDataCarriesTheFieldsWithTheirNamesAndComponents()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (2, 2, 2)));
        var stress = MeshField.Sample(box, "von Mises", "MPa", p => p.Z * 10);
        var displacement = MeshField.SampleVector(box, "displacement", "mm", p => new Vector3d(0, 0, p.Z));
        var root = WriteAndParse(box, stress, displacement);

        var pointData = Assert.Single(root.Descendants("PointData"));
        // The default-selection hints name the first scalar and the first vector.
        Assert.Equal("von Mises", (string?)pointData.Attribute("Scalars"));
        Assert.Equal("displacement", (string?)pointData.Attribute("Vectors"));

        var scalarArray = Array(root, "PointData", "von Mises");
        Assert.Equal("1", (string?)scalarArray.Attribute("NumberOfComponents"));
        Assert.Equal(box.VertexCount, Numbers(scalarArray).Length);

        var vectorArray = Array(root, "PointData", "displacement");
        Assert.Equal("3", (string?)vectorArray.Attribute("NumberOfComponents"));
        Assert.Equal(box.VertexCount * 3, Numbers(vectorArray).Length);

        // Values land against the points they belong to, not shuffled.
        double[] values = Numbers(scalarArray);
        for (int v = 0; v < box.VertexCount; v++)
            Assert.Equal(box.GetPosition(v).Z * 10, values[v], 12);
    }

    [Fact]
    public void Write_RefusesAFieldOfTheWrongLength()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        var wrong = MeshField.Scalar("s", "", [1, 2, 3]);
        var thrown = Assert.Throws<ArgumentException>(
            () => VtuWriter.Write(box, [wrong], new StringWriter()));
        Assert.Contains("3 vertices", thrown.Message);
        Assert.Contains("8 points", thrown.Message);
    }

    [Fact]
    public void Write_MergedParts_ConcatenatesPointsAndOffsetsTheConnectivity()
    {
        var a = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        var b = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        using var writer = new StringWriter();
        VtuWriter.Write(
            [(a, Matrix4d.Identity, System.Array.Empty<MeshField>()),
             (b, Matrix4d.CreateTranslation((5, 0, 0)), System.Array.Empty<MeshField>())],
            writer);
        var root = XDocument.Parse(writer.ToString()).Root!;

        var piece = root.Descendants("Piece").Single();
        Assert.Equal("16", (string?)piece.Attribute("NumberOfPoints"));
        Assert.Equal("12", (string?)piece.Attribute("NumberOfCells"));

        double[] points = Numbers(piece.Elements("Points").Single().Elements("DataArray").Single());
        // The second part's points carry its transform.
        Assert.Equal(1, points.Take(24).Where((_, i) => i % 3 == 0).Max(), 12);
        Assert.Equal(6, points.Skip(24).Where((_, i) => i % 3 == 0).Max(), 12);

        double[] connectivity = Numbers(Array(root, "Cells", "connectivity"));
        Assert.All(connectivity.Take(24), i => Assert.InRange(i, 0, 7));
        Assert.All(connectivity.Skip(24), i => Assert.InRange(i, 8, 15));
    }

    [Fact]
    public void Write_MergedParts_FillsAMissingArrayWithNaN()
    {
        var a = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        var b = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        var stress = MeshField.Sample(a, "stress", "MPa", _ => 42);
        using var writer = new StringWriter();
        VtuWriter.Write(
            [(a, Matrix4d.Identity, new[] { stress }),
             (b, Matrix4d.CreateTranslation((5, 0, 0)), System.Array.Empty<MeshField>())],
            writer);
        var root = XDocument.Parse(writer.ToString()).Root!;

        double[] values = Numbers(Array(root, "PointData", "stress"));
        Assert.Equal(16, values.Length);
        // The part that HAS the result keeps it; the part that does not gets VTK's own
        // "no value" rather than an invented zero (which would read as a safe region).
        Assert.All(values.Take(8), v => Assert.Equal(42, v));
        Assert.All(values.Skip(8), v => Assert.True(double.IsNaN(v)));
    }

    [Fact]
    public void Write_MergedParts_RefusesOneNameUsedAsTwoShapes()
    {
        var a = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        var b = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        var thrown = Assert.Throws<ArgumentException>(() => VtuWriter.Write(
            [(a, Matrix4d.Identity, new[] { MeshField.Sample(a, "u", "mm", _ => 1) }),
             (b, Matrix4d.Identity, new[] { MeshField.SampleVector(b, "u", "mm", _ => Vector3d.UnitZ) })],
            new StringWriter()));
        Assert.Contains("cannot be both", thrown.Message);
    }

    [Fact]
    public void Write_EscapesArrayNamesSoTheXmlStaysWellFormed()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        var field = MeshField.Sample(box, "S \"max\" & <min>", "", _ => 0);
        var root = WriteAndParse(box, field);

        Assert.Equal("S \"max\" & <min>",
            (string?)Array(root, "PointData", "S \"max\" & <min>").Attribute("Name"));
    }

    [Fact]
    public void CellTypeFor_PicksTheDedicatedTypesAndTheGeneralPolygon()
    {
        Assert.Equal(VtkCellType.Triangle, VtuWriter.CellTypeFor(3));
        Assert.Equal(VtkCellType.Quad, VtuWriter.CellTypeFor(4));
        Assert.Equal(VtkCellType.Polygon, VtuWriter.CellTypeFor(7));
        Assert.Throws<ArgumentOutOfRangeException>(() => VtuWriter.CellTypeFor(2));
    }

    [Fact]
    public void Write_TetCells_GoThroughTheSameSeamAsTriangles()
    {
        // The seam a volumetric mesher plugs into: nothing about the writer changes,
        // only the cell type. One tetrahedron, one scalar result.
        Vector3d[] points = [(0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1)];
        using var writer = new StringWriter();
        VtuWriter.Write(points, [new[] { 0, 1, 2, 3 }], [VtkCellType.Tetra],
            [MeshField.Scalar("T", "K", [300, 310, 320, 330])], writer);
        var root = XDocument.Parse(writer.ToString()).Root!;

        Assert.Equal("4", (string?)root.Descendants("Piece").Single().Attribute("NumberOfPoints"));
        Assert.Equal("1", (string?)root.Descendants("Piece").Single().Attribute("NumberOfCells"));
        Assert.Equal([10], Numbers(Array(root, "Cells", "types")));
        Assert.Equal([4], Numbers(Array(root, "Cells", "offsets")));
        Assert.Equal([300, 310, 320, 330], Numbers(Array(root, "PointData", "T")));
    }
}
