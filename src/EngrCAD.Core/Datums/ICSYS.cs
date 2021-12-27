using System.Numerics;

namespace EngrCAD.Core.Datums;

public interface ICSYS
{
    Vector3 Origin { get; init; }
    Vector3 XDirection { get; init; }
    Vector3 YDirection { get; }
    Vector3 ZDirection { get; }
    Vector3 Normal { get; init; }
}