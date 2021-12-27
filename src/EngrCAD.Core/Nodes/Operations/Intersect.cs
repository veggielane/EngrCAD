using System.Collections.Generic;
using System.Linq;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

public class Intersect : BaseNode
{
    public INode Node { get; }
    public List<INode> Others { get; }

    public Intersect(INode node, IEnumerable<INode> others)
    {
        Node = node;
        Others = new List<INode>(others);
    }

    public override ShapeWrapper Generate() => Node.Wrapper.Intersect(Others.Select(n => n.Wrapper).ToList());
}