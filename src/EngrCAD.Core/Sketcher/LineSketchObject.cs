using System.Numerics;

namespace EngrCAD.Core.Sketcher;

internal class LineSketchObject:ISketchObject
{
    public Vector3 Start { get; init; }
    public Vector3 End { get; init; }
}