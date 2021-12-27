using System.Numerics;

namespace EngrCAD.Core.Sketcher;

public interface IPlane
{
    Vector3 Origin { get; init; }
    Vector3 Normal { get; init; }
    Vector3 XDirection { get; init; }
}
