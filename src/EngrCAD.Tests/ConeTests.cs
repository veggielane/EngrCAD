using System;
using EngrCAD.Core.Nodes.Primitives;
using Xunit;

namespace EngrCAD.Tests;

public class ConeTests
{
    [Fact]
    public void Volume()
    {
        var cone = new Cone()
        {
            BottomRadius = 5f,
            Height = 10f
        };
        var calc = (1f / 3f) * MathF.PI * MathF.Pow(cone.BottomRadius,2) * cone.Height;
        Assert.Equal(calc, cone.Volume, 3);
    }
}