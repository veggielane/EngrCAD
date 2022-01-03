using System.Numerics;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Nodes.Primitives;
using EngrCAD.Core.Sketcher;
using Xunit;

namespace EngrCAD.Tests;

public class TextTests
{
    [Fact]
    public void Volume()
    {
        var text = new Text()
        {
            Line = "SONIA",
            Thickness = 5f
        };
        text.SaveSTL("G.stl");
        Assert.True(text.Volume > 0);

        var aabb = text.BoundingBox;


    }
}