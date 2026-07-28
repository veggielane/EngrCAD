using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class LaplacianDeformerTests
{
    [Fact]
    public void LiftedHandle_MakesSmoothBump()
    {
        const int size = 12;
        var grid = LaplacianSmootherTests.PlaneGrid(size);
        int centre = LaplacianSmootherTests.Index(size, 6, 6);

        var deformer = new LaplacianMeshDeformer(grid);
        deformer.SetHandle(centre, new Vector3d(6, 6, 1.0), weight: 100);
        var bumped = deformer.Solve();

        // The handle tracks its target closely at this weight.
        Assert.Equal(1.0, bumped.GetPosition(centre).Z, 2);

        // The bump is the apex, and it decays with distance along the centre row.
        double At(int i) => bumped.GetPosition(LaplacianSmootherTests.Index(size, i, 6)).Z;
        Assert.True(At(6) >= At(5) && At(5) > At(3) && At(3) > At(1),
            $"Bump should decay from the handle: {At(6):F3}, {At(5):F3}, {At(3):F3}, {At(1):F3}");

        // Smooth, not a cone: the immediate neighbour carries a large fraction of the
        // apex height (a hard constraint would leave a C0 spike instead).
        Assert.True(At(5) > 0.4 * At(6), $"Neighbour {At(5):F3} vs apex {At(6):F3}");

        // Bounded: biharmonic solutions can undershoot slightly near the rim, but never wildly.
        foreach (var v in bumped.Vertices)
            Assert.InRange(v.Position.Z, -0.2, 1.05);

        // Rim pinned bit-identically.
        foreach (var v in grid.Vertices)
        {
            if (v.IsBoundary)
                Assert.Equal(v.Position, bumped.GetPosition(v.Index));
        }
    }

    [Fact]
    public void CylinderBend_TopFollowsHandles_BottomStaysPinned()
    {
        var cylinder = MeshPrimitives.Cylinder(1, 10, 16).Triangulated();
        var deformer = new LaplacianMeshDeformer(cylinder);

        var pinnedVertices = new List<int>();
        var handleVertices = new List<int>();
        foreach (var v in cylinder.Vertices)
        {
            if (v.Position.Z < 0.5)
            {
                deformer.PinVertex(v.Index);
                pinnedVertices.Add(v.Index);
            }
            else if (v.Position.Z > 9.5)
            {
                deformer.SetHandle(v.Index, v.Position + new Vector3d(2, 0, 0), weight: 100);
                handleVertices.Add(v.Index);
            }
        }
        Assert.NotEmpty(pinnedVertices);
        Assert.NotEmpty(handleVertices);

        var bent = deformer.Solve();

        foreach (int v in pinnedVertices)
            Assert.Equal(cylinder.GetPosition(v), bent.GetPosition(v)); // bitwise

        foreach (int v in handleVertices)
        {
            double dx = bent.GetPosition(v).X - cylinder.GetPosition(v).X;
            Assert.InRange(dx, 1.8, 2.2);
        }

        // The shaft bends through intermediate displacements rather than shearing rigidly.
        foreach (var v in cylinder.Vertices)
        {
            if (v.Position.Z is > 4.5 and < 5.5)
            {
                double dx = bent.GetPosition(v.Index).X - v.Position.X;
                Assert.InRange(dx, 0.2, 1.8);
            }
        }
    }

    [Fact]
    public void WeightExtremes_InterpolateOrIgnore()
    {
        const int size = 10;
        var grid = LaplacianSmootherTests.PlaneGrid(size);
        int centre = LaplacianSmootherTests.Index(size, 5, 5);
        var target = new Vector3d(5, 5, 2.0);

        // Huge weight: the constraint is satisfied to solver precision.
        var strong = new LaplacianMeshDeformer(grid);
        strong.SetHandle(centre, target, weight: 1e6);
        double strongMiss = (strong.Solve().GetPosition(centre) - target).Length;
        Assert.True(strongMiss < 1e-6, $"w=1e6 missed by {strongMiss:E3}");

        // Tiny weight: the surface barely acknowledges the handle.
        var weak = new LaplacianMeshDeformer(grid);
        weak.SetHandle(centre, target, weight: 1e-4);
        double weakMove = weak.Solve().GetPosition(centre).Z;
        Assert.True(weakMove < 0.02, $"w=1e-4 moved the vertex {weakMove:E3}");
    }

    [Fact]
    public void NoHandles_ReturnsInputMesh()
    {
        var grid = LaplacianSmootherTests.PlaneGrid(4);
        var deformer = new LaplacianMeshDeformer(grid);
        Assert.Same(grid, deformer.Solve());
    }

    [Fact]
    public void HandleOnPinnedVertex_Throws()
    {
        var grid = LaplacianSmootherTests.PlaneGrid(4);
        var deformer = new LaplacianMeshDeformer(grid);
        deformer.SetHandle(0, new Vector3d(0, 0, 1)); // vertex 0 is a boundary corner
        Assert.Throws<InvalidOperationException>(() => deformer.Solve());
    }

    [Fact]
    public void Solve_IsDeterministic()
    {
        const int size = 8;
        var grid = LaplacianSmootherTests.PlaneGrid(size);
        HalfEdgeMesh Run()
        {
            var d = new LaplacianMeshDeformer(grid);
            d.SetHandle(LaplacianSmootherTests.Index(size, 4, 4), new Vector3d(4, 4, 1), 50);
            d.SetHandle(LaplacianSmootherTests.Index(size, 2, 6), new Vector3d(2, 6, -0.5), 20);
            return d.Solve();
        }
        var a = Run();
        var b = Run();
        foreach (var v in a.Vertices)
            Assert.Equal(v.Position, b.GetPosition(v.Index)); // bitwise
    }

    [Fact]
    public void DeformRegion_TouchesOnlyTheRegion()
    {
        const int size = 16;
        var grid = LaplacianSmootherTests.PlaneGrid(size);

        // Central disk of faces (by face centroid distance from the middle).
        var centreOfGrid = new Vector3d(size / 2.0, size / 2.0, 0);
        var faceIds = grid.Faces
            .Where(f => (f.Centroid() - centreOfGrid).Length < 4.5)
            .Select(f => f.Index);
        var region = MeshFaceSelection.FromIndices(grid, faceIds);
        Assert.True(region.Count > 20);

        int handle = LaplacianSmootherTests.Index(size, size / 2, size / 2);
        var deformed = LaplacianMeshDeformer.DeformRegion(
            grid, region, [(handle, new Vector3d(size / 2.0, size / 2.0, 1.5), 100.0)]);

        Assert.Equal(grid.VertexCount, deformed.VertexCount);
        Assert.Equal(grid.FaceCount, deformed.FaceCount);

        // Reinsertion renumbers vertices, so compare by exact position: every vertex
        // outside the region (and the pinned rim itself) must survive bit-identically.
        var deformedPositions = deformed.Vertices.Select(v => v.Position).ToHashSet();
        var regionVertices = region.ToVertices();
        int moved = 0;
        foreach (var v in grid.Vertices)
        {
            if (!regionVertices.Contains(v.Index))
                Assert.Contains(v.Position, deformedPositions); // bitwise survival
            else if (!deformedPositions.Contains(v.Position))
                moved++;
        }
        Assert.True(moved > 10, $"Only {moved} region vertices moved.");
        Assert.True(deformed.Vertices.Max(v => v.Position.Z) > 1.0);
    }

    [Fact]
    public void DeformRegion_RejectsHandleOutsideRegion()
    {
        const int size = 8;
        var grid = LaplacianSmootherTests.PlaneGrid(size);
        var region = MeshFaceSelection.FromIndices(grid, grid.Faces.Take(4).Select(f => f.Index));
        Assert.Throws<ArgumentException>(() => LaplacianMeshDeformer.DeformRegion(
            grid, region, [(LaplacianSmootherTests.Index(size, 7, 7), Vector3d.Zero, 10.0)]));
    }
}
