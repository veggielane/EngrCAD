using System.Numerics;
using EngrCAD.Core;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Nodes.Primitives;
using EngrCAD.Core.Sketcher;

namespace EngrCAD.Examples;

public static class ExampleShapes
{
    public static INode Classic()
    {
        var cube = new Box {X = 2f, Y = 2f, Z = 2f };
        var sphere = new Sphere { Radius = 1.35f };
        var cylinderA = new Cylinder { Radius = 0.7f, Height = 3 };
        var cylinderB = cylinderA.RotateX(MathHelper.DegreesToRadians(90));
        var cylinderC = cylinderA.RotateY(MathHelper.DegreesToRadians(90));
        return cube.Intersect(sphere).Subtract((cylinderA, cylinderB, cylinderC).Union());
    }

    public static INode Heart()
    {
        return new Sketch(Plane.XY)
            .MoveTo(140, 20)
            .BezierCurveTo(new Vector2(20, 140), new Vector2(73, 20), new Vector2(20, 74))
            .BezierCurve(new Vector2(228, 303), new Vector2(0, 135), new Vector2(136, 170))
            .BezierCurve(new Vector2(229, -303), new Vector2(88, -132), new Vector2(229, -173))
            .BezierCurve(new Vector2(-120, -120), new Vector2(0, -66), new Vector2(-54, -120))
            .BezierCurve(new Vector2(-109, 69), new Vector2(-48, 0), new Vector2(-90, 28))
            .BezierCurve(new Vector2(-108, -69), new Vector2(-19, -41), new Vector2(-60, -69))
            .Close()
            .Extrude(10)
            .Centered();
    }
}