using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Circular helix about the Z axis of <see cref="Frame"/>:
/// P(t) = O + X·r·cos t + Y·r·sin t + Z·(p·t/2π), where r = <see cref="Radius"/> and
/// p = <see cref="Pitch"/> (axial advance per turn). The parameter t is the turning
/// angle; the domain is [0, 2π·<see cref="Turns"/>], so the curve starts on the frame's
/// X axis at the frame origin's plane and advances along +Z. A positive pitch gives a
/// right-hand helix; a negative pitch advances along −Z (mirror the frame's Z for a
/// left-hand helix about the same axis). The parameterization has constant speed
/// √(r² + (p/2π)²), so the exact arc length over n turns is the closed form
/// L = Δt·√(r² + (p/2π)²) = turns·√((2πr)² + p²).
/// All derivatives are analytic overrides — never finite differences (helical sweep
/// frames are weld-sensitive to tangent error, per the codebase rule).
/// </summary>
public sealed class Helix3d : Curve3d
{
    public Helix3d(in Frame3d frame, double radius, double pitch, double turns)
    {
        if (!(radius > 0))
            throw new ArgumentOutOfRangeException(nameof(radius), "Helix radius must be positive.");
        if (pitch == 0 || !double.IsFinite(pitch))
            throw new ArgumentOutOfRangeException(nameof(pitch),
                "Helix pitch must be finite and nonzero (a zero pitch is a circle — use Circle3d).");
        if (!(turns > 0) || !double.IsFinite(turns))
            throw new ArgumentOutOfRangeException(nameof(turns), "Helix turns must be positive and finite.");
        Frame = frame;
        Radius = radius;
        Pitch = pitch;
        Turns = turns;
    }

    /// <summary>Helix about the axis through <paramref name="axisOrigin"/> along
    /// <paramref name="axisDirection"/>, using the codebase's
    /// <see cref="Frame3d.FromNormal(in Vector3d, in Vector3d)"/> convention for the
    /// start (t = 0) direction.</summary>
    public Helix3d(in Vector3d axisOrigin, in Vector3d axisDirection, double radius, double pitch, double turns)
        : this(Frame3d.FromNormal(axisOrigin, axisDirection), radius, pitch, turns) { }

    /// <summary>The axis frame: the helix winds about its Z axis, starting on its X axis.</summary>
    public Frame3d Frame { get; }

    /// <summary>Distance from the axis (constant).</summary>
    public double Radius { get; }

    /// <summary>Axial advance per full turn; sign selects the advance direction along frame Z.</summary>
    public double Pitch { get; }

    /// <summary>Number of turns; the domain is [0, 2π·Turns].</summary>
    public double Turns { get; }

    public override Interval Domain => new(0, 2 * Math.PI * Turns);
    public override bool IsClosed => false;

    private double AxialRate => Pitch / (2 * Math.PI);

    public override Vector3d PointAt(double t) =>
        Frame.Origin
        + Frame.X * (Radius * Math.Cos(t))
        + Frame.Y * (Radius * Math.Sin(t))
        + Frame.Z * (AxialRate * t);

    public override Vector3d DerivativeAt(double t) =>
        Frame.X * (-Radius * Math.Sin(t))
        + Frame.Y * (Radius * Math.Cos(t))
        + Frame.Z * AxialRate;

    /// <summary>Exactly the in-plane radial acceleration: −(P(t) − axis point) projected
    /// to the plane — the axial component is linear in t and vanishes.</summary>
    public override Vector3d SecondDerivativeAt(double t) =>
        Frame.X * (-Radius * Math.Cos(t))
        + Frame.Y * (-Radius * Math.Sin(t));

    public override Vector3d TangentAt(double t) => DerivativeAt(t).Normalized();

    /// <summary>Exact arc length over <see cref="Domain"/>: the speed is the constant
    /// √(r² + (p/2π)²), so L = 2π·turns·√(r² + (p/2π)²) = turns·√((2πr)² + p²).</summary>
    public double Length() => Domain.Length * Math.Sqrt(Radius * Radius + AxialRate * AxialRate);

    /// <summary>Lead angle λ = atan(|p| / 2πr) — the angle between the tangent and the
    /// plane perpendicular to the axis (constant along the helix).</summary>
    public double LeadAngle => Math.Atan2(Math.Abs(Pitch), 2 * Math.PI * Radius);
}
