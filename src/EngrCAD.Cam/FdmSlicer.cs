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

    /// <summary>A support run — sparse breakaway material under an overhang.</summary>
    Support,

    /// <summary>A solid (100%) infill run — a top or bottom skin.</summary>
    SolidInfill,

    /// <summary>A bridge run — solid fill spanning air, laid along the span direction.</summary>
    Bridge,

    /// <summary>An ironing pass — a low-flow smoothing sweep over a finished top skin.</summary>
    Ironing,

    /// <summary>A raft run — the sacrificial base the part prints on.</summary>
    Raft,
}

/// <summary>One deposition path on a layer: a 2D polyline in bed coordinates (world x/y),
/// closed for a wall loop (the closing segment is implied, never repeated as a point),
/// open for an infill run.</summary>
public sealed record SlicePath(
    SlicePathRole Role, IReadOnlyList<Vector2d> Points, bool IsClosed, int WallIndex = 0,
    double Flow = 1)
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
    IReadOnlyList<Region2d> Regions, IReadOnlyList<SlicePath> Paths, double Height = 0)
{
    /// <summary>The layer's total deposition length (mm).</summary>
    public double DepositionLength => Paths.Sum(p => p.Length);

    /// <summary>The layer's own height (mm): its stated value, or the profile's
    /// <c>LayerHeight</c> when none was stated (a uniform slice states none, keeping the
    /// record byte-compatible).</summary>
    public double HeightOr(PrinterProfile profile) =>
        Height > 0 ? Height : profile.LayerHeight;
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

    /// <summary>The deposited material volume (mm³): flow-weighted deposition length × each
    /// LAYER's own bead cross-section (variable layer heights change the stadium per layer)
    /// — the number the G-code's E values are the filament-side spelling of.</summary>
    public double ExtrudedVolume =>
        Layers.Sum(l => l.Paths.Sum(p => p.Length * p.Flow)
            * Profile.BeadAreaFor(l.HeightOr(Profile)));

    /// <summary>Filament per path role (mm) — walls vs infill vs supports vs skins, the
    /// per-role split of <see cref="FilamentUsed"/> (they sum exactly).</summary>
    public IReadOnlyDictionary<SlicePathRole, double> FilamentByRole =>
        Layers.SelectMany(l => l.Paths.Select(path => (l, path)))
            .GroupBy(x => x.path.Role).ToDictionary(
                g => g.Key,
                g => g.Sum(x => x.path.Length * x.path.Flow
                    * Profile.BeadAreaFor(x.l.HeightOr(Profile))) / Profile.FilamentArea);

    /// <summary>The filament length consumed (mm): the deposited volume through the filament's
    /// own cross-section.</summary>
    public double FilamentUsed => ExtrudedVolume / Profile.FilamentArea;
}

/// <summary>Per-JOB support geometry: BLOCKER shapes mask support generation over their
/// own volume (sectioned per layer like the part), ENFORCER shapes force support under
/// any downward-facing facet inside them, threshold or no threshold — the code-first
/// equivalent of paint-on supports.</summary>
public sealed record FdmSupportModifiers(
    IReadOnlyList<Shape>? Blockers = null, IReadOnlyList<Shape>? Enforcers = null);

/// <summary>
/// The FDM slicer — CAM stage 1's core, and deliberately a THIN layer over machinery that already
/// existed: each layer is an exact <see cref="Shape.Section"/> at the layer's mid-height (the
/// standard slicer convention, so a plane never lands flush on the part's own top or bottom
/// face), perimeter shells are inward <see cref="Region2dOffset"/>s (wall k's centreline at
/// <c>bead·(k + ½)</c> — successive inward offsets ARE the walls), infill is a rectilinear scan
/// clipped by an exact even-odd crossing rule (half-open at vertices, the <c>SheetHatch</c>
/// lesson) alternating ±45° per layer, and travel ordering is <see cref="RunLinker"/> — the same
/// deterministic greedy tour every fill consumer in the repo already uses. SUPPORTS (opt-in via
/// <see cref="PrinterProfile.SupportOverhangAngle"/>) are columns under the shape's own overhang
/// facets — detected by the <c>Manufacturability</c> dot-product rule, projected and unioned,
/// clipped per layer to what is still above and kept an XY gap clear of the part's section.
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
        Shape shape, PrinterProfile? profile = null, Vector3d? printDirection = null,
        FdmSupportModifiers? supportModifiers = null,
        IReadOnlyList<double>? layerHeights = null)
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
        double bead = p.ResolvedBeadWidth;

        // The per-layer height table: uniform from the profile, or the stated VARIABLE
        // table (bottom-up), which must be printable per layer and COVER the part.
        double[] heights;
        if (layerHeights is null)
        {
            heights = new double[Math.Max(1, (int)Math.Ceiling(height / h - 1e-9))];
            Array.Fill(heights, h);
        }
        else
        {
            if (layerHeights.Count == 0)
                throw new ArgumentException(
                    "The layer-height table is empty.", nameof(layerHeights));
            double covered = 0;
            for (int i = 0; i < layerHeights.Count; i++)
            {
                double stated = layerHeights[i];
                if (!(stated > 0) || !double.IsFinite(stated))
                    throw new ArgumentException(
                        $"Layer height [{i}] must be finite and positive; got {stated:0.###}.",
                        nameof(layerHeights));
                if (stated > bead)
                    throw new ArgumentException(
                        $"Layer height [{i}] ({stated:0.###}) exceeds the bead width "
                        + $"({bead:0.###}) — the stadium cross-section degenerates.",
                        nameof(layerHeights));
                covered += stated;
            }
            if (covered < height - 1e-9)
                throw new ArgumentException(
                    $"The layer-height table covers {covered:0.###} of the part's "
                    + $"{height:0.###} — {height - covered:0.###} short. State enough "
                    + "layers to reach the top.", nameof(layerHeights));
            // Keep only the layers that carry material (the table may overshoot).
            int used = 0;
            double reach = 0;
            while (reach < height - 1e-9)
                reach += layerHeights[used++];
            heights = [.. layerHeights.Take(used)];
        }
        int layerCount = heights.Length;
        // Cumulative layer TOPS (bed frame): tops[i] is where layer i's deposition ends.
        var tops = new double[layerCount];
        double accumulate = bounds.Min.Z;
        for (int i = 0; i < layerCount; i++)
        {
            accumulate += heights[i];
            tops[i] = accumulate;
        }

        // Lower ONCE, section N times (the `Part.TryGetSolid` lesson — a hundred layers must
        // not mean a hundred B-Rep lowerings of the same shape).
        BrepSolid? solid = shape.CanConvertTo(TargetRep.Brep) ? shape.ToBrep() : null;
        HalfEdgeMesh? mesh = solid is null ? shape.ToMesh() : null;

        // Support plan: the ORIENTED shape's overhang facets (the Manufacturability rule —
        // the threshold compared on the dot product, never on a derived angle), kept as 3D
        // loops sorted by their highest point so the active set shrinks monotonically as the
        // layers ascend. The tessellation reuses the one lowering rather than re-lowering.
        List<(double MinZ, double MaxZ, Vector3d[] Loop)>? supportFacets = null;
        bool enforcersStated = supportModifiers?.Enforcers is { Count: > 0 };
        if (p.SupportOverhangAngle > 0 || enforcersStated)
        {
            var overhangMesh = solid is not null ? BRepTessellator.Tessellate(solid) : mesh!;
            supportFacets = p.SupportOverhangAngle > 0
                ? OverhangFacets(overhangMesh, p.SupportOverhangAngle)
                : [];
            if (enforcersStated)
            {
                // An ENFORCER forces support under ANY downward-facing facet inside its
                // volume, threshold or no threshold — the code-first paint-on support.
                var fields = supportModifiers!.Enforcers!
                    .Select(s => OrientForPrinting(s, up).ToImplicit()).ToList();
                foreach (var facet in OverhangFacets(overhangMesh, 0.01))
                {
                    var centroid = Vector3d.Zero;
                    foreach (var v in facet.Loop)
                        centroid += v;
                    centroid /= facet.Loop.Length;
                    if (fields.Any(f => f.Evaluate(centroid) <= 0))
                        supportFacets.Add(facet);
                }
            }
            supportFacets.Sort((a, b) => a.MaxZ.CompareTo(b.MaxZ));
        }
        int supportStart = 0;
        IReadOnlyList<Region2d> supportUnion = [];
        bool supportDirty = supportFacets is { Count: > 0 };

        // Section EVERY layer up front (mid-layer planes; the top layer's plane clamped
        // below the part's own top face so an exactly-divisible height cannot section
        // flush with it): the solid-shell split reads the NEIGHBOUR layers' regions, so
        // sections must exist before any layer's paths are built.
        var sectionZs = new double[layerCount];
        var sections = new IReadOnlyList<Region2d>[layerCount];
        for (int i = 0; i < layerCount; i++)
        {
            sectionZs[i] = Math.Min(
                tops[i] - heights[i] / 2,
                bounds.Max.Z - NudgeFraction * heights[i]);
            sections[i] = Compensate(
                SectionWithNudge(solid, mesh, sectionZs[i], h),
                i == 0 ? p.ElephantFootCompensation : 0, p.XYCompensation, p.HoleCompensation);
        }

        // Support BLOCKER shapes are sectioned per layer like the part itself (oriented
        // the same way), so a blocker masks supports exactly over its own volume.
        IReadOnlyList<Region2d>[]? blockerSections = null;
        if (supportModifiers?.Blockers is { Count: > 0 } blockers)
        {
            blockerSections = new IReadOnlyList<Region2d>[layerCount];
            var lowered = blockers
                .Select(s => OrientForPrinting(s, up))
                .Select(s => s.CanConvertTo(TargetRep.Brep)
                    ? ((BrepSolid?)s.ToBrep(), (HalfEdgeMesh?)null)
                    : (null, s.ToMesh()))
                .ToList();
            for (int i = 0; i < layerCount; i++)
            {
                var cut = new List<Region2d>();
                foreach (var (bSolid, bMesh) in lowered)
                    cut.AddRange(SectionWithNudge(bSolid, bMesh, sectionZs[i], h));
                blockerSections[i] = cut;
            }
        }

        // A spiral vase is ONE continuous wall, so every layer above the base must be a
        // single unholed island — refused by name here, before any path is built.
        if (p.SpiralVase)
        {
            for (int i = Math.Max(p.BottomSolidLayers, 1); i < layerCount; i++)
            {
                if (sections[i].Count != 1 || sections[i][0].Holes.Count > 0)
                    throw new ArgumentException(
                        $"SpiralVase needs a single island with no holes on every layer above "
                        + $"the base; layer {i} has {sections[i].Count} island(s) and "
                        + $"{sections[i].Sum(r => r.Holes.Count)} hole(s).");
            }
        }

        // The seam anchor for SeamPosition.Aligned: a fixed point per PART, so the seam
        // lands on the same side of every layer and lines up vertically.
        var seamAnchor = new Vector2d(bounds.Max.X, (bounds.Min.Y + bounds.Max.Y) / 2);

        var layers = new List<SliceLayer>(layerCount);
        var pen = new Vector2d(bounds.Min.X, bounds.Min.Y);

        // First-layer adhesion, outermost-first so the nozzle primes on the skirt, lays the
        // brim inward, and finishes AT the outline it rings. Both are outward offsets of
        // the whole region SET (islands' brims merge where they meet), and both are
        // write-only-when-stated: 0 loops / 0 width leaves every existing slice byte-identical.
        void Adhesion(IReadOnlyList<Region2d> around, List<SlicePath> into)
        {
            for (int k = p.SkirtLoops - 1; k >= 0; k--)
                foreach (var ring in Region2dOffset.Offset(
                    around, p.SkirtGap + bead * (k + 0.5)))
                    into.Add(new SlicePath(SlicePathRole.Skirt, ring.Outer, IsClosed: true));
            int brimLoops = (int)Math.Ceiling(p.BrimWidth / bead - 1e-9);
            for (int k = brimLoops - 1; k >= 0; k--)
                foreach (var ring in Region2dOffset.Offset(around, bead * (k + 0.5)))
                {
                    into.Add(new SlicePath(SlicePathRole.Brim, ring.Outer, IsClosed: true));
                    // A brim rings the INSIDE of a bore too (the outward offset's hole
                    // loops are the bore shrunk inward — exactly the interior brim).
                    foreach (var hole in ring.Holes)
                        into.Add(new SlicePath(SlicePathRole.Brim, hole, IsClosed: true));
                }
            if (into.Count > 0)
                pen = into[^1].End;
        }

        // The RAFT: sacrificial base layers under the part (and its supports), the whole
        // footprint grown by the margin and solid-filled; the part LIFTS by the raft's
        // height, so every part layer's Z shifts while its geometry stands still.
        double zShift = p.RaftLayers * h;
        if (p.RaftLayers > 0)
        {
            var seed = new List<Region2d>(sections[0]);
            if (supportFacets is { Count: > 0 })
            {
                foreach (var facet in supportFacets)
                    if (Footprint(facet.Loop) is { } footprint)
                        seed.Add(footprint);
            }
            var raftRegions = seed.Count > 0
                ? Region2dOffset.Offset(Region2dBoolean.UnionAll(seed), p.RaftMargin)
                : [];
            for (int r = 0; r < p.RaftLayers; r++)
            {
                var raftPaths = new List<SlicePath>();
                if (r == 0)
                    Adhesion(raftRegions, raftPaths);
                var fill = new List<SlicePath>();
                double raftAngle = r % 2 == 0 ? Math.PI / 4 : 3 * Math.PI / 4;
                foreach (var region in raftRegions)
                    fill.AddRange(RectilinearInfill(region, bead, raftAngle)
                        .Select(path => path with { Role = SlicePathRole.Raft }));
                raftPaths.AddRange(LinkGroup(fill, ref pen));
                layers.Add(new SliceLayer(
                    layers.Count, bounds.Min.Z + (layers.Count + 1) * h,
                    bounds.Min.Z + (layers.Count + 0.5) * h, raftRegions, raftPaths));
            }
        }

        for (int i = 0; i < layerCount; i++)
        {
            double sectionZ = sectionZs[i];
            var regions = sections[i];

            var paths = new List<SlicePath>();
            if (i == 0 && p.RaftLayers == 0)
                Adhesion(regions, paths);

            // Supports: columns under whatever overhang material is still ABOVE this layer's
            // top, minus the part's own section grown by the XY gap (so a column never fuses
            // to a wall), patterned as sparse one-direction lines (breakaway). A facet only
            // PARTLY above the plane contributes the projection of its clipped upper part, so
            // a slanted overhang's supports track its own height instead of stopping at the
            // facet's lowest point. Printed before the walls, and the whole block is skipped
            // when the profile states no supports — the write-only-when-stated path.
            if (supportFacets is not null)
            {
                double layerTop = tops[i];
                while (supportStart < supportFacets.Count
                    && supportFacets[supportStart].MaxZ - p.SupportZGap < layerTop - 1e-9)
                {
                    supportStart++;
                    supportDirty = true;
                }
                double clipPlane = layerTop + p.SupportZGap;
                bool anyClipped = false;
                for (int f = supportStart; f < supportFacets.Count && !anyClipped; f++)
                    anyClipped = supportFacets[f].MinZ < clipPlane - 1e-9;
                if (supportDirty || anyClipped)
                {
                    var footprints = new List<Region2d>();
                    for (int f = supportStart; f < supportFacets.Count; f++)
                    {
                        var facet = supportFacets[f];
                        IReadOnlyList<Vector3d> loop = facet.MinZ < clipPlane - 1e-9
                            ? ClipAbove(facet.Loop, clipPlane)
                            : facet.Loop;
                        if (Footprint(loop) is { } footprint)
                            footprints.Add(footprint);
                    }
                    supportUnion = Region2dBoolean.UnionAll(footprints);
                    supportDirty = anyClipped;
                }
                if (supportUnion.Count > 0)
                {
                    var supportRegions = regions.Count > 0
                        ? Region2dBoolean.Difference(
                            supportUnion, Region2dOffset.Offset(regions, p.SupportGap))
                        : supportUnion;
                    if (blockerSections?[i] is { Count: > 0 } masked && supportRegions.Count > 0)
                        supportRegions = Region2dBoolean.Difference(supportRegions, masked);

                    // INTERFACE layers: near the overhang the support densifies and turns
                    // perpendicular, so the part's first layer lands on a tighter grid.
                    IReadOnlyList<Region2d> interfaceRegions = [];
                    if (p.SupportInterfaceLayers > 0 && supportRegions.Count > 0)
                    {
                        var near = new List<Region2d>();
                        for (int f = supportStart; f < supportFacets.Count; f++)
                        {
                            var facet = supportFacets[f];
                            if (facet.MinZ - p.SupportZGap - layerTop
                                > p.SupportInterfaceLayers * h + 1e-9)
                                continue;
                            IReadOnlyList<Vector3d> loop = facet.MinZ < clipPlane - 1e-9
                                ? ClipAbove(facet.Loop, clipPlane)
                                : facet.Loop;
                            if (Footprint(loop) is { } footprint)
                                near.Add(footprint);
                        }
                        if (near.Count > 0)
                        {
                            var nearUnion = Region2dBoolean.UnionAll(near);
                            interfaceRegions =
                                Region2dBoolean.Intersection(supportRegions, nearUnion);
                            supportRegions =
                                Region2dBoolean.Difference(supportRegions, nearUnion);
                        }
                    }

                    var supports = new List<SlicePath>();
                    foreach (var region in supportRegions)
                        supports.AddRange(RectilinearInfill(region, p.SupportSpacing, 0)
                            .Select(path => path with { Role = SlicePathRole.Support }));
                    foreach (var region in interfaceRegions)
                        supports.AddRange(
                            RectilinearInfill(region, p.SupportSpacing / 2, Math.PI / 2)
                            .Select(path => path with { Role = SlicePathRole.Support }));
                    paths.AddRange(LinkGroup(supports, ref pen));
                }
            }

            var walls = new List<SlicePath>();
            var infill = new List<SlicePath>();
            foreach (var region in regions)
            {
                // Walls, innermost first by default (the outer wall prints last, onto
                // settled neighbours); ExternalPerimetersFirst inverts the order — the
                // stated trade: outer-first buys dimensional accuracy, inner-first
                // overhangs. The seam is the offset output's own first vertex unless the
                // profile states a SeamPosition, which rotates each closed loop.
                for (int step = 0; step < p.WallCount; step++)
                {
                    int k = p.ExternalPerimetersFirst ? step : p.WallCount - 1 - step;
                    double inset = bead * (k + 0.5);
                    foreach (var shell in Region2dOffset.Offset(region, -inset))
                    {
                        walls.Add(new SlicePath(SlicePathRole.Wall,
                            Fuzz(Seamed(shell.Outer, p.Seam, seamAnchor), p, k, i, bead),
                            IsClosed: true, k));
                        foreach (var hole in shell.Holes)
                            walls.Add(new SlicePath(SlicePathRole.Wall,
                                Fuzz(Seamed(hole, p.Seam, seamAnchor), p, k, i, bead),
                                IsClosed: true, k));
                    }
                }
            }

            // Infill: the region inside the innermost wall's inner face, less half a bead so
            // the infill bead just meets the wall bead. With solid shells stated, the core
            // splits into SOLID skin (where the neighbouring TopSolidLayers above or
            // BottomSolidLayers below do not cover it — a spot within N layers of air) filled
            // at the bead spacing, and SPARSE interior on the remainder; stating neither
            // keeps the incumbent per-region path byte-identically.
            bool shells = p.TopSolidLayers > 0 || p.BottomSolidLayers > 0;
            var monotonic = new List<SlicePath>();
            var ironing = new List<SlicePath>();
            if (p.InfillDensity > 0 || shells)
            {
                double infillInset = bead * (p.WallCount + 0.5);
                double angle = i % 2 == 0 ? Math.PI / 4 : 3 * Math.PI / 4;
                if (!shells)
                {
                    double spacing = bead / p.InfillDensity;
                    foreach (var region in regions)
                        foreach (var core in Region2dOffset.Offset(region, -infillInset))
                            infill.AddRange(FdmInfill.Sparse(
                                core, spacing, i, sectionZ, p.InfillPattern));
                }
                else
                {
                    var cores = new List<Region2d>();
                    foreach (var region in regions)
                        cores.AddRange(Region2dOffset.Offset(region, -infillInset));
                    if (cores.Count > 0)
                    {
                        // Covered = the intersection of the neighbour window's sections;
                        // a window reaching past the stack meets air, so those layers are
                        // wholly solid (the part's own top and bottom skins).
                        IReadOnlyList<Region2d>? covered = null;
                        for (int k = 1; k <= p.TopSolidLayers && covered is not { Count: 0 }; k++)
                            covered = IntersectNeighbour(covered, i + k);
                        for (int k = 1; k <= p.BottomSolidLayers && covered is not { Count: 0 }; k++)
                            covered = IntersectNeighbour(covered, i - k);

                        var solidSkin = covered!.Count == 0
                            ? cores
                            : Region2dBoolean.Difference(cores, covered);

                        // BRIDGES: skin the layer DIRECTLY below leaves in air (never the
                        // first layer — the bed is not air), filled solid along the
                        // region's own long axis so the strands span anchor to anchor.
                        if (p.DetectBridges && i > 0 && solidSkin.Count > 0)
                        {
                            var bridges = sections[i - 1].Count == 0
                                ? solidSkin
                                : Region2dBoolean.Difference(solidSkin, sections[i - 1]);
                            if (bridges.Count > 0)
                            {
                                solidSkin = Region2dBoolean.Difference(solidSkin, bridges);
                                foreach (var r in bridges)
                                {
                                    double spanX = r.Bounds.Max.X - r.Bounds.Min.X;
                                    double spanY = r.Bounds.Max.Y - r.Bounds.Min.Y;
                                    double bridgeAngle = spanX >= spanY ? 0 : Math.PI / 2;
                                    infill.AddRange(RectilinearInfill(r, bead, bridgeAngle)
                                        .Select(path => path with
                                        {
                                            Role = SlicePathRole.Bridge,
                                        }));
                                }
                            }
                        }

                        foreach (var r in solidSkin)
                        {
                            var skin = RectilinearInfill(r, bead, angle)
                                .Select(path => path with { Role = SlicePathRole.SolidInfill });
                            // MONOTONIC skins keep their scanline order and one direction
                            // (never linked or reversed), so overlaps always shingle the
                            // same way and the top surface reads as one sheet.
                            if (p.MonotonicSkins)
                                monotonic.AddRange(skin);
                            else
                                infill.AddRange(skin);
                        }
                        if (p.InfillDensity > 0 && covered.Count > 0)
                        {
                            foreach (var r in Region2dBoolean.Intersection(cores, covered))
                                infill.AddRange(FdmInfill.Sparse(
                                    r, bead / p.InfillDensity, i, sectionZ, p.InfillPattern));
                        }

                        // IRONING: a low-flow smoothing sweep over the TOP-exposed skin
                        // only (a bottom skin has nothing above to smooth for), one
                        // direction, appended after everything else on the layer.
                        if (p.IroningFlow > 0)
                        {
                            IReadOnlyList<Region2d>? above = null;
                            for (int k = 1; k <= p.TopSolidLayers && above is not { Count: 0 }; k++)
                                above = IntersectNeighbour(above, i + k);
                            var exposed = above!.Count == 0
                                ? cores
                                : Region2dBoolean.Difference(cores, above);
                            double spacing = p.IroningSpacing > 0 ? p.IroningSpacing : bead / 3;
                            foreach (var r in exposed)
                                ironing.AddRange(RectilinearInfill(r, spacing, 0)
                                    .Select(path => path with
                                    {
                                        Role = SlicePathRole.Ironing,
                                        Flow = p.IroningFlow,
                                    }));
                        }
                    }
                }
            }

            IReadOnlyList<Region2d>? IntersectNeighbour(
                IReadOnlyList<Region2d>? covered, int index)
            {
                IReadOnlyList<Region2d> neighbour =
                    index >= 0 && index < layerCount ? sections[index] : [];
                return covered is null
                    ? neighbour
                    : Region2dBoolean.Intersection(covered, neighbour);
            }

            // Walls keep their EMISSION order — innermost-out per region is a print-quality
            // decision (the outer wall lands on settled neighbours), not a travel optimisation,
            // and concentric shells are near each other anyway. Only the infill is greedily
            // linked (RunLinker), the pen carried across layers.
            paths.AddRange(walls);
            if (walls.Count > 0)
                pen = walls[^1].End;
            paths.AddRange(LinkGroup(infill, ref pen));
            if (monotonic.Count > 0)
            {
                paths.AddRange(monotonic);
                pen = monotonic[^1].End;
            }
            if (ironing.Count > 0)
            {
                paths.AddRange(ironing);
                pen = ironing[^1].End;
            }
            layers.Add(new SliceLayer(
                i + p.RaftLayers, tops[i] + zShift, sectionZ,
                regions, paths, layerHeights is null ? 0 : heights[i]));
        }

        return new SlicedPart(p, layers, up);
    }

    /// <summary>
    /// Per-layer heights from the STAIR-STEP CUSP criterion: a surface facet of unit
    /// normal n stepped by a layer of height h leaves a cusp of <c>h·|n_z|</c>, so
    /// bounding the cusp at the stated height gives <c>h ≤ cusp/|n_z|</c> — a
    /// near-horizontal surface takes thin layers, a vertical wall takes the maximum.
    /// The CUSP HEIGHT is a required engineering input (it IS the stated surface
    /// quality — a default would be a print-quality decision made by a library, the
    /// minimum-member-size rule). Facets resting on the bed exclude themselves (the
    /// bottom face is not a stair-step; it is the print). Feed the result to
    /// <see cref="Slice"/>'s <c>layerHeights</c>.
    /// </summary>
    public static IReadOnlyList<double> AdaptiveLayerHeights(
        Shape shape, double minHeight, double maxHeight, double cuspHeight,
        Vector3d? printDirection = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (!(minHeight > 0) || !(maxHeight >= minHeight) || !double.IsFinite(maxHeight))
            throw new ArgumentException(
                $"Need 0 < minHeight <= maxHeight; got [{minHeight:0.###}, {maxHeight:0.###}].");
        if (!(cuspHeight > 0) || !double.IsFinite(cuspHeight))
            throw new ArgumentException(
                $"cuspHeight must be finite and positive; got {cuspHeight:0.###}.");

        var up = (printDirection ?? Vector3d.UnitZ).Normalized();
        var oriented = OrientForPrinting(shape, up);
        var mesh = oriented.ToMesh();
        var bounds = oriented.Bounds();
        double bedZ = bounds.Min.Z;

        var facets = new List<(double MinZ, double MaxZ, double AbsNz)>();
        foreach (var face in mesh.Faces)
        {
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (var v in face.Vertices())
            {
                lo = Math.Min(lo, v.Position.Z);
                hi = Math.Max(hi, v.Position.Z);
            }
            if (hi <= bedZ + 1e-9)
                continue; // bed-resting: the bottom face is not a stair-step
            facets.Add((lo, hi, Math.Abs(face.Normal().Z)));
        }

        var heights = new List<double>();
        double z = bedZ;
        while (z < bounds.Max.Z - 1e-9)
        {
            // Two passes: bound over the widest candidate band, then re-bound over the
            // band the first answer actually spans (the standard adaptive refinement).
            double h = maxHeight;
            for (int pass = 0; pass < 2; pass++)
            {
                double maxNz = 0;
                foreach (var facet in facets)
                {
                    if (facet.MaxZ > z + 1e-12 && facet.MinZ < z + h - 1e-12)
                        maxNz = Math.Max(maxNz, facet.AbsNz);
                }
                h = maxNz > 1e-9
                    ? Math.Clamp(cuspHeight / maxNz, minHeight, maxHeight)
                    : maxHeight;
            }
            heights.Add(h);
            z += h;
        }
        return heights;
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

    /// <summary>Fuzzy skin: the OUTERMOST wall (never layer 0 — adhesion wants a flat
    /// first layer, and never an inner shell) resampled at the fuzz spacing and displaced
    /// ± half the thickness along its local normal by a DETERMINISTIC hash of
    /// (layer, point index) — the pattern-phase rule applied to noise: two slices of one
    /// shape are byte-identical, and no clock or RNG state exists to drift.</summary>
    private static IReadOnlyList<Vector2d> Fuzz(
        IReadOnlyList<Vector2d> loop, PrinterProfile p, int wallIndex, int layerIndex,
        double bead)
    {
        if (p.FuzzySkinThickness <= 0 || wallIndex != 0 || layerIndex == 0 || loop.Count < 3)
            return loop;
        double spacing = p.FuzzySkinSpacing > 0 ? p.FuzzySkinSpacing : 0.8 * bead;
        var points = new List<Vector2d>();
        int emitted = 0;
        for (int s = 0; s < loop.Count; s++)
        {
            var a = loop[s];
            var b = loop[(s + 1) % loop.Count];
            double length = (b - a).Length;
            if (length < 1e-12)
                continue;
            var direction = (b - a) / length;
            var normal = new Vector2d(direction.Y, -direction.X);
            int steps = Math.Max(1, (int)Math.Floor(length / spacing));
            for (int j = 0; j < steps; j++)
            {
                var q = a + direction * (j * length / steps);
                double amplitude = (Hash01(layerIndex, emitted) - 0.5) * p.FuzzySkinThickness;
                points.Add(q + normal * amplitude);
                emitted++;
            }
        }
        return points.Count >= 3 ? points : loop;
    }

    /// <summary>A uniform [0, 1) hash of (layer, index) — splittable, stateless, exact.</summary>
    private static double Hash01(int layer, int index)
    {
        unchecked
        {
            uint h = (uint)(layer * 73856093) ^ (uint)(index * 19349663) ^ 0x9E3779B9u;
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h / 4294967296.0;
        }
    }

    /// <summary>Dimensional compensations, applied to the stored sections so every
    /// consumer (walls, shells, supports) reads one geometry: the layer-0 INSET for
    /// elephant foot, the signed whole-section XY offset, and hole compensation — each
    /// hole grown by the stated amount, because printed holes come out small. All three
    /// zero returns the input by reference (the byte-identity path).</summary>
    private static IReadOnlyList<Region2d> Compensate(
        IReadOnlyList<Region2d> regions, double elephantInset, double xy, double hole)
    {
        double offset = xy - elephantInset;
        if (offset == 0 && hole == 0)
            return regions;
        var result = regions;
        if (offset != 0)
        {
            var moved = new List<Region2d>();
            foreach (var region in result)
                moved.AddRange(Region2dOffset.Offset(region, offset));
            result = moved;
        }
        if (hole > 0)
        {
            var adjusted = new List<Region2d>();
            foreach (var region in result)
            {
                if (region.Holes.Count == 0)
                {
                    adjusted.Add(region);
                    continue;
                }
                var grown = new List<Region2d>();
                foreach (var loop in region.Holes)
                {
                    var ccw = new List<Vector2d>(loop);
                    if (SignedArea(ccw) < 0)
                        ccw.Reverse();
                    grown.AddRange(Region2dOffset.Offset(new Region2d(ccw), hole));
                }
                adjusted.AddRange(Region2dBoolean.Difference(
                    new[] { new Region2d(region.Outer) }, grown));
            }
            result = adjusted;
        }
        return result;
    }

    private static double SignedArea(IReadOnlyList<Vector2d> loop)
    {
        double area2 = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Count];
            area2 += a.X * b.Y - b.X * a.Y;
        }
        return area2 / 2;
    }

    /// <summary>Rotates a closed loop so its stated seam vertex comes first: the offset
    /// output's own first vertex for <see cref="SeamPosition.Free"/> (the incumbent
    /// convention, bit-identical), the rearmost vertex for Rear, the vertex nearest the
    /// part's fixed anchor for Aligned (so seams line up vertically).</summary>
    private static IReadOnlyList<Vector2d> Seamed(
        IReadOnlyList<Vector2d> loop, SeamPosition seam, in Vector2d anchor)
    {
        if (seam == SeamPosition.Free || loop.Count < 2)
            return loop;
        int best = 0;
        for (int i = 1; i < loop.Count; i++)
        {
            bool better = seam == SeamPosition.Rear
                ? loop[i].Y > loop[best].Y
                    || (loop[i].Y == loop[best].Y && loop[i].X > loop[best].X)
                : (loop[i] - anchor).Length < (loop[best] - anchor).Length;
            if (better)
                best = i;
        }
        if (best == 0)
            return loop;
        var rotated = new List<Vector2d>(loop.Count);
        for (int i = 0; i < loop.Count; i++)
            rotated.Add(loop[(best + i) % loop.Count]);
        return rotated;
    }

    /// <summary>Collects the facets that OVERHANG past the threshold: downward-facing facets
    /// with <c>−n·Z &gt; sin(threshold)</c> — the <c>Manufacturability</c> rule, compared on
    /// the DOT PRODUCT and never on a derived angle (asin round-trips 1/√2 an ulp high, so a
    /// wall built at exactly 45° would read as an overhang — the recorded lesson). A facet
    /// resting on the bed excludes itself with no special case: nothing of it is above any
    /// layer top, so no layer ever finds material to support.</summary>
    private static List<(double MinZ, double MaxZ, Vector3d[] Loop)> OverhangFacets(
        HalfEdgeMesh mesh, double thresholdDegrees)
    {
        double sinThreshold = Math.Sin(thresholdDegrees * Math.PI / 180);
        var facets = new List<(double, double, Vector3d[])>();
        foreach (var face in mesh.Faces)
        {
            if (!(-face.Normal().Z > sinThreshold))
                continue;
            var loop = face.Vertices().Select(v => v.Position).ToArray();
            if (loop.Length < 3)
                continue;
            double lo = loop[0].Z, hi = loop[0].Z;
            for (int i = 1; i < loop.Length; i++)
            {
                lo = Math.Min(lo, loop[i].Z);
                hi = Math.Max(hi, loop[i].Z);
            }
            facets.Add((lo, hi, loop));
        }
        return facets;
    }

    /// <summary>Sutherland–Hodgman clip of a facet loop to the half-space <c>z ≥ zc</c> —
    /// the part of the overhang still above a layer's top, whose projection is what that
    /// layer must hold up.</summary>
    private static List<Vector3d> ClipAbove(IReadOnlyList<Vector3d> loop, double zc)
    {
        var result = new List<Vector3d>(loop.Count + 2);
        for (int i = 0; i < loop.Count; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Count];
            bool aIn = a.Z >= zc, bIn = b.Z >= zc;
            if (aIn)
                result.Add(a);
            if (aIn != bIn)
                result.Add(a + (b - a) * ((zc - a.Z) / (b.Z - a.Z)));
        }
        return result;
    }

    /// <summary>The loop's bed projection as a CCW region, or null when degenerate. The facet
    /// faces DOWN, so seen from above its loop winds clockwise and is reversed; the degeneracy
    /// guard is relative to the loop's own extent (an absolute epsilon on an AREA is the
    /// recorded trap — it fails quadratically with scale).</summary>
    private static Region2d? Footprint(IReadOnlyList<Vector3d> loop)
    {
        if (loop.Count < 3)
            return null;
        var flat = new List<Vector2d>(loop.Count);
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (var v in loop)
        {
            flat.Add(new Vector2d(v.X, v.Y));
            minX = Math.Min(minX, v.X);
            maxX = Math.Max(maxX, v.X);
            minY = Math.Min(minY, v.Y);
            maxY = Math.Max(maxY, v.Y);
        }
        double area2 = 0;
        for (int i = 0; i < flat.Count; i++)
        {
            var a = flat[i];
            var b = flat[(i + 1) % flat.Count];
            area2 += a.X * b.Y - b.X * a.Y;
        }
        double extent = Math.Max(maxX - minX, maxY - minY);
        if (Math.Abs(area2) < 1e-13 * extent * extent)
            return null;
        if (area2 < 0)
            flat.Reverse();
        return new Region2d(flat);
    }
}
