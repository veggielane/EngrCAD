using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class MeshPlaneCutTests
{
    [Fact]
    public void Box_CappedCut_ExactVolumeClosedEulerTwo()
    {
        var box = MeshPrimitives.Box(2, 2, 2); // z in [-1, 1]
        var result = MeshPlaneCut.Cut(box, (0, 0, 0.5), Vector3d.UnitZ);

        result.Mesh.Validate();
        Assert.True(result.Mesh.IsClosed);
        Assert.Equal(2, result.Mesh.EulerCharacteristic);
        Assert.Equal(2.0 * 2.0 * 1.5, result.Mesh.Volume(), 12); // exact 2·2·1.5 slab

        var loop = Assert.Single(result.CutLoops);
        Assert.Equal(4, loop.Count);
        foreach (var p in loop)
            Assert.Equal(0.5, p.Z, 12);
    }

    [Fact]
    public void Box_CutLoop_IsOrderedRectanglePerimeter()
    {
        var box = MeshPrimitives.Box(2, 2, 2);
        var result = MeshPlaneCut.Cut(box, (0, 0, 0.5), Vector3d.UnitZ, cap: false);

        // Consecutive loop vertices must trace the 2×2 rectangle: perimeter exactly 8.
        var loop = Assert.Single(result.CutLoops);
        double perimeter = 0;
        for (int i = 0; i < loop.Count; i++)
            perimeter += loop[i].DistanceTo(loop[(i + 1) % loop.Count]);
        Assert.Equal(8.0, perimeter, 12);
    }

    [Fact]
    public void CutLoop_WindsCounterClockwiseViewedFromNormalSide()
    {
        var box = MeshPrimitives.Box(2, 2, 2);
        var result = MeshPlaneCut.Cut(box, (0, 0, 0.5), Vector3d.UnitZ, cap: false);

        var loop = Assert.Single(result.CutLoops);
        double doubleArea = 0; // shoelace in xy; positive = CCW viewed from +Z
        for (int i = 0; i < loop.Count; i++)
        {
            var p = loop[i];
            var q = loop[(i + 1) % loop.Count];
            doubleArea += p.X * q.Y - q.X * p.Y;
        }
        Assert.Equal(2.0 * 2.0 * 2.0, doubleArea, 12); // 2 × area of the 2×2 rectangle
    }

    [Fact]
    public void Sphere_CappedCut_MatchesSphericalCapVolume()
    {
        double r = 1.0, zCut = 0.3;
        var sphere = MeshPrimitives.UvSphere(r, segments: 64, rings: 32);
        var result = MeshPlaneCut.Cut(sphere, (0, 0, zCut), Vector3d.UnitZ);

        result.Mesh.Validate();
        Assert.True(result.Mesh.IsClosed);
        Assert.Equal(2, result.Mesh.EulerCharacteristic);
        Assert.Single(result.CutLoops);

        // Kept volume = full sphere − spherical cap of height h above the plane.
        double h = r - zCut;
        double exact = 4.0 / 3.0 * Math.PI * r * r * r - Math.PI * h * h * (3 * r - h) / 3.0;
        double relativeError = Math.Abs(result.Mesh.Volume() - exact) / exact;
        // Same tessellation budget as the UvSphere volume test at this resolution (< 1%).
        Assert.True(relativeError < 0.01, $"volume off by {relativeError:P2}");
    }

    [Fact]
    public void UncappedCut_LeavesSingleBoundaryLoopOnPlane()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 32, rings: 16);
        var result = MeshPlaneCut.Cut(sphere, (0, 0, 0.25), Vector3d.UnitZ, cap: false);

        result.Mesh.Validate();
        Assert.False(result.Mesh.IsClosed);
        Assert.Single(result.Mesh.BoundaryLoops());

        // Crossing points come from the exact line-plane parameter: on-plane to 1e-12.
        var loop = Assert.Single(result.CutLoops);
        foreach (var p in loop)
            Assert.True(Math.Abs(p.Z - 0.25) < 1e-12, $"loop vertex off plane by {p.Z - 0.25:E3}");
    }

    [Fact]
    public void Cylinder_CappedCut_ExactPrismVolume()
    {
        // Exercises polygon (quad) crossing faces and a kept n-gon cap.
        int n = 16;
        double r = 1.0, zCut = 0.75;
        var cylinder = MeshPrimitives.Cylinder(r, height: 2.0, segments: n);
        var result = MeshPlaneCut.Cut(cylinder, (0, 0, zCut), Vector3d.UnitZ);

        result.Mesh.Validate();
        Assert.True(result.Mesh.IsClosed);
        Assert.Equal(2, result.Mesh.EulerCharacteristic);

        // The mesh is exactly an n-gonal prism, so the kept volume is exactly area · zCut.
        double prismArea = 0.5 * n * r * r * Math.Sin(2 * Math.PI / n);
        Assert.Equal(prismArea * zCut, result.Mesh.Volume(), 12);

        var loop = Assert.Single(result.CutLoops);
        Assert.Equal(n, loop.Count);
    }

    [Fact]
    public void CutThroughOnPlaneVertices_KeepsLowerPyramidExactly()
    {
        // Octahedron with its equator exactly in the cut plane: the equatorial vertices
        // classify as on-plane, upper faces drop whole, lower faces are kept whole.
        Vector3d[] positions = [(1, 0, 0), (0, 1, 0), (-1, 0, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];
        List<int[]> faces =
        [
            [0, 1, 4], [1, 2, 4], [2, 3, 4], [3, 0, 4], // upper
            [1, 0, 5], [2, 1, 5], [3, 2, 5], [0, 3, 5], // lower
        ];
        var octahedron = HalfEdgeMesh.Build(positions, faces);

        var result = MeshPlaneCut.Cut(octahedron, Vector3d.Zero, Vector3d.UnitZ);
        result.Mesh.Validate();
        Assert.True(result.Mesh.IsClosed);

        // Lower pyramid: square base of area 2, height 1 → volume 2/3, exactly.
        Assert.Equal(2.0 / 3.0, result.Mesh.Volume(), 12);
        var loop = Assert.Single(result.CutLoops);
        Assert.Equal(4, loop.Count);
    }

    [Fact]
    public void PlaneAboveMesh_ReturnsWholeMeshWithNoLoops()
    {
        var box = MeshPrimitives.Box(2, 2, 2);
        var result = MeshPlaneCut.Cut(box, (0, 0, 5), Vector3d.UnitZ);

        Assert.Same(box, result.Mesh); // documented: nothing removed → original instance back
        Assert.Empty(result.CutLoops);
        Assert.Equal(8.0, result.Mesh.Volume(), 12);
    }

    [Fact]
    public void PlaneTangentToTopFace_ReturnsWholeMesh()
    {
        // Top face exactly on the plane: on-plane vertices are kept, nothing lies above.
        var box = MeshPrimitives.Box(2, 2, 2);
        var result = MeshPlaneCut.Cut(box, (0, 0, 1.0), Vector3d.UnitZ);

        Assert.Same(box, result.Mesh);
        Assert.Empty(result.CutLoops);
    }

    [Fact]
    public void PlaneBelowMesh_Throws()
    {
        var box = MeshPrimitives.Box(2, 2, 2);
        Assert.Throws<InvalidOperationException>(() => MeshPlaneCut.Cut(box, (0, 0, -5), Vector3d.UnitZ));
    }

    [Fact]
    public void TwoDisjointBoxes_ProduceTwoCapsBothClosed()
    {
        var a = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        var b = MeshPrimitives.Box(new Aabb((3, 0, 0), (4, 1, 1)));
        var (pa, fa) = a.ToIndexed();
        var (pb, fb) = b.ToIndexed();
        var positions = pa.Concat(pb).ToArray();
        var faces = fa.Concat(fb.Select(f => f.Select(i => i + pa.Length).ToArray())).ToList();
        var combined = HalfEdgeMesh.Build(positions, faces);

        var result = MeshPlaneCut.Cut(combined, (0, 0, 0.5), Vector3d.UnitZ);
        result.Mesh.Validate();
        Assert.True(result.Mesh.IsClosed);
        Assert.Equal(2, result.CutLoops.Count);
        Assert.Equal(4, result.Mesh.EulerCharacteristic); // two closed genus-0 components
        Assert.Equal(2 * (1.0 * 1.0 * 0.5), result.Mesh.Volume(), 12);
    }

    [Fact]
    public void TiltedPlane_CutBoxDiagonally_VolumeMatchesHalf()
    {
        // Cut a 2×2×2 box through its center with a tilted plane: symmetry ⇒ exactly half.
        var box = MeshPrimitives.Box(2, 2, 2);
        var normal = new Vector3d(1, 1, 1); // normalized internally
        var result = MeshPlaneCut.Cut(box, Vector3d.Zero, normal);

        result.Mesh.Validate();
        Assert.True(result.Mesh.IsClosed);
        Assert.Equal(4.0, result.Mesh.Volume(), 12);
        Assert.Single(result.CutLoops);
    }

    [Fact]
    public void AnnularCutRegion_ThrowsOnCap_ReturnsBothLoopsUncapped()
    {
        // Cutting a vertical square tube horizontally leaves an annular cut region:
        // the documented NotSupportedException with cap: true, both loops with cap: false.
        var tube = SquareTube();
        Assert.Throws<NotSupportedException>(() => MeshPlaneCut.Cut(tube, Vector3d.Zero, Vector3d.UnitZ));

        var result = MeshPlaneCut.Cut(tube, Vector3d.Zero, Vector3d.UnitZ, cap: false);
        result.Mesh.Validate();
        Assert.False(result.Mesh.IsClosed);
        Assert.Equal(2, result.CutLoops.Count);
        Assert.Equal(2, result.Mesh.BoundaryLoops().Count());
        foreach (var loop in result.CutLoops)
            foreach (var p in loop)
                Assert.Equal(0.0, p.Z, 12);
    }

    [Fact]
    public void NonConvexFace_CrossingPlaneManyTimes_ClipsExactly()
    {
        // A comb-shaped prism whose top and bottom faces are single non-convex 12-gons.
        // The plane y = 2 crosses each 12-gon SIX times (through all three teeth), hitting
        // the crossings > 2 branch. A vertex-0 fan is invalid here (the polygon is not
        // star-shaped from (0,0): fan triangles bridge the gaps between teeth), so this
        // exercises the proper-triangulation path.
        var comb = CombPrism();
        var result = MeshPlaneCut.Cut(comb, (0, 2, 0), Vector3d.UnitY);

        result.Mesh.Validate();
        Assert.True(result.Mesh.IsClosed);

        // Kept region: base strip 5×1 plus three 1×1 tooth stubs, extruded height 1 → 8.
        Assert.Equal(8.0, result.Mesh.Volume(), 12);

        // One rectangular cut loop per tooth, each exactly 1 (x) × 1 (z): perimeter 4,
        // area 1. (Loops may carry extra collinear vertices where triangulation diagonals
        // of the n-gon faces cross the plane — they change neither metric.)
        Assert.Equal(3, result.CutLoops.Count);
        foreach (var loop in result.CutLoops)
        {
            double perimeter = 0, doubleArea = 0; // shoelace in the cut plane's (x, z)
            for (int i = 0; i < loop.Count; i++)
            {
                var p = loop[i];
                var q = loop[(i + 1) % loop.Count];
                perimeter += p.DistanceTo(q);
                doubleArea += p.X * q.Z - q.X * p.Z;
            }
            Assert.Equal(4.0, perimeter, 12);
            Assert.Equal(2.0, Math.Abs(doubleArea), 12); // 2 × unit-square area
            foreach (var p in loop)
                Assert.Equal(2.0, p.Y, 12);
        }
    }

    /// <summary>
    /// Prism (z ∈ [0, 1]) over a comb-shaped 12-gon in the xy-plane: a 5×1 base strip
    /// (y ∈ [0, 1]) with three 1-wide teeth rising to y = 3 at x ∈ [0,1], [2,3], [4,5].
    /// Top and bottom are single non-convex 12-gon faces; all faces wound outward.
    /// </summary>
    private static HalfEdgeMesh CombPrism()
    {
        // CCW outline viewed from +z.
        Vector2d[] outline =
        [
            (0, 0), (5, 0), (5, 3), (4, 3), (4, 1), (3, 1),
            (3, 3), (2, 3), (2, 1), (1, 1), (1, 3), (0, 3),
        ];
        int n = outline.Length;
        var positions = new Vector3d[2 * n];
        for (int i = 0; i < n; i++)
        {
            positions[i] = (outline[i].X, outline[i].Y, 0);     // bottom ring
            positions[i + n] = (outline[i].X, outline[i].Y, 1); // top ring
        }

        List<int[]> faces =
        [
            [.. Enumerable.Range(0, n).Select(i => i + n)],   // top: CCW from +z, outward +z
            [.. Enumerable.Range(0, n).Reverse()],            // bottom: reversed, outward -z
        ];
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            faces.Add([i, next, next + n, i + n]); // side wall, outward
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    /// <summary>
    /// Closed square tube: outer 4×4 square, inner 2×2 square hole, z ∈ [-1, 1];
    /// annulus caps split into four trapezoids per end. All faces wound outward.
    /// </summary>
    private static HalfEdgeMesh SquareTube()
    {
        Vector3d[] positions =
        [
            (-2, -2, -1), (2, -2, -1), (2, 2, -1), (-2, 2, -1), // 0-3  outer bottom
            (-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1), // 4-7  inner bottom
            (-2, -2, 1), (2, -2, 1), (2, 2, 1), (-2, 2, 1),     // 8-11 outer top
            (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1),     // 12-15 inner top
        ];
        List<int[]> faces =
        [
            [0, 1, 9, 8], [1, 2, 10, 9], [2, 3, 11, 10], [3, 0, 8, 11],     // outer walls
            [5, 4, 12, 13], [6, 5, 13, 14], [7, 6, 14, 15], [4, 7, 15, 12], // inner walls (face the hole)
            [8, 9, 13, 12], [9, 10, 14, 13], [10, 11, 15, 14], [11, 8, 12, 15], // top ring
            [1, 0, 4, 5], [2, 1, 5, 6], [3, 2, 6, 7], [0, 3, 7, 4],         // bottom ring
        ];
        return HalfEdgeMesh.Build(positions, faces);
    }
}
