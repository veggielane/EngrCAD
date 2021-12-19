using System.Collections.Generic;

namespace EngrCAD.Core.Sketcher;

public interface ISketch
{
    IPlane Plane { get; }
    List<ISketchObject> Objects { get; }

    ISketch HorizontalLine(float distance);
    ISketch VerticalLine(float distance);
    IClosedSketch Close();
}