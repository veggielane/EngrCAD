using System;
using System.Numerics;
using EngrCAD.Core;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Nodes.Primitives;
using EngrCAD.Core.Sketcher;
using Plane = EngrCAD.Core.Sketcher.Plane;

namespace EngrCAD.Client;

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

        a.Round(0.8f).SaveSTL("round_2.stl");

        a.Round(0.5f, edges => edges[..4]).SaveSTL("round_3.stl");

        a.Round((0.1f, edges => edges[0..12]), (0.8f, edges => edges[13..^0])).SaveSTL("round_5.stl");


        a.Round(edge => edge switch
        {
            
            _ => 0.1f
        }).SaveSTL("switch.stl");

        var faces = a.Faces;



        var b = new Sphere { Radius = 1.3f }.Translate(0.25f, 0.25f, 0.25f);

        a.Union(b).SaveSTP("union.stp");
        a.Subtract(b).SaveSTP("subtract.stp");
        a.Intersect(b).SaveSTP("intersect.stp");


        //var sketch = new Sketch(Plane.XY).HorizontalLine(10).VerticalLine(10).HorizontalLine(-10).Close();

        //var extrude =
    }
}