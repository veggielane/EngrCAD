using EngrCAD.Core.Nodes.Primitives;
using Xunit;

namespace EngrCAD.Tests;

public class ImportTests
{
    [Fact]
    public void FileSystem()
    {
        var import = new Import
        {
            FilePath = "Models/sphere-7.stp"
        };
        var sphere = new Sphere
        {
            Radius = 7f
        };
        Assert.Equal(sphere.Volume, import.Volume, 3);
    }
}