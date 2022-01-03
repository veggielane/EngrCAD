using System.Collections.Generic;
using System.Linq;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Sketcher;
using EngrCAD.Core.Sketcher.Edges;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

internal class Extrude : Node
{
    public ICSYS CSYS { get; }
    public IEnumerable<ISketchEdge> SketchEdges { get; }
    public float Distance { get; }
        
    public Extrude(ICSYS csys, IEnumerable<ISketchEdge> edges, float distance)
    {

        CSYS = csys;
        SketchEdges = edges;
        Distance = distance;
    }

    public override ShapeWrapper Generate()
    {
        var edges = SketchEdges.Select(o => o.ToEdge()).ToList();
        return ShapeWrapper.Extrude(edges, CSYS.Normal*Distance);
    }
}