using System.Collections.Generic;
using System.Linq;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

public class Subtract : BaseNode
{
    public INode Node { get; }
    public List<INode> Tools { get; }

    public Subtract(INode node, IEnumerable<INode> tools)
    {
        Node = node;
        Tools = new List<INode>(tools);
    }
    public override ShapeWrapper Generate() => Node.Wrapper.Subtract(Tools.Select(n => n.Wrapper).ToList());
}