namespace EngrCAD.Core.Geometry2;

/// <summary>
/// The space-filling curve families this kernel can lay over a region. They differ in what a
/// consumer needs rather than in fame — see <see cref="SpaceFillingCurve"/>.
/// </summary>
public enum SpaceFillingFamily
{
    /// <summary>Hilbert: radix 2, open, no straight run longer than three cells. The
    /// isotropy choice — an infill made of it has no preferred direction.</summary>
    Hilbert,

    /// <summary>Moore: Hilbert's CLOSED variant (four order-(n−1) Hilbert curves in a ring),
    /// radix 2. The one a toolpath that must return to its start should use.</summary>
    Moore,

    /// <summary>Peano: radix 3, open, with runs up to a whole sub-block long — fewer
    /// direction changes than Hilbert at the same spacing, so quicker to machine.</summary>
    Peano,

    /// <summary>Gosper (the flowsnake): radix 7 on a TRIANGULAR lattice, so it fills a
    /// hexagonal "Gosper island" the square families cannot tile. Its cells are hexagons and
    /// its footprint is not a rectangle — see <see cref="SpaceFillingCurve"/>.</summary>
    Gosper,

    /// <summary>Z-order (Morton): radix 2, and <b>NOT a curve</b> — consecutive cells are up
    /// to a whole grid width apart. It is a bijective spatial ORDERING (the one
    /// <c>PlanarSection</c>'s silhouette fold sorts by), offered here because it is the same
    /// index arithmetic, and refused by name where a continuous path is required.</summary>
    ZOrder,
}

/// <summary>
/// A finite-order space-filling curve laid over a rectangular region.
///
/// <para><b>The name overpromises, so state what this is.</b> A true space-filling curve is
/// the LIMIT of a sequence of approximations and has infinite length; what is built here is
/// one member of that sequence, and the ORDER is the parameter. A caller states a
/// <i>spacing</i> — the distance between neighbouring passes — and gets the order whose cell
/// size is at or under it, with the <see cref="Spacing"/> it actually achieved reported
/// rather than the <see cref="RequestedSpacing"/> echoed back (the <c>BiArcFit.MaxDeviation</c>
/// convention). The achieved spacing is always ≤ the request and can be up to a factor of the
/// family's radix finer, because the order is an integer.</para>
///
/// <para><b>Which quantity quantises is a decision, not arithmetic.</b> The order is fixed by
/// one inequality, <c>side ≤ spacing · radix^n</c>, and the surplus has to go somewhere: hold
/// the FOOTPRINT to the region and the spacing comes out finer than asked, hold the SPACING
/// and the footprint comes out larger than the region. Both give the SAME order, so neither is
/// cheaper. This class holds the footprint — a curve is laid <i>over</i> a region, and a
/// footprint that overhangs by an arbitrary amount would put the pattern's phase somewhere the
/// caller never stated, which for a layered infill is exactly what must be reproducible.</para>
///
/// <para><b>What is exact here.</b> Everything below the placement is integer: the lattice
/// sites are generated as integers, consecutive sites of a continuous family differ by exactly
/// one lattice step (<see cref="AreNeighbours"/>), the sites of an order-n curve are
/// <see cref="SiteCount">counted</see> in closed form and are pairwise distinct, and
/// <see cref="Length"/> is <see cref="SegmentCount"/> × <see cref="Spacing"/> exactly for every
/// continuous family. Only the placement into model coordinates is floating point.</para>
///
/// <para><b>Gosper is measured, not assumed.</b> The square families tile the region's
/// bounding square exactly, so coverage is structural. A Gosper island is a hexagonal blob, so
/// it is placed by its own INRADIUS — the largest disc about its centroid that its visited
/// cells contain, computed from the walk rather than tabulated — scaled to cover the region's
/// bounding circle. That makes Gosper's achieved spacing markedly finer than a square family's
/// at the same order, which is the honest price of a lattice that does not tile a rectangle.</para>
/// </summary>
public sealed class SpaceFillingCurve
{
    /// <summary>Largest number of lattice sites a curve may carry before
    /// <see cref="Over(in Aabb, SpaceFillingFamily, double, int)"/> refuses. A site costs a
    /// <see cref="Vector2i"/> plus a <see cref="Vector2d"/>, so the default is about 48 MB.</summary>
    public const int DefaultMaxSites = 1 << 21;

    /// <summary>The covering radius of a unit triangular lattice: no point of the plane is
    /// further than this from a lattice site. Used to turn "the nearest UNVISITED site is
    /// this far out" into a sound inradius for a Gosper island.</summary>
    private static readonly double TriangularCoveringRadius = 1.0 / Math.Sqrt(3.0);

    /// <summary>The six nearest-neighbour steps of the triangular lattice, in Eisenstein
    /// integer coordinates (basis <c>(1, 0)</c> and <c>(1/2, √3/2)</c>), at 60° apart starting
    /// from +x. Turning by +60° is the next index, modulo six.</summary>
    private static readonly Vector2i[] EisensteinSteps =
    [
        new(1, 0), new(0, 1), new(-1, 1), new(-1, 0), new(0, -1), new(1, -1),
    ];

    private SpaceFillingCurve(
        SpaceFillingFamily family, int order, double requestedSpacing, double spacing,
        Vector2i[] lattice, Vector2d[] points)
        : this(family, order, requestedSpacing, spacing, spacing, 1, 1, lattice, points)
    {
    }

    private SpaceFillingCurve(
        SpaceFillingFamily family, int order, double requestedSpacing,
        double spacingX, double spacingY, int blocksX, int blocksY,
        Vector2i[] lattice, Vector2d[] points)
    {
        Family = family;
        Order = order;
        RequestedSpacing = requestedSpacing;
        SpacingX = spacingX;
        SpacingY = spacingY;
        Spacing = Math.Max(spacingX, spacingY);
        BlocksX = blocksX;
        BlocksY = blocksY;
        Lattice = lattice;
        Points = points;
        IsClosed = family == SpaceFillingFamily.Moore;

        int maxStep = 0;
        bool continuous = true;
        for (int i = 1; i < lattice.Length; i++)
        {
            var delta = lattice[i] - lattice[i - 1];
            maxStep = Math.Max(maxStep, Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y)));
            continuous &= AreNeighbours(family, lattice[i - 1], lattice[i]);
        }
        MaxLatticeStep = maxStep;
        IsContinuous = continuous;

        double length = 0;
        for (int i = 1; i < points.Length; i++)
            length += points[i].DistanceTo(points[i - 1]);
        if (IsClosed && points.Length > 1)
            length += points[^1].DistanceTo(points[0]);
        Length = length;

        var bounds = Aabb.Empty;
        foreach (var p in points)
            bounds = bounds.Union(new Vector3d(p.X, p.Y, 0));
        Bounds = bounds;
    }

    /// <summary>Which family this curve belongs to.</summary>
    public SpaceFillingFamily Family { get; }

    /// <summary>The finite order: the number of recursive subdivisions. The parameter the
    /// spacing request is answered with.</summary>
    public int Order { get; }

    /// <summary>The spacing the caller asked for. Kept beside <see cref="Spacing"/> so a
    /// report can state both rather than implying they are one number.</summary>
    public double RequestedSpacing { get; }

    /// <summary>The spacing ACHIEVED: the model-space distance between consecutive points of
    /// a continuous family. Always ≤ <see cref="RequestedSpacing"/>. Where the cells are not
    /// square (<see cref="OverTiled"/> on a non-square rectangle) this is the LARGER of
    /// <see cref="SpacingX"/> and <see cref="SpacingY"/> — the number a bead has to cover.</summary>
    public double Spacing { get; }

    /// <summary>The achieved cell width. Equal to <see cref="Spacing"/> bit for bit for every
    /// square-footprint construction; only <see cref="OverTiled"/> can make the two differ.</summary>
    public double SpacingX { get; }

    /// <summary>The achieved cell height — see <see cref="SpacingX"/>.</summary>
    public double SpacingY { get; }

    /// <summary>How many Hilbert blocks the footprint is tiled from across x. 1 for every
    /// square-footprint construction, so the tiled path is a generalisation rather than a
    /// second mode.</summary>
    public int BlocksX { get; }

    /// <summary>How many Hilbert blocks the footprint is tiled from up y — see
    /// <see cref="BlocksX"/>.</summary>
    public int BlocksY { get; }

    /// <summary>How far the cells are from square: <c>max/min</c> of the two spacings, so 1
    /// exactly wherever the footprint is a square. REPORTED rather than bounded, because
    /// fitting a rectangle tightly and keeping cells square are genuinely in tension — small
    /// blocks fit any rectangle closely and stretch the cells, large ones keep the cells square
    /// and quantise the fit coarsely.</summary>
    public double Anisotropy => Math.Max(SpacingX, SpacingY) / Math.Min(SpacingX, SpacingY);

    /// <summary>The integer lattice sites in visit order. Square families index a
    /// <c>[0, radix^n)²</c> grid; <see cref="SpaceFillingFamily.Gosper"/> indexes the
    /// triangular lattice in Eisenstein coordinates (see <see cref="ToPlane"/>).</summary>
    public IReadOnlyList<Vector2i> Lattice { get; }

    /// <summary>The curve in model coordinates: one point per lattice site, at the site's cell
    /// centre.</summary>
    public IReadOnlyList<Vector2d> Points { get; }

    /// <summary>True when the last point is adjacent to the first, so the path is a loop —
    /// <see cref="SpaceFillingFamily.Moore"/> only. Asserted from the construction rather than
    /// trusted, by <c>AreNeighbours</c> in the tests.</summary>
    public bool IsClosed { get; }

    /// <summary>MEASURED: every consecutive pair of sites is a lattice neighbour. True for
    /// every family except <see cref="SpaceFillingFamily.ZOrder"/>.</summary>
    public bool IsContinuous { get; }

    /// <summary>MEASURED: the largest Chebyshev distance between consecutive lattice sites. 1
    /// for a continuous family; <c>gridSize − 1</c> for Z-order, whose quadrant handovers jump
    /// the full width of the grid.</summary>
    public int MaxLatticeStep { get; }

    /// <summary>Total path length, including the closing segment when
    /// <see cref="IsClosed"/>. For every continuous family this is
    /// <c>SegmentCount(Family, Order) × Spacing</c> exactly.</summary>
    public double Length { get; }

    /// <summary>The curve's own bounds (degenerate in z, the repo's 2D-bounds convention).
    /// For a square family this is the region's bounding square inset by half a cell on every
    /// side, because the points are cell CENTRES.</summary>
    public Aabb Bounds { get; }

    // ---- construction ----

    /// <summary>
    /// Lays a curve of <paramref name="family"/> over <paramref name="bounds"/> at a spacing at
    /// or under <paramref name="spacing"/>, reporting what it achieved.
    /// </summary>
    /// <param name="bounds">The region to cover (z is ignored — the 2D-bounds convention).</param>
    /// <param name="family">Which curve. See <see cref="SpaceFillingFamily"/>.</param>
    /// <param name="spacing">The largest acceptable distance between neighbouring passes.</param>
    /// <param name="maxSites">Refusal cap on the site count — see <see cref="DefaultMaxSites"/>.</param>
    public static SpaceFillingCurve Over(
        in Aabb bounds, SpaceFillingFamily family, double spacing, int maxSites = DefaultMaxSites)
    {
        if (bounds.IsEmpty)
            throw new ArgumentException("A space-filling curve needs a non-empty region to cover.", nameof(bounds));
        if (!(spacing > 0) || !double.IsFinite(spacing))
            throw new ArgumentOutOfRangeException(nameof(spacing), "The spacing must be positive and finite.");
        if (maxSites < 1)
            throw new ArgumentOutOfRangeException(nameof(maxSites), "The site cap must be at least 1.");

        double width = bounds.Max.X - bounds.Min.X;
        double height = bounds.Max.Y - bounds.Min.Y;
        if (!(width > 0) && !(height > 0))
        {
            throw new ArgumentException(
                "A space-filling curve needs a region of non-zero extent; the bounds given are a point.",
                nameof(bounds));
        }
        var centre = new Vector2d(
            (bounds.Min.X + bounds.Max.X) / 2, (bounds.Min.Y + bounds.Max.Y) / 2);

        return family == SpaceFillingFamily.Gosper
            ? OverByIsland(centre, Math.Sqrt(width * width + height * height) / 2, spacing, maxSites)
            : OverBySquare(centre, Math.Max(width, height), family, spacing, maxSites);
    }

    /// <summary>Lays a curve over a region's bounds — see
    /// <see cref="Over(in Aabb, SpaceFillingFamily, double, int)"/>.</summary>
    public static SpaceFillingCurve Over(
        Region2d region, SpaceFillingFamily family, double spacing, int maxSites = DefaultMaxSites)
    {
        ArgumentNullException.ThrowIfNull(region);
        return Over(region.Bounds, family, spacing, maxSites);
    }

    /// <summary>
    /// Lays a HILBERT curve over <paramref name="bounds"/> as a TILING of square blocks, so the
    /// footprint is the rectangle rather than its bounding square.
    ///
    /// <para><b>Why this exists.</b> <see cref="Over(in Aabb, SpaceFillingFamily, double, int)"/>
    /// holds the footprint to the region's bounding SQUARE, so on a long thin plate the achieved
    /// spacing is set by the LENGTH and most of the curve falls outside — an 80 × 12 plate at a
    /// requested spacing of 3 generates 1024 cells and keeps about 160. Tiling
    /// <c>blocksX × blocksY</c> Hilbert blocks and linking their ends
    /// (<see cref="TiledHilbertLattice"/>) covers the rectangle itself; the curve stays ONE
    /// continuous path, because a block enters and leaves at two adjacent corners of its square
    /// and the eight symmetries supply whichever pair its neighbours need.</para>
    ///
    /// <para><b>The footprint is still what is held</b>, which is what makes the cells
    /// anisotropic: with the block grid chosen, the cell size on each axis is that axis's extent
    /// over its cell count, so the curve covers exactly the rectangle asked for and
    /// <see cref="Anisotropy"/> reports how far from square that left the cells.
    /// <see cref="Spacing"/> is the larger of the two and is still never coarser than the
    /// request.</para>
    ///
    /// <para><b>Hilbert only.</b> Peano's blocks end at the same two adjacent corners so it
    /// would tile identically and is a straightforward addition when a caller wants the longer
    /// straight runs; Moore is a closed LOOP with no ends to link at all, and Gosper's island
    /// does not tile a rectangle in the first place. <c>blockOrder: 0</c> makes every block one
    /// cell and the route a plain boustrophedon serpentine — a legitimate member of the family
    /// rather than a degenerate case, and the one with the tightest fit and the worst
    /// isotropy.</para>
    /// </summary>
    /// <param name="bounds">The rectangle to cover (z ignored — the 2D-bounds convention).</param>
    /// <param name="spacing">The largest acceptable distance between neighbouring passes, on
    /// BOTH axes.</param>
    /// <param name="blockOrder">The Hilbert block order: blocks are <c>2^blockOrder</c> cells
    /// square. Higher is more isotropic and quantises the fit more coarsely.</param>
    /// <param name="maxSites">Refusal cap on the site count.</param>
    public static SpaceFillingCurve OverTiled(
        in Aabb bounds, double spacing, int blockOrder = 2, int maxSites = DefaultMaxSites)
    {
        if (bounds.IsEmpty)
            throw new ArgumentException("A space-filling curve needs a non-empty region to cover.", nameof(bounds));
        if (!(spacing > 0) || !double.IsFinite(spacing))
            throw new ArgumentOutOfRangeException(nameof(spacing), "The spacing must be positive and finite.");
        if (blockOrder < 0)
            throw new ArgumentOutOfRangeException(nameof(blockOrder), "The block order cannot be negative.");
        if (maxSites < 1)
            throw new ArgumentOutOfRangeException(nameof(maxSites), "The site cap must be at least 1.");

        double width = bounds.Max.X - bounds.Min.X;
        double height = bounds.Max.Y - bounds.Min.Y;
        if (!(width > 0) || !(height > 0))
        {
            throw new ArgumentException(
                "A tiled curve needs a region with positive extent on both axes; the bounds given "
                + "are degenerate.",
                nameof(bounds));
        }

        int m = checked(1 << blockOrder);
        long blocksX = TiledHilbertLattice.BlocksFor(width, m, spacing, maxSites);
        long blocksY = TiledHilbertLattice.BlocksFor(height, m, spacing, maxSites);
        long cellsX = blocksX * m;
        long cellsY = blocksY * m;
        if (cellsX * cellsY > maxSites)
        {
            throw new ArgumentOutOfRangeException(
                nameof(spacing),
                $"A tiled Hilbert curve at spacing {spacing} over {width} by {height} needs "
                + $"{cellsX} x {cellsY} cells, past the {maxSites}-site cap. The FINEST spacing "
                + $"this cap allows here is about {Math.Sqrt(width * height / maxSites)}; anything "
                + "coarser costs less.");
        }

        double spacingX = width / cellsX;
        double spacingY = height / cellsY;
        var route = TiledHilbertLattice.Build(blockOrder, (int)blocksX, (int)blocksY);
        var points = new Vector2d[route.Length];
        for (int i = 0; i < route.Length; i++)
        {
            points[i] = new Vector2d(
                bounds.Min.X + (route[i].X + 0.5) * spacingX,
                bounds.Min.Y + (route[i].Y + 0.5) * spacingY);
        }
        return new SpaceFillingCurve(
            SpaceFillingFamily.Hilbert, blockOrder, spacing, spacingX, spacingY,
            (int)blocksX, (int)blocksY, route, points);
    }

    private static SpaceFillingCurve OverBySquare(
        in Vector2d centre, double side, SpaceFillingFamily family, double spacing, int maxSites)
    {
        int radix = Radix(family);
        int order = MinimumOrder(family);
        long grid = IntegerPower(radix, order);

        // The order is fixed by one inequality with no epsilon in it: the smallest n whose cell
        // size side/radix^n is at or under the request. Equality stops the search, so a request
        // that lands exactly on a cell size is honoured exactly.
        while (side > spacing * grid)
        {
            long nextGrid = grid * radix;
            long nextSites = nextGrid * nextGrid;
            if (nextSites > maxSites)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(spacing),
                    $"A {family} curve at spacing {spacing} over an extent of {side} needs order "
                    + $"{order + 1} ({nextSites} cells), past the {maxSites}-site cap. The FINEST "
                    + $"spacing this cap allows here is {side / grid}; anything coarser costs less.");
            }
            order++;
            grid = nextGrid;
        }

        double h = side / grid;
        var lattice = LatticeSites(family, order);
        var points = new Vector2d[lattice.Count];
        double minX = centre.X - side / 2;
        double minY = centre.Y - side / 2;
        for (int i = 0; i < lattice.Count; i++)
        {
            // Cell CENTRES: the curve therefore stops half a cell short of the square's edge,
            // which is exactly what a stroke of width h covers back out to.
            points[i] = new Vector2d(minX + (lattice[i].X + 0.5) * h, minY + (lattice[i].Y + 0.5) * h);
        }
        return new SpaceFillingCurve(family, order, spacing, h, [.. lattice], points);
    }

    private static SpaceFillingCurve OverByIsland(
        in Vector2d centre, double radius, double spacing, int maxSites)
    {
        // A Gosper island is a hexagonal blob, so there is no side length to divide: the order
        // is grown until the island's own measured inradius, scaled so its step is h, reaches
        // the region's circumscribed circle.
        double finest = double.PositiveInfinity;
        for (int order = 1; ; order++)
        {
            long sites = SiteCount(SpaceFillingFamily.Gosper, order);
            if (sites > maxSites)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(spacing),
                    $"A Gosper curve at spacing {spacing} over a bounding circle of radius {radius} "
                    + $"needs order {order} ({sites} sites), past the {maxSites}-site cap. The FINEST "
                    + $"spacing this cap allows here is {finest}; anything coarser costs less.");
            }
            var lattice = GosperLattice(order);
            var island = IslandPlacement(lattice);
            if (!(island.Inradius > 0))
                continue;                       // too few cells to contain any disc at all

            double h = radius / island.Inradius;
            finest = h;
            if (h > spacing)
                continue;

            var points = new Vector2d[lattice.Length];
            for (int i = 0; i < lattice.Length; i++)
            {
                var local = ToPlane(SpaceFillingFamily.Gosper, lattice[i]) - island.Centroid;
                points[i] = centre + local * h;
            }
            return new SpaceFillingCurve(SpaceFillingFamily.Gosper, order, spacing, h, lattice, points);
        }
    }

    /// <summary>
    /// The centroid of a Gosper walk's sites and a SOUND inradius about it, both in lattice
    /// units. The inradius is the distance to the nearest UNVISITED site less the lattice's
    /// covering radius: a point closer than that to the centroid has its nearest site closer
    /// than the nearest unvisited one, so that site is visited and the point is inside the
    /// island. Measured from the walk rather than tabulated, so it cannot drift from it.
    /// </summary>
    private static (Vector2d Centroid, double Inradius) IslandPlacement(Vector2i[] lattice)
    {
        var sum = Vector2d.Zero;
        foreach (var site in lattice)
            sum += ToPlane(SpaceFillingFamily.Gosper, site);
        var centroid = sum / lattice.Length;

        var visited = new HashSet<Vector2i>(lattice.Length);
        int minA = int.MaxValue, maxA = int.MinValue, minB = int.MaxValue, maxB = int.MinValue;
        foreach (var site in lattice)
        {
            visited.Add(site);
            minA = Math.Min(minA, site.X);
            maxA = Math.Max(maxA, site.X);
            minB = Math.Min(minB, site.Y);
            maxB = Math.Max(maxB, site.Y);
        }

        // The box is grown by one so every unvisited site ADJACENT to the island is scanned;
        // an unvisited site nearer the centroid than all of those would have to be enclosed by
        // visited ones, which makes it adjacent to one and so already in the scan.
        double nearestUnvisited = double.PositiveInfinity;
        for (int a = minA - 1; a <= maxA + 1; a++)
        for (int b = minB - 1; b <= maxB + 1; b++)
        {
            var site = new Vector2i(a, b);
            if (visited.Contains(site))
                continue;
            nearestUnvisited = Math.Min(
                nearestUnvisited,
                ToPlane(SpaceFillingFamily.Gosper, site).DistanceTo(centroid));
        }
        return (centroid, nearestUnvisited - TriangularCoveringRadius);
    }

    // ---- family facts ----

    /// <summary>The linear subdivision factor: 2 for Hilbert/Moore/Z-order, 3 for Peano. A
    /// Gosper curve subdivides by 7 in CELLS and √7 in length, so it has no integer radix and
    /// is refused here by name.</summary>
    public static int Radix(SpaceFillingFamily family) => family switch
    {
        SpaceFillingFamily.Hilbert or SpaceFillingFamily.Moore or SpaceFillingFamily.ZOrder => 2,
        SpaceFillingFamily.Peano => 3,
        SpaceFillingFamily.Gosper => throw new ArgumentOutOfRangeException(
            nameof(family), "A Gosper curve tiles a triangular lattice: it subdivides by 7 in "
            + "cells and by the irrational sqrt(7) in length, so it has no linear radix."),
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };

    /// <summary>Cells per side of the square grid an order-n curve indexes. Square families only.</summary>
    public static int GridSize(SpaceFillingFamily family, int order)
    {
        RequireOrder(family, order);
        return checked((int)IntegerPower(Radix(family), order));
    }

    /// <summary>The number of lattice sites an order-n curve visits, in closed form:
    /// <c>4^n</c> (Hilbert, Moore, Z-order), <c>9^n</c> (Peano) and <c>7^n + 1</c> (Gosper,
    /// whose 7^n is a count of SEGMENTS — the one family whose recursion multiplies steps
    /// rather than cells).</summary>
    public static long SiteCount(SpaceFillingFamily family, int order)
    {
        RequireOrder(family, order);
        return family switch
        {
            SpaceFillingFamily.Peano => IntegerPower(9, order),
            SpaceFillingFamily.Gosper => IntegerPower(7, order) + 1,
            _ => IntegerPower(4, order),
        };
    }

    /// <summary>The number of segments an order-n curve draws — one fewer than its sites,
    /// except for the closed <see cref="SpaceFillingFamily.Moore"/> curve, whose closing
    /// segment makes the two equal.</summary>
    public static long SegmentCount(SpaceFillingFamily family, int order) =>
        family == SpaceFillingFamily.Moore
            ? SiteCount(family, order)
            : SiteCount(family, order) - 1;

    /// <summary>The lowest order a family is defined at: 1 for the closed Moore curve (a loop
    /// needs four cells) and for Gosper (whose axiom is one step), 0 for the rest.</summary>
    public static int MinimumOrder(SpaceFillingFamily family) =>
        family is SpaceFillingFamily.Moore or SpaceFillingFamily.Gosper ? 1 : 0;

    /// <summary>Are two lattice sites neighbours in this family's lattice? A square family's
    /// step is one cell along one axis (Manhattan 1 — a DIAGONAL is not a step); a Gosper
    /// step is one of the six Eisenstein unit vectors. An exact integer test with no
    /// tolerance in it, which is what makes the adjacency assertion an identity.</summary>
    public static bool AreNeighbours(SpaceFillingFamily family, in Vector2i a, in Vector2i b)
    {
        var delta = b - a;
        if (family == SpaceFillingFamily.Gosper)
        {
            foreach (var step in EisensteinSteps)
            {
                if (delta.X == step.X && delta.Y == step.Y)
                    return true;
            }
            return false;
        }
        return Math.Abs(delta.X) + Math.Abs(delta.Y) == 1;
    }

    /// <summary>Where a lattice site sits in the plane, in lattice units. Square families are
    /// the identity; Gosper's Eisenstein pair <c>(a, b)</c> becomes
    /// <c>a·(1, 0) + b·(1/2, √3/2)</c>, in which all six neighbour steps have length exactly
    /// one.</summary>
    public static Vector2d ToPlane(SpaceFillingFamily family, in Vector2i site) =>
        family == SpaceFillingFamily.Gosper
            ? new Vector2d(site.X + site.Y * 0.5, site.Y * (Math.Sqrt(3.0) / 2))
            : new Vector2d(site.X, site.Y);

    // ---- lattice generators ----

    /// <summary>The order-n curve's lattice sites in visit order — the integer half of every
    /// family, and where all the exactness lives.</summary>
    public static IReadOnlyList<Vector2i> LatticeSites(SpaceFillingFamily family, int order)
    {
        RequireOrder(family, order);
        return family switch
        {
            SpaceFillingFamily.Hilbert => HilbertLattice(order),
            SpaceFillingFamily.Moore => MooreLattice(order),
            SpaceFillingFamily.Peano => PeanoLattice(order),
            SpaceFillingFamily.Gosper => GosperLattice(order),
            SpaceFillingFamily.ZOrder => ZOrderLattice(order),
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };
    }

    /// <summary>Hilbert by the classic index-to-coordinate walk: two bits of the index per
    /// level pick a quadrant and a reflection. Starts at <c>(0, 0)</c> and ends at
    /// <c>(2^n − 1, 0)</c> — both on the bottom row, which is what lets four of them close
    /// into a Moore curve.</summary>
    private static Vector2i[] HilbertLattice(int order)
    {
        int side = 1 << order;
        long count = (long)side * side;
        var cells = new Vector2i[count];
        for (int d = 0; d < count; d++)
        {
            int x = 0, y = 0, t = d;
            for (int s = 1; s < side; s <<= 1)
            {
                int rx = 1 & (t >> 1);
                int ry = 1 & (t ^ rx);
                if (ry == 0)
                {
                    if (rx == 1)
                    {
                        x = s - 1 - x;
                        y = s - 1 - y;
                    }
                    (x, y) = (y, x);
                }
                x += s * rx;
                y += s * ry;
                t >>= 2;
            }
            cells[d] = new Vector2i(x, y);
        }
        return cells;
    }

    /// <summary>
    /// Moore as four order-(n−1) Hilbert curves in a ring. The base curve runs corner to corner
    /// along its bottom row, so a quarter turn one way puts a copy running UP its right column
    /// and a quarter turn the other puts one running DOWN its left column; stacking two of the
    /// first on the left and two of the second on the right closes the loop with nothing to
    /// arrange. The closure is asserted rather than trusted (see <see cref="AreNeighbours"/>).
    /// </summary>
    private static Vector2i[] MooreLattice(int order)
    {
        int m = 1 << (order - 1);
        var quarter = HilbertLattice(order - 1);
        var cells = new Vector2i[4 * quarter.Length];
        int at = 0;
        // Left column, bottom then top: the base curve rotated a quarter turn counter-clockwise
        // runs from (m-1, 0) to (m-1, m-1), so the lower copy hands over to the upper one.
        foreach (var c in quarter)
            cells[at++] = new Vector2i(m - 1 - c.Y, c.X);
        foreach (var c in quarter)
            cells[at++] = new Vector2i(m - 1 - c.Y, c.X + m);
        // Right column, top then bottom: the clockwise rotation runs from (0, m-1) down to (0, 0).
        foreach (var c in quarter)
            cells[at++] = new Vector2i(c.Y + m, m - 1 - c.X + m);
        foreach (var c in quarter)
            cells[at++] = new Vector2i(c.Y + m, m - 1 - c.X);
        return cells;
    }

    /// <summary>
    /// Peano by his own digit rule, which is a closed form rather than a recursion: the index
    /// in base 3 hands its odd-position digits to x and its even-position ones to y, each
    /// complemented (<c>t → 2 − t</c>) when the digits already spent on the OTHER axis sum to
    /// an odd number. That complement is what turns the serpentine round at every level, and
    /// getting its parity backwards is exactly what the bijectivity test catches.
    /// </summary>
    private static Vector2i[] PeanoLattice(int order)
    {
        long side = IntegerPower(3, order);
        long count = side * side;
        var cells = new Vector2i[count];
        var digits = new int[2 * order];
        for (long d = 0; d < count; d++)
        {
            long rest = d;
            for (int i = digits.Length - 1; i >= 0; i--)
            {
                digits[i] = (int)(rest % 3);
                rest /= 3;
            }
            int x = 0, y = 0, sumOdd = 0, sumEven = 0;
            for (int i = 0; i < order; i++)
            {
                int a = digits[2 * i];
                x = x * 3 + (sumEven % 2 == 1 ? 2 - a : a);
                sumOdd += a;
                int b = digits[2 * i + 1];
                y = y * 3 + (sumOdd % 2 == 1 ? 2 - b : b);
                sumEven += b;
            }
            cells[d] = new Vector2i(x, y);
        }
        return cells;
    }

    /// <summary>Z-order: the Morton code IS the index, so the walk is one
    /// <see cref="Morton2d.Decode"/> per cell. Bijective and cheap, and discontinuous — the
    /// handover between quadrants jumps the whole grid width.</summary>
    private static Vector2i[] ZOrderLattice(int order)
    {
        int side = 1 << order;
        long count = (long)side * side;
        var cells = new Vector2i[count];
        for (int d = 0; d < count; d++)
        {
            Morton2d.Decode((uint)d, out uint x, out uint y);
            cells[d] = new Vector2i((int)x, (int)y);
        }
        return cells;
    }

    /// <summary>
    /// Gosper by its L-system, walked directly on the Eisenstein integers rather than expanded
    /// into a string: <c>A → A−B−−B+A++AA+B−</c>, <c>B → +A−BB−−B−A++A+B</c>, sixty degrees per
    /// turn, both letters drawing. Every step is one of six unit vectors, so adjacency is a
    /// property of the construction and what the tests have to establish instead is that the
    /// 7^n + 1 sites are DISTINCT.
    /// <para>The recursion's depth is the ORDER, which the site cap bounds well under twenty —
    /// not the input size, so the recorded no-big-enough-stack rule does not apply.</para>
    /// </summary>
    private static Vector2i[] GosperLattice(int order)
    {
        var sites = new List<Vector2i>((int)IntegerPower(7, order) + 1);
        var at = Vector2i.Zero;
        int direction = 0;
        sites.Add(at);

        void Step()
        {
            at += EisensteinSteps[direction];
            sites.Add(at);
        }
        void Turn(int by) => direction = (direction + by + 6) % 6;
        void Expand(bool isA, int depth)
        {
            if (depth == 0)
            {
                Step();
                return;
            }
            if (isA)
            {
                // A - B - - B + A + + A A + B -
                Expand(true, depth - 1); Turn(-1);
                Expand(false, depth - 1); Turn(-1); Turn(-1);
                Expand(false, depth - 1); Turn(+1);
                Expand(true, depth - 1); Turn(+1); Turn(+1);
                Expand(true, depth - 1);
                Expand(true, depth - 1); Turn(+1);
                Expand(false, depth - 1); Turn(-1);
            }
            else
            {
                // + A - B B - - B - A + + A + B
                Turn(+1); Expand(true, depth - 1); Turn(-1);
                Expand(false, depth - 1);
                Expand(false, depth - 1); Turn(-1); Turn(-1);
                Expand(false, depth - 1); Turn(-1);
                Expand(true, depth - 1); Turn(+1); Turn(+1);
                Expand(true, depth - 1); Turn(+1);
                Expand(false, depth - 1);
            }
        }

        Expand(true, order);
        return [.. sites];
    }

    // ---- small helpers ----

    private static void RequireOrder(SpaceFillingFamily family, int order)
    {
        int minimum = MinimumOrder(family);
        if (order < minimum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(order), $"A {family} curve is defined from order {minimum}; {order} was asked for.");
        }
        if (order > 24)
            throw new ArgumentOutOfRangeException(nameof(order), "Orders past 24 overflow the site count.");
    }

    private static long IntegerPower(int b, int e)
    {
        long result = 1;
        for (int i = 0; i < e; i++)
            result *= b;
        return result;
    }
}
