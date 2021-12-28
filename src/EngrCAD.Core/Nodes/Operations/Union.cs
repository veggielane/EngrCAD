using System.Collections.Generic;
using System.Linq;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

internal class Union : Node
{
    public Node Node { get; }
    public List<Node> Others { get; }

    public Union( IEnumerable<Node> nodes)
    {
        Node = nodes.First();
        Others = new List<Node>(nodes.Skip(1));
    }

    public Union(Node node, IEnumerable<Node> others)
    {
        Node = node;
        Others = new List<Node>(others);
    }

    public override ShapeWrapper Generate() => Node.Wrapper.Union(Others.Select(n => n.Wrapper).ToList());
}