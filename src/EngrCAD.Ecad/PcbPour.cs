using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Ecad;

/// <summary>How a <see cref="CopperPour"/> is filled — as solid copper, or hatched (a grid of
/// copper strips, lighter and easier to etch/flex).</summary>
public enum PourFill
{
    /// <summary>Solid copper over the whole pour region.</summary>
    Solid,

    /// <summary>A crosshatch grid — the solid region intersected with a line/grid pattern, so the
    /// pour is a mesh of copper strips. Thermal-relief spokes stay SOLID (they are unioned after
    /// the hatch), so a hatched pour's pad connections are as robust as a solid pour's.</summary>
    Hatched,
}

/// <summary>What to do with a piece of a pour the net cannot reach (dead copper — a copper island
/// walled off from every same-net feature).</summary>
public enum DeadCopperPolicy
{
    /// <summary>Remove it (the default — a floating copper island is an EMC / manufacturing hazard,
    /// so a pour keeps only the connected component(s) touching the net). The removed area and
    /// count are still REPORTED (<see cref="PouredPour.DeadCopperArea"/>).</summary>
    Remove,

    /// <summary>Keep it — the dead copper stays in the fill. Still reported, so a caller who keeps
    /// it knows it is there.</summary>
    Keep,
}

/// <summary>
/// The thermal-relief spoke pattern a same-net THROUGH-HOLE pad is connected to a pour with — thin
/// radial <see cref="Spokes"/> that keep the pad solderable (they limit how much heat the pad sinks
/// into the plane) while an annular <see cref="Gap"/> of air isolates the rest of the pad from the
/// flood. <see cref="Spokes"/> = 0 is the DIRECT connection (the pour floods over the pad, no relief),
/// which is what <see cref="None"/> spells.
///
/// <para>SMD pads and vias are always DIRECT-connected (the pour floods over them) — relief is the
/// through-hole solder-thermal-mass rule, and a via or an SMD land has no such mass.</para>
/// </summary>
/// <param name="Spokes">The number of radial spokes (typically 4); 0 = direct connection (flood).</param>
/// <param name="SpokeWidth">The width of each spoke (mm), positive.</param>
/// <param name="Gap">The annular air gap (mm) between the pad and the pour, bridged only by the
/// spokes, positive.</param>
/// <param name="StartAngleDegrees">The angle of the first spoke (degrees, CCW from +X); the rest are
/// evenly spaced. The default 45° puts the spokes on the diagonals.</param>
public sealed record ThermalRelief(
    int Spokes = 4,
    double SpokeWidth = 0.5,
    double Gap = 0.4,
    double StartAngleDegrees = 45)
{
    /// <summary>The default relief — four diagonal spokes.</summary>
    public static ThermalRelief Default { get; } = new();

    /// <summary>No relief — a direct (flooded) connection to same-net through-hole pads.</summary>
    public static ThermalRelief None { get; } = new(Spokes: 0);

    /// <summary>Whether this relief actually relieves (has spokes and a gap) rather than
    /// flooding.</summary>
    public bool Relieves => Spokes > 0 && Gap > 0 && SpokeWidth > 0;

    internal void Validate()
    {
        if (Spokes < 0)
            throw new ArgumentException($"A thermal relief cannot have {Spokes} spokes.");
        if (Spokes > 0)
        {
            if (!(SpokeWidth > 0))
                throw new ArgumentException(
                    $"A thermal relief with spokes needs a positive spoke width (got {SpokeWidth:g6}).");
            if (!(Gap > 0))
                throw new ArgumentException(
                    $"A thermal relief with spokes needs a positive gap (got {Gap:g6}).");
        }
    }
}

/// <summary>
/// The crosshatch pattern a <see cref="PourFill.Hatched"/> pour is cut to — parallel copper strips
/// of <see cref="LineWidth"/> at <see cref="Spacing"/>, at <see cref="AngleDegrees"/> (plus a
/// perpendicular set when <see cref="CrossHatch"/>). The solid pour is intersected with this grid,
/// so the result is exactly the region ∩ pattern — the tamper-mesh construction.
/// </summary>
/// <param name="Spacing">The centre-to-centre spacing of the strips (mm), positive.</param>
/// <param name="LineWidth">The width of each copper strip (mm), positive and less than the
/// spacing.</param>
/// <param name="AngleDegrees">The angle of the strips (degrees, CCW from +X).</param>
/// <param name="CrossHatch">True for a crosshatch (a second set perpendicular to the first); false
/// for parallel strips only.</param>
public sealed record HatchStyle(
    double Spacing = 1.0,
    double LineWidth = 0.3,
    double AngleDegrees = 45,
    bool CrossHatch = true)
{
    /// <summary>The default hatch — a 1 mm 45° crosshatch of 0.3 mm strips.</summary>
    public static HatchStyle Default { get; } = new();

    internal void Validate()
    {
        if (!(Spacing > 0))
            throw new ArgumentException($"A hatch needs a positive spacing (got {Spacing:g6}).");
        if (!(LineWidth > 0))
            throw new ArgumentException($"A hatch needs a positive line width (got {LineWidth:g6}).");
        if (!(LineWidth < Spacing))
            throw new ArgumentException(
                $"A hatch line width ({LineWidth:g6}) must be less than its spacing ({Spacing:g6}); "
                + "at or above the spacing the strips merge into a solid pour.");
    }
}

/// <summary>
/// A copper pour (a ground plane / power plane): copper flooded over a board <see cref="Layer"/>,
/// assigned to a <see cref="Net"/>. It is LAYOUT TRUTH (a pour is part of the design), so it rides
/// in the layout file and DERIVES into copper features the DRC and connectivity engine read exactly
/// like any other copper — a GND pour JOINS every GND pad it touches, so the GND ratsnest empties.
///
/// <para><b>The fill region is exact and the tamper-mesh construction.</b> The pour = (the board
/// area, or a stated <see cref="Outline"/>, inset from the board edge by <see cref="EdgeClearance"/>)
/// MINUS (every OTHER-net copper feature and drill on the layer, grown by <see cref="Clearance"/> /
/// <see cref="DrillClearance"/>) MINUS (a thermal-relief annulus around each same-net through-hole
/// pad, bridged by spokes). Built with <c>CurvedRegion2dOffset</c> / <c>CurvedRegion2dBoolean</c>,
/// no tolerance — so a poured board clears every other net by construction and passes
/// <see cref="PcbDrc.Check(PcbCopperModel, DrcRuleSet?)"/>.</para>
///
/// <para><b>Clearances live on the pour, not on a rule set.</b> A pour is a design object with its
/// own clearances (the defaults exceed the <see cref="DrcRuleSet.Default"/> minimums, so a default
/// pour passes the default DRC with margin), so the fill is a pure function of the pour and the base
/// copper — nothing threads a rule set through <c>PcbCopperModel.FromLayout</c>.</para>
/// </summary>
/// <param name="Net">The net the pour carries (a signal or stub net of the schematic).</param>
/// <param name="Layer">The copper layer the pour is on (a copper layer of the board stackup).</param>
/// <param name="Outline">The pour outline in board-local 2D coordinates, or null for the whole
/// board. When set it is intersected with the board interior (a stated outline off the board is
/// refused by name).</param>
/// <param name="Fill">Solid or hatched.</param>
/// <param name="Clearance">The gap the pour keeps from OTHER-net copper on the layer (mm).</param>
/// <param name="DrillClearance">The gap the pour keeps from OTHER-net drilled holes (mm), a
/// cross-layer rule (a drill goes through the stack).</param>
/// <param name="EdgeClearance">The gap the pour keeps from the board outline (mm).</param>
/// <param name="Relief">The thermal-relief pattern for same-net through-hole pads; null =
/// <see cref="ThermalRelief.Default"/> (four spokes). <see cref="ThermalRelief.None"/> floods.</param>
/// <param name="Hatch">The hatch pattern when <paramref name="Fill"/> is
/// <see cref="PourFill.Hatched"/>; null = <see cref="HatchStyle.Default"/>.</param>
/// <param name="DeadCopper">What to do with copper islands the net cannot reach (default:
/// remove).</param>
public sealed record CopperPour(
    string Net,
    string Layer,
    IReadOnlyList<Vector2d>? Outline = null,
    PourFill Fill = PourFill.Solid,
    double Clearance = 0.2,
    double DrillClearance = 0.25,
    double EdgeClearance = 0.3,
    ThermalRelief? Relief = null,
    HatchStyle? Hatch = null,
    DeadCopperPolicy DeadCopper = DeadCopperPolicy.Remove)
{
    /// <summary>The resolved relief (the stated one, or <see cref="ThermalRelief.Default"/>).</summary>
    public ThermalRelief ResolvedRelief => Relief ?? ThermalRelief.Default;

    /// <summary>The resolved hatch (the stated one, or <see cref="HatchStyle.Default"/>).</summary>
    public HatchStyle ResolvedHatch => Hatch ?? HatchStyle.Default;

    internal void Validate()
    {
        if (!(Clearance > 0))
            throw new ArgumentException($"A pour needs a positive clearance (got {Clearance:g6}).");
        if (!(DrillClearance > 0))
            throw new ArgumentException(
                $"A pour needs a positive drill clearance (got {DrillClearance:g6}).");
        if (!(EdgeClearance > 0))
            throw new ArgumentException(
                $"A pour needs a positive edge clearance (got {EdgeClearance:g6}).");
        ResolvedRelief.Validate();
        if (Fill == PourFill.Hatched)
            ResolvedHatch.Validate();
    }
}

/// <summary>
/// The filled copper of one pour — its kept connected region(s), plus the diagnostics a pour is
/// measured by: the dead copper the net could not reach (removed or kept per policy), and how many
/// through-hole pads got a thermal relief.
/// </summary>
/// <param name="Net">The pour's net.</param>
/// <param name="Layer">The pour's layer.</param>
/// <param name="Regions">The kept filled region(s) — one per connected component that reaches the
/// net (or every component when <see cref="DeadCopperPolicy.Keep"/>).</param>
/// <param name="DeadCopperRegions">How many copper islands the net could not reach.</param>
/// <param name="DeadCopperArea">The total area (mm²) of those islands.</param>
/// <param name="RelievedPads">How many same-net through-hole pads got a thermal relief.</param>
/// <param name="Spokes">How many thermal-relief spokes were placed in total.</param>
public sealed record PouredPour(
    string Net,
    string Layer,
    IReadOnlyList<CurvedRegion2d> Regions,
    int DeadCopperRegions,
    double DeadCopperArea,
    int RelievedPads,
    int Spokes)
{
    /// <summary>The total copper area of the kept fill (mm²).</summary>
    public double Area => Regions.Sum(r => r.Area);

    /// <summary>Whether the pour filled nothing (nothing reached the net, or the outline was
    /// entirely eaten by clearances).</summary>
    public bool IsEmpty => Regions.Count == 0;
}

/// <summary>
/// Builds a <see cref="CopperPour"/>'s filled copper against a board's base copper — the exact
/// tamper-mesh construction (a region grown / clipped by <c>Region2dOffset</c> /
/// <c>CurvedRegion2dBoolean</c>, no tolerance). See <see cref="CopperPour"/> for the recipe.
/// </summary>
public static class CopperPourBuilder
{
    /// <summary>
    /// Fills a pour over a board's base copper (the pads / vias / traces — NOT other pours; v1 does
    /// no inter-pour priority). The result is the kept connected region(s) plus diagnostics.
    /// </summary>
    /// <param name="baseModel">The board's copper WITHOUT pours (pads, vias, traces, drills).</param>
    /// <param name="pour">The pour to fill.</param>
    /// <exception cref="ArgumentException">The pour outline lies entirely off the board, refused by
    /// name.</exception>
    public static PouredPour Fill(PcbCopperModel baseModel, CopperPour pour)
    {
        ArgumentNullException.ThrowIfNull(baseModel);
        ArgumentNullException.ThrowIfNull(pour);
        pour.Validate();

        // 1. The start region: the board interior inset by the edge clearance, intersected with the
        //    pour outline when one is stated. A polygon's inward offset is exact.
        var board = new Region2d([.. baseModel.Board.OutlinePoints]);
        IReadOnlyList<CurvedRegion2d> start =
            [.. Region2dOffset.Offset(board, -pour.EdgeClearance).Select(CurvedRegion2d.FromRegion)];
        if (pour.Outline is { Count: >= 3 })
        {
            var outline = CurvedRegion2d.FromRegion(new Region2d([.. pour.Outline]));
            start = CurvedRegion2dBoolean.Intersection(start, [outline]);
            if (start.Count == 0)
                throw new ArgumentException(
                    $"The pour outline for net '{pour.Net}' lies entirely off the board (nothing to "
                    + "fill after the edge clearance).", nameof(pour));
        }
        if (start.Count == 0)
            return new PouredPour(pour.Net, pour.Layer, [], 0, 0, 0, 0);

        // 2. Subtract every OTHER-net copper feature on the layer, grown by the clearance, and every
        //    OTHER-net (or netless) drill, grown by the drill clearance (cross-layer). Growing the
        //    obstacle by the clearance keeps the pour at least that far away — the tamper-mesh rule.
        var keepouts = new List<CurvedRegion2d>();
        foreach (var f in baseModel.Copper)
            if (f.Layer == pour.Layer && !SameNet(f.Net, pour.Net))
                keepouts.AddRange(Grow(f.Region, pour.Clearance));
        foreach (var d in baseModel.Drills)
            if (!SameNet(d.Net, pour.Net))
                keepouts.AddRange(Grow(CurvedRegion2d.Disc(d.Center, d.Diameter / 2), pour.DrillClearance));

        var flood = keepouts.Count == 0
            ? start
            : CurvedRegion2dBoolean.Difference(start, CurvedRegion2dBoolean.UnionAll(keepouts));
        if (flood.Count == 0)
            return new PouredPour(pour.Net, pour.Layer, [], 0, 0, 0, 0);

        // 3. Thermal relief: carve an annular gap around each same-net through-hole pad, so the pad
        //    is isolated from the flood except along the spokes (added solid in step 5).
        var relief = pour.ResolvedRelief;
        var reliefPads = ReliefPads(baseModel, pour);
        int relievedPads = 0, spokeCount = 0;
        var spokes = new List<CurvedRegion2d>();
        if (relief.Relieves && reliefPads.Count > 0)
        {
            var reliefDiscs = new List<CurvedRegion2d>(reliefPads.Count);
            foreach (var (center, padRadius) in reliefPads)
            {
                reliefDiscs.Add(CurvedRegion2d.Disc(center, padRadius + relief.Gap));
                spokes.AddRange(Spokes(center, padRadius, relief));
                relievedPads++;
                spokeCount += relief.Spokes;
            }
            flood = CurvedRegion2dBoolean.Difference(flood, reliefDiscs);
        }

        // 4. Hatch the flood (before the spokes, so the spokes stay solid).
        if (pour.Fill == PourFill.Hatched && flood.Count > 0)
        {
            var grid = HatchGrid(FloodBounds(flood), pour.ResolvedHatch);
            if (grid.Count > 0)
                flood = CurvedRegion2dBoolean.Intersection(flood, grid);
        }

        // 5. Union the solid spokes back in — they bridge each relieved pad to the flood.
        if (spokes.Count > 0)
            flood = CurvedRegion2dBoolean.Union(flood, spokes);

        // 6. Island removal: keep only the connected component(s) the net can reach (a component that
        //    touches a same-net feature). Each returned region IS a connected component.
        var netFeatures = baseModel.Copper
            .Where(f => f.Layer == pour.Layer && SameNet(f.Net, pour.Net))
            .Select(f => f.Region)
            .ToList();

        var kept = new List<CurvedRegion2d>();
        var dead = new List<CurvedRegion2d>();
        foreach (var component in flood)
            (ReachesNet(component, netFeatures) ? kept : dead).Add(component);

        var result = pour.DeadCopper == DeadCopperPolicy.Keep ? flood : kept;
        return new PouredPour(
            pour.Net, pour.Layer, result,
            dead.Count, dead.Sum(r => r.Area), relievedPads, spokeCount);
    }

    // ---- thermal relief -----------------------------------------------------

    /// <summary>The same-net THROUGH-HOLE pads on the pour's layer to relieve — a drill of the pour's
    /// net that is not a via (vias flood), paired with its copper radius (read off the pad's own
    /// copper region, so an oval pad is covered on its long axis too).</summary>
    private static IReadOnlyList<(Vector2d Center, double PadRadius)> ReliefPads(
        PcbCopperModel model, CopperPour pour)
    {
        var viaSources = new HashSet<string>(model.Vias.Select(v => v.Source), StringComparer.Ordinal);
        var padBySource = new Dictionary<string, CopperFeature>(StringComparer.Ordinal);
        foreach (var f in model.Copper)
            if (f.Layer == pour.Layer && f.Source.Length > 0)
                padBySource[f.Source] = f;

        var pads = new List<(Vector2d, double)>();
        foreach (var d in model.Drills)
        {
            if (!SameNet(d.Net, pour.Net) || viaSources.Contains(d.Source))
                continue;
            double padRadius = padBySource.TryGetValue(d.Source, out var f)
                ? 0.5 * Math.Max(f.Region.Bounds.Max.X - f.Region.Bounds.Min.X,
                                 f.Region.Bounds.Max.Y - f.Region.Bounds.Min.Y)
                : d.PadDiameter / 2;
            if (padRadius > 0)
                pads.Add((d.Center, padRadius));
        }
        return pads;
    }

    /// <summary>The spoke rectangles bridging a relieved pad to the flood — radial strips of the
    /// relief's spoke width, overlapping the pad copper (inner) and the flood (outer) so both
    /// connections are a robust AREA overlap (a tangent touch is measure-zero and would not
    /// connect).</summary>
    private static IEnumerable<CurvedRegion2d> Spokes(
        Vector2d center, double padRadius, ThermalRelief relief)
    {
        double overlap = Math.Min(relief.Gap, padRadius * 0.5);
        double inner = Math.Max(padRadius - overlap, padRadius * 0.4);
        double outer = padRadius + relief.Gap + overlap;
        double halfWidth = relief.SpokeWidth / 2;
        double start = relief.StartAngleDegrees * Math.PI / 180.0;
        for (int k = 0; k < relief.Spokes; k++)
        {
            double angle = start + k * 2 * Math.PI / relief.Spokes;
            var radial = new Vector2d(Math.Cos(angle), Math.Sin(angle));
            var perp = new Vector2d(-radial.Y, radial.X);
            var a = center + radial * inner - perp * halfWidth;
            var b = center + radial * outer - perp * halfWidth;
            var c = center + radial * outer + perp * halfWidth;
            var e = center + radial * inner + perp * halfWidth;
            yield return new CurvedRegion2d([
                CurvedEdge2d.Line(a, b), CurvedEdge2d.Line(b, c),
                CurvedEdge2d.Line(c, e), CurvedEdge2d.Line(e, a),
            ]);
        }
    }

    // ---- hatch --------------------------------------------------------------

    private static Aabb FloodBounds(IReadOnlyList<CurvedRegion2d> flood)
    {
        var bounds = Aabb.Empty;
        foreach (var region in flood)
            bounds = bounds.Union(region.Bounds);
        return bounds;
    }

    /// <summary>The crosshatch grid over a bounds — parallel stroked lines at the hatch angle (and a
    /// perpendicular set for a crosshatch), unioned. Intersecting the flood with this is the region ∩
    /// pattern that makes a hatched pour.</summary>
    private static IReadOnlyList<CurvedRegion2d> HatchGrid(in Aabb bounds, HatchStyle hatch)
    {
        var lines = new List<CurvedRegion2d>();
        AddLines(lines, bounds, hatch.AngleDegrees, hatch.Spacing, hatch.LineWidth);
        if (hatch.CrossHatch)
            AddLines(lines, bounds, hatch.AngleDegrees + 90, hatch.Spacing, hatch.LineWidth);
        return lines.Count == 0 ? [] : CurvedRegion2dBoolean.UnionAll(lines);
    }

    private static void AddLines(
        List<CurvedRegion2d> lines, in Aabb bounds, double angleDegrees, double spacing, double width)
    {
        double a = angleDegrees * Math.PI / 180.0;
        var dir = new Vector2d(Math.Cos(a), Math.Sin(a));      // along the strip
        var normal = new Vector2d(-dir.Y, dir.X);              // across the strips
        var center = new Vector2d(
            (bounds.Min.X + bounds.Max.X) / 2, (bounds.Min.Y + bounds.Max.Y) / 2);
        double halfDiag = 0.5 * Math.Sqrt(
            Math.Pow(bounds.Max.X - bounds.Min.X, 2) + Math.Pow(bounds.Max.Y - bounds.Min.Y, 2)) + spacing;
        int count = (int)Math.Ceiling(halfDiag / spacing);
        for (int k = -count; k <= count; k++)
        {
            var mid = center + normal * (k * spacing);
            var p0 = mid - dir * halfDiag;
            var p1 = mid + dir * halfDiag;
            foreach (var strip in CurvedRegion2dOffset.Stroke(
                [CurvedEdge2d.Line(p0, p1)], width, StrokeCap.Butt, OffsetJoin.Miter))
                lines.Add(strip);
        }
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>Whether a pour component reaches the net — it intersects (exactly) any same-net copper
    /// feature on the layer. An AABB gap proves no touch (a box is a superset of its region).</summary>
    private static bool ReachesNet(CurvedRegion2d component, IReadOnlyList<CurvedRegion2d> netFeatures)
    {
        foreach (var feature in netFeatures)
        {
            if (!BoxesTouch(component.Bounds, feature.Bounds))
                continue;
            if (CurvedRegion2dBoolean.Intersection([component], [feature]).Count > 0)
                return true;
        }
        return false;
    }

    private static bool BoxesTouch(in Aabb a, in Aabb b) =>
        a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y;

    private static IReadOnlyList<CurvedRegion2d> Grow(CurvedRegion2d region, double delta) =>
        CurvedRegion2dOffset.Offset(region, delta, OffsetJoin.Round);

    private static bool SameNet(string? a, string? b) => a is not null && b is not null && a == b;
}

public sealed partial class PcbLayout
{
    private readonly List<CopperPour> _pours = [];

    /// <summary>The copper pours on this layout, in declaration order. A pour is LAYOUT TRUTH (a
    /// ground / power plane is part of the design), so pours round-trip in the layout file — a layout
    /// with no pours saves byte-identically to a pre-pour one.</summary>
    public IReadOnlyList<CopperPour> Pours => _pours;

    /// <summary>
    /// Adds a copper pour. The net must be a signal or stub net of the schematic, the layer must be a
    /// copper layer of the board stackup, a stated outline must have at least three points and lie on
    /// the board, and the clearances / relief / hatch must be positive — all refused by name.
    /// </summary>
    /// <exception cref="ArgumentException">The net or layer does not exist, the outline is off the
    /// board or malformed, or a clearance is non-positive — refused by name.</exception>
    public PcbLayout AddPour(CopperPour pour)
    {
        ValidatePour(pour);
        _pours.Add(pour);
        return this;
    }

    /// <summary>Adds a copper pour from its parts (see <see cref="AddPour(CopperPour)"/>).</summary>
    public PcbLayout AddPour(string net, string layer, PourFill fill = PourFill.Solid) =>
        AddPour(new CopperPour(net, layer, Fill: fill));

    // Used by the loader to reconstruct a pour verbatim (the same validation, so a saved-then-loaded
    // pour is refused for the same reasons a freshly-declared one is).
    internal void AddLoadedPour(CopperPour pour)
    {
        try
        {
            ValidatePour(pour);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"A saved pour is invalid: {ex.Message}");
        }
        _pours.Add(pour);
    }

    private void ValidatePour(CopperPour pour)
    {
        ArgumentNullException.ThrowIfNull(pour);
        pour.Validate();
        if (!Schematic.Nets.Any(n => n.Name == pour.Net))
            throw new ArgumentException(
                $"Pour names net '{pour.Net}', which the schematic does not declare (a pour carries a "
                + $"net, so it must be a net the design defines). Nets: "
                + $"{string.Join(", ", Schematic.Nets.Select(n => n.Name))}.", nameof(pour));
        if (!Board.Stackup.Coppers.Any(c => c.Name == pour.Layer))
            throw new ArgumentException(
                $"Pour on net '{pour.Net}' names copper layer '{pour.Layer}', which the stackup does not "
                + $"contain (layers: {string.Join(", ", Board.Stackup.Coppers.Select(c => c.Name))}).",
                nameof(pour));
        if (pour.Outline is not null)
        {
            if (pour.Outline.Count < 3)
                throw new ArgumentException(
                    $"Pour on net '{pour.Net}' has an outline with fewer than three points.", nameof(pour));
            // A stated outline must overlap the board (an outline off the board fills nothing).
            var boardRegion = CurvedRegion2d.FromRegion(new Region2d([.. Board.OutlinePoints]));
            var outlineRegion = CurvedRegion2d.FromRegion(new Region2d([.. pour.Outline]));
            if (CurvedRegion2dBoolean.Intersection([boardRegion], [outlineRegion]).Count == 0)
                throw new ArgumentException(
                    $"Pour on net '{pour.Net}' has an outline that lies entirely off the board.",
                    nameof(pour));
        }
    }
}
