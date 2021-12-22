using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Primitives;

public class Cylinder : BaseNode
{
    public float Height { get; init; } = 1f;
    public float Radius { get; init; } = 1f;


    public bool Centered { get; init; } = true;

    public override ShapeWrapper Generate() => ShapeWrapper.Cylinder(Radius, Height, Centered);
}

public class Cone : BaseNode
{
    public float BottomRadius { get; init; } = 1f;

    public float TopRadius { get; init; } = 0f;

    public float Height { get; init; } = 1f;
        
    public bool Centered { get; init; } = true;

    public override ShapeWrapper Generate() => ShapeWrapper.Cone(BottomRadius, TopRadius, Height, Centered);
}