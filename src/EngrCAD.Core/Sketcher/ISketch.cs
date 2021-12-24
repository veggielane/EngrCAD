using System.Collections.Generic;
using System.Numerics;
using EngrCAD.Core.Sketcher.Edges;

namespace EngrCAD.Core.Sketcher;

public interface ISketch
{
    IPlane Plane { get; }
    List<ISketchEdge> Edges { get; }

    ISketch MoveTo(Vector2 v);
    ISketch MoveTo(float x, float y);

    ISketch HorizontalLine(float distance);
    ISketch VerticalLine(float distance);
    IClosedSketch Close();
    ISketch LineTo(Vector2 v);
    ISketch LineTo(float x, float y);

    ISketch Line(Vector2 v);
    ISketch Line(float x, float y);

    ISketch BezierCurveTo(Vector2 end, params Vector2[] controlPoints);
}