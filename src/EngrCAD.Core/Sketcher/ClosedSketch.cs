using System.Collections.Generic;
using System.Linq;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Sketcher.Edges;

namespace EngrCAD.Core.Sketcher;

public class ClosedSketch: IClosedSketch
{
    public ICSYS CSYS { get; }
    public IReadOnlyList<ISketchEdge> Edges { get; }
    internal ClosedSketch(ICSYS csys, IEnumerable<ISketchEdge> sketchObjects)
    {
        CSYS = csys;
        Edges = sketchObjects.ToList();
    }
}