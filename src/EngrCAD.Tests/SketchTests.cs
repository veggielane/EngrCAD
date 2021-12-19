using EngrCAD.Core.Sketcher;
using Xunit;

namespace EngrCAD.Tests;

public class SketchTests
{
    [Fact]
    public void Create()
    {
        var sketch = new Sketch(Plane.XY).HorizontalLine(5).VerticalLine(6).Close();


    }
}