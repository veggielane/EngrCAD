using System.Numerics;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Transformations;

internal class Transform : Node
{
    public Matrix4x4 Matrix { get; set; } = Matrix4x4.Identity;

    public Node Node { get; init; }

    public override ShapeWrapper Generate()
    {
        return Node.Wrapper.Transform(Matrix);
    }
}