using System.Numerics;

namespace EngrCAD.Core.Datums;

public interface ICSYS
{
    Vector3 Origin { get; init; }
    Vector3 XDirection { get; init; }
    Vector3 YDirection { get; init; }
    Vector3 ZDirection { get; init; }
}