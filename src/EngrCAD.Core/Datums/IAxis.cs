using System.Numerics;

namespace EngrCAD.Core.Sketcher;

public interface IAxis
{
    Vector3 Position { get; init; }
    Vector3 Direction { get; init; }
}