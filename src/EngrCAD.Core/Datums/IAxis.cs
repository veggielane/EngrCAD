using System.Numerics;

namespace EngrCAD.Core.Datums;

public interface IAxis
{
    Vector3 Origin { get; init; }
    Vector3 Direction { get; init; }
}