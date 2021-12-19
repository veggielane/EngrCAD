using System;
using System.Linq;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Sketcher;
using Xunit;

namespace EngrCAD.Tests;

public class SketchTests
{
    [Fact]
    public void Create()
    {
        var size = 5f;
        var sketch = new Sketch(Plane.XY).HorizontalLine(size).VerticalLine(size).HorizontalLine(-size).Close();
        var extrude = sketch.Extrude(size);
        var calc = MathF.Pow(size, 3);
        Assert.Equal(calc, extrude.CalculateVolume(), 3);
    }
}