using System.Collections.Generic;
using System.Linq;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Nodes.Operations;
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

public interface IClosedSketch
{
    public ICSYS CSYS { get; }
    public IReadOnlyList<ISketchEdge> Edges { get; }

    public INode Extrude(float distance) => new Extrude(this, distance);
    public INode Revolve(IAxis axis) => new Revolve(this, axis);
    public INode Revolve(IAxis axis, float angle) => new RevolveAngle(this, axis, angle);
}