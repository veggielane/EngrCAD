using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes
{
    public class Sphere:BaseNode
    {
        public float Radius { get; init; } = 1f;


        public override NativeWrapper Generate()
        {
            return NativeWrapper.Sphere(Radius);
        }
    }
}