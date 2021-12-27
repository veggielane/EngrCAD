using System.Collections.Generic;
using System.Linq;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

public class Intersect : Node
{
    public Node Node { get; }
    public List<Node> Others { get; }

    public Intersect(Node node, IEnumerable<Node> others)
    {
        Node = node;
        Others = new List<Node>(others);
    }

    public override ShapeWrapper Generate() => Node.Wrapper.Intersect(Others.Select(n => n.Wrapper).ToList());
}