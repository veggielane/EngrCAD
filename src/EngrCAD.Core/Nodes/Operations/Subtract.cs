using System.Collections.Generic;
using System.Linq;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

public class Subtract : BaseNode
{
    public List<INode> Children { get; init; } = new();
    public override ShapeWrapper Generate() => Children.First().Wrapper.Subtract(Children.Skip(1).Select(n => n.Wrapper).ToList());

    public Subtract()
    {

    }

    public Subtract(IEnumerable<INode> nodes)
    {
        Children = new List<INode>(nodes);
    }
}