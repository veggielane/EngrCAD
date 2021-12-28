using System.Collections.Generic;
using System.Linq;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

internal class Subtract : Node
{
    public Node Node { get; }
    public List<Node> Tools { get; }

    public Subtract(Node node, IEnumerable<Node> tools)
    {
        Node = node;
        Tools = new List<Node>(tools);
    }
    public override ShapeWrapper Generate() => Node.Wrapper.Subtract(Tools.Select(n => n.Wrapper).ToList());
}