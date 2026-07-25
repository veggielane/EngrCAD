using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class MeshSelectionTests
{
    private static int FaceWithNormal(HalfEdgeMesh mesh, Vector3d normal) =>
        mesh.Faces.First(f => f.Normal().Dot(normal) > 0.9).Index;

    /// <summary>Flat quad grid in z = 0 over [0, nx] × [0, ny], one unit quad per cell.</summary>
    private static HalfEdgeMesh Grid(int nx, int ny)
    {
        var positions = new List<Vector3d>();
        for (int j = 0; j <= ny; j++)
        {
            for (int i = 0; i <= nx; i++)
                positions.Add(new Vector3d(i, j, 0));
        }
        int V(int i, int j) => j * (nx + 1) + i;
        var faces = new List<int[]>();
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
                faces.Add([V(i, j), V(i + 1, j), V(i + 1, j + 1), V(i, j + 1)]);
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    /// <summary>Two disjoint boxes in one mesh (2×2×2 at origin, translated 1×1×1 beside it).</summary>
    private static HalfEdgeMesh TwoBoxes()
    {
        var (pa, fa) = MeshPrimitives.Box(2, 2, 2).ToIndexed();
        var (pb, fb) = MeshPrimitives.Box(1, 1, 1)
            .Transformed(Matrix4d.CreateTranslation(new Vector3d(10, 0, 0))).ToIndexed();
        var positions = new List<Vector3d>(pa);
        positions.AddRange(pb);
        var faces = new List<int[]>(fa);
        foreach (var f in fb)
            faces.Add([.. f.Select(v => v + pa.Length)]);
        return HalfEdgeMesh.Build(positions, faces);
    }

    // ---- MeshFaceSelection ----

    [Fact]
    public void FaceSelection_GrowOnBox_OneRingThenAll()
    {
        var box = MeshPrimitives.Box(1, 1, 1);
        var top = MeshFaceSelection.FromIndices(box, [FaceWithNormal(box, Vector3d.UnitZ)]);

        var grown = top.Grow();
        Assert.Equal(5, grown.Count); // top + 4 vertex-adjacent sides (not the bottom)
        Assert.Equal(6, grown.Grow().Count);
        Assert.Equal(1, top.Count); // original untouched (immutable)
    }

    [Fact]
    public void FaceSelection_ContractOnBox_RemovesBorderTouchingFaces()
    {
        var box = MeshPrimitives.Box(1, 1, 1);
        int bottom = FaceWithNormal(box, -Vector3d.UnitZ);
        var allButBottom = MeshFaceSelection.FromIndices(box,
            Enumerable.Range(0, 6).Where(f => f != bottom));

        var contracted = allButBottom.Contract();
        // Border vertices = the bottom 4 (they belong to the unselected bottom face);
        // the 4 sides touch them, the top does not.
        Assert.Equal(1, contracted.Count);
        Assert.True(contracted.Contains(FaceWithNormal(box, Vector3d.UnitZ)));
        Assert.Empty(contracted.Contract().Indices);
    }

    [Fact]
    public void FaceSelection_GrowOnGrid_VertexAdjacency()
    {
        var grid = Grid(7, 7);
        // Center cell (3,3) → face index 3*7+3 = 24; the 5×5 grown block stays clear of the
        // grid edge so contracting sees genuine unselected border faces.
        var center = MeshFaceSelection.FromIndices(grid, [24]);
        Assert.Equal(9, center.Grow().Count);      // 3×3 block (vertex adjacency includes diagonals)
        Assert.Equal(25, center.Grow(2).Count);    // 5×5 block
        Assert.Equal(9, center.Grow(2).Contract().Count); // contract undoes one ring
    }

    [Fact]
    public void FaceSelection_BoundaryLoop_TopFace_FourHalfEdges()
    {
        var box = MeshPrimitives.Box(1, 1, 1);
        int top = FaceWithNormal(box, Vector3d.UnitZ);
        var selection = MeshFaceSelection.FromIndices(box, [top]);

        var boundary = selection.BoundaryHalfEdges();
        Assert.Equal(4, boundary.Count);
        Assert.All(boundary, he => Assert.Equal(top, he.Face.Index));

        var loop = Assert.Single(selection.BoundaryLoops());
        Assert.Equal(4, loop.Count);
        // Chained: each successor starts where the previous ended.
        for (int i = 0; i < loop.Count; i++)
            Assert.Equal(loop[i].Destination, loop[(i + 1) % loop.Count].Origin);
    }

    [Fact]
    public void FaceSelection_BoundaryLoops_TwoFaces_SingleLoopOfSix()
    {
        var box = MeshPrimitives.Box(1, 1, 1);
        var selection = MeshFaceSelection.FromIndices(box,
            [FaceWithNormal(box, Vector3d.UnitZ), FaceWithNormal(box, Vector3d.UnitX)]);

        var loop = Assert.Single(selection.BoundaryLoops());
        Assert.Equal(6, loop.Count); // 4 + 4 minus the shared edge's two half-edges
    }

    [Fact]
    public void FaceSelection_Conversions_VerticesAndEdges()
    {
        var box = MeshPrimitives.Box(1, 1, 1);
        int top = FaceWithNormal(box, Vector3d.UnitZ);
        var selection = MeshFaceSelection.FromIndices(box, [top]);

        Assert.Equal(4, selection.ToVertices().Count);
        Assert.Equal(4, selection.ToEdges().Count);
        Assert.Equal(1.0, selection.Area(), 12);
    }

    [Fact]
    public void FaceSelection_ToMesh_ExtractsPatch()
    {
        var box = MeshPrimitives.Box(2, 3, 4);
        int top = FaceWithNormal(box, Vector3d.UnitZ);
        var patch = MeshFaceSelection.FromIndices(box, [top]).ToMesh();

        patch.Validate();
        Assert.Equal(1, patch.FaceCount);
        Assert.Equal(4, patch.VertexCount);
        Assert.Equal(box.GetFace(top).Area, patch.GetFace(0).Area, 12);
    }

    [Fact]
    public void FaceSelection_ToMesh_PinchSelection_ThrowsWithContext()
    {
        // Diagonal cells of a 2×2 grid share only the center vertex — a bow-tie extraction.
        var grid = Grid(2, 2);
        var diagonal = MeshFaceSelection.FromIndices(grid, [0, 3]);
        var ex = Assert.Throws<ArgumentException>(() => diagonal.ToMesh());
        Assert.Contains("pinch", ex.Message);
    }

    // ---- MeshVertexSelection ----

    [Fact]
    public void VertexSelection_GrowContract_OnBoxCorner()
    {
        var box = MeshPrimitives.Box(1, 1, 1);
        var corner = MeshVertexSelection.FromIndices(box, [0]);

        var grown = corner.Grow();
        Assert.Equal(4, grown.Count); // corner + its 3 neighbors

        var back = grown.Contract();
        Assert.Equal(1, back.Count); // only the corner keeps all-selected neighbors
        Assert.True(back.Contains(0));
    }

    [Fact]
    public void VertexSelection_ToFaces_RequireAllVersusAny()
    {
        var box = MeshPrimitives.Box(1, 1, 1);
        int top = FaceWithNormal(box, Vector3d.UnitZ);
        var topVertices = MeshFaceSelection.FromIndices(box, [top]).ToVertices();

        var strict = topVertices.ToFaces();
        Assert.Equal(1, strict.Count);
        Assert.True(strict.Contains(top));

        var loose = topVertices.ToFaces(requireAll: false);
        Assert.Equal(5, loose.Count); // top + 4 sides touching the rim
    }

    // ---- MeshEdgeSelection ----

    [Fact]
    public void EdgeSelection_CanonicalizesAndGrows()
    {
        var box = MeshPrimitives.Box(1, 1, 1);
        var edge = box.Edges.First();
        var selection = MeshEdgeSelection.FromHalfEdgeIndices(box, [edge.Index, edge.Twin.Index]);
        Assert.Equal(1, selection.Count); // twin pair collapses to one undirected edge
        Assert.True(selection.Contains(edge.Twin.Index));

        // A cube vertex has 3 incident edges; growing one edge reaches every edge at both
        // endpoints: 1 + 2 + 2 = 5.
        Assert.Equal(5, selection.Grow().Count);
        Assert.Equal(2, selection.ToVertices().Count);
    }

    [Fact]
    public void EdgeSelection_ContractRemovesBorder()
    {
        var box = MeshPrimitives.Box(1, 1, 1);
        var all = MeshEdgeSelection.FromHalfEdgeIndices(box, box.Edges.Select(e => e.Index));
        Assert.Equal(12, all.Count);
        Assert.Equal(12, all.Contract().Count); // no unselected edge anywhere: nothing is border

        var one = MeshEdgeSelection.FromHalfEdgeIndices(box, [box.Edges.First().Index]);
        Assert.Equal(0, one.Contract().Count); // both endpoints touch unselected edges
    }

    // ---- MeshConnectedComponents ----

    [Fact]
    public void Components_SingleClosedMesh_OneComponent()
    {
        var sphere = MeshPrimitives.UvSphere(1, 12, 6);
        var component = Assert.Single(MeshConnectedComponents.Find(sphere));

        Assert.Equal(sphere.FaceCount, component.FaceCount);
        Assert.True(component.IsClosed);
        Assert.Equal(sphere.SurfaceArea(), component.Area, 12);
        Assert.Equal(sphere.Volume(), component.SignedVolume, 12);
    }

    [Fact]
    public void Components_TwoBodies_MetricsAndExtractionRoundTrip()
    {
        var mesh = TwoBoxes();
        var components = MeshConnectedComponents.Find(mesh);

        Assert.Equal(2, components.Count);
        Assert.All(components, c => Assert.True(c.IsClosed));
        Assert.Equal(6, components[0].FaceCount);
        Assert.Equal(8.0, components[0].SignedVolume, 12);   // 2×2×2, seeded at face 0
        Assert.Equal(1.0, components[1].SignedVolume, 12);   // 1×1×1
        Assert.Equal(24.0, components[0].Area, 12);
        Assert.Equal(6.0, components[1].Area, 12);

        var small = components[1].ToMesh();
        small.Validate();
        Assert.True(small.IsClosed);
        Assert.Equal(1.0, small.Volume(), 12);
        Assert.Equal(8, small.VertexCount);
    }

    [Fact]
    public void Components_OpenPatch_ReportedNotClosed()
    {
        var grid = Grid(3, 3);
        var component = Assert.Single(MeshConnectedComponents.Find(grid));
        Assert.False(component.IsClosed);
        Assert.Equal(9.0, component.Area, 12);
        var extracted = component.ToMesh();
        extracted.Validate();
        Assert.Equal(grid.FaceCount, extracted.FaceCount);
    }

    [Fact]
    public void Components_Separate_ExtractsEachBody()
    {
        var meshes = MeshConnectedComponents.Separate(TwoBoxes());
        Assert.Equal(2, meshes.Count);
        Assert.All(meshes, m =>
        {
            m.Validate();
            Assert.True(m.IsClosed);
        });
        Assert.Equal(8.0, meshes[0].Volume(), 12);
        Assert.Equal(1.0, meshes[1].Volume(), 12);
    }
}
