using System.Drawing;
using EngrCAD.Core.Nodes;

namespace EngrCAD.Core;

public class Body
{
    public INode Node { get; init; }
    public Color Color { get; init; } = Color.DimGray;

}