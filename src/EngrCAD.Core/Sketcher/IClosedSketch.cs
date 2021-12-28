using System.Collections.Generic;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Nodes.Operations;
using EngrCAD.Core.Numerics;
using EngrCAD.Core.Sketcher.Edges;

namespace EngrCAD.Core.Sketcher;

public interface IClosedSketch
{
    public ICSYS CSYS { get; }
    public IReadOnlyList<ISketchEdge> Edges { get; }

    public Node Extrude(float distance) => new Extrude(this, distance);
    public Node Revolve(IAxis axis) => new Revolve(this, axis);
    public Node Revolve(IAxis axis, Angle angle) => new RevolveAngle(this, axis, angle);
}