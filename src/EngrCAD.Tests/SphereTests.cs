using System;
using EngrCAD.Core.Nodes.Primitives;
using Xunit;

namespace EngrCAD.Tests;

public class SphereTests
{
    [Fact]
    public void Volume()
    {
        var sphere = new Sphere
        {
            Radius = 7f
        };

        var calc = (4f / 3f) * MathF.PI * MathF.Pow(sphere.Radius, 3);
        Assert.Equal(calc,sphere.Volume,3);
    }
}