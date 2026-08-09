namespace EngrCAD.Core.Geometry2;

/// <summary>
/// A finite-order <b>three-dimensional Hilbert curve</b> laid over a box: one continuous path
/// that visits every cell of a <c>2^n</c> cube, for a solid infill, a single-channel cooling
/// passage, or any other consumer that wants ONE connected route through a volume rather than a
/// stack of independent layers.
///
/// <para><b>Everything <see cref="SpaceFillingCurve"/>'s remarks say applies here.</b> The name
/// still overpromises — this is one finite member of the sequence whose limit fills space — the
/// ORDER is still the parameter, a caller still states a <i>spacing</i> and is told the
/// <see cref="Spacing"/> it ACHIEVED beside the <see cref="RequestedSpacing"/>, and the
/// FOOTPRINT is still what is held (a cube over the box's largest extent), so the surplus lands
/// in a finer spacing rather than in a pattern phase the caller never stated.</para>
///
/// <para><b>Hilbert only, deliberately.</b> The consumer this exists for is a single connected
/// path through a volume, which is what Hilbert is for. Z-order's 3D member is the same
/// interleave one dimension up and is <i>not a curve</i> (consecutive cells are up to a grid
/// width apart), Peano's is radix 3 — 27 cells per level, so the achieved spacing quantises
/// three times as coarsely for no property this consumer wants — and Gosper's triangular
/// lattice has no 3D analogue at all. Offering an enum of one member would only invite the
/// other three to be filled in without a caller.</para>
///
/// <para><b>A PARALLEL type rather than a mode of <see cref="SpaceFillingCurve"/>.</b> A 2D
/// curve's lattice is <see cref="Vector2i"/> and its placement <see cref="Vector2d"/>; a 3D
/// one's are <see cref="Vector3i"/> and <see cref="Vector3d"/>, so the two share their
/// conventions and none of their data — the same call `CurvedRegion2d` makes against
/// `Region2d`. What IS shared is the vocabulary, so a consumer reading `Spacing`,
/// `RequestedSpacing`, `Lattice`, `Points`, `IsContinuous` and `MaxLatticeStep` finds the same
/// names meaning the same things.</para>
///
/// <para><b>What is exact.</b> Everything below the placement is integer: the sites are
/// generated as integers, they are <see cref="SiteCount">counted</see> in closed form
/// (<c>8^n</c>) and are pairwise distinct, consecutive sites differ by exactly one lattice step
/// (<see cref="AreNeighbours"/> — Manhattan 1, so a face diagonal is not a step), and
/// <see cref="Length"/> is <see cref="SegmentCount"/> × <see cref="Spacing"/> exactly. The
/// curve runs from <see cref="StartCell"/> to <see cref="EndCell"/>, two ADJACENT corners of
/// the cube, which is measured off the walk rather than asserted from the literature.</para>
/// </summary>
public sealed class SpaceFillingCurve3d
{
    /// <summary>Largest number of lattice sites a curve may carry before
    /// <see cref="Over(in Aabb, double, int)"/> refuses. A site costs a
    /// <see cref="Vector3i"/> plus a <see cref="Vector3d"/>, so the default is about 40 MB.</summary>
    public const int DefaultMaxSites = 1 << 20;

    /// <summary>The number of dimensions the transpose walk interleaves. Named because it
    /// appears in the bit arithmetic three times and a bare 3 there reads as a coincidence.</summary>
    private const int Dimensions = 3;

    private SpaceFillingCurve3d(
        int order, double requestedSpacing, double spacing, Vector3i[] lattice, Vector3d[] points)
    {
        Order = order;
        RequestedSpacing = requestedSpacing;
        Spacing = spacing;
        Lattice = lattice;
        Points = points;

        int maxStep = 0;
        bool continuous = true;
        for (int i = 1; i < lattice.Length; i++)
        {
            var delta = lattice[i] - lattice[i - 1];
            maxStep = Math.Max(maxStep, Math.Max(Math.Abs(delta.X), Math.Max(Math.Abs(delta.Y), Math.Abs(delta.Z))));
            continuous &= AreNeighbours(lattice[i - 1], lattice[i]);
        }
        MaxLatticeStep = maxStep;
        IsContinuous = continuous;

        double length = 0;
        for (int i = 1; i < points.Length; i++)
            length += points[i].DistanceTo(points[i - 1]);
        Length = length;

        var bounds = Aabb.Empty;
        foreach (var p in points)
            bounds = bounds.Union(p);
        Bounds = bounds;
    }

    /// <summary>The finite order: the number of recursive subdivisions, and the parameter the
    /// spacing request is answered with.</summary>
    public int Order { get; }

    /// <summary>The spacing the caller asked for, kept beside <see cref="Spacing"/> so a report
    /// can state both rather than implying they are one number.</summary>
    public double RequestedSpacing { get; }

    /// <summary>The spacing ACHIEVED: the model-space distance between consecutive points.
    /// Always at or under <see cref="RequestedSpacing"/>, and up to a factor of two finer,
    /// because the order is an integer.</summary>
    public double Spacing { get; }

    /// <summary>The integer lattice sites in visit order, indexing a <c>[0, 2^n)³</c> grid.</summary>
    public IReadOnlyList<Vector3i> Lattice { get; }

    /// <summary>The curve in model coordinates: one point per lattice site, at the site's cell
    /// centre.</summary>
    public IReadOnlyList<Vector3d> Points { get; }

    /// <summary>MEASURED: every consecutive pair of sites is a lattice neighbour.</summary>
    public bool IsContinuous { get; }

    /// <summary>MEASURED: the largest Chebyshev distance between consecutive lattice sites — 1
    /// for a continuous walk.</summary>
    public int MaxLatticeStep { get; }

    /// <summary>Total path length. This is <c>SegmentCount(Order) × Spacing</c> exactly.</summary>
    public double Length { get; }

    /// <summary>The curve's own bounds: the box's bounding CUBE inset by half a cell on every
    /// side, because the points are cell centres.</summary>
    public Aabb Bounds { get; }

    /// <summary>The first lattice site — <c>(0, 0, 0)</c>.</summary>
    public Vector3i StartCell => Lattice[0];

    /// <summary>The last lattice site. Adjacent to <see cref="StartCell"/> along one axis, so
    /// the two terminals are the ends of one edge of the cube.</summary>
    public Vector3i EndCell => Lattice[^1];

    // ---- construction ----

    /// <summary>
    /// Lays a Hilbert curve over <paramref name="bounds"/> at a spacing at or under
    /// <paramref name="spacing"/>, reporting what it achieved.
    /// </summary>
    /// <param name="bounds">The box to cover. The footprint is its bounding CUBE, centred on
    /// it — see the class remarks on which quantity quantises.</param>
    /// <param name="spacing">The largest acceptable distance between neighbouring passes.</param>
    /// <param name="maxSites">Refusal cap on the site count — see <see cref="DefaultMaxSites"/>.</param>
    public static SpaceFillingCurve3d Over(
        in Aabb bounds, double spacing, int maxSites = DefaultMaxSites)
    {
        if (bounds.IsEmpty)
            throw new ArgumentException("A space-filling curve needs a non-empty region to cover.", nameof(bounds));
        if (!(spacing > 0) || !double.IsFinite(spacing))
            throw new ArgumentOutOfRangeException(nameof(spacing), "The spacing must be positive and finite.");
        if (maxSites < 1)
            throw new ArgumentOutOfRangeException(nameof(maxSites), "The site cap must be at least 1.");

        var size = bounds.Max - bounds.Min;
        double side = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (!(side > 0))
        {
            throw new ArgumentException(
                "A space-filling curve needs a region of non-zero extent; the bounds given are a point.",
                nameof(bounds));
        }
        var centre = (bounds.Min + bounds.Max) * 0.5;

        int order = 0;
        long grid = 1;
        // One inequality with no epsilon in it: the smallest n whose cell size side/2^n is at or
        // under the request. Equality stops the search, so a request landing exactly on a cell
        // size is honoured exactly.
        while (side > spacing * grid)
        {
            long nextGrid = grid * 2;
            long nextSites = nextGrid * nextGrid * nextGrid;
            if (nextSites > maxSites)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(spacing),
                    $"A 3D Hilbert curve at spacing {spacing} over an extent of {side} needs order "
                    + $"{order + 1} ({nextSites} cells), past the {maxSites}-site cap. The FINEST "
                    + $"spacing this cap allows here is {side / grid}; anything coarser costs less.");
            }
            order++;
            grid = nextGrid;
        }

        double h = side / grid;
        var lattice = LatticeSites(order);
        var points = new Vector3d[lattice.Length];
        double minX = centre.X - side / 2;
        double minY = centre.Y - side / 2;
        double minZ = centre.Z - side / 2;
        for (int i = 0; i < lattice.Length; i++)
        {
            // Cell CENTRES, the 2D convention: the curve stops half a cell short of the cube's
            // faces, which is exactly what a bead of width h covers back out to.
            points[i] = new Vector3d(
                minX + (lattice[i].X + 0.5) * h,
                minY + (lattice[i].Y + 0.5) * h,
                minZ + (lattice[i].Z + 0.5) * h);
        }
        return new SpaceFillingCurve3d(order, spacing, h, lattice, points);
    }

    // ---- family facts ----

    /// <summary>Cells per side of the grid an order-n curve indexes: <c>2^n</c>.</summary>
    public static int GridSize(int order)
    {
        RequireOrder(order);
        return 1 << order;
    }

    /// <summary>The number of lattice sites an order-n curve visits, in closed form:
    /// <c>8^n</c>.</summary>
    public static long SiteCount(int order)
    {
        RequireOrder(order);
        long side = 1L << order;
        return side * side * side;
    }

    /// <summary>The number of segments an order-n curve draws — one fewer than its sites, the
    /// curve being open.</summary>
    public static long SegmentCount(int order) => SiteCount(order) - 1;

    /// <summary>Are two lattice sites neighbours? One cell along one axis — Manhattan 1, so a
    /// face or body diagonal is NOT a step. Exact integer arithmetic with no tolerance in it,
    /// which is what makes the adjacency assertion an identity.</summary>
    public static bool AreNeighbours(in Vector3i a, in Vector3i b)
    {
        var d = b - a;
        return Math.Abs(d.X) + Math.Abs(d.Y) + Math.Abs(d.Z) == 1;
    }

    // ---- lattice generator ----

    /// <summary>
    /// The order-n curve's lattice sites in visit order — the integer half, and where all the
    /// exactness lives.
    ///
    /// <para>Skilling's transpose algorithm (<i>Programming the Hilbert curve</i>, 2004), which
    /// is the n-dimensional generalisation the 2D bit walk is a special case of: the index's
    /// bits are dealt round-robin into one accumulator per axis (the "transpose" form), Gray
    /// decoded, and then the excess work of the recursion is undone by one invert-or-exchange
    /// pass per bit plane. It is chosen over a hand-written octant recursion for the reason the
    /// 2D file gives for Peano's digit rule: a closed form has no orientation table to get
    /// backwards, and the bijectivity test is what catches a flipped one.</para>
    /// </summary>
    public static Vector3i[] LatticeSites(int order)
    {
        RequireOrder(order);
        int side = 1 << order;
        long count = (long)side * side * side;
        var cells = new Vector3i[count];
        Span<uint> x = stackalloc uint[Dimensions];

        for (long d = 0; d < count; d++)
        {
            x.Clear();
            // Deal the index's bits, most significant first, round-robin across the axes: bit j
            // of the index lands in axis j % 3 at bit plane (order - 1 - j / 3).
            for (int j = 0; j < Dimensions * order; j++)
            {
                int bit = (int)((d >> (Dimensions * order - 1 - j)) & 1);
                x[j % Dimensions] |= (uint)bit << (order - 1 - j / Dimensions);
            }
            TransposeToAxes(x, order);
            cells[d] = new Vector3i((int)x[0], (int)x[1], (int)x[2]);
        }
        return cells;
    }

    /// <summary>Skilling's <c>TransposeToAxes</c>, transcribed term for term. The two branches
    /// of the inner test are his "invert" and "exchange"; <c>order == 0</c> leaves the single
    /// site at the origin because both loops are empty.</summary>
    private static void TransposeToAxes(Span<uint> x, int order)
    {
        if (order == 0)
            return;

        uint n = 2u << (order - 1);          // 2^order

        // Gray decode by H ^ (H / 2).
        uint t = x[Dimensions - 1] >> 1;
        for (int i = Dimensions - 1; i > 0; i--)
            x[i] ^= x[i - 1];
        x[0] ^= t;

        // Undo the excess work, one bit plane at a time.
        for (uint q = 2; q != n; q <<= 1)
        {
            uint p = q - 1;
            for (int i = Dimensions - 1; i >= 0; i--)
            {
                if ((x[i] & q) != 0)
                {
                    x[0] ^= p;                                  // invert
                }
                else
                {
                    t = (x[0] ^ x[i]) & p;                      // exchange
                    x[0] ^= t;
                    x[i] ^= t;
                }
            }
        }
    }

    private static void RequireOrder(int order)
    {
        if (order < 0)
            throw new ArgumentOutOfRangeException(nameof(order), $"A 3D Hilbert curve is defined from order 0; {order} was asked for.");
        if (order > 10)
            throw new ArgumentOutOfRangeException(nameof(order), "Orders past 10 overflow the site count of one array.");
    }
}
