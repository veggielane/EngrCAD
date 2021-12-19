using System.Linq;
using System.Numerics;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Transformations;

public class Translate: BaseNode
{
    public Vector3 Position { get; init; } = Vector3.Zero;

    public INode Child { get; init; }

    public override NativeWrapper Generate()
    {
        return Child.Wrapper.Translate(Position.X, Position.Y, Position.Z);
    }
}