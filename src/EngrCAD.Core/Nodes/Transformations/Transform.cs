using System.Numerics;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Transformations;

public class Transform : BaseNode
{
    public Matrix4x4 Matrix { get; set; } = Matrix4x4.Identity;

    public INode Child { get; init; }

    public override ShapeWrapper Generate()
    {
        return Child.Wrapper.Transform(Matrix);
    }
}