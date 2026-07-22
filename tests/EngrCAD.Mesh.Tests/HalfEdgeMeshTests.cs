using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class HalfEdgeMeshTests
{
    private static HalfEdgeMesh SingleTriangle() => HalfEdgeMesh.Build(
        [(0, 0, 0), (1, 0, 0), (0, 1, 0)],
        [new[] { 0, 1, 2 }]);

    /// <summary>2×2 grid of quads in the XY plane: 9 vertices, 4 faces, open boundary.</summary>
    private static HalfEdgeMesh QuadGrid()
    {
        var positions = new List<Vector3d>();
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
                positions.Add((x, y, 0));
        }
        var faces = new List<int[]>();
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                int i = y * 3 + x;
                faces.Add([i, i + 1, i + 4, i + 3]);
            }
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    [Fact]
    public void Box_CountsAndEuler()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 2, 3)));
        box.Validate();
        Assert.Equal(8, box.VertexCount);
        Assert.Equal(12, box.EdgeCount);
        Assert.Equal(6, box.FaceCount);
        Assert.Equal(2, box.EulerCharacteristic);
        Assert.True(box.IsClosed);
        Assert.Empty(box.BoundaryLoops());
    }

    [Fact]
    public void Box_VolumeAndArea()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (2, 3, 4)));
        Assert.Equal(24, box.Volume(), 9);
        Assert.Equal(2 * (6 + 8 + 12), box.SurfaceArea(), 9);
    }

    [Fact]
    public void Box_FaceNormalsAreOutwardUnitAxes()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        var sum = Vector3d.Zero;
        foreach (var face in box.Faces)
        {
            var n = face.Normal();
            Assert.Equal(1.0, n.Length, 12);
            // Axis-aligned: exactly one non-zero component.
            int nonZero = 0;
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(n[i]) > 0.5) nonZero++;
            }
            Assert.Equal(1, nonZero);
            // Outward: points away from the center.
            Assert.True(n.Dot(face.Centroid() - (Vector3d)(0.5, 0.5, 0.5)) > 0);
            sum += n;
        }
        Assert.True(sum.IsZero(Tolerance.Default));
    }

    [Fact]
    public void Box_CornerTopology()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        foreach (var vertex in box.Vertices)
        {
            Assert.Equal(3, vertex.Valence);
            Assert.Equal(3, vertex.IncidentFaces().Count());
            Assert.Equal(3, vertex.Neighbors().Count());
            Assert.False(vertex.IsBoundary);
        }
    }

    [Fact]
    public void SingleTriangle_BoundaryLoop()
    {
        var tri = SingleTriangle();
        tri.Validate();
        Assert.False(tri.IsClosed);
        Assert.Equal(1, tri.EulerCharacteristic); // disk topology
        var loops = tri.BoundaryLoops();
        var loop = Assert.Single(loops);
        Assert.Equal(3, loop.Count);
        Assert.All(loop, h => Assert.True(h.IsBoundary));
        Assert.All(tri.Vertices, v => Assert.True(v.IsBoundary));
    }

    [Fact]
    public void QuadGrid_TopologyAndBoundary()
    {
        var grid = QuadGrid();
        grid.Validate();
        Assert.Equal(9, grid.VertexCount);
        Assert.Equal(12, grid.EdgeCount);
        Assert.Equal(4, grid.FaceCount);
        Assert.Equal(1, grid.EulerCharacteristic);

        var loop = Assert.Single(grid.BoundaryLoops());
        Assert.Equal(8, loop.Count);

        // Center vertex (index 4) is interior with full valence 4.
        var center = grid.GetVertex(4);
        Assert.False(center.IsBoundary);
        Assert.Equal(4, center.Valence);
        Assert.Equal(4, center.IncidentFaces().Count());
    }

    [Fact]
    public void Build_RejectsNonManifoldEdge()
    {
        // Three triangles sharing the edge (0,1).
        var ex = Assert.Throws<ArgumentException>(() => HalfEdgeMesh.Build(
            [(0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1), (0, -1, 0)],
            [new[] { 0, 1, 2 }, new[] { 1, 0, 3 }, new[] { 0, 1, 4 }]));
        Assert.Contains("non-manifold", ex.Message);
    }

    [Fact]
    public void Build_RejectsInconsistentWinding()
    {
        // Two triangles sharing edge (1,2) traversed in the same direction.
        Assert.Throws<ArgumentException>(() => HalfEdgeMesh.Build(
            [(0, 0, 0), (1, 0, 0), (0, 1, 0), (1, 1, 0)],
            [new[] { 0, 1, 2 }, new[] { 3, 1, 2 }]));
    }

    [Fact]
    public void Build_RejectsDegenerateFace()
    {
        Assert.Throws<ArgumentException>(() => HalfEdgeMesh.Build(
            [(0, 0, 0), (1, 0, 0)],
            [new[] { 0, 1, 1 }]));
    }

    [Fact]
    public void Volume_ThrowsOnOpenMesh()
    {
        Assert.Throws<InvalidOperationException>(() => SingleTriangle().Volume());
    }

    [Fact]
    public void HalfEdge_NavigationInvariants()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        foreach (var he in box.HalfEdges)
        {
            Assert.Equal(he, he.Twin.Twin);
            Assert.Equal(he, he.Next.Prev);
            Assert.Equal(he.Destination, he.Twin.Origin);
            Assert.Equal(he.Destination, he.Next.Origin);
        }
    }

    [Fact]
    public void Edges_EnumeratesEachEdgeOnce()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        Assert.Equal(12, box.Edges.Count());
    }

    [Fact]
    public void DihedralAngle_BoxEdgesAreRightAngles()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        foreach (var edge in box.Edges)
            Assert.Equal(Math.PI / 2, edge.DihedralAngle(), 9);
    }

    [Fact]
    public void ToIndexed_RoundTrips()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 2, 3)));
        var (positions, faces) = box.ToIndexed();
        var rebuilt = HalfEdgeMesh.Build(positions, faces);
        rebuilt.Validate();
        Assert.Equal(box.VertexCount, rebuilt.VertexCount);
        Assert.Equal(box.FaceCount, rebuilt.FaceCount);
        Assert.Equal(box.Volume(), rebuilt.Volume(), 12);
    }

    [Fact]
    public void ComputeBounds_MatchesInput()
    {
        var bounds = new Aabb((-1, -2, -3), (4, 5, 6));
        var box = MeshPrimitives.Box(bounds);
        Assert.Equal(bounds, box.ComputeBounds());
    }

    [Fact]
    public void LinqTraversal_ComposesNaturally()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));

        // The LINQ-native style the kernel is designed around.
        var topFaces = box.Faces
            .Where(f => f.Normal().Dot(Vector3d.UnitZ) > 0.9)
            .ToList();
        var top = Assert.Single(topFaces);

        var ringAroundTop = top.HalfEdges()
            .Select(h => h.Twin.Face)
            .Distinct()
            .ToList();
        Assert.Equal(4, ringAroundTop.Count);
    }
}
