using System.Numerics;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Sketcher.Edges;

public class SketchEdgeCircle : ISketchEdge
{
    public float Radius { get; init; } = 1;
    public Vector3 Center { get; init; } = Vector3.Zero;
    public Vector3 Direction { get; init; } = Vector3.UnitZ;

    public EdgeWrapper ToEdge()
    {
        return EdgeWrapper.Circle(Radius, Center,Direction);
    }
}