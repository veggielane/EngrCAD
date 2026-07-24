using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// <see cref="Part.GetFeatureEdges"/>: B-Rep-backed parts take their display edge
/// overlay from the ACTUAL B-Rep edges at display resolution (exact circles stay
/// smooth at any mesh tessellation), everything else falls back to mesh-dihedral
/// extraction, and the result is cached per part like the display mesh.
/// </summary>
public class PartFeatureEdgeTests
{
    [Fact]
    public void BrepShapePart_EdgesStaySmoothAtCoarseTessellation()
    {
        // A cylinder meshed at 16 segments per circle gives 2 x 16 dihedral rim
        // segments (the 22.5-degree side facets are safely under the 30-degree sharp
        // threshold); the B-Rep route samples the exact rim circles at display
        // resolution (>= 96) regardless of the coarse mesh quality.
        var coarse = new MeshQuality { SegmentsPerCircle = 16 };
        var part = new Part("cyl", Shape.Cylinder(2, 5));
        var edges = part.GetFeatureEdges(coarse);

        Assert.Equal(2 * 96, edges.Count);

        // The mesh route on the same part really is coarse — the overlay is finer
        // than anything the tessellation could provide.
        var meshEdges = MeshFeatureEdges.Extract(part.GetMesh(coarse));
        Assert.Equal(2 * 16, meshEdges.Count);
        Assert.True(meshEdges.Count < edges.Count);
    }

    [Fact]
    public void RawBrepPart_UsesTheBrepRouteToo()
    {
        var part = new Part("box", EngrCAD.BRep.SolidFactory.MakeBox(new Aabb((0, 0, 0), (1, 1, 1))));
        Assert.Equal(12, part.GetFeatureEdges().Count);   // one segment per line edge
    }

    [Fact]
    public void MeshPart_FallsBackToDihedralExtraction()
    {
        var mesh = Shape.Box(2, 2, 2).ToMesh();
        var part = new Part("box", mesh);
        var edges = part.GetFeatureEdges();
        Assert.Equal(MeshFeatureEdges.Extract(mesh).Count, edges.Count);
    }

    [Fact]
    public void FeatureEdges_AreCachedPerPart()
    {
        var part = new Part("cyl", Shape.Cylinder(1, 2));
        var first = part.GetFeatureEdges();
        Assert.Same(first, part.GetFeatureEdges(new MeshQuality { SegmentsPerCircle = 8 }));
    }

    [Fact]
    public void DrilledBox_ShowsNoBoreSeamLines()
    {
        // Boolean output: the bore wall is wrap-split into two half-bands sharing two
        // vertical seam edges on one smooth carrier — those must be classified smooth
        // and omitted. The only vertical feature edges are the box's own 4 corners
        // (exact vertical lines); every bore edge is a horizontal rim arc.
        var part = new Part("drilled", Shape.Box(4, 3, 2) - Shape.Cylinder(0.5, 3));
        var edges = part.GetFeatureEdges();

        int verticalSegments = 0;
        foreach (var (a, b) in edges)
        {
            // Exact-construction test: a vertical line edge samples to two points
            // with identical x and y.
            if (a.X == b.X && a.Y == b.Y && a.Z != b.Z)
                verticalSegments++;
        }
        Assert.Equal(4, verticalSegments);

        // And the bore rims are present at display resolution: many more segments
        // than the 12 box edges alone.
        Assert.True(edges.Count > 100, $"expected display-resolution bore rims, got {edges.Count} segments");
    }
}
