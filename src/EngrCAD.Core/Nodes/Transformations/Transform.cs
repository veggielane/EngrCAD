using System.Numerics;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Transformations;

public class Transform : BaseNode
{
    public Matrix4x4 Matrix { get; set; } = Matrix4x4.Identity;

    public INode Child { get; init; }

    public override NativeWrapper Generate()
    {
        return Child.Wrapper.Transform(Matrix);
    }
}

public class Rotate : BaseNode
{
    public Vector3 Origin { get; set; } = Vector3.Zero;
    public Vector3 Direction { get; init; } = Vector3.UnitZ;

    public float Degrees
    {
        set => Radians = MathHelper.DegreesToRadians(value);
    }

    public float Radians { get; set; } = 0f;

    public INode Child { get; init; }

    public override NativeWrapper Generate()
    {
        return Child.Wrapper.Rotate(Radians, Origin, Direction);
    }
}