using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>How a <see cref="LengthMatch.Tune"/> ended.</summary>
public enum LengthTuneOutcome
{
    /// <summary>The trace reached the target length within tolerance (the tuned trace is returned,
    /// DRC-clean).</summary>
    Reached,

    /// <summary>The target equalled the current length within tolerance, so nothing was added — the
    /// ORIGINAL trace is returned unchanged.</summary>
    Unchanged,

    /// <summary>The request is invalid — a target SHORTER than the current length (a serpentine can
    /// only ADD length). The original trace is returned unchanged.</summary>
    Refused,

    /// <summary>There is no DRC-clean room to add the needed length on this trace. The original trace
    /// is returned unchanged; <see cref="LengthTuneResult.MaxAddableLength"/> says how much it COULD
    /// have added.</summary>
    Untunable,
}

/// <summary>
/// The result of tuning one trace to a target length — the outcome, the resulting trace (tuned, or
/// the original for every non-<see cref="LengthTuneOutcome.Reached"/> outcome), its measured length,
/// and a human-readable message naming what happened.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Trace">The resulting trace — tuned on <see cref="LengthTuneOutcome.Reached"/>, the
/// original otherwise.</param>
/// <param name="TargetLength">The requested length (mm).</param>
/// <param name="AchievedLength">The resulting trace's measured centre-line length (mm).</param>
/// <param name="MaxAddableLength">On <see cref="LengthTuneOutcome.Untunable"/>, the largest length the
/// trace could add on its longest segment without a DRC violation (mm); 0 otherwise.</param>
/// <param name="Message">A description naming the outcome and the numbers involved.</param>
public readonly record struct LengthTuneResult(
    LengthTuneOutcome Outcome,
    PcbTrace Trace,
    double TargetLength,
    double AchievedLength,
    double MaxAddableLength,
    string Message)
{
    /// <summary>True when the trace reached its target (or was already there).</summary>
    public bool Ok => Outcome is LengthTuneOutcome.Reached or LengthTuneOutcome.Unchanged;
}

/// <summary>
/// Length matching (serpentine / accordion tuning) for routed <see cref="PcbTrace"/>s — the timing
/// stage after routing, which lengthens a trace to a target so a bus arrives with matched
/// propagation delay.
///
/// <para><b>The tuning is a square-wave COMB, and the DRC is the source of truth.</b> The trace's
/// longest straight segment is replaced by a comb of <c>N</c> teeth of amplitude <c>A</c>, which adds
/// exactly <c>2·N·A</c> of centre-line length, so setting <c>A = (target − current) / (2N)</c> hits
/// the target by construction (verified by MEASURING the built polyline, never by a claimed number).
/// <c>N</c> is MAXIMISED subject to a pitch floor of two trace widths — more teeth means a SMALLER
/// amplitude, the DRC-friendliest comb — and the whole tuned trace is committed only after
/// <see cref="PcbDrc.Violates"/> confirms it adds no clearance violation against the board's other
/// copper (the router's exact-DRC-is-truth rule, so a tuned trace is DRC-clean or the tuning is
/// refused by name). The trace's endpoints and net never move — only the middle path lengthens — so
/// connectivity is unchanged.</para>
///
/// <para><b>v1 scope.</b> A single uniform comb on the ONE longest segment, teeth to alternating
/// sides; a segment boxed in on both sides within the needed amplitude is reported
/// <see cref="LengthTuneOutcome.Untunable"/> with how much it could add. Filed as follow-ups:
/// spreading the comb over several segments, routing teeth only to the OPEN side, ripping up a
/// neighbour to make room, differential-pair coupled tuning (matching within a pair while holding the
/// gap), and impedance/skew beyond pure length.</para>
/// </summary>
public static class LengthMatch
{
    /// <summary>The default length tolerance (mm) — how close to the target counts as matched.</summary>
    public const double DefaultToleranceMm = 0.05;

    /// <summary>The centre-line length of a trace (mm) — the sum of its segment lengths.</summary>
    public static double Length(in PcbTrace trace)
    {
        double total = 0;
        for (int i = 1; i < trace.Points.Count; i++)
            total += trace.Points[i - 1].DistanceTo(trace.Points[i]);
        return total;
    }

    /// <summary>
    /// Tunes the trace at <paramref name="traceIndex"/> in <paramref name="layout"/> up to
    /// <paramref name="targetLength"/> by inserting a DRC-clean serpentine on its longest segment.
    /// The layout is NOT mutated — the tuned trace is returned in the result for the caller to apply.
    /// </summary>
    /// <param name="layout">The routed layout (its other copper is the DRC obstacle set).</param>
    /// <param name="traceIndex">The index into <see cref="PcbLayout.Traces"/> of the trace to tune.</param>
    /// <param name="targetLength">The target centre-line length (mm); must be ≥ the current length.</param>
    /// <param name="tolerance">How close to the target counts as matched (mm); default
    /// <see cref="DefaultToleranceMm"/>.</param>
    /// <param name="rules">The DRC rules the serpentine is gated against; null = <see cref="DrcRuleSet.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="traceIndex"/> is out of range.</exception>
    /// <exception cref="ArgumentException"><paramref name="tolerance"/> is not positive or
    /// <paramref name="targetLength"/> is not finite.</exception>
    public static LengthTuneResult Tune(
        PcbLayout layout, int traceIndex, double targetLength,
        double tolerance = DefaultToleranceMm, DrcRuleSet? rules = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (traceIndex < 0 || traceIndex >= layout.Traces.Count)
            throw new ArgumentOutOfRangeException(nameof(traceIndex),
                $"Trace index {traceIndex} is out of range (the layout has {layout.Traces.Count} traces).");
        if (!(tolerance > 0))
            throw new ArgumentException($"The tolerance must be positive (got {tolerance:g6}).", nameof(tolerance));
        if (!double.IsFinite(targetLength))
            throw new ArgumentException($"The target length must be finite (got {targetLength:g6}).", nameof(targetLength));

        rules ??= DrcRuleSet.Default;
        var trace = layout.Traces[traceIndex];
        var model = ModelExcludingTrace(PcbCopperModel.FromLayout(layout), traceIndex);
        return TuneAgainst(trace, traceIndex, model, targetLength, tolerance, rules);
    }

    /// <summary>
    /// Matches every trace named in <paramref name="traceIndices"/> to a common target — by default
    /// the LONGEST member's current length, so the whole set arrives within <paramref name="tolerance"/>
    /// of each other. Members are tuned in order and each is DRC-checked against the others' CURRENT
    /// geometry — including members already tuned in this call — so two serpentines from one group
    /// cannot collide unnoticed. The layout is not mutated; the results carry the tuned traces.
    /// </summary>
    /// <param name="layout">The routed layout.</param>
    /// <param name="traceIndices">The traces to match (indices into <see cref="PcbLayout.Traces"/>).</param>
    /// <param name="tolerance">The skew budget (mm); default <see cref="DefaultToleranceMm"/>.</param>
    /// <param name="rules">The DRC rules; null = <see cref="DrcRuleSet.Default"/>.</param>
    /// <returns>One result per input index, in the same order.</returns>
    public static IReadOnlyList<LengthTuneResult> MatchGroup(
        PcbLayout layout, IReadOnlyList<int> traceIndices,
        double tolerance = DefaultToleranceMm, DrcRuleSet? rules = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(traceIndices);
        if (traceIndices.Count == 0)
            throw new ArgumentException("A length-match group needs at least one trace.", nameof(traceIndices));
        foreach (int i in traceIndices)
            if (i < 0 || i >= layout.Traces.Count)
                throw new ArgumentOutOfRangeException(nameof(traceIndices),
                    $"Trace index {i} is out of range (the layout has {layout.Traces.Count} traces).");
        if (!(tolerance > 0))
            throw new ArgumentException($"The tolerance must be positive (got {tolerance:g6}).", nameof(tolerance));
        rules ??= DrcRuleSet.Default;

        double target = traceIndices.Max(i => Length(layout.Traces[i]));

        // Tune sequentially; each member sees the others at their current (possibly already-tuned)
        // geometry, so a later serpentine that would collide with an earlier one is refused.
        var baseModel = PcbCopperModel.FromLayout(layout);
        var tuned = new Dictionary<int, PcbTrace>();
        var results = new LengthTuneResult[traceIndices.Count];
        for (int k = 0; k < traceIndices.Count; k++)
        {
            int index = traceIndices[k];
            var model = ModelWithTuned(baseModel, tuned, exclude: index);
            var result = TuneAgainst(layout.Traces[index], index, model, target, tolerance, rules);
            results[k] = result;
            // Adopt the tuned geometry for the members that follow, whether or not it fully reached —
            // a partially-tuned trace is still real copper the next member must clear.
            tuned[index] = result.Trace;
        }
        return results;
    }

    // ---- the tuner ----------------------------------------------------------

    private static LengthTuneResult TuneAgainst(
        in PcbTrace trace, int traceIndex, PcbCopperModel model,
        double target, double tolerance, DrcRuleSet rules)
    {
        double current = Length(trace);

        if (target < current - tolerance)
            return new LengthTuneResult(LengthTuneOutcome.Refused, trace, target, current, 0,
                $"Target length {target:g6} mm is shorter than the current {current:g6} mm; a serpentine can only ADD length.");

        if (Math.Abs(target - current) <= tolerance)
            return new LengthTuneResult(LengthTuneOutcome.Unchanged, trace, target, current, 0,
                $"Trace is already {current:g6} mm, within {tolerance:g6} mm of the target {target:g6} mm; nothing added.");

        double add = target - current;   // > tolerance > 0

        // The longest segment carries the comb.
        int seg = LongestSegment(trace, out var a, out var b);
        double s = a.DistanceTo(b);
        double minCell = 4 * trace.Width;               // pitch floor: a bump's two 90° sides clear at cell/2 ≥ 2·width
        int nMax = (int)Math.Floor(s / minCell);
        if (nMax < 1)
            return new LengthTuneResult(LengthTuneOutcome.Untunable, trace, target, current, 0,
                $"The trace's longest segment ({s:g6} mm) is shorter than one serpentine tooth ({minCell:g6} mm); "
                + "there is nowhere to add length.");

        // More teeth means a smaller amplitude (A = add / (2N)), which is the DRC-friendliest comb, so
        // try the maximum tooth count first. It also gives the exact target by construction.
        double amp = add / (2.0 * nMax);
        var candidate = BuildComb(trace, seg, a, b, nMax, amp);
        if (CleanAgainst(model, candidate, traceIndex, rules))
        {
            double achieved = Length(candidate);
            return new LengthTuneResult(LengthTuneOutcome.Reached, candidate, target, achieved, 0,
                $"Tuned from {current:g6} to {achieved:g6} mm ({nMax} teeth, {amp:g6} mm amplitude), DRC-clean.");
        }

        // The friendliest comb still hit copper — the segment is boxed in. Report how much it could add
        // with the largest DRC-clean amplitude at this tooth count.
        double cleanAmp = LargestCleanAmplitude(trace, seg, a, b, nMax, amp, model, traceIndex, rules);
        double maxAdd = 2.0 * nMax * cleanAmp;
        return new LengthTuneResult(LengthTuneOutcome.Untunable, trace, target, current, maxAdd,
            $"No DRC-clean room to add {add:g6} mm on the trace's longest segment; the largest clean serpentine there "
            + $"adds {maxAdd:g6} mm. (v1 tunes one segment with a uniform comb; a different route or manual tuning is needed.)");
    }

    // The largest amplitude (in [0, cap]) whose comb is DRC-clean, by bisection. Returns 0 if even a
    // vanishing amplitude is blocked (copper coincident with the trace itself, which a clean board
    // never has, so this is a robustness floor rather than a real case).
    private static double LargestCleanAmplitude(
        in PcbTrace trace, int seg, Vector2d a, Vector2d b, int n, double cap,
        PcbCopperModel model, int traceIndex, DrcRuleSet rules)
    {
        double lo = 0, hi = cap;
        for (int iter = 0; iter < 24; iter++)
        {
            double mid = 0.5 * (lo + hi);
            var candidate = BuildComb(trace, seg, a, b, n, mid);
            if (CleanAgainst(model, candidate, traceIndex, rules))
                lo = mid;
            else
                hi = mid;
        }
        return lo;
    }

    // Replaces segment [a,b] of the trace (its point pair at index seg,seg+1) with a comb of n
    // rectangular BUMPS, one per cell, alternating sides. A bump rises at 90° from the baseline, runs
    // flat across the middle half of its cell, and drops back to the baseline — all 90° corners (which
    // a round-joined trace passes the acute-angle rule with) and NO 180° hairpin, so the copper is
    // DRC-clean where a naive up-then-down square wave would pinch. Each bump adds 2·amp of length, so
    // the comb adds 2·n·amp. It starts at a and ends at b on the centre line, leaving the rest of the
    // trace untouched and the polyline connected.
    private static PcbTrace BuildComb(in PcbTrace trace, int seg, Vector2d a, Vector2d b, int n, double amp)
    {
        var d = (b - a);
        double s = d.Length;
        var dir = d / s;                         // unit along the segment
        var nrm = new Vector2d(-dir.Y, dir.X);   // left normal
        double cell = s / n;

        Vector2d P(double along, double lateral) => a + along * dir + lateral * nrm;

        var comb = new List<Vector2d>(4 * n + 2) { a };
        for (int k = 0; k < n; k++)
        {
            double sign = (k % 2 == 0) ? 1.0 : -1.0;
            double x0 = k * cell;
            double xUp = x0 + cell / 4;      // rise here
            double xDn = x0 + 3 * cell / 4;  // fall here (bump top spans the middle half)
            comb.Add(P(xUp, 0));             // baseline lead-in
            comb.Add(P(xUp, sign * amp));    // up
            comb.Add(P(xDn, sign * amp));    // along the top
            comb.Add(P(xDn, 0));             // back to baseline (lead-out is the next cell's lead-in)
        }
        comb.Add(b);                         // baseline to the segment end

        var points = new List<Vector2d>(trace.Points.Count + comb.Count);
        for (int i = 0; i < seg; i++) points.Add(trace.Points[i]);          // …up to a
        points.AddRange(comb);                                              // a …comb… b
        for (int i = seg + 2; i < trace.Points.Count; i++) points.Add(trace.Points[i]);   // after b…
        return trace with { Points = points };
    }

    private static int LongestSegment(in PcbTrace trace, out Vector2d a, out Vector2d b)
    {
        int best = 0;
        double bestLen = -1;
        for (int i = 1; i < trace.Points.Count; i++)
        {
            double len = trace.Points[i - 1].DistanceTo(trace.Points[i]);
            if (len > bestLen) { bestLen = len; best = i - 1; }
        }
        a = trace.Points[best];
        b = trace.Points[best + 1];
        return best;
    }

    // The tuned trace adds no clearance violation: every copper region of the candidate clears the
    // model's OTHER-net copper. Same-net copper (the trace's own pads/regions) is the intended
    // connection and is never flagged, so the comb's own teeth do not fight each other.
    private static bool CleanAgainst(PcbCopperModel model, in PcbTrace candidate, int traceIndex, DrcRuleSet rules)
    {
        string source = PcbLayout.TraceSource(traceIndex);
        foreach (var region in TraceGeometry.Regions(candidate))
        {
            var feature = new CopperFeature(candidate.Layer, candidate.Net, source, region);
            if (PcbDrc.Violates(model, feature, rules).Violations.Count > 0)
                return false;
        }
        return true;
    }

    // The copper model with one trace's features removed, so a tuned replacement can be checked as a
    // candidate against everything ELSE (its pads stay, same-net, so the connection is not flagged).
    private static PcbCopperModel ModelExcludingTrace(PcbCopperModel model, int traceIndex)
    {
        string source = PcbLayout.TraceSource(traceIndex);
        var copper = model.Copper.Where(f => f.Source != source).ToList();
        return new PcbCopperModel(model.Board, copper, model.Drills, model.Cavities, model.Vias);
    }

    // The base copper model with the excluded trace removed and every already-tuned group member's
    // features swapped for its tuned geometry — so each member of a MatchGroup is checked against the
    // others' current (tuned-so-far) copper.
    private static PcbCopperModel ModelWithTuned(
        PcbCopperModel baseModel, IReadOnlyDictionary<int, PcbTrace> tuned, int exclude)
    {
        var replacedSources = new HashSet<string>(tuned.Keys.Select(PcbLayout.TraceSource))
        {
            PcbLayout.TraceSource(exclude),
        };
        var copper = baseModel.Copper.Where(f => !replacedSources.Contains(f.Source)).ToList();
        foreach (var (index, t) in tuned)
        {
            if (index == exclude) continue;
            string source = PcbLayout.TraceSource(index);
            foreach (var region in TraceGeometry.Regions(t))
                copper.Add(new CopperFeature(t.Layer, t.Net, source, region));
        }
        return new PcbCopperModel(baseModel.Board, copper, baseModel.Drills, baseModel.Cavities, baseModel.Vias);
    }
}
