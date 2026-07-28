using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// <see cref="Shape.From(string)"/> — the import sugar over
/// <c>MeshReader.ReadAndRepair</c>: a mesh file comes back as a mesh-backed shape that
/// composes with the rest of the vocabulary, with the repair report available on the
/// out-parameter overload.
/// </summary>
public class ShapeImportTests
{
    private static string TempFile(string extension) =>
        Path.Combine(Path.GetTempPath(), $"engrcad-import-{Guid.NewGuid():N}{extension}");

    [Fact]
    public void StlRoundTrip_ImportsAsAClosedMeshShape()
    {
        // STL is an unindexed facet soup by design, so writing and re-importing
        // exercises the weld + repair pipeline on every run.
        var path = TempFile(".stl");
        try
        {
            var source = Shape.Box(20, 10, 5).ToMesh();
            StlWriter.WriteFile(source, path);

            var imported = Shape.From(path, out var report);
            var mesh = imported.ToMesh();
            Assert.True(mesh.IsClosed);
            Assert.Equal(source.Volume(), mesh.Volume(), 6);   // float32 quantization
            Assert.Equal(1, report.ComponentCount);

            // And it composes: boolean against ordinary shapes still works.
            var drilled = imported - Shape.Cylinder(2, 20);
            Assert.True(drilled.ToMesh().IsClosed);
            Assert.True(drilled.ToMesh().Volume() < mesh.Volume());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OffRoundTrip_ImportsExactly()
    {
        var path = TempFile(".off");
        try
        {
            var source = Shape.Cone(6, 2, 9).ToMesh();
            OffWriter.WriteFile(source, path);
            var mesh = Shape.From(path).ToMesh();
            Assert.True(mesh.IsClosed);
            Assert.Equal(source.Volume(), mesh.Volume(), 12);   // R-format doubles
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnsupportedExtension_ThrowsByName()
    {
        var exception = Assert.Throws<NotSupportedException>(() => Shape.From("model.step"));
        Assert.Contains(".stl", exception.Message);
    }
}
