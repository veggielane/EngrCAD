using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class MeshMeshCutTests
{
    private static HalfEdgeMesh Box(double x0, double y0, double z0, double x1, double y1, double z1) =>
        MeshPrimitives.Box(new Aabb((x0, y0, z0), (x1, y1, z1)));

    /// <summary>Position → vertex index, keyed by exact bits: the seam is supposed to weld
    /// by equality, not by tolerance, so the lookup must be exact.</summary>
    private static Dictionary<Vector3d, int> VertexIndex(HalfEdgeMesh mesh)
    {
        var map = new Dictionary<Vector3d, int>();
        foreach (var vertex in mesh.Vertices)
            map[vertex.Position] = vertex.Index;
        return map;
    }

    /// <summary>
    /// The invariant the exact boolean stands on: every intersection segment is an edge of
    /// BOTH meshes, between vertices at bit-identical coordinates.
    /// </summary>
    private static void AssertSeamIsSharedByBothMeshes(MeshImprint imprint)
    {
        var indexA = VertexIndex(imprint.MeshA);
        var indexB = VertexIndex(imprint.MeshB);
        foreach (var (start, end) in imprint.Segments)
        {
            var p = imprint.Points[start];
            var q = imprint.Points[end];
            Assert.True(indexA.TryGetValue(p, out int a0), $"{p} is not a vertex of the first mesh");
            Assert.True(indexA.TryGetValue(q, out int a1), $"{q} is not a vertex of the first mesh");
            Assert.True(indexB.TryGetValue(p, out int b0), $"{p} is not a vertex of the second mesh");
            Assert.True(indexB.TryGetValue(q, out int b1), $"{q} is not a vertex of the second mesh");
            Assert.Contains(a1, imprint.MeshA.GetVertex(a0).Neighbors().Select(n => n.Index));
            Assert.Contains(b1, imprint.MeshB.GetVertex(b0).Neighbors().Select(n => n.Index));
        }
    }

    private static void AssertUncutGeometry(HalfEdgeMesh before, HalfEdgeMesh after, double volumeTolerance = 1e-12)
    {
        after.Validate();
        Assert.True(after.IsClosed);
        Assert.Equal(before.Volume(), after.Volume(), volumeTolerance);
        Assert.Equal(before.SurfaceArea(), after.SurfaceArea(), volumeTolerance);
    }

    // ---------------------------------------------------------------- the curve itself

    [Fact]
    public void CrossingBoxes_CurveIsTheClosedHexagonAroundTheOverlap()
    {
        // A = [0,2]³, B = [1,3]³ overlap in the corner cube [1,2]³. The two surfaces meet
        // along a closed hexagon: three unit squares' worth of boundary, six unit edges.
        var a = Box(0, 0, 0, 2, 2, 2);
        var b = Box(1, 1, 1, 3, 3, 3);

        var imprint = MeshMeshCut.Imprint(a, b);

        Assert.Equal(6, imprint.Segments.Count);
        Assert.Equal(6, imprint.Points.Count);
        Assert.Equal(6.0, imprint.Length, 12);
        var loop = Assert.Single(imprint.Polylines);
        Assert.Equal(loop[0], loop[^1]);          // closed
        Assert.Equal(7, loop.Count);              // six segments, first index repeated
        AssertUncutGeometry(a, imprint.MeshA);
        AssertUncutGeometry(b, imprint.MeshB);
        AssertSeamIsSharedByBothMeshes(imprint);
    }

    [Fact]
    public void BarThroughBox_CurveIsTwoClosedRectangles()
    {
        var box = Box(-1, -1, -1, 1, 1, 1);
        var bar = Box(-0.5, -0.5, -3, 0.5, 0.5, 3);

        var imprint = MeshMeshCut.Imprint(box, bar);

        Assert.Equal(2, imprint.Polylines.Count);
        foreach (var loop in imprint.Polylines)
            Assert.Equal(loop[0], loop[^1]);
        Assert.Equal(8.0, imprint.Length, 12); // two 1×1 squares
        AssertUncutGeometry(box, imprint.MeshA);
        AssertUncutGeometry(bar, imprint.MeshB);
        AssertSeamIsSharedByBothMeshes(imprint);
    }

    [Fact]
    public void SphereThroughBoxFace_ImprintsOneLoopOnBothSides()
    {
        // The plane x = 0 cuts the sphere along a great circle, and the sphere's own
        // meridian vertices land exactly on it — the degenerate case where crossings
        // coincide with existing vertices.
        var box = Box(-2, -2, -2, 0, 2, 2);
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 16, rings: 8);

        var imprint = MeshMeshCut.Imprint(box, sphere);

        var loop = Assert.Single(imprint.Polylines);
        Assert.Equal(loop[0], loop[^1]);
        Assert.Equal(16, imprint.Segments.Count); // one segment per meridian wedge
        // The tessellated great circle is the inscribed 16-gon of the unit circle.
        Assert.Equal(16 * 2 * Math.Sin(Math.PI / 16), imprint.Length, 9);
        AssertUncutGeometry(box, imprint.MeshA);
        AssertUncutGeometry(sphere, imprint.MeshB, volumeTolerance: 1e-9);
        AssertSeamIsSharedByBothMeshes(imprint);
    }

    [Fact]
    public void CylinderThroughBox_CutsBothCapsAndKeepsVolumes()
    {
        // Offset so no vertex, edge or face of either mesh is aligned with the other.
        var box = Box(-1, -1, -1, 1, 1, 1);
        var cylinder = MeshPrimitives.Cylinder(0.4, 4, 24)
            .Transformed(Matrix4d.CreateTranslation((0.13, -0.07, -2)));

        var imprint = MeshMeshCut.Imprint(box, cylinder);

        Assert.Equal(2, imprint.Polylines.Count);
        // Two rims, each the cylinder's own 24-gon (radius is the circumradius).
        Assert.Equal(2 * 24 * 2 * 0.4 * Math.Sin(Math.PI / 24), imprint.Length, 9);
        AssertUncutGeometry(box, imprint.MeshA);
        AssertUncutGeometry(cylinder, imprint.MeshB, volumeTolerance: 1e-9);
        AssertSeamIsSharedByBothMeshes(imprint);
    }

    [Fact]
    public void DisjointMeshes_ProduceNoSeam()
    {
        var a = Box(0, 0, 0, 1, 1, 1);
        var b = Box(5, 5, 5, 6, 6, 6);

        var imprint = MeshMeshCut.Imprint(a, b);

        Assert.Empty(imprint.Segments);
        Assert.Empty(imprint.Points);
        Assert.Equal(0.0, imprint.Length);
        Assert.Equal(a.Triangulated().FaceCount, imprint.MeshA.FaceCount);
    }

    [Fact]
    public void NestedMeshes_ProduceNoSeam()
    {
        var outer = Box(-2, -2, -2, 2, 2, 2);
        var inner = MeshPrimitives.UvSphere(1.0, 16, 8);

        var imprint = MeshMeshCut.Imprint(outer, inner);

        Assert.Empty(imprint.Segments);
        Assert.Equal(inner.Triangulated().FaceCount, imprint.MeshB.FaceCount);
    }

    // ---------------------------------------------------------------- invariants

    [Fact]
    public void Imprint_IsIdempotent()
    {
        var a = Box(0, 0, 0, 2, 2, 2);
        var b = Box(1, 1, 1, 3, 3, 3);

        var once = MeshMeshCut.Imprint(a, b);
        var twice = MeshMeshCut.Imprint(once.MeshA, once.MeshB);

        // The seam is already there: re-cutting adds no vertex, edge or face.
        Assert.Equal(once.MeshA.FaceCount, twice.MeshA.FaceCount);
        Assert.Equal(once.MeshA.VertexCount, twice.MeshA.VertexCount);
        Assert.Equal(once.MeshB.FaceCount, twice.MeshB.FaceCount);
        Assert.Equal(once.MeshB.VertexCount, twice.MeshB.VertexCount);
        Assert.Equal(once.Segments.Count, twice.Segments.Count);
        Assert.Equal(once.Length, twice.Length, 12);
    }

    [Fact]
    public void Imprint_IsScaleFree()
    {
        // The BSP path drops every polygon of a 1e-5-scale model (its degeneracy test is
        // an absolute 1e-9 on a cross product, i.e. on an AREA). The exact path's guards
        // are relative, so the cut is combinatorially identical at any scale.
        const double s = 1e-5;
        var unit = MeshMeshCut.Imprint(Box(0, 0, 0, 2, 2, 2), Box(1, 1, 1, 3, 3, 3));
        var tiny = MeshMeshCut.Imprint(
            Box(0, 0, 0, 2 * s, 2 * s, 2 * s), Box(s, s, s, 3 * s, 3 * s, 3 * s));

        Assert.Equal(unit.Segments.Count, tiny.Segments.Count);
        Assert.Equal(unit.MeshA.FaceCount, tiny.MeshA.FaceCount);
        Assert.Equal(unit.Length * s, tiny.Length, 15);
        AssertSeamIsSharedByBothMeshes(tiny);
    }

    [Fact]
    public void NearTangentCylinder_CutsCleanly()
    {
        // The cylinder's flats stop one part in 1e9 short of the box's side planes — near
        // enough that the BSP path's absolute 1e-9 plane epsilon calls them coincident and
        // returns a mesh with boundary edges. The relative guard resolves them.
        var box = Box(-1, -1, -1, 1, 1, 1);
        var cylinder = MeshPrimitives.Cylinder(0.999999999, 4, 64)
            .Transformed(Matrix4d.CreateTranslation((0, 0, -2)));

        var imprint = MeshMeshCut.Imprint(box, cylinder);

        Assert.Equal(2, imprint.Polylines.Count); // the two rims where the bore meets the caps
        AssertUncutGeometry(box, imprint.MeshA);
        AssertUncutGeometry(cylinder, imprint.MeshB, volumeTolerance: 1e-9);
        AssertSeamIsSharedByBothMeshes(imprint);
    }

    // ---------------------------------------------------------------- coincident surface

    [Fact]
    public void CoplanarOverlap_IsReportedAsCoincidentSurface_NotAsCurve()
    {
        // Two boxes sharing a whole face. Two coplanar triangles meet in an AREA, so there
        // is no curve to imprint there — the shared square is reported separately instead,
        // and the only segments are the rim where the two surfaces genuinely change plane.
        var lower = Box(0, 0, 0, 1, 1, 1);
        var upper = Box(0, 0, 1, 1, 1, 2);

        var imprint = MeshMeshCut.Imprint(lower, upper);

        Assert.True(imprint.HasCoincidentSurface);
        Assert.Equal(2, imprint.CoincidentFacesA.Count);   // the lower box's top face
        Assert.Equal(2, imprint.CoincidentFacesB.Count);   // the upper box's bottom face
        Assert.All(imprint.CoincidentFacesA, OnTheSharedPlane);
        Assert.All(imprint.CoincidentFacesB, OnTheSharedPlane);
        Assert.Equal(1.0, Area(imprint.CoincidentFacesA), 12);
        AssertUncutGeometry(lower, imprint.MeshA);
        AssertUncutGeometry(upper, imprint.MeshB);
        AssertSeamIsSharedByBothMeshes(imprint);

        static void OnTheSharedPlane((Vector3d A, Vector3d B, Vector3d C) t)
        {
            Assert.Equal(1.0, t.A.Z, 15);
            Assert.Equal(1.0, t.B.Z, 15);
            Assert.Equal(1.0, t.C.Z, 15);
        }
    }

    [Fact]
    public void CoincidentRegionBoundary_IsImprintedByTheOrdinaryTransversalPath()
    {
        // This is what makes centroid classification legal downstream: the shared area's
        // RIM is a real edge of both meshes. Here the upper box covers only half the lower
        // box's top face, so the rim runs through the middle of that face — the lower box's
        // top must come back cut in two along x = 1.
        var lower = Box(0, 0, 0, 2, 2, 1);
        var upper = Box(1, 0, 1, 3, 2, 2);

        var imprint = MeshMeshCut.Imprint(lower, upper);

        Assert.True(imprint.HasCoincidentSurface);
        // The carriers are whole faces, not the clipped overlap: each operand reports the
        // 2×2 face that carries the shared [1,2]×[0,2] strip. Clipping is the classifier's
        // job (a face counts as coincident when its centroid falls inside a carrier).
        Assert.Equal(4.0, Area(imprint.CoincidentFacesA), 12);
        Assert.Equal(4.0, Area(imprint.CoincidentFacesB), 12);
        // The rim x = 1, z = 1 is imprinted over its full length into BOTH meshes, at
        // bit-identical coordinates (split at whatever vertices the triangulations impose).
        AssertSeamIsSharedByBothMeshes(imprint);
        double rim = imprint.Segments
            .Select(s => (P: imprint.Points[s.Start], Q: imprint.Points[s.End]))
            .Where(s => s.P.X == 1 && s.P.Z == 1 && s.Q.X == 1 && s.Q.Z == 1)
            .Sum(s => s.P.DistanceTo(s.Q));
        Assert.Equal(2.0, rim, 12);
        AssertUncutGeometry(lower, imprint.MeshA);
        AssertUncutGeometry(upper, imprint.MeshB);
    }

    [Fact]
    public void CoplanarFacesThatOnlyTouch_AreNotCoincident()
    {
        // Sharing a plane is not sharing surface: this bar's side is flush with the box's
        // side, and the two only meet along a line — zero area, nothing to classify.
        var box = Box(0, 0, 0, 2, 2, 2);
        var bar = Box(2 - 1e-12, 0.5, 0.5, 4, 1.5, 1.5);

        var imprint = MeshMeshCut.Imprint(box, bar);

        Assert.NotEmpty(imprint.Segments);
        Assert.False(imprint.HasCoincidentSurface);
        AssertSeamIsSharedByBothMeshes(imprint);
    }

    private static double Area(IReadOnlyList<(Vector3d A, Vector3d B, Vector3d C)> triangles) =>
        triangles.Sum(t => 0.5 * (t.B - t.A).Cross(t.C - t.A).Length);

    // ---------------------------------------------------------------- transactionality

    [Fact]
    public void FailedImprint_RollsBackTheMeshBitIdentically()
    {
        // A plan that cannot be realized: a segment between two seam points that were never
        // placed. The journal must undo the splits already performed and leave the storage
        // bit-for-bit as it was.
        var mesh = EditableMesh.FromMesh(MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1))).Triangulated());
        var before = mesh.CaptureState();

        var points = new List<Vector3d> { (0.5, 0, 0.25), (0.5, 0, 0.75), (17, 17, 17) };
        var plan = new ImprintPlan();
        // Two real insertions on the first face's edges, then an impossible segment.
        int face = 0;
        int he = mesh.FaceHalfEdge(face);
        plan.AddEdgePoint(he, 0.5, 0);
        plan.AddFacePoint(face, 0);
        plan.AddFacePoint(face, 1);
        plan.AddSegment(face, 0, 2);

        bool ok = MeshImprinter.TryImprint(mesh, plan, points, 1e-13, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        MeshStateAssert.Equal(before, mesh.CaptureState());
        Assert.Null(mesh.ActiveChangeSet);
        mesh.Validate();
    }
}
