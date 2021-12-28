using System.Numerics;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Transformations;

public class Translate: Node
{
    public Node Node { get; init; }
    public Vector3 Position { get; init; } = Vector3.Zero;

    public Translate()
    {

    }

    public Translate(Node node, Vector3 position)
    {
        Node = node;
        Position = position;
    }

    public override ShapeWrapper Generate()
    {
        return Node.Wrapper.Translate(Position.X, Position.Y, Position.Z);
    }
}