using System.Collections.Generic;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Sketcher.Edges;

namespace EngrCAD.Core.Sketcher;

public class Rectangle : IClosedSketch
{
    public ICSYS CSYS { get; }
    public IReadOnlyList<ISketchEdge> Edges { get; }

    public Rectangle(ICSYS csys, float x, float y)
    {
        CSYS = csys;
        var sketch = new Sketch(csys)
            .MoveTo(-x / 2f, -y / 2f)
            .HorizontalLine(x)
            .VerticalLine(y)
            .HorizontalLine(-x)
            .VerticalLine(-y);
        Edges = sketch.Edges;
    }
}