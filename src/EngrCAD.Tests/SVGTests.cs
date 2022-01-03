using System.Linq;
using EngrCAD.Core;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Nodes.IO;
using EngrCAD.Core.Nodes.Primitives;
using Xunit;

namespace EngrCAD.Tests;

public class SVGTests
{
    [Fact]
    public void FileSystem()
    {

        var triangle = "<svg height=\"210\" width=\"400\"><path d=\"M150 0 L75 200 L225 200 Z\" /></svg>";

        var svg = SVG.FromString(CSYS.XY, triangle).Sketches.ToList();
        svg.First().Extrude(20f).Save("svg.stl");


        //var arcs =
        //    "<svg width=\"325\" height=\"325\" xmlns=\"http://www.w3.org/2000/svg\">\r\n  <path d=\"M 80 80A 45 45, 0, 0, 0, 125 125L 125 80 Z\" fill=\"green\"/>\r\n  <path d=\"M 230 80A 45 45, 0, 1, 0, 275 125L 275 80 Z\" fill=\"red\"/>\r\n  <path d=\"M 80 230A 45 45, 0, 0, 1, 125 275L 125 230 Z\" fill=\"purple\"/>\r\n  <path d=\"M 230 230A 45 45, 0, 1, 1, 275 275L 275 230 Z\" fill=\"blue\"/>\r\n</svg>";
        //var t = SVG.FromString(CSYS.XY, arcs).Sketches.Select(sketch => sketch.Extrude(10));
        //t.UnionAll().Save("arcs.stl");
        /*
        var import = new Import
        {
            FilePath = "Models/sphere-7.stp"
        };
        var sphere = new Sphere
        {
            Radius = 7f
        };
        Assert.Equal(sphere.Volume, import.Volume, 3);*/
    }
}