using System.Numerics;
using EngrCAD.Core;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Nodes.Primitives;

namespace EngrCAD.Examples;

public static class ExampleShapes
{
    public static INode Classic()
    {
        var cube = new Box {X = 2f, Y = 2f, Z = 2f };
        var sphere = new Sphere { Radius = 1.35f };
        var cylinderA = new Cylinder { Radius = 0.7f, Height = 3 };
        var cylinderB = cylinderA.RotateX(MathHelper.DegreesToRadians(90));
        //var cylinderB = cylinderA.Transform(Matrix4x4.CreateRotationX(MathHelper.DegreesToRadians(90)));
        var cylinderC = cylinderA.RotateY(MathHelper.DegreesToRadians(90));
        //var cylinderC = cylinderA.Transform(Matrix4x4.CreateRotationY(MathHelper.DegreesToRadians(90)));
        return cube.Intersect(sphere).Subtract(cylinderA.Union(cylinderB).Union(cylinderC));
    }
}