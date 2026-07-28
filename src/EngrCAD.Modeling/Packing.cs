using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>Where one part landed on the plate: <see cref="Offset"/> is the XY
/// translation to apply to the part (z untouched), <see cref="Footprint"/> its measured
/// silhouette bounds BEFORE the offset (degenerate in z, the silhouette convention).</summary>
public readonly record struct PackPlacement(int Index, Vector2d Offset, Aabb Footprint);

/// <summary>A computed build-plate layout — the placements plus the plate they were
/// packed onto. Placements are in INPUT order (each carries its index), so callers zip
/// them with their part lists directly.</summary>
public sealed class PackLayout
{
    internal PackLayout(IReadOnlyList<PackPlacement> placements, double width, double depth, double gap)
    {
        Placements = placements;
        PlateWidth = width;
        PlateDepth = depth;
        Gap = gap;
    }

    public IReadOnlyList<PackPlacement> Placements { get; }
    public double PlateWidth { get; }
    public double PlateDepth { get; }
    public double Gap { get; }

    /// <summary>The packed parts as translated shapes (input order) — feed them to a
    /// <c>Scene</c> or <c>StlWriter</c> as one plate.</summary>
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
            var offset = Placements[i].Offset;
            placed[i] = parts[i].Translate(offset.X, offset.Y, 0);
        }
        return placed;
    }
}

/// <summary>
/// 2D bin packing of part footprints onto a build plate — build123d's <c>pack</c>, for
/// laying out a multi-part print before STL export. Footprints are the parts'
/// <see cref="Shape.Silhouette"/> bounds (so an overhang wider than the base counts),
/// and the algorithm is a deterministic <b>shelf packer</b>: parts sorted by footprint
/// depth (then width, then index — no randomness, so the same parts always give the
/// same plate), placed left-to-right into rows from the plate's front-left corner,
/// each row as tall as its tallest member. Shelf packing is simple and predictable
/// rather than optimal; parts keep their orientation (no rotation, no nesting into
/// concavities) — v1 scope, stated rather than implied.
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
        double gap = 2, MeshQuality? quality = null)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
            throw new ArgumentException("Nothing to pack.", nameof(parts));
        if (!(plateWidth > 0) || !(plateDepth > 0))
            throw new ArgumentOutOfRangeException(nameof(plateWidth), "The plate needs a positive size.");
        if (!(gap >= 0))
            throw new ArgumentOutOfRangeException(nameof(gap));

        // Footprint = the silhouette's bounds: the true shadow the part casts on the
        // plate, so a part wider up top than at its base still gets the room it needs.
        var footprints = new Aabb[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            var regions = parts[i].Silhouette(SketchPlane.XY, quality);
            var bounds = Aabb.Empty;
            foreach (var region in regions)
                bounds = bounds.Union(region.Bounds);
            if (!(bounds.Size.X > 0) || !(bounds.Size.Y > 0))
                throw new InvalidOperationException($"Part {i} casts no footprint to pack.");
            footprints[i] = bounds;
        }

        // Deterministic order: deepest first (shelf packing wastes least when rows are
        // filled tallest-first), ties by width then input index.
        var order = Enumerable.Range(0, parts.Count)
            .OrderByDescending(i => footprints[i].Size.Y)
            .ThenByDescending(i => footprints[i].Size.X)
            .ThenBy(i => i)
            .ToList();

        var placements = new PackPlacement[parts.Count];
        double cursorX = gap, cursorY = gap, shelfDepth = 0;
        foreach (int index in order)
        {
            double w = footprints[index].Size.X;
            double d = footprints[index].Size.Y;
            if (w > plateWidth - 2 * gap)
                throw new InvalidOperationException(
                    $"Part {index} is too wide for the plate: footprint {w:g4} × {d:g4} " +
                    $"against a {plateWidth:g4} × {plateDepth:g4} plate with gap {gap:g4}.");
            if (cursorX + w > plateWidth - gap)
            {
                // Next shelf.
                cursorX = gap;
                cursorY += shelfDepth + gap;
                shelfDepth = 0;
            }
            if (cursorY + d > plateDepth - gap)
                throw new InvalidOperationException(
                    $"The parts do not fit: part {index} (footprint {w:g4} × {d:g4}) needs a row " +
                    $"at y = {cursorY:g4} but the {plateWidth:g4} × {plateDepth:g4} plate ends at " +
                    $"{plateDepth - gap:g4} (gap {gap:g4}). Use a larger plate or a second one.");

            placements[index] = new PackPlacement(
                index,
                new Vector2d(cursorX - footprints[index].Min.X, cursorY - footprints[index].Min.Y),
                footprints[index]);
            cursorX += w + gap;
            shelfDepth = Math.Max(shelfDepth, d);
        }

        return new PackLayout(placements, plateWidth, plateDepth, gap);
    }

    /// <summary>Packs and applies in one call: the parts translated onto the plate.</summary>
    public static IReadOnlyList<Shape> Arrange(
        IReadOnlyList<Shape> parts, double plateWidth, double plateDepth,
        double gap = 2, MeshQuality? quality = null) =>
        Pack(parts, plateWidth, plateDepth, gap, quality).Apply(parts);
}
