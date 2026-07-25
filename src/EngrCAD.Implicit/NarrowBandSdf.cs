using System.Buffers;
using EngrCAD.Core;

namespace EngrCAD.Implicit;

// Narrow-band sampled grid: evaluate the source exactly only near its surface, then fill
// the rest of the grid by a distance transform.
//
// geometry3Sharp counterpart: MeshSignedDistanceGrid ("compute exact distances in a
// narrow band, then extend out to the rest of the grid using fast sweeping"), itself a
// port of Batty's SDFGen. Deliberate differences:
//   - g3's version is mesh-specific: it rasterizes triangle bounding boxes to find the
//     band, propagates the closest-triangle index while sweeping, and signs the grid by
//     ray-crossing parity. Ours takes ANY Sdf, so the band is found by hierarchical
//     culling against the source's own conservative distance and the sign comes from the
//     source (which is sign-exact by contract) — no ray casting, and it accelerates every
//     expensive field, MeshSdf included (which is the case the backlog item cared about).
//   - the outward fill is a two-scan chamfer transform rather than eight sweeps: with the
//     full 26-neighbour mask, one causal scan plus one anti-causal scan is provably the
//     complete chamfer distance, so there is nothing to iterate to convergence.

/// <summary>
/// A sampled grid that pays for source evaluations only near the surface: samples within
/// <c>bandWidth</c> of the zero level set hold the source's exact value, and everything
/// beyond is filled by a chamfer distance transform seeded from the band, signed by the
/// side of the surface it sits on.
/// <para>
/// <b>How the band is found.</b> The grid is classified by recursive octant subdivision
/// (blocks of 8³ samples at the leaves): each node is probed at its centre, and if
/// <c>|d(centre)| − circumradius &gt; bandWidth</c> the whole node is provably clear of
/// the band — the true distance is 1-Lipschitz, so no point of the node can be within
/// <c>bandWidth</c> of the surface — and its samples are recorded with the sign of
/// <c>d(centre)</c> and left for the transform. Nodes that fail the test recurse, and
/// leaves are evaluated in full through the batch (SIMD) seam. Cost is therefore
/// proportional to the surface's area, not the region's volume: one probe per octree node
/// plus the samples of the straddling leaves.
/// </para>
/// <para>
/// <b>Distance fidelity — where this stops being exact.</b> Inside the band the values are
/// the source's own, so the reconstruction is exactly a dense <see cref="Sdf.Sampled(double,
/// bool)"/> bake there (exact at samples, trilinear between); since the zero level set lies
/// inside the band, polygonization and inside/outside classification see full-quality
/// values. Outside the band the <em>sign</em> is exact at every grid sample (the culling
/// test proves the node does not straddle the surface) but the <em>magnitude</em> is a
/// chamfer approximation. It is an <em>over</em>-estimate of the true distance — each far
/// sample takes min(exact band value + chamfer path) and the true distance is 1-Lipschitz,
/// so the triangle inequality bounds it from below — by up to about 13%: that is
/// Borgefors' anisotropy bound for the 26-neighbour chamfer metric with true Euclidean
/// step lengths ⟨1, √2, √3⟩ (measured worst case on a sphere: 1.112×). It stays
/// continuous, monotone and correctly signed, which is what meshing and inside/outside
/// tests need; do not use a narrow-band grid as a sphere-tracing lower bound, or offset it
/// by more than the band width. (Borgefors-optimized weights would cut the error to ~1.4%
/// but would forfeit the provable over-estimate — the invariant is worth more here than
/// the last decimal of a far-field value nobody reads.)
/// </para>
/// <para>
/// <b>When this is worth it — and when it is not.</b> The saving is in <em>source
/// evaluations</em>; the outward fill still touches every sample twice, at roughly 60 ns
/// per sample (26 candidate reads, and raster scans do not parallelize). So a narrow band
/// wins exactly when a source evaluation costs well more than that, and loses when it does
/// not. Measured against a dense <see cref="Sdf.Sampled(in Aabb, double, bool)"/> bake of
/// the same grid: a <c>MeshSdf</c> (a BVH walk per query, ~1 µs) bakes <b>8.4× faster</b>
/// at a 0.35 cell and <b>11.1× faster</b> at 0.18 — the ratio improves as the grid gets
/// finer, because the shell's thickness scales with the cell size while the region does
/// not. A CSG tree of analytic primitives goes the other way: it is 3–10× <em>slower</em>
/// than a dense bake, because the dense bake is parallel and costs ~14 ns per sample.
/// Reach for this for mesh-backed and otherwise expensive fields, not for cheap ones.
/// </para>
/// <para>
/// <b>Precondition.</b> The culling test needs the source's magnitude to be a lower bound
/// on the true distance to the surface — the engine's field contract (exact for
/// primitives and rigid/uniform transforms, a lower bound for CSG and blend compositions).
/// A source that <em>over</em>-estimates its distance somewhere (documented cases: deep
/// inside <see cref="Sdf.Thread"/>, and any already-grid-sampled source) can have its band
/// come out narrower than requested there; the sign field is unaffected.
/// </para>
/// </summary>
internal sealed class NarrowBandSdf : SampledGridSdf
{
    /// <summary>
    /// Samples per axis in a classification leaf. This is THE tuning knob: a surviving
    /// leaf is evaluated in full, so the exactly-evaluated shell is
    /// 2·(bandWidth + leaf circumradius) thick, and the circumradius is √3·(n−1)/2 cells —
    /// 6.1 cells at n = 8 but only 2.6 at n = 4, which nearly halves the shell. Four keeps
    /// the leaf a 64-point batch, still comfortably above the SIMD register width.
    /// </summary>
    private const int LeafSamples = 4;

    private readonly double[] _values; // x-fastest: [(k * Ny + j) * Nx + i]

    private NarrowBandSdf(in GridFrame frame, double[] values, int exactSamples, int probes)
        : base(frame)
    {
        _values = values;
        ExactSamples = exactSamples;
        Probes = probes;
    }

    private protected override double Sample(int i, int j, int k) =>
        _values[(k * Frame.Ny + j) * Frame.Nx + i];

    /// <summary>Grid samples whose value came from a source evaluation; the rest were
    /// swept. Diagnostic — the ratio to the total sample count is the point of the node.</summary>
    public int ExactSamples { get; }

    /// <summary>Extra source evaluations spent probing octree node centres.</summary>
    public int Probes { get; }

    public static NarrowBandSdf Bake(Sdf source, in Aabb region, double cellSize, double bandWidth)
    {
        if (!(bandWidth >= 0))
            throw new ArgumentOutOfRangeException(nameof(bandWidth), "Band width must be non-negative.");

        var frame = new GridFrame(region, cellSize);
        frame.RequireDenseAddressable();
        int total = frame.Nx * frame.Ny * frame.Nz;
        var magnitudes = new double[total];
        var signs = new sbyte[total];
        magnitudes.AsSpan().Fill(double.PositiveInfinity); // "not yet known" for the transform

        var leaves = new List<Block>();
        int probes = Classify(source, frame, bandWidth, signs, leaves, 0, 0, 0, RootSpan(frame));

        // The whole region can be clear of the surface — nothing to be exact about, and no
        // seed for the transform. Then the grid is simply a dense bake.
        if (leaves.Count == 0)
            AddEveryLeaf(frame, leaves);

        int exact = 0;
        foreach (var leaf in leaves)
            exact += (leaf.I1 - leaf.I0) * (leaf.J1 - leaf.J0) * (leaf.K1 - leaf.K0);

        ParallelFor.Blocks(0, leaves.Count, (from, to) =>
        {
            for (int n = from; n < to; n++)
                EvaluateLeaf(source, frame, leaves[n], magnitudes, signs);
        });

        Chamfer(magnitudes, frame);

        for (int c = 0; c < total; c++)
            magnitudes[c] *= signs[c];

        return new NarrowBandSdf(frame, magnitudes, exact, probes);
    }

    private static int RootSpan(in GridFrame frame)
    {
        int span = 1;
        int longest = Math.Max(frame.Nx, Math.Max(frame.Ny, frame.Nz));
        while (span < longest)
            span *= 2;
        return span;
    }

    /// <summary>A half-open sample box, already clipped to the grid.</summary>
    private readonly record struct Block(int I0, int J0, int K0, int I1, int J1, int K1);

    private static void AddEveryLeaf(in GridFrame frame, List<Block> leaves)
    {
        for (int k = 0; k < frame.Nz; k += LeafSamples)
            for (int j = 0; j < frame.Ny; j += LeafSamples)
                for (int i = 0; i < frame.Nx; i += LeafSamples)
                    leaves.Add(new Block(i, j, k,
                        Math.Min(i + LeafSamples, frame.Nx),
                        Math.Min(j + LeafSamples, frame.Ny),
                        Math.Min(k + LeafSamples, frame.Nz)));
    }

    /// <summary>
    /// Recursive octant culling; returns the number of centre probes spent. Leaves that
    /// may reach the band are appended to <paramref name="leaves"/> for batch evaluation;
    /// culled nodes get their sign stamped and their magnitudes left to the transform.
    /// </summary>
    private static int Classify(
        Sdf source, in GridFrame frame, double bandWidth, sbyte[] signs, List<Block> leaves,
        int i0, int j0, int k0, int span)
    {
        int i1 = Math.Min(i0 + span, frame.Nx);
        int j1 = Math.Min(j0 + span, frame.Ny);
        int k1 = Math.Min(k0 + span, frame.Nz);
        if (i0 >= i1 || j0 >= j1 || k0 >= k1)
            return 0;

        var min = frame.SamplePosition(i0, j0, k0);
        var max = frame.SamplePosition(i1 - 1, j1 - 1, k1 - 1);
        double d = source.Evaluate((min + max) * 0.5);
        double circumradius = (max - min).Length * 0.5;

        if (Math.Abs(d) - circumradius > bandWidth)
        {
            StampSign(signs, frame, i0, i1, j0, j1, k0, k1, d < 0 ? (sbyte)-1 : (sbyte)1);
            return 1;
        }

        if (span <= LeafSamples)
        {
            leaves.Add(new Block(i0, j0, k0, i1, j1, k1));
            return 1;
        }

        int probes = 1;
        int half = span / 2;
        for (int oi = 0; oi < 2; oi++)
            for (int oj = 0; oj < 2; oj++)
                for (int ok = 0; ok < 2; ok++)
                    probes += Classify(source, frame, bandWidth, signs, leaves,
                        i0 + oi * half, j0 + oj * half, k0 + ok * half, half);
        return probes;
    }

    private static void StampSign(
        sbyte[] signs, in GridFrame frame, int i0, int i1, int j0, int j1, int k0, int k1, sbyte sign)
    {
        for (int k = k0; k < k1; k++)
            for (int j = j0; j < j1; j++)
                signs.AsSpan((k * frame.Ny + j) * frame.Nx + i0, i1 - i0).Fill(sign);
    }

    /// <summary>Evaluates a whole leaf block in one batch call (the SIMD seam).</summary>
    private static void EvaluateLeaf(
        Sdf source, in GridFrame frame, Block leaf, double[] magnitudes, sbyte[] signs)
    {
        int sx = leaf.I1 - leaf.I0, sy = leaf.J1 - leaf.J0, sz = leaf.K1 - leaf.K0;
        int count = sx * sy * sz;
        var points = ArrayPool<Vector3d>.Shared.Rent(count);
        var values = ArrayPool<double>.Shared.Rent(count);
        try
        {
            int n = 0;
            for (int k = leaf.K0; k < leaf.K1; k++)
                for (int j = leaf.J0; j < leaf.J1; j++)
                    for (int i = leaf.I0; i < leaf.I1; i++)
                        points[n++] = frame.SamplePosition(i, j, k);
            source.Evaluate(points.AsSpan(0, count), values.AsSpan(0, count));

            n = 0;
            for (int k = leaf.K0; k < leaf.K1; k++)
                for (int j = leaf.J0; j < leaf.J1; j++)
                {
                    int row = (k * frame.Ny + j) * frame.Nx + leaf.I0;
                    for (int i = 0; i < sx; i++, n++)
                    {
                        double v = values[n];
                        magnitudes[row + i] = Math.Abs(v);
                        signs[row + i] = v < 0 ? (sbyte)-1 : (sbyte)1;
                    }
                }
        }
        finally
        {
            ArrayPool<Vector3d>.Shared.Return(points);
            ArrayPool<double>.Shared.Return(values);
        }
    }

    // The 26-neighbour chamfer mask, split into its causal half (offsets a k-ascending /
    // j-ascending / i-ascending raster has already visited) and — by negation — its
    // anti-causal half. One scan in each direction with the matching half is the COMPLETE
    // chamfer distance transform; unlike eikonal fast sweeping there is nothing to iterate.
    private static readonly (int I, int J, int K)[] CausalMask = BuildCausalMask();

    private static (int, int, int)[] BuildCausalMask()
    {
        var mask = new List<(int, int, int)>(13);
        for (int k = -1; k <= 1; k++)
            for (int j = -1; j <= 1; j++)
                for (int i = -1; i <= 1; i++)
                    if (k < 0 || (k == 0 && j < 0) || (k == 0 && j == 0 && i < 0))
                        mask.Add((i, j, k));
        return [.. mask];
    }

    private static void Chamfer(double[] magnitude, in GridFrame frame)
    {
        int nx = frame.Nx, ny = frame.Ny, nz = frame.Nz;
        int masks = CausalMask.Length;
        var di = new int[masks];
        var dj = new int[masks];
        var dk = new int[masks];
        var offset = new int[masks];
        var weight = new double[masks];
        for (int m = 0; m < masks; m++)
        {
            (di[m], dj[m], dk[m]) = CausalMask[m];
            offset[m] = (dk[m] * ny + dj[m]) * nx + di[m];
            weight[m] = Math.Sqrt(di[m] * di[m] + dj[m] * dj[m] + dk[m] * dk[m]) * frame.CellSize;
        }

        // The causal mask spans dk ∈ {−1, 0}, dj/di ∈ {−1, 0, +1}, so a row is fully
        // in-range when k ≥ 1 and 1 ≤ j ≤ ny−2, for i ∈ [1, nx−2]. Splitting that interior
        // run out of the checked path is what makes the transform affordable: it is 26
        // candidate reads per sample over the whole grid, and the bounds tests alone cost
        // more than the min they guard.
        for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
            {
                int row = (k * ny + j) * nx;
                bool fastRow = k >= 1 && j >= 1 && j <= ny - 2;
                int lo = fastRow ? 1 : nx;
                int hi = fastRow ? nx - 1 : nx;
                for (int i = 0; i < Math.Min(lo, nx); i++)
                    RelaxChecked(magnitude, di, dj, dk, offset, weight, row + i, i, j, k, nx, ny, nz, +1);
                for (int i = lo; i < hi; i++)
                    RelaxFast(magnitude, offset, weight, row + i, +1);
                for (int i = hi; i < nx; i++)
                    RelaxChecked(magnitude, di, dj, dk, offset, weight, row + i, i, j, k, nx, ny, nz, +1);
            }

        for (int k = nz - 1; k >= 0; k--)
            for (int j = ny - 1; j >= 0; j--)
            {
                int row = (k * ny + j) * nx;
                bool fastRow = k <= nz - 2 && j >= 1 && j <= ny - 2;
                int lo = fastRow ? 1 : 0;
                int hi = fastRow ? nx - 1 : 0;
                for (int i = nx - 1; i >= hi; i--)
                    RelaxChecked(magnitude, di, dj, dk, offset, weight, row + i, i, j, k, nx, ny, nz, -1);
                for (int i = hi - 1; i >= lo; i--)
                    RelaxFast(magnitude, offset, weight, row + i, -1);
                for (int i = lo - 1; i >= 0; i--)
                    RelaxChecked(magnitude, di, dj, dk, offset, weight, row + i, i, j, k, nx, ny, nz, -1);
            }
    }

    private static void RelaxFast(
        double[] magnitude, int[] offset, double[] weight, int index, int direction)
    {
        double best = magnitude[index];
        for (int m = 0; m < offset.Length; m++)
        {
            double candidate = magnitude[index + direction * offset[m]] + weight[m];
            if (candidate < best)
                best = candidate;
        }
        magnitude[index] = best;
    }

    private static void RelaxChecked(
        double[] magnitude, int[] di, int[] dj, int[] dk, int[] offset, double[] weight,
        int index, int i, int j, int k, int nx, int ny, int nz, int direction)
    {
        double best = magnitude[index];
        for (int m = 0; m < offset.Length; m++)
        {
            if ((uint)(i + direction * di[m]) >= (uint)nx ||
                (uint)(j + direction * dj[m]) >= (uint)ny ||
                (uint)(k + direction * dk[m]) >= (uint)nz)
                continue;
            double candidate = magnitude[index + direction * offset[m]] + weight[m];
            if (candidate < best)
                best = candidate;
        }
        magnitude[index] = best;
    }
}
