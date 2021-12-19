using System.Numerics;

namespace EngrCAD.Core;

public static class MathExtensions
{

    public static Vector3 Cross(this Vector3 v1, Vector3 v2) => Vector3.Cross(v1, v2);
    public static float Dot(this Vector3 v1, Vector3 v2) => Vector3.Dot(v1, v2);
}