namespace EngrCAD.Cam;

/// <summary>A print-time estimate as an honest BRACKET: the lower bound is every move at
/// its own feed (a machine with infinite acceleration), the upper bound accelerates every
/// move from rest and back (a machine that carries no velocity through corners). The real
/// firmware sits between — how close to which end depends on its junction handling, which
/// is the filed refinement.</summary>
public sealed record PrintTimeEstimate(double MinSeconds, double MaxSeconds);

/// <summary>
/// The print-time estimator — computed from the DECODED program, deliberately: the
/// estimate reads what the file says, exactly as the printer will, so a wrong feed or a
/// lost move shows up in the time the way it would on the machine. Per move the model is
/// the closed-form TRAPEZOID at the machine's acceleration: a move of length d at feed v
/// takes <c>d/v + v/a</c> when it reaches full speed (d ≥ v²/a) and <c>2·√(d/a)</c>
/// when it stays triangular — exact arithmetic, asserted directly in the tests, with the
/// infinite-acceleration limit collapsing the bracket onto the lower bound.
///
/// <para>An E-only move (a retract) runs the extruder axis: its distance is |ΔE|.
/// Junction-deviation cornering, per-axis limits and jerk are filed with the campaign —
/// they narrow the bracket, they do not move its ends.</para>
/// </summary>
public static class PrintTime
{
    /// <summary>Estimates the program's print time at the given acceleration (mm/s²).</summary>
    public static PrintTimeEstimate Estimate(GcodeProgram program, double acceleration = 500)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (!(acceleration > 0) || !double.IsFinite(acceleration))
            throw new ArgumentException(
                $"acceleration must be finite and positive; got {acceleration:0.###}.");

        double min = 0, max = 0;
        foreach (var move in program.Moves)
        {
            double d = Math.Max((move.To - move.From).Length, Math.Abs(move.DeltaE));
            double v = move.Feed / 60; // F words are mm/min
            if (!(d > 0) || !(v > 0))
                continue;
            min += d / v;
            max += d >= v * v / acceleration
                ? d / v + v / acceleration
                : 2 * Math.Sqrt(d / acceleration);
        }
        return new PrintTimeEstimate(min, max);
    }
}
