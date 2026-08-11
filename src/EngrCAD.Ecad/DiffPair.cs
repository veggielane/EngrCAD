using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>
/// A differential pair — two nets (<see cref="PositiveNet"/>, <see cref="NegativeNet"/>) routed
/// together as a coupled pair for controlled impedance and common-mode rejection. The pair carries
/// the two properties a diff pair is judged by: a target coupling <see cref="TargetGapMm"/> (the
/// edge-to-edge spacing the two traces should hold) and a <see cref="SkewToleranceMm"/> (how close
/// their lengths must match so the two halves arrive together).
/// </summary>
/// <param name="PositiveNet">The + net name.</param>
/// <param name="NegativeNet">The − net name.</param>
/// <param name="TargetGapMm">The intended centre-to-centre spacing between the two traces (mm).</param>
/// <param name="SkewToleranceMm">The allowed length mismatch between the two halves (mm).</param>
/// <param name="GapToleranceMm">How far the actual spacing may stray from <see cref="TargetGapMm"/>
/// and still count as coupled (mm).</param>
public readonly record struct DiffPair(
    string PositiveNet, string NegativeNet, double TargetGapMm,
    double SkewToleranceMm = 0.1, double GapToleranceMm = 0.05)
{
    /// <summary>Validates the declaration, refusing an empty/duplicate net or a non-positive
    /// gap/tolerance by name.</summary>
    public void Validate()
    {
        if (string.IsNullOrEmpty(PositiveNet) || string.IsNullOrEmpty(NegativeNet))
            throw new ArgumentException("A differential pair needs two net names.");
        if (PositiveNet == NegativeNet)
            throw new ArgumentException($"A differential pair's two nets must differ (both are '{PositiveNet}').");
        if (!(TargetGapMm > 0))
            throw new ArgumentException($"The target gap must be positive (got {TargetGapMm:g6}).");
        if (!(SkewToleranceMm > 0))
            throw new ArgumentException($"The skew tolerance must be positive (got {SkewToleranceMm:g6}).");
        if (!(GapToleranceMm > 0))
            throw new ArgumentException($"The gap tolerance must be positive (got {GapToleranceMm:g6}).");
    }
}

/// <summary>
/// The measured state of a routed differential pair — its two lengths and their skew, and how much of
/// the + trace runs coupled to the − trace at the target gap.
/// </summary>
/// <param name="PositiveLength">The + trace's centre-line length (mm).</param>
/// <param name="NegativeLength">The − trace's centre-line length (mm).</param>
/// <param name="Skew">The length mismatch <c>|PositiveLength − NegativeLength|</c> (mm).</param>
/// <param name="WithinSkew">Whether <see cref="Skew"/> is within the pair's skew tolerance.</param>
/// <param name="MedianGapMm">The median nearest-neighbour spacing from the + trace to the − trace (mm).</param>
/// <param name="CoupledFraction">The fraction of the + trace's length whose nearest point on the
/// − trace lies within the gap tolerance of the target gap — 1.0 for a perfectly parallel pair.</param>
/// <param name="WellCoupled">Whether <see cref="CoupledFraction"/> is at least
/// <see cref="DiffPairs.WellCoupledFraction"/>.</param>
/// <param name="Message">A description of the measured state.</param>
public readonly record struct DiffPairReport(
    double PositiveLength, double NegativeLength, double Skew, bool WithinSkew,
    double MedianGapMm, double CoupledFraction, bool WellCoupled, string Message)
{
    /// <summary>True when the pair is both well-coupled and within its skew tolerance.</summary>
    public bool Ok => WithinSkew && WellCoupled;
}

/// <summary>
/// Differential-pair analysis over a routed <see cref="PcbLayout"/>: measure a pair's coupling and
/// skew (<see cref="Check"/>), and equalise the two halves' lengths (<see cref="MatchSkew"/>) by
/// reusing the exact-DRC-gated serpentine tuner (<see cref="LengthMatch"/>).
///
/// <para><b>v1 is analysis + skew tuning, not coupled routing.</b> The pair must already be routed
/// (each net to exactly one trace); this reports whether the two traces ARE a good diff pair and
/// tunes their lengths to match. Filed as follow-ups: COUPLED routing (routing the two together while
/// holding the gap — the hard research stage), per-segment skew tuning that preserves coupling, and
/// impedance from the stackup.</para>
/// </summary>
public static class DiffPairs
{
    /// <summary>The coupled-length fraction at or above which a pair counts as well coupled (0.9).</summary>
    public const double WellCoupledFraction = 0.9;

    /// <summary>The arc-length step (mm) at which the + trace is sampled to measure coupling.</summary>
    public const double DefaultSampleStepMm = 0.25;

    /// <summary>
    /// Measures the routed differential pair — its lengths, skew and coupling. Both nets must be
    /// routed to exactly one trace each; otherwise the report says so.
    /// </summary>
    public static DiffPairReport Check(PcbLayout layout, DiffPair pair, double sampleStep = DefaultSampleStepMm)
    {
        ArgumentNullException.ThrowIfNull(layout);
        pair.Validate();
        if (!(sampleStep > 0))
            throw new ArgumentException($"The sample step must be positive (got {sampleStep:g6}).", nameof(sampleStep));

        if (!TryOnlyTrace(layout, pair.PositiveNet, out var p, out string pWhy))
            return NotRouted(pair, pWhy);
        if (!TryOnlyTrace(layout, pair.NegativeNet, out var n, out string nWhy))
            return NotRouted(pair, nWhy);

        double pl = LengthMatch.Length(p), nl = LengthMatch.Length(n);
        double skew = Math.Abs(pl - nl);
        bool withinSkew = skew <= pair.SkewToleranceMm;

        // Sample the + trace by arc length; for each sample, the nearest point on the − trace.
        var gaps = new List<double>();
        int coupled = 0, total = 0;
        foreach (var s in SampleAlong(p.Points, sampleStep))
        {
            double d = Math.Sqrt(NearestSquaredDistance(s, n.Points));
            gaps.Add(d);
            total++;
            if (Math.Abs(d - pair.TargetGapMm) <= pair.GapToleranceMm) coupled++;
        }
        double coupledFraction = total == 0 ? 0 : (double)coupled / total;
        double medianGap = Median(gaps);
        bool wellCoupled = coupledFraction >= WellCoupledFraction;

        string msg =
            $"{pair.PositiveNet}/{pair.NegativeNet}: skew {skew:g4} mm ({(withinSkew ? "within" : "over")} "
            + $"{pair.SkewToleranceMm:g4}), median gap {medianGap:g4} mm, {coupledFraction:P0} coupled at "
            + $"{pair.TargetGapMm:g4}±{pair.GapToleranceMm:g4} mm ({(wellCoupled ? "well coupled" : "poorly coupled")}).";
        return new DiffPairReport(pl, nl, skew, withinSkew, medianGap, coupledFraction, wellCoupled, msg);
    }

    /// <summary>
    /// Equalises the two halves' lengths by lengthening the SHORTER to the longer, via the DRC-gated
    /// serpentine tuner. The layout is not mutated; the tuned traces are returned in the two results
    /// (apply them with <see cref="PcbLayout.ReplaceTrace"/>). If either net is not routed to exactly
    /// one trace, both results carry the reason.
    /// </summary>
    /// <returns>(the + result, the − result) — the one that was already the longer is
    /// <see cref="LengthTuneOutcome.Unchanged"/>.</returns>
    public static (LengthTuneResult Positive, LengthTuneResult Negative) MatchSkew(
        PcbLayout layout, DiffPair pair, DrcRuleSet? rules = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        pair.Validate();
        bool pOk = TryOnlyTraceIndex(layout, pair.PositiveNet, out int pi, out string pWhy);
        bool nOk = TryOnlyTraceIndex(layout, pair.NegativeNet, out int ni, out string nWhy);
        if (!pOk || !nOk)
        {
            string why = !pOk ? pWhy : nWhy;
            var refused = new LengthTuneResult(LengthTuneOutcome.Refused, default, 0, 0, 0, why);
            return (refused, refused);
        }

        var results = LengthMatch.MatchGroup(layout, [pi, ni], pair.SkewToleranceMm, rules);
        return (results[0], results[1]);
    }

    // ---- helpers ------------------------------------------------------------

    private static DiffPairReport NotRouted(DiffPair pair, string why) =>
        new(0, 0, 0, false, 0, 0, false,
            $"{pair.PositiveNet}/{pair.NegativeNet}: not a checkable pair — {why}");

    private static bool TryOnlyTrace(PcbLayout layout, string net, out PcbTrace trace, out string why)
    {
        if (TryOnlyTraceIndex(layout, net, out int i, out why)) { trace = layout.Traces[i]; return true; }
        trace = default;
        return false;
    }

    private static bool TryOnlyTraceIndex(PcbLayout layout, string net, out int index, out string why)
    {
        var idxs = Enumerable.Range(0, layout.Traces.Count).Where(i => layout.Traces[i].Net == net).ToList();
        if (idxs.Count == 1) { index = idxs[0]; why = ""; return true; }
        index = -1;
        why = idxs.Count == 0
            ? $"net '{net}' has no routed trace"
            : $"net '{net}' is routed to {idxs.Count} traces (v1 checks a single trace per net)";
        return false;
    }

    // Points along a polyline at a fixed arc-length step (endpoints included).
    private static IEnumerable<Vector2d> SampleAlong(IReadOnlyList<Vector2d> pts, double step)
    {
        yield return pts[0];
        double carry = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            double segLen = a.DistanceTo(b);
            if (segLen <= 0) continue;
            var dir = (b - a) / segLen;
            double x = step - carry;
            while (x < segLen)
            {
                yield return a + x * dir;
                x += step;
            }
            carry = segLen - (x - step);
        }
        yield return pts[^1];
    }

    private static double NearestSquaredDistance(in Vector2d p, IReadOnlyList<Vector2d> polyline)
    {
        double best = double.PositiveInfinity;
        for (int i = 1; i < polyline.Count; i++)
            best = Math.Min(best, PointSegmentSquaredDistance(p, polyline[i - 1], polyline[i]));
        return best;
    }

    private static double PointSegmentSquaredDistance(in Vector2d p, in Vector2d a, in Vector2d b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 <= 0) return (p - a).LengthSquared;
        double t = ((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2;
        t = Math.Clamp(t, 0, 1);
        var proj = a + t * ab;
        return (p - proj).LengthSquared;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : 0.5 * (values[mid - 1] + values[mid]);
    }
}
