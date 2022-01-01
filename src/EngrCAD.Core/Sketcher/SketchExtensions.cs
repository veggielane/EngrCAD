using System.Collections.Generic;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Nodes.Operations;
using EngrCAD.Core.Numerics;

namespace EngrCAD.Core.Sketcher;

public static class SketchExtensions
{
    public static Node Extrude(this IClosedSketch sketch, float distance) => new Extrude(sketch, distance);

    public static Node Revolve(this IClosedSketch sketch, IAxis axis) => new Revolve(sketch, axis);
    public static Node Revolve(this IClosedSketch sketch, IAxis axis, Angle angle) => new RevolveAngle(sketch, axis, angle);

    public static Node Loft(this IClosedSketch sketch, params IClosedSketch[] sketches)
    {
        var list = new List<IClosedSketch> { sketch };
        list.AddRange(sketches);
        return new Loft(list);
    }
}