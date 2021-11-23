using System.Linq;
using System.Numerics;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Transformations
{
    public class Translate: BaseNode, INodeWithVolume
    {
        public Vector3 Position { get; init; } = Vector3.Zero;

        public override NativeWrapper Generate()
        {
            return Children.First().Wrapper.Translate(Position.X, Position.Y, Position.Z);
        }

        public float CalculateVolume() => Wrapper.CalculateVolume();
    }
}
