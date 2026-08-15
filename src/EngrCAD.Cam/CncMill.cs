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
/// chip-load derivation — a transcribed feeds/speeds catalogue is filed with the campaign.
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
/// the twin decoder reads a drill cycle with nothing new — canned G81/G83 cycles are filed),
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
        Region2d region, MillTool tool, double depth, string name = "pocket")
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(tool);
        tool.Validate();
        RequireDepth(depth);

        // The ring ladder, outermost (boundary pass) first as generated.
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
                loops.Add([.. shell.Outer]);
                foreach (var hole in shell.Holes)
                    loops.Add([.. hole]);
            }
            rings.Add(loops);
        }
        if (rings.Count == 0)
            throw new ArgumentException(
                $"'{name}': the tool (Ø{tool.Diameter:0.###}) does not fit the region at all — "
                + "the boundary pass at one radius inset is already empty.");

        var passes = new List<MillPass>();
        var pen = new Vector2d(region.Bounds.Min.X, region.Bounds.Min.Y);
        foreach (double z in DepthLevels(depth, tool.StepDown))
        {
            // Innermost ring first, climbing outward; loops within the level greedily linked.
            var loops = new List<Vector2d[]>();
            for (int k = rings.Count - 1; k >= 0; k--)
                loops.AddRange(rings[k]);
            AppendLinkedLoops(passes, loops, z, ref pen);
        }
        return new MillOperation(name, tool, passes);
    }

    /// <summary>
    /// Cuts along the outline at one tool radius to the chosen side, in depth steps, optionally
    /// leaving <paramref name="tabs"/> holding tabs on the FINAL pass (evenly spaced by arc
    /// length — a stated convention, not rounding luck — each <paramref name="tabWidth"/> long
    /// and <paramref name="tabHeight"/> tall).
    /// </summary>
    public static MillOperation Profile(
        Region2d region, MillTool tool, double depth, ProfileSide side,
        int tabs = 0, double tabHeight = 0, double tabWidth = 0, string name = "profile")
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
        var loops = new List<Vector2d[]>();
        foreach (var shell in offsets)
        {
            loops.Add([.. shell.Outer]);
            foreach (var hole in shell.Holes)
                loops.Add([.. hole]);
        }

        var levels = DepthLevels(depth, tool.StepDown);
        var passes = new List<MillPass>();
        var pen = new Vector2d(region.Bounds.Min.X, region.Bounds.Min.Y);
        for (int i = 0; i < levels.Count; i++)
        {
            bool final = i == levels.Count - 1;
            if (final && tabs > 0)
            {
                foreach (var loop in loops)
                    passes.Add(WithTabs(loop, levels[i], tabs, -depth + tabHeight, tabWidth, name));
                if (loops.Count > 0)
                    pen = new Vector2d(loops[^1][0].X, loops[^1][0].Y);
            }
            else
            {
                AppendLinkedLoops(passes, loops, levels[i], ref pen);
            }
        }
        return new MillOperation(name, tool, passes);
    }

    /// <summary>
    /// Drills each point to <paramref name="depth"/>, pecking in <paramref name="peck"/>-deep
    /// bites with a retract between (0 = one plunge). The moves are EXPANDED plain G0/G1 —
    /// down at the plunge rate, up as a rapid — so the twin decoder reads a drill cycle with
    /// nothing new; canned G81/G83 cycles are filed with the campaign.
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

        const double retract = 0.5;                          // chip-clear height above the stock
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
        List<MillPass> passes, List<Vector2d[]> loops, double z, ref Vector2d pen)
    {
        if (loops.Count == 0)
            return;
        var ends = new (Vector3d Start, Vector3d End)[loops.Count];
        for (int i = 0; i < loops.Count; i++)
        {
            var start = new Vector3d(loops[i][0].X, loops[i][0].Y, 0);
            ends[i] = (start, start);                        // a closed loop starts where it ends
        }
        var linkage = RunLinker.Link(ends, new Vector3d(pen.X, pen.Y, 0));
        foreach (var run in linkage.Order)
        {
            var loop = loops[run.Index];
            var points = new Vector3d[loop.Length];
            for (int i = 0; i < loop.Length; i++)
                points[i] = new Vector3d(loop[i].X, loop[i].Y, z);
            passes.Add(new MillPass(points, IsClosed: true));
        }
        var last = loops[linkage.Order[^1].Index][0];
        pen = last;
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
