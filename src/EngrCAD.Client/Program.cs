using System;
using EngrCAD.Core;
using EngrCAD.Core.Nodes;

namespace EngrCAD.Client
{
    class Program
    {
        static void Main(string[] args)
        {

            var sphere = new Sphere()
            {
                Radius = 10f
            };

            sphere.SaveSTP("sphere.stp");
        }
    }
}
