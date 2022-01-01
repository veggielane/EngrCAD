using System;
using System.Collections.Generic;
using System.Linq;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Nodes.Operations;
using EngrCAD.Core.Numerics;
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

    public Node Extrude(float distance) => new Extrude(this, distance);
    public Node Revolve(IAxis axis) => new Revolve(this, axis);
    public Node Revolve(IAxis axis, Angle angle) => new RevolveAngle(this, axis, angle);
    public Node Loft(params IClosedSketch[] sketches)
    {
        if (!sketches.Any()) throw new ArgumentException("Must contain at least 1", nameof(sketches));
        var list = new List<IClosedSketch> { this };
        list.AddRange(sketches);
        return new Loft(list);
    }

    public Node Loft(IEnumerable<IClosedSketch> sketches)
    {
        var closedSketches = sketches.ToList();
        if (!closedSketches.Any()) throw new ArgumentException("Must contain at least 1", nameof(sketches));
        var list = new List<IClosedSketch> { this };
        list.AddRange(closedSketches);
        return new Loft(list);
    }
}