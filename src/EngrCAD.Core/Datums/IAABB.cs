using System;
using System.Numerics;

namespace EngrCAD.Core.Datums;

public interface IAABB : IEquatable<IAABB>
{
    public Vector3 Min { get; }
    public Vector3 Max { get; }
    public Vector3 Size { get; }
    public Vector3 Center { get; }


    public bool Contains(Vector3 point);
    public bool Contains(Vector3 point, bool boundaryInclusive);
    public bool Contains(IAABB other);
    public float DistanceToNearestEdge(Vector3 point);
    public void Translate(Vector3 distance);

    public void Scale(Vector3 scale, Vector3 anchor);

    public void Inflate(Vector3 toPoint);

    public bool Equals(IAABB other);
    public bool Equals(object obj);

}