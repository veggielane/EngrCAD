using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EngrCAD.Core;

/// <summary>
/// Useful Maths Functions
/// </summary>
public static class MathHelper
{
    /// <summary>
    /// Calculates the Circumscribed Radius for a Regular Polygon
    /// </summary>
    /// <param name="radius"></param>
    /// <param name="fragmentResolution"></param>
    /// <returns></returns>
    public static float CircumscribedRadius(float radius, float fragmentResolution)
    {
        return (float)(radius * (1 / Math.Cos(DegreesToRadians(180f / fragmentResolution))));
    }

    /// <summary>
    /// Calculates the Middle Radius for a Regular Polygon
    /// </summary>
    /// <param name="radius"></param>
    /// <param name="fragmentResolution"></param>
    /// <returns></returns>
    public static float MidRadius(float radius, float fragmentResolution)
    {
        return (float)(radius * ((1 + 1 / Math.Cos(DegreesToRadians(180f / fragmentResolution))) / 2f));
    }

    /// <summary>
    /// Converts Radians to Degrees
    /// </summary>
    /// <param name="radians"></param>
    /// <returns></returns>
    public static float RadiansToDegrees(float radians)
    {
        return (float)((180 / Math.PI) * radians);
    }

    /// <summary>
    /// Converts Degrees to Radians
    /// </summary>
    /// <param name="degrees"></param>
    /// <returns></returns>
    public static float DegreesToRadians(float degrees)
    {
        return (float)((Math.PI / 180) * degrees);
    }
}