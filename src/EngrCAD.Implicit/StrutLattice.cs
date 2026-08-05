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
// visits a three-wide neighbourhood, and pays for the wider window with a prune.
//
// THE SOUNDNESS ARGUMENT. The strut set is invariant under the lattice, so d(p, S) =
// d(fold(p), S) with fold(p) = p - cell*round(p/cell) an isometry landing in [-cell/2,
// cell/2]^3. Every strut axis of the unit cell lies within that box too (the unit cells below
// are built so, with each strut's MIDPOINT folded in), so a copy at lattice index n differs
// from the folded point by at least (|n_i| - 1)*cell along axis i: any |n_i| >= 2 is at least
// one full cell away. The cell's own struts are never further than a cell away from a point
// inside it — measured per kind by StrutLatticeTests.OwnCellCoversTheQueryPoint, and asserted
// there rather than assumed — so the 27 copies with |n_i| <= 1 contain the nearest one. The
// end-to-end check is stronger than the argument: the field is compared for EQUALITY against
// a brute-force minimum over an explicit 5x5x5 block of capsules.
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
    private static QuantileTable Table(StrutLatticeKind kind, int resolution) =>
        QuantileTable.For(UnitTables[(int)kind], resolution, res => Sample(kind, res));

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
    /// nothing listed twice — the deduplication matters, because each strut costs a segment
    /// distance in every one of the 27 visited copies.
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
                if (faces[i].Dot(faces[j]) == 0)   // adjacent, i.e. not the antipodal pair
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
    /// <summary>The 27 lattice offsets, ordered by how far the copy can be from a folded
    /// point (the cell itself first): the prune below is a branch and bound, so the order it
    /// meets candidates in decides how many segment distances it pays for.</summary>
    private static readonly (int X, int Y, int Z)[] Neighbourhood = BuildNeighbourhood();

    private readonly (Vector3d A, Vector3d B)[] _struts;
    private readonly double _cell;
    private readonly double _radius;
    private readonly Vector3d _min, _max;

    public StrutLatticeSdf((Vector3d A, Vector3d B)[] struts, double cell, double radius)
    {
        _struts = struts;
        _cell = cell;
        _radius = radius;
        var min = new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var max = -min;
        foreach (var (a, b) in struts)
        {
            min = Vector3d.Min(min, Vector3d.Min(a, b));
            max = Vector3d.Max(max, Vector3d.Max(a, b));
        }
        _min = min;
        _max = max;
    }

    private static (int, int, int)[] BuildNeighbourhood()
    {
        var offsets = new List<(int, int, int)>(27);
        for (int ring = 0; ring <= 3; ring++)
            for (int i = -1; i <= 1; i++)
                for (int j = -1; j <= 1; j++)
                    for (int k = -1; k <= 1; k++)
                        if (Math.Abs(i) + Math.Abs(j) + Math.Abs(k) == ring)
                            offsets.Add((i, j, k));
        return [.. offsets];
    }

    public override double Evaluate(in Vector3d p)
    {
        double qx = p.X - _cell * Math.Round(p.X / _cell);
        double qy = p.Y - _cell * Math.Round(p.Y / _cell);
        double qz = p.Z - _cell * Math.Round(p.Z / _cell);

        double best = double.PositiveInfinity;
        foreach (var (i, j, k) in Neighbourhood)
        {
            var q = new Vector3d(qx - _cell * i, qy - _cell * j, qz - _cell * k);
            // A copy whose whole axis box is already further than the incumbent cannot hold
            // the nearest point; ">=" is safe because an equal candidate cannot lower the
            // minimum.
            if (BoxDistanceSquared(q) >= best)
                continue;
            foreach (var (a, b) in _struts)
                best = Math.Min(best, SegmentDistanceSquared(q, a, b));
        }
        return Math.Sqrt(best) - _radius;
    }

    private double BoxDistanceSquared(in Vector3d p)
    {
        double dx = Math.Max(Math.Max(_min.X - p.X, p.X - _max.X), 0);
        double dy = Math.Max(Math.Max(_min.Y - p.Y, p.Y - _max.Y), 0);
        double dz = Math.Max(Math.Max(_min.Z - p.Z, p.Z - _max.Z), 0);
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>The capsule's own kernel without the radius — the same clamp-and-project
    /// <see cref="Sdf.Capsule"/> uses, so the two cannot disagree about where a strut is.</summary>
    private static double SegmentDistanceSquared(in Vector3d p, in Vector3d a, in Vector3d b)
    {
        var pa = p - a;
        var ba = b - a;
        double h = Math.Clamp(pa.Dot(ba) / ba.LengthSquared, 0, 1);
        return (pa - ba * h).LengthSquared;
    }

    /// <summary>Infinite, like every lattice here — intersect it with a finite solid.</summary>
    public override Aabb Bounds => InfiniteBounds;
}
