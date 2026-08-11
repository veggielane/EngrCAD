using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Ecad;

/// <summary>Which design rule a <see cref="MidDrcViolation"/> broke.</summary>
public enum MidDrcRule
{
    /// <summary>Copper of different nets closer than the minimum clearance ON THE SURFACE (a near
    /// miss), the clearance folded through the exp map's distortion.</summary>
    Clearance,

    /// <summary>Copper of different nets overlapping in parameter space — a SHORT, definite whatever
    /// the distortion, and the strongest failure.</summary>
    Short,

    /// <summary>A copper conductor narrower on the surface than the minimum trace width.</summary>
    TraceWidth,
}

/// <summary>What the 3D DRC could say about a pair — the honest three-valued answer the surface
/// distortion forces.</summary>
public enum MidDrcVerdict
{
    /// <summary>The rule is DEFINITELY broken: even the map's most favourable local scale cannot make
    /// the surface distance meet the rule.</summary>
    Violation,

    /// <summary>The DRC CANNOT CERTIFY the pair either way — the distortion band straddles the limit,
    /// so the surface clearance depends on which local scale is real. Refused rather than passed
    /// false-precise (the tamper-mesh near-tangency rule). A conservative DRC treats this as not
    /// passable, so <see cref="Mid3dDrcReport.Ok"/> is false while any uncertainty remains.</summary>
    Uncertain,
}

/// <summary>
/// One finding of the 3D DRC. It NAMES its offenders, LOCATES the finding in parameter AND surface
/// coordinates, and reports the MEASURED parameter separation against the REQUIRED SURFACE clearance
/// with the distortion band that connects them — a report that only said "fail" would be useless (the
/// <c>DrcViolation</c> / <c>PcbLayoutCheck</c> house style).
/// </summary>
/// <param name="Rule">The rule the finding is about.</param>
/// <param name="Verdict">Whether the rule is definitely broken or merely un-certifiable.</param>
/// <param name="Message">A human-readable line naming the offending nets / features.</param>
/// <param name="ParameterLocation">Where it is, in exp-map (u, v) coordinates.</param>
/// <param name="SurfaceLocation">Where it is on the moulded surface (the lift of
/// <see cref="ParameterLocation"/>).</param>
/// <param name="MeasuredParameter">The parameter-space edge-to-edge separation (mm in (u, v)). This is
/// the quantity the DRC and the flat unrolled DRC share bit for bit, since both measure it on the same
/// (u, v) geometry.</param>
/// <param name="Required">The rule's SURFACE limit (mm on the moulded surface).</param>
/// <param name="MinScale">The smallest local scale (surface / parameter) over the pair — the surface
/// separation is at least <c>MeasuredParameter × MinScale</c>.</param>
/// <param name="MaxScale">The largest local scale — the surface separation is at most
/// <c>MeasuredParameter × MaxScale</c>.</param>
public readonly record struct MidDrcViolation(
    MidDrcRule Rule, MidDrcVerdict Verdict, string Message,
    Vector2d ParameterLocation, Vector3d SurfaceLocation,
    double MeasuredParameter, double Required, double MinScale, double MaxScale)
{
    /// <summary>The lower bound on the surface separation the pair could actually have.</summary>
    public double SurfaceSeparationMin => MeasuredParameter * MinScale;

    /// <summary>The upper bound on the surface separation the pair could actually have.</summary>
    public double SurfaceSeparationMax => MeasuredParameter * MaxScale;

    /// <summary>The finding as one line.</summary>
    public override string ToString() =>
        $"{Rule} [{Verdict}]: {Message} at (u {ParameterLocation.X:g4}, v {ParameterLocation.Y:g4})";
}

/// <summary>
/// The result of <see cref="Mid3dDrc.Check(MidBoard, DrcRuleSet?)"/>. The <see cref="Violations"/> are
/// definite faults; the <see cref="Uncertain"/> findings are pairs the map's distortion left
/// un-certifiable (refused, not passed); the <see cref="Ratsnest"/> is the INVERSE — nets the copper
/// does not yet connect — reported as INFORMATION.
/// </summary>
/// <param name="Violations">Definite rule breaks, each naming and locating its offender.</param>
/// <param name="Uncertain">Pairs the DRC could not certify — a conservative refusal in the distortion
/// band.</param>
/// <param name="Ratsnest">Nets whose copper is not all connected (unrouted), in name order.</param>
public sealed record Mid3dDrcReport(
    IReadOnlyList<MidDrcViolation> Violations,
    IReadOnlyList<MidDrcViolation> Uncertain,
    IReadOnlyList<string> Ratsnest)
{
    /// <summary>True when the DRC could CERTIFY the whole board: no definite violation AND nothing left
    /// uncertain. An un-certifiable pair is not passable — a moulded board the parameterization cannot
    /// vouch for is not one a conservative DRC signs off (the un-routed ratsnest is not a fault, so it
    /// does not fail this).</summary>
    public bool Ok => Violations.Count == 0 && Uncertain.Count == 0;

    /// <summary>The findings of one rule (violations and uncertain).</summary>
    public IEnumerable<MidDrcViolation> OfRule(MidDrcRule rule) =>
        Violations.Concat(Uncertain).Where(f => f.Rule == rule);

    /// <summary>One line per violation, then per uncertain finding, then per unrouted net.</summary>
    public IEnumerable<string> Messages
    {
        get
        {
            foreach (var v in Violations)
                yield return v.ToString();
            foreach (var u in Uncertain)
                yield return u.ToString();
            foreach (var net in Ratsnest)
                yield return $"unrouted: net '{net}' is not fully connected by surface copper";
        }
    }

    /// <summary>A human-readable report.</summary>
    public override string ToString() =>
        Ok && Ratsnest.Count == 0
            ? "3D DRC clean"
            : string.Join(Environment.NewLine, Messages);
}

/// <summary>
/// The 3D design-rule check for a moulded (MID) board: the flat copper DRC's rules run in the exp
/// map's (u, v) parameter coordinates, with the surface distortion FOLDED into the clearance.
///
/// <para><b>It is the SAME grow-and-intersect the flat DRC uses</b> (<see cref="PcbDrc"/>): a pair of
/// different-net copper is clear iff, once each is grown by half the clearance, they do not intersect
/// (an empty intersection PROVES the clearance — the tamper-mesh construction). What the surface adds is
/// the FOLD: a required SURFACE clearance <c>C</c> becomes a PARAMETER clearance <c>C / scale</c>, and
/// because the exp map's local scale varies, the check is three-valued rather than two:</para>
/// <list type="bullet">
/// <item><b>Clear</b> — even the map's smallest local scale keeps the surface separation at or above
/// <c>C</c> (the parameter separation is at least <c>C / minScale</c>).</item>
/// <item><b>Violation</b> — even the map's largest local scale cannot (the parameter separation is
/// below <c>C / maxScale</c>).</item>
/// <item><b>Uncertain</b> — the band straddles the limit, so the answer depends on which scale is real.
/// REFUSED with the band stated, not passed false-precise.</item>
/// </list>
///
/// <para><b>On a developable surface the band collapses</b> (min scale == max scale == 1), so the
/// three outcomes reduce to the flat DRC's two and the 3D DRC AGREES with the unrolled 2D DRC — the
/// decisive oracle (a cylindrical MID board checked here equals its flat unrolled sheet, verdicts and
/// parameter separations, to the weld tier).</para>
///
/// <para><b>The load-bearing rule survives the surface</b>: the DRC reads the NETLIST to decide what
/// should connect — a short is copper of DIFFERENT nets touching; SAME-net copper touching is the
/// intended connection and is never flagged. And a net whose copper is not all joined is an unrouted
/// RATSNEST (information, not a fault).</para>
///
/// <para><b>Scope, v1.</b> Clearance, shorts and trace width — the rules a single conductive surface
/// carries. No drilled holes, board-edge or vias (a moulded surface has none in v1); a copper-to-map-
/// boundary rule is filed. Multi-shell MID (an inner moulded copper layer) is filed.</para>
/// </summary>
public static class Mid3dDrc
{
    /// <summary>Runs the 3D DRC over a MID board against a rule set (null = the flat
    /// <see cref="DrcRuleSet.Default"/>; only its clearance and trace-width fields apply on a moulded
    /// surface).</summary>
    public static Mid3dDrcReport Check(MidBoard board, DrcRuleSet? rules = null)
    {
        ArgumentNullException.ThrowIfNull(board);
        rules ??= DrcRuleSet.Default;

        var features = board.Features();
        var violations = new List<MidDrcViolation>();
        var uncertain = new List<MidDrcViolation>();

        CheckClearanceAndShorts(board, features, rules.MinCopperClearance, violations, uncertain);
        CheckTraceWidths(board, features, rules.MinTraceWidth, violations, uncertain);

        return new Mid3dDrcReport(violations, uncertain, Ratsnest(features));
    }

    // ---- clearance and shorts ------------------------------------------------

    private static void CheckClearanceAndShorts(
        MidBoard board, IReadOnlyList<MidFeature> features, double c,
        List<MidDrcViolation> violations, List<MidDrcViolation> uncertain)
    {
        for (int i = 0; i < features.Count; i++)
            for (int j = i + 1; j < features.Count; j++)
            {
                var a = features[i];
                var b = features[j];
                if (SameNet(a.Net, b.Net))
                    continue;   // the intended connection, never a short
                ClassifyPair(board, a, b, c, violations, uncertain);
            }
    }

    /// <summary>Classifies one different-net pair into Clear / Violation / Uncertain (see the class
    /// remarks). The distortion band folded in is the COMBINED band of the two features — conservative,
    /// since it covers the whole region either occupies.</summary>
    private static void ClassifyPair(
        MidBoard board, in MidFeature a, in MidFeature b, double c,
        List<MidDrcViolation> violations, List<MidDrcViolation> uncertain)
    {
        double combinedMin = Math.Min(a.MinScale, b.MinScale);
        double combinedMax = Math.Max(a.MaxScale, b.MaxScale);

        // Overlap in parameter space is a short whatever the distortion.
        if (CurvedRegion2dBoolean.Intersection([a.Region], [b.Region]).Count > 0)
        {
            violations.Add(Finding(board, MidDrcRule.Short, MidDrcVerdict.Violation, a, b,
                $"short: net {Display(a.Net)} ({a.Source}) touches net {Display(b.Net)} ({b.Source})",
                0, c, combinedMin, combinedMax));
            return;
        }
        if (!(c > 0))
            return;

        // The two parameter thresholds. Below C/maxScale it is too close even best-case (a violation);
        // at or above C/minScale it is clear even worst-case; between, the band straddles (uncertain).
        double effViolation = c / combinedMax;
        double effClear = c / combinedMin;

        // The reported separation is bisected up to a BAND-INDEPENDENT cap, so it is the SAME
        // deterministic number the flat unrolled DRC measures on the same (u, v) geometry — capping at
        // the band-dependent effClear would make a developable board's reported clearance differ from
        // its unrolling by the bisection's own last bits, breaking the bit-identity oracle.
        double reportCap = 4 * c;

        // Broad phase: a parameter AABB gap at or above the LARGEST threshold proves the pair clear.
        if (ParameterGap(a.Region.Bounds, b.Region.Bounds) >= effClear)
            return;

        if (IntersectAfterGrow(a.Region, b.Region, effViolation))
        {
            double s = Separation([a.Region], [b.Region], reportCap);
            violations.Add(Finding(board, MidDrcRule.Clearance, MidDrcVerdict.Violation, a, b,
                $"surface clearance {SurfaceSpan(s, combinedMin, combinedMax)} < {c:g3} mm: "
                + $"net {Display(a.Net)} ({a.Source}) and net {Display(b.Net)} ({b.Source})",
                s, c, combinedMin, combinedMax));
            return;
        }
        if (IntersectAfterGrow(a.Region, b.Region, effClear))
        {
            double s = Separation([a.Region], [b.Region], reportCap);
            uncertain.Add(Finding(board, MidDrcRule.Clearance, MidDrcVerdict.Uncertain, a, b,
                $"surface clearance {SurfaceSpan(s, combinedMin, combinedMax)} straddles {c:g3} mm "
                + $"(distortion {(combinedMax / combinedMin - 1) * 100:g2}%): "
                + $"net {Display(a.Net)} ({a.Source}) and net {Display(b.Net)} ({b.Source})",
                s, c, combinedMin, combinedMax));
        }
    }

    // ---- trace width ---------------------------------------------------------

    private static void CheckTraceWidths(
        MidBoard board, IReadOnlyList<MidFeature> features, double minWidth,
        List<MidDrcViolation> violations, List<MidDrcViolation> uncertain)
    {
        if (!(minWidth > 0))
            return;
        foreach (var feature in features)
        {
            // The surface width is the authored parameter width folded through the scale band. Checked
            // directly rather than re-measured, since round joins never pinch a width and an
            // opposing-wall measure under-reports on a round cap; too thin even at the largest scale is a
            // violation, thin only at the smaller scale is uncertain.
            double wParam = feature.Width;
            var at = new Vector2d(feature.Region.Bounds.Center.X, feature.Region.Bounds.Center.Y);
            if (wParam * feature.MaxScale < minWidth)
            {
                board.TryLift(at, out var surface);
                violations.Add(new MidDrcViolation(
                    MidDrcRule.TraceWidth, MidDrcVerdict.Violation,
                    $"surface width {SurfaceSpan(wParam, feature.MinScale, feature.MaxScale)} < {minWidth:g3} mm "
                    + $"at {feature.Source}",
                    at, surface, wParam, minWidth, feature.MinScale, feature.MaxScale));
            }
            else if (wParam * feature.MinScale < minWidth)
            {
                board.TryLift(at, out var surface);
                uncertain.Add(new MidDrcViolation(
                    MidDrcRule.TraceWidth, MidDrcVerdict.Uncertain,
                    $"surface width {SurfaceSpan(wParam, feature.MinScale, feature.MaxScale)} straddles "
                    + $"{minWidth:g3} mm at {feature.Source}",
                    at, surface, wParam, minWidth, feature.MinScale, feature.MaxScale));
            }
        }
    }

    // ---- ratsnest (unrouted nets) --------------------------------------------

    /// <summary>Nets whose copper is not all in one connected component — unrouted. Two features join
    /// when their (u, v) copper regions TOUCH (an exact intersection, no tolerance), the same rule the
    /// clearance check calls a short between different nets and the flat <see cref="PcbConnectivity"/>
    /// engine uses; here every feature of a net is a terminal, so a net with more than one component has
    /// copper it does not connect.</summary>
    private static IReadOnlyList<string> Ratsnest(IReadOnlyList<MidFeature> features)
    {
        var byNet = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < features.Count; i++)
        {
            if (features[i].Net is not { } net)
                continue;
            if (!byNet.TryGetValue(net, out var list))
                byNet[net] = list = [];
            list.Add(i);
        }

        var unrouted = new List<string>();
        foreach (var (net, indices) in byNet)
        {
            if (indices.Count < 2)
                continue;   // a single piece of copper cannot be disconnected
            var parent = new int[indices.Count];
            for (int i = 0; i < indices.Count; i++)
                parent[i] = i;
            for (int a = 0; a < indices.Count; a++)
                for (int b = a + 1; b < indices.Count; b++)
                {
                    if (Find(parent, a) == Find(parent, b))
                        continue;
                    var ra = features[indices[a]].Region;
                    var rb = features[indices[b]].Region;
                    if (!ParameterBoxesTouch(ra.Bounds, rb.Bounds))
                        continue;
                    if (CurvedRegion2dBoolean.Intersection([ra], [rb]).Count > 0)
                        Union(parent, a, b);
                }
            int roots = 0;
            for (int i = 0; i < indices.Count; i++)
                if (Find(parent, i) == i)
                    roots++;
            if (roots > 1)
                unrouted.Add(net);
        }
        unrouted.Sort(StringComparer.Ordinal);
        return unrouted;
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
            i = parent[i] = parent[parent[i]];
        return i;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra != rb)
            parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
    }

    // ---- geometry helpers (parameter space) ----------------------------------

    private static bool IntersectAfterGrow(CurvedRegion2d a, CurvedRegion2d b, double gap) =>
        CurvedRegion2dBoolean.Intersection(Grow([a], gap / 2), Grow([b], gap / 2)).Count > 0;

    private static IReadOnlyList<CurvedRegion2d> Grow(IReadOnlyList<CurvedRegion2d> regions, double delta) =>
        delta <= 0 ? regions : CurvedRegion2dOffset.Offset(regions, delta, OffsetJoin.Round);

    /// <summary>The parameter-space edge-to-edge separation of two disjoint region sets, capped at
    /// <paramref name="cap"/> — the largest g for which growing each by g/2 keeps them disjoint. The
    /// SAME grow-and-intersect the pass/fail rests on, so the reported number and the verdict cannot
    /// disagree; it is what the flat unrolled DRC measures on the same (u, v) geometry, so the two are
    /// bit-identical on a developable surface.</summary>
    private static double Separation(
        IReadOnlyList<CurvedRegion2d> a, IReadOnlyList<CurvedRegion2d> b, double cap)
    {
        if (CurvedRegion2dBoolean.Intersection(a, b).Count > 0)
            return 0;
        if (CurvedRegion2dBoolean.Intersection(Grow(a, cap / 2), Grow(b, cap / 2)).Count == 0)
            return cap;
        double lo = 0, hi = cap;
        for (int i = 0; i < 20; i++)   // relative 1e-6 of the cap
        {
            double m = (lo + hi) / 2;
            if (CurvedRegion2dBoolean.Intersection(Grow(a, m / 2), Grow(b, m / 2)).Count == 0)
                lo = m;
            else
                hi = m;
        }
        return lo;
    }

    private static MidDrcViolation Finding(
        MidBoard board, MidDrcRule rule, MidDrcVerdict verdict, in MidFeature a, in MidFeature b,
        string message, double measured, double required, double minScale, double maxScale)
    {
        var at = (a.Region.Bounds.Center + b.Region.Bounds.Center) * 0.5;
        var uv = new Vector2d(at.X, at.Y);
        board.TryLift(uv, out var surface);
        return new MidDrcViolation(rule, verdict, message, uv, surface, measured, required, minScale, maxScale);
    }

    private static string SurfaceSpan(double parameter, double min, double max)
    {
        double lo = parameter * min, hi = parameter * max;
        return Math.Abs(hi - lo) <= 1e-9 * Math.Max(1, hi) ? $"{lo:g3}" : $"[{lo:g3}, {hi:g3}]";
    }

    private static bool SameNet(string? a, string? b) => a is not null && b is not null && a == b;

    private static string Display(string? net) => net is null ? "(unconnected)" : $"'{net}'";

    private static double ParameterGap(in Aabb a, in Aabb b)
    {
        double dx = Math.Max(0, Math.Max(a.Min.X - b.Max.X, b.Min.X - a.Max.X));
        double dy = Math.Max(0, Math.Max(a.Min.Y - b.Max.Y, b.Min.Y - a.Max.Y));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool ParameterBoxesTouch(in Aabb a, in Aabb b) =>
        a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y;
}
