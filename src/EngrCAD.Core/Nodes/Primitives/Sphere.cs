using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Primitives;

public class Sphere:Node
{
    public float Radius { get; init; } = 1f;
        
    public override ShapeWrapper Generate() => ShapeWrapper.Sphere(Radius);
}