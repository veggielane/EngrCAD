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
    public readonly int Nx, Ny, Nz; // samples per axis

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
        Nx = SampleCount(size.X, cellSize);
        Ny = SampleCount(size.Y, cellSize);
        Nz = SampleCount(size.Z, cellSize);

        long total = (long)Nx * Ny * Nz;
        if (total > int.MaxValue)
            throw new ArgumentException(
                $"Grid would need {total} samples; increase the cell size or shrink the region.");
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

    private protected SampledGridSdf(in GridFrame frame)
    {
        Frame = frame;
        _region = frame.Region;
    }

    public sealed override Aabb Bounds => _region;

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

    private GridSdf(in GridFrame frame, double[] values) : base(frame) => _values = values;

    public static GridSdf Bake(Sdf source, in Aabb region, double cellSize)
    {
        var frame = new GridFrame(region, cellSize);
        var values = new double[frame.Nx * frame.Ny * frame.Nz];
        var rented = ArrayPool<Vector3d>.Shared.Rent(frame.Nx);
        try
        {
            var points = rented.AsSpan(0, frame.Nx);
            int index = 0;
            for (int k = 0; k < frame.Nz; k++)
                for (int j = 0; j < frame.Ny; j++)
                {
                    for (int i = 0; i < frame.Nx; i++)
                        points[i] = frame.SamplePosition(i, j, k);
                    source.Evaluate(points, values.AsSpan(index, frame.Nx));
                    index += frame.Nx;
                }
        }
        finally
        {
            ArrayPool<Vector3d>.Shared.Return(rented);
        }
        return new GridSdf(frame, values);
    }

    private protected override double Sample(int i, int j, int k) =>
        _values[(k * Frame.Ny + j) * Frame.Nx + i];
}

/// <summary>
/// Lazily baked grid: samples live in 16³-sample blocks materialized on first touch,
/// each filled with a single batch <see cref="Sdf.Evaluate(ReadOnlySpan{Vector3d},
/// Span{double})"/> call. Queries that never visit a region never pay for it — the
/// right choice when only part of the domain is probed (e.g. localized booleans).
/// Thread-safe for concurrent evaluation: block values are deterministic, so racing
/// fills produce identical arrays and first-publish wins without locking.
/// </summary>
internal sealed class LazyGridSdf : SampledGridSdf
{
    private const int BlockSize = 16; // samples per axis per block (16³ = 4096 per batch)

    private readonly Sdf _source;
    private readonly int _nbx, _nby, _nbz; // block counts per axis
    private readonly double[]?[] _blocks;

    public LazyGridSdf(Sdf source, in Aabb region, double cellSize)
        : base(new GridFrame(region, cellSize))
    {
        _source = source;
        _nbx = (Frame.Nx + BlockSize - 1) / BlockSize;
        _nby = (Frame.Ny + BlockSize - 1) / BlockSize;
        _nbz = (Frame.Nz + BlockSize - 1) / BlockSize;
        _blocks = new double[_nbx * _nby * _nbz][];
    }

    private protected override double Sample(int i, int j, int k)
    {
        int bi = i / BlockSize, bj = j / BlockSize, bk = k / BlockSize;
        var block = _blocks[(bk * _nby + bj) * _nbx + bi] ?? Materialize(bi, bj, bk);
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
        var rented = ArrayPool<Vector3d>.Shared.Rent(count);
        try
        {
            var points = rented.AsSpan(0, count);
            int n = 0;
            for (int k = k0; k < k0 + sz; k++)
                for (int j = j0; j < j0 + sy; j++)
                    for (int i = i0; i < i0 + sx; i++)
                        points[n++] = Frame.SamplePosition(i, j, k);
            _source.Evaluate(points, values);
        }
        finally
        {
            ArrayPool<Vector3d>.Shared.Return(rented);
        }
        int slot = (bk * _nby + bj) * _nbx + bi;
        return Interlocked.CompareExchange(ref _blocks[slot], values, null) ?? values;
    }
}
