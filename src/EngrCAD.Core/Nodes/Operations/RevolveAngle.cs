using System.Linq;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Sketcher;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

public class RevolveAngle : Revolve
{

    public float Angle { get; }

    public RevolveAngle(IClosedSketch sketch, IAxis axis, float angle)
        :base(sketch,axis)
    {
        Angle = angle;
    }

    public override ShapeWrapper Generate() => ShapeWrapper.Revolve(Sketch.Edges.Select(o => o.ToEdge()).ToList(), Axis.Origin, Axis.Direction);
}