using System;
using System.IO;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Nodes.Primitives;
using EngrCAD.Examples;
using Xunit;

namespace EngrCAD.Tests;

public class ExampleTests
{
    private static string _examplePath = "../../../../../../examples";
    [Fact]
    public void GenerateExamples()
    {
        var classic = ExampleShapes.Classic();
        classic.SaveSTL(Path.Combine(_examplePath, "classic.stl"));
    }
}