using System.Collections.Generic;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

internal class Chamfer : Node
{
    public Node Node { get; init; }
    public List<ChamferDefinition> Definitions { get; }
    public Chamfer(Node node, ChamferDefinition definition)
    {
        Node = node;
        Definitions = new List<ChamferDefinition>() { definition };
    }
    public Chamfer(Node node, IEnumerable<ChamferDefinition> definitions)
    {
        Node = node;
        Definitions = new List<ChamferDefinition>(definitions);
    }
    public override ShapeWrapper Generate() => Node.Wrapper.Chamfer(Definitions);
}