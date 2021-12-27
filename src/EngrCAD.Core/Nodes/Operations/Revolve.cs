using System.Linq;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Sketcher;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

public class Revolve : BaseNode
{
    public ClosedSketch Sketch { get; }
    public IAxis Axis { get; }

    public Revolve(ClosedSketch sketch, IAxis axis)
    {
        Sketch = sketch;
        Axis = axis;
    }

    public override ShapeWrapper Generate()
    {
        var edges = Sketch.Edges.Select(o => o.ToEdge()).ToList();
        return ShapeWrapper.Revolve(edges, Axis.Origin, Axis.Direction);
    }
}

public class RevolveAngle : Revolve
{

    public float Angle { get; }

    public RevolveAngle(ClosedSketch sketch, IAxis axis, float angle):base(sketch,axis)
    {

        Angle = angle;
    }

    public override ShapeWrapper Generate()
    {
        var edges = Sketch.Edges.Select(o => o.ToEdge()).ToList();
        return ShapeWrapper.Revolve(edges, Axis.Origin, Axis.Direction);
    }
}