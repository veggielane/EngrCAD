using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Ecad;

/// <summary>Which design rule a <see cref="MidDrcViolation"/> broke.</summary>
public enum MidDrcRule
{
    /// <summary>Copper of different nets closer than the minimum clearance ON THE SURFACE (a near
    /// miss), the clearance folded through the surface distortion.</summary>
    Clearance,

    /// <summary>Copper of different nets touching / overlapping — a SHORT, definite whatever the
    /// distortion, and the strongest failure.</summary>
    Short,

    /// <summary>A copper conductor narrower on the surface than the minimum trace width.</summary>
    TraceWidth,
}

/// <summary>What the 3D DRC could say about a pair — the honest three-valued answer the surface
/// distortion forces.</summary>
public enum MidDrcVerdict
{
    /// <summary>The rule is DEFINITELY broken: even the most favourable local scale cannot make the
    /// surface distance meet the rule.</summary>
    Violation,

    /// <summary>The DRC CANNOT CERTIFY the pair either way — the distortion band straddles the limit, so
    /// the surface clearance depends on which local scale is real. Refused rather than passed
    /// false-precise (the tamper-mesh near-tangency rule). A conservative DRC treats this as not
    /// passable, so <see cref="Mid3dDrcReport.Ok"/> is false while any uncertainty remains.</summary>
    Uncertain,
}

/// <summary>
/// One finding of the 3D DRC. It NAMES its offenders, LOCATES the finding in surface coordinates, and
/// reports the MEASURED separation against the REQUIRED SURFACE clearance with the distortion band that
/// connects them — a report that only said "fail" would be useless (the <c>DrcViolation</c> /
/// <c>PcbLayoutCheck</c> house style).
/// </summary>
/// <param name="Rule">The rule the finding is about.</param>
/// <param name="Verdict">Whether the rule is definitely broken or merely un-certifiable.</param>
/// <param name="Message">A human-readable line naming the offending nets / features.</param>
/// <param name="ParameterLocation">Where it is, in the measuring chart's (u, v) coordinates (0 for an
/// intrinsic pair measured in a per-pair local chart).</param>
/// <param name="SurfaceLocation">Where it is on the moulded surface.</param>
/// <param name="MeasuredParameter">The measured edge-to-edge separation (mm, in the measuring chart's
/// coordinates). On a global-chart board this is the (u, v) separation the flat unrolled DRC shares bit
/// for bit; on an intrinsic board it is the geodesic separation the per-pair chart measured.</param>
/// <param name="Required">The rule's SURFACE limit (mm).</param>
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
/// definite faults; the <see cref="Uncertain"/> findings are pairs the surface distortion left
/// un-certifiable (refused, not passed); the <see cref="Ratsnest"/> is the INVERSE — nets the copper
/// does not yet connect — reported as INFORMATION.
/// </summary>
public sealed record Mid3dDrcReport(
    IReadOnlyList<MidDrcViolation> Violations,
    IReadOnlyList<MidDrcViolation> Uncertain,
    IReadOnlyList<string> Ratsnest)
{
    /// <summary>True when the DRC could CERTIFY the whole board: no definite violation AND nothing left
    /// uncertain. An un-certifiable pair is not passable — a moulded board the surface cannot vouch for
    /// is not one a conservative DRC signs off (the un-routed ratsnest is not a fault, so it does not
    /// fail this).</summary>
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
/// The 3D design-rule check for a moulded (MID) board — the copper DRC's rules run on the surface, with
/// the distortion FOLDED into the clearance.
///
/// <para><b>On an INTRINSIC board (any geometry) the clearance is a GEODESIC surface distance</b> between
/// two different-net conductors, measured on the mesh. The certified broad phase is the theorem that a
/// 3D chord is never longer than a surface geodesic: a 3D edge-to-edge distance at or above the
/// clearance PROVES the surface clearance (CLEAR). A closer pair is measured tightly in a LOCAL exp-map
/// chart built around it — the geodesic-distance approximator, exact on a developable patch — with the
/// SAME grow-and-intersect the flat copper DRC uses and the local distortion folded in. The result is
/// three-valued: Clear, Violation, or <b>Uncertain</b> where the distortion band straddles the limit or
/// the curvature is too high to cover the pair in a chart at all — refused, not passed false-precise.</para>
///
/// <para><b>On a GLOBAL-chart board (a developable patch) the same rules run in the ONE exp map's
/// (u, v)</b>, which is the intrinsic geodesic exactly (an isometry), so a cylindrical board's verdicts
/// and measured separations equal its unrolled flat sheet's bit for bit — the decisive developable
/// oracle.</para>
///
/// <para><b>The load-bearing rule survives the surface</b>: the DRC reads the NETLIST — a short is copper
/// of DIFFERENT nets touching; SAME-net copper touching is the intended connection and is never flagged.
/// A net whose copper is not all joined is an unrouted RATSNEST (information, not a fault).</para>
/// </summary>
public static class Mid3dDrc
{
    /// <summary>Runs the 3D DRC over a MID board against a rule set (null = <see cref="DrcRuleSet.Default"/>;
    /// only clearance and trace-width apply on a moulded surface).</summary>
    public static Mid3dDrcReport Check(MidBoard board, DrcRuleSet? rules = null)
    {
        ArgumentNullException.ThrowIfNull(board);
        rules ??= DrcRuleSet.Default;
        return board.IsIntrinsic ? CheckIntrinsic(board, rules) : CheckGlobal(board, rules);
    }

    // ======================================================================
    //  GLOBAL-CHART PATH — the exact (u, v) DRC (bit-for-bit on a developable patch)
    // ======================================================================

    private static Mid3dDrcReport CheckGlobal(MidBoard board, DrcRuleSet rules)
    {
        var features = board.Features();
        var violations = new List<MidDrcViolation>();
        var uncertain = new List<MidDrcViolation>();

        double c = rules.MinCopperClearance;
        for (int i = 0; i < features.Count; i++)
            for (int j = i + 1; j < features.Count; j++)
            {
                var a = features[i];
                var b = features[j];
                if (SameNet(a.Net, b.Net))
                    continue;
                ClassifyChartPair(board, a, b, c, violations, uncertain);
            }

        CheckChartTraceWidths(board, features, rules.MinTraceWidth, violations, uncertain);
        return new Mid3dDrcReport(violations, uncertain, RatsnestChart(features));
    }

    private static void ClassifyChartPair(
        MidBoard board, in MidFeature a, in MidFeature b, double c,
        List<MidDrcViolation> violations, List<MidDrcViolation> uncertain)
    {
        double combinedMin = Math.Min(a.MinScale, b.MinScale);
        double combinedMax = Math.Max(a.MaxScale, b.MaxScale);
        var ra = new[] { a.Region };
        var rb = new[] { b.Region };

        var outcome = ClearanceOutcome(ra, rb, combinedMin, combinedMax, c, out double s);
        switch (outcome)
        {
            case PairOutcome.Short:
                violations.Add(FindingAt(ChartLocation(board, a, b),
                    MidDrcRule.Short, MidDrcVerdict.Violation,
                    $"short: net {Display(a.Net)} ({a.Source}) touches net {Display(b.Net)} ({b.Source})",
                    0, c, combinedMin, combinedMax));
                break;
            case PairOutcome.Violation:
                violations.Add(FindingAt(ChartLocation(board, a, b),
                    MidDrcRule.Clearance, MidDrcVerdict.Violation,
                    $"surface clearance {SurfaceSpan(s, combinedMin, combinedMax)} < {c:g3} mm: "
                    + $"net {Display(a.Net)} ({a.Source}) and net {Display(b.Net)} ({b.Source})",
                    s, c, combinedMin, combinedMax));
                break;
            case PairOutcome.Uncertain:
                uncertain.Add(FindingAt(ChartLocation(board, a, b),
                    MidDrcRule.Clearance, MidDrcVerdict.Uncertain,
                    $"surface clearance {SurfaceSpan(s, combinedMin, combinedMax)} straddles {c:g3} mm "
                    + $"(distortion {(combinedMax / combinedMin - 1) * 100:g2}%): "
                    + $"net {Display(a.Net)} ({a.Source}) and net {Display(b.Net)} ({b.Source})",
                    s, c, combinedMin, combinedMax));
                break;
        }
    }

    private static (Vector2d Uv, Vector3d Surface) ChartLocation(MidBoard board, in MidFeature a, in MidFeature b)
    {
        var at = (a.Region.Bounds.Center + b.Region.Bounds.Center) * 0.5;
        var uv = new Vector2d(at.X, at.Y);
        board.TryLift(uv, out var surface);
        return (uv, surface);
    }

    private static void CheckChartTraceWidths(
        MidBoard board, IReadOnlyList<MidFeature> features, double minWidth,
        List<MidDrcViolation> violations, List<MidDrcViolation> uncertain)
    {
        if (!(minWidth > 0))
            return;
        foreach (var feature in features)
        {
            double wParam = feature.Width;
            var at = new Vector2d(feature.Region.Bounds.Center.X, feature.Region.Bounds.Center.Y);
            board.TryLift(at, out var surface);
            if (wParam * feature.MaxScale < minWidth)
                violations.Add(new MidDrcViolation(MidDrcRule.TraceWidth, MidDrcVerdict.Violation,
                    $"surface width {SurfaceSpan(wParam, feature.MinScale, feature.MaxScale)} < {minWidth:g3} mm at {feature.Source}",
                    at, surface, wParam, minWidth, feature.MinScale, feature.MaxScale));
            else if (wParam * feature.MinScale < minWidth)
                uncertain.Add(new MidDrcViolation(MidDrcRule.TraceWidth, MidDrcVerdict.Uncertain,
                    $"surface width {SurfaceSpan(wParam, feature.MinScale, feature.MaxScale)} straddles {minWidth:g3} mm at {feature.Source}",
                    at, surface, wParam, minWidth, feature.MinScale, feature.MaxScale));
        }
    }

    private static IReadOnlyList<string> RatsnestChart(IReadOnlyList<MidFeature> features)
    {
        var byNet = GroupByNet(features.Count, i => features[i].Net);
        var unrouted = new List<string>();
        foreach (var (net, indices) in byNet)
        {
            if (indices.Count < 2)
                continue;
            var parent = MakeSet(indices.Count);
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
            if (Roots(parent) > 1)
                unrouted.Add(net);
        }
        unrouted.Sort(StringComparer.Ordinal);
        return unrouted;
    }

    // ======================================================================
    //  INTRINSIC PATH — geodesic surface distances through per-pair local charts
    // ======================================================================

    private static Mid3dDrcReport CheckIntrinsic(MidBoard board, DrcRuleSet rules)
    {
        var features = board.SurfaceFeatures();
        var violations = new List<MidDrcViolation>();
        var uncertain = new List<MidDrcViolation>();

        double c = rules.MinCopperClearance;
        for (int i = 0; i < features.Count; i++)
            for (int j = i + 1; j < features.Count; j++)
            {
                if (SameNet(features[i].Net, features[j].Net))
                    continue;
                ClassifySurfacePair(board, features[i], features[j], c, violations, uncertain);
            }

        CheckSurfaceTraceWidths(board, features, rules.MinTraceWidth, violations, uncertain);
        return new Mid3dDrcReport(violations, uncertain, RatsnestSurface(features));
    }

    private static void ClassifySurfacePair(
        MidBoard board, MidSurfaceFeature a, MidSurfaceFeature b, double c,
        List<MidDrcViolation> violations, List<MidDrcViolation> uncertain)
    {
        // Certified broad phase: a 3D chord is never longer than a surface geodesic, so a chord edge-to-
        // edge distance at or above the clearance PROVES the surface clearance, whatever the curvature.
        double chordCentres = SurfaceGeometry.PolylineDistance(a.Polyline, b.Polyline);
        double chordEdge = chordCentres - a.Width / 2 - b.Width / 2;
        if (c > 0 && chordEdge >= c)
            return;   // CLEAR, certified

        // The pair is 3D-close: measure it in a local exp-map chart. If the surface is so curved that no
        // chart covers both, the pair cannot be certified — a conservative Uncertain.
        var location = board.Surface.Locate((a.Centre.Position + b.Centre.Position) * 0.5);
        if (!TryMeasurePair(board, a, b, location, chordCentres, c,
                out var ra, out var rb, out double minScale, out double maxScale))
        {
            uncertain.Add(FindingAt((Vector2d.Zero, location.Position),
                MidDrcRule.Clearance, MidDrcVerdict.Uncertain,
                $"surface too curved to certify the clearance between net {Display(a.Net)} ({a.Source}) "
                + $"and net {Display(b.Net)} ({b.Source}) — no exp-map chart covers the pair; "
                + $"chord edge-to-edge {chordEdge:g3} mm < {c:g3} mm",
                chordEdge, c, 1, 1));
            return;
        }

        var outcome = ClearanceOutcome(ra, rb, minScale, maxScale, c, out double s);
        switch (outcome)
        {
            case PairOutcome.Short:
                violations.Add(FindingAt((Vector2d.Zero, location.Position),
                    MidDrcRule.Short, MidDrcVerdict.Violation,
                    $"short: net {Display(a.Net)} ({a.Source}) touches net {Display(b.Net)} ({b.Source})",
                    0, c, minScale, maxScale));
                break;
            case PairOutcome.Violation:
                violations.Add(FindingAt((Vector2d.Zero, location.Position),
                    MidDrcRule.Clearance, MidDrcVerdict.Violation,
                    $"surface clearance {SurfaceSpan(s, minScale, maxScale)} < {c:g3} mm: "
                    + $"net {Display(a.Net)} ({a.Source}) and net {Display(b.Net)} ({b.Source})",
                    s, c, minScale, maxScale));
                break;
            case PairOutcome.Uncertain:
                uncertain.Add(FindingAt((Vector2d.Zero, location.Position),
                    MidDrcRule.Clearance, MidDrcVerdict.Uncertain,
                    $"surface clearance {SurfaceSpan(s, minScale, maxScale)} straddles {c:g3} mm "
                    + $"(distortion {(maxScale / minScale - 1) * 100:g2}%): "
                    + $"net {Display(a.Net)} ({a.Source}) and net {Display(b.Net)} ({b.Source})",
                    s, c, minScale, maxScale));
                break;
        }
    }

    /// <summary>Projects both features into a local chart around the pair and reads the chart's local
    /// scale band. Grows the chart until it covers both (a couple of tries); returns false when even a
    /// generous chart cannot — the surface is too curved to certify.</summary>
    private static bool TryMeasurePair(
        MidBoard board, MidSurfaceFeature a, MidSurfaceFeature b, in SurfacePoint centre,
        double chordCentres, double c,
        out CurvedRegion2d[] ra, out CurvedRegion2d[] rb, out double minScale, out double maxScale)
    {
        ra = [];
        rb = [];
        minScale = 1;
        maxScale = 1;
        double span = Math.Max(a.Bounds.Size.Length, b.Bounds.Size.Length);
        // The chart need only cover the two features and the gap between them — kept as TIGHT as possible,
        // since an over-large chart on a small closed surface wraps onto itself and its (u, v) degenerates.
        double baseR = chordCentres + Math.Max(a.Width, b.Width) + 0.5 * span + 2 * board.LongestEdge();

        foreach (double mult in ChartGrowth)
        {
            var chart = board.Surface.Chart(centre, baseR * mult);
            if (chart.IsEmpty)
                continue;
            if (!TryProjectFeature(chart, a, out ra) || !TryProjectFeature(chart, b, out rb))
                continue;
            if (!chart.TryProject(centre, out var uvCentre))
                continue;
            // The distortion that matters to a clearance is the exp map's scale variation over the GAP
            // being measured, not over a feature: probe a cross whose half-span covers the separation
            // between the two projected copper regions (plus their own size). On a developable patch the
            // band collapses (exact); where the clearance is comparable to the curvature radius it spreads.
            var ca = Bounds(ra).Center;
            var cb = Bounds(rb).Center;
            double sep = new Vector2d(ca.X, ca.Y).DistanceTo(new Vector2d(cb.X, cb.Y));
            double probeSpan = Math.Max(Math.Max(sep, span), baseR * mult * 0.02);
            (minScale, maxScale) = chart.ScaleBand(uvCentre, probeSpan);
            FoldFeatureScale(chart, a, probeSpan, ref minScale, ref maxScale);
            FoldFeatureScale(chart, b, probeSpan, ref minScale, ref maxScale);
            // A degenerate band (a chart that wrapped a tightly-curved patch) is not a measurement: the
            // surface is too curved at this scale to certify, so refuse cleanly rather than report
            // nonsense numbers. A bigger chart only wraps worse, so this is the answer for the pair.
            if (minScale > 0.5 && maxScale < 2 && maxScale <= 2.5 * minScale)
                return true;
            return false;
        }
        return false;
    }

    private static void FoldFeatureScale(LocalExpChart chart, MidSurfaceFeature f, double span, ref double min, ref double max)
    {
        if (!chart.TryProject(f.Centre, out var uv))
            return;
        var (fmin, fmax) = chart.ScaleBand(uv, Math.Max(f.Width, span * 0.25));
        min = Math.Min(min, fmin);
        max = Math.Max(max, fmax);
    }

    private static bool TryProjectFeature(LocalExpChart chart, MidSurfaceFeature f, out CurvedRegion2d[] regions)
    {
        regions = [];
        if (f.CentreLine.Count == 1)
        {
            if (!chart.TryProject(f.CentreLine[0], out var uv))
                return false;
            regions = [CurvedRegion2d.Disc(uv, f.Width / 2)];
            return true;
        }
        var pts = new Vector2d[f.CentreLine.Count];
        for (int i = 0; i < pts.Length; i++)
        {
            if (!chart.TryProject(f.CentreLine[i], out pts[i]))
                return false;
            if (double.IsNaN(pts[i].X) || double.IsInfinity(pts[i].X))
                return false;
        }
        var strokes = Region2dOffset.Stroke(pts, f.Width, StrokeCap.Round, OffsetJoin.Round);
        var list = new List<CurvedRegion2d>(strokes.Count);
        foreach (var stroke in strokes)
            list.Add(CurvedRegion2d.FromRegion(stroke));
        if (list.Count == 0)
            return false;
        regions = [.. list];
        return true;
    }

    private static void CheckSurfaceTraceWidths(
        MidBoard board, IReadOnlyList<MidSurfaceFeature> features, double minWidth,
        List<MidDrcViolation> violations, List<MidDrcViolation> uncertain)
    {
        if (!(minWidth > 0))
            return;
        foreach (var feature in features)
        {
            var (min, max) = board.LocalScaleBandAround(feature.Centre, feature.Width, feature.Bounds.Size.Length);
            double w = feature.Width;
            if (w * max < minWidth)
                violations.Add(new MidDrcViolation(MidDrcRule.TraceWidth, MidDrcVerdict.Violation,
                    $"surface width {SurfaceSpan(w, min, max)} < {minWidth:g3} mm at {feature.Source}",
                    Vector2d.Zero, feature.Centre.Position, w, minWidth, min, max));
            else if (w * min < minWidth)
                uncertain.Add(new MidDrcViolation(MidDrcRule.TraceWidth, MidDrcVerdict.Uncertain,
                    $"surface width {SurfaceSpan(w, min, max)} straddles {minWidth:g3} mm at {feature.Source}",
                    Vector2d.Zero, feature.Centre.Position, w, minWidth, min, max));
        }
    }

    /// <summary>Same-net copper whose 3D footprints touch (edge-to-edge distance ≤ the weld tier) is
    /// joined — a trace endpoint lands exactly on its pad, so the two coincide there. Copper the union
    /// leaves in more than one piece is an unrouted net (information, not a fault).</summary>
    private static IReadOnlyList<string> RatsnestSurface(IReadOnlyList<MidSurfaceFeature> features)
    {
        var byNet = GroupByNet(features.Count, i => features[i].Net);
        var unrouted = new List<string>();
        foreach (var (net, indices) in byNet)
        {
            if (indices.Count < 2)
                continue;
            var parent = MakeSet(indices.Count);
            for (int a = 0; a < indices.Count; a++)
                for (int b = a + 1; b < indices.Count; b++)
                {
                    if (Find(parent, a) == Find(parent, b))
                        continue;
                    var fa = features[indices[a]];
                    var fb = features[indices[b]];
                    double edge = SurfaceGeometry.PolylineDistance(fa.Polyline, fb.Polyline)
                        - fa.Width / 2 - fb.Width / 2;
                    double weld = 1e-6 * Math.Max(1, Math.Max(fa.Bounds.Size.Length, fb.Bounds.Size.Length));
                    if (edge <= weld)
                        Union(parent, a, b);
                }
            if (Roots(parent) > 1)
                unrouted.Add(net);
        }
        unrouted.Sort(StringComparer.Ordinal);
        return unrouted;
    }

    // ======================================================================
    //  SHARED classifier and helpers
    // ======================================================================

    private static readonly double[] ChartGrowth = [1.5, 3.0, 6.0];

    private enum PairOutcome { Clear, Short, Violation, Uncertain }

    /// <summary>The one grow-and-intersect classifier both paths share: two copper region sets in a
    /// chart's (u, v), a combined scale band and a surface clearance. On the global path each set is one
    /// region and the numbers are bit-identical to the flat unrolled DRC.</summary>
    private static PairOutcome ClearanceOutcome(
        IReadOnlyList<CurvedRegion2d> ra, IReadOnlyList<CurvedRegion2d> rb,
        double combinedMin, double combinedMax, double c, out double separation)
    {
        separation = 0;
        if (CurvedRegion2dBoolean.Intersection(ra, rb).Count > 0)
            return PairOutcome.Short;
        if (!(c > 0))
            return PairOutcome.Clear;

        double effViolation = c / combinedMax;
        double effClear = c / combinedMin;
        double reportCap = 4 * c;

        if (ParameterGap(Bounds(ra), Bounds(rb)) >= effClear)
            return PairOutcome.Clear;
        if (IntersectAfterGrow(ra, rb, effViolation))
        {
            separation = Separation(ra, rb, reportCap);
            return PairOutcome.Violation;
        }
        if (IntersectAfterGrow(ra, rb, effClear))
        {
            separation = Separation(ra, rb, reportCap);
            return PairOutcome.Uncertain;
        }
        return PairOutcome.Clear;
    }

    private static bool IntersectAfterGrow(IReadOnlyList<CurvedRegion2d> a, IReadOnlyList<CurvedRegion2d> b, double gap) =>
        CurvedRegion2dBoolean.Intersection(Grow(a, gap / 2), Grow(b, gap / 2)).Count > 0;

    private static IReadOnlyList<CurvedRegion2d> Grow(IReadOnlyList<CurvedRegion2d> regions, double delta) =>
        delta <= 0 ? regions : CurvedRegion2dOffset.Offset(regions, delta, OffsetJoin.Round);

    private static double Separation(
        IReadOnlyList<CurvedRegion2d> a, IReadOnlyList<CurvedRegion2d> b, double cap)
    {
        if (CurvedRegion2dBoolean.Intersection(a, b).Count > 0)
            return 0;
        if (CurvedRegion2dBoolean.Intersection(Grow(a, cap / 2), Grow(b, cap / 2)).Count == 0)
            return cap;
        double lo = 0, hi = cap;
        for (int i = 0; i < 20; i++)
        {
            double m = (lo + hi) / 2;
            if (CurvedRegion2dBoolean.Intersection(Grow(a, m / 2), Grow(b, m / 2)).Count == 0)
                lo = m;
            else
                hi = m;
        }
        return lo;
    }

    private static Aabb Bounds(IReadOnlyList<CurvedRegion2d> regions)
    {
        var box = Aabb.Empty;
        foreach (var r in regions)
            box = box.Union(r.Bounds);
        return box;
    }

    private static MidDrcViolation FindingAt(
        (Vector2d Uv, Vector3d Surface) at, MidDrcRule rule, MidDrcVerdict verdict,
        string message, double measured, double required, double minScale, double maxScale) =>
        new(rule, verdict, message, at.Uv, at.Surface, measured, required, minScale, maxScale);

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

    // ---- union-find ----------------------------------------------------------

    private static Dictionary<string, List<int>> GroupByNet(int count, Func<int, string?> netOf)
    {
        var byNet = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            if (netOf(i) is not { } net)
                continue;
            if (!byNet.TryGetValue(net, out var list))
                byNet[net] = list = [];
            list.Add(i);
        }
        return byNet;
    }

    private static int[] MakeSet(int n)
    {
        var parent = new int[n];
        for (int i = 0; i < n; i++)
            parent[i] = i;
        return parent;
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

    private static int Roots(int[] parent)
    {
        int roots = 0;
        for (int i = 0; i < parent.Length; i++)
            if (Find(parent, i) == i)
                roots++;
        return roots;
    }
}
