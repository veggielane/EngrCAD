using System;
using System.Collections.Generic;
using System.Numerics;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Numerics;
using EngrCAD.Core.Sketcher.Edges;

namespace EngrCAD.Core.Sketcher;

public interface IClosedSketch
{
    public ICSYS CSYS { get; }
    public IReadOnlyList<ISketchEdge> Edges { get; }


    Node Extrude(float distance);
    Node Revolve(IAxis axis);
    Node Revolve(IAxis axis, Angle angle);
    Node Loft(params IClosedSketch[] sketches);
    Node Loft(IEnumerable<IClosedSketch> sketches);
}