using System.Collections.Generic;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

public class Round : Node
{
    public Node Node { get; init; }
    public List<RadiusDefinition> Definitions { get; }
    public Round(Node node, RadiusDefinition definition)
    {
        Node = node;
        Definitions = new List<RadiusDefinition>(){definition};
    }
    public Round(Node node, IEnumerable<RadiusDefinition> definitions)
    {
        Node = node;
        Definitions = new List<RadiusDefinition>(definitions);
    }
    public override ShapeWrapper Generate() => Node.Wrapper.Round(Definitions);
}