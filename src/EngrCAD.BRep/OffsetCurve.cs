using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Planar offset of a base curve as first-class geometry:
/// O(t) = C(t) + d · (n̂ × T̂(t)), where C is the base curve, n̂ the (unit) plane
/// normal, T̂ the base unit tangent, and d = <see cref="Distance"/> the signed offset.
/// Positive d offsets to the LEFT of the traversal direction as seen from the +n̂ side
/// (for a CCW circle whose axis is n̂, positive d offsets inward: radius r − d).
/// </summary>
/// <remarks>
/// <para>
/// The constructor validates that the base curve is planar with respect to the given
/// normal (sampled). It deliberately does NOT validate |d| against the base curve's
/// minimum radius of curvature: offsets larger than that produce cusps and local
/// self-intersections, and — matching OCCT's Geom_OffsetCurve — handling or avoiding
/// them is the caller's responsibility.
/// </para>
/// <para>
/// Derivative (documented because it must never be finite-differenced): with
/// T̂ = C′/|C′| and N̂ = n̂ × T̂, the planar Frenet equations give T̂′ = κ|C′|·N̂ and
/// N̂′ = −κ|C′|·T̂, where κ = C″·N̂ / |C′|² is the signed curvature about n̂. Then
/// O′ = C′ + d·N̂′ = |C′|·T̂ − d·κ|C′|·T̂ = (1 − d·κ)·C′.
/// Exactness therefore inherits from the base curve's <see cref="Curve3d.DerivativeAt"/>
/// and <see cref="Curve3d.SecondDerivativeAt"/> overrides (exact for lines, conics, and
/// NURBS; finite-difference fallback otherwise). <see cref="Curve3d.SecondDerivativeAt"/>
/// is NOT overridden here — O″ involves κ′ and hence C‴, which the curve interface does
/// not expose exactly, so the base finite-difference fallback applies.
/// </para>
/// </remarks>
public sealed class OffsetCurve3d : Curve3d
{
    private readonly Curve3d _base;
    private readonly Vector3d _normal;
    private readonly double _distance;

    public OffsetCurve3d(Curve3d baseCurve, Vector3d planeNormal, double distance)
    {
        ArgumentNullException.ThrowIfNull(baseCurve);
        _base = baseCurve;
        _normal = planeNormal.Normalized();
        _distance = distance;

        var domain = baseCurve.Domain;
        if (!double.IsFinite(domain.Length))
            throw new ArgumentException("Offset base curve must have a bounded domain.", nameof(baseCurve));

        // Planarity check: every sampled point must lie in the plane through the start
        // point with normal n̂ (tolerance scaled by the curve's sampled extent).
        const int samples = 16;
        var origin = baseCurve.PointAt(domain.Start);
        double size = 0, deviation = 0;
        for (int i = 1; i <= samples; i++)
        {
            var offsetFromOrigin = baseCurve.PointAt(domain.ParameterAt((double)i / samples)) - origin;
            size = Math.Max(size, offsetFromOrigin.Length);
            deviation = Math.Max(deviation, Math.Abs(offsetFromOrigin.Dot(_normal)));
        }
        if (deviation > Tolerance.Default.Linear * Math.Max(1.0, size))
            throw new ArgumentException(
                $"Base curve is not planar with respect to the given normal (deviation {deviation:E3}).",
                nameof(planeNormal));
    }

    public Curve3d Base => _base;
    public Vector3d PlaneNormal => _normal;
    public double Distance => _distance;

    public override Interval Domain => _base.Domain;
    public override bool IsClosed => _base.IsClosed;

    /// <summary>
    /// Forwards to the base curve's innermost geometry so consumers pick the same
    /// SAMPLING rules (e.g. angular sampling for an offset arc). As everywhere in the
    /// kernel, <see cref="Curve3d.Underlying"/> must never be trusted for POSITION —
    /// the offset geometry lives elsewhere; sample this curve.
    /// </summary>
    public override Curve3d Underlying => _base.Underlying;

    public override Vector3d PointAt(double t) =>
        _base.PointAt(t) + _normal.Cross(_base.TangentAt(t)) * _distance;

    /// <summary>O′(t) = (1 − d·κ(t))·C′(t); see the class remarks for the derivation.</summary>
    public override Vector3d DerivativeAt(double t)
    {
        var d1 = _base.DerivativeAt(t);
        var d2 = _base.SecondDerivativeAt(t);
        double speedSquared = d1.LengthSquared;
        // Signed curvature about n̂: κ = C″·(n̂ × C′) / |C′|³.
        double curvature = d2.Dot(_normal.Cross(d1)) / (speedSquared * Math.Sqrt(speedSquared));
        return d1 * (1 - _distance * curvature);
    }

    public override Vector3d TangentAt(double t)
    {
        var derivative = DerivativeAt(t);
        // O′ vanishes only at a cusp (d·κ = 1); fall back to finite differences there.
        return derivative.TryNormalize(new Tolerance(1e-14, 1e-14), out var unit)
            ? unit
            : base.TangentAt(t);
    }
}
