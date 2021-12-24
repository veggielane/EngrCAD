using System;
using System.Collections.Generic;
using System.Numerics;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Sketcher.Edges;

internal class SketchEdgeBezierCurve : ISketchEdge
{
    public Vector3 Start { get; init; }
    public Vector3 End { get; init; }

    public List<Vector3> ControlPoints { get; init; }
    public EdgeWrapper ToEdge()
    {
        var points = new List<Vector3>();
        points.Add(Start);
        points.AddRange(ControlPoints);
        points.Add(End);
        return EdgeWrapper.BezierCurve(points);
    }
}