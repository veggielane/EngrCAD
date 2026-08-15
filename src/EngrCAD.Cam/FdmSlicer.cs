using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using EngrCAD.Modeling;

namespace EngrCAD.Cam;

/// <summary>What a toolpath is doing — a perimeter wall or an infill run.</summary>
public enum SlicePathRole
{
    /// <summary>A closed perimeter loop (a wall shell, or a hole's wall).</summary>
    Wall,

    /// <summary>An open infill run.</summary>
    Infill,

    /// <summary>A first-layer adhesion brim loop (attached to the part's outline).</summary>
    Brim,

    /// <summary>A first-layer skirt loop (a purge line standing clear of the part).</summary>
    Skirt,
}

/// <summary>One deposition path on a layer: a 2D polyline in bed coordinates (world x/y),
/// closed for a wall loop (the closing segment is implied, never repeated as a point),
/// open for an infill run.</summary>
public sealed record SlicePath(
    SlicePathRole Role, IReadOnlyList<Vector2d> Points, bool IsClosed, int WallIndex = 0)
{
    /// <summary>The deposition length (mm), the closing segment of a closed loop included.</summary>
    public double Length
    {
        get
        {
            double length = 0;
            for (int i = 1; i < Points.Count; i++)
                length += (Points[i] - Points[i - 1]).Length;
            if (IsClosed && Points.Count > 2)
                length += (Points[0] - Points[^1]).Length;
            return length;
        }
    }

    /// <summary>The point the path starts at.</summary>
    public Vector2d Start => Points[0];

    /// <summary>The point the path ends at (the start again for a closed loop).</summary>
    public Vector2d End => IsClosed ? Points[0] : Points[^1];
}

/// <summary>One slice layer: its index, the height its deposition ENDS at (the layer covers
/// <c>[Z − h, Z]</c>), the plane it was sectioned at (the layer's own mid-height), the exact
/// section regions, and the deposition paths in print order.</summary>
public sealed record SliceLayer(
    int Index, double Z, double SectionZ,
    IReadOnlyList<Region2d> Regions, IReadOnlyList<SlicePath> Paths)
{
    /// <summary>The layer's total deposition length (mm).</summary>
    public double DepositionLength => Paths.Sum(p => p.Length);
}

/// <summary>A sliced part: the profile it was sliced with, the layers bottom-up, and the PRINT
/// DIRECTION that was chosen — the part axis that points up on the bed. Layers and G-code are
/// always in BED coordinates (the part rotated so that direction is +Z); the direction is
/// recorded so a consumer can pose the result back into the part's own frame.</summary>
public sealed record SlicedPart(
    PrinterProfile Profile, IReadOnlyList<SliceLayer> Layers, Vector3d PrintDirection)
{
    /// <summary>Total deposition length over every layer (mm).</summary>
    public double DepositionLength => Layers.Sum(l => l.DepositionLength);

    /// <summary>The deposited material volume (mm³): deposition length × the bead's stadium
    /// cross-section — the number the G-code's E values are the filament-side spelling of.</summary>
    public double ExtrudedVolume => DepositionLength * Profile.BeadArea;

    /// <summary>The filament length consumed (mm): the deposited volume through the filament's
    /// own cross-section.</summary>
    public double FilamentUsed => ExtrudedVolume / Profile.FilamentArea;
}

/// <summary>
/// The FDM slicer — CAM stage 1's core, and deliberately a THIN layer over machinery that already
/// existed: each layer is an exact <see cref="Shape.Section"/> at the layer's mid-height (the
/// standard slicer convention, so a plane never lands flush on the part's own top or bottom
/// face), perimeter shells are inward <see cref="Region2dOffset"/>s (wall k's centreline at
/// <c>bead·(k + ½)</c> — successive inward offsets ARE the walls), infill is a rectilinear scan
/// clipped by an exact even-odd crossing rule (half-open at vertices, the <c>SheetHatch</c>
/// lesson) alternating ±45° per layer, and travel ordering is <see cref="RunLinker"/> — the same
/// deterministic greedy tour every fill consumer in the repo already uses.
///
/// <para><b>Determinism is the regression mechanism</b>: the slice is a pure function of
/// (shape, profile) — the infill scan is anchored to the global grid (phase is a function of
/// the stated spacing, never of the part's position rounding), a wall loop's seam is the offset
/// output's own first vertex (a stated convention, not rounding luck), and two slices of one
/// shape are byte-identical through the G-code writer.</para>
///
/// <para><b>Refused by name</b>: an unusable profile (see <see cref="PrinterProfile.Validate"/>)
/// and a shape whose bounds carry no height. A section plane that lands flush on an internal
/// horizontal face is retried once at a deterministic nudge (+5% of a layer) before the refusal
/// propagates. A layer whose section is EMPTY is kept as an empty layer (a part with a gap in z
/// is legal), never invented around.</para>
/// </summary>
public static class FdmSlicer
{
    private const double NudgeFraction = 0.05;

    /// <summary>
    /// Slices a shape with the given profile (null = <see cref="PrinterProfile.Default"/>).
    /// <paramref name="printDirection"/> selects the BUILD ORIENTATION — the part axis that
    /// should point up on the bed (null = the part's own +Z): the shape is rotated by the
    /// MINIMAL rotation taking that direction to +Z and sliced in bed coordinates, so choosing
    /// a direction never re-models anything, it re-orients it. A direction already equal to +Z
    /// takes the identity fast path (bit-identical to passing null); the antiparallel case
    /// (−Z) turns π about the codebase's one arbitrary-perpendicular convention
    /// (<see cref="Vector3d.ArbitraryPerpendicular"/>), so it is deterministic rather than a
    /// rounding accident; a zero direction is refused by name.
    /// </summary>
    public static SlicedPart Slice(
        Shape shape, PrinterProfile? profile = null, Vector3d? printDirection = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var p = profile ?? PrinterProfile.Default;
        p.Validate();

        var direction = printDirection ?? Vector3d.UnitZ;
        if (!(direction.Length > 0) || !double.IsFinite(direction.Length))
            throw new ArgumentException(
                "The print direction must be a nonzero finite vector.", nameof(printDirection));
        var up = direction.Normalized();
        shape = OrientForPrinting(shape, up);

        var bounds = shape.Bounds();
        double height = bounds.Max.Z - bounds.Min.Z;
        if (!(height > 0))
            throw new ArgumentException(
                $"The shape has no height to slice (z extent {height:0.###}).");

        double h = p.LayerHeight;
        int layerCount = Math.Max(1, (int)Math.Ceiling(height / h - 1e-9));
        double bead = p.ResolvedBeadWidth;

        // Lower ONCE, section N times (the `Part.TryGetSolid` lesson — a hundred layers must
        // not mean a hundred B-Rep lowerings of the same shape).
        BrepSolid? solid = shape.CanConvertTo(TargetRep.Brep) ? shape.ToBrep() : null;
        HalfEdgeMesh? mesh = solid is null ? shape.ToMesh() : null;

        var layers = new List<SliceLayer>(layerCount);
        var pen = new Vector2d(bounds.Min.X, bounds.Min.Y);
        for (int i = 0; i < layerCount; i++)
        {
            // Mid-layer sectioning; the top layer's plane is clamped below the part's own top
            // face so an exactly-divisible height cannot section flush with it.
            double sectionZ = Math.Min(
                bounds.Min.Z + (i + 0.5) * h,
                bounds.Max.Z - NudgeFraction * h);
            var regions = SectionWithNudge(solid, mesh, sectionZ, h);

            var paths = new List<SlicePath>();

            // First-layer adhesion, outermost-first so the nozzle primes on the skirt, lays the
            // brim inward, and finishes AT the part's own outline. Both are outward offsets of
            // the whole layer-0 region SET (islands' brims merge where they meet), and both are
            // write-only-when-stated: 0 loops / 0 width leaves every existing slice byte-identical.
            if (i == 0)
            {
                for (int k = p.SkirtLoops - 1; k >= 0; k--)
                    foreach (var ring in Region2dOffset.Offset(
                        regions, p.SkirtGap + bead * (k + 0.5)))
                        paths.Add(new SlicePath(SlicePathRole.Skirt, ring.Outer, IsClosed: true));
                int brimLoops = (int)Math.Ceiling(p.BrimWidth / bead - 1e-9);
                for (int k = brimLoops - 1; k >= 0; k--)
                    foreach (var ring in Region2dOffset.Offset(regions, bead * (k + 0.5)))
                    {
                        paths.Add(new SlicePath(SlicePathRole.Brim, ring.Outer, IsClosed: true));
                        // A brim rings the INSIDE of a bore too (the outward offset's hole
                        // loops are the bore shrunk inward — exactly the interior brim).
                        foreach (var hole in ring.Holes)
                            paths.Add(new SlicePath(SlicePathRole.Brim, hole, IsClosed: true));
                    }
                if (paths.Count > 0)
                    pen = paths[^1].End;
            }

            var walls = new List<SlicePath>();
            var infill = new List<SlicePath>();
            foreach (var region in regions)
            {
                // Walls, innermost first (the outer wall prints last, onto settled neighbours).
                for (int k = p.WallCount - 1; k >= 0; k--)
                {
                    double inset = bead * (k + 0.5);
                    foreach (var shell in Region2dOffset.Offset(region, -inset))
                    {
                        walls.Add(new SlicePath(SlicePathRole.Wall, shell.Outer, IsClosed: true, k));
                        foreach (var hole in shell.Holes)
                            walls.Add(new SlicePath(SlicePathRole.Wall, hole, IsClosed: true, k));
                    }
                }

                // Infill: the region inside the innermost wall's inner face, less half a bead so
                // the infill bead just meets the wall bead.
                if (p.InfillDensity > 0)
                {
                    double infillInset = bead * (p.WallCount + 0.5);
                    double spacing = bead / p.InfillDensity;
                    double angle = i % 2 == 0 ? Math.PI / 4 : 3 * Math.PI / 4;
                    foreach (var core in Region2dOffset.Offset(region, -infillInset))
                        infill.AddRange(RectilinearInfill(core, spacing, angle));
                }
            }

            // Walls keep their EMISSION order — innermost-out per region is a print-quality
            // decision (the outer wall lands on settled neighbours), not a travel optimisation,
            // and concentric shells are near each other anyway. Only the infill is greedily
            // linked (RunLinker), the pen carried across layers.
            paths.AddRange(walls);
            if (walls.Count > 0)
                pen = walls[^1].End;
            paths.AddRange(LinkGroup(infill, ref pen));
            layers.Add(new SliceLayer(i, bounds.Min.Z + (i + 1) * h, sectionZ, regions, paths));
        }

        return new SlicedPart(p, layers, up);
    }

    /// <summary>Rotates the shape so <paramref name="up"/> (unit) becomes bed +Z, by the
    /// minimal rotation. +Z itself is the identity (no transform node at all, so the default
    /// slice is bit-identical to the pre-orientation code); −Z has no unique minimal axis, so
    /// it turns π about the one arbitrary-perpendicular convention.</summary>
    private static Shape OrientForPrinting(Shape shape, in Vector3d up)
    {
        double cos = up.Dot(Vector3d.UnitZ);
        if (cos > 1 - 1e-12)
            return shape;
        if (cos < -1 + 1e-12)
            return shape.Rotate(up.ArbitraryPerpendicular(Tolerance.Default), Math.PI);
        var axis = up.Cross(Vector3d.UnitZ).Normalized();
        return shape.Rotate(axis, Math.Acos(Math.Clamp(cos, -1, 1)));
    }

    /// <summary>The section at z (the same routes <c>Shape.Section</c> takes, over the ONE
    /// lowering), retried once at a deterministic +5%-of-a-layer nudge when the plane lands
    /// flush on an internal horizontal face (which sectioning refuses — an in-plane face makes
    /// the section an area, not a curve).</summary>
    private static IReadOnlyList<Region2d> SectionWithNudge(
        BrepSolid? solid, HalfEdgeMesh? mesh, double z, double h)
    {
        try
        {
            return At(z);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return At(z + NudgeFraction * h);
        }

        IReadOnlyList<Region2d> At(double height)
        {
            var plane = SketchPlane.At(new Vector3d(0, 0, height), Vector3d.UnitX, Vector3d.UnitY);
            return solid is not null
                ? PlanarSection.OfSolid(solid, plane.Frame)
                : PlanarSection.OfMesh(mesh!, plane.Frame);
        }
    }

    /// <summary>Deterministic greedy linking of one path group from the pen's position — the
    /// shared <see cref="RunLinker"/>, with a reversed run traversed backwards (a closed loop's
    /// reversal is the same loop walked the other way from the same seam).</summary>
    private static List<SlicePath> LinkGroup(List<SlicePath> group, ref Vector2d pen)
    {
        if (group.Count == 0)
            return group;
        var ends = new (Vector3d Start, Vector3d End)[group.Count];
        for (int i = 0; i < group.Count; i++)
            ends[i] = (To3d(group[i].Start), To3d(group[i].End));
        var linkage = RunLinker.Link(ends, To3d(pen));

        var ordered = new List<SlicePath>(group.Count);
        foreach (var run in linkage.Order)
        {
            var path = group[run.Index];
            ordered.Add(run.Reversed ? Reverse(path) : path);
        }
        if (ordered.Count > 0)
            pen = ordered[^1].End;
        return ordered;

        static Vector3d To3d(in Vector2d p) => new(p.X, p.Y, 0);
    }

    private static SlicePath Reverse(SlicePath path)
    {
        if (!path.IsClosed)
            return path with { Points = [.. path.Points.Reverse()] };
        // A closed loop reversed keeps its seam (point 0) and walks the other way.
        var points = new List<Vector2d>(path.Points.Count) { path.Points[0] };
        for (int i = path.Points.Count - 1; i >= 1; i--)
            points.Add(path.Points[i]);
        return path with { Points = points };
    }

    /// <summary>
    /// Rectilinear infill: parallel lines at <paramref name="angle"/>, one bead-spacing apart,
    /// clipped to the region by an EXACT even-odd crossing count. The region is rotated into the
    /// scan frame (so the lines are horizontal there), crossings use the half-open rule — an
    /// edge crosses the scan line iff exactly one endpoint is strictly above it — so a line
    /// through a vertex is counted by exactly one of the two incident edges (the
    /// <c>SheetHatch</c> vertex rule), and the scan positions are anchored to the GLOBAL grid
    /// (integer multiples of the spacing in the rotated frame), so the pattern's phase is a
    /// function of what was asked, never of where the part happens to sit.
    /// </summary>
    internal static List<SlicePath> RectilinearInfill(Region2d region, double spacing, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        var loops = new List<IReadOnlyList<Vector2d>>(1 + region.Holes.Count) { region.Outer };
        loops.AddRange(region.Holes);

        // Rotate into the scan frame and find the scan band.
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        var rotated = new List<Vector2d[]>(loops.Count);
        foreach (var loop in loops)
        {
            var r = new Vector2d[loop.Count];
            for (int i = 0; i < loop.Count; i++)
            {
                r[i] = new Vector2d(loop[i].X * c + loop[i].Y * s, -loop[i].X * s + loop[i].Y * c);
                minY = Math.Min(minY, r[i].Y);
                maxY = Math.Max(maxY, r[i].Y);
            }
            rotated.Add(r);
        }

        var paths = new List<SlicePath>();
        var crossings = new List<double>();
        int first = (int)Math.Ceiling(minY / spacing - 1e-12);
        int last = (int)Math.Floor(maxY / spacing + 1e-12);
        for (int k = first; k <= last; k++)
        {
            double y = k * spacing;
            crossings.Clear();
            foreach (var loop in rotated)
            {
                for (int i = 0; i < loop.Length; i++)
                {
                    var a = loop[i];
                    var b = loop[(i + 1) % loop.Length];
                    if (a.Y > y == b.Y > y)
                        continue;
                    crossings.Add(a.X + (y - a.Y) / (b.Y - a.Y) * (b.X - a.X));
                }
            }
            crossings.Sort();
            for (int i = 0; i + 1 < crossings.Count; i += 2)
            {
                double x0 = crossings[i], x1 = crossings[i + 1];
                if (x1 - x0 < 1e-9)
                    continue;
                paths.Add(new SlicePath(SlicePathRole.Infill,
                    [Back(x0, y), Back(x1, y)], IsClosed: false));
            }
        }
        return paths;

        Vector2d Back(double x, double y) => new(x * c - y * s, x * s + y * c);
    }
}
