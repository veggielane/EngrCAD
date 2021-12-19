using System.Numerics;

namespace EngrCAD.Core.Sketcher;

public class Plane : IPlane
{
    public Vector3 Origin { get; init; } = Vector3.Zero;
    public Vector3 Normal { get; init; } = Vector3.UnitZ;
    public Vector3 XDirection { get; init; }

    public static IPlane XY => new Plane
    {
        XDirection = Vector3.UnitX,
        Normal = Vector3.UnitZ,
    };
}