using System.Collections.Generic;
using System.Linq;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

public class Union : BaseNode
{
    public List<INode> Children { get; init; } = new();
    public override NativeWrapper Generate() => Children.First().Wrapper.Union(Children.Skip(1).First().Wrapper);
}