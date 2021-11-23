using System.Collections.Generic;
using System.Linq;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes
{
    public class Subtract : BaseNode
    {
        public override NativeWrapper Generate()
        {
            return Children.First().Generate().Subtract(Children.Skip(1).First().Generate());
        }
    }
}