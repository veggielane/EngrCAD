using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Primitives
{
    public class Cylinder : BaseNode
    {
        public float Height { get; init; } = 1f;
        public float Radius { get; init; } = 1f;


        public bool Centered { get; init; } = true;

        public override NativeWrapper Generate() => NativeWrapper.Cylinder(Radius, Height, Centered);
    }
}