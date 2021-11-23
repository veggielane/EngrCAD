using System.Linq;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations
{
    public class Subtract : BaseNode
    {
        public override NativeWrapper Generate()
        {
            return Children.First().Wrapper.Subtract(Children.Skip(1).First().Wrapper);
        }
    }
}