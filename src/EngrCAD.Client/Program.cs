using System;
using System.Numerics;
using EngrCAD.Core;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Nodes.Shapes;
using EngrCAD.Core.Sketcher;
using Plane = EngrCAD.Core.Sketcher.Plane;

namespace EngrCAD.Client
{
    class Program
    {
        static void Main(string[] args)
        {

            var sphereA = new Sphere()
            {
                Radius = 10f
            };

            var sphereB = sphereA.Translate(new Vector3(5,5,5));

            var shape = sphereA.Subtract(sphereB);
            shape.SaveSTP("test.stp");
            shape.SaveSTL("test.stl");

            var a = new Box { X = 2, Y = 2, Z = 2 }.Translate(-0.25f, -0.25f, -0.25f);
            var b = new Sphere { Radius = 1.3f }.Translate(0.25f, 0.25f, 0.25f);

            a.Union(b).SaveSTP("union.stp");
            a.Subtract(b).SaveSTP("subtract.stp");
            a.Intersect(b).SaveSTP("intersect.stp");


            var sketch = new Sketch(Plane.XY).HorizontalLine(10).VerticalLine(10).HorizontalLine(-10).Close();

            var extrude =
        }
    }
}
