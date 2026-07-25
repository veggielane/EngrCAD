using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The secondary progress line (<see cref="MeshFlavor"/>): it names the route the part
/// actually takes through the kernel. The point of these tests is that the famous one
/// is a TRUE statement — "Reticulating splines..." appears only for geometry that really
/// carries NURBS.
/// </summary>
public class MeshFlavorTests
{
    [Fact]
    public void SketchDerivedPartsReticulateSplines()
    {
        // A sketch profile becomes exact NurbsCurves on the way to a B-Rep — lines,
        // arcs and Beziers alike — so this part genuinely has splines in it.
        var sketch = Sketch.Start(0, 0)
            .LineTo(2, 0)
            .ArcTo(new Vector2d(2, 1), 0.8, clockwise: false)
            .BezierTo(new(1, 1.6), new(0.4, 1.4), new(0, 1))
            .Close();
        Assert.Equal(MeshFlavor.Splines, MeshFlavor.For(new Part("plate", Shape.Extrude(sketch, 0.4))));
    }

    [Fact]
    public void SolidsWithNurbsGeometryReticulateSplinesToo()
    {
        // A swept solid's rails are NurbsCurves; the flavor reads them off the solid.
        var path = new NurbsCurve(2, [(0, 0, 0), (0, 0, 1), (0, 1, 2)], null, [0, 0, 0, 1, 1, 1]);
        var tube = SolidFactory.Sweep(
            Profile.Circle(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 0.3), path);
        Assert.Equal(MeshFlavor.Splines, MeshFlavor.For(new Part("tube", tube)));
    }

    [Fact]
    public void PlainPrimitivesGetTheDryMessages()
    {
        // No splines anywhere in a box of planes: the joke must not fire.
        var box = new Part("box", Shape.Box(1, 1, 1));
        Assert.Equal(MeshFlavor.Lowering, MeshFlavor.For(box));
        Assert.NotEqual(MeshFlavor.Splines, MeshFlavor.For(box));

        Assert.Equal(MeshFlavor.Tessellating,
            MeshFlavor.For(new Part("solid", SolidFactory.MakeBox(new Aabb((0, 0, 0), (1, 1, 1))))));
        Assert.Equal(MeshFlavor.Field, MeshFlavor.For(new Part("blob", Sdf.Sphere(1))));
        Assert.Equal(MeshFlavor.Mesh, MeshFlavor.For(new Part("mesh", MeshPrimitives.Box(1, 1, 1))));
    }

    [Fact]
    public void ImplicitOnlyShapesPolygonizeTheField()
    {
        // A smooth blend has no B-Rep route, so its mesh comes from the field.
        var blend = Shape.Sphere(1).SmoothUnion(Shape.Sphere(1).Translate(1, 0, 0), 0.4);
        Assert.Equal(MeshFlavor.Field, MeshFlavor.For(new Part("blend", blend)));
    }

    [Fact]
    public void EveryFlavorIsPlainAsciiAndEndsInAnEllipsis()
    {
        foreach (string flavor in new[]
        {
            MeshFlavor.Splines, MeshFlavor.Field, MeshFlavor.Lowering,
            MeshFlavor.Tessellating, MeshFlavor.Mesh,
        })
        {
            Assert.EndsWith("...", flavor);
            Assert.All(flavor, c => Assert.InRange(c, ' ', '~'));
        }
        Assert.Equal("Reticulating splines...", MeshFlavor.Splines);
    }
}
