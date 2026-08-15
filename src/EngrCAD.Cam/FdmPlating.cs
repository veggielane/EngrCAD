using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Cam;

/// <summary>
/// Multi-part build plates over the landed <see cref="Packing"/> machinery: arrange the
/// parts on the bed (the deterministic shelf packer — or outline nesting, via
/// <see cref="PackOptions"/>), drop each onto the bed plane, and return ONE shape the
/// slicer takes whole — disjoint parts section into disjoint islands, so walls, brims,
/// skins and supports all work per island with nothing new, and the gap is what keeps
/// neighbouring brims from merging.
/// </summary>
public static class FdmPlating
{
    /// <summary>Arranges <paramref name="parts"/> onto a <paramref name="bedWidth"/> ×
    /// <paramref name="bedDepth"/> plate with <paramref name="gap"/> clearance (leave room
    /// for brims), each part rotated/placed by the packer and rested on the bed plane
    /// (z = 0). Refuses loudly — naming the part — when the plate runs out of room (the
    /// packer's own refusal). Slice the returned shape as usual.</summary>
    public static Shape Plate(
        IReadOnlyList<Shape> parts, double bedWidth, double bedDepth, double gap = 5,
        PackOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
            throw new ArgumentException("A plate needs at least one part.", nameof(parts));

        var layout = options is null
            ? Packing.Pack(parts, bedWidth, bedDepth, gap)
            : Packing.Pack(parts, bedWidth, bedDepth, options);

        Shape? plate = null;
        foreach (var placement in layout.Placements)
        {
            var placed = parts[placement.Index];
            if (placement.RotationDegrees != 0)
                placed = placed.Rotate(
                    Vector3d.UnitZ, placement.RotationDegrees * Math.PI / 180);
            // Rest every part on the bed plane, whatever frame it was modeled in.
            placed = placed.Translate(
                placement.Offset.X, placement.Offset.Y, -placed.Bounds().Min.Z);
            plate = plate is null ? placed : plate | placed;
        }
        return plate!;
    }
}
