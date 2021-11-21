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

            sphereA.Subtract(sphereB).SaveSTP("sphere_sub.stp");
        }
    }
}
