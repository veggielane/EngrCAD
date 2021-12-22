using System.Collections.Generic;
using System.Linq;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

public class Intersect : BaseNode
{
    public List<INode> Children { get; init; } = new();
    public override ShapeWrapper Generate() => Children.First().Wrapper.Intersect(Children.Skip(1).First().Wrapper);
}