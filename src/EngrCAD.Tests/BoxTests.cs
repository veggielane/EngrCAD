using System;
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
            X = size,
            Y = size,
            Z = size
        };

        var calc = MathF.Pow(size, 3);
        Assert.Equal(calc, box.CalculateVolume(), 3);
    }
}