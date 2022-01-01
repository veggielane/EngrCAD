using System.Collections.Generic;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Sketcher.Edges;

namespace EngrCAD.Core.Sketcher;

public class Circle:IClosedSketch
{
    public ICSYS CSYS { get; }
    public IReadOnlyList<ISketchEdge> Edges { get; }

    public Circle(ICSYS csys, float radius)
    {
        CSYS = csys;
        Edges = new List<ISketchEdge>() { new SketchEdgeCircle()
        {
            Center = csys.Origin,
            Radius = radius,
            Direction = csys.Normal
        } };
    }
}