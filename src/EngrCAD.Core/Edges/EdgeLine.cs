using System.Numerics;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Edges;

public class EdgeLine : Edge
{
    internal EdgeLine(EdgeWrapper wrapper) : base(wrapper)
    {


    }
    public Vector3 Normal => (Vector3)Wrapper.Normal();

    public bool ParallelTo(Vector3 other)
    {
        return Vector3.Cross(Vector3.Abs(Normal), Vector3.Abs(other)) == Vector3.Zero;
    }
}