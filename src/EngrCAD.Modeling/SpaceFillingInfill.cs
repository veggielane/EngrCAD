using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Modeling;

/// <summary>
/// A space-filling curve clipped to a sketch — the 2D infill / toolpath consumer of
/// <see cref="SpaceFillingCurve"/>: one continuous pass that reaches everywhere inside a
/// region, for a printed infill, a pocket clearing pass, an engraved fill or a heater track.
///
/// <para><b>Clipping needs no tolerance.</b> <see cref="SketchRegion"/> is the sketch's exact
/// 2D signed distance — lines and arcs in closed form, béziers Newton-refined — so "is this
/// point at least <c>clearance</c> inside the wall" is a comparison against an exact number
/// rather than a tolerant containment test. The default clearance is half the ACHIEVED
/// spacing, which is what stops a bead of that width from running into the perimeter.</para>
///
/// <para><b>Coverage is measured, not assumed.</b> <see cref="InfillPath.Footprint"/> strokes
/// the path through <see cref="Region2dOffset.Stroke"/> — already the toolpath-footprint
/// operation — and <see cref="InfillPath.CoveredArea"/> intersects that with the region, so
/// what a caller reads is the area the tool actually sweeps. It converges on the region's area
/// with order rather than being exact: the outer half-cell ring, the clearance band and the
/// corners a round join cannot reach are all genuinely uncovered.</para>
///
/// <para><b>What is refused, and why each one is loud.</b> A region whose thinnest feature is
/// under the achieved spacing would be missed <i>silently</i> — the one failure a fill must not
/// have — so a region the clearance erodes to nothing is named, and so is any connected PIECE
/// of the eroded region that catches no point of the curve (the alignment miss: there is room,
/// and the lattice's phase put nothing in it). A self-intersecting outline is refused by
/// <c>Region2d</c>'s own simplicity guard, which names the crossing segments, because "inside"
/// is exactly what the clip is asking; an open chain cannot be spelled, since a
/// <see cref="Sketch"/> validates closure at construction.
/// <see cref="SpaceFillingFamily.ZOrder"/> is refused by name too — its consecutive cells are
/// up to a grid width apart, so it is a spatial ordering rather than a path, and a "toolpath"
/// made of it would be mostly rapids.</para>
/// </summary>
public static class SpaceFillingInfill
{
    /// <summary>
    /// Fills <paramref name="region"/> with a space-filling curve at a spacing at or under
    /// <paramref name="spacing"/>.
    /// </summary>
    /// <param name="region">The outline to fill; its <c>WithHole</c> holes are respected.</param>
    /// <param name="spacing">The largest acceptable distance between neighbouring passes. The
    /// achieved spacing is reported on the result and is never coarser.</param>
    /// <param name="family">Which curve — <see cref="SpaceFillingFamily.Hilbert"/> by default
    /// (no long straight runs, so an infill has no preferred direction);
    /// <see cref="SpaceFillingFamily.Moore"/> if the path must return to its start.</param>
    /// <param name="clearance">How far inside the wall the path must stay. Null takes half the
    /// ACHIEVED spacing — a bead of that width then just touches the outline. Zero fills to the
    /// boundary.</param>
    /// <param name="maxSites">Refusal cap passed to the generator.</param>
    /// <param name="tiled">Lay the curve over the region's bounding RECTANGLE as a tiling of
    /// Hilbert blocks (<see cref="SpaceFillingCurve.OverTiled"/>) instead of over its bounding
    /// SQUARE. On a long thin region the square footprint spends most of the curve outside it and
    /// sets the achieved spacing by the region's LENGTH; tiling fits the rectangle, at the cost
    /// of cells that are no longer square (<see cref="SpaceFillingCurve.Anisotropy"/> reports how
    /// far). Hilbert only — Moore is a closed loop with no block ends to link and Gosper does not
    /// tile a rectangle.</param>
    /// <param name="blockOrder">The Hilbert block order when <paramref name="tiled"/> is set:
    /// higher is more isotropic and fits the rectangle more coarsely.</param>
    public static InfillPath Fill(
        Sketch region, double spacing,
        SpaceFillingFamily family = SpaceFillingFamily.Hilbert,
        double? clearance = null,
        int maxSites = SpaceFillingCurve.DefaultMaxSites,
        bool tiled = false,
        int blockOrder = 2)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (!(spacing > 0) || !double.IsFinite(spacing))
            throw new ArgumentOutOfRangeException(nameof(spacing), "The infill spacing must be positive and finite.");
        if (clearance is { } stated && (!(stated >= 0) || !double.IsFinite(stated)))
            throw new ArgumentOutOfRangeException(nameof(clearance), "The wall clearance must be zero or positive.");
        if (family == SpaceFillingFamily.ZOrder)
        {
            throw new ArgumentOutOfRangeException(
                nameof(family),
                "Z-order is a spatial ORDERING, not a curve: its consecutive cells are up to a whole "
                + "grid width apart, so a path built from it would be mostly rapid moves. Use "
                + "Hilbert (isotropic), Moore (closed) or Peano (longer straight runs) for a fill; "
                + "SpaceFillingCurve.Over still offers Z-order for indexing.");
        }
        if (tiled && family != SpaceFillingFamily.Hilbert)
        {
            throw new ArgumentOutOfRangeException(
                nameof(family),
                $"A tiled footprint links Hilbert BLOCKS by their two end corners, and {family} has "
                + "no such pair: Moore is a closed loop with no ends at all and Gosper's island does "
                + "not tile a rectangle. Ask for Hilbert, or drop tiled: true and accept the "
                + "bounding square.");
        }

        // Region2d.FromLoops runs the simplicity guard, so a self-crossing outline is named
        // here with its crossing segments rather than producing a fill over an arbitrary fill
        // rule. The polygonal regions are also what the erosion and the coverage measure read.
        var polygons = region.ToRegions();
        if (polygons.Count == 0)
            throw new ArgumentException("The infill region encloses no area.", nameof(region));

        // The POLYGONS' bounds, not the sketch's: Sketch.Bounds reads the outer loop alone, and
        // a region that comes back in several pieces (a "hole" that lies outside its outer
        // loop is a detached piece, not a hole) would then have a piece the curve never
        // reached — the silent miss this class exists to refuse.
        var bounds = Aabb.Empty;
        foreach (var polygon in polygons)
            bounds = bounds.Union(polygon.Bounds);

        var curve = tiled
            ? SpaceFillingCurve.OverTiled(bounds, spacing, blockOrder, maxSites)
            : SpaceFillingCurve.Over(bounds, family, spacing, maxSites);
        double wallClearance = clearance ?? curve.Spacing / 2;

        // The erosion says where a point COULD sit and still keep its clearance. An empty one
        // is the thin-feature refusal; a non-empty one that catches no curve point is the
        // alignment miss, and telling those apart is what makes the second message honest.
        var room = Region2dOffset.Offset(polygons, -wallClearance);
        if (room.Count == 0)
        {
            throw new ArgumentException(
                $"No part of this region is more than {wallClearance} from its own outline, so a "
                + $"fill at the achieved spacing {curve.Spacing} (asked for {spacing}) would miss it "
                + "entirely. Reduce the spacing, or pass clearance: 0 to fill to the boundary.",
                nameof(region));
        }

        var field = new SketchRegion(region);
        var points = curve.Points;
        var xs = new double[points.Count];
        var ys = new double[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            xs[i] = points[i].X;
            ys[i] = points[i].Y;
        }
        var distances = new double[points.Count];
        field.SignedDistance(xs, ys, distances);

        var runs = new List<IReadOnlyList<Vector2d>>();
        var current = new List<Vector2d>();
        var kept = new List<Vector2d>();
        for (int i = 0; i < points.Count; i++)
        {
            // Exact decision: the field is the sketch's own signed distance, and this is a
            // comparison against a stated clearance rather than a tolerance band.
            if (distances[i] <= -wallClearance)
            {
                current.Add(points[i]);
                kept.Add(points[i]);
                continue;
            }
            if (current.Count > 0)
            {
                runs.Add(current);
                current = [];
            }
        }
        if (current.Count > 0)
            runs.Add(current);

        if (kept.Count == 0)
        {
            throw new ArgumentException(
                $"The {family} curve at the achieved spacing {curve.Spacing} (asked for {spacing}) "
                + $"put no point inside this region at the required clearance {wallClearance}, "
                + "although the erosion says a pass would fit: the region's thinnest feature is "
                + "under the spacing, so the lattice stepped over it. Reduce the spacing.",
                nameof(region));
        }
        // Per CONNECTED PIECE of the room, which is the reachable form of the silent miss: the
        // erosion has already said a pass fits there, so a piece with no point in it was
        // stepped over by the lattice's phase rather than being too thin. A thin NECK inside
        // one piece is not caught here (the piece as a whole does catch points) and shows up in
        // CoveredFraction instead — stated rather than implied.
        foreach (var piece in room)
        {
            if (kept.Any(p => piece.Contains(p)))
                continue;
            throw new ArgumentException(
                $"One piece of this region has room for a pass at clearance {wallClearance} "
                + $"(it spans {piece.Bounds.Min.X}..{piece.Bounds.Max.X} by "
                + $"{piece.Bounds.Min.Y}..{piece.Bounds.Max.Y} after the erosion) but the {family} "
                + $"curve at the achieved spacing {curve.Spacing} put no point in it — the lattice's "
                + "phase stepped over it. Reduce the spacing.",
                nameof(region));
        }

        return new InfillPath(curve, wallClearance, runs, polygons);
    }
}

/// <summary>
/// One space-filling fill of a region: the generator's report, the path broken into the RUNS
/// the clip left, and the measurements a caller needs to believe it —
/// see <see cref="SpaceFillingInfill"/>.
/// </summary>
public sealed class InfillPath
{
    private readonly IReadOnlyList<Region2d> _regions;

    internal InfillPath(
        SpaceFillingCurve curve, double clearance,
        IReadOnlyList<IReadOnlyList<Vector2d>> runs, IReadOnlyList<Region2d> regions)
    {
        Curve = curve;
        Clearance = clearance;
        Runs = runs;
        _regions = regions;
        RegionArea = regions.Sum(r => r.Area);

        double length = 0;
        int points = 0;
        int isolated = 0;
        foreach (var run in runs)
        {
            points += run.Count;
            if (run.Count < 2)
                isolated++;
            for (int i = 1; i < run.Count; i++)
                length += run[i].DistanceTo(run[i - 1]);
        }
        Length = length;
        PointCount = points;
        IsolatedPoints = isolated;
    }

    /// <summary>The generator's own report — family, order, requested and ACHIEVED spacing.</summary>
    public SpaceFillingCurve Curve { get; }

    /// <summary>Which family the fill used.</summary>
    public SpaceFillingFamily Family => Curve.Family;

    /// <summary>The finite order the spacing request resolved to.</summary>
    public int Order => Curve.Order;

    /// <summary>The spacing the caller asked for.</summary>
    public double RequestedSpacing => Curve.RequestedSpacing;

    /// <summary>The spacing ACHIEVED — the distance between neighbouring passes. Never coarser
    /// than <see cref="RequestedSpacing"/>, and up to a factor of the family's radix finer,
    /// because the order is an integer.</summary>
    public double Spacing => Curve.Spacing;

    /// <summary>How far inside the outline the path was required to stay.</summary>
    public double Clearance { get; }

    /// <summary>The path, broken where the clip left the region: each run is a stretch of
    /// consecutive curve points, so within a run consecutive points are exactly
    /// <see cref="Spacing"/> apart. Between runs a machine rapids and a printer travels.</summary>
    public IReadOnlyList<IReadOnlyList<Vector2d>> Runs { get; }

    /// <summary>Total drawn length — the sum over runs, excluding the moves between them.</summary>
    public double Length { get; }

    /// <summary>How many curve points survived the clip.</summary>
    public int PointCount { get; }

    /// <summary>Runs of a single point: a cell the curve reaches but cannot draw through.
    /// Reported rather than dropped, because a fill that quietly loses them is a fill that
    /// quietly loses coverage — they DO contribute to <see cref="Footprint"/>.</summary>
    public int IsolatedPoints { get; }

    /// <summary>The number of travel moves between runs — <c>Runs.Count − 1</c>, and the
    /// number a family choice actually changes.</summary>
    public int TravelMoves => Math.Max(0, Runs.Count - 1);

    /// <summary>The share of the generated curve the clip threw away. On a region filling its own
    /// bounding square this is only the clearance band; on a long thin one it is most of the
    /// curve, which is what <c>tiled: true</c> exists to fix — reported as a number, so reaching
    /// for the tiled footprint is a decision rather than a guess.</summary>
    public double Waste => 1.0 - (double)PointCount / Curve.Points.Count;

    /// <summary>
    /// The THINNEST place in the region, by <see cref="Region2dThickness"/>' opposing-edge probe.
    ///
    /// <para>This is what the per-piece refusal cannot see. That check catches a whole connected
    /// piece of the eroded region the lattice stepped over; a NECK inside a piece that otherwise
    /// catches plenty of passes is invisible to it, because the piece as a whole is reached. A
    /// neck narrower than <c>Spacing + 2·Clearance</c> cannot hold a pass at all, and one under
    /// about twice that holds at most one — so comparing this number against the spacing is how a
    /// caller decides whether the <see cref="CoveredFraction"/> it is about to read is missing
    /// something local.</para>
    ///
    /// <para>Measured on demand rather than at construction, since it costs
    /// O(samples × outline edges) and most fills never ask.</para>
    /// </summary>
    public Region2dThicknessReport ThinnestFeature(int samplesPerEdge = 1) =>
        Region2dThickness.Measure(_regions, samplesPerEdge);

    /// <summary>The travel between runs, ordered — see <see cref="RunLinker"/>. The runs come
    /// back in CURVE order, which is already a good tour (consecutive runs are spatial
    /// neighbours), so what the linker buys is picking up the ends that order left behind, and
    /// <see cref="PathLinkage.Improvement"/> says how much that was.</summary>
    public PathLinkage Link() => RunLinker.Link(RunLinker.EndsOf(Runs));

    /// <summary>The region's enclosed area as the FLATTENED polygons carry it — deliberately
    /// not <c>Sketch.Area()</c>, which is exact for arcs and béziers. The footprint is polygonal
    /// too, so this is the denominator that makes <see cref="CoveredFraction"/> a ratio of two
    /// numbers measured the same way rather than a coverage figure fighting a chord error.</summary>
    public double RegionArea { get; }

    /// <summary>
    /// The swept footprint of the path at a bead of <paramref name="width"/> (null =
    /// <see cref="Spacing"/>, so neighbouring passes just touch), through
    /// <see cref="Region2dOffset.Stroke"/>. Isolated points contribute the disc a round cap
    /// would leave, so nothing the path visits is missing from the answer.
    /// <para>The union is <see cref="Region2dBoolean.UnionAll"/>'s balanced tree, and the pieces
    /// arrive in CURVE order — the best input that fold can get, since a space-filling curve's
    /// whole point is that consecutive pieces are neighbours. Recomputed per call: a union of
    /// thousands of slabs is not free.</para>
    /// </summary>
    public IReadOnlyList<Region2d> Footprint(double? width = null, OffsetJoin join = OffsetJoin.Round)
    {
        double bead = width ?? Spacing;
        if (!(bead > 0) || !double.IsFinite(bead))
            throw new ArgumentOutOfRangeException(nameof(width), "The bead width must be positive.");

        var pieces = new List<Region2d>();
        foreach (var run in Runs)
        {
            pieces.AddRange(run.Count >= 2
                ? Region2dOffset.Stroke(run, bead, StrokeCap.Round, join)
                : [Disc(run[0], bead / 2)]);
        }
        return Region2dBoolean.UnionAll(pieces);
    }

    /// <summary>The area of <see cref="Footprint"/> that lies inside the region — MEASURED by
    /// intersecting the two, never assumed from the path length.</summary>
    public double CoveredArea(double? width = null, OffsetJoin join = OffsetJoin.Round)
    {
        var footprint = Footprint(width, join);
        return footprint.Count == 0
            ? 0
            : Region2dBoolean.Intersection(footprint, _regions).Sum(r => r.Area);
    }

    /// <summary>What fraction of the region the bead sweeps. Converges on 1 as the order rises
    /// and never reaches it — the clearance band along every wall is uncovered by design.</summary>
    public double CoveredFraction(double? width = null, OffsetJoin join = OffsetJoin.Round) =>
        RegionArea > 0 ? CoveredArea(width, join) / RegionArea : 0;

    /// <summary>
    /// The EXACT swept footprint — the curved twin of <see cref="Footprint"/>, named rather
    /// than a silent upgrade because two estimators answering one question must both be
    /// nameable. With round joins and caps (the only style offered — they are the toolpath
    /// truth) the exact stroke IS the path's Minkowski sum with the bead disc, where the
    /// polygonal route is short of it by the inscribed-arc sagitta, a one-sided
    /// UNDER-estimate; ask for this one when the fill's own bead width is the deliverable.
    /// Costlier: every union runs the curved arrangement.
    /// </summary>
    public IReadOnlyList<CurvedRegion2d> ExactFootprint(double? width = null)
    {
        double bead = width ?? Spacing;
        if (!(bead > 0) || !double.IsFinite(bead))
            throw new ArgumentOutOfRangeException(nameof(width), "The bead width must be positive.");

        var pieces = new List<CurvedRegion2d>();
        foreach (var run in Runs)
        {
            if (run.Count >= 2)
            {
                var edges = new List<CurvedEdge2d>(run.Count - 1);
                for (int i = 1; i < run.Count; i++)
                    edges.Add(CurvedEdge2d.Line(run[i - 1], run[i]));
                pieces.AddRange(CurvedRegion2dOffset.Stroke(edges, bead));
            }
            else
            {
                pieces.Add(CurvedRegion2d.Disc(run[0], bead / 2));
            }
        }
        return CurvedRegion2dBoolean.UnionAll(pieces);
    }

    /// <summary>The exact footprint's area inside the region — measured by intersecting the
    /// two through the curved boolean, never assumed from the path length.</summary>
    public double ExactCoveredArea(double? width = null)
    {
        var footprint = ExactFootprint(width);
        if (footprint.Count == 0)
            return 0;
        var region = _regions.Select(CurvedRegion2d.FromRegion).ToList();
        return CurvedRegion2dBoolean.Intersection(footprint, region).Sum(r => r.Area);
    }

    /// <summary>The exact-footprint coverage fraction. The DENOMINATOR deliberately stays
    /// <see cref="RegionArea"/> — the flattened region is what the path was CLIPPED against,
    /// so this is the covered fraction of the region the fill was actually computed on; an
    /// exact sketch area would divide an exact numerator by a different region than the one
    /// that decided the runs. Always at least <see cref="CoveredFraction"/> for the same
    /// width — the polygonal footprint is inscribed in this one.</summary>
    public double ExactCoveredFraction(double? width = null) =>
        RegionArea > 0 ? ExactCoveredArea(width) / RegionArea : 0;

    /// <summary>The footprint an isolated point leaves under a round cap: a disc, as an
    /// INSCRIBED polygon matching <see cref="Region2dOffset"/>'s round joins — vertices exactly
    /// on the circle and chords inside it, so a lone point can never over-report coverage.</summary>
    private static Region2d Disc(in Vector2d centre, double radius)
    {
        // Inscribed, matching Region2dOffset's round joins: vertices exactly on the circle,
        // chords inside it, so an isolated point can never over-report coverage.
        int sides = Math.Max(8, (int)Math.Ceiling(
            Math.PI / Math.Acos(Math.Max(-1, 1 - Region2dOffset.DefaultArcTolerance / radius))));
        var loop = new Vector2d[sides];
        for (int i = 0; i < sides; i++)
        {
            double angle = 2 * Math.PI * i / sides;
            loop[i] = new Vector2d(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle));
        }
        return new Region2d(loop);
    }
}
