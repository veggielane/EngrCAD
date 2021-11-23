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

            var sphereB = new Sphere()
            {
                Radius = 10f
            }.Translate(new Vector3(5,5,5));

            var shape = sphereA.Subtract(sphereB);
            //shape.SaveSTP("sphere_sub.stp");
            new Sphere()
            {
                Radius = 100f
            }.SaveSTL("test.stl");
        }
    }
}
