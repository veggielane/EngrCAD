using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>How a <see cref="CoupledRouter.Route"/> ended.</summary>
public enum CoupledOutcome
{
    /// <summary>The pair was routed — both traces placed at the gap, DRC-clean.</summary>
    Routed,

    /// <summary>The route is refused (the gap does not exceed the trace width, or the routed pair
    /// would violate the DRC). Nothing is produced.</summary>
    Refused,
}

/// <summary>The result of coupled-routing a differential pair — the outcome and the two generated
/// traces (the + trace on the left-normal side of the centre-line, the − trace on the other).</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Positive">The + net's trace (empty on refusal).</param>
/// <param name="Negative">The − net's trace (empty on refusal).</param>
/// <param name="Message">A description of the outcome.</param>
public readonly record struct CoupledRouteResult(
    CoupledOutcome Outcome, PcbTrace Positive, PcbTrace Negative, string Message)
{
    /// <summary>True when the pair was routed.</summary>
    public bool Ok => Outcome == CoupledOutcome.Routed;
}

/// <summary>
/// Coupled routing of a differential pair — routing the two nets TOGETHER at the target gap, rather
/// than routing each alone and checking their coupling afterwards (<see cref="DiffPairs"/>). Given a
/// shared centre-line, the two traces are its parallel offsets at ±<c>gap/2</c>, so they hold the gap
/// EXACTLY along the whole run by construction (a pair of parallel offset curves stays a constant
/// distance apart, mitred through every bend), which is what makes the result well-coupled without a
/// search.
///
/// <para><b>v1 scope.</b> The caller supplies the centre-line (typically routed as a single virtual
/// net, or drawn); this generates and DRC-checks the pair. The gap must exceed the trace width (or the
/// two traces merge) and the pair must clear the rest of the board at the general clearance — a
/// TIGHT intra-pair gap below the general copper clearance needs a diff-pair-aware DRC rule, which is
/// filed. Also filed: routing the centre-line itself (a fat-net maze), and matched-length coupled
/// routing that tunes the bend skew inside the pair (<see cref="DiffPairs.MatchSkew"/> tunes it after).</para>
/// </summary>
public static class CoupledRouter
{
    /// <summary>
    /// Routes the differential <paramref name="pair"/> as the two parallel offsets of
    /// <paramref name="centreLine"/> at ±<c>pair.TargetGapMm/2</c>, DRC-checked as a whole. The layout
    /// is not mutated; add the two returned traces with <see cref="PcbLayout.AddTrace(PcbTrace)"/>.
    /// </summary>
    /// <param name="layout">The layout the pair is routed into (its copper is the obstacle set).</param>
    /// <param name="pair">The differential pair (its two nets and the target gap).</param>
    /// <param name="centreLine">The shared centre-line the two traces straddle (≥ 2 points).</param>
    /// <param name="layer">The copper layer to route on.</param>
    /// <param name="width">The trace width (mm).</param>
    /// <param name="rules">The DRC rules; null = <see cref="DrcRuleSet.Default"/>.</param>
    public static CoupledRouteResult Route(
        PcbLayout layout, DiffPair pair, IReadOnlyList<Vector2d> centreLine,
        string layer, double width, DrcRuleSet? rules = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(centreLine);
        pair.Validate();
        rules ??= DrcRuleSet.Default;
        if (centreLine.Count < 2)
            throw new ArgumentException("A coupled route needs a centre-line of at least two points.", nameof(centreLine));
        if (!(width > 0))
            throw new ArgumentException($"The trace width must be positive (got {width:g6}).", nameof(width));

        double gap = pair.TargetGapMm;
        if (gap <= width)
            return new CoupledRouteResult(CoupledOutcome.Refused, default, default,
                $"The pair gap ({gap:g4} mm) must exceed the trace width ({width:g4} mm), or the two traces merge.");

        var pPts = OffsetPolyline(centreLine, +gap / 2);
        var nPts = OffsetPolyline(centreLine, -gap / 2);
        var pTrace = new PcbTrace(pair.PositiveNet, layer, width, pPts);
        var nTrace = new PcbTrace(pair.NegativeNet, layer, width, nPts);

        // Commit only if the whole board with BOTH traces added is DRC-clean.
        var baseModel = PcbCopperModel.FromLayout(layout);
        var copper = baseModel.Copper.ToList();
        foreach (var region in TraceGeometry.Regions(pTrace))
            copper.Add(new CopperFeature(layer, pair.PositiveNet, "coupled-p", region));
        foreach (var region in TraceGeometry.Regions(nTrace))
            copper.Add(new CopperFeature(layer, pair.NegativeNet, "coupled-n", region));
        var candidate = new PcbCopperModel(baseModel.Board, copper, baseModel.Drills, baseModel.Cavities, baseModel.Vias);

        if (PcbDrc.Check(candidate, rules).Violations.Count > 0)
            return new CoupledRouteResult(CoupledOutcome.Refused, default, default,
                "the coupled pair violates the DRC (the centre-line is too close to other copper, or the gap is "
                + "below the general clearance — a tight intra-pair gap needs a diff-pair DRC rule, which is filed).");

        return new CoupledRouteResult(CoupledOutcome.Routed, pTrace, nTrace,
            $"Routed {pair.PositiveNet}/{pair.NegativeNet} coupled at {gap:g4} mm, DRC-clean.");
    }

    // The parallel offset of a polyline by a signed perpendicular distance d (left normal positive):
    // each segment is offset, and interior vertices are the mitre intersection of the two offset
    // segments, so the offset curve stays exactly |d| from the centre-line along straight runs and
    // meets cleanly at bends.
    private static List<Vector2d> OffsetPolyline(IReadOnlyList<Vector2d> pts, double d)
    {
        var result = new List<Vector2d>(pts.Count);
        var n0 = LeftNormal(pts[0], pts[1]);
        result.Add(pts[0] + d * n0);

        for (int i = 1; i < pts.Count - 1; i++)
        {
            var na = LeftNormal(pts[i - 1], pts[i]);
            var nb = LeftNormal(pts[i], pts[i + 1]);
            var a = pts[i - 1] + d * na; var da = pts[i] - pts[i - 1];
            var b = pts[i + 1] + d * nb; var db = pts[i + 1] - pts[i];
            result.Add(LineIntersect(a, da, b, db) ?? pts[i] + d * na);   // fall back to a butt join if parallel
        }

        var nL = LeftNormal(pts[^2], pts[^1]);
        result.Add(pts[^1] + d * nL);
        return result;
    }

    private static Vector2d LeftNormal(in Vector2d a, in Vector2d b)
    {
        var d = b - a;
        double len = d.Length;
        var u = len > 0 ? d / len : new Vector2d(1, 0);
        return new Vector2d(-u.Y, u.X);
    }

    // Intersection of line (p0, dir d0) with line (p1, dir d1); null if (near-)parallel.
    private static Vector2d? LineIntersect(in Vector2d p0, in Vector2d d0, in Vector2d p1, in Vector2d d1)
    {
        double cross = d0.X * d1.Y - d0.Y * d1.X;
        if (Math.Abs(cross) < 1e-12) return null;
        double t = ((p1.X - p0.X) * d1.Y - (p1.Y - p0.Y) * d1.X) / cross;
        return p0 + t * d0;
    }
}
