using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Arc-length parameterization table for repeated queries on a <see cref="Curve3d"/> (the
/// 3D twin of <see cref="ArcLengthTable2d"/>, and geometry3Sharp's <c>ArcLengthParam</c>
/// role). Builds a monotone (length, parameter) table once and answers
/// <see cref="ParameterAtLength"/> by table lookup plus one safeguarded Newton polish
/// against the exact speed, so a resampling loop costs O(n) lookups instead of O(n) full
/// integrations.
/// </summary>
public sealed class ArcLengthTable3d
{
    private readonly Curve3d _curve;
    private readonly double[] _parameters;
    private readonly double[] _lengths;

    /// <summary>Total arc length of the curve.</summary>
    public double TotalLength => _lengths[^1];

    /// <summary>The curve this table parameterizes.</summary>
    public Curve3d Curve => _curve;

    public ArcLengthTable3d(Curve3d curve, int steps = 128, double relativeTolerance = 1e-12)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (steps < 1)
            throw new ArgumentOutOfRangeException(nameof(steps));
        _curve = curve;
        _parameters = new double[steps + 1];
        _lengths = new double[steps + 1];
        var domain = curve.Domain;
        for (int i = 0; i <= steps; i++)
            _parameters[i] = domain.ParameterAt((double)i / steps);
        // Cumulative sum of per-interval quadrature: each piece is integrated exactly once,
        // so the table costs one full integration, not one per entry.
        for (int i = 1; i <= steps; i++)
            _lengths[i] = _lengths[i - 1] + curve.ArcLength(_parameters[i - 1], _parameters[i], relativeTolerance);
    }

    /// <summary>The curve parameter at arc length <paramref name="length"/> from the domain
    /// start; lengths outside [0, <see cref="TotalLength"/>] clamp to the domain ends.</summary>
    public double ParameterAtLength(double length)
    {
        if (length <= 0)
            return _parameters[0];
        if (length >= TotalLength)
            return _parameters[^1];

        int index = Array.BinarySearch(_lengths, length);
        if (index >= 0)
            return _parameters[index];
        index = ~index; // first entry greater than length
        double lo = _parameters[index - 1], hi = _parameters[index];
        double span = _lengths[index] - _lengths[index - 1];
        double t = span > 0 ? lo + (hi - lo) * (length - _lengths[index - 1]) / span : lo;

        double target = length - _lengths[index - 1];
        for (int iteration = 0; iteration < 12; iteration++)
        {
            double f = _curve.ArcLength(lo, t) - target;
            double speed = _curve.DerivativeAt(t).Length;
            if (speed <= 0)
                break;
            double next = t - f / speed;
            if (!(next > lo && next < hi))
                break;
            if (Math.Abs(next - t) <= (hi - lo) * 1e-15)
            {
                t = next;
                break;
            }
            t = next;
        }
        return t;
    }

    /// <summary>The point at arc length <paramref name="length"/> from the domain start.</summary>
    public Vector3d PointAtLength(double length) => _curve.PointAt(ParameterAtLength(length));

    /// <summary>
    /// <paramref name="count"/> + 1 points spaced equally BY ARC LENGTH from one end of the
    /// curve to the other — the resampling loop the table exists for.
    /// </summary>
    public Vector3d[] SampleByLength(int count)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count));
        var points = new Vector3d[count + 1];
        for (int i = 0; i <= count; i++)
            points[i] = PointAtLength(TotalLength * i / count);
        return points;
    }
}
