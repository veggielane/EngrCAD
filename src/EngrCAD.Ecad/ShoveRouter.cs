using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>How a <see cref="ShoveRouter.Insert"/> ended.</summary>
public enum ShoveOutcome
{
    /// <summary>The new trace was placed, shoving one or more blockers aside — all DRC-clean. The
    /// shoved geometry is in <see cref="ShoveResult.ShovedTraces"/>.</summary>
    Inserted,

    /// <summary>The new trace fits with no shove needed — nothing blocked it.</summary>
    NoShoveNeeded,

    /// <summary>The insertion is refused (a blocker v1 cannot shove, or shoving would create a new
    /// violation). Nothing is changed.</summary>
    Refused,
}

/// <summary>
/// The result of a shove insertion — the outcome, the new trace to add, and the shoved replacements
/// for any blockers (keyed by their index in <see cref="PcbLayout.Traces"/>). The layout is NOT
/// mutated; apply the result with <see cref="PcbLayout.ReplaceTrace"/> for each shoved blocker and
/// <see cref="PcbLayout.AddTrace(PcbTrace)"/> for the new trace.
/// </summary>
public readonly record struct ShoveResult(
    ShoveOutcome Outcome, PcbTrace NewTrace,
    IReadOnlyDictionary<int, PcbTrace> ShovedTraces, string Message)
{
    /// <summary>True when the trace was placed (with or without a shove).</summary>
    public bool Ok => Outcome is ShoveOutcome.Inserted or ShoveOutcome.NoShoveNeeded;
}

/// <summary>
/// Shove (push-and-route) insertion: place a new trace on a direct path even where an existing trace
/// is in the way, by PUSHING the blocker aside rather than detouring the new trace around it. The
/// commit rule is the router's — the whole result (the new trace and every shoved blocker) is
/// DRC-clean, or the insertion is refused by name, so a shove can never ship a clearance violation.
///
/// <para><b>How a blocker is shoved.</b> A blocker running roughly PARALLEL to the new trace's main
/// run, and too close to it, is JOGGED: the stretch of the blocker alongside the new trace is offset
/// perpendicular (away from the new trace) to the target clearance, with a ramp in and out, while its
/// ENDPOINTS stay put (so its pads and its connectivity never move). The new trace itself does not
/// move — that is what makes this a shove rather than a detour.</para>
///
/// <para><b>v1 scope.</b> A blocker must be a single straight segment, roughly parallel to the new
/// trace's longest segment, and extend past that run far enough on each side to ramp (otherwise it is
/// refused by name). It shoves each blocker ONCE (no CASCADE — a shove that would push a blocker into
/// a third trace is refused, not propagated). Filed follow-ups: cascading shoves, shoving a bent
/// blocker, and full push-and-route inside the maze search.</para>
/// </summary>
public static class ShoveRouter
{
    /// <summary>
    /// Places <paramref name="newTrace"/> in <paramref name="layout"/>, shoving any parallel blocker
    /// aside to make room. The layout is not mutated; apply the returned result. The new trace's net,
    /// layer and width are validated as <see cref="PcbLayout.AddTrace(PcbTrace)"/> validates.
    /// </summary>
    public static ShoveResult Insert(PcbLayout layout, PcbTrace newTrace, DrcRuleSet? rules = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        rules ??= DrcRuleSet.Default;
        double clearance = rules.MinCopperClearance;

        var baseModel = PcbCopperModel.FromLayout(layout);

        // The new trace's longest segment is the run a parallel blocker is shoved off.
        (Vector2d f, Vector2d t) = MainSegment(newTrace);
        var u = (t - f);
        double L = u.Length;
        u /= L;
        var p = new Vector2d(-u.Y, u.X);   // left normal

        // Blockers: OTHER-net traces whose centre-line comes within the clear distance of the new
        // trace's centre-line (h = the two half-widths plus the clearance).
        var shoved = new Dictionary<int, PcbTrace>();
        for (int i = 0; i < layout.Traces.Count; i++)
        {
            var b = layout.Traces[i];
            if (b.Net == newTrace.Net) continue;
            double h = newTrace.Width / 2 + clearance + b.Width / 2;
            if (MinCentreLineDistance(newTrace.Points, b.Points) >= h) continue;   // clears already

            double ramp = h;   // ~45° jog in and out
            // Shove past the clearance limit by half a trace width, so the round-join bulge at the new
            // trace's corners (where its lead-ins converge on the run) still clears the blocker.
            double margin = 0.5 * clearance + 0.5 * Math.Max(newTrace.Width, b.Width);
            if (!TryShoveBlocker(b, f, u, p, L, h, ramp, margin, out var jogged, out string why))
                return Refused(newTrace, $"cannot shove the trace on net '{b.Net}': {why}");
            shoved[i] = jogged;
        }

        if (shoved.Count == 0)
        {
            // Nothing blocked it — but it must still be DRC-clean on its own (it might hit a pad).
            return CandidateClean(baseModel, shoved, newTrace, rules)
                ? new ShoveResult(ShoveOutcome.NoShoveNeeded, newTrace, shoved,
                    "The new trace fits directly; no shove was needed.")
                : Refused(newTrace, "the new trace violates the DRC and no parallel blocker could be shoved to fix it.");
        }

        // Commit only if the whole candidate (shoved blockers + the new trace) is DRC-clean.
        if (!CandidateClean(baseModel, shoved, newTrace, rules))
            return Refused(newTrace,
                "shoving the blocker(s) aside created a new DRC violation (v1 does not cascade shoves).");

        return new ShoveResult(ShoveOutcome.Inserted, newTrace, shoved,
            $"Placed the trace on net '{newTrace.Net}', shoving {shoved.Count} blocker(s) aside, DRC-clean.");
    }

    // ---- shoving one blocker ------------------------------------------------

    // Jogs a straight parallel blocker B out of the corridor of the new trace's main run [f, f+L·u].
    // Returns the jogged trace (endpoints unmoved) or false with the reason.
    private static bool TryShoveBlocker(
        in PcbTrace b, Vector2d f, Vector2d u, Vector2d p, double L, double h, double ramp, double margin,
        out PcbTrace jogged, out string why)
    {
        jogged = default;
        why = "";
        if (b.Points.Count != 2)
        {
            why = "v1 shoves a single straight blocker (this one is bent).";
            return false;
        }

        // The blocker in the run's (along, perp) frame.
        double a0 = Along(b.Points[0], f, u), q0 = Perp(b.Points[0], f, p);
        double a1 = Along(b.Points[1], f, u), q1 = Perp(b.Points[1], f, p);
        if (a1 < a0) { (a0, a1) = (a1, a0); (q0, q1) = (q1, q0); }

        if (Math.Abs(q1 - q0) > 0.05 * (h + 1))
        {
            why = "the blocker is not parallel to the route.";
            return false;
        }
        double perp = 0.5 * (q0 + q1);
        if (Math.Abs(perp) < 1e-9)
        {
            why = "the blocker is collinear with the route — there is no side to shove it to.";
            return false;
        }
        double pushed = Math.Sign(perp) * (h + margin);   // push out, a hair past the clear distance

        // It must reach past the run on each side to ramp cleanly.
        if (a0 > -ramp || a1 < L + ramp)
        {
            why = "the blocker does not extend far enough past the route to ramp aside "
                + $"(needs to span [{-ramp:g4}, {L + ramp:g4}] in the route frame, spans [{a0:g4}, {a1:g4}]).";
            return false;
        }

        // The jogged centre-line in the run frame, converted back to board coordinates. Endpoints at
        // the blocker's own (a0, perp)/(a1, perp), the middle pushed to `pushed`.
        Vector2d W(double along, double lateral) => f + along * u + lateral * p;
        var points = new List<Vector2d>
        {
            W(a0, perp),        // the blocker's start (unmoved)
            W(-ramp, perp),     // ramp start
            W(0, pushed),       // pushed in
            W(L, pushed),       // pushed across the run
            W(L + ramp, perp),  // ramp out
            W(a1, perp),        // the blocker's end (unmoved)
        };
        jogged = b with { Points = points };
        return true;
    }

    // ---- the DRC gate -------------------------------------------------------

    // The whole candidate board (base copper with each shoved blocker replaced and the new trace
    // added) has no DRC violation. A clean input board therefore commits only a clean shove.
    private static bool CandidateClean(
        PcbCopperModel baseModel, IReadOnlyDictionary<int, PcbTrace> shoved,
        in PcbTrace newTrace, DrcRuleSet rules)
    {
        var replaced = new HashSet<string>(shoved.Keys.Select(PcbLayout.TraceSource));
        var copper = baseModel.Copper.Where(fe => !replaced.Contains(fe.Source)).ToList();
        foreach (var (index, tr) in shoved)
        {
            string source = PcbLayout.TraceSource(index);
            foreach (var region in TraceGeometry.Regions(tr))
                copper.Add(new CopperFeature(tr.Layer, tr.Net, source, region));
        }
        // The new trace joins with its own distinct source label (the DRC groups by NET, not source,
        // so this only needs to be unique for reporting and must not collide with a replaced blocker).
        foreach (var region in TraceGeometry.Regions(newTrace))
            copper.Add(new CopperFeature(newTrace.Layer, newTrace.Net, "shove-candidate", region));

        var candidate = new PcbCopperModel(baseModel.Board, copper, baseModel.Drills, baseModel.Cavities, baseModel.Vias);
        return PcbDrc.Check(candidate, rules).Violations.Count == 0;
    }

    // ---- geometry helpers ---------------------------------------------------

    private static ShoveResult Refused(in PcbTrace newTrace, string why) =>
        new(ShoveOutcome.Refused, newTrace, new Dictionary<int, PcbTrace>(), why);

    private static (Vector2d, Vector2d) MainSegment(in PcbTrace trace)
    {
        int best = 0;
        double bestLen = -1;
        for (int i = 1; i < trace.Points.Count; i++)
        {
            double len = trace.Points[i - 1].DistanceTo(trace.Points[i]);
            if (len > bestLen) { bestLen = len; best = i - 1; }
        }
        return (trace.Points[best], trace.Points[best + 1]);
    }

    private static double Along(in Vector2d pt, in Vector2d f, in Vector2d u) =>
        (pt.X - f.X) * u.X + (pt.Y - f.Y) * u.Y;

    private static double Perp(in Vector2d pt, in Vector2d f, in Vector2d p) =>
        (pt.X - f.X) * p.X + (pt.Y - f.Y) * p.Y;

    private static double MinCentreLineDistance(IReadOnlyList<Vector2d> a, IReadOnlyList<Vector2d> b)
    {
        double best = double.PositiveInfinity;
        for (int i = 1; i < a.Count; i++)
            for (int j = 1; j < b.Count; j++)
                best = Math.Min(best, SegmentSegmentDistance(a[i - 1], a[i], b[j - 1], b[j]));
        return best;
    }

    private static double SegmentSegmentDistance(Vector2d a0, Vector2d a1, Vector2d b0, Vector2d b1)
    {
        // Min over the four endpoint-to-segment distances (sufficient for non-crossing segments; a
        // crossing pair distance is 0, which the endpoint tests also drive toward for near-crossings).
        double d = Math.Min(
            Math.Min(PointSegmentDistance(a0, b0, b1), PointSegmentDistance(a1, b0, b1)),
            Math.Min(PointSegmentDistance(b0, a0, a1), PointSegmentDistance(b1, a0, a1)));
        if (SegmentsCross(a0, a1, b0, b1)) return 0;
        return d;
    }

    private static double PointSegmentDistance(in Vector2d pt, in Vector2d a, in Vector2d b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 <= 0) return pt.DistanceTo(a);
        double s = ((pt.X - a.X) * ab.X + (pt.Y - a.Y) * ab.Y) / len2;
        s = Math.Clamp(s, 0, 1);
        return pt.DistanceTo(a + s * ab);
    }

    private static bool SegmentsCross(Vector2d a, Vector2d b, Vector2d c, Vector2d d)
    {
        double O(Vector2d p, Vector2d q, Vector2d r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        double d1 = O(c, d, a), d2 = O(c, d, b), d3 = O(a, b, c), d4 = O(a, b, d);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }
}
