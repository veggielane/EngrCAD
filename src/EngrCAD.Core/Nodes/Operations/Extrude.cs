using System.Linq;
using EngrCAD.Core.Sketcher;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

public class Extrude : BaseNode
{
    public IClosedSketch Sketch { get; }

    public float Distance { get; }
        
    public Extrude(IClosedSketch sketch, float distance)
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