using System.IO;
using EngrCAD.Core.Nodes;
using EngrCAD.Examples;
using Xunit;

namespace EngrCAD.Tests;

public class ExampleTests
{
    private static string _examplePath = "../../../../../../examples";
    [Fact]
    public void GenerateExamples()
    {
        ExampleShapes.Classic().SaveSTL(Path.Combine(_examplePath, "classic.stl"));
        ExampleShapes.Heart().SaveSTL(Path.Combine(_examplePath, "heart.stl"));
    }
}