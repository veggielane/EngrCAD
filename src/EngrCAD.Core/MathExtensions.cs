using System;
using System.Numerics;

namespace EngrCAD.Core;

public static class MathExtensions
{

    public static Vector3 Cross(this Vector3 v1, Vector3 v2) => Vector3.Cross(v1, v2);
    public static float Dot(this Vector3 v1, Vector3 v2) => Vector3.Dot(v1, v2);

    public static bool NearlyEquals(this double x, double y, double epsilon = 0.0000001)
    {
        return Math.Abs(x - y) <= Math.Abs(x * .00001);
    }
    public static bool NearlyEquals(this float x, float y, float epsilon = 0.0000001f)
    {
        return Math.Abs(x - y) <= Math.Abs(x * .00001);
    }

    public static bool NearlyLessThanOrEquals(this double x, double y, double epsilon = 0.0000001)
    {
        return x <= y || x.NearlyEquals(y, epsilon);
    }

    public static bool NearlyGreaterThanOrEquals(this double x, double y, double epsilon = 0.0000001)
    {
        return x >= y || x.NearlyEquals(y, epsilon);
    }

    public static bool NearlyLessThanOrEquals(this float x, float y, float epsilon = 0.0000001f)
    {
        return x <= y || x.NearlyEquals(y, epsilon);
    }

    public static bool NearlyGreaterThanOrEquals(this float x, float y, float epsilon = 0.0000001f)
    {
        return x >= y || x.NearlyEquals(y, epsilon);
    }
}