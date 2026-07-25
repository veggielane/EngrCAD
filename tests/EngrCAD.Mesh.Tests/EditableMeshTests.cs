using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class EditableMeshTests
{
    private static HalfEdgeMesh TriangulatedBox() =>
        MeshPrimitives.Box(new Aabb((0, 0, 0), (2, 3, 4))).Triangulated();

    /// <summary>Open 3×3-vertex grid of 8 triangles in the XY plane (a disk, χ = 1).</summary>
    private static HalfEdgeMesh TriangleGrid()
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
                faces.Add([i, i + 1, i + 4]);
                faces.Add([i, i + 4, i + 3]);
            }
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    private static HalfEdgeMesh Tetrahedron() => HalfEdgeMesh.Build(
        [(0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1)],
        [new[] { 0, 2, 1 }, new[] { 0, 1, 3 }, new[] { 1, 2, 3 }, new[] { 2, 0, 3 }]);

    private static int InteriorHalfEdge(EditableMesh mesh) =>
        mesh.HalfEdgeIndices().First(he => !mesh.IsBoundaryEdge(he));

    private static int BoundaryEdgeHalfEdge(EditableMesh mesh) =>
        mesh.HalfEdgeIndices().First(he => mesh.FaceOf(he) >= 0 && mesh.IsBoundaryEdge(he));

    private static int FindBoundaryHalfEdge(EditableMesh mesh, int from, int to)
    {
        int he = mesh.FindHalfEdge(from, to);
        Assert.True(he >= 0, $"no half-edge {from}->{to}");
        Assert.True(mesh.IsBoundaryHalfEdge(he), $"half-edge {from}->{to} is not a boundary half-edge");
        return he;
    }

    // ---------------------------------------------------------------- round trip

    [Fact]
    public void FromMesh_ToMesh_RoundTripsExactly()
    {
        var source = MeshPrimitives.UvSphere(1.5, 10, 6);
        var editable = EditableMesh.FromMesh(source);
        editable.Validate();
        Assert.Equal(source.VertexCount, editable.VertexCount);
        Assert.Equal(source.EdgeCount, editable.EdgeCount);
        Assert.Equal(source.FaceCount, editable.FaceCount);
        Assert.Equal(source.EulerCharacteristic, editable.EulerCharacteristic);

        var round = editable.ToMesh();
        var (expectedPositions, expectedFaces) = source.ToIndexed();
        var (actualPositions, actualFaces) = round.ToIndexed();
        Assert.Equal(expectedPositions.Length, actualPositions.Length);
        for (int i = 0; i < expectedPositions.Length; i++)
            Assert.Equal(expectedPositions[i], actualPositions[i]);
        Assert.Equal(expectedFaces.Count, actualFaces.Count);
        for (int i = 0; i < expectedFaces.Count; i++)
            Assert.Equal(expectedFaces[i], actualFaces[i]);
    }

    // ---------------------------------------------------------------- split

    [Fact]
    public void SplitEdge_Interior_CountsAndGeometry()
    {
        var editable = EditableMesh.FromMesh(TriangulatedBox());
        double volume = editable.ToMesh().Volume();
        int v = editable.VertexCount, e = editable.EdgeCount, f = editable.FaceCount;

        int he = InteriorHalfEdge(editable);
        var p0 = editable.GetPosition(editable.Origin(he));
        var p1 = editable.GetPosition(editable.Destination(he));

        Assert.Equal(MeshOperationResult.Ok, editable.SplitEdge(he, out var info, 0.25));
        editable.Validate();
        Assert.Equal(v + 1, editable.VertexCount);
        Assert.Equal(e + 3, editable.EdgeCount);
        Assert.Equal(f + 2, editable.FaceCount);
        Assert.Equal(2, editable.EulerCharacteristic);
        Assert.False(info.IsBoundary);
        Assert.True(info.NewFaceRight >= 0);

        // Position is the exact lerp at t measured from the passed half-edge's origin.
        var expected = p0 + (p1 - p0) * 0.25;
        Assert.Equal(expected.X, editable.GetPosition(info.NewVertex).X, 15);
        Assert.Equal(expected.Y, editable.GetPosition(info.NewVertex).Y, 15);
        Assert.Equal(expected.Z, editable.GetPosition(info.NewVertex).Z, 15);

        var result = editable.ToMesh();
        Assert.True(result.IsClosed);
        Assert.Equal(volume, result.Volume(), 12);
    }

    [Fact]
    public void SplitEdge_Boundary_CountsAndBoundaryStatus()
    {
        var editable = EditableMesh.FromMesh(TriangleGrid());
        int v = editable.VertexCount, e = editable.EdgeCount, f = editable.FaceCount;

        int he = BoundaryEdgeHalfEdge(editable);
        Assert.Equal(MeshOperationResult.Ok, editable.SplitEdge(he, out var info));
        editable.Validate();
        Assert.Equal(v + 1, editable.VertexCount);
        Assert.Equal(e + 2, editable.EdgeCount);
        Assert.Equal(f + 1, editable.FaceCount);
        Assert.Equal(1, editable.EulerCharacteristic);
        Assert.True(info.IsBoundary);
        Assert.Equal(-1, info.NewFaceRight);
        Assert.True(editable.IsBoundaryVertex(info.NewVertex));
        editable.ToMesh().Validate();
    }

    [Fact]
    public void SplitEdge_OnBoundaryHalfEdgeItself_MeasuresParameterFromItsOrigin()
    {
        var editable = EditableMesh.FromMesh(TriangleGrid());
        int interior = BoundaryEdgeHalfEdge(editable);
        int boundaryHe = editable.Twin(interior);
        var p0 = editable.GetPosition(editable.Origin(boundaryHe));
        var p1 = editable.GetPosition(editable.Destination(boundaryHe));

        Assert.Equal(MeshOperationResult.Ok, editable.SplitEdge(boundaryHe, out var info, 0.25));
        var expected = p0 + (p1 - p0) * 0.25;
        Assert.Equal(expected.X, editable.GetPosition(info.NewVertex).X, 15);
        Assert.Equal(expected.Y, editable.GetPosition(info.NewVertex).Y, 15);
    }

    [Fact]
    public void SplitEdge_Guards()
    {
        var editable = EditableMesh.FromMesh(TriangulatedBox());
        int he = InteriorHalfEdge(editable);
        Assert.Equal(MeshOperationResult.InvalidParameter, editable.SplitEdge(he, out _, 0));
        Assert.Equal(MeshOperationResult.InvalidParameter, editable.SplitEdge(he, out _, 1));
        Assert.Equal(MeshOperationResult.NotAHalfEdge, editable.SplitEdge(-1, out _));
        Assert.Equal(MeshOperationResult.NotAHalfEdge, editable.SplitEdge(999_999, out _));

        // Polygon faces refuse (split is a triangle operator).
        var quads = EditableMesh.FromMesh(MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1))));
        Assert.Equal(MeshOperationResult.NotATriangle, quads.SplitEdge(0, out _));
    }

    // ---------------------------------------------------------------- flip

    [Fact]
    public void FlipEdge_QuadDiagonal_FlipsAndRestores()
    {
        // Two triangles sharing the diagonal 0–2 of a unit quad.
        var mesh = HalfEdgeMesh.Build(
            [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)],
            [new[] { 0, 1, 2 }, new[] { 0, 2, 3 }]);
        var editable = EditableMesh.FromMesh(mesh);
        int diagonal = editable.FindHalfEdge(0, 2);
        Assert.True(diagonal >= 0);

        Assert.Equal(MeshOperationResult.Ok, editable.FlipEdge(diagonal, out var info));
        editable.Validate();
        Assert.Equal(4, editable.VertexCount);
        Assert.Equal(5, editable.EdgeCount);
        Assert.Equal(2, editable.FaceCount);
        // The diagonal now connects the former apexes 1 and 3.
        Assert.True(editable.FindHalfEdge(1, 3) >= 0 || editable.FindHalfEdge(3, 1) >= 0);
        Assert.Equal(-1, editable.FindHalfEdge(0, 2));
        Assert.Equal(-1, editable.FindHalfEdge(2, 0));

        // Flipping the new diagonal restores the original connectivity.
        Assert.Equal(MeshOperationResult.Ok, editable.FlipEdge(info.HalfEdge, out _));
        editable.Validate();
        Assert.True(editable.FindHalfEdge(0, 2) >= 0);
        Assert.Equal(-1, editable.FindHalfEdge(1, 3));
        editable.ToMesh().Validate();
    }

    [Fact]
    public void FlipEdge_Guards()
    {
        var grid = EditableMesh.FromMesh(TriangleGrid());
        int boundary = BoundaryEdgeHalfEdge(grid);
        Assert.Equal(MeshOperationResult.BoundaryEdge, grid.FlipEdge(boundary, out _));

        // Every flip on a tetrahedron would duplicate the opposite edge.
        var tet = EditableMesh.FromMesh(Tetrahedron());
        foreach (int he in tet.HalfEdgeIndices().ToList())
            Assert.Equal(MeshOperationResult.EdgeAlreadyExists, tet.FlipEdge(he, out _));
        tet.Validate();

        var quads = EditableMesh.FromMesh(MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1))));
        Assert.Equal(MeshOperationResult.NotATriangle, quads.FlipEdge(0, out _));
    }

    [Fact]
    public void FlipEdge_OnSphere_PreservesCountsAndManifoldness()
    {
        var editable = EditableMesh.FromMesh(MeshPrimitives.UvSphere(1, 8, 5).Triangulated());
        int v = editable.VertexCount, e = editable.EdgeCount, f = editable.FaceCount;
        int flipped = 0;
        foreach (int he in editable.HalfEdgeIndices().Take(40).ToList())
        {
            if (editable.IsHalfEdge(he) && editable.FlipEdge(he, out _) == MeshOperationResult.Ok)
                flipped++;
        }
        Assert.True(flipped > 0);
        editable.Validate();
        Assert.Equal(v, editable.VertexCount);
        Assert.Equal(e, editable.EdgeCount);
        Assert.Equal(f, editable.FaceCount);
        Assert.True(editable.ToMesh().IsClosed);
    }

    // ---------------------------------------------------------------- collapse

    [Fact]
    public void CollapseEdge_Interior_CountsAndKeptVertex()
    {
        var editable = EditableMesh.FromMesh(MeshPrimitives.UvSphere(1, 10, 6).Triangulated());
        int v = editable.VertexCount, e = editable.EdgeCount, f = editable.FaceCount;

        int he = InteriorHalfEdge(editable);
        int kept = editable.Origin(he);
        var keptPosition = editable.GetPosition(kept);

        Assert.Equal(MeshOperationResult.Ok, editable.CollapseEdge(he, out var info));
        editable.Validate();
        Assert.Equal(kept, info.KeptVertex);
        Assert.Equal(v - 1, editable.VertexCount);
        Assert.Equal(e - 3, editable.EdgeCount);
        Assert.Equal(f - 2, editable.FaceCount);
        Assert.Equal(2, editable.EulerCharacteristic);
        Assert.False(editable.IsVertex(info.RemovedVertex));
        Assert.Equal(keptPosition, editable.GetPosition(kept));
        Assert.True(editable.ToMesh().IsClosed);
    }

    [Fact]
    public void CollapseEdge_Boundary_Counts()
    {
        var editable = EditableMesh.FromMesh(TriangleGrid());
        int v = editable.VertexCount, e = editable.EdgeCount, f = editable.FaceCount;

        // Collapse a boundary edge whose triangle is well attached: 0–1 on the bottom row.
        int he = editable.FindHalfEdge(0, 1);
        Assert.True(he >= 0);
        Assert.Equal(MeshOperationResult.Ok, editable.CollapseEdge(he, out var info));
        editable.Validate();
        Assert.True(info.IsBoundary);
        Assert.Equal(v - 1, editable.VertexCount);
        Assert.Equal(e - 2, editable.EdgeCount);
        Assert.Equal(f - 1, editable.FaceCount);
        Assert.Equal(1, editable.EulerCharacteristic);
        editable.ToMesh().Validate();
    }

    [Fact]
    public void CollapseEdge_Guards()
    {
        // Tetrahedron: every collapse would flatten it.
        var tet = EditableMesh.FromMesh(Tetrahedron());
        foreach (int he in tet.HalfEdgeIndices().ToList())
            Assert.Equal(MeshOperationResult.WouldCollapseTetrahedron, tet.CollapseEdge(he, out _));
        tet.Validate();

        // Single triangle: collapsing any edge leaves an isolated edge.
        var tri = EditableMesh.FromIndexed(
            [(0, 0, 0), (1, 0, 0), (0, 1, 0)], [new[] { 0, 1, 2 }]);
        foreach (int he in tri.HalfEdgeIndices().ToList())
            Assert.Equal(MeshOperationResult.WouldCollapseLastTriangle, tri.CollapseEdge(he, out _));

        // Interior edge whose endpoints are both boundary vertices (quad diagonal):
        // collapsing pinches the two boundary fans into a bow-tie.
        var quad = EditableMesh.FromIndexed(
            [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)],
            [new[] { 0, 1, 2 }, new[] { 0, 2, 3 }]);
        int diagonal = quad.FindHalfEdge(0, 2);
        Assert.Equal(MeshOperationResult.WouldCreateNonManifold, quad.CollapseEdge(diagonal, out _));

        // Double pyramid (two tetrahedra glued on a triangle): equator vertices share
        // three neighbors — the link condition refuses.
        var bipyramid = EditableMesh.FromIndexed(
            [(0, 0, 0), (1, 0, 0), (0.5, 1, 0), (0.5, 0.3, 1), (0.5, 0.3, -1)],
            [
                new[] { 0, 1, 3 }, new[] { 1, 2, 3 }, new[] { 2, 0, 3 },
                new[] { 1, 0, 4 }, new[] { 2, 1, 4 }, new[] { 0, 2, 4 },
            ]);
        int equator = bipyramid.FindHalfEdge(0, 1);
        Assert.Equal(MeshOperationResult.WouldCreateNonManifold, bipyramid.CollapseEdge(equator, out _));

        // Pillow (two triangles sharing all edges): opposite apexes coincide.
        var pillow = EditableMesh.FromIndexed(
            [(0, 0, 0), (1, 0, 0), (0, 1, 0)],
            [new[] { 0, 1, 2 }, new[] { 0, 2, 1 }]);
        foreach (int he in pillow.HalfEdgeIndices().ToList())
            Assert.Equal(MeshOperationResult.WouldCreateNonManifold, pillow.CollapseEdge(he, out _));
    }

    // ---------------------------------------------------------------- poke

    [Fact]
    public void PokeFace_Triangle_CountsCentroidAndVolume()
    {
        var editable = EditableMesh.FromMesh(TriangulatedBox());
        double volume = editable.ToMesh().Volume();
        int v = editable.VertexCount, e = editable.EdgeCount, f = editable.FaceCount;

        int face = editable.FaceIndices().First();
        var centroid = Vector3d.Zero;
        int n = 0;
        foreach (int vi in editable.FaceVertices(face))
        {
            centroid += editable.GetPosition(vi);
            n++;
        }
        centroid /= n;

        Assert.Equal(MeshOperationResult.Ok, editable.PokeFace(face, out var info));
        editable.Validate();
        Assert.Equal(v + 1, editable.VertexCount);
        Assert.Equal(e + 3, editable.EdgeCount);
        Assert.Equal(f + 2, editable.FaceCount);
        Assert.Equal(2, editable.EulerCharacteristic);
        Assert.Equal(3, info.Faces.Length);
        Assert.Equal(face, info.Faces[0]);
        Assert.Equal(centroid.X, editable.GetPosition(info.NewVertex).X, 14);
        Assert.Equal(centroid.Y, editable.GetPosition(info.NewVertex).Y, 14);
        Assert.Equal(centroid.Z, editable.GetPosition(info.NewVertex).Z, 14);

        var result = editable.ToMesh();
        Assert.True(result.IsClosed);
        Assert.Equal(volume, result.Volume(), 12);
    }

    [Fact]
    public void PokeFace_Quad_FansIntoFourTriangles()
    {
        var editable = EditableMesh.FromMesh(MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1))));
        int v = editable.VertexCount, e = editable.EdgeCount, f = editable.FaceCount;
        Assert.Equal(MeshOperationResult.Ok, editable.PokeFace(0, out var info));
        editable.Validate();
        Assert.Equal(v + 1, editable.VertexCount);
        Assert.Equal(e + 4, editable.EdgeCount);
        Assert.Equal(f + 3, editable.FaceCount);
        Assert.Equal(2, editable.EulerCharacteristic);
        Assert.Equal(4, info.Faces.Length);
        Assert.True(editable.ToMesh().IsClosed);
        Assert.Equal(MeshOperationResult.NotAFace, editable.PokeFace(-1, out _));
    }

    // ---------------------------------------------------------------- merge

    [Fact]
    public void MergeEdges_TwoSeparateTriangles_WeldsIntoStrip()
    {
        // Two triangles with a coincident but unshared edge (a crack).
        var editable = EditableMesh.FromIndexed(
            [(0, 0, 0), (1, 0, 0), (0, 1, 0), (1, 0, 0), (0, 1, 0), (1, 1, 0)],
            [new[] { 0, 1, 2 }, new[] { 4, 3, 5 }]);
        Assert.Equal(6, editable.VertexCount);
        Assert.Equal(2, editable.EulerCharacteristic); // two disks

        // Crack sides: edge 1–2 of the first triangle (boundary half-edge 2→1) and the
        // coincident edge 3–4 of the second (boundary half-edge 3→4).
        int keep = FindBoundaryHalfEdge(editable, 2, 1);
        int discard = FindBoundaryHalfEdge(editable, 3, 4);
        Assert.Equal(MeshOperationResult.Ok, editable.MergeEdges(keep, discard, out var info));
        editable.Validate();
        Assert.Equal(4, editable.VertexCount);
        Assert.Equal(5, editable.EdgeCount);
        Assert.Equal(2, editable.FaceCount);
        Assert.Equal(1, editable.EulerCharacteristic); // one disk
        Assert.Equal(0, info.ExtraWeldedEdges);
        Assert.False(editable.IsBoundaryEdge(info.KeptHalfEdge));

        var mesh = editable.ToMesh();
        mesh.Validate();
        Assert.Single(mesh.BoundaryLoops());
    }

    [Fact]
    public void MergeEdges_AdjacentSeam_ClosesFanIntoCone()
    {
        // Open fan around a center with a duplicated seam rim vertex (v4 coincides
        // with v0): welding the two seam spokes closes the cone.
        var editable = EditableMesh.FromIndexed(
            [
                (0, 0, 1),                                        // 0: apex
                (1, 0, 0), (0, 1, 0), (-1, 0, 0), (0, -1, 0),     // 1..4: rim
                (1, 0, 0),                                        // 5: duplicate of 1
            ],
            [new[] { 0, 1, 2 }, new[] { 0, 2, 3 }, new[] { 0, 3, 4 }, new[] { 0, 4, 5 }]);
        Assert.Equal(1, editable.EulerCharacteristic);

        int keep = FindBoundaryHalfEdge(editable, 1, 0);   // seam edge 0–1
        int discard = FindBoundaryHalfEdge(editable, 0, 5); // seam edge 5–0
        Assert.Equal(MeshOperationResult.Ok, editable.MergeEdges(keep, discard, out var info));
        editable.Validate();
        Assert.Equal(5, editable.VertexCount);
        Assert.Equal(8, editable.EdgeCount);
        Assert.Equal(4, editable.FaceCount);
        Assert.Equal(1, editable.EulerCharacteristic); // still a disk (open cone)
        Assert.Equal(-1, info.RemovedVertex0);          // apex end was already shared
        Assert.Equal(5, info.RemovedVertex1);

        var mesh = editable.ToMesh();
        Assert.Single(mesh.BoundaryLoops());
        Assert.Equal(4, mesh.BoundaryLoops()[0].Count);
    }

    [Fact]
    public void MergeEdges_FoldedStrip_AutoWeldsDoubledEdgeIntoPillow()
    {
        // Strip of two triangles sharing edge x–p; folding it shut welds p–a onto d–p
        // (d coincides with a), which doubles the boundary edges to x — the automatic
        // post-weld seals them, producing a closed two-triangle pillow.
        var editable = EditableMesh.FromIndexed(
            [
                (0, 0, 0),   // 0: p
                (0, 1, 0),   // 1: a
                (1, 0, 0),   // 2: x
                (0, 1, 0),   // 3: d (coincident with a)
            ],
            [new[] { 1, 2, 0 }, new[] { 2, 3, 0 }]); // T1 (a,x,p), T2 (x,d,p)

        int keep = FindBoundaryHalfEdge(editable, 1, 0);    // boundary side of edge p–a
        int discard = FindBoundaryHalfEdge(editable, 0, 3); // boundary side of edge d–p
        Assert.Equal(MeshOperationResult.Ok, editable.MergeEdges(keep, discard, out var info));
        editable.Validate();
        Assert.Equal(1, info.ExtraWeldedEdges);
        Assert.Equal(3, editable.VertexCount);
        Assert.Equal(3, editable.EdgeCount);
        Assert.Equal(2, editable.FaceCount);
        Assert.Equal(2, editable.EulerCharacteristic);
        Assert.True(editable.IsClosed);
        Assert.True(editable.ToMesh().IsClosed);
    }

    [Fact]
    public void MergeEdges_Guards()
    {
        var tri = EditableMesh.FromIndexed(
            [(0, 0, 0), (1, 0, 0), (0, 1, 0)], [new[] { 0, 1, 2 }]);
        int b10 = FindBoundaryHalfEdge(tri, 1, 0);
        int b21 = FindBoundaryHalfEdge(tri, 2, 1);
        int interior = tri.FindHalfEdge(0, 1);

        Assert.Equal(MeshOperationResult.SameEdge, tri.MergeEdges(b10, b10, out _));
        Assert.Equal(MeshOperationResult.NotABoundaryEdge, tri.MergeEdges(interior, b21, out _));
        Assert.Equal(MeshOperationResult.NotAHalfEdge, tri.MergeEdges(-1, b21, out _));

        // Folding a triangle onto itself: the identified vertices are already joined
        // by the third edge, which would become a self-loop.
        Assert.Equal(MeshOperationResult.WouldCreateNonManifold, tri.MergeEdges(b10, b21, out _));
        tri.Validate();
    }

    // ---------------------------------------------------------------- misc

    [Fact]
    public void SetPosition_MovesVertexAndBumpsTimestamps()
    {
        var editable = EditableMesh.FromMesh(TriangulatedBox());
        int ts = editable.ShapeTimestamp;
        editable.SetPosition(0, (9, 9, 9));
        Assert.Equal(new Vector3d(9, 9, 9), editable.GetPosition(0));
        Assert.True(editable.ShapeTimestamp > ts);
    }

    [Fact]
    public void RefusedOperation_LeavesStateAndTimestampsUntouched()
    {
        var editable = EditableMesh.FromMesh(Tetrahedron());
        var before = editable.CaptureState();
        int ts = editable.Timestamp, sts = editable.ShapeTimestamp;
        Assert.NotEqual(MeshOperationResult.Ok, editable.CollapseEdge(0, out _));
        Assert.NotEqual(MeshOperationResult.Ok, editable.FlipEdge(0, out _));
        Assert.Equal(ts, editable.Timestamp);
        Assert.Equal(sts, editable.ShapeTimestamp);
        MeshStateAssert.Equal(before, editable.CaptureState());
    }

    [Fact]
    public void FreedIndices_AreRecycledByLaterAllocations()
    {
        var editable = EditableMesh.FromMesh(MeshPrimitives.UvSphere(1, 10, 6).Triangulated());
        int capacityV = editable.VertexCapacity;
        int capacityHe = editable.HalfEdgeCapacity;
        int capacityF = editable.FaceCapacity;

        // Collapse frees one vertex, six half-edges, two faces...
        int he = InteriorHalfEdge(editable);
        Assert.Equal(MeshOperationResult.Ok, editable.CollapseEdge(he, out _));
        // ...and a split needs one vertex, six half-edges, two faces: all recycled.
        Assert.Equal(MeshOperationResult.Ok, editable.SplitEdge(InteriorHalfEdge(editable), out _));
        Assert.Equal(capacityV, editable.VertexCapacity);
        Assert.Equal(capacityHe, editable.HalfEdgeCapacity);
        Assert.Equal(capacityF, editable.FaceCapacity);
        editable.Validate();
    }
}
