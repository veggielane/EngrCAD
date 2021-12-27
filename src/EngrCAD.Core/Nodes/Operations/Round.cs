using System.Collections.Generic;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

public class Round : BaseNode
{
    public INode Node { get; init; }
    public List<RadiusDefinition> Definitions = new();
    public Round(INode node, RadiusDefinition definition)
    {
        Node = node;
        Definitions = new List<RadiusDefinition>(){definition};
    }
    public Round(INode node, IEnumerable<RadiusDefinition> definitions)
    {
        Node = node;
        Definitions = new List<RadiusDefinition>(definitions);
    }
    public override ShapeWrapper Generate() => Node.Wrapper.Round(Definitions);
}