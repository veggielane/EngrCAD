namespace EngrCAD.Core;

/// <summary>
/// A ray with origin and direction. Direction need not be unit length for containment
/// and AABB tests, but reported t-parameters are in units of the direction's length.
/// </summary>
public readonly struct Ray3d
{
    public Vector3d Origin { get; }
    public Vector3d Direction { get; }

    public Ray3d(in Vector3d origin, in Vector3d direction)
    {
        Origin = origin;
        Direction = direction;
    }

    public Vector3d PointAt(double t) => Origin + Direction * t;

    public bool Intersects(in Aabb box) => Intersects(box, out _, out _);

    /// <summary>
    /// Slab test. On hit, [<paramref name="tMin"/>, <paramref name="tMax"/>] is the parameter
    /// interval inside the box, clamped so <paramref name="tMin"/> ≥ 0 (ray, not line).
    /// A zero direction component is handled by IEEE infinities.
    /// </summary>
    public bool Intersects(in Aabb box, out double tMin, out double tMax)
    {
        tMin = 0;
        tMax = double.PositiveInfinity;

        for (int axis = 0; axis < 3; axis++)
        {
            double origin = Origin[axis];
            double direction = Direction[axis];
            double min = box.Min[axis];
            double max = box.Max[axis];

            // Deliberate exact-zero test (not a tolerance decision): only an exactly-zero
            // component makes (min - origin) * (1 / direction) produce 0 * inf = NaN;
            // any nonzero direction, however tiny, yields correct IEEE-infinite slabs.
            if (direction == 0)
            {
                // Parallel to the slab: miss unless origin lies within it.
                if (origin < min || origin > max)
                    return false;
                continue;
            }

            double inv = 1.0 / direction;
            double t0 = (min - origin) * inv;
            double t1 = (max - origin) * inv;
            if (t0 > t1)
                (t0, t1) = (t1, t0);

            tMin = Math.Max(tMin, t0);
            tMax = Math.Min(tMax, t1);
            if (tMin > tMax)
                return false;
        }

        return true;
    }

    public override string ToString() => $"{Origin} + t·{Direction}";
}
