using EngrCAD.Core;

namespace EngrCAD.Implicit;

// Strut (beam) lattices.
//
// THE CONTRAST WITH THE TPMS FAMILY IS THE POINT OF HAVING BOTH. A TPMS is a level set of a
// trigonometric polynomial, so its field is a normalized implicit function and its distance
// is a LOWER BOUND (see Tpms.cs). A strut is a CAPSULE, whose distance is exact, and the
// exact distance to a union is the minimum of the exact distances to its members — so a
// strut lattice is an EXACT distance field, LipschitzBound stays 1, and the strut diameter
// means exactly what it says. Nothing here is thicker than you asked for.
//
// WHY NOT Sdf.Repeat, WHICH IS WHAT THIS LOOKS LIKE. A lattice's struts span the whole cell
// — that is what makes them join up into a lattice at all — so a unit cell's capsules have
// bounds that overhang the cell by the strut RADIUS on every side. Repeat refuses exactly
// that (its two-cells-per-axis window is sound only while the child fits inside one cell),
// and it is right to: the overhang is precisely the case where a query point can be nearest
// to an instance the two-cell window never visits. Shortening the axes to make the SOLIDS
// fit would make consecutive copies meet at a single tangent point instead of joining, which
// is a pinched lattice rather than a lattice. So this node folds the query point itself and
// visits a three-wide neighbourhood, expanded once at construction and indexed.
//
// THE SOUNDNESS ARGUMENT. The strut set is invariant under the lattice, so d(p, S) =
// d(fold(p), S) with fold(p) = p - cell*round(p/cell) an isometry landing in [-cell/2,
// cell/2]^3. Every strut axis of the unit cell lies within that box too (the unit cells below
// are built so, with each strut's MIDPOINT folded in), so a copy at lattice index n differs
// from the folded point by at least (|n_i| - 1)*cell along axis i: any |n_i| >= 2 is at least
// one full cell away. The nearest strut the window DOES visit is nearer than that — measured
// per kind by StrutLatticeTests.TheVisitedNeighbourhoodCoversTheQueryPoint, with the grid's
// own resolution added back so the bound is certified rather than merely sampled — so the 27
// copies with |n_i| <= 1 contain the nearest one. The end-to-end check is stronger than the
// argument: the field is compared against a brute-force minimum over an explicit 5x5x5 block
// of capsules, with a companion showing that comparison can see a missing neighbour.
//
// Deliberately scalar in the batch path for now. A capsule kernel vectorizes well, but the
// fold and the per-point prune are data-dependent branches, so the vector form is its own
// piece of work; the node still batches through the default loop and is bit-identical to the
// scalar path by construction (it does not override the seam).

/// <summary>
/// The strut (beam) lattices this engine can build. Each is a periodic set of capsules, so
/// each is an <b>exact</b> distance field — the contrast with <see cref="TpmsKind"/>, whose
/// fields are normalized implicit functions and therefore lower bounds.
/// </summary>
public enum StrutLatticeKind
{
    /// <summary>Simple cubic: three mutually perpendicular struts through each lattice
    /// point, so the struts run along infinite axis-parallel lines.</summary>
    SimpleCubic,

    /// <summary>Body-centred cubic: the four body diagonals of each cell, meeting at the
    /// cell centre and at the corners.</summary>
    BodyCentredCubic,

    /// <summary>Face-centred cubic: the two diagonals of every cube face.</summary>
    FaceCentredCubic,

    /// <summary>The octet truss — the face-centred struts plus the octahedron formed by the
    /// six face centres, i.e. the nearest-neighbour graph of the FCC point lattice. The
    /// classic stretch-dominated cellular solid.</summary>
    Octet,

    /// <summary>Diamond cubic: the four-coordinated tetrahedral network, sixteen bonds per
    /// cell.</summary>
    Diamond,

    /// <summary>Kelvin: the edges of the space-filling packing of truncated octahedra (the
    /// BCC Voronoi cells) — the classic minimal-surface-area foam.</summary>
    Kelvin,
}

/// <summary>
/// The strut-lattice catalogue's geometry and the volume-fraction solve. The fields
/// themselves come from <see cref="Sdf.StrutLattice"/>.
/// </summary>
public static class StrutLattices
{
    /// <summary>
    /// The struts of one unit cell, as axis segments, in a cell centred on the origin. This
    /// is the geometry the field is built from — exposed because a caller (or a test) that
    /// wants to check the field against an explicit union of capsules needs exactly this
    /// list, and a second transcription of it would be free to drift from the one in use.
    /// </summary>
    public static IReadOnlyList<(Vector3d A, Vector3d B)> UnitCell(
        StrutLatticeKind kind, double cellSize)
    {
        RequireCell(cellSize);
        return StrutCells.For(kind, cellSize);
    }

    /// <summary>
    /// The fraction of space a lattice of the given strut diameter occupies — measured over a
    /// sampled unit cell, not a formula: the struts overlap at every node, so the sum of the
    /// capsule volumes over-counts and there is no closed form worth quoting.
    /// </summary>
    public static double VolumeFraction(
        StrutLatticeKind kind, double cellSize, double strutDiameter)
    {
        RequireCell(cellSize);
        return Table(kind, QuantileTable.CheckResolution)
            .FractionAtOrBelow(strutDiameter / 2 / cellSize);
    }

    /// <summary>
    /// The lattice of the given kind whose material occupies
    /// <paramref name="volumeFraction"/> of space. The diameter is solved as a quantile of
    /// the distance-to-axis field over a sampled unit cell, and the achieved fraction is then
    /// re-measured on a finer grid sharing no sample with it — see <see cref="LatticeFit"/>.
    /// </summary>
    public static LatticeFit ForVolumeFraction(
        StrutLatticeKind kind, double cellSize, double volumeFraction)
    {
        RequireCell(cellSize);
        if (!(volumeFraction > 0 && volumeFraction < 1))
            throw new ArgumentOutOfRangeException(
                nameof(volumeFraction), volumeFraction,
                "A volume fraction must lie strictly between 0 and 1.");

        double diameter =
            2 * cellSize * Table(kind, QuantileTable.FitResolution).Quantile(volumeFraction);
        return new LatticeFit(
            Sdf.StrutLattice(kind, cellSize, diameter),
            volumeFraction,
            VolumeFraction(kind, cellSize, diameter),
            diameter);
    }

    /// <summary>
    /// The distribution of the distance-to-strut-axis field over one cell, <b>measured on the
    /// unit cell and reused at every size</b>: the geometry scales linearly, so
    /// <c>d(p; cell) = cell * d(p / cell; 1)</c> exactly and one table serves every cell size.
    /// That is what makes the second call free — see <see cref="QuantileTable"/>.
    /// </summary>
    private static QuantileTable Table(StrutLatticeKind kind, int resolution)
    {
        // Named before the cache is indexed: an unknown kind must reach StrutCells' own
        // refusal rather than an index out of range.
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown strut lattice.");
        return QuantileTable.For(UnitTables[(int)kind], resolution, res => Sample(kind, res));
    }

    // One stable key object per kind for the shared cache.
    private static readonly object[] UnitTables =
        [.. Enum.GetValues<StrutLatticeKind>().Select(_ => new object())];

    private static double[] Sample(StrutLatticeKind kind, int resolution)
    {
        var axes = new StrutLatticeSdf(StrutCells.For(kind, 1), 1, 0);
        double step = 1.0 / resolution;
        var values = new double[resolution * resolution * resolution];
        int at = 0;
        for (int i = 0; i < resolution; i++)
        {
            double x = -0.5 + step * (i + 0.5);
            for (int j = 0; j < resolution; j++)
            {
                double y = -0.5 + step * (j + 0.5);
                for (int k = 0; k < resolution; k++)
                    values[at++] = axes.Evaluate(new Vector3d(x, y, -0.5 + step * (k + 0.5)));
            }
        }
        return values;
    }

    private static void RequireCell(double cellSize)
    {
        if (!(cellSize > 0))
            throw new ArgumentOutOfRangeException(
                nameof(cellSize), cellSize, "The cell size must be positive.");
    }
}

/// <summary>
/// The unit cells, as strut axis segments in a cell centred on the origin. Every segment's
/// MIDPOINT is folded into the cell, which is the precondition the field's neighbourhood
/// argument rests on (see the file remarks).
/// </summary>
internal static class StrutCells
{
    public static (Vector3d A, Vector3d B)[] For(StrutLatticeKind kind, double cellSize)
    {
        double h = cellSize / 2;
        var struts = kind switch
        {
            StrutLatticeKind.SimpleCubic => SimpleCubic(h),
            StrutLatticeKind.BodyCentredCubic => BodyCentredCubic(h),
            StrutLatticeKind.FaceCentredCubic => FaceCentredCubic(h),
            StrutLatticeKind.Octet => [.. FaceCentredCubic(h), .. Octahedron(h)],
            StrutLatticeKind.Diamond => Diamond(cellSize),
            StrutLatticeKind.Kelvin => Kelvin(cellSize),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown strut lattice."),
        };
        return [.. struts.Select(s => FoldMidpoint(s, cellSize))];
    }

    /// <summary>Three struts through the cell centre, one per axis. Their periodic union is a
    /// grid of infinite lines — consecutive copies join end to end.</summary>
    private static (Vector3d, Vector3d)[] SimpleCubic(double h) =>
    [
        ((-h, 0, 0), (h, 0, 0)),
        ((0, -h, 0), (0, h, 0)),
        ((0, 0, -h), (0, 0, h)),
    ];

    /// <summary>The four body diagonals; they meet at the cell centre and continue into the
    /// neighbouring cells through the corners.</summary>
    private static (Vector3d, Vector3d)[] BodyCentredCubic(double h) =>
    [
        ((-h, -h, -h), (h, h, h)),
        ((-h, -h, h), (h, h, -h)),
        ((-h, h, -h), (h, -h, h)),
        ((-h, h, h), (h, -h, -h)),
    ];

    /// <summary>
    /// The diagonals of the three LOW faces only. Every face of the lattice is the low face
    /// of exactly one cell, so the periodic union is all twelve diagonals of every cell with
    /// nothing listed twice — the deduplication matters, because every strut is expanded into
    /// 27 copies and carried in the sub-cells' candidate lists.
    /// </summary>
    private static (Vector3d, Vector3d)[] FaceCentredCubic(double h) =>
    [
        ((-h, -h, -h), (-h, h, h)), ((-h, -h, h), (-h, h, -h)),
        ((-h, -h, -h), (h, -h, h)), ((-h, -h, h), (h, -h, -h)),
        ((-h, -h, -h), (h, h, -h)), ((-h, h, -h), (h, -h, -h)),
    ];

    /// <summary>The twelve edges of the octahedron on the six face centres — the octet
    /// truss's other half. All strictly interior to the cell, so none is shared.</summary>
    private static (Vector3d, Vector3d)[] Octahedron(double h)
    {
        Vector3d[] faces = [(h, 0, 0), (-h, 0, 0), (0, h, 0), (0, -h, 0), (0, 0, h), (0, 0, -h)];
        var edges = new List<(Vector3d, Vector3d)>();
        for (int i = 0; i < faces.Length; i++)
            for (int j = i + 1; j < faces.Length; j++)
                // Adjacent, i.e. not the antipodal pair. An EXACT zero on purpose (the
                // scale-free tier's semantic-test case): the products are of +-h with an
                // exact 0, so a perpendicular pair reads exactly 0 and an antipodal one
                // exactly -h^2 — there is no near miss for a tolerance to arbitrate.
                if (faces[i].Dot(faces[j]) == 0)
                    edges.Add((faces[i], faces[j]));
        return [.. edges];
    }

    /// <summary>
    /// The diamond cubic network: two interpenetrating FCC sublattices offset by
    /// (1/4, 1/4, 1/4), each atom of one bonded to the four nearest of the other. Sixteen
    /// bonds per cell.
    /// </summary>
    private static (Vector3d, Vector3d)[] Diamond(double cell)
    {
        Vector3d[] fcc = [(0, 0, 0), (0, 0.5, 0.5), (0.5, 0, 0.5), (0.5, 0.5, 0)];
        Vector3d[] offsets = [(-1, -1, -1), (1, 1, -1), (1, -1, 1), (-1, 1, 1)];
        var bonds = new List<(Vector3d, Vector3d)>();
        foreach (var a in fcc)
        {
            var b = a + new Vector3d(0.25, 0.25, 0.25);   // the second sublattice's atom
            foreach (var d in offsets)
                bonds.Add((Place(b, cell), Place(b + d * 0.25, cell)));
        }
        return [.. bonds];

        // Fractional coordinates (cell corner at the origin) into the centred cell.
        static Vector3d Place(in Vector3d f, double cell) =>
            new((f.X - 0.5) * cell, (f.Y - 0.5) * cell, (f.Z - 0.5) * cell);
    }

    /// <summary>
    /// The Kelvin foam's edges: the truncated octahedron is the Voronoi cell of the BCC
    /// lattice, so the conventional cubic cell carries two of them (centred on the corner and
    /// on the cell centre) and every edge is shared by three — 2*36/3 = 24 distinct struts,
    /// which is what the deduplication below must produce. Generated rather than transcribed:
    /// the vertex set is every permutation of (0, ±cell/4, ±cell/2) about a centre, and the
    /// edges are the vertex pairs at the polyhedron's single edge length.
    /// </summary>
    private static (Vector3d, Vector3d)[] Kelvin(double cell)
    {
        double q = cell / 4, h = cell / 2;
        var offsets = new List<Vector3d>();
        for (int zeroAxis = 0; zeroAxis < 3; zeroAxis++)
            for (int swap = 0; swap < 2; swap++)
                for (int s1 = -1; s1 <= 1; s1 += 2)
                    for (int s2 = -1; s2 <= 1; s2 += 2)
                    {
                        var v = new double[3];
                        int a = (zeroAxis + 1) % 3, b = (zeroAxis + 2) % 3;
                        v[zeroAxis] = 0;
                        v[a] = s1 * (swap == 0 ? q : h);
                        v[b] = s2 * (swap == 0 ? h : q);
                        offsets.Add(new Vector3d(v[0], v[1], v[2]));
                    }

        double edgeLength = q * Math.Sqrt(2);
        // The weld tier expressed RELATIVELY: these vertices are exactly constructed from the
        // cell size, so the only question is round-off in the length, and an absolute 1e-9
        // would mean different things at a micrometre and at a metre.
        double tolerance = 1e-9 * cell;
        var seen = new HashSet<(long, long, long, long, long, long)>();
        var struts = new List<(Vector3d, Vector3d)>();
        Vector3d[] centres = [(0, 0, 0), (h, h, h)];
        foreach (var centre in centres)
            for (int i = 0; i < offsets.Count; i++)
                for (int j = i + 1; j < offsets.Count; j++)
                {
                    if (Math.Abs((offsets[i] - offsets[j]).Length - edgeLength) > tolerance)
                        continue;
                    var strut = FoldMidpoint((centre + offsets[i], centre + offsets[j]), cell);
                    if (seen.Add(Key(strut, cell)))
                        struts.Add(strut);
                }
        return [.. struts];

        // Midpoint (already canonical, see FoldMidpoint) plus an orientation-free direction,
        // quantized — the same strut reached from two of the three polyhedra sharing it must
        // collapse to one entry.
        static (long, long, long, long, long, long) Key(in (Vector3d A, Vector3d B) s, double cell)
        {
            var mid = (s.A + s.B) * 0.5;
            var dir = (s.B - s.A).Normalized();
            if (dir.X < 0 || (dir.X == 0 && (dir.Y < 0 || (dir.Y == 0 && dir.Z < 0))))
                dir = -dir;
            // Quantized three decades COARSER than the weld tier above, deliberately: this is
            // a bucket key rather than a comparison, and the struts it separates are a
            // quarter of a cell apart, so a coarse grid cannot collide two distinct ones
            // while a fine one could split a pair that the length test already called equal.
            long Q(double v) => (long)Math.Round(v / (1e-6 * cell));
            return (Q(mid.X), Q(mid.Y), Q(mid.Z), Q(dir.X * cell), Q(dir.Y * cell), Q(dir.Z * cell));
        }
    }

    /// <summary>
    /// Translates a strut by whole cells so its midpoint lands in the centred cell — the
    /// precondition the field's three-wide neighbourhood argument rests on.
    /// <para>
    /// The fold is HALF-OPEN, <c>[-cell/2, +cell/2)</c>, and that detail is load-bearing for
    /// the generated Kelvin cell: a symmetric round leaves a midpoint at +cell/2 where it is
    /// and one at −cell/2 where it is, so two spellings of the SAME lattice point survive and
    /// the deduplication below sees two struts (measured: 36 where the bitruncated cubic
    /// honeycomb has 24 per cell). Flooring the shifted coordinate collapses the pair.
    /// </para>
    /// </summary>
    private static (Vector3d A, Vector3d B) FoldMidpoint((Vector3d A, Vector3d B) s, double cell)
    {
        var mid = (s.A + s.B) * 0.5;
        var shift = new Vector3d(
            cell * Math.Floor(mid.X / cell + 0.5),
            cell * Math.Floor(mid.Y / cell + 0.5),
            cell * Math.Floor(mid.Z / cell + 0.5));
        return (s.A - shift, s.B - shift);
    }
}

/// <summary>
/// A periodic union of capsules. Exact distance — see the file remarks for why the
/// neighbourhood is three wide and why <see cref="Sdf.Repeat(in Vector3d)"/> cannot express
/// this.
/// </summary>
internal sealed class StrutLatticeSdf : Sdf
{
    /// <summary>Sub-cells per axis for the candidate lists. Four is where the measurement puts
    /// it: the lists are short enough that the scan is a handful of segment distances, and the
    /// build stays a few milliseconds.</summary>
    private const int Buckets = 4;

    private readonly Vector3d[] _a, _b;
    private readonly int[] _bucketStart;   // Buckets^3 + 1 offsets into _bucketItems
    private readonly int[] _bucketItems;
    private readonly double _cell, _radius, _bucketSize;

    /// <summary>
    /// <b>The query cost is decided here, not in Evaluate.</b> The visited neighbourhood is
    /// 27 copies of the unit cell — up to 648 segments for Kelvin — and scanning all of them
    /// per query is what makes a lattice unusable rather than merely slow (measured 4.7
    /// microseconds a sample, so a single polygonization runs into minutes). Two cheaper
    /// strategies were measured and both lost: a running-minimum prune over the struts' own
    /// boxes is barely better than no prune at all (4.1 microseconds), and a BVH over the
    /// block wins on the big sets and LOSES on the small ones (0.9 against 0.44 for simple
    /// cubic), so it is not a uniform answer either.
    /// <para>
    /// What is uniform is to do the pruning ONCE. The cell is divided into 4^3 sub-cells and
    /// each keeps the list of struts that can be nearest to a point inside it, so a query is a
    /// fold, an index and a short scan. The selection is exact rather than heuristic: the
    /// distance to a segment is a CONVEX function, so its maximum over a box is attained at a
    /// corner, and <c>U = min over struts of (max over the sub-cell's corners)</c> is an upper
    /// bound on how far a point in that sub-cell can be from the whole block. Any strut whose
    /// box is further than U from the sub-cell therefore cannot be nearest to any point in it,
    /// and is dropped.
    /// </para>
    /// </summary>
    public StrutLatticeSdf((Vector3d A, Vector3d B)[] unitCell, double cell, double radius)
    {
        _cell = cell;
        _radius = radius;
        _bucketSize = cell / Buckets;

        _a = new Vector3d[unitCell.Length * 27];
        _b = new Vector3d[_a.Length];
        var min = new Vector3d[_a.Length];
        var max = new Vector3d[_a.Length];
        int at = 0;
        for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
                for (int k = -1; k <= 1; k++)
                {
                    var shift = new Vector3d(cell * i, cell * j, cell * k);
                    foreach (var (a, b) in unitCell)
                    {
                        _a[at] = a + shift;
                        _b[at] = b + shift;
                        min[at] = Vector3d.Min(_a[at], _b[at]);
                        max[at] = Vector3d.Max(_a[at], _b[at]);
                        at++;
                    }
                }

        // A sub-cell's box is grown by a scale-free sliver, because the fold's own round-off
        // can leave a query point an ulp outside the cell and the list must still be the right
        // one for it. Growing a box can only lengthen a list, never shorten it.
        double slack = 1e-12 * cell;
        double half = cell / 2;
        _bucketStart = new int[Buckets * Buckets * Buckets + 1];
        var items = new List<int>();
        int bucket = 0;
        for (int i = 0; i < Buckets; i++)
            for (int j = 0; j < Buckets; j++)
                for (int k = 0; k < Buckets; k++)
                {
                    var lo = new Vector3d(
                        -half + i * _bucketSize - slack,
                        -half + j * _bucketSize - slack,
                        -half + k * _bucketSize - slack);
                    var hi = lo + new Vector3d(_bucketSize + 2 * slack, _bucketSize + 2 * slack, _bucketSize + 2 * slack);

                    double reach = double.PositiveInfinity;
                    for (int s = 0; s < _a.Length; s++)
                        reach = Math.Min(reach, FurthestCorner(lo, hi, s));

                    _bucketStart[bucket++] = items.Count;
                    for (int s = 0; s < _a.Length; s++)
                        if (BoxDistance(lo, hi, min[s], max[s]) <= reach)
                            items.Add(s);
                }
        _bucketStart[bucket] = items.Count;
        _bucketItems = [.. items];
    }

    public override double Evaluate(in Vector3d p)
    {
        // Fold into the cell — an isometry, since the strut set is invariant under the
        // lattice, and the step that lets one block of struts answer for all of space.
        double qx = p.X - _cell * Math.Round(p.X / _cell);
        double qy = p.Y - _cell * Math.Round(p.Y / _cell);
        double qz = p.Z - _cell * Math.Round(p.Z / _cell);
        var q = new Vector3d(qx, qy, qz);

        double half = _cell / 2;
        int i = Math.Clamp((int)((qx + half) / _bucketSize), 0, Buckets - 1);
        int j = Math.Clamp((int)((qy + half) / _bucketSize), 0, Buckets - 1);
        int k = Math.Clamp((int)((qz + half) / _bucketSize), 0, Buckets - 1);
        int bucket = (i * Buckets + j) * Buckets + k;

        double best = double.PositiveInfinity;
        for (int at = _bucketStart[bucket]; at < _bucketStart[bucket + 1]; at++)
            best = Math.Min(best, SegmentDistanceSquared(q, _bucketItems[at]));
        return Math.Sqrt(best) - _radius;
    }

    /// <summary>The capsule's own kernel without the radius — the same clamp-and-project
    /// <see cref="Sdf.Capsule"/> uses, so the two cannot disagree about where a strut is.</summary>
    private double SegmentDistanceSquared(in Vector3d p, int item)
    {
        var pa = p - _a[item];
        var ba = _b[item] - _a[item];
        double h = Math.Clamp(pa.Dot(ba) / ba.LengthSquared, 0, 1);
        return (pa - ba * h).LengthSquared;
    }

    /// <summary>The largest distance from a box to a strut, exactly: the distance to a segment
    /// is convex, so it is maximized at a corner.</summary>
    private double FurthestCorner(in Vector3d lo, in Vector3d hi, int item)
    {
        double worst = 0;
        for (int c = 0; c < 8; c++)
        {
            var corner = new Vector3d(
                (c & 1) == 0 ? lo.X : hi.X,
                (c & 2) == 0 ? lo.Y : hi.Y,
                (c & 4) == 0 ? lo.Z : hi.Z);
            worst = Math.Max(worst, SegmentDistanceSquared(corner, item));
        }
        return Math.Sqrt(worst);
    }

    private static double BoxDistance(
        in Vector3d aLo, in Vector3d aHi, in Vector3d bLo, in Vector3d bHi)
    {
        double dx = Math.Max(Math.Max(bLo.X - aHi.X, aLo.X - bHi.X), 0);
        double dy = Math.Max(Math.Max(bLo.Y - aHi.Y, aLo.Y - bHi.Y), 0);
        double dz = Math.Max(Math.Max(bLo.Z - aHi.Z, aLo.Z - bHi.Z), 0);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Infinite, like every lattice here — intersect it with a finite solid.</summary>
    public override Aabb Bounds => InfiniteBounds;
}
