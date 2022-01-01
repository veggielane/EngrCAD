using System;
using System.Numerics;
using EngrCAD.Core.Nodes.Primitives;
using Xunit;

namespace EngrCAD.Tests;

public class BoxTests
{
    [Fact]
    public void Volume()
    {
        var size = 5f;
        var box = new Box
        {
            Size = new Vector3(size)
        };

        var calc = MathF.Pow(size, 3);
        Assert.Equal(calc, box.Volume, 3);
    }
}