using System.Numerics;
using EngrCAD.Core.Numerics;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Transformations;

internal class Rotate : Node
{
    public Node Node { get; }

    public Vector3 Origin { get; }
    public Vector3 Direction { get; }
    public Angle Angle { get; }

    public Rotate(Node node, Vector3 direction, Angle angle)
    {
        Node = node;
        Origin = Vector3.Zero;
        Direction = direction;
        Angle = angle;
    }

    public Rotate(Node node, Vector3 origin, Vector3 direction, Angle angle)
    {
        Node = node;
        Origin = origin;
        Direction = direction;
        Angle = angle;
    }

    public override ShapeWrapper Generate()
    {
        return Node.Wrapper.Rotate(Angle.Radians, Origin, Direction);
    }
}