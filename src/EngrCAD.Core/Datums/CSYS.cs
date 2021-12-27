using System.Numerics;

namespace EngrCAD.Core.Datums;

public class CSYS : ICSYS
{
    public Vector3 Origin { get; init; } = Vector3.Zero;
    public Vector3 XDirection { get; init; } = Vector3.UnitX;
    public Vector3 YDirection => Vector3.Cross(Normal, XDirection);
    public Vector3 Normal { get; init; } = Vector3.UnitZ;

    public static ICSYS XY => new CSYS
    {
        XDirection = Vector3.UnitX,
        Normal = Vector3.UnitZ,
    };
}