using System.Collections.Generic;
using System.Linq;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

public class Union : Node
{
    public INode Node { get; }
    public List<INode> Others { get; }

    public Union( IEnumerable<INode> nodes)
    {
        Node = nodes.First();
        Others = new List<INode>(nodes.Skip(1));
    }

    public Union(INode node, IEnumerable<INode> others)
    {
        Node = node;
        Others = new List<INode>(others);
    }

    public override ShapeWrapper Generate() => Node.Wrapper.Union(Others.Select(n => n.Wrapper).ToList());
}