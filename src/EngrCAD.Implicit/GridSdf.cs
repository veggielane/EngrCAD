using System.Buffers;
using EngrCAD.Core;

namespace EngrCAD.Implicit;

// Sampled-grid implicits: bake any Sdf onto a dense uniform grid and evaluate it by
// trilinear interpolation — the standard acceleration for expensive ASTs (mesh SDFs,
// deep CSG trees) that get evaluated many times over the same region.
//
// geometry3Sharp counterparts: DenseGridTrilinearImplicit (grid → evaluable field),
// ImplicitFieldSampler3d (baker), CachingDenseGridTrilinearImplicit (lazy fill).
// Deliberate differences from g3:
//   - values are doubles, so grid nodes reproduce the source exactly (g3 stores floats);
//   - queries outside the grid return boundary value + distance-to-region instead of a
//     huge sentinel — continuous across the region boundary and correct in sign whenever
//     the solid is contained in the baked region;
//   - the lazy variant materializes whole blocks through the batch Evaluate seam
//     (one call per block) rather than filling single corners on demand.

/// <summary>
/// Geometry of a uniform sample grid: origin at the region minimum, cubic cells of
/// <see cref="CellSize"/>, sample counts per axis (cells + 1, so ≥ 2). The grid is
/// rounded up to whole cells, so it may extend slightly past the requested region's max.
/// </summary>
internal readonly struct GridFrame
{
    public readonly Vector3d Origin;
    public readonly double CellSize;
    public readonly double InverseCellSize;
    public readonly Vector3i Samples; // samples per axis

    public int Nx => Samples.X;
    public int Ny => Samples.Y;
    public int Nz => Samples.Z;

    public GridFrame(in Aabb region, double cellSize)
    {
        if (!(cellSize > 0))
            throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be positive.");
        if (!Sdf.IsFinite(region) || region.IsEmpty)
            throw new ArgumentException("The baked region must be finite and non-empty.", nameof(region));

        Origin = region.Min;
        CellSize = cellSize;
        InverseCellSize = 1.0 / cellSize;
        var size = region.Size;
        Samples = new Vector3i(
            SampleCount(size.X, cellSize),
            SampleCount(size.Y, cellSize),
            SampleCount(size.Z, cellSize));

    }

    /// <summary>
    /// Total sample count as a <see cref="long"/> — a fine grid over a large region
    /// overflows <see cref="int"/> long before it overflows this, and the sparse lazy grid
    /// is designed to work up there. Only bakers that need one contiguous value array are
    /// bounded by <see cref="int"/>; they call <see cref="RequireDenseAddressable"/>.
    /// </summary>
    public long TotalSamples => (long)Nx * Ny * Nz;

    /// <summary>Guards the dense bakers, whose values live in a single <c>double[]</c>.</summary>
    public void RequireDenseAddressable()
    {
        if (TotalSamples > int.MaxValue)
            throw new ArgumentException(
                $"Grid would need {TotalSamples} samples; increase the cell size, shrink the region, " +
                "or use a lazy grid (Sampled(..., lazy: true)), which materializes only the blocks touched.");
    }

    private static int SampleCount(double extent, double cellSize)
    {
        // Enough whole cells to cover the extent; the 1e-9 relative slack keeps exact
        // multiples (e.g. 2.5 / 0.25) from rounding up to an extra cell.
        double cells = Math.Max(1, Math.Ceiling(extent / cellSize * (1 - 1e-9)));
        if (cells >= int.MaxValue)
            throw new ArgumentException("Grid resolution overflows; increase the cell size.");
        return (int)cells + 1;
    }

    /// <summary>The actual grid extent (requested region rounded up to whole cells).</summary>
    public Aabb Region => new(
        Origin,
        Origin + new Vector3d((Nx - 1) * CellSize, (Ny - 1) * CellSize, (Nz - 1) * CellSize));

    /// <summary>World position of sample (i, j, k). Both bakers use this exact expression
    /// so dense and lazy grids of the same frame are bitwise identical.</summary>
    public Vector3d SamplePosition(int i, int j, int k) => new(
        Origin.X + i * CellSize,
        Origin.Y + j * CellSize,
        Origin.Z + k * CellSize);
}

/// <summary>
/// Shared trilinear evaluation over a <see cref="GridFrame"/>; subclasses supply sample
/// storage. Distance fidelity: exact at grid sample points, trilinear between — error
/// O(cellSize²) where the source field is smooth, O(cellSize) across derivative creases
/// (edges, the medial axis), and the zero level set shifts by the same order. The sign is
/// therefore reliable only where the cell size resolves the geometry: walls, gaps, or
/// holes thinner than a cell can vanish or fuse. Near the surface the magnitude is
/// neither a strict lower nor upper bound of the true distance — consumers should be
/// sign-driven (polygonization, CSG classification). Outside the baked region the value
/// is the boundary-clamped interpolant plus the Euclidean distance to the region:
/// continuous across the boundary, and correct in sign whenever the solid is contained
/// in the baked region (boundary samples ≥ 0). <see cref="Sdf.Bounds"/> is the baked
/// region itself, which is conservative under the same containment condition.
/// </summary>
internal abstract class SampledGridSdf : Sdf
{
    private protected readonly GridFrame Frame;
    private readonly Aabb _region;
    private readonly double _sourceBound;

    private protected SampledGridSdf(in GridFrame frame, double sourceBound)
    {
        Frame = frame;
        _region = frame.Region;
        _sourceBound = sourceBound;
    }

    public sealed override Aabb Bounds => _region;

    /// <summary>
    /// <b>√3 times the baked field's own bound</b> — a sampled grid is measurably steeper
    /// than what went into it, which is worth stating plainly because the rest of the engine
    /// had been assuming otherwise.
    /// <para>
    /// Each first difference of the interpolant along an axis spans one cell of the source,
    /// so each partial derivative inherits the source's bound; but the three can reach it at
    /// once, and then the gradient magnitude is √3 times it. That is attained rather than
    /// merely permitted: baking <c>max(x, y, z)</c> — a 1-Lipschitz field — onto the unit
    /// cell gives the interpolant <c>1 − (1−x)(1−y)(1−z)</c>, whose gradient at the origin
    /// corner is exactly (1, 1, 1).
    /// </para>
    /// <para>
    /// So a sampled grid genuinely breaks the 1-Lipschitz assumption <c>SurfaceCull</c> and
    /// the narrow-band octree rest on, by up to √3 — enough to matter, since a block's
    /// half-diagonal is many cells and the cull's cushion is one. Nothing in the repository
    /// reached that combination (no production path and no rendered example bakes a grid and
    /// then polygonizes it), which is why it had never surfaced; reporting the honest bound
    /// closes it rather than leaving it to be discovered.
    /// </para>
    /// </summary>
    public sealed override double LipschitzBound(in Aabb region) => Math.Sqrt(3) * _sourceBound;

    private protected abstract double Sample(int i, int j, int k);

    public sealed override double Evaluate(in Vector3d point)
    {
        // Outside the region, evaluate at the clamped boundary point and add the
        // Euclidean distance to it (see the class contract).
        var clamped = _region.ClosestPoint(point);
        double outside = clamped.DistanceTo(point);

        double gx = (clamped.X - Frame.Origin.X) * Frame.InverseCellSize;
        double gy = (clamped.Y - Frame.Origin.Y) * Frame.InverseCellSize;
        double gz = (clamped.Z - Frame.Origin.Z) * Frame.InverseCellSize;

        int i0 = Math.Clamp((int)gx, 0, Frame.Nx - 2);
        int j0 = Math.Clamp((int)gy, 0, Frame.Ny - 2);
        int k0 = Math.Clamp((int)gz, 0, Frame.Nz - 2);
        double fx = gx - i0;
        double fy = gy - j0;
        double fz = gz - k0;

        double v000 = Sample(i0, j0, k0);
        double v100 = Sample(i0 + 1, j0, k0);
        double v010 = Sample(i0, j0 + 1, k0);
        double v110 = Sample(i0 + 1, j0 + 1, k0);
        double v001 = Sample(i0, j0, k0 + 1);
        double v101 = Sample(i0 + 1, j0, k0 + 1);
        double v011 = Sample(i0, j0 + 1, k0 + 1);
        double v111 = Sample(i0 + 1, j0 + 1, k0 + 1);

        double v00 = v000 + (v100 - v000) * fx;
        double v10 = v010 + (v110 - v010) * fx;
        double v01 = v001 + (v101 - v001) * fx;
        double v11 = v011 + (v111 - v011) * fx;
        double v0 = v00 + (v10 - v00) * fy;
        double v1 = v01 + (v11 - v01) * fy;
        return v0 + (v1 - v0) * fz + outside;
    }
}

/// <summary>
/// Dense baked grid: every sample evaluated up front, row by row through the source's
/// batch <see cref="Sdf.Evaluate(ReadOnlySpan{Vector3d}, Span{double})"/> seam.
/// Memory: 8 bytes per sample (doubles — exact at nodes; e.g. a 256³ bake is ~134 MB).
/// </summary>
internal sealed class GridSdf : SampledGridSdf
{
    private readonly double[] _values; // x-fastest: [(k * Ny + j) * Nx + i]

    private GridSdf(in GridFrame frame, double[] values, double sourceBound)
        : base(frame, sourceBound) => _values = values;

    public static GridSdf Bake(Sdf source, in Aabb region, double cellSize)
    {
        double sourceBound = source.LipschitzBound(region);
        var frameLocal = new GridFrame(region, cellSize);
        frameLocal.RequireDenseAddressable();
        var values = new double[frameLocal.Nx * frameLocal.Ny * frameLocal.Nz];
        // Parallel over k-slabs: each block owns a contiguous slice of the value array
        // and every sample is computed from its (i, j, k) alone, so the bake is
        // bit-for-bit identical to a sequential fill regardless of scheduling.
        ParallelFor.Blocks(0, frameLocal.Nz, (k0, k1) =>
        {
            var rented = ArrayPool<Vector3d>.Shared.Rent(frameLocal.Nx);
            try
            {
                var points = rented.AsSpan(0, frameLocal.Nx);
                for (int k = k0; k < k1; k++)
                    for (int j = 0; j < frameLocal.Ny; j++)
                    {
                        for (int i = 0; i < frameLocal.Nx; i++)
                            points[i] = frameLocal.SamplePosition(i, j, k);
                        source.Evaluate(points, values.AsSpan((k * frameLocal.Ny + j) * frameLocal.Nx, frameLocal.Nx));
                    }
            }
            finally
            {
                ArrayPool<Vector3d>.Shared.Return(rented);
            }
        });
        return new GridSdf(frameLocal, values, sourceBound);
    }

    private protected override double Sample(int i, int j, int k) =>
        _values[(k * Frame.Ny + j) * Frame.Nx + i];
}

/// <summary>
/// Lazily baked <em>sparse</em> grid: samples live in 16³-sample blocks materialized on
/// first touch, each filled with a single deinterleaved batch
/// <see cref="Sdf.Evaluate(ReadOnlySpan{double}, ReadOnlySpan{double},
/// ReadOnlySpan{double}, Span{double})"/> call. Queries that never visit a region never
/// pay for it — the right choice when only part of the domain is probed (localized
/// booleans), or when the interesting part of a huge domain is a thin shell around a
/// surface.
/// <para>
/// <b>The block table is flat while that is free and two-level once it is not, and that is
/// what makes large domains possible at all.</b> A flat array of block pointers costs
/// 8 bytes per block <em>whether or not the block is ever touched</em>. Up to a 1024³
/// grid that is 2 MB — cheaper than any indirection saved, so the flat table stays, and
/// existing models keep exactly the lookup they had. A 4096³ grid is 256³ blocks, i.e.
/// 134 MB of pointers allocated up front to index a surface that may occupy well under 1%
/// of them — and its dense value array (550 GB) cannot be allocated at all. Above the
/// threshold, blocks are grouped into 16³-block super-blocks whose slot tables are
/// allocated on first touch: those 134 MB become a 32 KB top-level array plus 32 KB per
/// super-block actually visited.
/// </para>
/// <para>
/// (geometry3Sharp's <c>BiGrid3</c> is the same two-level idea, but its own implementation
/// is an unfinished stub with no value API, and its <c>DSparseGrid3</c> sibling hashes
/// <c>Vector3i</c> keys into a plain <c>Dictionary</c> with no thread-safety story,
/// allocate-on-read defaults and bounds that never shrink. The idea is adopted; the code is
/// not. Two dense array indices also beat hashing on the hot path — and this repo has a
/// standing lesson about packing structured 3D keys into hashed integers.)
/// </para>
/// <para>
/// Thread-safe for concurrent evaluation at both levels: block values are deterministic,
/// so racing fills produce identical arrays, first publish wins by
/// <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/> and the loser's array is
/// dropped. No locks anywhere.
/// </para>
/// </summary>
internal sealed class LazyGridSdf : SampledGridSdf
{
    private const int BlockSize = 16; // samples per axis per block (16³ = 4096 per batch)
    private const int SuperSize = 16; // blocks per axis per super-block (16³ slots = 32 KB)
    private const int SuperSlots = SuperSize * SuperSize * SuperSize;

    /// <summary>
    /// Blocks a flat pointer table is allowed to cover: 64³ blocks is a 1024³-sample grid
    /// and 2 MB of pointers, which is not worth an extra indirection to avoid. Past it the
    /// flat table would be the dominant cost of merely *constructing* the grid.
    /// </summary>
    private const int FlatBlockLimit = 64 * 64 * 64;

    private readonly Sdf _source;
    private readonly int _nbx, _nby, _nbz; // block counts per axis
    private readonly int _nsx, _nsy;       // super-block counts (x, y); z is implied
    private readonly double[]?[]? _flat;   // small grids: one slot per block
    private readonly double[]?[]?[]? _super; // large grids: slot tables per super-block
    private int _materialized;

    /// <param name="flatBlockLimit">
    /// Test seam: the block count above which the table groups. Production always uses
    /// <see cref="FlatBlockLimit"/>; a test passes 0 to force the grouped path onto a grid
    /// small enough to compare against a dense bake, which is how the two paths are held
    /// bit-for-bit equal.
    /// </param>
    public LazyGridSdf(Sdf source, in Aabb region, double cellSize, int flatBlockLimit = FlatBlockLimit)
        : base(new GridFrame(region, cellSize), source.LipschitzBound(region))
    {
        _source = source;
        _nbx = (Frame.Nx + BlockSize - 1) / BlockSize;
        _nby = (Frame.Ny + BlockSize - 1) / BlockSize;
        _nbz = (Frame.Nz + BlockSize - 1) / BlockSize;

        long blocks = (long)_nbx * _nby * _nbz;
        if (blocks <= flatBlockLimit)
        {
            _flat = new double[blocks][];
            return;
        }

        _nsx = (_nbx + SuperSize - 1) / SuperSize;
        _nsy = (_nby + SuperSize - 1) / SuperSize;
        int nsz = (_nbz + SuperSize - 1) / SuperSize;
        long groups = (long)_nsx * _nsy * nsz;
        if (groups > Array.MaxLength)
            throw new ArgumentException(
                $"Grid would need {groups} block groups to index; increase the cell size or shrink the region.");
        _super = new double[]?[]?[groups];
    }

    /// <summary>Blocks filled so far — the diagnostic that says how much of the domain was
    /// actually paid for. Grows as queries reach new regions.</summary>
    public int MaterializedBlocks => Volatile.Read(ref _materialized);

    /// <summary>Bytes of sample storage currently held. Counts blocks only; the two-level
    /// index is negligible by construction, which is the whole point of it.</summary>
    public long MaterializedBytes =>
        (long)MaterializedBlocks * BlockSize * BlockSize * BlockSize * sizeof(double);

    private protected override double Sample(int i, int j, int k)
    {
        int bi = i / BlockSize, bj = j / BlockSize, bk = k / BlockSize;
        double[]? block;
        if (_flat is not null)
        {
            block = _flat[(bk * _nby + bj) * _nbx + bi];
        }
        else
        {
            var slots = _super![(bk / SuperSize * _nsy + bj / SuperSize) * _nsx + bi / SuperSize];
            block = slots?[(bk % SuperSize * SuperSize + bj % SuperSize) * SuperSize + bi % SuperSize];
        }
        block ??= Materialize(bi, bj, bk);
        int sx = Math.Min(BlockSize, Frame.Nx - bi * BlockSize);
        int sy = Math.Min(BlockSize, Frame.Ny - bj * BlockSize);
        return block[((k - bk * BlockSize) * sy + (j - bj * BlockSize)) * sx + (i - bi * BlockSize)];
    }

    private double[] Materialize(int bi, int bj, int bk)
    {
        int i0 = bi * BlockSize, j0 = bj * BlockSize, k0 = bk * BlockSize;
        int sx = Math.Min(BlockSize, Frame.Nx - i0);
        int sy = Math.Min(BlockSize, Frame.Ny - j0);
        int sz = Math.Min(BlockSize, Frame.Nz - k0);
        int count = sx * sy * sz;
        var values = new double[count];
        var rented = ArrayPool<double>.Shared.Rent(count * 3);
        try
        {
            var xs = rented.AsSpan(0, count);
            var ys = rented.AsSpan(count, count);
            var zs = rented.AsSpan(count * 2, count);
            int n = 0;
            for (int k = k0; k < k0 + sz; k++)
                for (int j = j0; j < j0 + sy; j++)
                    for (int i = i0; i < i0 + sx; i++)
                    {
                        var p = Frame.SamplePosition(i, j, k);
                        xs[n] = p.X;
                        ys[n] = p.Y;
                        zs[n++] = p.Z;
                    }
            _source.Evaluate(xs, ys, zs, values);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rented);
        }

        double[]?[] slots;
        int slot;
        if (_flat is not null)
        {
            slots = _flat;
            slot = (bk * _nby + bj) * _nbx + bi;
        }
        else
        {
            slots = Slots((bk / SuperSize * _nsy + bj / SuperSize) * _nsx + bi / SuperSize);
            slot = (bk % SuperSize * SuperSize + bj % SuperSize) * SuperSize + bi % SuperSize;
        }

        var winner = Interlocked.CompareExchange(ref slots[slot], values, null);
        if (winner is not null)
            return winner;
        Interlocked.Increment(ref _materialized);
        return values;
    }

    /// <summary>Slot table of a super-block, published on first touch (loser's table dropped).</summary>
    private double[]?[] Slots(int group)
    {
        var existing = _super![group];
        if (existing is not null)
            return existing;
        var created = new double[]?[SuperSlots];
        return Interlocked.CompareExchange(ref _super[group], created, null) ?? created;
    }
}
