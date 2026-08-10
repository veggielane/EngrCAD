using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Ecad;

/// <summary>Which design rule a <see cref="DrcViolation"/> broke.</summary>
public enum DrcRule
{
    /// <summary>Copper of different nets closer than the minimum clearance (a near miss).</summary>
    Clearance,

    /// <summary>Copper of different nets actually touching or overlapping — a SHORT, a stronger
    /// failure than a clearance miss.</summary>
    Short,

    /// <summary>A drilled pad's copper ring is thinner than the minimum annular ring.</summary>
    AnnularRing,

    /// <summary>A drilled hole is closer than the minimum to OTHER-net copper (cross-layer).</summary>
    DrillToCopper,

    /// <summary>Copper is closer than the minimum to the board outline.</summary>
    CopperToEdge,

    /// <summary>A copper conductor is narrower than the minimum trace width.</summary>
    TraceWidth,

    /// <summary>A copper corner is sharper than the acid-trap threshold.</summary>
    AcuteAngle,

    /// <summary>Copper of another component is closer than the minimum to an embedded component's
    /// cavity wall (the milled edge), on the layer the cavity is seated on.</summary>
    CavityClearance,

    /// <summary>Two vias' drills are closer than the minimum via-to-via web (a manufacturing
    /// spacing between drilled holes, applied to all via pairs regardless of net).</summary>
    ViaToVia,
}

/// <summary>
/// One design-rule violation the copper DRC found. It NAMES its offender, LOCATES it, and reports
/// the MEASURED value against the REQUIRED one — a report that only said "fail" would be useless
/// (the <c>PcbLayoutCheck</c> / <c>SchematicCheck</c> house style).
/// </summary>
/// <param name="Rule">The rule that was broken.</param>
/// <param name="Layer">The copper layer, or the empty string for a cross-layer rule
/// (drill-to-copper) or a rule with no single layer.</param>
/// <param name="Message">A human-readable line naming the offending nets / features.</param>
/// <param name="Location">Where it is, in board-local 2D coordinates (mm).</param>
/// <param name="Measured">The value found (mm, or degrees for <see cref="DrcRule.AcuteAngle"/>).</param>
/// <param name="Required">The rule's limit (mm, or degrees).</param>
public readonly record struct DrcViolation(
    DrcRule Rule, string Layer, string Message, Vector2d Location, double Measured, double Required)
{
    /// <summary>The violation as one line, prefixed by its rule.</summary>
    public override string ToString()
    {
        string where = Layer.Length > 0 ? $" [{Layer}]" : "";
        return $"{Rule}{where}: {Message} at ({Location.X:g4}, {Location.Y:g4})";
    }
}

/// <summary>
/// The result of <see cref="PcbDrc.Check(PcbCopperModel, DrcRuleSet?)"/> — the geometric DRC of a
/// board's copper. The <see cref="Violations"/> are the faults; the <see cref="Ratsnest"/> is the
/// INVERSE — signal nets the copper does not yet connect — reported as INFORMATION, not a fault,
/// because a bare-pads board (before routing) is not "wrong", it is unrouted.
/// </summary>
/// <param name="Violations">Every rule broken, each naming and locating its offender.</param>
/// <param name="Ratsnest">Signal nets whose pads the copper does not all connect (an unrouted
/// count) — expected before stage-5 routing, so informational rather than a violation.</param>
public sealed record DrcReport(
    IReadOnlyList<DrcViolation> Violations,
    IReadOnlyList<string> Ratsnest)
{
    /// <summary>True when there are no VIOLATIONS. An unrouted net is not a violation, so the
    /// ratsnest does not fail the check (a board with copper laid out but not yet routed is DRC
    /// clean and has a ratsnest).</summary>
    public bool Ok => Violations.Count == 0;

    /// <summary>The violations of one rule.</summary>
    public IEnumerable<DrcViolation> OfRule(DrcRule rule) => Violations.Where(v => v.Rule == rule);

    /// <summary>Whether any violation of a given rule was found.</summary>
    public bool Has(DrcRule rule) => Violations.Any(v => v.Rule == rule);

    /// <summary>One line per violation, then one line per unrouted net.</summary>
    public IEnumerable<string> Messages
    {
        get
        {
            foreach (var violation in Violations)
                yield return violation.ToString();
            foreach (var net in Ratsnest)
                yield return $"unrouted (ratsnest): net '{net}' is not fully connected by copper";
        }
    }

    /// <summary>A human-readable report.</summary>
    public override string ToString() =>
        Ok && Ratsnest.Count == 0
            ? "DRC clean"
            : string.Join(Environment.NewLine, Messages)
              + (Ok ? Environment.NewLine + $"({Violations.Count} violations, {Ratsnest.Count} unrouted)"
                    : "");
}

/// <summary>
/// The copper design-rule check: an exact 2D-region query over a board's copper against a
/// <see cref="DrcRuleSet"/>. Every rule is a region query the exact 2D machinery answers with no
/// tolerance — clearance and shorts by growing net copper and testing an EMPTY intersection (the
/// tamper-mesh construction, where an empty intersection PROVES the clearance), annular ring by
/// arithmetic, drill-to-copper / copper-to-edge by offset-and-overlap, trace width by the opposing-
/// wall thickness, and acute angles by a corner-angle scan.
///
/// <para><b>The load-bearing rule</b> is that the DRC reads the NETLIST to decide what SHOULD
/// connect: a SHORT is copper of DIFFERENT nets electrically connected; copper of the SAME net
/// touching is the INTENDED connection and is NEVER flagged (the one-declaration identity doing
/// real work — the geometry and the netlist must agree).</para>
///
/// <para><b>Multi-layer.</b> Clearance, shorts, trace width and acute angles are PER LAYER (top
/// copper does not clear against bottom copper in-plane). Drill-to-copper is CROSS-LAYER, since a
/// drill goes through the whole stack.</para>
///
/// <para><b>Traces arrive in stage 5.</b> Trace width and the acute-angle / acid-trap rule genuinely
/// want conductors; the copper today is PADS, so those rules run on whatever copper a layer carries
/// (a deliberately-thin pad, a sharp corner) and fully ENGAGE once routing produces traces — a
/// trace is a stroked centre-line region through the same <see cref="CopperFeature"/> type, so the
/// DRC needs no change to reach it.</para>
/// </summary>
public static class PcbDrc
{
    /// <summary>Runs the whole DRC over a placed layout against a rule set (null = the defaults).</summary>
    public static DrcReport Check(PcbLayout layout, DrcRuleSet? rules = null) =>
        Check(PcbCopperModel.FromLayout(layout), rules);

    /// <summary>Runs the whole DRC over a copper model against a rule set (null = the defaults).</summary>
    public static DrcReport Check(PcbCopperModel model, DrcRuleSet? rules = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        rules ??= DrcRuleSet.Default;

        var violations = new List<DrcViolation>();
        CheckClearanceAndShorts(model, rules, violations);
        CheckAnnularRings(model, rules, violations);
        CheckDrillToCopper(model, rules, violations);
        CheckCopperToEdge(model, rules, violations);
        CheckTraceWidth(model, rules, violations);
        CheckAcuteAngles(model, rules, violations);
        CheckCavityClearance(model, rules, violations);
        CheckViaToVia(model, rules, violations);
        return new DrcReport(violations, Ratsnest(model));
    }

    /// <summary>
    /// The INCREMENTAL entry a stage-5 router costs a candidate route with: the violations a single
    /// <paramref name="candidate"/> copper feature would introduce against the model as it stands —
    /// clearance and shorts against existing copper on its own layer, copper-to-edge, trace width,
    /// acute angles, and drill-to-copper against every OTHER-net drilled hole. It does NOT re-run the
    /// whole board (that is <see cref="Check(PcbCopperModel, DrcRuleSet?)"/>); it answers "does adding
    /// THIS copper violate anything". The via overload (<see cref="Violates(PcbCopperModel, PlacedVia,
    /// DrcRuleSet?)"/>) adds the drill / via rules a via carries.
    /// </summary>
    public static DrcReport Violates(PcbCopperModel model, CopperFeature candidate, DrcRuleSet? rules = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        rules ??= DrcRuleSet.Default;

        var violations = new List<DrcViolation>();
        var candidateGroup = NetGroup.One(candidate);
        double c = rules.MinCopperClearance;
        foreach (var group in BuildGroups(model.Copper.Where(f => f.Layer == candidate.Layer)))
        {
            // Same-net copper is the intended connection — the candidate joining it is not a short.
            if (SameNet(candidate.Net, group.Net))
                continue;
            ComparePair(candidateGroup, group, candidate.Layer, c, violations);
        }

        CheckFeatureAgainstEdge(model.Board, candidate, rules, violations);
        CheckFeatureWidth(candidate, rules, violations);
        CheckFeatureAcuteAngles(candidate, rules, violations);

        // Drill-to-copper: an OTHER-net drilled hole coming within the minimum of this new copper.
        double dd = rules.MinDrillToCopper;
        if (dd > 0)
            foreach (var drill in model.Drills)
            {
                var grownHole = Grow([CurvedRegion2d.Disc(drill.Center, drill.Diameter / 2)], dd);
                var hit = DrillCopperHit(grownHole, drill.Center, drill.Diameter / 2, drill.Source, drill.Net, candidate, dd);
                if (hit is not null)
                    violations.Add(new DrcViolation(
                        DrcRule.DrillToCopper, "",
                        $"drill-to-copper {Separation([CurvedRegion2d.Disc(drill.Center, drill.Diameter / 2)], [candidate.Region], dd):g3}"
                        + $" < {dd:g3} mm: {drill.Source} hole near {candidate.Source}",
                        CenterOf(hit), Separation([CurvedRegion2d.Disc(drill.Center, drill.Diameter / 2)], [candidate.Region], dd), dd));
            }
        return new DrcReport(violations, []);
    }

    /// <summary>
    /// The INCREMENTAL via entry a router costs a candidate via with — the drill / via rules a single
    /// <paramref name="candidate"/> via carries against the model as it stands: its annular ring, its
    /// drill against OTHER-net copper (drill-to-copper), and its drill against every existing via
    /// (the via-to-via web). The via's PAD copper clearance / edge / width is checked by adding its
    /// per-layer pad features through <see cref="Violates(PcbCopperModel, CopperFeature, DrcRuleSet?)"/>,
    /// so between the two a router asks the DRC's OWN constructions rather than restating them.
    /// </summary>
    public static DrcReport Violates(PcbCopperModel model, PlacedVia candidate, DrcRuleSet? rules = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        rules ??= DrcRuleSet.Default;
        var violations = new List<DrcViolation>();

        if (candidate.AnnularRing < rules.MinAnnularRing)
            violations.Add(new DrcViolation(
                DrcRule.AnnularRing, "",
                $"annular ring {candidate.AnnularRing:g3} < {rules.MinAnnularRing:g3} mm at {candidate.Source}",
                candidate.Center, candidate.AnnularRing, rules.MinAnnularRing));

        double dd = rules.MinDrillToCopper;
        if (dd > 0)
        {
            var hole = CurvedRegion2d.Disc(candidate.Center, candidate.DrillDiameter / 2);
            var grownHole = Grow([hole], dd);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var feature in model.Copper)
            {
                if (!seen.Add(feature.Source))
                    continue;
                var hit = DrillCopperHit(grownHole, candidate.Center, candidate.DrillDiameter / 2,
                    candidate.Source, candidate.Net, feature, dd);
                if (hit is not null)
                    violations.Add(new DrcViolation(
                        DrcRule.DrillToCopper, "",
                        $"drill-to-copper {Separation([hole], [feature.Region], dd):g3} < {dd:g3} mm: "
                        + $"{candidate.Source} hole near {feature.Source}",
                        CenterOf(hit), Separation([hole], [feature.Region], dd), dd));
            }
        }

        double m = rules.MinViaToVia;
        if (m > 0)
            foreach (var ev in model.Vias)
                if (ViaWebViolation(candidate, ev, m) is { } web)
                    violations.Add(web);

        return new DrcReport(violations, []);
    }

    // ---- clearance and shorts (the core rule) --------------------------------

    private static void CheckClearanceAndShorts(PcbCopperModel model, DrcRuleSet rules, List<DrcViolation> v)
    {
        double c = rules.MinCopperClearance;
        foreach (var layer in model.Layers)
        {
            var groups = BuildGroups(model.Copper.Where(f => f.Layer == layer));
            for (int i = 0; i < groups.Count; i++)
                for (int j = i + 1; j < groups.Count; j++)
                    ComparePair(groups[i], groups[j], layer, c, v);
        }
    }

    /// <summary>Compares two different-net copper groups: a positive-area overlap is a SHORT,
    /// otherwise an overlap once both are grown by half the clearance is a CLEARANCE miss (the
    /// tamper-mesh construction — the empty intersection is the clearance proof).</summary>
    private static void ComparePair(NetGroup a, NetGroup b, string layer, double c, List<DrcViolation> v)
    {
        // Broad phase: the AABBs (a superset of the copper) are more than the clearance apart, so
        // the copper is too — proven clear, skip. Sound because an AABB gap bounds the region gap.
        if (AabbGap(a.Bounds, b.Bounds) >= c)
            return;

        var overlap = CurvedRegion2dBoolean.Intersection(a.Copper, b.Copper);
        if (overlap.Count > 0)
        {
            var (fa, fb) = NearestFeatures(a, b);
            v.Add(new DrcViolation(
                DrcRule.Short, layer,
                $"short: net {a.Display} ({fa}) touches net {b.Display} ({fb})",
                CenterOf(overlap), 0, c));
            return;
        }

        if (c <= 0)
            return;
        var grown = CurvedRegion2dBoolean.Intersection(Grow(a.Copper, c / 2), Grow(b.Copper, c / 2));
        if (grown.Count > 0)
        {
            double measured = Separation(a.Copper, b.Copper, c);
            var (fa, fb) = NearestFeatures(a, b);
            v.Add(new DrcViolation(
                DrcRule.Clearance, layer,
                $"clearance {measured:g3} < {c:g3} mm: net {a.Display} ({fa}) and net {b.Display} ({fb})",
                CenterOf(grown), measured, c));
        }
    }

    // ---- annular ring (arithmetic) -------------------------------------------

    private static void CheckAnnularRings(PcbCopperModel model, DrcRuleSet rules, List<DrcViolation> v)
    {
        foreach (var drill in model.Drills)
        {
            if (!(drill.PadDiameter > 0))   // a bare hole (no pad) carries no annular-ring rule
                continue;
            double ring = (drill.PadDiameter - drill.Diameter) / 2;
            if (ring < rules.MinAnnularRing)
                v.Add(new DrcViolation(
                    DrcRule.AnnularRing, "",
                    $"annular ring {ring:g3} < {rules.MinAnnularRing:g3} mm at {drill.Source}",
                    drill.Center, ring, rules.MinAnnularRing));
        }
    }

    // ---- drill-to-copper (cross-layer) ---------------------------------------

    private static void CheckDrillToCopper(PcbCopperModel model, DrcRuleSet rules, List<DrcViolation> v)
    {
        double d = rules.MinDrillToCopper;
        if (!(d > 0))
            return;
        foreach (var drill in model.Drills)
        {
            var hole = CurvedRegion2d.Disc(drill.Center, drill.Diameter / 2);
            var grownHole = Grow([hole], d);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var feature in model.Copper)
            {
                if (!seen.Add(feature.Source))   // one check per copper source (it repeats per layer)
                    continue;
                var hit = DrillCopperHit(grownHole, drill.Center, drill.Diameter / 2, drill.Source, drill.Net, feature, d);
                if (hit is null)
                    continue;
                double measured = Separation([hole], [feature.Region], d);
                v.Add(new DrcViolation(
                    DrcRule.DrillToCopper, "",
                    $"drill-to-copper {measured:g3} < {d:g3} mm: {drill.Source} hole near {feature.Source}"
                    + (feature.Net is null ? "" : $" (net {feature.Net})"),
                    CenterOf(hit), measured, d));
            }
        }
    }

    /// <summary>The shared drill-to-copper primitive: whether a drilled hole (already grown by the
    /// minimum <paramref name="d"/>) comes within it of a copper feature of a DIFFERENT net (never
    /// its own pad, and same-net copper is intended). Returns the overlap region (for locating the
    /// violation) or null. One construction used by the whole-board check and both incremental
    /// entries, so the router and the DRC cannot disagree about it.</summary>
    private static IReadOnlyList<CurvedRegion2d>? DrillCopperHit(
        IReadOnlyList<CurvedRegion2d> grownHole, in Vector2d center, double radius,
        string drillSource, string? drillNet, in CopperFeature feature, double d)
    {
        if (feature.Source == drillSource || SameNet(drillNet, feature.Net))
            return null;
        if (AabbGap(center, radius, feature.Region.Bounds) >= d)
            return null;
        var hit = CurvedRegion2dBoolean.Intersection(grownHole, [feature.Region]);
        return hit.Count > 0 ? hit : null;
    }

    // ---- copper-to-board-edge ------------------------------------------------

    private static void CheckCopperToEdge(PcbCopperModel model, DrcRuleSet rules, List<DrcViolation> v)
    {
        double d = rules.MinCopperToEdge;
        if (!(d > 0))
            return;

        var board = new Region2d([.. model.Board.OutlinePoints]);
        // The safe interior: the outline shrunk by the edge clearance. Copper must lie entirely
        // within it. A polygon's inward offset is exact, so this is exact for a polygonal outline.
        var inner = Region2dOffset.Offset(board, -d)
            .Select(CurvedRegion2d.FromRegion).ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in model.Copper)
        {
            if (!seen.Add(feature.Source))
                continue;
            // Any part of the copper NOT inside the safe interior is too close to (or past) the edge.
            IReadOnlyList<CurvedRegion2d> outside = [feature.Region];
            foreach (var region in inner)
            {
                outside = CurvedRegion2dBoolean.Difference(outside, [region]);
                if (outside.Count == 0)
                    break;
            }
            if (outside.Count == 0)
                continue;

            double measured = DistanceToOutline(feature.Region, model.Board.OutlinePoints);
            v.Add(new DrcViolation(
                DrcRule.CopperToEdge, feature.Layer,
                $"copper-to-edge {measured:g3} < {d:g3} mm at {feature.Source}",
                CenterOf(outside), measured, d));
        }
    }

    // ---- copper-to-cavity-wall (embedded parts) ------------------------------

    /// <summary>An embedded component sits in a milled cavity; copper of OTHER components on the
    /// cavity's seat layer must clear the cavity wall (a milled edge) by the copper-to-edge minimum.
    /// The embedded component's OWN pads sit inside their own cavity by construction and are exempt.
    /// v1 checks the SEAT layer only — the layer the pads occupy; spanning layers is a later
    /// concern (the microvia-stitching v1 boundary).</summary>
    private static void CheckCavityClearance(PcbCopperModel model, DrcRuleSet rules, List<DrcViolation> v)
    {
        double d = rules.MinCopperToEdge;   // a cavity wall is a milled edge, like the board edge
        if (!(d > 0) || model.Cavities.Count == 0)
            return;

        foreach (var cavity in model.Cavities)
        {
            var corners = new List<Vector2d>();
            foreach (var loop in cavity.Footprint.AllLoops())
                foreach (var edge in loop)
                    corners.Add(edge.Start);
            if (corners.Count < 3)
                continue;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var feature in model.Copper)
            {
                if (feature.Layer != cavity.Layer)
                    continue;
                // The embedded part's own pads are inside its own cavity by construction — exempt.
                if (feature.Source == cavity.Source
                    || feature.Source.StartsWith(cavity.Source + ".", StringComparison.Ordinal))
                    continue;
                if (!seen.Add(feature.Source))
                    continue;
                if (AabbGap(feature.Region.Bounds, cavity.Footprint.Bounds) >= d)
                    continue;
                double measured = DistanceToOutline(feature.Region, corners);
                if (measured < d)
                    v.Add(new DrcViolation(
                        DrcRule.CavityClearance, cavity.Layer,
                        $"copper-to-cavity {measured:g3} < {d:g3} mm: {feature.Source} near the cavity for "
                        + $"{cavity.Source}",
                        CenterOf([feature.Region]), measured, d));
            }
        }
    }

    // ---- via-to-via (the drill web between two vias) -------------------------

    /// <summary>Two vias whose DRILLS are closer than the minimum via-to-via web are flagged. The
    /// convention is stated: this is the minimum web between two drilled holes, so it applies to
    /// ALL via pairs regardless of net — a drill web is a manufacturing minimum, and same-net-ness
    /// does not relax it (electrical clearance between DIFFERENT-net via PADS is already the general
    /// copper-clearance rule, which via annular pads ride as ordinary copper). Measured drill-edge to
    /// drill-edge across the whole board (a drill goes through the stack, so it is not per-layer).
    /// The web is proven by the SAME grow-and-intersect the clearance rule uses — grow each drill
    /// disc by half the minimum and require the grown discs disjoint — so a web AT the limit passes
    /// robustly (a tangent contact is no region), matching the tamper-mesh clearance construction.</summary>
    private static void CheckViaToVia(PcbCopperModel model, DrcRuleSet rules, List<DrcViolation> v)
    {
        double m = rules.MinViaToVia;
        if (!(m > 0) || model.Vias.Count < 2)
            return;
        var vias = model.Vias;
        for (int i = 0; i < vias.Count; i++)
            for (int j = i + 1; j < vias.Count; j++)
                if (ViaWebViolation(vias[i], vias[j], m) is { } web)
                    v.Add(web);
    }

    /// <summary>The shared via-to-via primitive: whether two vias' drills are closer than the minimum
    /// web <paramref name="m"/> (proven by the SAME grow-and-intersect the clearance rule uses — grow
    /// each drill disc by half the minimum and require the grown discs disjoint, so a web AT the limit
    /// passes robustly). Returns the violation or null. One construction, shared by the whole-board
    /// check and the incremental via entry.</summary>
    private static DrcViolation? ViaWebViolation(in PlacedVia a, in PlacedVia b, double m)
    {
        double ra = a.DrillDiameter / 2, rb = b.DrillDiameter / 2;
        // Broad phase: two drill centres far enough apart are clear (a sound skip — it only ever
        // skips a definitely-clear pair, so a near-limit pair falls to the exact check).
        if (a.Center.DistanceTo(b.Center) - ra - rb >= m)
            return null;
        var da = new[] { CurvedRegion2d.Disc(a.Center, ra) };
        var db = new[] { CurvedRegion2d.Disc(b.Center, rb) };
        if (CurvedRegion2dBoolean.Intersection(Grow(da, m / 2), Grow(db, m / 2)).Count == 0)
            return null;   // the grown discs are disjoint — the web is at least the minimum
        double web = Separation(da, db, m);
        return new DrcViolation(
            DrcRule.ViaToVia, "",
            $"via-to-via web {web:g3} < {m:g3} mm: {a.Source} (net {a.Net}) and {b.Source} (net {b.Net})",
            (a.Center + b.Center) * 0.5, web, m);
    }

    // ---- trace width (opposing-wall thickness) -------------------------------

    private static void CheckTraceWidth(PcbCopperModel model, DrcRuleSet rules, List<DrcViolation> v)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in model.Copper)
            if (seen.Add(feature.Source))
                CheckFeatureWidth(feature, rules, v);
    }

    private static void CheckFeatureWidth(in CopperFeature feature, DrcRuleSet rules, List<DrcViolation> v)
    {
        if (!(rules.MinTraceWidth > 0))
            return;
        double extent = Math.Min(feature.Region.Bounds.Max.X - feature.Region.Bounds.Min.X,
                                 feature.Region.Bounds.Max.Y - feature.Region.Bounds.Min.Y);
        var polygon = feature.Region.ToRegion(Math.Max(1e-12, extent * 1e-3));
        var report = Region2dThickness.Measure([polygon], samplesPerEdge: 2);
        if (report.Samples > 0 && report.Minimum < rules.MinTraceWidth)
            v.Add(new DrcViolation(
                DrcRule.TraceWidth, feature.Layer,
                $"trace width {report.Minimum:g3} < {rules.MinTraceWidth:g3} mm at {feature.Source}",
                report.ThinnestAt, report.Minimum, rules.MinTraceWidth));
    }

    // ---- acute-angle / acid-trap ---------------------------------------------

    private static void CheckAcuteAngles(PcbCopperModel model, DrcRuleSet rules, List<DrcViolation> v)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in model.Copper)
            if (seen.Add(feature.Source))
                CheckFeatureAcuteAngles(feature, rules, v);
    }

    private static void CheckFeatureAcuteAngles(in CopperFeature feature, DrcRuleSet rules, List<DrcViolation> v)
    {
        double threshold = rules.MinAcuteAngleDegrees;
        if (!(threshold > 0))
            return;
        double thresholdRad = threshold * Math.PI / 180.0;
        foreach (var loop in feature.Region.AllLoops())
        {
            int n = loop.Count;
            if (n < 2)
                continue;   // a single full-circle edge has no corner
            for (int i = 0; i < n; i++)
            {
                var incoming = loop[i];                 // arrives at the joint
                var outgoing = loop[(i + 1) % n];       // leaves the joint
                if (!EdgeInDirection(incoming, out var dirIn) || !EdgeOutDirection(outgoing, out var dirOut))
                    continue;
                // The wedge angle between the two edges meeting at the joint, measured either side:
                // acute either way (a copper spike OR an etchant-side notch) is an acid-trap risk.
                double cos = Math.Clamp((-dirIn).Dot(dirOut), -1, 1);
                double wedge = Math.Acos(cos);
                if (wedge < thresholdRad)
                {
                    double degrees = wedge * 180.0 / Math.PI;
                    v.Add(new DrcViolation(
                        DrcRule.AcuteAngle, feature.Layer,
                        $"acute angle {degrees:g3}° < {threshold:g3}° at {feature.Source}",
                        outgoing.Start, degrees, threshold));
                }
            }
        }
    }

    // ---- ratsnest (unrouted nets) --------------------------------------------

    /// <summary>A named net whose pads are not all in one connected component is UNROUTED — the
    /// inverse of a short. The real connectivity answer lives in <see cref="PcbConnectivity"/>: two
    /// features join when they TOUCH on a layer OR are the ends of a via (or through-hole pad), so a
    /// net whose pads sit on different layers is CONNECTED once a via ties them, not a ratsnest. The
    /// unrouted names come back in ordinal order.</summary>
    private static IReadOnlyList<string> Ratsnest(PcbCopperModel model) =>
        PcbConnectivity.Analyze(model).Unrouted;

    // ---- net grouping --------------------------------------------------------

    /// <summary>A net's copper on one layer, unioned into a region set with its bounds — the unit
    /// the clearance rule compares. A connected net (Signal/Stub) groups its features; an
    /// unconnected feature (null net) is its OWN group, since two floating pieces of copper are
    /// electrically distinct and must clear each other.</summary>
    private sealed class NetGroup
    {
        public required string? Net { get; init; }
        public required string Display { get; init; }
        public required IReadOnlyList<CopperFeature> Features { get; init; }
        public required IReadOnlyList<CurvedRegion2d> Copper { get; init; }
        public required Aabb Bounds { get; init; }

        public static NetGroup Of(string? net, IReadOnlyList<CopperFeature> features)
        {
            var copper = CurvedRegion2dBoolean.UnionAll([.. features.Select(f => f.Region)]);
            var bounds = Aabb.Empty;
            foreach (var f in features)
                bounds = bounds.Union(f.Region.Bounds);
            return new NetGroup
            {
                Net = net,
                Display = net is null ? "(unconnected)" : $"'{net}'",
                Features = features,
                Copper = copper,
                Bounds = bounds,
            };
        }

        public static NetGroup One(CopperFeature feature) => Of(feature.Net, [feature]);
    }

    private static IReadOnlyList<NetGroup> BuildGroups(IEnumerable<CopperFeature> features)
    {
        var byNet = new Dictionary<string, List<CopperFeature>>(StringComparer.Ordinal);
        var groups = new List<NetGroup>();
        foreach (var f in features)
        {
            if (f.Net is null)
                groups.Add(NetGroup.One(f));   // its own group
            else if (byNet.TryGetValue(f.Net, out var list))
                list.Add(f);
            else
                byNet[f.Net] = [f];
        }
        foreach (var (net, list) in byNet)
            groups.Add(NetGroup.Of(net, list));
        return groups;
    }

    private static bool SameNet(string? a, string? b) => a is not null && b is not null && a == b;

    private static (string A, string B) NearestFeatures(NetGroup a, NetGroup b)
    {
        double best = double.PositiveInfinity;
        string fa = a.Features[0].Source, fb = b.Features[0].Source;
        foreach (var x in a.Features)
            foreach (var y in b.Features)
            {
                double dist = x.Region.Bounds.Center.DistanceTo(y.Region.Bounds.Center);
                if (dist < best)
                {
                    best = dist;
                    fa = x.Source;
                    fb = y.Source;
                }
            }
        return (fa, fb);
    }

    // ---- geometry helpers ----------------------------------------------------

    private static IReadOnlyList<CurvedRegion2d> Grow(IReadOnlyList<CurvedRegion2d> regions, double delta) =>
        CurvedRegion2dOffset.Offset(regions, delta, OffsetJoin.Round);

    /// <summary>The minimum edge-to-edge separation of two disjoint region sets, capped at
    /// <paramref name="cap"/> — the largest g for which growing each by g/2 keeps them disjoint.
    /// A bisection over the SAME grow-and-intersect the clearance rule proves itself with, so the
    /// reported number and the pass/fail decision cannot disagree; run only on the few violations,
    /// never on the whole board.</summary>
    private static double Separation(
        IReadOnlyList<CurvedRegion2d> a, IReadOnlyList<CurvedRegion2d> b, double cap)
    {
        if (CurvedRegion2dBoolean.Intersection(a, b).Count > 0)
            return 0;   // overlapping (a short) — no positive separation
        if (CurvedRegion2dBoolean.Intersection(Grow(a, cap / 2), Grow(b, cap / 2)).Count == 0)
            return cap;   // ≥ cap; the caller only needs to know it is not below the limit
        double lo = 0, hi = cap;
        for (int i = 0; i < 20; i++)   // relative 1e-6 of the cap — plenty for a reported number
        {
            double m = (lo + hi) / 2;
            if (CurvedRegion2dBoolean.Intersection(Grow(a, m / 2), Grow(b, m / 2)).Count == 0)
                lo = m;
            else
                hi = m;
        }
        return lo;
    }

    private static Vector2d CenterOf(IReadOnlyList<CurvedRegion2d> regions)
    {
        var center = regions[0].Bounds.Center;
        return new Vector2d(center.X, center.Y);
    }

    /// <summary>XY separation of two AABBs (0 if they overlap in the plane).</summary>
    private static double AabbGap(in Aabb a, in Aabb b)
    {
        double dx = Math.Max(0, Math.Max(a.Min.X - b.Max.X, b.Min.X - a.Max.X));
        double dy = Math.Max(0, Math.Max(a.Min.Y - b.Max.Y, b.Min.Y - a.Max.Y));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>XY separation of a disc's AABB from another AABB.</summary>
    private static double AabbGap(in Vector2d center, double radius, in Aabb b)
    {
        var disc = Aabb.FromCenterAndSize(
            new Vector3d(center.X, center.Y, 0), new Vector3d(2 * radius, 2 * radius, 0));
        return AabbGap(disc, b);
    }

    /// <summary>Minimum distance from a curved region's boundary to a closed polygon (the board
    /// outline) — the copper-to-edge gap, sampled at a fine chord tolerance for the reported
    /// number.</summary>
    private static double DistanceToOutline(CurvedRegion2d region, IReadOnlyList<Vector2d> outline)
    {
        double extent = Math.Min(region.Bounds.Max.X - region.Bounds.Min.X,
                                 region.Bounds.Max.Y - region.Bounds.Min.Y);
        var polygon = region.ToRegion(Math.Max(1e-12, extent * 1e-3));
        double best = double.PositiveInfinity;
        foreach (var loop in polygon.AllLoops())
            foreach (var p in loop)
                best = Math.Min(best, PointToPolygon(p, outline));
        return best;
    }

    private static double PointToPolygon(in Vector2d p, IReadOnlyList<Vector2d> polygon)
    {
        double best = double.PositiveInfinity;
        int n = polygon.Count;
        for (int i = 0; i < n; i++)
            best = Math.Min(best, PointToSegment(p, polygon[i], polygon[(i + 1) % n]));
        return best;
    }

    private static double PointToSegment(in Vector2d p, in Vector2d a, in Vector2d b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 == 0)
            return p.DistanceTo(a);
        double t = Math.Clamp((p - a).Dot(ab) / len2, 0, 1);
        return p.DistanceTo(a + ab * t);
    }

    /// <summary>The unit direction an edge leaves its start point along (its outgoing tangent).</summary>
    private static bool EdgeOutDirection(CurvedEdge2d edge, out Vector2d direction) =>
        (edge.PointAt(1e-4) - edge.Start).TryNormalize(Tolerance.Default, out direction);

    /// <summary>The unit direction an edge arrives at its end point along (its incoming tangent).</summary>
    private static bool EdgeInDirection(CurvedEdge2d edge, out Vector2d direction) =>
        (edge.End - edge.PointAt(1 - 1e-4)).TryNormalize(Tolerance.Default, out direction);

    private static void CheckFeatureAgainstEdge(
        PcbBoard board, in CopperFeature feature, DrcRuleSet rules, List<DrcViolation> v)
    {
        double d = rules.MinCopperToEdge;
        if (!(d > 0))
            return;
        var region = new Region2d([.. board.OutlinePoints]);
        var inner = Region2dOffset.Offset(region, -d).Select(CurvedRegion2d.FromRegion).ToList();
        IReadOnlyList<CurvedRegion2d> outside = [feature.Region];
        foreach (var r in inner)
        {
            outside = CurvedRegion2dBoolean.Difference(outside, [r]);
            if (outside.Count == 0)
                return;
        }
        double measured = DistanceToOutline(feature.Region, board.OutlinePoints);
        v.Add(new DrcViolation(
            DrcRule.CopperToEdge, feature.Layer,
            $"copper-to-edge {measured:g3} < {d:g3} mm at {feature.Source}",
            CenterOf(outside), measured, d));
    }
}
