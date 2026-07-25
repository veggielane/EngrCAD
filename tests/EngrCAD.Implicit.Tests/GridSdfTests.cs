using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Implicit.Tests;

public class GridSdfTests
{
    private const double Precision = 1e-12;

    /// <summary>
    /// Counting test double distinguishing scalar from batch evaluations, to verify the
    /// bakers go through the batch Evaluate seam (and, for the lazy grid, how many
    /// source samples were actually paid for).
    /// </summary>
    private sealed class BatchSpySdf(Sdf inner) : Sdf
    {
        // The dense bake runs batch rows on ParallelFor threads — counters must be
        // interlocked or increments race and undercount (seen: 1254 of 1331).
        private int _scalarCalls;
        private int _batchCalls;
        private int _batchPoints;

        public int ScalarCalls => Volatile.Read(ref _scalarCalls);
        public int BatchCalls => Volatile.Read(ref _batchCalls);
        public int BatchPoints => Volatile.Read(ref _batchPoints);

        public override double Evaluate(in Vector3d point)
        {
            Interlocked.Increment(ref _scalarCalls);
            return inner.Evaluate(point);
        }

        // Counts at EvaluateBatch, the seam EVERY batch goes through — the dense baker
        // hands over interleaved points and the sparse one hands over deinterleaved
        // coordinates, and both land here.
        protected internal override void EvaluateBatch(
            ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
        {
            Interlocked.Increment(ref _batchCalls);
            Interlocked.Add(ref _batchPoints, x.Length);
            inner.EvaluateBatch(x, y, z, distances);
        }

        public override Aabb Bounds => inner.Bounds;
    }

    private static IEnumerable<Vector3d> ProbeGrid(Aabb box, int n)
    {
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                for (int k = 0; k < n; k++)
                    yield return new Vector3d(
                        box.Min.X + box.Size.X * i / (n - 1),
                        box.Min.Y + box.Size.Y * j / (n - 1),
                        box.Min.Z + box.Size.Z * k / (n - 1));
    }

    // ---- fidelity on analytic primitives ----

    [Fact]
    public void Sampled_ReproducesTheSource_AtGridNodes()
    {
        var exact = Sdf.Sphere(1);
        var region = new Aabb((-1.25, -1.25, -1.25), (1.25, 1.25, 1.25));
        const double h = 0.25;
        var baked = exact.Sampled(region, h);

        for (int i = 0; i <= 10; i++)
            for (int j = 0; j <= 10; j++)
                for (int k = 0; k <= 10; k++)
                {
                    var node = region.Min + new Vector3d(i * h, j * h, k * h);
                    Assert.Equal(exact.Evaluate(node), baked.Evaluate(node), Precision);
                }
    }

    [Fact]
    public void Sampled_Sphere_ErrorBoundedByCellSize_AndConvergesQuadratically()
    {
        var exact = Sdf.Sphere(1);
        var region = new Aabb((-1.2, -1.2, -1.2), (1.2, 1.2, 1.2));

        // Trilinear error is bounded by (1/8) h² Σ max|∂²f/∂xi²| over the cell. For the
        // sphere SDF each second derivative is at most 1/|p|, and within the cell |p| can
        // be h√3 closer than the probe, so with probes kept at |p| ≥ rMin the bound is
        //   (3/8) h² / (rMin − h√3).
        double MaxError(double h)
        {
            var baked = exact.Sampled(region, h);
            const double rMin = 0.4;
            double bound = 3.0 / 8.0 * h * h / (rMin - h * Math.Sqrt(3));
            double worst = 0;
            int probes = 0;
            foreach (var p in ProbeGrid(new Aabb((-1.15, -1.15, -1.15), (1.15, 1.15, 1.15)), 17))
            {
                if (p.Length < rMin)
                    continue;
                double error = Math.Abs(baked.Evaluate(p) - exact.Evaluate(p));
                Assert.True(error <= bound, $"error {error} exceeds trilinear bound {bound} at {p} (h = {h})");
                worst = Math.Max(worst, error);
                probes++;
            }
            Assert.True(probes > 4000); // the sweep actually exercised the field
            return worst;
        }

        double coarse = MaxError(0.1);
        double fine = MaxError(0.05);
        Assert.True(fine <= 0.5 * coarse, // O(h²): halving h should roughly quarter the error
            $"halving the cell size only reduced max error from {coarse} to {fine}");
    }

    [Fact]
    public void Sampled_Box_IsExactWhereTheFieldIsPiecewiseLinear()
    {
        // The box SDF is linear throughout any cell that stays inside a single face
        // region, so trilinear interpolation reproduces it exactly there.
        var exact = Sdf.Box(2, 2, 2);
        var baked = exact.Sampled(new Aabb((-1.5, -1.5, -1.5), (1.5, 1.5, 1.5)), 0.25);

        foreach (var p in ProbeGrid(new Aabb((1.05, -0.6, -0.6), (1.45, 0.6, 0.6)), 6))
            Assert.Equal(exact.Evaluate(p), baked.Evaluate(p), Precision);
    }

    // ---- outside-region contract ----

    [Fact]
    public void Sampled_OutsideBakedRegion_StaysPositiveAndAtLeastDistanceToRegion()
    {
        var baked = Sdf.Sphere(1).Sampled(0.1); // bounds + one-cell margin: solid contained
        var region = baked.Bounds;

        foreach (var p in new Vector3d[] { (2, 0, 0), (0, 3, 1), (-5, -5, -5), (0, 0, -1.5) })
        {
            Assert.False(region.Contains(p));
            double d = baked.Evaluate(p);
            Assert.True(d > 0, $"outside point {p} must read positive, got {d}");
            // Boundary samples are ≥ 0 (solid inside the region), so the clamp+distance
            // formula can never under-report the distance to the region itself.
            Assert.True(d >= region.DistanceTo(p) - Precision,
                $"outside value {d} at {p} under-reports region distance {region.DistanceTo(p)}");
        }
    }

    [Fact]
    public void Sampled_IsContinuousAcrossTheRegionBoundary()
    {
        var baked = Sdf.Sphere(1).Sampled(0.1);
        var region = baked.Bounds;
        const double eps = 1e-7;

        var inside = new Vector3d(region.Max.X - eps, 0.3, -0.2);
        var outside = new Vector3d(region.Max.X + eps, 0.3, -0.2);
        Assert.True(Math.Abs(baked.Evaluate(inside) - baked.Evaluate(outside)) < 10 * eps);
    }

    // ---- baking an expensive AST (MeshSdf) ----

    [Fact]
    public void Sampled_MeshSdf_PolygonizedVolume_MatchesTheUnbakedOriginal()
    {
        var mesh = MeshPrimitives.UvSphere(1.0, segments: 32, rings: 16);
        var meshSdf = new MeshSdf(mesh);
        var baked = meshSdf.Sampled(0.05);

        double unbakedVolume = SurfaceNets.Polygonize(meshSdf, resolution: 64).Volume();
        double bakedVolume = SurfaceNets.Polygonize(baked, resolution: 64).Volume();

        Assert.True(Math.Abs(bakedVolume - unbakedVolume) / unbakedVolume < 0.02,
            $"baked volume {bakedVolume} vs unbaked {unbakedVolume}");
        Assert.True(Math.Abs(bakedVolume - mesh.Volume()) / mesh.Volume() < 0.05,
            $"baked volume {bakedVolume} vs source mesh {mesh.Volume()}");
    }

    // ---- baker mechanics ----

    [Fact]
    public void Sampled_BakesThroughTheBatchEvaluateSeam()
    {
        var spy = new BatchSpySdf(Sdf.Sphere(1));
        var region = new Aabb((-1.25, -1.25, -1.25), (1.25, 1.25, 1.25));
        spy.Sampled(region, 0.25); // 10 cells → 11 samples per axis

        Assert.Equal(0, spy.ScalarCalls);
        Assert.True(spy.BatchCalls > 0);
        Assert.Equal(11 * 11 * 11, spy.BatchPoints);
    }

    [Fact]
    public void Sampled_Validation()
    {
        var sphere = Sdf.Sphere(1);
        var region = new Aabb((-2, -2, -2), (2, 2, 2));

        Assert.Throws<ArgumentOutOfRangeException>(() => sphere.Sampled(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => sphere.Sampled(region, -0.1));
        Assert.Throws<ArgumentException>(() => sphere.Sampled(Aabb.Empty, 0.1));
        // Unbounded fields need an explicit region.
        Assert.Throws<InvalidOperationException>(() => Sdf.HalfSpace(Vector3d.UnitZ, 0).Sampled(0.1));
        _ = Sdf.Gyroid(1, 0.2).Sampled(region, 0.1); // explicit region works for them
    }

    // ---- lazy variant ----

    [Fact]
    public void SampledLazy_MatchesTheDenseBake_Everywhere()
    {
        var exact = Sdf.Sphere(1).SmoothUnion(Sdf.Box(1.2, 1.2, 1.2).Translate((0.9, 0, 0)), 0.3);
        var region = new Aabb((-2, -2, -2), (2.6, 2, 2));
        const double h = 0.11;
        var dense = exact.Sampled(region, h);
        var lazy = exact.Sampled(region, h, lazy: true);

        Assert.Equal(dense.Bounds, lazy.Bounds);
        foreach (var p in ProbeGrid(region.Expanded(0.5), 9)) // includes outside-region probes
            Assert.Equal(dense.Evaluate(p), lazy.Evaluate(p), Precision);
    }

    [Fact]
    public void SampledLazy_OnlyBakesTouchedBlocks_AndCachesThem()
    {
        var spy = new BatchSpySdf(Sdf.Sphere(1.8));
        var region = new Aabb((-2, -2, -2), (2, 2, 2));
        var lazy = spy.Sampled(region, 0.1, lazy: true); // 41 samples per axis, 3³ blocks

        Assert.Equal(0, spy.ScalarCalls + spy.BatchPoints); // nothing paid up front

        lazy.Evaluate((-1.9, -1.9, -1.9)); // one corner cell → one 16³ block
        int denseTotal = 41 * 41 * 41;
        Assert.True(spy.BatchPoints > 0);
        Assert.True(spy.BatchPoints < denseTotal,
            $"lazy bake evaluated {spy.BatchPoints} samples, no better than dense {denseTotal}");
        Assert.Equal(0, spy.ScalarCalls); // blocks fill through the batch seam

        int afterFirst = spy.BatchPoints;
        lazy.Evaluate((-1.85, -1.95, -1.88)); // same block: fully cached
        Assert.Equal(afterFirst, spy.BatchPoints);
    }

    // ---- sparse (two-level) block table ----

    /// <summary>
    /// The case that fails outright without a sparse grid: a domain whose dense sample
    /// count overflows int addressing. The dense bake must say so and point at the way
    /// out; the lazy grid must simply work, holding only the blocks a query reaches.
    /// </summary>
    [Fact]
    public void ADomainTooLargeToBakeDensely_StillWorksLazily()
    {
        var region = new Aabb((-1000, -1000, -1000), (1000, 1000, 1000));
        const double cell = 0.4; // 5001³ = 1.25e11 samples — 1 TB of doubles if dense

        var dense = Assert.Throws<ArgumentException>(() => Sdf.Sphere(900).Sampled(region, cell));
        Assert.Contains("lazy", dense.Message);

        var lazy = Sdf.Sphere(900).Sampled(region, cell, lazy: true);
        var grid = Assert.IsType<LazyGridSdf>(lazy);
        Assert.Equal(0, grid.MaterializedBlocks);

        // A handful of probes near the surface, spread far apart so they land in different
        // super-blocks — the index has to be sparse at BOTH levels for this to be cheap.
        foreach (var direction in new Vector3d[]
                 { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
        {
            Assert.Equal(0, lazy.Evaluate(direction * 900), 1);
            Assert.True(lazy.Evaluate(direction * 500) < 0);
            Assert.True(lazy.Evaluate(direction * 990) > 0);
        }

        // A trilinear read touches a cell's 8 corners, which can straddle two blocks per
        // axis, so 18 probes can reach at most 8 blocks each. Even that ceiling is 1.2 MB
        // of samples — where a FLAT block table for this grid would have cost 313³ = 30.7 M
        // pointers (245 MB) before a single sample was evaluated.
        Assert.InRange(grid.MaterializedBlocks, 1, 8 * 18);
        Assert.InRange(grid.MaterializedBytes, 1, 8L * 18 * 4096 * sizeof(double));
    }

    /// <summary>
    /// Values must not depend on the block table's shape. The table is flat while that is
    /// free and grouped into super-blocks above a threshold; forcing the grouped path onto
    /// a small grid (flatBlockLimit 0) lets both be compared against the dense bake to the
    /// bit — the grouped path is otherwise only reachable on grids too big to bake.
    /// </summary>
    [Fact]
    public void FlatAndGroupedBlockTables_AgreeWithTheDenseBake_BitForBit()
    {
        var field = Sdf.Torus(1, 0.35) | Sdf.Sphere(0.5).Translate((0.9, 0, 0.4));
        var region = new Aabb((-1.7, -1.7, -0.9), (1.7, 1.7, 0.9));
        const double h = 0.017; // 201x201x107 samples = 13x13x7 blocks

        var dense = field.Sampled(region, h);
        var flat = new LazyGridSdf(field, region, h, flatBlockLimit: int.MaxValue);
        var grouped = new LazyGridSdf(field, region, h, flatBlockLimit: 0);
        foreach (var p in ProbeGrid(region.Expanded(0.3), 13))
        {
            long expected = BitConverter.DoubleToInt64Bits(dense.Evaluate(p));
            Assert.Equal(expected, BitConverter.DoubleToInt64Bits(flat.Evaluate(p)));
            Assert.Equal(expected, BitConverter.DoubleToInt64Bits(grouped.Evaluate(p)));
        }
        Assert.Equal(flat.MaterializedBlocks, grouped.MaterializedBlocks);
    }

    /// <summary>Concurrent first touches of the same and of different blocks must publish
    /// once and agree — the two-level table is lock-free at both levels.</summary>
    [Fact]
    public void ConcurrentEvaluation_IsConsistent()
    {
        var field = Sdf.Sphere(1.5);
        var region = new Aabb((-2, -2, -2), (2, 2, 2));
        var lazy = field.Sampled(region, 0.02, lazy: true); // 201³ samples, 13³ blocks
        var probes = new List<Vector3d>();
        for (int i = 0; i < 4096; i++)
        {
            double a = i * 0.0123, b = i * 0.0456;
            probes.Add(new Vector3d(1.9 * Math.Cos(a) * Math.Sin(b), 1.9 * Math.Sin(a) * Math.Sin(b), 1.9 * Math.Cos(b)));
        }

        var sequential = probes.Select(p => lazy.Evaluate(p)).ToArray();
        var fresh = field.Sampled(region, 0.02, lazy: true);
        var parallel = new double[probes.Count];
        Parallel.For(0, probes.Count, i => parallel[i] = fresh.Evaluate(probes[i]));

        for (int i = 0; i < probes.Count; i++)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(sequential[i]), BitConverter.DoubleToInt64Bits(parallel[i]));
        }
        Assert.Equal(
            ((LazyGridSdf)lazy).MaterializedBlocks, ((LazyGridSdf)fresh).MaterializedBlocks);
    }
}
