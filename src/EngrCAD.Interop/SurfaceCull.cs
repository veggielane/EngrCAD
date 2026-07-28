using System.Runtime.InteropServices;
using EngrCAD.Core;
using EngrCAD.Implicit;

namespace EngrCAD.Interop;

/// <summary>
/// Which blocks of a Surface Nets grid can possibly contain the iso-surface — the "only
/// visit cells near the surface" half of a continuation contouring scheme, minus the
/// continuation.
/// <para>
/// <b>Why culling rather than seed-and-flood.</b> The textbook surface-following contour
/// seeds a few surface cells and floods to neighbours that keep changing sign. That is
/// incomplete on its own: a component the seeds miss is silently dropped, and no amount of
/// flooding finds it. Making the seed set complete means proving no surface hides in the
/// unvisited part of the domain — which is exactly a culling argument. Once the cull is in
/// hand it has already produced the visit set, and the flood adds nothing.
/// </para>
/// <para>
/// <b>The completeness argument, in full.</b> An <see cref="Sdf"/> in this engine is
/// 1-Lipschitz: |d(p) − d(q)| ≤ |p − q| (the property <see cref="SdfProjectionTarget"/> and
/// <see cref="NarrowBandSdf"/> already rest on). Take a block whose world box has centre c
/// and half-diagonal R. Every point p in the box satisfies |c − p| ≤ R, so
/// |d(p)| ≥ |d(c)| − |c − p| ≥ |d(c)| − R. If |d(c)| &gt; R then d has no zero anywhere in
/// the box, so no cell of that block has a sign change, so no cell of that block would have
/// produced a vertex or a quad. Blocks that fail the test are visited in full. The test uses
/// ONE evaluation per block and needs no assumption beyond the Lipschitz bound — in
/// particular it does not need d to be a lower bound on the true distance.
/// </para>
/// <para>
/// <see cref="SafetyCells"/> widens the test by one grid cell so that fields which are only
/// <em>approximately</em> Lipschitz — the smooth blends, whose deviation from the true
/// distance is bounded by a fraction of their blend radius — keep a cushion. It is not what
/// makes the argument work; it is what makes the argument survive a blend radius smaller
/// than a cell.
/// </para>
/// </summary>
internal sealed class SurfaceCull
{
    /// <summary>
    /// Cells per block edge. The band of blocks kept alive around the surface is about
    /// (√3/2)·<see cref="BlockCells"/> cells thick, so a smaller block visits fewer cells
    /// but pays more cull evaluations and shorter sample runs; 8 keeps the runs long enough
    /// for the batch kernels while culling 511 of every 512 far-field samples.
    /// </summary>
    public const int BlockCells = 8;

    /// <summary>
    /// Blocks per coarse block edge. The coarse pass throws away the far field for
    /// 1/64th of the fine pass's evaluations; without it a mostly-empty region costs one
    /// evaluation per fine block whether or not anything is near.
    /// </summary>
    private const int CoarseFactor = 4;

    /// <summary>Slack on the cull radius, in grid cells. See the class remarks.</summary>
    private const double SafetyCells = 1.0;

    private readonly bool[] _cellActive;    // [bi, bj, bk] over cell blocks
    private readonly bool[] _sampleActive;  // [bi, bj, bk] over sample blocks (one more per axis)
    private readonly int _cellStrideI, _cellStrideJ;
    private readonly int _sampleStrideI, _sampleStrideJ;
    private readonly int _cellBlocksX, _cellBlocksY;

    /// <summary>Number of cell blocks along z (a cell row's tile count).</summary>
    public int CellBlocksZ { get; }

    /// <summary>Number of sample blocks along y.</summary>
    public int SampleBlocksY { get; }

    /// <summary>Number of sample blocks along z (a sample row's tile count).</summary>
    public int SampleBlocksZ { get; }

    private SurfaceCull(bool[] cellActive, bool[] sampleActive, int nbi, int nbj, int nbk)
    {
        _cellActive = cellActive;
        _sampleActive = sampleActive;
        _cellStrideJ = nbk;
        _cellStrideI = nbj * nbk;
        _sampleStrideJ = nbk + 1;
        _sampleStrideI = (nbj + 1) * (nbk + 1);
        _cellBlocksX = nbi;
        _cellBlocksY = nbj;
        CellBlocksZ = nbk;
        SampleBlocksY = nbj + 1;
        SampleBlocksZ = nbk + 1;
    }

    /// <summary>
    /// The no-op cull: every block active, so the walk visits the whole grid. This is the
    /// pre-cull sampler, kept as a test seam — the culled walk must reproduce it bit for
    /// bit, vertices, faces and ordering alike.
    /// </summary>
    public static SurfaceCull All(in Vector3i cells)
    {
        int nbi = BlockCount(cells.X, BlockCells);
        int nbj = BlockCount(cells.Y, BlockCells);
        int nbk = BlockCount(cells.Z, BlockCells);
        var cellActive = new bool[nbi * nbj * nbk];
        Array.Fill(cellActive, true);
        var sampleActive = new bool[(nbi + 1) * (nbj + 1) * (nbk + 1)];
        Array.Fill(sampleActive, true);
        return new SurfaceCull(cellActive, sampleActive, nbi, nbj, nbk);
    }

    /// <summary>
    /// Active-tile flags for the cells of block row (<paramref name="bi"/>,
    /// <paramref name="bj"/>): entry <c>bk</c> covers cells
    /// [bk·<see cref="BlockCells"/>, (bk+1)·<see cref="BlockCells"/>).
    /// </summary>
    public ReadOnlySpan<bool> CellRow(int bi, int bj) =>
        _cellActive.AsSpan(bi * _cellStrideI + bj * _cellStrideJ, CellBlocksZ);

    /// <summary>
    /// Active-tile flags for the grid SAMPLES of block row (<paramref name="bi"/>,
    /// <paramref name="bj"/>) — the cell mask dilated by one block in the negative
    /// direction on each axis, which is exactly the set of samples that are a corner of
    /// some active cell (and, equally, the set of grid edges that can carry a quad).
    /// </summary>
    public ReadOnlySpan<bool> SampleRow(int bi, int bj) =>
        _sampleActive.AsSpan(bi * _sampleStrideI + bj * _sampleStrideJ, SampleBlocksZ);

    /// <summary>Single-tile probe of the same mask, for the pass that iterates z outermost.</summary>
    public bool SampleActive(int bi, int bj, int bk) =>
        _sampleActive[bi * _sampleStrideI + bj * _sampleStrideJ + bk];

    /// <summary>
    /// Fills <paramref name="tiles"/> (length <see cref="CellBlocksZ"/>) with the cell tiles
    /// that need grid sample row (<paramref name="slab"/>, <paramref name="j"/>) evaluated:
    /// the sample is a corner of cells (slab−1, slab) × (j−1, j) × (k−1, k), so the tiles are
    /// the union over the up-to-four adjacent cell-block rows. The caller then evaluates each
    /// run of set tiles plus ONE extra sample past its end (a cell run [k0, k1) has corners
    /// [k0, k1]).
    /// <para>Doing the x/y dilation here rather than in <see cref="SampleRow"/>'s stored mask
    /// is what keeps the sampled shell tight: a mask dilated a whole block per axis costs a
    /// block of extra samples on three sides of the surface, which at
    /// <see cref="BlockCells"/> = 8 is most of the work the cull just saved.</para>
    /// </summary>
    public void SampleTilesForRow(int slab, int j, Span<bool> tiles)
    {
        tiles.Clear();
        int bi0 = Math.Max(0, (slab - 1) / BlockCells), bi1 = Math.Min(slab / BlockCells, _cellBlocksX - 1);
        int bj0 = Math.Max(0, (j - 1) / BlockCells), bj1 = Math.Min(j / BlockCells, _cellBlocksY - 1);
        for (int bi = bi0; bi <= bi1; bi++)
        {
            for (int bj = bj0; bj <= bj1; bj++)
            {
                var row = CellRow(bi, bj);
                for (int bk = 0; bk < row.Length; bk++)
                {
                    if (row[bk])
                        tiles[bk] = true;
                }
            }
        }
    }

    private static int BlockCount(int cells, int blockCells) => (cells + blockCells - 1) / blockCells;

    public static SurfaceCull Build(
        Sdf sdf, in Vector3d origin, double cell, in Vector3i cells, ProgressCancel? progress)
    {
        double margin = SafetyCells * cell;
        var coarse = Scan(sdf, origin, cell, cells, BlockCells * CoarseFactor, margin, null, 0, progress);
        var fine = Scan(
            sdf, origin, cell, cells, BlockCells, margin, coarse, BlockCells * CoarseFactor, progress);

        int nbi = BlockCount(cells.X, BlockCells);
        int nbj = BlockCount(cells.Y, BlockCells);
        int nbk = BlockCount(cells.Z, BlockCells);
        return new SurfaceCull(fine, DilateToSamples(fine, nbi, nbj, nbk), nbi, nbj, nbk);
    }

    /// <summary>
    /// One cull level: tests the centre of every block that survived <paramref name="parent"/>
    /// (or every block, when there is no parent) and marks the blocks the surface may reach.
    /// Evaluations are batched one block-slab at a time, so peak scratch is O(blocks per
    /// slab) rather than O(blocks) — at resolution 1024 that is 16 384 points instead of
    /// two million.
    /// </summary>
    private static bool[] Scan(
        Sdf sdf, in Vector3d origin, double cell, in Vector3i cells,
        int blockCells, double margin, bool[]? parent, int parentBlockCells, ProgressCancel? progress)
    {
        int nx = cells.X, ny = cells.Y, nz = cells.Z;
        int nbi = BlockCount(nx, blockCells), nbj = BlockCount(ny, blockCells), nbk = BlockCount(nz, blockCells);
        var active = new bool[nbi * nbj * nbk];

        int ratio = parent is null ? 1 : parentBlockCells / blockCells;
        int pbj = parent is null ? 0 : BlockCount(ny, parentBlockCells);
        int pbk = parent is null ? 0 : BlockCount(nz, parentBlockCells);

        var slots = new List<int>();
        var points = new List<Vector3d>();
        var radii = new List<double>();
        var values = new List<double>();

        for (int bi = 0; bi < nbi; bi++)
        {
            progress?.ThrowIfCancelled();
            slots.Clear();
            points.Clear();
            radii.Clear();

            int i0 = bi * blockCells, i1 = Math.Min(i0 + blockCells, nx);
            for (int bj = 0; bj < nbj; bj++)
            {
                for (int bk = 0; bk < nbk; bk++)
                {
                    if (parent is not null &&
                        !parent[(bi / ratio * pbj + bj / ratio) * pbk + bk / ratio])
                        continue;

                    int j0 = bj * blockCells, j1 = Math.Min(j0 + blockCells, ny);
                    int k0 = bk * blockCells, k1 = Math.Min(k0 + blockCells, nz);
                    var min = origin + (i0 * cell, j0 * cell, k0 * cell);
                    var max = origin + (i1 * cell, j1 * cell, k1 * cell);
                    points.Add((min + max) * 0.5);
                    radii.Add((max - min).Length * 0.5);
                    slots.Add((bi * nbj + bj) * nbk + bk);
                }
            }

            if (points.Count == 0)
                continue;
            CollectionsMarshal.SetCount(values, points.Count);
            sdf.Evaluate(CollectionsMarshal.AsSpan(points), CollectionsMarshal.AsSpan(values));
            for (int c = 0; c < slots.Count; c++)
                active[slots[c]] = Math.Abs(values[c]) <= radii[c] + margin;
        }

        return active;
    }

    /// <summary>
    /// Grid sample (i, j, k) is a corner of cells (i−1, i) × (j−1, j) × (k−1, k), whose
    /// blocks are (i/B, i/B − 1) × … — so the sample mask is the cell mask dilated by one
    /// block in the negative direction on each axis, over one extra block per axis.
    /// </summary>
    private static bool[] DilateToSamples(bool[] cellActive, int nbi, int nbj, int nbk)
    {
        int sbj = nbj + 1, sbk = nbk + 1;
        var sample = new bool[(nbi + 1) * sbj * sbk];
        for (int bi = 0; bi < nbi; bi++)
        {
            for (int bj = 0; bj < nbj; bj++)
            {
                for (int bk = 0; bk < nbk; bk++)
                {
                    if (!cellActive[(bi * nbj + bj) * nbk + bk])
                        continue;
                    for (int di = 0; di <= 1; di++)
                    {
                        for (int dj = 0; dj <= 1; dj++)
                        {
                            for (int dk = 0; dk <= 1; dk++)
                                sample[((bi + di) * sbj + bj + dj) * sbk + bk + dk] = true;
                        }
                    }
                }
            }
        }
        return sample;
    }
}
