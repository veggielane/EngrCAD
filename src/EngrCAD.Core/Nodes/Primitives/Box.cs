using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Primitives
{
    public class Box : BaseNode
    {
        public float X { get; init; } = 1f;
        public float Y { get; init; } = 1f;
        public float Z { get; init; } = 1f;

        public bool Centered { get; init; } = true;

        public override NativeWrapper Generate() => NativeWrapper.Box(X, Y, Z, Centered);
    }
}