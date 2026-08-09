using EngrCAD.Core;

namespace EngrCAD.Implicit;

// GRADED lattices: a thickness, a strut diameter or a level that VARIES over space — stiffness
// where the stress is, porosity where the flow is.
//
// WHAT IS GRADED IS THE PARAMETER, NEVER THE CELL, and that is the scoping decision the whole
// file turns on. Grading the thickness leaves the underlying periodic structure exactly as it
// was: a TPMS sheet is still |F| / (bound * omega) with only the level moving, and a strut
// lattice's fold and its three-wide candidate neighbourhood are arguments about the strut AXES,
// which do not move either. So every soundness property those nodes carry — the exact sign, the
// completeness of the visited neighbourhood, the periodicity of F — is inherited unchanged, and
// the only thing that changes is the Lipschitz constant, which picks up the grading's own.
//
// Grading the CELL SIZE would be a different feature and a much larger one: the fold stops being
// a fold, the neighbourhood argument has nothing to rest on, and there is no sound evaluation to
// fall back to. It is refused by omission rather than approximated.
//
// THE LIPSCHITZ CONSTANT IS STATED, NEVER MEASURED. Every field here is (something 1-Lipschitz)
// minus (the grading), so the composed bound is 1 + L where L is the grading's own constant —
// and a constant that is too small is the one failure this engine cannot absorb, since
// SurfaceNets' block cull, the narrow-band octree and the projection target all read
// LipschitzBound as a promise and would drop geometry silently. So the factories that CAN derive
// it exactly (a linear ramp along a direction, a radial ramp) do, and FromFunction makes the
// caller say it rather than guessing on their behalf.
//
// A CONFORMAL lattice — one following a curved body rather than a flat gradient — is already
// expressible: Twist, Bend and Taper compose with any of these fields and each reports its own
// Lipschitz factor, so the cull widens correctly through them. A general free-form warp is a
// different feature and is not offered here.
//
// WHAT A GRADED FIELD GIVES UP is the volume-fraction ESTIMATOR's premise. "Sample one cell" is
// only meaningful for a periodic field, so Tpms.SheetVolumeFraction and friends are not offered
// for a graded lattice; what IS offered is the other direction — a grading stated as a volume
// fraction, converted pointwise through the same measured cell distribution the uniform solves
// use, so a caller says "40% here, 12% there" and the parameter follows.

/// <summary>
/// A lattice parameter that varies over space: a sheet thickness, a strut diameter, a level or
/// a volume fraction. Carries its own <see cref="LipschitzConstant"/> and its own range, both
/// of which the graded fields need — see the file remarks for why the constant is stated rather
/// than measured.
/// </summary>
public sealed class LatticeGrading
{
    private readonly Func<Vector3d, double> _value;

    private LatticeGrading(
        Func<Vector3d, double> value, double lipschitzConstant, double minimum, double maximum)
    {
        _value = value;
        LipschitzConstant = lipschitzConstant;
        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>An upper bound on how fast this grading changes per unit of movement. The
    /// graded field's own bound is <c>1 + </c> this (scaled, for a level grading, by the
    /// field's normalization), so a value that is too small drops geometry.</summary>
    public double LipschitzConstant { get; }

    /// <summary>The smallest value this grading can take. The value is CLAMPED to
    /// <c>[Minimum, Maximum]</c>, which is what makes the range a guarantee rather than a
    /// promise — and clamping is 1-Lipschitz, so it can only reduce the constant.</summary>
    public double Minimum { get; }

    /// <summary>The largest value this grading can take.</summary>
    public double Maximum { get; }

    /// <summary>The value at a point, clamped into <c>[Minimum, Maximum]</c>.</summary>
    public double At(in Vector3d p) => Math.Clamp(_value(p), Minimum, Maximum);

    /// <summary>A grading that does not vary — the identity, whose constant is exactly 0, so a
    /// field built on it reports the same Lipschitz bound its ungraded twin does.</summary>
    public static LatticeGrading Constant(double value)
    {
        RequireFinite(value, nameof(value));
        return new LatticeGrading(_ => value, 0, value, value);
    }

    /// <summary>
    /// A linear ramp along a direction: <paramref name="atStart"/> at
    /// <paramref name="start"/> along <paramref name="direction"/>, <paramref name="atEnd"/> at
    /// <paramref name="end"/>, held at the end values beyond. The constant is exact —
    /// <c>|atEnd − atStart| / |end − start|</c> — because the coordinate along a unit direction
    /// is 1-Lipschitz and the clamp cannot steepen it.
    /// </summary>
    public static LatticeGrading Along(
        in Vector3d direction, double start, double end, double atStart, double atEnd)
    {
        if (!direction.TryNormalize(Tolerance.Default, out var unit))
            throw new ArgumentException("The grading direction is degenerate.", nameof(direction));
        return Ramp(p => p.Dot(unit), start, end, atStart, atEnd);
    }

    /// <summary>
    /// A radial ramp about a centre: <paramref name="atInner"/> at
    /// <paramref name="innerRadius"/>, <paramref name="atOuter"/> at
    /// <paramref name="outerRadius"/>, held beyond both. Exact for the same reason
    /// <see cref="Along"/> is — a distance from a point is 1-Lipschitz.
    /// </summary>
    public static LatticeGrading Radial(
        in Vector3d centre, double innerRadius, double outerRadius, double atInner, double atOuter)
    {
        var c = centre;
        return Ramp(p => (p - c).Length, innerRadius, outerRadius, atInner, atOuter);
    }

    /// <summary>
    /// The escape hatch: any function, with its Lipschitz constant and its range <b>stated by
    /// the caller</b>. Nothing here can measure the constant — sampling a function proves
    /// nothing about it between the samples — and a value that is too small drops geometry
    /// silently, so this is the one place the guarantee is the caller's.
    /// </summary>
    public static LatticeGrading FromFunction(
        Func<Vector3d, double> value, double lipschitzConstant, double minimum, double maximum)
    {
        ArgumentNullException.ThrowIfNull(value);
        RequireFinite(minimum, nameof(minimum));
        RequireFinite(maximum, nameof(maximum));
        if (!(lipschitzConstant >= 0) || !double.IsFinite(lipschitzConstant))
            throw new ArgumentOutOfRangeException(
                nameof(lipschitzConstant), lipschitzConstant,
                "A grading's Lipschitz constant must be finite and non-negative.");
        if (minimum > maximum)
            throw new ArgumentException(
                $"The grading's range is inverted: minimum {minimum:R} exceeds maximum {maximum:R}.",
                nameof(minimum));
        return new LatticeGrading(value, lipschitzConstant, minimum, maximum);
    }

    private static LatticeGrading Ramp(
        Func<Vector3d, double> coordinate, double start, double end, double atStart, double atEnd)
    {
        RequireFinite(start, nameof(start));
        RequireFinite(end, nameof(end));
        RequireFinite(atStart, nameof(atStart));
        RequireFinite(atEnd, nameof(atEnd));
        double span = end - start;
        if (span == 0)
            throw new ArgumentException(
                "A graded ramp needs two distinct stations; start and end are the same.", nameof(end));

        double slope = (atEnd - atStart) / span;
        return new LatticeGrading(
            p => atStart + slope * Math.Clamp(coordinate(p) - start, Math.Min(0, span), Math.Max(0, span)),
            Math.Abs(slope),
            Math.Min(atStart, atEnd),
            Math.Max(atStart, atEnd));
    }

    /// <summary>
    /// This grading pushed through a monotone map, as a PIECEWISE-LINEAR interpolant over a
    /// fixed ladder — which is what makes the composed constant exact rather than estimated:
    /// the composed map IS the ladder, so its Lipschitz constant is the largest slope between
    /// two consecutive entries, and the chain rule gives <c>this × that</c>.
    /// <para>
    /// The ladder also makes the composition CHEAP, which is the point: converting a volume
    /// fraction to a parameter is a bisection over a sampled cell distribution, and doing that
    /// per query would cost more than the field.
    /// </para>
    /// </summary>
    internal LatticeGrading Through(Func<double, double> map, int steps = 256)
    {
        var table = new double[steps + 1];
        double lo = Minimum, span = Maximum - Minimum;
        for (int i = 0; i <= steps; i++)
            table[i] = map(lo + span * i / steps);

        double slope = 0;
        double tMin = table[0], tMax = table[0];
        for (int i = 1; i <= steps; i++)
        {
            slope = Math.Max(slope, Math.Abs(table[i] - table[i - 1]) / (span / steps));
            tMin = Math.Min(tMin, table[i]);
            tMax = Math.Max(tMax, table[i]);
        }
        if (span == 0)
            slope = 0;

        var source = this;
        return new LatticeGrading(
            p =>
            {
                double f = source.At(p);
                if (span == 0)
                    return table[0];
                double at = Math.Clamp((f - lo) / span * steps, 0, steps);
                int i = Math.Min((int)at, steps - 1);
                return table[i] + (table[i + 1] - table[i]) * (at - i);
            },
            LipschitzConstant * slope,
            tMin,
            tMax);
    }

    private static void RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(name, value, "A grading's values must be finite.");
    }
}

/// <summary>
/// A TPMS sheet whose thickness varies. Everything but the level is the uniform sheet's; see
/// <see cref="LatticeGrading"/>'s file remarks for why that is the whole feature.
/// </summary>
internal sealed class GradedTpmsSheetSdf(
    TpmsSurface surface, double cellSize, LatticeGrading thickness) : Sdf
{
    private readonly double _omega = 2 * Math.PI / cellSize;

    public override double Evaluate(in Vector3d p)
    {
        double g = surface.Value(p.X * _omega, p.Y * _omega, p.Z * _omega);
        return Math.Abs(g) / (surface.GradientBound * _omega) - thickness.At(p) / 2;
    }

    public override Aabb Bounds => InfiniteBounds;

    /// <summary>The uniform sheet is 1-Lipschitz by construction and the half-thickness is
    /// subtracted from it, so the gradients add: <c>1 + L/2</c>.</summary>
    public override double LipschitzBound(in Aabb region) => 1 + thickness.LipschitzConstant / 2;
}

/// <summary>A TPMS solid whose level varies. See <see cref="GradedTpmsSheetSdf"/>.</summary>
internal sealed class GradedTpmsSolidSdf(
    TpmsSurface surface, double cellSize, LatticeGrading level) : Sdf
{
    private readonly double _omega = 2 * Math.PI / cellSize;

    public override double Evaluate(in Vector3d p)
    {
        double g = surface.Value(p.X * _omega, p.Y * _omega, p.Z * _omega);
        return (g - level.At(p)) / (surface.GradientBound * _omega);
    }

    public override Aabb Bounds => InfiniteBounds;

    /// <summary>The level is divided by the same normalization the polynomial is, so its
    /// contribution to the slope is <c>L / (bound·omega)</c>.</summary>
    public override double LipschitzBound(in Aabb region) =>
        1 + level.LipschitzConstant / (surface.GradientBound * _omega);
}
