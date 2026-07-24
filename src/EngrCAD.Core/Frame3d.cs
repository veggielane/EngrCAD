namespace EngrCAD.Core;

/// <summary>
/// Right-handed rigid coordinate frame: an <see cref="Origin"/> plus orthonormal axes
/// <see cref="X"/>, <see cref="Y"/>, <see cref="Z"/> with Z = X × Y. A frame is a rigid
/// map: <see cref="ToWorld(in Vector3d)"/> takes frame-local coordinates to world space
/// and <see cref="ToLocal(in Vector3d)"/> is its exact inverse (a rotation's inverse is
/// its transpose — no matrix solve, no drift beyond rounding).
/// </summary>
/// <remarks>
/// Factories orthonormalize their inputs through the central <see cref="Tolerance"/>
/// policy, so the axes are orthonormal to rounding (~1 ulp). Iterated composition
/// (<see cref="Then"/>) accumulates rounding drift; <see cref="Renormalized"/>
/// re-orthonormalizes. Exact equality (<c>==</c>, <see cref="Equals(Frame3d)"/>) is
/// bitwise per the Core convention; geometric equality goes through
/// <see cref="AreEqual"/>.
/// </remarks>
public readonly struct Frame3d : IEquatable<Frame3d>
{
    public Vector3d Origin { get; }
    public Vector3d X { get; }
    public Vector3d Y { get; }
    public Vector3d Z { get; }

    private Frame3d(in Vector3d origin, in Vector3d x, in Vector3d y, in Vector3d z)
    {
        Origin = origin;
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>The identity frame: world origin and world axes.</summary>
    public static readonly Frame3d WorldXY = new(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, Vector3d.UnitZ);

    /// <summary>
    /// Frame from an origin and two in-plane axis hints: X is <paramref name="xAxis"/>
    /// normalized, Y is <paramref name="yAxis"/> Gram–Schmidt-orthogonalized against X,
    /// Z = X × Y. Throws when either axis is zero-length or the axes are parallel.
    /// </summary>
    public static Frame3d FromXY(in Vector3d origin, in Vector3d xAxis, in Vector3d yAxis) =>
        FromXY(origin, xAxis, yAxis, Tolerance.Default);

    /// <inheritdoc cref="FromXY(in Vector3d, in Vector3d, in Vector3d)"/>
    public static Frame3d FromXY(in Vector3d origin, in Vector3d xAxis, in Vector3d yAxis, in Tolerance tolerance)
    {
        if (!xAxis.TryNormalize(tolerance, out var x))
            throw new ArgumentException("The x axis is zero-length; a frame needs a nonzero x axis.", nameof(xAxis));
        if (!(yAxis - x * yAxis.Dot(x)).TryNormalize(tolerance, out var y))
            throw new ArgumentException(
                "The y axis is parallel to the x axis (or zero-length); the axes must span a plane.", nameof(yAxis));
        return new Frame3d(origin, x, y, x.Cross(y));
    }

    /// <summary>
    /// Frame from an origin and a Z axis (plane normal / rotation axis), with X chosen
    /// deterministically as <see cref="Vector3d.ArbitraryPerpendicular"/> of the
    /// normalized Z and Y = Z × X.
    /// </summary>
    /// <remarks>
    /// The perpendicular choice deliberately replicates the codebase-wide
    /// <see cref="Vector3d.ArbitraryPerpendicular"/> convention (cross Z with the
    /// canonical axis it is least aligned with) — the same rule used for axis frames in
    /// <c>SolidFactory.MakeTorus</c>, <c>StepReader.Axis2</c>'s defaulted X, and the
    /// plane/sphere intersection circles. Two sites deriving frames for the same axis
    /// must agree bit-for-bit or welded tessellation cracks (see the phase-alignment
    /// lesson in CLAUDE.md). Note that plane⊥cylinder intersection circles do NOT use an
    /// arbitrary perpendicular at all — they phase-align to the cylinder's own frame;
    /// <see cref="FromNormal(in Vector3d, in Vector3d)"/> is for when no natural frame
    /// exists.
    /// </remarks>
    public static Frame3d FromNormal(in Vector3d origin, in Vector3d normal) =>
        FromNormal(origin, normal, Tolerance.Default);

    /// <inheritdoc cref="FromNormal(in Vector3d, in Vector3d)"/>
    public static Frame3d FromNormal(in Vector3d origin, in Vector3d normal, in Tolerance tolerance)
    {
        var z = normal.Normalized(tolerance);
        var x = z.ArbitraryPerpendicular(tolerance);
        return new Frame3d(origin, x, z.Cross(x), z);
    }

    /// <summary>
    /// Frame from an origin, a Z axis, and an X hint (the STEP AXIS2_PLACEMENT_3D
    /// convention): Z is <paramref name="zAxis"/> normalized, X is
    /// <paramref name="xHint"/> Gram–Schmidt-orthogonalized against Z, Y = Z × X.
    /// Throws when an axis is zero-length or the hint is parallel to Z.
    /// </summary>
    public static Frame3d FromZX(in Vector3d origin, in Vector3d zAxis, in Vector3d xHint) =>
        FromZX(origin, zAxis, xHint, Tolerance.Default);

    /// <inheritdoc cref="FromZX(in Vector3d, in Vector3d, in Vector3d)"/>
    public static Frame3d FromZX(in Vector3d origin, in Vector3d zAxis, in Vector3d xHint, in Tolerance tolerance)
    {
        if (!zAxis.TryNormalize(tolerance, out var z))
            throw new ArgumentException("The z axis is zero-length; a frame needs a nonzero z axis.", nameof(zAxis));
        if (!(xHint - z * z.Dot(xHint)).TryNormalize(tolerance, out var x))
            throw new ArgumentException(
                "The x hint is parallel to the z axis (or zero-length); it must have an in-plane component.", nameof(xHint));
        return new Frame3d(origin, x, z.Cross(x), z);
    }

    /// <summary>
    /// Frame from axes that are <b>already</b> orthonormal: X and Y are stored exactly
    /// as given (no Gram–Schmidt — callers whose axes must weld bit-for-bit with
    /// geometry built elsewhere cannot tolerate re-derivation), Z = X × Y. Validates
    /// (without modifying) that the axes are unit and perpendicular within
    /// <paramref name="tolerance"/> and throws otherwise.
    /// </summary>
    public static Frame3d FromOrthonormal(in Vector3d origin, in Vector3d xAxis, in Vector3d yAxis) =>
        FromOrthonormal(origin, xAxis, yAxis, Tolerance.Default);

    /// <inheritdoc cref="FromOrthonormal(in Vector3d, in Vector3d, in Vector3d)"/>
    public static Frame3d FromOrthonormal(in Vector3d origin, in Vector3d xAxis, in Vector3d yAxis, in Tolerance tolerance)
    {
        if (!tolerance.AreEqual(xAxis.Length, 1) || !tolerance.AreEqual(yAxis.Length, 1) ||
            !tolerance.IsZero(xAxis.Dot(yAxis)))
            throw new ArgumentException(
                "FromOrthonormal requires unit, mutually perpendicular axes; use FromXY to orthonormalize.");
        return new Frame3d(origin, xAxis, yAxis, xAxis.Cross(yAxis));
    }

    // ---- point / vector / ray maps ----

    /// <summary>Maps frame-local coordinates to world space: Origin + X·p.X + Y·p.Y + Z·p.Z.</summary>
    public Vector3d ToWorld(in Vector3d point) =>
        Origin + X * point.X + Y * point.Y + Z * point.Z;

    /// <summary>Maps a world point to frame-local coordinates (exact inverse of <see cref="ToWorld(in Vector3d)"/>).</summary>
    public Vector3d ToLocal(in Vector3d point)
    {
        var d = point - Origin;
        return new Vector3d(d.Dot(X), d.Dot(Y), d.Dot(Z));
    }

    /// <summary>Rotates a frame-local direction into world space (no translation).</summary>
    public Vector3d ToWorldVector(in Vector3d vector) =>
        X * vector.X + Y * vector.Y + Z * vector.Z;

    /// <summary>Rotates a world direction into frame-local coordinates (no translation).</summary>
    public Vector3d ToLocalVector(in Vector3d vector) =>
        new(vector.Dot(X), vector.Dot(Y), vector.Dot(Z));

    /// <summary>Maps a frame-local ray to world space (origin as point, direction as vector).</summary>
    public Ray3d ToWorld(in Ray3d ray) => new(ToWorld(ray.Origin), ToWorldVector(ray.Direction));

    /// <summary>Maps a world ray to frame-local coordinates (origin as point, direction as vector).</summary>
    public Ray3d ToLocal(in Ray3d ray) => new(ToLocal(ray.Origin), ToLocalVector(ray.Direction));

    // ---- composition ----

    /// <summary>
    /// Composes rigid maps: the resulting frame's <see cref="ToWorld(in Vector3d)"/>
    /// applies this frame first, then <paramref name="outer"/> —
    /// <c>a.Then(b).ToWorld(p) == b.ToWorld(a.ToWorld(p))</c>.
    /// </summary>
    public Frame3d Then(in Frame3d outer) => new(
        outer.ToWorld(Origin),
        outer.ToWorldVector(X),
        outer.ToWorldVector(Y),
        outer.ToWorldVector(Z));

    /// <summary>
    /// The inverse rigid map: <c>Inverse().ToWorld == ToLocal</c>. The rotation inverse
    /// is the exact transpose; the origin is <c>ToLocal(world origin)</c>.
    /// </summary>
    public Frame3d Inverse() => new(
        new Vector3d(-Origin.Dot(X), -Origin.Dot(Y), -Origin.Dot(Z)),
        new Vector3d(X.X, Y.X, Z.X),
        new Vector3d(X.Y, Y.Y, Z.Y),
        new Vector3d(X.Z, Y.Z, Z.Z));

    /// <summary>
    /// The frame as a <see cref="Matrix4d"/> (column-vector convention: the axes are the
    /// rotation columns, the origin the translation column), so
    /// <c>ToMatrix().TransformPoint(p) == ToWorld(p)</c>.
    /// </summary>
    public Matrix4d ToMatrix() => new(
        X.X, Y.X, Z.X, Origin.X,
        X.Y, Y.Y, Z.Y, Origin.Y,
        X.Z, Y.Z, Z.Z, Origin.Z,
        0, 0, 0, 1);

    /// <summary>
    /// Re-orthonormalizes the axes (Gram–Schmidt from X, Z rebuilt as X × Y) to undo the
    /// rounding drift of iterated composition. The origin is unchanged.
    /// </summary>
    public Frame3d Renormalized() => Renormalized(Tolerance.Default);

    /// <inheritdoc cref="Renormalized()"/>
    public Frame3d Renormalized(in Tolerance tolerance) => FromXY(Origin, X, Y, tolerance);

    /// <summary>Geometric equality: origin and all axes within <paramref name="tolerance"/>.</summary>
    public bool AreEqual(in Frame3d other, in Tolerance tolerance) =>
        Origin.AreEqual(other.Origin, tolerance) &&
        X.AreEqual(other.X, tolerance) &&
        Y.AreEqual(other.Y, tolerance) &&
        Z.AreEqual(other.Z, tolerance);

    public bool Equals(Frame3d other) =>
        Origin.Equals(other.Origin) && X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

    public override bool Equals(object? obj) => obj is Frame3d f && Equals(f);
    public override int GetHashCode() => HashCode.Combine(Origin, X, Y, Z);
    public static bool operator ==(in Frame3d a, in Frame3d b) => a.Equals(b);
    public static bool operator !=(in Frame3d a, in Frame3d b) => !a.Equals(b);

    public override string ToString() => $"O={Origin} X={X} Y={Y} Z={Z}";
}
