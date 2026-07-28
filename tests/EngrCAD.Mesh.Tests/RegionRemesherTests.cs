using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// Remeshing one face selection in place. The invariant throughout is the region operator's:
/// the model stays closed and manifold, and everything outside the selection is untouched
/// except where a refined seam had to be carried into it.
/// </summary>
public class RegionRemesherTests
{
    private static HalfEdgeMesh Box() =>
        MeshPrimitives.Box(new Aabb((0, 0, 0), (10, 10, 10))).Triangulated();

    /// <summary>The faces whose vertices all sit on z = 10.</summary>
    private static MeshFaceSelection Top(HalfEdgeMesh mesh) => MeshFaceSelection.FromIndices(
        mesh, mesh.Faces.Where(f => f.Vertices().All(v => v.Position.Z == 10)).Select(f => f.Index));

    private static double LongestEdge(HalfEdgeMesh mesh, IEnumerable<int> faces)
    {
        double longest = 0;
        foreach (int f in faces)
        {
            foreach (var he in mesh.GetFace(f).HalfEdges())
                longest = Math.Max(longest, he.Vector.Length);
        }
        return longest;
    }

    [Fact]
    public void Remesh_RefinesOnlyTheSelectedRegion()
    {
        var mesh = Box();
        var top = Top(mesh);

        var result = RegionRemesher.Remesh(mesh, top, new RemeshOptions(2.0)
        {
            Iterations = 10,
            FeatureAngleDegrees = 0, // the patch is flat; nothing to protect
        });

        result.Mesh.Validate();
        Assert.True(result.Mesh.IsClosed, "a region remesh must not open the model");
        Assert.Equal(2, result.Mesh.EulerCharacteristic);
        // The box is unchanged as a solid: the top is planar, so retriangulating it moves
        // nothing off the plane.
        Assert.Equal(1000.0, result.Mesh.Volume(), 9);
        Assert.Equal(600.0, result.Mesh.SurfaceArea(), 9);

        // The region got denser; the sides did not.
        Assert.True(result.Region.Count > top.Count * 4,
            $"expected the top to refine, {top.Count} -> {result.Region.Count}");
        double longest = LongestEdge(result.Mesh, result.Region.Indices);
        // The split threshold is 1.33 x target = 2.66, and smoothing runs AFTER the sweep, so
        // the last pass can leave an edge a little over it — hence the slack, not a wish.
        Assert.True(longest < 2.8, $"region edges should be near the 2.0 target, longest is {longest}");
        var elsewhere = Enumerable.Range(0, result.Mesh.FaceCount).Except(result.Region.Indices);
        Assert.True(LongestEdge(result.Mesh, elsewhere) > 10, "the sides keep their full-height edges");
    }

    [Fact]
    public void Remesh_CarriesTheRefinedSeamIntoTheNeighbours()
    {
        // The seam is pinned but splittable by default, so the rim gains vertices and the
        // side faces have to gain them too — otherwise the result is a T-junction. That it
        // comes back closed IS the assertion.
        var mesh = Box();
        var result = RegionRemesher.Remesh(mesh, Top(mesh), new RemeshOptions(2.0)
        {
            Iterations = 10,
            FeatureAngleDegrees = 0,
        });

        result.Mesh.Validate();
        Assert.Empty(result.Mesh.BoundaryLoops());
        // The rim really was refined: the box's top rim was 4 corners, and the sides now
        // carry more vertices at z = 10 than that.
        int onRim = Enumerable.Range(0, result.Mesh.VertexCount)
            .Count(v => result.Mesh.GetPosition(v).Z == 10);
        Assert.True(onRim > 4);
    }

    [Fact]
    public void Remesh_WithSplitFixedEdgesOff_KeepsTheRimVertexForVertex()
    {
        var mesh = Box();
        var top = Top(mesh);
        var result = RegionRemesher.Remesh(mesh, top, new RemeshOptions(2.0)
        {
            Iterations = 10,
            FeatureAngleDegrees = 0,
            SplitFixedEdges = false,
        });

        result.Mesh.Validate();
        Assert.True(result.Mesh.IsClosed);
        // Exactly the four original corners sit on the rim, so no side face was touched.
        var rim = Enumerable.Range(0, result.Mesh.VertexCount)
            .Where(v => result.Mesh.GetPosition(v).Z == 10)
            .Select(v => result.Mesh.GetPosition(v))
            .ToList();
        Assert.Equal(4, rim.Count);
    }

    [Fact]
    public void Remesh_KeepsCurvatureThroughItsDefaultProjectionTarget()
    {
        // No target passed, so one is built over the extracted region. Without it, smoothing
        // is curvature flow and the patch would sink inward, leaving a dent inside an unmoved
        // rim — the reason the default is not "no projection".
        var sphere = MeshPrimitives.UvSphere(1.0, 32, 20).Triangulated();
        var cap = MeshFaceSelection.FromIndices(
            sphere, sphere.Faces.Where(f => f.Vertices().All(v => v.Position.Z > 0.5)).Select(f => f.Index));

        var result = RegionRemesher.Remesh(sphere, cap, new RemeshOptions(0.12)
        {
            Iterations = 12,
            FeatureAngleDegrees = 0,
        });

        result.Mesh.Validate();
        Assert.True(result.Mesh.IsClosed);
        double worst = 0;
        foreach (int f in result.Region.Indices)
        {
            foreach (var vertex in result.Mesh.GetFace(f).Vertices())
                worst = Math.Max(worst, Math.Abs(vertex.Position.Length - 1));
        }
        // Within the source tessellation's own chord sagitta of the unit sphere; a sunk cap
        // measures several times this.
        Assert.True(worst < 0.02, $"worst radial deviation in the region {worst}");
    }

    [Fact]
    public void Remesh_HonoursExplicitPinsGivenInBaseIndices()
    {
        var mesh = Box();
        var top = Top(mesh);
        var corner = Enumerable.Range(0, mesh.VertexCount)
            .First(v => mesh.GetPosition(v) == new Vector3d(10, 10, 10));

        var result = RegionRemesher.Remesh(mesh, top, new RemeshOptions(2.0)
        {
            Iterations = 8,
            FeatureAngleDegrees = 0,
            FixedVertices = [corner],
        });

        result.Mesh.Validate();
        Assert.Contains(result.Mesh.Vertices, v => v.Position == new Vector3d(10, 10, 10));
    }

    [Fact]
    public void Remesh_ChainsThroughItsOwnResult()
    {
        var mesh = Box();
        var first = RegionRemesher.Remesh(mesh, Top(mesh), new RemeshOptions(3.0)
        {
            Iterations = 8,
            FeatureAngleDegrees = 0,
        });
        var second = RegionRemesher.Remesh(first.Mesh, first.Region, new RemeshOptions(1.5)
        {
            Iterations = 8,
            FeatureAngleDegrees = 0,
        });

        second.Mesh.Validate();
        Assert.True(second.Mesh.IsClosed);
        Assert.True(second.Region.Count > first.Region.Count);
        Assert.Equal(1000.0, second.Mesh.Volume(), 9);
    }

    [Fact]
    public void Remesh_WholeMeshSelection_IsAPlainRemesh()
    {
        // No seam at all: the degenerate case must not need a special path.
        var sphere = MeshPrimitives.UvSphere(1.0, 20, 14).Triangulated();
        var all = MeshFaceSelection.FromIndices(sphere, Enumerable.Range(0, sphere.FaceCount));

        var result = RegionRemesher.Remesh(sphere, all, new RemeshOptions(0.2)
        {
            Iterations = 10,
            FeatureAngleDegrees = 0,
        });

        result.Mesh.Validate();
        Assert.True(result.Mesh.IsClosed);
        Assert.Equal(result.Mesh.FaceCount, result.Region.Count);
    }
}
