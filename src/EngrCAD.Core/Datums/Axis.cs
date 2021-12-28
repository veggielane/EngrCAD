using System.Numerics;

namespace EngrCAD.Core.Datums;

public class Axis : IAxis
{
    public Vector3 Origin { get; init; }
    public Vector3 Direction { get; init; }

    public static IAxis X => new Axis
    {
        Origin = Vector3.Zero,
        Direction = Vector3.UnitX
    };

    public static IAxis Y => new Axis
    {
        Origin = Vector3.Zero,
        Direction = Vector3.UnitY
    };

    public static IAxis Z => new Axis
    {
        Origin = Vector3.Zero,
        Direction = Vector3.UnitZ
    };
}