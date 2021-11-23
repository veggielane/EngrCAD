using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Shapes
{
    public class Sphere:BaseNode
    {
        public float Radius { get; init; } = 1f;
        
        public override NativeWrapper Generate() => NativeWrapper.Sphere(Radius);
    }
}