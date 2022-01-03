using System.Numerics;
using EngrCAD.Core.Datums;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Primitives;

public class Text : Node
{
    public string Line { get; init; } = "EngrCAD";
    public float Height { get; init; } = 10f;
    public float Thickness { get; init; } = 1f;

    public HorizontalTextAlignment HorizontalAlignment { get; init; } = HorizontalTextAlignment.Center;
    public VerticalTextAlignment VerticalAlignment { get; init; } = VerticalTextAlignment.Center;

    public override ShapeWrapper Generate()
    {
        return ShapeWrapper.Text(Line, Height,new Vector3(0,0,Thickness), (int)HorizontalAlignment, (int)VerticalAlignment);
    }
}
public enum HorizontalTextAlignment
{
    Left,
    Center,
    Right
};
public enum VerticalTextAlignment
{
    Bottom,
    Center,
    Top
};