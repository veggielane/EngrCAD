using System;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Nodes.Primitives;
using Xunit;

namespace EngrCAD.Tests;

public class CylinderTests
{
    [Fact]
    public void Volume()
    {
        var cylinder = new Cylinder
        {
            Radius = 5f,
            Height = 10f
        };

        var calc = MathF.PI * MathF.Pow(cylinder.Radius, 2) * cylinder.Height;
        Assert.Equal(calc, cylinder.Volume, 3);
    }

    [Fact]
    public void Centered()
    {
        var a = new Cylinder
        {
            Radius = 4f,
            Height = 8f
        };

        var b = new Cylinder
        {
            Radius = 4f,
            Height = 8f,
            Centered = false
        };

        var box = new Box()
        {
            X = 10,
            Y = 10,
            Z = 10
        }.Translate(5, 0, 0);

        var aVolume = a.Subtract(box).Volume;
        var bVolume = b.Volume;

        Assert.Equal(aVolume, bVolume/ 2f, 3);
    }
}