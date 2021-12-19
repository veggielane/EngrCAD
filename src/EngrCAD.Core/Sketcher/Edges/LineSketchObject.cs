using System.Numerics;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Sketcher.Edges;

internal class LineSketchObject: ISketchEdge
{
    public Vector3 Start { get; init; }
    public Vector3 End { get; init; }
    public EdgeWrapper ToEdge()
    {
        return EdgeWrapper.Line(Start, End);
    }
}