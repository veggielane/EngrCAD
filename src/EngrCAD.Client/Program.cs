using System;
using System.Numerics;
using EngrCAD.Core;
using EngrCAD.Core.Nodes;

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
        }
    }
}
