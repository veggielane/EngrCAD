using System;
using System.Numerics;

namespace EngrCAD.Core.Datums;

public class AABB : IAABB
{
    public Vector3 Min { get; }
    public Vector3 Max { get; }

    public Vector3 Size { get; }
    public Vector3 Center { get; }

    public AABB(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
        Center = (Min + Max) * 0.5f;
        Size = Max - Min;
    }

    internal AABB(Tuple<Vector3, Vector3> tuple):this(tuple.Item1, tuple.Item2)
    {

    }

    public bool Contains(Vector3 point)
    {
        throw new NotImplementedException();
    }

    public bool Contains(Vector3 point, bool boundaryInclusive)
    {
        throw new NotImplementedException();
    }

    public bool Contains(IAABB other)
    {
        throw new NotImplementedException();
    }

    public float DistanceToNearestEdge(Vector3 point)
    {
        throw new NotImplementedException();
    }

    public void Translate(Vector3 distance)
    {
        throw new NotImplementedException();
    }

    public void Scale(Vector3 scale, Vector3 anchor)
    {
        throw new NotImplementedException();
    }

    public void Inflate(Vector3 toPoint)
    {
        throw new NotImplementedException();
    }

    bool IEquatable<IAABB>.Equals(IAABB other)
    {
        throw new NotImplementedException();
    }
}