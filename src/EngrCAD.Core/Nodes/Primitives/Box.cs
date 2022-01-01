using System.Numerics;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Primitives;

public class Box : Node
{
    public Vector3 Size { get; init; } = Vector3.Zero;

    public bool Centered { get; init; } = true;

    public override ShapeWrapper Generate() => ShapeWrapper.Box(Size.X, Size.Y, Size.Z, Centered);
}