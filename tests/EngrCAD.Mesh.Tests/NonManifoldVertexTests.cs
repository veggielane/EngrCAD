using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// The <b>pinch</b> vertex — a vertex whose link is two or more fans rather than one — and
/// the boundary this project draws around it. <see cref="HalfEdgeMesh.Build"/> accepts one
/// deliberately (a pinch is sometimes the correct answer; see its remarks), while
/// <see cref="HalfEdgeMesh.Validate"/> and <see cref="HalfEdgeMesh.NonManifoldVertices"/>
/// report it. Both halves are pinned here so the documented edge cannot rot in either
/// direction.
/// </summary>
public class NonManifoldVertexTests
{
    /// <summary>
    /// Two tetrahedra meeting at one shared apex: closed, outward-wound, every directed edge
    /// distinct, every edge used by exactly two faces, and no boundary at all — so nothing
    /// <c>Build</c> tests can see it. The apex's link is two separate triangles.
    /// </summary>
    private static (List<Vector3d> Positions, List<int[]> Faces) TwoTetsSharingAnApex()
    {
        var positions = new List<Vector3d>
        {
            (0, 0, 0),                                              // 0: the shared apex
            (1, 0, -1), (-0.5, 0.866, -1), (-0.5, -0.866, -1),      // 1-3: lower base
            (1, 0, 1), (-0.5, 0.866, 1), (-0.5, -0.866, 1),         // 4-6: upper base (mirrored)
        };
        var faces = new List<int[]>
        {
            new[] { 1, 3, 2 }, new[] { 0, 1, 2 }, new[] { 0, 2, 3 }, new[] { 0, 3, 1 },
            new[] { 4, 5, 6 }, new[] { 0, 5, 4 }, new[] { 0, 6, 5 }, new[] { 0, 4, 6 },
        };
        return (positions, faces);
    }

    [Fact]
    public void Build_AcceptsAPinchVertex_AndValidateReportsIt()
    {
        var (positions, faces) = TwoTetsSharingAnApex();

        // Deliberate, and stated in Build's remarks: the structure stores and traverses
        // perfectly, so refusing it would make a correct boolean answer unrepresentable.
        var mesh = HalfEdgeMesh.Build(positions, faces);
        Assert.True(mesh.IsClosed);
        Assert.Equal(7, mesh.VertexCount);
        Assert.Equal(12, mesh.EdgeCount);
        Assert.Equal(8, mesh.FaceCount);
        // chi = 3, not the 4 two disjoint tetrahedra would give: the shared apex is the whole
        // difference. That is NOT a usable test on its own, which is why the fan walk exists —
        // it needs the component count to interpret, and one component of chi 3 is impossible
        // for a closed surface while nothing local says so.
        Assert.Equal(3, mesh.EulerCharacteristic);

        Assert.Equal([0], mesh.NonManifoldVertices());
        var ex = Assert.Throws<InvalidOperationException>(mesh.Validate);
        Assert.Contains("Vertex 0", ex.Message);
        Assert.Contains("disconnected fan", ex.Message);
    }

    /// <summary>
    /// The half the fan walk exists to catch: every per-half-edge and per-face query is
    /// correct on a pinched mesh, and only a per-VERTEX fan walk under-reports. Asserting
    /// exactly that is what makes <c>Build</c>'s documented consequence a measurement rather
    /// than a claim.
    /// </summary>
    [Fact]
    public void APinchVertexUnderReportsOnlyItsOwnFanWalk()
    {
        var (positions, faces) = TwoTetsSharingAnApex();
        var mesh = HalfEdgeMesh.Build(positions, faces);

        int byHalfEdge = mesh.HalfEdges.Count(h => h.Origin.Index == 0);
        Assert.Equal(6, byHalfEdge);                        // six edges genuinely meet there
        Assert.Equal(3, mesh.GetVertex(0).Valence);         // the fan walk sees one tet only
        Assert.Equal(3, mesh.GetVertex(0).OutgoingHalfEdges().Count());
        Assert.False(mesh.GetVertex(0).IsBoundary);

        // Everything not routed through a fan is unaffected: the volume is both tets' (each
        // is an equilateral base of area 1.299 at height 1, so 0.433 apiece).
        Assert.Equal(0.866, mesh.Volume(), 9);
        Assert.Equal(8, mesh.Faces.Count());

        // ComputeVertexNormals walks half-edges by INDEX, so it sees both fans — and this
        // fixture is the mirror-symmetric one, so what it sees cancels exactly. Zero is the
        // interface's own spelling for "no orientation available", and it is the honest
        // answer: a pinch point genuinely has no single surface normal.
        var normals = mesh.ComputeVertexNormals();
        Assert.Equal(Vector3d.Zero, normals[0]);
        for (int v = 1; v < mesh.VertexCount; v++)
            Assert.NotEqual(Vector3d.Zero, normals[v]);
    }

    /// <summary>
    /// The same two tetrahedra pulled apart still build and validate, so the refusal above is
    /// about the shared apex and nothing else about the fixture.
    /// </summary>
    [Fact]
    public void TheSameTetrahedraApartAreClean()
    {
        var positions = new List<Vector3d>
        {
            (0, 0, 0), (1, 0, -1), (-0.5, 0.866, -1), (-0.5, -0.866, -1),
            (0, 0, 1), (1, 0, 2), (-0.5, 0.866, 2), (-0.5, -0.866, 2),
        };
        var faces = new List<int[]>
        {
            new[] { 1, 3, 2 }, new[] { 0, 1, 2 }, new[] { 0, 2, 3 }, new[] { 0, 3, 1 },
            new[] { 5, 6, 7 }, new[] { 4, 6, 5 }, new[] { 4, 7, 6 }, new[] { 4, 5, 7 },
        };

        var mesh = HalfEdgeMesh.Build(positions, faces);
        mesh.Validate();
        Assert.Empty(mesh.NonManifoldVertices());
        Assert.Equal(4, mesh.EulerCharacteristic);
    }

    /// <summary>
    /// A BOW-TIE boundary vertex (two OPEN fans) is a pinch too, so the general fan test
    /// subsumes the specific one — but <c>Build</c> must keep refusing it separately and with
    /// its own message, because that check guards the boundary-loop chaining that runs
    /// immediately after it. This pins which message a caller gets.
    /// </summary>
    [Fact]
    public void ABowTieBoundaryVertexIsStillRefusedByName()
    {
        // Two triangles meeting at one corner, both open.
        var ex = Assert.Throws<ArgumentException>(() => HalfEdgeMesh.Build(
            [(0, 0, 0), (1, 0, 0), (1, 1, 0), (-1, 0, 0), (-1, -1, 0)],
            [new[] { 0, 1, 2 }, new[] { 0, 3, 4 }]));
        Assert.Contains("bow-tie", ex.Message);
    }

    /// <summary>
    /// A clean mesh answers with no allocation past the early-out, and the answer agrees with
    /// the whole-mesh scalar test <c>Build</c> would use if it ever ran one.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CleanMeshesReportNothing(int fixtureId)
    {
        var mesh = fixtureId switch
        {
            0 => MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 2, 3))),
            1 => MeshPrimitives.UvSphere(1.0, 24, 16),
            2 => MeshPrimitives.Cylinder(1.0, 3.0, 20),
            _ => MeshPrimitives.UvSphere(1.0, 12, 8).Triangulated(),
        };

        Assert.Empty(mesh.NonManifoldVertices());
        Assert.Equal(mesh.HalfEdgeCount, mesh.VertexFanTotal());
        mesh.Validate();
    }

    /// <summary>
    /// An OPEN mesh's fans include their boundary half-edge, so the fan total still covers
    /// everything — the test must not read a boundary as a pinch.
    /// </summary>
    [Fact]
    public void OpenMeshesAreNotMistakenForPinches()
    {
        var strip = HalfEdgeMesh.Build(
            [(0, 0, 0), (1, 0, 0), (2, 0, 0), (0, 1, 0), (1, 1, 0), (2, 1, 0)],
            [new[] { 0, 1, 4, 3 }, new[] { 1, 2, 5, 4 }]);

        Assert.Equal(strip.HalfEdgeCount, strip.VertexFanTotal());
        Assert.Empty(strip.NonManifoldVertices());
        strip.Validate();
    }
}
