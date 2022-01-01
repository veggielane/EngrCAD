using System;
using System.Numerics;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Nodes.Primitives;
using Xunit;

namespace EngrCAD.Tests;

public class IntersectionTests
{
    [Fact]
    public void Volume()
    {
        var sphere = new Sphere
        {
            Radius = 5f
        };
        var box = new Box()
        {
            Size = new Vector3(10, 10, 10)
        }.Translate(5, 0, 0);

        var calc = (4f / 3f) * MathF.PI * MathF.Pow(sphere.Radius, 3) / 2f;

        Assert.Equal(calc, (sphere.Intersect(box)).Volume, 3);
    }
}