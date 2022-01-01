using System.Collections.Generic;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Sketcher.Edges;

namespace EngrCAD.Core.Sketcher;

public class Circle:ClosedSketch
{
    public Circle(ICSYS csys, float radius):base(csys, new List<ISketchEdge>() { new SketchEdgeCircle { Center = csys.Origin, Radius = radius, Direction = csys.Normal } })
    {

    }
}