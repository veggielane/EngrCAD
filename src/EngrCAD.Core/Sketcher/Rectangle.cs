using EngrCAD.Core.Datums;

namespace EngrCAD.Core.Sketcher;

public class Rectangle : ClosedSketch
{
    public Rectangle(ICSYS csys, float x, float y):base(csys, new Sketch(csys)
        .MoveTo(-x / 2f, -y / 2f)
        .HorizontalLine(x)
        .VerticalLine(y)
        .HorizontalLine(-x)
        .VerticalLine(-y).Edges)
    {

    }
}