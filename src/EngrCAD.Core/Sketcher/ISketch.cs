using System.Collections.Generic;
using EngrCAD.Core.Sketcher.Edges;

namespace EngrCAD.Core.Sketcher;

public interface ISketch
{
    IPlane Plane { get; }
    List<ISketchEdge> Edges { get; }

    ISketch HorizontalLine(float distance);
    ISketch VerticalLine(float distance);
    IClosedSketch Close();
}