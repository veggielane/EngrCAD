using System.Collections.Generic;
using System.Linq;
using EngrCAD.Core.Sketcher;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

internal class Loft : Node
{
    public List<IClosedSketch> Sketches { get; }

    public Loft(IEnumerable<IClosedSketch> sketches)
    {
        Sketches = new List<IClosedSketch>(sketches);
    }

    public override ShapeWrapper Generate()
    {
        var listOfEdges = Sketches.Select(sketch => sketch.Edges.Select(e => e.ToEdge()).ToList()).ToList();
        return ShapeWrapper.Loft(listOfEdges);
    }
}