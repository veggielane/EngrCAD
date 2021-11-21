using System.Collections.Generic;
using System.Linq;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes
{
    public class Subtract : INodeWithChildren
    {
        public INode Child { get; init; }
        public NativeWrapper Generate()
        {
            return Children.First().Generate().Subtract(Children.Skip(1).First().Generate());
        }

        public List<INode> Children { get; init; }
    }
}