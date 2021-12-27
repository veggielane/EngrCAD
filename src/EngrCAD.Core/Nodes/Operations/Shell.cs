using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

public class Shell : Node
{
    public Node Child { get; init; }
    public float Thickness { get; init; } = 0.5f;
    public override ShapeWrapper Generate() => Child.Wrapper.Shell(-1.0*Thickness);
}