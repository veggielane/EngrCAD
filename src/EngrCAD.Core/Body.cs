using System.Drawing;
using EngrCAD.Core.Nodes;

namespace EngrCAD.Core;

public class Body
{
    public Node Node { get; init; }
    public Color Color { get; init; } = Color.DimGray;

}