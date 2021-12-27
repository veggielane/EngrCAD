﻿using System.Linq;
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

    public override ShapeWrapper Generate() => ShapeWrapper.Revolve(Sketch.Edges.Select(o => o.ToEdge()).ToList(), Axis.Origin, Axis.Direction);
}