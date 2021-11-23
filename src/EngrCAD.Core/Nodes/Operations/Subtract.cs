using System.Linq;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations
{
    public class Subtract : BaseNode, INodeWithVolume
    {
        public override NativeWrapper Generate()
        {
            return Children.First().Wrapper.Subtract(Children.Skip(1).First().Wrapper);
        }

        public float CalculateVolume() => Wrapper.CalculateVolume();
    }
}