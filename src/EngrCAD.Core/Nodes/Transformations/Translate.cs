using System.Numerics;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Transformations;

public class Translate: Node
{
    public Vector3 Position { get; init; } = Vector3.Zero;

    public Node Child { get; init; }

    public override ShapeWrapper Generate()
    {
        return Child.Wrapper.Translate(Position.X, Position.Y, Position.Z);
    }
}