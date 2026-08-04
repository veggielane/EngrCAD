using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Modeling;

/// <summary>Which orientations the packer may choose from. Every member is a FINITE set of
/// poses, which is what keeps the search deterministic and exhaustive.</summary>
public enum PackRotation
{
    /// <summary>Parts keep the orientation they were modelled in — the v1 contract.</summary>
    None,

    /// <summary>Quarter turns about z. Exact (a quarter turn is a sign swap, never a
    /// <c>cos</c>), and for <see cref="PackNesting.BoundingBox"/> a quarter turn merely
    /// transposes the footprint box, so only two of the four are distinguishable there.</summary>
    Quarter,

    /// <summary>Any angle. <b>Refused by name</b> — see
    /// <see cref="Packing.Pack(IReadOnlyList{Shape}, double, double, PackOptions)"/> for the
    /// reason (a continuous orientation has no finite candidate set, so it needs a no-fit
    /// polygon or an optimiser with a stated stopping rule, and neither is here yet).</summary>
    Free,
}

/// <summary>What shape the packer keeps parts apart by.</summary>
public enum PackNesting
{
    /// <summary>Footprint bounding boxes — fast, and the v1 contract.</summary>
    BoundingBox,

    /// <summary>The true silhouette outline, so one part may sit in another's concavity or
    /// inside its through hole. Searched on a raster whose cells are
    /// <see cref="PackOptions.Resolution"/> across; the rasterization is CONSERVATIVE, so a
    /// coarse grid can only refuse a legal placement, never accept an illegal one.</summary>
    Outline,
}

/// <summary>
/// The optional half of <see cref="Packing.Pack(IReadOnlyList{Shape}, double, double, PackOptions)"/>.
/// The default value is the v1 contract exactly — no rotation, bounding-box nesting — and
/// packing with it produces bit-identical placements to the simpler overload.
/// </summary>
public sealed class PackOptions
{
    /// <summary>Clearance held between parts AND to the plate edges.</summary>
    public double Gap { get; init; } = 2;

    /// <summary>Which orientations the packer may choose from (default: none).</summary>
    public PackRotation Rotation { get; init; } = PackRotation.None;

    /// <summary>Whether parts are kept apart by their boxes or their outlines
    /// (default: boxes).</summary>
    public PackNesting Nesting { get; init; } = PackNesting.BoundingBox;

    /// <summary>Raster cell size for <see cref="PackNesting.Outline"/>, in model units.
    /// Placements are quantized to it, so a finer grid packs tighter and costs more; null
    /// takes <c>min(plateWidth, plateDepth) / 256</c>, which keeps the grid a fixed size
    /// whatever the plate. Unused by <see cref="PackNesting.BoundingBox"/>.</summary>
    public double? Resolution { get; init; }

    /// <summary>Mesh quality for the silhouette footprints (a footprint is measured from
    /// the tessellation, so extremes read a chord's sagitta small at coarse quality — the
    /// <see cref="Shape.Bounds"/> caveat).</summary>
    public MeshQuality? Quality { get; init; }
}

/// <summary>Where one part landed on the plate: the part is turned by
/// <see cref="RotationDegrees"/> about z (about the part's own origin) and THEN translated by
/// <see cref="Offset"/> in XY; <see cref="Footprint"/> is its measured silhouette bounds AS
/// TURNED and before the offset (degenerate in z, the silhouette convention).</summary>
public readonly record struct PackPlacement(
    int Index, Vector2d Offset, Aabb Footprint, int RotationDegrees = 0);

/// <summary>A computed build-plate layout — the placements plus the plate they were
/// packed onto. Placements are in INPUT order (each carries its index), so callers zip
/// them with their part lists directly.</summary>
public sealed class PackLayout
{
    private readonly IReadOnlyList<Region2d>[] _outlines;

    internal PackLayout(
        IReadOnlyList<PackPlacement> placements, IReadOnlyList<Region2d>[] outlines,
        double width, double depth, double gap, PackRotation rotation, PackNesting nesting)
    {
        Placements = placements;
        _outlines = outlines;
        PlateWidth = width;
        PlateDepth = depth;
        Gap = gap;
        Rotation = rotation;
        Nesting = nesting;

        double packed = 0, boxes = 0, usedDepth = 0, usedWidth = 0;
        for (int i = 0; i < placements.Count; i++)
        {
            packed += outlines[i].Sum(region => region.Area);
            var footprint = placements[i].Footprint;
            boxes += footprint.Size.X * footprint.Size.Y;
            usedWidth = Math.Max(usedWidth, footprint.Max.X + placements[i].Offset.X + gap);
            usedDepth = Math.Max(usedDepth, footprint.Max.Y + placements[i].Offset.Y + gap);
        }
        PackedArea = packed;
        FootprintArea = boxes;
        UsedWidth = usedWidth;
        UsedDepth = usedDepth;
    }

    /// <summary>One placement per input part, in input order.</summary>
    public IReadOnlyList<PackPlacement> Placements { get; }

    public double PlateWidth { get; }
    public double PlateDepth { get; }

    /// <summary>The clearance this layout holds between parts and to the plate edges.</summary>
    public double Gap { get; }

    /// <summary>The orientation freedom this layout was packed with.</summary>
    public PackRotation Rotation { get; }

    /// <summary>The separation shape this layout was packed with.</summary>
    public PackNesting Nesting { get; }

    /// <summary>Total area of the packed parts' true silhouette OUTLINES — measured the same
    /// way whichever <see cref="PackNesting"/> was used, so two settings are comparable.</summary>
    public double PackedArea { get; }

    /// <summary>Total area of the packed parts' footprint BOXES. The gap between this and
    /// <see cref="PackedArea"/> is what outline nesting has to play with.</summary>
    public double FootprintArea { get; }

    /// <summary>Plate depth actually consumed: the deepest placed footprint plus the gap
    /// (measured from y = 0, so it includes the leading gap).</summary>
    public double UsedDepth { get; }

    /// <summary>Plate width actually consumed, measured the same way.</summary>
    public double UsedWidth { get; }

    /// <summary>The plate strip the layout consumed — full width by <see cref="UsedDepth"/>,
    /// because both packers fill rows across the plate.</summary>
    public double UsedArea => PlateWidth * UsedDepth;

    /// <summary>Packed outline area over <see cref="UsedArea"/> — the number to compare two
    /// packing settings on the same parts.</summary>
    public double Utilisation => UsedArea > 0 ? PackedArea / UsedArea : 0;

    /// <summary>The packed parts as posed shapes (input order) — feed them to a
    /// <c>Scene</c> or <c>StlWriter</c> as one plate. A part placed at 0 degrees is
    /// translated only, with no rotation node added to its graph.</summary>
    public IReadOnlyList<Shape> Apply(IReadOnlyList<Shape> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count != Placements.Count)
            throw new ArgumentException(
                $"The layout was computed for {Placements.Count} part(s), not {parts.Count}.",
                nameof(parts));
        var placed = new Shape[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            var placement = Placements[i];
            var shape = placement.RotationDegrees == 0
                ? parts[i]
                : parts[i].Transform(Packing.QuarterTurn(placement.RotationDegrees));
            placed[i] = shape.Translate(placement.Offset.X, placement.Offset.Y, 0);
        }
        return placed;
    }

    /// <summary>The measured silhouette outline of part <paramref name="index"/> as it sits
    /// on the plate (turned and translated) — the exact 2D geometry to check a layout
    /// against, and what <see cref="PackedArea"/> is measured from.</summary>
    public IReadOnlyList<Region2d> PlacedOutline(int index)
    {
        var placement = Placements[index];
        var placed = new List<Region2d>(_outlines[index].Count);
        foreach (var region in _outlines[index])
        {
            placed.Add(new Region2d(
                Packing.PoseLoop(region.Outer, placement.RotationDegrees, placement.Offset),
                [.. region.Holes.Select(hole =>
                    (IReadOnlyList<Vector2d>)Packing.PoseLoop(hole, placement.RotationDegrees, placement.Offset))]));
        }
        return placed;
    }
}

/// <summary>
/// 2D bin packing of part footprints onto a build plate — build123d's <c>pack</c>, for
/// laying out a multi-part print before STL export. Footprints come from the parts'
/// <see cref="Shape.Silhouette"/> (so an overhang wider than the base counts), and there are
/// two packers, both deterministic — no randomness, no time-based cutoff, so the same parts
/// always give the same plate.
///
/// <para><b>Shelf packing</b> (<see cref="PackNesting.BoundingBox"/>, the default) sorts
/// parts by footprint depth (then width, then index) and lays them left-to-right into rows
/// from the plate's front-left corner, each row as deep as its deepest member. Simple and
/// predictable rather than optimal.</para>
///
/// <para><b>Outline nesting</b> (<see cref="PackNesting.Outline"/>) keeps parts apart by
/// their true outlines instead, so one part may sit in another's concavity or inside its
/// through hole. Each outline is grown by HALF the gap — dilation by a disk, so two grown
/// outlines being disjoint IS "these parts are at least <c>gap</c> apart", one existing
/// operation rather than a new distance predicate — and the grown outlines are searched on a
/// raster in bottom-left-first order. The rasterization is CONSERVATIVE (a cell is occupied
/// if the grown outline touches it at all), so a coarse grid can only refuse a legal
/// placement, never accept an illegal one.</para>
///
/// <para><b>Rotation</b> is <see cref="PackRotation.Quarter"/> only: four poses, exact (a
/// quarter turn is a sign swap). Free rotation is refused by name.</para>
/// </summary>
public static class Packing
{
    /// <summary>
    /// Packs <paramref name="parts"/> onto a <paramref name="plateWidth"/> ×
    /// <paramref name="plateDepth"/> plate with at least <paramref name="gap"/> between
    /// parts and to the plate edges. The plate spans [0, width] × [0, depth] in XY;
    /// parts are translated in XY only (z — how the part sits — is the caller's).
    /// Throws naming the first part that does not fit, its footprint and the plate.
    /// </summary>
    /// <param name="quality">Mesh quality for the silhouette footprints (a footprint is
    /// measured from the tessellation, so extremes read a chord's sagitta small at
    /// coarse quality — the <see cref="Shape.Bounds"/> caveat).</param>
    public static PackLayout Pack(
        IReadOnlyList<Shape> parts, double plateWidth, double plateDepth,
        double gap = 2, MeshQuality? quality = null) =>
        Pack(parts, plateWidth, plateDepth, new PackOptions { Gap = gap, Quality = quality });

    /// <summary>
    /// Packs <paramref name="parts"/> onto the plate under <paramref name="options"/>. With
    /// the default options this is the shelf packer over footprint boxes and its placements
    /// are bit-identical to the simpler overload's.
    /// </summary>
    /// <exception cref="NotSupportedException"><see cref="PackRotation.Free"/> was asked
    /// for. Free rotation makes the orientation a CONTINUOUS variable, so there is no finite
    /// candidate set to search exhaustively and no bottom-left-first order to break ties
    /// with: it needs a no-fit polygon per part pair per angle, or an optimiser with a stated
    /// stopping rule to stay deterministic. Neither is here yet, and quietly sampling a few
    /// angles would be a search that is not the one it claims to be.</exception>
    public static PackLayout Pack(
        IReadOnlyList<Shape> parts, double plateWidth, double plateDepth, PackOptions options)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(options);
        if (parts.Count == 0)
            throw new ArgumentException("Nothing to pack.", nameof(parts));
        if (!(plateWidth > 0) || !(plateDepth > 0))
            throw new ArgumentOutOfRangeException(nameof(plateWidth), "The plate needs a positive size.");
        double gap = options.Gap;
        if (!(gap >= 0))
            throw new ArgumentOutOfRangeException(nameof(options), "The gap cannot be negative.");
        if (options.Rotation == PackRotation.Free)
            throw new NotSupportedException(
                "Free rotation is not supported: a continuous orientation has no finite " +
                "candidate set, so the packer could neither search it exhaustively nor break " +
                "ties deterministically. Use PackRotation.Quarter (four exact poses), or " +
                "pre-rotate the parts yourself.");

        // Footprint = the silhouette: the true shadow the part casts on the plate, so a part
        // wider up top than at its base still gets the room it needs.
        var outlines = new IReadOnlyList<Region2d>[parts.Count];
        var footprints = new Aabb[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            var regions = parts[i].Silhouette(SketchPlane.XY, options.Quality);
            var bounds = Aabb.Empty;
            foreach (var region in regions)
                bounds = bounds.Union(region.Bounds);
            if (!(bounds.Size.X > 0) || !(bounds.Size.Y > 0))
                throw new InvalidOperationException($"Part {i} casts no footprint to pack.");
            outlines[i] = regions;
            footprints[i] = bounds;
        }

        var placements = options.Nesting == PackNesting.Outline
            ? OutlinePack(outlines, footprints, plateWidth, plateDepth, gap, options)
            : BoxPack(footprints, plateWidth, plateDepth, gap, options.Rotation);

        return new PackLayout(
            placements, outlines, plateWidth, plateDepth, gap, options.Rotation, options.Nesting);
    }

    /// <summary>Packs and applies in one call: the parts posed onto the plate.</summary>
    public static IReadOnlyList<Shape> Arrange(
        IReadOnlyList<Shape> parts, double plateWidth, double plateDepth,
        double gap = 2, MeshQuality? quality = null) =>
        Pack(parts, plateWidth, plateDepth, gap, quality).Apply(parts);

    /// <summary>Packs and applies in one call, under <paramref name="options"/>.</summary>
    public static IReadOnlyList<Shape> Arrange(
        IReadOnlyList<Shape> parts, double plateWidth, double plateDepth, PackOptions options) =>
        Pack(parts, plateWidth, plateDepth, options).Apply(parts);

    // ---- quarter turns, exactly (a sign swap, never a cos) ----

    internal static readonly int[] QuarterTurns = [0, 90, 180, 270];

    /// <summary>The exact z rotation for a quarter turn, built from literal 0 and ±1 rather
    /// than <c>Math.Cos</c>, which returns 6.1e-17 for a quarter turn — the same rule the
    /// glTF writer's Y-up root node follows. Here it is what keeps a turned part's measured
    /// bounds equal to the turned outline the packer placed.</summary>
    internal static Matrix4d QuarterTurn(int degrees) => degrees switch
    {
        0 => Matrix4d.Identity,
        90 => new Matrix4d(0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1),
        180 => new Matrix4d(-1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1),
        270 => new Matrix4d(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "Not a quarter turn."),
    };

    internal static Vector2d Turn(in Vector2d p, int degrees) => degrees switch
    {
        0 => p,
        90 => new Vector2d(-p.Y, p.X),
        180 => new Vector2d(-p.X, -p.Y),
        270 => new Vector2d(p.Y, -p.X),
        _ => throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "Not a quarter turn."),
    };

    private static Aabb Turn(in Aabb box, int degrees)
    {
        var a = Turn(new Vector2d(box.Min.X, box.Min.Y), degrees);
        var b = Turn(new Vector2d(box.Max.X, box.Max.Y), degrees);
        return new Aabb(
            new Vector3d(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), 0),
            new Vector3d(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), 0));
    }

    internal static Vector2d[] PoseLoop(IReadOnlyList<Vector2d> loop, int degrees, in Vector2d offset)
    {
        var posed = new Vector2d[loop.Count];
        for (int i = 0; i < loop.Count; i++)
        {
            var p = Turn(loop[i], degrees);
            posed[i] = new Vector2d(p.X + offset.X, p.Y + offset.Y);
        }
        return posed;
    }

    // ---- shelf packing over footprint boxes ----

    private static PackPlacement[] BoxPack(
        Aabb[] footprints, double plateWidth, double plateDepth, double gap, PackRotation rotation)
    {
        if (rotation == PackRotation.None)
        {
            var only = TryShelfPack(
                footprints, new int[footprints.Length], plateWidth, plateDepth, gap, out string? why);
            return only ?? throw new InvalidOperationException(why!);
        }

        // A quarter turn only TRANSPOSES a footprint box, so the four poses collapse to two.
        // Which of the two is better is NOT a per-part question — a row is as deep as its
        // deepest member — so the packer runs both global preferences and keeps the shallower
        // plate. Two packs, both cheap; the tie-break is stated: less depth used, then less
        // width, then the landscape preference.
        PackPlacement[]? best = null;
        double bestDepth = 0, bestWidth = 0;
        string? firstFailure = null;
        foreach (bool landscape in (bool[])[true, false])
        {
            var turned = new Aabb[footprints.Length];
            var degrees = new int[footprints.Length];
            for (int i = 0; i < footprints.Length; i++)
            {
                var size = footprints[i].Size;
                bool wide = size.X >= size.Y;
                bool turn = landscape ? !wide : wide;
                // The preference yields where it would not fit the plate at all.
                if (turn && size.Y > plateWidth - 2 * gap && size.X <= plateWidth - 2 * gap)
                    turn = false;
                else if (!turn && size.X > plateWidth - 2 * gap && size.Y <= plateWidth - 2 * gap)
                    turn = true;
                degrees[i] = turn ? 90 : 0;
                turned[i] = turn ? Turn(footprints[i], 90) : footprints[i];
            }

            var candidate = TryShelfPack(turned, degrees, plateWidth, plateDepth, gap, out string? failure);
            firstFailure ??= failure;
            if (candidate is null)
                continue;

            double depth = 0, width = 0;
            foreach (var placement in candidate)
            {
                width = Math.Max(width, placement.Footprint.Max.X + placement.Offset.X);
                depth = Math.Max(depth, placement.Footprint.Max.Y + placement.Offset.Y);
            }
            if (best is null || depth < bestDepth || (depth == bestDepth && width < bestWidth))
            {
                best = candidate;
                bestDepth = depth;
                bestWidth = width;
            }
        }
        return best ?? throw new InvalidOperationException(firstFailure!);
    }

    private static PackPlacement[]? TryShelfPack(
        Aabb[] footprints, int[] degrees, double plateWidth, double plateDepth, double gap,
        out string? failure)
    {
        // Deterministic order: deepest first (shelf packing wastes least when rows are
        // filled tallest-first), ties by width then input index.
        var order = Enumerable.Range(0, footprints.Length)
            .OrderByDescending(i => footprints[i].Size.Y)
            .ThenByDescending(i => footprints[i].Size.X)
            .ThenBy(i => i)
            .ToList();

        var placements = new PackPlacement[footprints.Length];
        double cursorX = gap, cursorY = gap, shelfDepth = 0;
        foreach (int index in order)
        {
            double w = footprints[index].Size.X;
            double d = footprints[index].Size.Y;
            if (w > plateWidth - 2 * gap)
            {
                failure =
                    $"Part {index} is too wide for the plate: footprint {w:g4} × {d:g4} " +
                    $"against a {plateWidth:g4} × {plateDepth:g4} plate with gap {gap:g4}.";
                return null;
            }
            if (cursorX + w > plateWidth - gap)
            {
                // Next shelf.
                cursorX = gap;
                cursorY += shelfDepth + gap;
                shelfDepth = 0;
            }
            if (cursorY + d > plateDepth - gap)
            {
                failure =
                    $"The parts do not fit: part {index} (footprint {w:g4} × {d:g4}) needs a row " +
                    $"at y = {cursorY:g4} but the {plateWidth:g4} × {plateDepth:g4} plate ends at " +
                    $"{plateDepth - gap:g4} (gap {gap:g4}). Use a larger plate or a second one.";
                return null;
            }

            placements[index] = new PackPlacement(
                index,
                new Vector2d(cursorX - footprints[index].Min.X, cursorY - footprints[index].Min.Y),
                footprints[index],
                degrees[index]);
            cursorX += w + gap;
            shelfDepth = Math.Max(shelfDepth, d);
        }

        failure = null;
        return placements;
    }

    // ---- outline nesting ----

    /// <summary>Largest raster the outline packer will build in either direction — a guard on
    /// the caller's resolution, not a quality choice.</summary>
    private const int MaxCells = 4096;

    private static PackPlacement[] OutlinePack(
        IReadOnlyList<Region2d>[] outlines, Aabb[] footprints,
        double plateWidth, double plateDepth, double gap, PackOptions options)
    {
        double cell = options.Resolution ?? Math.Min(plateWidth, plateDepth) / 256;
        if (!(cell > 0))
            throw new ArgumentOutOfRangeException(nameof(options), "The raster resolution must be positive.");
        // The raster's own origin is the plate's legal corner (the plate shrunk by half the
        // gap), so cell (0, 0) IS the tightest legal placement and the margin costs no
        // quantization of its own.
        // Two spare columns and rows: a stamp's own width rounds up to a whole cell and then
        // carries one more for its far edge, so a placement the exact bounds test allows can
        // reach one cell past the plate's last cell. Without the slack `Overlaps` would refuse
        // it for running off the mask — conservative, and needlessly so at the far edge.
        double half = gap / 2;
        int plateCellsX = (int)Math.Ceiling((plateWidth - gap) / cell) + 2;
        int plateCellsY = (int)Math.Ceiling((plateDepth - gap) / cell) + 2;
        if (plateCellsX > MaxCells || plateCellsY > MaxCells)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Resolution {cell:g4} would raster the {plateWidth:g4} × {plateDepth:g4} plate at " +
                $"{plateCellsX} × {plateCellsY} cells, past the {MaxCells} limit. Use a coarser resolution.");

        int[] poses = options.Rotation == PackRotation.Quarter ? QuarterTurns : [0];

        // The outline grown by HALF the gap: two grown outlines being disjoint IS "these
        // parts are at least `gap` apart", symmetric and needing no distance predicate.
        // Dilation by a disk COMMUTES with a rotation, so the grow runs once per part and the
        // four poses are exact turns of its result — the offset is the expensive step
        // (measured 9-200 ms on real silhouettes) while a turn is a sign swap.
        var grown = new IReadOnlyList<Region2d>[outlines.Length];
        for (int i = 0; i < outlines.Length; i++)
        {
            grown[i] = gap > 0 ? Region2dOffset.Offset(outlines[i], gap / 2) : outlines[i];
            if (grown[i].Count == 0)
                throw new InvalidOperationException($"Part {i} casts no footprint to pack.");
        }

        // One mask per (part, pose), plus the grown bounds its cell (0, 0) starts at.
        var masks = new CellMask[outlines.Length][];
        var grownBounds = new Aabb[outlines.Length][];
        for (int i = 0; i < outlines.Length; i++)
        {
            masks[i] = new CellMask[poses.Length];
            grownBounds[i] = new Aabb[poses.Length];
            for (int p = 0; p < poses.Length; p++)
            {
                var loops = new List<Vector2d[]>();
                var bounds = Aabb.Empty;
                foreach (var region in grown[i])
                {
                    foreach (var loop in region.AllLoops())
                    {
                        var turned = PoseLoop(loop, poses[p], default);
                        loops.Add(turned);
                        foreach (var q in turned)
                            bounds = bounds.Union(new Vector3d(q.X, q.Y, 0));
                    }
                }
                grownBounds[i][p] = bounds;
                masks[i][p] = CellMask.Rasterize(loops, new Vector2d(bounds.Min.X, bounds.Min.Y), cell);
            }
        }

        // Deterministic order: biggest outline area first (a nester places the awkward parts
        // while the plate is empty), ties by footprint box area then input index. Every key
        // is orientation-independent, so the order cannot depend on a pose the search has not
        // chosen yet.
        var areas = outlines.Select(regions => regions.Sum(region => region.Area)).ToArray();
        var order = Enumerable.Range(0, outlines.Length)
            .OrderByDescending(i => areas[i])
            .ThenByDescending(i => footprints[i].Size.X * footprints[i].Size.Y)
            .ThenBy(i => i)
            .ToList();

        var plate = new CellMask(plateCellsX, plateCellsY);
        var placements = new PackPlacement[outlines.Length];
        foreach (int index in order)
        {
            bool placed = false;
            int bestPose = 0, bestX = 0, bestY = 0;
            for (int p = 0; p < poses.Length; p++)
            {
                var box = grownBounds[index][p];
                // Exact plate containment: the grown outline (raw plus half the gap) must sit
                // inside the plate shrunk by half the gap, which IS the raw outline a full gap
                // from the edge. Measured on the bounds rather than on the raster.
                double roomX = plateWidth - gap - box.Size.X;
                double roomY = plateDepth - gap - box.Size.Y;
                if (roomX < 0 || roomY < 0)
                    continue;
                int maxX = (int)Math.Floor(roomX / cell);
                int maxY = (int)Math.Floor(roomY / cell);

                var mask = masks[index][p];
                // Bottom-left-first: the first free cell in y-then-x order wins for this pose,
                // and across poses the lowest (then leftmost, then smallest angle) wins.
                for (int y = 0; y <= maxY && (!placed || y <= bestY); y++)
                {
                    int limit = placed && y == bestY ? Math.Min(maxX, bestX - 1) : maxX;
                    for (int x = 0; x <= limit; x++)
                    {
                        if (plate.Overlaps(mask, x, y))
                            continue;
                        placed = true;
                        bestPose = p;
                        bestX = x;
                        bestY = y;
                        break;
                    }
                }
            }

            if (!placed)
            {
                var box = grownBounds[index][0];
                throw new InvalidOperationException(
                    $"The parts do not fit: part {index} (outline {footprints[index].Size.X:g4} × " +
                    $"{footprints[index].Size.Y:g4}, grown to {box.Size.X:g4} × {box.Size.Y:g4}) found " +
                    $"no free place on the {plateWidth:g4} × {plateDepth:g4} plate (gap {gap:g4}, " +
                    $"raster {cell:g4}). Use a larger plate, a second one, or a finer resolution.");
            }

            plate.Add(masks[index][bestPose], bestX, bestY);
            var offset = new Vector2d(
                half + bestX * cell - grownBounds[index][bestPose].Min.X,
                half + bestY * cell - grownBounds[index][bestPose].Min.Y);
            placements[index] = new PackPlacement(
                index, offset, Turn(footprints[index], poses[bestPose]), poses[bestPose]);
        }
        return placements;
    }

    /// <summary>
    /// A bitmap over square cells — the outline packer's occupancy map and each part's stamp.
    /// Rasterization is CONSERVATIVE: every cell the region touches is set, so an empty AND
    /// between two masks proves the regions are disjoint at any cell size, and a coarse
    /// raster costs only utilisation.
    /// </summary>
    private sealed class CellMask
    {
        private readonly ulong[] _bits;

        internal CellMask(int width, int height)
        {
            Width = width;
            Height = height;
            // One spare word per row absorbs a shifted stamp's overhang.
            Words = (width + 63) / 64 + 1;
            _bits = new ulong[Words * height];
        }

        internal int Width { get; }
        internal int Height { get; }
        internal int Words { get; }

        internal static CellMask Rasterize(IReadOnlyList<Vector2d[]> loops, in Vector2d origin, double cell)
        {
            double spanX = 0, spanY = 0;
            foreach (var loop in loops)
            {
                foreach (var p in loop)
                {
                    spanX = Math.Max(spanX, p.X - origin.X);
                    spanY = Math.Max(spanY, p.Y - origin.Y);
                }
            }
            var mask = new CellMask(
                (int)Math.Ceiling(spanX / cell) + 1, (int)Math.Ceiling(spanY / cell) + 1);

            // (a) Every cell the boundary passes through. Samples are no more than half a
            // cell apart along each segment, so every boundary point p is within a QUARTER
            // cell of some sample s; p then lies in [s - h/2, s + h/2] on each axis, an
            // interval of width h that spans at most TWO cells, so marking that 2x2 block per
            // sample is sound. (The obvious 3x3 block is sound too and dilates the mask by a
            // whole cell on every side — measured, that costs about one cell of clearance per
            // part, which is what a tight fit runs out of.)
            foreach (var loop in loops)
            {
                for (int i = 0; i < loop.Length; i++)
                {
                    var a = loop[i];
                    var b = loop[(i + 1) % loop.Length];
                    int steps = Math.Max(1, (int)Math.Ceiling((b - a).Length / (cell / 2)));
                    for (int s = 0; s <= steps; s++)
                    {
                        double t = (double)s / steps;
                        double x = a.X + (b.X - a.X) * t - origin.X;
                        double y = a.Y + (b.Y - a.Y) * t - origin.Y;
                        int x0 = (int)Math.Floor((x - cell / 2) / cell);
                        int x1 = (int)Math.Floor((x + cell / 2) / cell);
                        int y0 = (int)Math.Floor((y - cell / 2) / cell);
                        int y1 = (int)Math.Floor((y + cell / 2) / cell);
                        for (int cy = y0; cy <= y1; cy++)
                            for (int cx = x0; cx <= x1; cx++)
                                mask.Set(cx, cy);
                    }
                }
            }

            // (b) Every cell whose CENTRE is inside, by even-odd parity across all loops (so
            // holes come out empty and another part may nest in a through hole). A cell that
            // meets the region with its centre outside must have the boundary crossing it,
            // which (a) has already marked.
            var crossings = new List<double>();
            for (int row = 0; row < mask.Height; row++)
            {
                double y = origin.Y + (row + 0.5) * cell;
                crossings.Clear();
                foreach (var loop in loops)
                {
                    for (int i = 0; i < loop.Length; i++)
                    {
                        var a = loop[i];
                        var b = loop[(i + 1) % loop.Length];
                        if (a.Y <= y == b.Y <= y)
                            continue;
                        crossings.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                    }
                }
                if (crossings.Count < 2)
                    continue;
                crossings.Sort();
                for (int k = 0; k + 1 < crossings.Count; k += 2)
                {
                    int from = (int)Math.Ceiling((crossings[k] - origin.X) / cell - 0.5);
                    int to = (int)Math.Floor((crossings[k + 1] - origin.X) / cell - 0.5);
                    for (int column = Math.Max(0, from); column <= Math.Min(mask.Width - 1, to); column++)
                        mask.Set(column, row);
                }
            }
            return mask;
        }

        private void Set(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                return;
            _bits[y * Words + (x >> 6)] |= 1UL << (x & 63);
        }

        /// <summary>Does <paramref name="stamp"/>, placed with its cell (0, 0) on this mask's
        /// cell (<paramref name="atX"/>, <paramref name="atY"/>), share a set cell with this
        /// one? A stamp that would run off the mask counts as overlapping.</summary>
        internal bool Overlaps(CellMask stamp, int atX, int atY)
        {
            if (atX < 0 || atY < 0 || atX + stamp.Width > Width || atY + stamp.Height > Height)
                return true;
            int shift = atX & 63;
            int word = atX >> 6;
            for (int row = 0; row < stamp.Height; row++)
            {
                int here = (atY + row) * Words + word;
                int there = row * stamp.Words;
                for (int w = 0; w < stamp.Words; w++)
                {
                    ulong bits = stamp._bits[there + w];
                    if (bits == 0)
                        continue;
                    if ((_bits[here + w] & (bits << shift)) != 0)
                        return true;
                    if (shift != 0 && (_bits[here + w + 1] & (bits >> (64 - shift))) != 0)
                        return true;
                }
            }
            return false;
        }

        /// <summary>Stamps <paramref name="stamp"/> into this mask at
        /// (<paramref name="atX"/>, <paramref name="atY"/>).</summary>
        internal void Add(CellMask stamp, int atX, int atY)
        {
            int shift = atX & 63;
            int word = atX >> 6;
            for (int row = 0; row < stamp.Height; row++)
            {
                int here = (atY + row) * Words + word;
                int there = row * stamp.Words;
                for (int w = 0; w < stamp.Words; w++)
                {
                    ulong bits = stamp._bits[there + w];
                    if (bits == 0)
                        continue;
                    _bits[here + w] |= bits << shift;
                    if (shift != 0)
                        _bits[here + w + 1] |= bits >> (64 - shift);
                }
            }
        }
    }
}
