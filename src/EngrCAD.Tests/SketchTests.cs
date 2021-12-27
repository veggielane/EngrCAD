using System;
using System.Numerics;
using EngrCAD.Core.Datums;
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
        var height = 10f;
        var sketch = new Sketch(CSYS.XY).HorizontalLine(size).VerticalLine(size).HorizontalLine(-size).Close();
        var extrude = sketch.Extrude(height);
        var calc = MathF.Pow(size, 2)* height;
        Assert.Equal(calc, extrude.Volume, 3);


        var heart = new Sketch(CSYS.XY)
            .MoveTo(140, 20)
            .BezierCurveTo(new Vector2(20, 140), new Vector2(73, 20), new Vector2(20, 74))
            .BezierCurve(new Vector2(228, 303), new Vector2(0, 135), new Vector2(136, 170))
            .BezierCurve(new Vector2(229, -303), new Vector2(88, -132), new Vector2(229, -173))
            .BezierCurve(new Vector2(-120, -120), new Vector2(0, -66), new Vector2(-54, -120))
            .BezierCurve(new Vector2(-109, 69), new Vector2(-48, 0), new Vector2(-90, 28))
            .BezierCurve(new Vector2(-108, -69), new Vector2(-19, -41), new Vector2(-60, -69))
            .Close()
            .Extrude(10).Centered();
        heart.SaveSTL("heart.stl");
        //  

    }
}