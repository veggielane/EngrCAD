using System.Numerics;

namespace EngrCAD.Core.Datums;

public class CSYS : ICSYS
{
    public Vector3 Origin { get; init; } = Vector3.Zero;
    public Vector3 XDirection { get; init; } = Vector3.UnitX;
    public Vector3 YDirection => Vector3.Cross(Normal, XDirection);
    public Vector3 ZDirection => Normal;
    public Vector3 Normal { get; init; } = Vector3.UnitZ;


    public ICSYS Translate(Vector3 position)
    {
        return new CSYS
        {
            Origin = Origin + position,
            Normal = Normal,
            XDirection = XDirection,
        };
    }
    public static ICSYS XY => new CSYS
    {
        Origin = Vector3.Zero,
        Normal = Vector3.UnitZ,
        XDirection = Vector3.UnitX,
    };


}