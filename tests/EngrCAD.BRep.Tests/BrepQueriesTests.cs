using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

public class BrepQueriesTests
{
    [Fact]
    public void Box_ClassifiesFacesAndEdges()
    {
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 1, 1)));

        Assert.Equal(6, box.Faces.Count(f => f.IsPlanar(out _, out _)));
        Assert.Single(box.PlanarFacesWithNormal(Vector3d.UnitZ));
        Assert.Single(box.PlanarFacesWithNormal(-Vector3d.UnitX));

        var edges = box.Edges.ToList();
        Assert.Equal(12, edges.Count);
        Assert.All(edges, e => Assert.True(e.IsLinear(out _, out _)));
        Assert.All(edges, e => Assert.True(box.IsConvex(e)));
        Assert.Equal(4 * (2 + 1 + 1), edges.Sum(e => e.Length()), 9);

        foreach (var edge in edges)
            Assert.Equal(2, box.FacesOf(edge).Count);
        var top = box.PlanarFacesWithNormal(Vector3d.UnitZ).Single();
        Assert.Equal(4, top.Edges().Count());
    }

    [Fact]
    public void Cylinder_ClassifiesBandAndRims()
    {
        var cylinder = SolidFactory.MakeCylinder(0.75, 2);
        var band = cylinder.Faces.Single(f => f.IsCylindrical(out _, out _, out _));
        band.IsCylindrical(out _, out var axis, out double radius);
        Assert.True(axis.IsParallelTo(Vector3d.UnitZ, Tolerance.Default));
        Assert.Equal(0.75, radius, 12);

        Assert.Equal(2, cylinder.Edges.Count(e => e.IsCircular(out _, out _, out _)));
        var rim = cylinder.Edges.First(e => e.IsCircular(out _, out _, out double r) && r == 0.75);
        Assert.Equal(2 * Math.PI * 0.75, rim.Length(), 9);
    }

    [Fact]
    public void Bounds_PoleBoundedHemisphereFace_IncludesTheDome()
    {
        // A hemisphere face's only edge is its equator: edge samples alone would give a
        // flat disk and boolean face-pair prefiltering would skip real intersections
        // with cap planes. Face bounds must include the surface's own (finite) domain.
        var sphere = SolidFactory.MakeSphere(13);
        var north = sphere.Faces.First(f =>
            f.Loops[0].Coedges[0].SameSense); // the north generator starts at the equator

        var bounds = north.Bounds();
        Assert.True(bounds.Max.Z > 12.9, $"dome missing from bounds: {bounds}");
        Assert.True(bounds.Min.Z > -0.1 && bounds.Min.Z < 0.1, $"unexpected lower bound: {bounds}");
    }

    [Fact]
    public void ConcaveEdge_IsNotConvex()
    {
        // An L-shaped extrusion has exactly one concave vertical edge at the inner corner.
        var profile = Profile.FromPoints([(0, 0, 0), (2, 0, 0), (2, 1, 0), (1, 1, 0), (1, 2, 0), (0, 2, 0)]);
        var solid = SolidFactory.Extrude(profile, (0, 0, 1));
        var vertical = solid.Edges
            .Where(e => e.IsLinear(out var a, out var b) && (b - a).IsParallelTo(Vector3d.UnitZ, Tolerance.Default))
            .ToList();
        Assert.Equal(6, vertical.Count);
        Assert.Equal(1, vertical.Count(e => !solid.IsConvex(e)));
    }
}
