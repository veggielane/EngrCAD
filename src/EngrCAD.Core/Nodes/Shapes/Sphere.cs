using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Shapes
{
    public class Sphere:BaseNode, INodeWithVolume
    {
        public float Radius { get; init; } = 1f;
        
        public override NativeWrapper Generate() => NativeWrapper.Sphere(Radius);
        public float CalculateVolume() => Wrapper.CalculateVolume();
    }
}