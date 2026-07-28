using EngrCAD.Core;
using EngrCAD.Fea;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Fea.Tests;

public class QuadraticTetMeshTests
{
    [Fact]
    public void SingleTetrahedron_GetsExactlyTenNodes()
    {
        var linear = OneTet();
        var quadratic = QuadraticTetMesh.From(linear);

        Assert.Equal(1, quadratic.TetCount);
        Assert.Equal(10, quadratic.NodeCount);
        Assert.Equal(4, quadratic.CornerNodeCount);

        var e = quadratic.Tets[0];
        var distinct = Enumerable.Range(0, 10).Select(i => e[i]).Distinct().ToArray();
        Assert.Equal(10, distinct.Length);
    }

    [Fact]
    public void NodeOrderIsTheAbaqusC3D10Convention()
    {
        var linear = OneTet();
        var q = QuadraticTetMesh.From(linear);
        var e = q.Tets[0];
        var l = linear.GetTet(0);

        // 4 = mid(0,1), 5 = mid(1,2), 6 = mid(0,2), 7 = mid(0,3), 8 = mid(1,3), 9 = mid(2,3)
        AssertMidpoint(q, e.N4, linear.Position(l.A), linear.Position(l.B));
        AssertMidpoint(q, e.N5, linear.Position(l.B), linear.Position(l.C));
        AssertMidpoint(q, e.N6, linear.Position(l.A), linear.Position(l.C));
        AssertMidpoint(q, e.N7, linear.Position(l.A), linear.Position(l.D));
        AssertMidpoint(q, e.N8, linear.Position(l.B), linear.Position(l.D));
        AssertMidpoint(q, e.N9, linear.Position(l.C), linear.Position(l.D));
    }

    [Fact]
    public void CornerNodesKeepTheirLinearIndices_SoCornerDataTransfersWithNoMapping()
    {
        var linear = TetMesher.Mesh(MeshPrimitives.Box(
            new Aabb(new Vector3d(0, 0, 0), new Vector3d(2, 3, 4))));
        var quadratic = QuadraticTetMesh.From(linear);

        Assert.Equal(linear.VertexCount, quadratic.CornerNodeCount);
        for (int v = 0; v < linear.VertexCount; v++)
            Assert.Equal(linear.Position(v), quadratic.Position(v));

        for (int t = 0; t < linear.TetCount; t++)
        {
            var l = linear.GetTet(t);
            var q = quadratic.Tets[t];
            Assert.Equal(l.A, q.N0);
            Assert.Equal(l.B, q.N1);
            Assert.Equal(l.C, q.N2);
            Assert.Equal(l.D, q.N3);
        }
    }

    [Fact]
    public void MidEdgeNodesAreSharedByEveryElementOnThatEdge()
    {
        // The whole content of the class: a node count of exactly V + E, so no edge got two
        // nodes and no two edges collapsed onto one.
        var linear = TetMesher.Mesh(MeshPrimitives.UvSphere(1.0, 16, 8));
        var quadratic = QuadraticTetMesh.From(linear);

        var edges = new HashSet<(int, int)>();
        for (int t = 0; t < linear.TetCount; t++)
        {
            var e = linear.GetTet(t);
            for (int i = 0; i < 4; i++)
                for (int j = i + 1; j < 4; j++)
                {
                    int a = e[i], b = e[j];
                    edges.Add(a < b ? (a, b) : (b, a));
                }
        }

        Assert.Equal(linear.VertexCount + edges.Count, quadratic.NodeCount);

        // Directly: two elements sharing an edge must name the SAME node for it.
        var nodeForEdge = new Dictionary<(int, int), int>();
        var pairs = new[] { (0, 1, 4), (1, 2, 5), (0, 2, 6), (0, 3, 7), (1, 3, 8), (2, 3, 9) };
        for (int t = 0; t < quadratic.TetCount; t++)
        {
            var q = quadratic.Tets[t];
            foreach (var (i, j, mid) in pairs)
            {
                int a = q[i], b = q[j];
                var key = a < b ? (a, b) : (b, a);
                if (nodeForEdge.TryGetValue(key, out int existing))
                    Assert.Equal(existing, q[mid]);
                else
                    nodeForEdge[key] = q[mid];
            }
        }
    }

    [Fact]
    public void StraightSidedElements_ReproduceTheLinearVolumeExactly()
    {
        var linear = TetMesher.Mesh(MeshPrimitives.Cylinder(1.0, 3.0, 24));
        var quadratic = QuadraticTetMesh.From(linear);
        Assert.Equal(linear.Volume, quadratic.Volume, Math.Abs(linear.Volume) * 1e-14);
    }

    [Fact]
    public void BoundaryFacetsGainThreeMidEdgeNodesAndKeepTheirTags()
    {
        var linear = TetMesher.Mesh(MeshPrimitives.Box(
            new Aabb(new Vector3d(0, 0, 0), new Vector3d(1, 1, 1))));
        var quadratic = QuadraticTetMesh.From(linear);

        Assert.Equal(linear.BoundaryFacetCount, quadratic.BoundaryFacets.Count);
        for (int i = 0; i < linear.BoundaryFacetCount; i++)
        {
            var l = linear.BoundaryFacets[i];
            var q = quadratic.BoundaryFacets[i];
            Assert.Equal(l.V0, q.V0);
            Assert.Equal(l.V1, q.V1);
            Assert.Equal(l.V2, q.V2);
            Assert.Equal(l.SourceTriangle, q.SourceTriangle);
            Assert.Equal(l.Tet, q.Tet);

            // The facet's mid-edge nodes must be the SAME nodes its owning element uses.
            var owner = quadratic.Tets[q.Tet];
            var owned = Enumerable.Range(0, 10).Select(k => owner[k]).ToHashSet();
            Assert.Contains(q.M01, owned);
            Assert.Contains(q.M12, owned);
            Assert.Contains(q.M20, owned);
        }
    }

    [Fact]
    public void ItIsAPureFunction_TwoCallsAgreeBitForBit()
    {
        var linear = TetMesher.Mesh(MeshPrimitives.UvSphere(2.0, 12, 6));
        var a = QuadraticTetMesh.From(linear);
        var b = QuadraticTetMesh.From(linear);

        Assert.Equal(a.NodeCount, b.NodeCount);
        for (int i = 0; i < a.NodeCount; i++)
            Assert.Equal(a.Position(i), b.Position(i));
        Assert.Equal(a.Tets, b.Tets);
    }

    [Fact]
    public void RegionsCarryThrough()
    {
        var x = MeshPrimitives.Box(new Aabb(new Vector3d(0, 0, 0), new Vector3d(1, 1, 1)));
        var y = MeshPrimitives.Box(new Aabb(new Vector3d(5, 0, 0), new Vector3d(6, 1, 1)));
        var linear = TetMesher.Mesh([x, y], null, out _);
        var quadratic = QuadraticTetMesh.From(linear);

        for (int t = 0; t < linear.TetCount; t++)
            Assert.Equal(linear.RegionOf(t), quadratic.RegionOf(t));
    }

    private static void AssertMidpoint(QuadraticTetMesh mesh, int node, Vector3d a, Vector3d b) =>
        Assert.Equal((a + b) * 0.5, mesh.Position(node));

    private static TetMesh OneTet() => TetMesher.Mesh(HalfEdgeMesh.Build(
        [new Vector3d(0, 0, 0), new Vector3d(1, 0, 0), new Vector3d(0, 1, 0), new Vector3d(0, 0, 1)],
        [new[] { 0, 2, 1 }, new[] { 0, 1, 3 }, new[] { 1, 2, 3 }, new[] { 0, 3, 2 }]));
}
