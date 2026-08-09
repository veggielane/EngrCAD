using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// The tagged <see cref="MeshWelder.WeldPolygons(IReadOnlyList{IReadOnlyList{Vector3d}},
/// IReadOnlyList{int}, out int[], double, bool)"/> overload: it carries a per-polygon tag
/// onto the surviving faces, in face order, and is otherwise the untagged weld — the seam
/// B-Rep provenance rides through tessellation on.
/// </summary>
public class MeshWelderTaggedTests
{
    private static IReadOnlyList<Vector3d> Tri(Vector3d a, Vector3d b, Vector3d c) => [a, b, c];

    [Fact]
    public void TagsRideOntoSurvivingFacesInOrder_AndDroppedPolygonsDropTheirTags()
    {
        // Three real triangles (tags 10, 20, 30) with a degenerate one (all vertices
        // coincident, tag 99) spliced in the middle: welding drops the degenerate polygon,
        // and its tag must go with it so the remaining tags still line up face for face.
        var polygons = new List<IReadOnlyList<Vector3d>>
        {
            Tri((0, 0, 0), (1, 0, 0), (0, 1, 0)),
            Tri((5, 5, 5), (5, 5, 5), (5, 5, 5)),      // collapses on weld
            Tri((10, 0, 0), (11, 0, 0), (10, 1, 0)),
            Tri((20, 0, 0), (21, 0, 0), (20, 1, 0)),
        };
        var tags = new[] { 10, 99, 20, 30 };

        var mesh = MeshWelder.WeldPolygons(polygons, tags, out var faceTags);

        Assert.Equal(3, mesh.FaceCount);
        Assert.Equal(new[] { 10, 20, 30 }, faceTags);
    }

    [Fact]
    public void OutputMeshIsBitIdenticalToTheUntaggedOverload()
    {
        var polygons = new List<IReadOnlyList<Vector3d>>
        {
            Tri((0, 0, 0), (1, 0, 0), (0, 1, 0)),
            Tri((1, 0, 0), (1, 1, 0), (0, 1, 0)),
            Tri((0, 0, 0), (0, 1, 0), (-1, 0, 0)),
        };
        var tags = new[] { 1, 2, 3 };

        var untagged = MeshWelder.WeldPolygons(polygons, tolerance: 1e-9, zipSeams: true);
        var tagged = MeshWelder.WeldPolygons(polygons, tags, out var faceTags, 1e-9, zipSeams: true);

        Assert.Equal(untagged.VertexCount, tagged.VertexCount);
        Assert.Equal(untagged.FaceCount, tagged.FaceCount);
        var (pa, fa) = untagged.ToIndexed();
        var (pb, fb) = tagged.ToIndexed();
        for (int i = 0; i < pa.Length; i++)
            Assert.Equal(pa[i], pb[i]);
        for (int f = 0; f < fa.Count; f++)
            Assert.Equal(fa[f], fb[f]);
        Assert.Equal(new[] { 1, 2, 3 }, faceTags);
    }

    [Fact]
    public void MismatchedTagCountIsRefused()
    {
        var polygons = new List<IReadOnlyList<Vector3d>> { Tri((0, 0, 0), (1, 0, 0), (0, 1, 0)) };
        Assert.Throws<ArgumentException>(() =>
            MeshWelder.WeldPolygons(polygons, new[] { 1, 2 }, out _));
    }
}
