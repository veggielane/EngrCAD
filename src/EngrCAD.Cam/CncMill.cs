using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Modeling;

namespace EngrCAD.Cam;

/// <summary>
/// A milling tool and its process numbers. Feeds are mm/min (the CNC convention — G-code's F
/// word carries them verbatim), <see cref="StepDown"/> is the deepest cut per pass, and
/// <see cref="Stepover"/> is the fraction of the diameter between neighbouring pocket rings —
/// <b>at or below 0.5 the ring ladder provably covers the whole reachable area</b> (each ring
/// clears ± a radius about its centreline, and consecutive centrelines are stepover·D apart, so
/// coverage needs stepover·D ≤ r); a larger stepover is legal and the coverage oracle is what
/// reports what it leaves. Feeds and speeds are ENGINEERING INPUTS with stated defaults, not a
/// chip-load derivation — <see cref="CncToolLibrary.Suggest"/> derives a starting set from the
/// transcribed <see cref="MillMaterials"/> catalogue when the caller wants one.
/// </summary>
public sealed record MillTool(
    double Diameter,
    double FeedRate = 300,
    double PlungeRate = 100,
    double SpindleRpm = 10000,
    double StepDown = 2,
    double Stepover = 0.5)
{
    /// <summary>The tool radius (mm).</summary>
    public double Radius => Diameter / 2;

    /// <summary>Refuses an unusable tool by name.</summary>
    public void Validate()
    {
        Require(Diameter, nameof(Diameter));
        Require(FeedRate, nameof(FeedRate));
        Require(PlungeRate, nameof(PlungeRate));
        Require(SpindleRpm, nameof(SpindleRpm));
        Require(StepDown, nameof(StepDown));
        if (!(Stepover > 0) || Stepover > 1)
            throw new ArgumentException(
                $"Stepover must lie in (0, 1] as a fraction of the diameter; got {Stepover:0.###}.");

        static void Require(double value, string name)
        {
            if (!(value > 0) || !double.IsFinite(value))
                throw new ArgumentException($"{name} must be finite and positive; got {value:0.###}.");
        }
    }
}

/// <summary>Which side of the outline a profile cut runs on.</summary>
public enum ProfileSide
{
    /// <summary>The tool outside the outline (cutting a part free of stock).</summary>
    Outside,

    /// <summary>The tool inside the outline (an opening cut to the line).</summary>
    Inside,
}

/// <summary>
/// Climb or conventional milling — which way a closed pass traverses its loop, DERIVED rather
/// than transcribed: an M3 right-hand cutter viewed from +Z spins clockwise (ω = −ẑ|ω|), so a
/// tooth at the contact point with material on the LEFT of travel (offset r·(ẑ×d̂)) moves at
/// ω×offset = +|ω|r·d̂ — the same direction as the feed, the chip starting thick, which IS
/// climb milling. Hence <b>climb = material on the left of travel</b>: walking a loop with the
/// material inside it counter-clockwise, or with the material beyond it clockwise. The
/// orientation is MEASURED per loop (shoelace sign) and set by that requirement, never assumed
/// from the offset machinery's emission order.
/// </summary>
public enum MillDirection
{
    /// <summary>Material on the left of travel — the chip starts thick and thins to zero, the
    /// finish and tool-life default on any machine with backlash-free drives.</summary>
    Climb,

    /// <summary>Material on the right of travel — the chip starts at zero and thickens, the
    /// choice for machines with backlash (the cutter cannot pull itself into the work).</summary>
    Conventional,
}

/// <summary>One tool pass: a 3D polyline in bed coordinates (the stock top is z = 0, cuts run
/// negative). Within a pass the WRITER classifies each move by shape — an XY move is a CUT at
/// the feed rate, a straight-down move a PLUNGE at the plunge rate, a straight-up move a RAPID
/// retract — so pecked drilling and tab lifts need no per-move annotations.</summary>
public sealed record MillPass(IReadOnlyList<Vector3d> Points, bool IsClosed)
{
    /// <summary>The XY cutting length of the pass (vertical moves excluded).</summary>
    public double CutLength
    {
        get
        {
            double length = 0;
            int count = Points.Count + (IsClosed ? 1 : 0);
            for (int i = 1; i < count; i++)
            {
                var a = Points[i - 1];
                var b = Points[i % Points.Count];
                length += new Vector2d(b.X - a.X, b.Y - a.Y).Length;
            }
            return length;
        }
    }
}

/// <summary>One milling operation: a name, the tool, and its passes in cut order.</summary>
public sealed record MillOperation(string Name, MillTool Tool, IReadOnlyList<MillPass> Passes)
{
    /// <summary>Total XY cutting length over the passes (mm).</summary>
    public double CutLength => Passes.Sum(p => p.CutLength);
}

/// <summary>
/// 2.5D CNC milling — CAM stage 2, and like the slicer a THIN layer over landed machinery:
/// pocket clearing IS the inward-offset ladder (`Region2dOffset` rings a stepover apart, holes'
/// grown boundaries ridden like any other loop), profiling is ONE outline offset by the tool
/// radius (round joins — the path a tool centre PHYSICALLY rolls around an outside corner,
/// keeping contact, where a miter would lift it off the part), depth comes in `StepDown` slices
/// with the last clamped to the stated depth, drilling is EXPANDED peck moves (plain G0/G1, so
/// the twin decoder reads a drill cycle with nothing new — the writer's opt-in <c>cannedDrilling</c>
/// re-spells a drill pass as G81/G83 cycles),
/// and travel ordering per level is the shared `RunLinker`.
///
/// <para><b>The verification oracle is the morphological OPENING</b>: a pocket cut with a
/// radius-r tool can reach exactly `grow_r(shrink_r(region))` — internal corners come back
/// rounded — so the union of the passes' swept footprints (each centreline stroked at the tool
/// diameter, the stock simulation) must equal the opening, and for a rectangular pocket the
/// unreachable corner residue is CLOSED FORM: `(4 − π)·r²`. The no-gouge claim is exact and
/// point-by-point: every pass point at least the tool radius from the region boundary, which
/// insetting by construction guarantees and the tests measure anyway.</para>
/// </summary>
public static class CncMill
{
    /// <summary>
    /// Clears <paramref name="region"/> to <paramref name="depth"/> below the stock top: rings
    /// from the boundary pass (inset r) inward by stepover·D until the region is exhausted,
    /// executed innermost-first per depth level (the tool climbs outward, finishing at the
    /// wall), levels at StepDown with the last clamped to the exact depth.
    /// </summary>
    public static MillOperation Pocket(
        Region2d region, MillTool tool, double depth, string name = "pocket",
        MillDirection direction = MillDirection.Climb, double rampAngleDegrees = 0)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(tool);
        tool.Validate();
        RequireDepth(depth);
        if (!(rampAngleDegrees >= 0) || rampAngleDegrees > 45)
            throw new ArgumentException(
                $"'{name}': rampAngleDegrees must lie in [0, 45] (0 = straight plunge); "
                + $"got {rampAngleDegrees:0.###}.");

        // The ring ladder, outermost (boundary pass) first as generated. Cutting INSIDE the
        // outline, the remaining stock sits beyond each outer-derived ring (toward the wall —
        // rings run innermost-first, so inward is already cleared) and inside each
        // island-derived ring, which is what the direction rule reads.
        double step = tool.Stepover * tool.Diameter;
        var rings = new List<List<Vector2d[]>>();
        for (int k = 0; ; k++)
        {
            var offsets = Region2dOffset.Offset(region, -(tool.Radius + k * step));
            if (offsets.Count == 0)
                break;
            var loops = new List<Vector2d[]>();
            foreach (var shell in offsets)
            {
                loops.Add(Orient([.. shell.Outer], direction, materialInside: false));
                foreach (var hole in shell.Holes)
                    loops.Add(Orient([.. hole], direction, materialInside: true));
            }
            rings.Add(loops);
        }
        if (rings.Count == 0)
            throw new ArgumentException(
                $"'{name}': the tool (Ø{tool.Diameter:0.###}) does not fit the region at all — "
                + "the boundary pass at one radius inset is already empty.");

        var passes = new List<MillPass>();
        var pen = new Vector2d(region.Bounds.Min.X, region.Bounds.Min.Y);
        double previousLevel = 0;
        foreach (double z in DepthLevels(depth, tool.StepDown))
        {
            // Innermost RING first, climbing outward, loops linked WITHIN each ring level —
            // per ring rather than one global link, because a global nearest-endpoint link
            // is pen-dependent and measurably started a level at its BOUNDARY ring, which
            // both contradicts this method's own contract and undermines the direction
            // rule's premise that inward is already cleared when a ring runs.
            if (rampAngleDegrees <= 0)
            {
                for (int k = rings.Count - 1; k >= 0; k--)
                    AppendLinkedLoops(passes, rings[k], z, ref pen);
            }
            else
            {
                // Helical entry + depth linking: descend from the previous (already cleared)
                // level on a helix about the level's own first point — radius UNDER the tool
                // radius so no core post is left, inside the measured room, one flat closing
                // turn at the level — then run the level's rings as ONE pass, linked at depth
                // (a link cut through one stepover of web) wherever the straight link stays a
                // tool radius clear of the boundary; a link that would pass a concave gap too
                // closely, or a pocket too tight to helix, falls back to the plunge entry.
                var ordered = new List<Vector2d[]>();
                for (int k = rings.Count - 1; k >= 0; k--)
                    ordered.AddRange(LinkLoops(rings[k], ref pen));
                if (ordered.Count == 0)
                {
                    previousLevel = z;
                    continue;
                }
                var centre = ordered[0][0];
                double room = DistanceToBoundary(region, centre) - tool.Radius;
                double rh = Math.Min(tool.Radius / 2, room);
                List<Vector3d>? current = null;
                if (rh >= tool.Radius / 10)
                    current = [.. HelixEntry(centre, previousLevel, z, rh, rampAngleDegrees).Points];
                foreach (var loop in ordered)
                {
                    if (current is not null && current.Count > 0
                        && !SegmentClearOfBoundary(region,
                            new Vector2d(current[^1].X, current[^1].Y), loop[0], tool.Radius))
                    {
                        passes.Add(new MillPass(current, IsClosed: false));
                        current = null;
                    }
                    // A blocked link (or no helix room) starts a fresh run whose entry is
                    // the incumbent plunge; clear links downstream still merge at depth.
                    current ??= [];
                    foreach (var q in loop)
                        current.Add(new Vector3d(q.X, q.Y, z));
                    current.Add(new Vector3d(loop[0].X, loop[0].Y, z));
                }
                if (current is { Count: > 0 })
                    passes.Add(new MillPass(current, IsClosed: false));
            }
            previousLevel = z;
        }
        return new MillOperation(name, tool, passes);
    }

    /// <summary>The helical entry pass: full turns from <paramref name="zFrom"/> down to
    /// <paramref name="zTo"/> at pitch <c>2π·r·tan(angle)</c> (the ramp angle is the descent
    /// slope along the helix's own arc), then ONE flat closing turn at the level so the ramp's
    /// floor is cleared before the ring ladder starts.</summary>
    private static MillPass HelixEntry(
        in Vector2d centre, double zFrom, double zTo, double radius, double angleDegrees)
    {
        const int segmentsPerTurn = 16;
        double pitch = 2 * Math.PI * radius * Math.Tan(angleDegrees * Math.PI / 180);
        double drop = zFrom - zTo;
        int turns = Math.Max(1, (int)Math.Ceiling(drop / pitch - 1e-12));
        int descentSteps = turns * segmentsPerTurn;
        var points = new List<Vector3d>(descentSteps + segmentsPerTurn + 2);
        for (int i = 0; i <= descentSteps; i++)
        {
            double theta = 2 * Math.PI * i / segmentsPerTurn;
            double z = zFrom - drop * i / descentSteps;
            points.Add(new Vector3d(
                centre.X + radius * Math.Cos(theta),
                centre.Y + radius * Math.Sin(theta), z));
        }
        for (int i = 1; i <= segmentsPerTurn; i++)
        {
            double theta = 2 * Math.PI * i / segmentsPerTurn;
            points.Add(new Vector3d(
                centre.X + radius * Math.Cos(theta),
                centre.Y + radius * Math.Sin(theta), zTo));
        }
        // No handover point: the merged pass appends the level's first ring point next,
        // and a duplicated consecutive point is a zero-length segment the stroke
        // machinery cannot carry.
        return new MillPass(points, IsClosed: false);
    }

    /// <summary>The exact distance from a point to the region's boundary (outer and hole
    /// loops, point-to-segment) — what sizes a helical entry's room.</summary>
    internal static double DistanceToBoundary(Region2d region, Vector2d p)
    {
        double best = double.PositiveInfinity;
        Visit(region.Outer);
        foreach (var hole in region.Holes)
            Visit(hole);
        return best;

        void Visit(IReadOnlyList<Vector2d> loop)
        {
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                var e = b - a;
                double len2 = e.LengthSquared;
                double t = len2 > 0 ? Math.Clamp((p - a).Dot(e) / len2, 0, 1) : 0;
                best = Math.Min(best, (p - (a + e * t)).Length);
            }
        }
    }

    /// <summary>
    /// REST machining: clears what a rough pocket with <paramref name="roughTool"/> could not
    /// reach — the corner residues of the morphological opening — with the smaller
    /// <paramref name="finishTool"/>. The rest region is <c>region − opening(region, R₁)</c>,
    /// and each residue piece is pocketed over <c>intersect(grow(piece, r₂), region)</c>: the
    /// GROW is what lets the small tool's centre stand in already-cleared space (the whole
    /// point of rest machining — the residue itself is often too small to hold a tool), while
    /// the INTERSECT keeps the wall inviolate, so the no-gouge claim against the ORIGINAL
    /// boundary survives point by point. The opening is grown by an ε before the difference,
    /// because it touches the wall TANGENTIALLY at every residue cusp and a tangential contact
    /// is the arrangement's hostile case — the ε makes it transversal and costs an ε-band of
    /// residue the grow immediately wins back. A region the rough tool leaves no residue in
    /// returns an operation with NO passes (an honest no-op, not a refusal).
    /// </summary>
    public static MillOperation PocketRest(
        Region2d region, MillTool roughTool, MillTool finishTool, double depth,
        string name = "rest", MillDirection direction = MillDirection.Climb,
        double minRestThickness = -1)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(roughTool);
        ArgumentNullException.ThrowIfNull(finishTool);
        roughTool.Validate();
        finishTool.Validate();
        RequireDepth(depth);
        if (finishTool.Radius >= roughTool.Radius)
            throw new ArgumentException(
                $"'{name}': the finish tool (Ø{finishTool.Diameter:0.###}) must be smaller than "
                + $"the rough tool (Ø{roughTool.Diameter:0.###}) — a rest pass with an equal or "
                + "larger tool reaches nothing the rough pass did not.");
        if (minRestThickness < 0)
            minRestThickness = finishTool.Radius / 4;

        // The rough tool's reachable area is the opening; grown by ε so its tangential
        // contact with the wall at each residue cusp becomes transversal.
        double extent = (region.Bounds.Max - region.Bounds.Min).Length;
        double epsilon = 1e-6 * extent;
        var opening = Region2dBoolean.UnionAll([.. Region2dOffset
            .Offset(region, -roughTool.Radius)
            .SelectMany(s => Region2dOffset.Offset(s, roughTool.Radius + epsilon))]);
        var rest = Region2dBoolean.Difference([region], opening);

        var passes = new List<MillPass>();
        foreach (var piece in rest)
        {
            // A residue that cannot hold a disc of the stated minimum thickness anywhere
            // is flattening noise, not a feature: a chorded arc's junction corners leave
            // sagitta-scale crumbs the opening genuinely cannot reach, and pocketing them
            // would emit micro-passes for material no drawing ever asked about. The
            // threshold is a stated engineering input (default a quarter of the finish
            // radius; 0 keeps everything).
            if (minRestThickness > 0
                && Region2dOffset.Offset(piece, -minRestThickness).Count == 0)
                continue;

            // The grow is 2·r₂ and that is a DERIVED sufficiency, not a margin: for any
            // residue point p the finish tool can legally reach, there is a disc B(c, r₂)
            // with |c − p| ≤ r₂ and c at least r₂ from the region boundary — and every
            // point of that disc is within 2·r₂ of p and inside the region, so the disc
            // lies in intersect(grow(residue, 2r₂), region) and its centre in that
            // region's own r₂-inset, which is exactly where the ring ladder puts the tool.
            foreach (var milled in Region2dBoolean.Intersection(
                Region2dOffset.Offset(piece, 2 * finishTool.Radius), [region]))
            {
                // A piece the finish tool STILL cannot reach (inset empty) is skipped —
                // it is the finish tool's own residue, which the coverage oracle reports.
                if (Region2dOffset.Offset(milled, -finishTool.Radius).Count == 0)
                    continue;
                var op = Pocket(milled, finishTool, depth, name, direction);
                passes.AddRange(op.Passes);
            }
        }
        return new MillOperation(name, finishTool, passes);
    }

    /// <summary>
    /// Cuts along the outline at one tool radius to the chosen side, in depth steps, optionally
    /// leaving <paramref name="tabs"/> holding tabs on the FINAL pass (evenly spaced by arc
    /// length — a stated convention, not rounding luck — each <paramref name="tabWidth"/> long
    /// and <paramref name="tabHeight"/> tall).
    /// <para><b>Lead-in/out arcs are opt-in</b> (<paramref name="leadRadius"/> &gt; 0, 0 is
    /// byte-identical): each loop is entered and left on a quarter arc TANGENT to the path at
    /// the seam, placed on the side AWAY from the material — which <see cref="Orient"/> has
    /// already made a travel-relative fact (climb puts material left of travel, so away is
    /// RIGHT; conventional the mirror), so no per-loop kind flag exists to get wrong. The
    /// plunge then happens at the arc's start, off the wall, which is the point: a plunge ON
    /// the profile dwells and marks it. A lead that does not fit (a small hole's far wall
    /// inside the arc's reach) is refused by name with the measured shortfall.</para>
    /// </summary>
    public static MillOperation Profile(
        Region2d region, MillTool tool, double depth, ProfileSide side,
        int tabs = 0, double tabHeight = 0, double tabWidth = 0, string name = "profile",
        MillDirection direction = MillDirection.Climb, double leadRadius = 0)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(tool);
        tool.Validate();
        RequireDepth(depth);
        if (tabs < 0)
            throw new ArgumentException($"'{name}': tabs must be non-negative; got {tabs}.");
        if (tabs > 0)
        {
            if (!(tabHeight > 0) || tabHeight >= depth)
                throw new ArgumentException(
                    $"'{name}': {tabs} tabs need a tab height in (0, depth); got {tabHeight:0.###} "
                    + $"against depth {depth:0.###}.");
            if (!(tabWidth > 0))
                throw new ArgumentException(
                    $"'{name}': {tabs} tabs need a positive tab width; got {tabWidth:0.###}.");
        }

        double delta = side == ProfileSide.Outside ? tool.Radius : -tool.Radius;
        var offsets = Region2dOffset.Offset(region, delta);
        if (offsets.Count == 0)
            throw new ArgumentException(
                $"'{name}': the {side} profile at one tool radius (Ø{tool.Diameter:0.###}) "
                + "leaves no outline — the tool does not fit.");
        // Cutting OUTSIDE the outline the material (the part) lies within each outer-derived
        // loop and beyond each hole-derived one; cutting INSIDE it is the pocket relationship.
        bool outside = side == ProfileSide.Outside;
        var loops = new List<Vector2d[]>();
        foreach (var shell in offsets)
        {
            loops.Add(Orient([.. shell.Outer], direction, materialInside: outside));
            foreach (var hole in shell.Holes)
                loops.Add(Orient([.. hole], direction, materialInside: !outside));
        }

        if (!(leadRadius >= 0) || !double.IsFinite(leadRadius))
            throw new ArgumentException(
                $"'{name}': leadRadius must be finite and non-negative; got {leadRadius:0.###}.");
        (Vector2d[] In, Vector2d[] Out)[]? leads = null;
        if (leadRadius > 0)
        {
            leads = new (Vector2d[], Vector2d[])[loops.Count];
            for (int i = 0; i < loops.Count; i++)
            {
                leads[i] = LeadArcs(loops[i], direction, leadRadius);
                foreach (var q in leads[i].In.Concat(leads[i].Out))
                {
                    double room = DistanceToBoundary(region, q) - tool.Radius;
                    if (room < -1e-9)
                        throw new ArgumentException(
                            $"'{name}': a lead radius of {leadRadius:0.###} does not fit "
                            + $"loop {i} — a lead point comes {-room:0.###} closer to the "
                            + "outline than the tool radius allows. Reduce the lead radius.");
                }
            }
        }

        var levels = DepthLevels(depth, tool.StepDown);
        var passes = new List<MillPass>();
        var pen = new Vector2d(region.Bounds.Min.X, region.Bounds.Min.Y);
        for (int i = 0; i < levels.Count; i++)
        {
            bool final = i == levels.Count - 1;
            if (final && tabs > 0)
            {
                for (int k = 0; k < loops.Count; k++)
                {
                    var pass = WithTabs(loops[k], levels[i], tabs, -depth + tabHeight, tabWidth, name);
                    passes.Add(leads is null ? pass : WithLeads(pass, leads[k], levels[i]));
                }
                if (loops.Count > 0)
                    pen = new Vector2d(loops[^1][0].X, loops[^1][0].Y);
            }
            else
            {
                AppendLinkedLoops(passes, loops, levels[i], ref pen, leads);
            }
        }
        return new MillOperation(name, tool, passes);
    }

    /// <summary>The two quarter arcs a loop is entered and left on: tangent to the path at
    /// the seam (loop[0]), curving to the side away from the material — RIGHT of travel for
    /// climb, LEFT for conventional, the fact <see cref="Orient"/>'s winding contract makes
    /// travel-relative so it needs no per-loop kind. Sixteen chords per quarter (sagitta
    /// R·0.0012, visually an arc; the writer's G2/G3 fitter may decline them at large radii
    /// under its own sagitta cap, which is its call to make).</summary>
    private static (Vector2d[] In, Vector2d[] Out) LeadArcs(
        Vector2d[] loop, MillDirection direction, double radius)
    {
        var p0 = loop[0];
        var d = (loop[1] - p0).Normalized();
        var e = (p0 - loop[^1]).Normalized();
        // Away from material: climb cuts with material LEFT of travel (the M3 cross-product
        // derivation), so away is the RIGHT normal; conventional is the mirror.
        Vector2d Away(Vector2d t) => direction == MillDirection.Climb
            ? new Vector2d(t.Y, -t.X)
            : new Vector2d(-t.Y, t.X);
        const int segments = 16;

        var nIn = Away(d);
        var cIn = p0 + nIn * radius;
        var lead = new Vector2d[segments];
        for (int i = 0; i < segments; i++)
        {
            double a = i * (Math.PI / 2) / segments;
            lead[i] = cIn - d * (radius * Math.Cos(a)) - nIn * (radius * Math.Sin(a));
        }

        var nOut = Away(e);
        var cOut = p0 + nOut * radius;
        var leave = new Vector2d[segments];
        for (int i = 1; i <= segments; i++)
        {
            double a = i * (Math.PI / 2) / segments;
            leave[i - 1] = cOut - nOut * (radius * Math.Cos(a)) + e * (radius * Math.Sin(a));
        }
        return (lead, leave);
    }

    /// <summary>Rewrites a closed (or tabbed) loop pass as an OPEN pass carrying its lead
    /// arcs: the lead-in, the loop's own points, the closing point, the lead-out — so the
    /// writer's plunge (always at the pass's first point) lands at the arc's start, off the
    /// wall, with no writer change at all.</summary>
    private static MillPass WithLeads(
        MillPass pass, (Vector2d[] In, Vector2d[] Out) leads, double z)
    {
        var points = new List<Vector3d>(pass.Points.Count + 33);
        foreach (var q in leads.In)
            points.Add(new Vector3d(q.X, q.Y, z));
        points.AddRange(pass.Points);
        if (pass.IsClosed)
            points.Add(pass.Points[0]);
        foreach (var q in leads.Out)
            points.Add(new Vector3d(q.X, q.Y, z));
        return new MillPass(points, IsClosed: false);
    }

    /// <summary>
    /// Drills each point to <paramref name="depth"/>, pecking in <paramref name="peck"/>-deep
    /// bites with a retract between (0 = one plunge). The moves are EXPANDED plain G0/G1 —
    /// down at the plunge rate, up as a rapid — so the twin decoder reads a drill cycle with
    /// nothing new; <see cref="CncGcodeWriter"/>'s opt-in <c>cannedDrilling</c> re-spells the
    /// same passes as canned G81/G83 cycles.
    /// </summary>
    public static MillOperation Drill(
        IReadOnlyList<Vector2d> points, MillTool tool, double depth, double peck = 0,
        string name = "drill")
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(tool);
        tool.Validate();
        RequireDepth(depth);
        if (peck < 0)
            throw new ArgumentException($"'{name}': peck must be non-negative (0 = one plunge).");
        if (points.Count == 0)
            throw new ArgumentException($"'{name}': at least one drill point is needed.");

        double retract = DrillRetract;                       // chip-clear height above the stock
        var passes = new List<MillPass>(points.Count);
        foreach (var p in points)
        {
            var moves = new List<Vector3d>();
            if (peck > 0)
            {
                for (double z = -Math.Min(peck, depth); ; z -= peck)
                {
                    if (z <= -depth + 1e-12)
                    {
                        moves.Add(new Vector3d(p.X, p.Y, -depth));
                        break;
                    }
                    moves.Add(new Vector3d(p.X, p.Y, z));
                    moves.Add(new Vector3d(p.X, p.Y, retract));
                }
            }
            else
            {
                moves.Add(new Vector3d(p.X, p.Y, -depth));
            }
            passes.Add(new MillPass(moves, IsClosed: false));
        }
        return new MillOperation(name, tool, passes);
    }

    /// <summary>The drill cycles' chip-clear retract height above the stock top (mm) — ONE rule
    /// shared by the expanded peck moves and the canned-cycle writer's R plane, so the two
    /// spellings of a drill cannot disagree about where the tool clears chips.</summary>
    internal const double DrillRetract = 0.5;

    /// <summary>Walks the loop with the material on the LEFT for climb (counter-clockwise iff
    /// the material is inside the loop) and on the RIGHT for conventional — the orientation
    /// MEASURED by shoelace sign and reversed about its own start point when it disagrees, so
    /// the pass's start (and the travel linking) stays put and only the traversal flips.</summary>
    private static Vector2d[] Orient(Vector2d[] loop, MillDirection direction, bool materialInside)
    {
        bool wantCcw = (direction == MillDirection.Climb) == materialInside;
        double doubleArea = 0;
        for (int i = 0; i < loop.Length; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Length];
            doubleArea += a.X * b.Y - b.X * a.Y;
        }
        if (doubleArea > 0 == wantCcw)
            return loop;
        var reversed = new Vector2d[loop.Length];
        reversed[0] = loop[0];
        for (int i = 1; i < loop.Length; i++)
            reversed[i] = loop[loop.Length - i];
        return reversed;
    }

    /// <summary>The cut depths: StepDown increments with the LAST clamped to the exact stated
    /// depth (so depth 5 at step 2 cuts −2, −4, −5 — arithmetic, not accumulation).</summary>
    internal static List<double> DepthLevels(double depth, double stepDown)
    {
        int count = Math.Max(1, (int)Math.Ceiling(depth / stepDown - 1e-9));
        var levels = new List<double>(count);
        for (int i = 1; i <= count; i++)
            levels.Add(i == count ? -depth : -(i * stepDown));
        return levels;
    }

    private static void RequireDepth(double depth)
    {
        if (!(depth > 0) || !double.IsFinite(depth))
            throw new ArgumentException($"A cut depth must be finite and positive; got {depth:0.###}.");
    }

    /// <summary>Greedy-links the level's loops from the pen (the shared <see cref="RunLinker"/>)
    /// and appends them at <paramref name="z"/>.</summary>
    private static void AppendLinkedLoops(
        List<MillPass> passes, List<Vector2d[]> loops, double z, ref Vector2d pen,
        (Vector2d[] In, Vector2d[] Out)[]? leads = null)
    {
        foreach (var loop in LinkLoops(loops, ref pen))
        {
            var points = new Vector3d[loop.Length];
            for (int i = 0; i < loop.Length; i++)
                points[i] = new Vector3d(loop[i].X, loop[i].Y, z);
            var pass = new MillPass(points, IsClosed: true);
            if (leads is not null)
            {
                // LinkLoops permutes the travel order; the lead belongs to the LOOP, so
                // match by reference — the linker reorders, it never rebuilds.
                int k = loops.IndexOf(loop);
                pass = WithLeads(pass, leads[k], z);
            }
            passes.Add(pass);
        }
    }

    /// <summary>The level's loops in the shared <see cref="RunLinker"/>'s travel order,
    /// updating the pen to the last loop's start — ONE ordering rule for the plunged and
    /// the ramped emission.</summary>
    private static List<Vector2d[]> LinkLoops(List<Vector2d[]> loops, ref Vector2d pen)
    {
        if (loops.Count == 0)
            return [];
        var ends = new (Vector3d Start, Vector3d End)[loops.Count];
        for (int i = 0; i < loops.Count; i++)
        {
            var start = new Vector3d(loops[i][0].X, loops[i][0].Y, 0);
            ends[i] = (start, start);                        // a closed loop starts where it ends
        }
        var linkage = RunLinker.Link(ends, new Vector3d(pen.X, pen.Y, 0));
        var ordered = new List<Vector2d[]>(loops.Count);
        foreach (var run in linkage.Order)
            ordered.Add(loops[run.Index]);
        pen = ordered[^1][0];
        return ordered;
    }

    /// <summary>True when the straight segment a-b stays at least <paramref name="clearance"/>
    /// from every boundary segment — what decides whether two rings may be LINKED at depth
    /// (a link cut through one stepover of web) rather than by retract and re-plunge. The
    /// distance is exact segment-to-segment, so a link across a concave pocket's gap is
    /// refused rather than gouged.</summary>
    private static bool SegmentClearOfBoundary(
        Region2d region, Vector2d a, Vector2d b, double clearance)
    {
        double limit2 = clearance * clearance;
        return Visit(region.Outer) && region.Holes.All(Visit);

        bool Visit(IReadOnlyList<Vector2d> loop)
        {
            for (int i = 0; i < loop.Count; i++)
            {
                var c = loop[i];
                var d = loop[(i + 1) % loop.Count];
                if (SegmentDistanceSquared(a, b, c, d) < limit2)
                    return false;
            }
            return true;
        }
    }

    private static double SegmentDistanceSquared(Vector2d a, Vector2d b, Vector2d c, Vector2d d)
    {
        double o1 = Cross(a, b, c), o2 = Cross(a, b, d);
        double o3 = Cross(c, d, a), o4 = Cross(c, d, b);
        if (((o1 > 0 && o2 < 0) || (o1 < 0 && o2 > 0))
            && ((o3 > 0 && o4 < 0) || (o3 < 0 && o4 > 0)))
            return 0;                                        // proper crossing
        return Math.Min(
            Math.Min(PointSegment2(c, a, b), PointSegment2(d, a, b)),
            Math.Min(PointSegment2(a, c, d), PointSegment2(b, c, d)));

        static double Cross(Vector2d p, Vector2d q, Vector2d r) =>
            (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);

        static double PointSegment2(Vector2d p, Vector2d s0, Vector2d s1)
        {
            var e = s1 - s0;
            double len2 = e.LengthSquared;
            double t = len2 > 0 ? Math.Clamp((p - s0).Dot(e) / len2, 0, 1) : 0;
            return (p - (s0 + e * t)).LengthSquared;
        }
    }

    /// <summary>The final profile pass with holding tabs: the loop is split at
    /// <paramref name="tabs"/> evenly spaced arc-length windows, and within each window the
    /// cutter rises to <paramref name="tabZ"/> (straight up, across, straight down — the
    /// writer's move classification turns those into rapid-up / cut / plunge).</summary>
    private static MillPass WithTabs(
        Vector2d[] loop, double z, int tabs, double tabZ, double tabWidth, string name)
    {
        // The loop's cumulative arc length, closing segment included.
        int n = loop.Length;
        var cumulative = new double[n + 1];
        for (int i = 0; i < n; i++)
            cumulative[i + 1] = cumulative[i] + (loop[(i + 1) % n] - loop[i]).Length;
        double total = cumulative[n];
        if (tabs * tabWidth >= total)
            throw new ArgumentException(
                $"'{name}': {tabs} tabs of width {tabWidth:0.###} consume the whole outline "
                + $"(length {total:0.###}).");

        // Tab k occupies [k·L/tabs, k·L/tabs + tabWidth) of arc length.
        var points = new List<Vector3d>();
        bool raised = false;
        void Emit(double s)
        {
            var p = PointAt(s);
            bool inTab = InTab(s);
            if (inTab != raised)
            {
                // The transition: rise or fall vertically at this point.
                points.Add(new Vector3d(p.X, p.Y, raised ? tabZ : z));
                points.Add(new Vector3d(p.X, p.Y, raised ? z : tabZ));
                raised = inTab;
            }
            else
            {
                points.Add(new Vector3d(p.X, p.Y, raised ? tabZ : z));
            }
        }
        bool InTab(double s)
        {
            double period = total / tabs;
            double within = s % period;
            return within < tabWidth;
        }
        Vector2d PointAt(double s)
        {
            int i = Array.BinarySearch(cumulative, s);
            if (i < 0) i = ~i - 1;
            i = Math.Clamp(i, 0, n - 1);
            double span = cumulative[i + 1] - cumulative[i];
            double t = span > 0 ? (s - cumulative[i]) / span : 0;
            var a = loop[i];
            var b = loop[(i + 1) % n];
            return new Vector2d(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));
        }

        // Walk the loop's own vertices, inserting the exact tab boundaries between them.
        var stations = new SortedSet<double>();
        for (int i = 0; i < n; i++)
            stations.Add(cumulative[i]);
        for (int k = 0; k < tabs; k++)
        {
            stations.Add(k * total / tabs % total);
            stations.Add((k * total / tabs + tabWidth) % total);
        }
        raised = InTab(0);
        foreach (double s in stations)
            Emit(s);
        // Close back to the start: tab 0 begins AT s = 0, so the stretch from the last station
        // to the seam is out-of-tab and must be CUT at depth first; the rise happens vertically
        // at the seam — the tab's own leading edge — never as a diagonal ramp that would leave
        // the closing stretch part-cut.
        var home = PointAt(0);
        points.Add(new Vector3d(home.X, home.Y, z));
        points.Add(new Vector3d(home.X, home.Y, tabZ));
        return new MillPass(points, IsClosed: false);
    }
}
