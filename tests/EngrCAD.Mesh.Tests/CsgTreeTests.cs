using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// The BSP path's tree walks used to be recursive, and the splitting plane is the first
/// polygon's plane — so a convex body (a sphere above all) builds an essentially degenerate
/// chain whose depth is O(polygons). Two 32k-triangle spheres killed the process with a stack
/// overflow inside <c>CsgNode.Build</c>. These pin the iterative walks.
/// </summary>
public class CsgTreeTests
{
    // Measured on this machine: the recursive Build overflowed the 1 MB stack somewhere
    // between depth 3000 (ok, 419 ms) and 4000 (crash). 5000 is comfortably past it while
    // staying a second or so of work, since Build is inherently O(depth x polygons).
    private const int ChainDepth = 5000;

    /// <summary>
    /// n triangles in parallel planes, each entirely in front of the previous plane: no
    /// polygon is ever split, so the tree is a chain of exactly n nodes.
    /// </summary>
    private static List<CsgPolygon> ParallelChain(int count, double z0 = 0)
    {
        var polygons = new List<CsgPolygon>(count);
        for (int i = 0; i < count; i++)
        {
            double z = z0 + i;
            List<Vector3d> vertices = [new(0, 0, z), new(1, 0, z), new(0, 1, z)];
            Assert.True(CsgPlane.TryFromPoints(vertices[0], vertices[1], vertices[2], out var plane));
            polygons.Add(new CsgPolygon(vertices, plane));
        }
        return polygons;
    }

    [Fact]
    public void DeepTree_BuildInvertAndEnumerate_DoNotOverflowTheStack()
    {
        var tree = new CsgNode(ParallelChain(ChainDepth));
        Assert.Equal(ChainDepth, tree.AllPolygons().Count);

        tree.Invert();
        Assert.Equal(ChainDepth, tree.AllPolygons().Count);
        // Inverting flips every polygon: the chain was built from +Z-facing triangles.
        foreach (var polygon in tree.AllPolygons())
            Assert.True(polygon.Plane.Normal.Z < 0);

        // Building into an existing deep tree descends the whole chain to reach the leaf.
        tree.Build(ParallelChain(8, z0: -ChainDepth - 1.0));
        Assert.Equal(ChainDepth + 8, tree.AllPolygons().Count);
    }

    [Fact]
    public void DeepTree_ClipToAndClipPolygons_DoNotOverflowTheStack()
    {
        var deep = new CsgNode(ParallelChain(ChainDepth));

        // ClipTo walks the receiver's chain (depth n) ...
        var shallow = new CsgNode(ParallelChain(1, z0: -1));
        deep.ClipTo(shallow);
        Assert.Equal(ChainDepth, deep.AllPolygons().Count); // all in front of z = -1: nothing removed

        // ... and ClipPolygons descends the argument's chain (depth n).
        shallow.ClipTo(deep);
        Assert.Empty(shallow.AllPolygons()); // z = -1 is behind the whole stack: fully clipped
    }
}
