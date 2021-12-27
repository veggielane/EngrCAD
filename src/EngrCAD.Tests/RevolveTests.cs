using System;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Nodes.Primitives;
using EngrCAD.Core.Sketcher;
using Xunit;

namespace EngrCAD.Tests;

public class RevolveTests
{
    [Fact]
    public void Volume()
    {
        var id = 30f;
        var od = 70f;
        var thickness = 20f;
        var sketch = new Sketch(CSYS.XY)
            .MoveTo(id, thickness)
            .LineTo(od, thickness)
            .LineTo(od, 0)
            .LineTo(id, 0)
            .Close();
        var revolve = sketch.Revolve(Axis.Y);
        Assert.NotNull(revolve);
        Assert.Equal(ShapeType.Solid,revolve.ShapeType);
        var outer = MathF.PI * MathF.Pow(od, 2) * thickness;
        var inner = MathF.PI * MathF.Pow(id, 2) * thickness;
        Assert.Equal(outer - inner, revolve.Volume, 1);
    }
}