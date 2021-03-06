using System.Numerics;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Sketcher.Edges;

public class SketchEdgeLine: ISketchEdge
{
    public Vector3 Start { get; init; }
    public Vector3 End { get; init; }
    public EdgeWrapper ToEdge()
    {
        return EdgeWrapper.Line(Start, End);
    }
}