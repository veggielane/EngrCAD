using System;
using System.Numerics;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Numerics;
using EngrCAD.Core.Sketcher;
using Xunit;

namespace EngrCAD.Tests;

public class LoftTests
{
    [Fact]
    public void Volume()
    {
        var loft = new Rectangle(CSYS.XY, 5, 10).Loft(new Circle(CSYS.XY.Translate(new Vector3(0, 0, 10)), 3));
        Assert.True(loft.Volume > 0);
    }
}