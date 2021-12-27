using System.Linq;
using EngrCAD.Core.Sketcher;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

public class Extrude : BaseNode
{
    public ClosedSketch Sketch { get; }

    public float Distance { get; }
        
    public Extrude(ClosedSketch sketch, float distance)
    {
        Sketch = sketch;
        Distance = distance;
    }

    public override ShapeWrapper Generate()
    {
        var edges = Sketch.Edges.Select(o => o.ToEdge()).ToList();
        return ShapeWrapper.Extrude(edges, Sketch.CSYS.Normal*Distance);
    }
}