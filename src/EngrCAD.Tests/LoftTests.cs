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

        var loftRectangle = new Rectangle(CSYS.XY, 5, 10).Loft(new Rectangle(CSYS.XY.Translate(new Vector3(0, 0, 8)), 5, 10));
        Assert.Equal(5f*10f*8f, loftRectangle.Volume, 3);
    }
}