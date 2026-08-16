using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// Cell-associated fields: the association is part of the field's identity (preserved by
/// every derived operation), the VTU writer routes each field to PointData or CellData by
/// it with counts validated against the right total, and the flat render mesh's
/// source-face map places a cell value on every duplicate of its face's corners.
/// </summary>
public class CellFieldTests
{
    [Fact]
    public void TheAssociation_SurvivesEveryDerivedOperation()
    {
        var cell = new MeshField("q", "", 3, new double[6], FieldAssociation.Cell);
        Assert.Equal(FieldAssociation.Cell, cell.Association);
        Assert.Equal(FieldAssociation.Cell, cell.Magnitude().Association);
        Assert.Equal(FieldAssociation.Cell, cell.Component(0).Association);
        Assert.Equal(FieldAssociation.Cell, cell.Renamed("r").Association);
        Assert.Equal(FieldAssociation.Cell, cell.Scaled(2).Association);
        Assert.Equal(FieldAssociation.Vertex, MeshField.Scalar("s", "", [1.0]).Association);
    }

    [Fact]
    public void TheVtuWriter_RoutesByAssociation_AndValidatesTheRightCount()
    {
        var points = new[]
        {
            Vector3d.Zero, new Vector3d(1, 0, 0), new Vector3d(0, 1, 0), new Vector3d(0, 0, 1),
        };
        var cells = new IReadOnlyList<int>[] { new[] { 0, 1, 2, 3 } };
        var types = new[] { VtkCellType.Tetra };
        var vertexField = MeshField.Scalar("t", "K", [1.0, 2, 3, 4]);
        var cellField = MeshField.CellScalar("quality", "", [0.5]);

        var writer = new StringWriter();
        VtuWriter.Write(points, cells, types, [vertexField, cellField], writer);
        string vtu = writer.ToString();

        Assert.Contains("<PointData", vtu);
        Assert.Contains("<CellData", vtu);
        Assert.Contains("Name=\"quality\"", vtu);
        // The cell block sits after the point block, and carries the cell's one value.
        Assert.True(vtu.IndexOf("<CellData", StringComparison.Ordinal)
            > vtu.IndexOf("</PointData>", StringComparison.Ordinal));

        // A cell field is validated against the CELL count, not the point count.
        Assert.Contains("cells", Assert.Throws<ArgumentException>(() =>
            VtuWriter.Write(points, cells, types,
                [MeshField.CellScalar("bad", "", [1.0, 2])], new StringWriter())).Message);
    }

    [Fact]
    public void TheFlatRenderMesh_MapsEveryDuplicate_ToItsSourceFace()
    {
        var box = MeshPrimitives.Box(2, 2, 2);
        var flat = RenderMesh.CreateFlat(box);
        Assert.Equal(flat.VertexCount, flat.SourceFaces.Length);
        // Every triangle's three vertices name ONE face, and every face is named.
        var seen = new HashSet<int>();
        for (int tri = 0; tri < flat.TriangleCount; tri++)
        {
            int f = flat.SourceFaces[(int)flat.Indices[tri * 3]];
            Assert.Equal(f, flat.SourceFaces[(int)flat.Indices[tri * 3 + 1]]);
            Assert.Equal(f, flat.SourceFaces[(int)flat.Indices[tri * 3 + 2]]);
            seen.Add(f);
        }
        Assert.Equal(box.FaceCount, seen.Count);
        // A smooth mesh honestly carries no face map (shared vertices have no one face).
        Assert.Empty(RenderMesh.CreateSmooth(box).SourceFaces);
    }
}
