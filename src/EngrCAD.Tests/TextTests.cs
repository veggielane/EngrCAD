using System.Numerics;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Sketcher;
using Xunit;

namespace EngrCAD.Tests;

public class TextTests
{
    [Fact]
    public void Volume()
    {
        var G = new Text(CSYS.XY, "SONIA").Extrude(5f);
        G.SaveSTL("G.stl");
        Assert.True(G.Volume > 0);

    }
}