using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

public class TopologyTests
{
    [Fact]
    public void Box_CountsAndEuler()
    {
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 4)));
        box.Validate();

        Assert.Equal(8, box.Vertices.Count());
        Assert.Equal(12, box.Edges.Count());
        Assert.Equal(6, box.Faces.Count());
        Assert.Equal(6, box.Loops.Count());
        Assert.True(box.SatisfiesEulerFormula(genus: 0));
        Assert.False(box.SatisfiesEulerFormula(genus: 1));
    }

    [Fact]
    public void Box_EveryCoedgeHasAnOppositePartner()
    {
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (1, 1, 1)));
        foreach (var coedge in box.Coedges)
        {
            var partner = coedge.Partner;
            Assert.NotNull(partner);
            Assert.Same(coedge.Edge, partner.Edge);
            Assert.NotEqual(coedge.SameSense, partner.SameSense);
            Assert.Same(coedge, partner.Partner);
        }
    }

    [Fact]
    public void Box_FaceNormalsPointOutward()
    {
        var bounds = new Aabb((0, 0, 0), (2, 2, 2));
        var box = SolidFactory.MakeBox(bounds);
        foreach (var face in box.Faces)
        {
            var plane = Assert.IsType<PlaneSurface>(face.Surface);
            var toFace = plane.Origin - bounds.Center;
            Assert.True(plane.Normal.Dot(toFace) > 0, "surface normal should point away from the solid's center");
        }
    }

    [Fact]
    public void Cylinder_CountsAndEuler()
    {
        var cylinder = SolidFactory.MakeCylinder(radius: 1, height: 2);
        cylinder.Validate();

        Assert.Equal(2, cylinder.Vertices.Count());
        Assert.Equal(2, cylinder.Edges.Count());
        Assert.Equal(3, cylinder.Faces.Count());
        Assert.Equal(4, cylinder.Loops.Count());
        Assert.True(cylinder.SatisfiesEulerFormula(genus: 0));
    }

    [Fact]
    public void Cylinder_ClosedEdgesShareTheirSeamVertex()
    {
        var cylinder = SolidFactory.MakeCylinder(1, 2);
        foreach (var edge in cylinder.Edges)
        {
            Assert.True(edge.IsClosedEdge);
            Assert.Same(edge.StartVertex, edge.EndVertex);
            Assert.Equal(2, edge.Uses.Count);
        }
    }

    [Fact]
    public void Validate_DetectsBrokenLoopChains()
    {
        // Two disconnected line edges cannot chain into a loop.
        var v0 = new BrepVertex((0, 0, 0));
        var v1 = new BrepVertex((1, 0, 0));
        var v2 = new BrepVertex((0, 1, 0));
        var v3 = new BrepVertex((1, 1, 0));
        var e0 = new BrepEdge(new Line3d(v0.Position, v1.Position), Interval.Unit, v0, v1);
        var e1 = new BrepEdge(new Line3d(v2.Position, v3.Position), Interval.Unit, v2, v3);
        var loop = new BrepLoop([new BrepCoedge(e0, true), new BrepCoedge(e1, true)]);
        var face = new BrepFace(new PlaneSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY), [loop]);
        var solid = new BrepSolid([new BrepShell([face])]);

        Assert.Throws<InvalidOperationException>(solid.Validate);
    }
}
