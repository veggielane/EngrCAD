using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// <see cref="SketchRegion"/> flattens its segments into structure-of-arrays form, runs
/// lane-wise kernels for lines and full circles, rejects far segments by bounding box, and
/// indexes the ray-parity pieces by y. Every one of those is meant to be a pure
/// restructuring — the field's magnitude is exact and its even–odd sign drives boolean
/// classification, so "close enough" is not a thing here. These tests hold the line:
/// golden bit-hashes against the plain segment loop that preceded the restructuring, and
/// bit equality between the batch and scalar entry points on both the region and the
/// extruded/revolved SDF nodes built on it.
/// </summary>
public class SketchRegionKernelTests
{
    private static Sketch Plate() => Sketch.Rectangle(40, 24)
        .WithHole(Sketch.Circle(new Vector2d(-12, 0), 4))
        .WithHole(Sketch.Circle(new Vector2d(12, 0), 4))
        .WithHole(Sketch.RoundedRectangle(10, 6, 1.5));

    private static Sketch Curvy() => Sketch.Start(0, 0)
        .LineTo(10, 0)
        .BezierTo(new Vector2d(14, 3), new Vector2d(14, 9), new Vector2d(10, 12))
        .ArcTo(new Vector2d(2, 12), 5, clockwise: false)
        .QuadraticTo(new Vector2d(-3, 6), new Vector2d(0, 0))
        .Close();

    private static Sketch Named(string name) => name switch
    {
        "plate" => Plate(),
        "curvy" => Curvy(),
        "slot" => Sketch.Slot(20, 8),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>
    /// A grid straddling the sketch (so vertices, tangencies and exterior points all
    /// appear) plus 20 000 seeded random points, hashed on the exact bits of every result.
    /// </summary>
    private static long Fingerprint(SketchRegion region, in Aabb bounds)
    {
        unchecked
        {
            long hash = (long)14695981039346656037UL;
            void Mix(double value)
            {
                hash ^= BitConverter.DoubleToInt64Bits(value);
                hash *= 1099511628211L;
            }

            double x0 = bounds.Min.X - 3, x1 = bounds.Max.X + 3;
            double y0 = bounds.Min.Y - 3, y1 = bounds.Max.Y + 3;
            const int n = 61;
            for (int i = 0; i <= n; i++)
                for (int j = 0; j <= n; j++)
                    Mix(region.SignedDistance(new Vector2d(x0 + (x1 - x0) * i / n, y0 + (y1 - y0) * j / n)));

            var random = new Random(20260725);
            for (int i = 0; i < 20000; i++)
                Mix(region.SignedDistance(new Vector2d(
                    x0 + (x1 - x0) * random.NextDouble(), y0 + (y1 - y0) * random.NextDouble())));
            return hash;
        }
    }

    [Theory]
    [InlineData("plate", -20992552982407019L)]
    [InlineData("curvy", -1804961155336630648L)]
    [InlineData("slot", 9097049425223680080L)]
    public void SignedDistance_MatchesTheGoldenBitPattern(string name, long fingerprint)
    {
        var sketch = Named(name);
        Assert.Equal(fingerprint, Fingerprint(new SketchRegion(sketch), sketch.Bounds));
    }

    private static (double[] X, double[] Y) SamplePoints(in Aabb bounds, int count, int seed)
    {
        var random = new Random(seed);
        var x = new double[count];
        var y = new double[count];
        for (int i = 0; i < count; i++)
        {
            x[i] = bounds.Min.X - 3 + (bounds.Size.X + 6) * random.NextDouble();
            y[i] = bounds.Min.Y - 3 + (bounds.Size.Y + 6) * random.NextDouble();
        }
        return (x, y);
    }

    /// <summary>
    /// Batch and scalar must agree to the bit, at every length around the SIMD register
    /// width and the batch chunk boundary (the ragged tail is where a lane-wise kernel and
    /// its scalar fallback would diverge if they disagreed).
    /// </summary>
    [Theory]
    [InlineData("plate")]
    [InlineData("curvy")]
    [InlineData("slot")]
    public void BatchSignedDistance_IsBitIdenticalToScalar(string name)
    {
        var sketch = Named(name);
        var region = new SketchRegion(sketch);
        foreach (int count in new[] { 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 1023, 1024, 1025, 2049 })
        {
            var (x, y) = SamplePoints(sketch.Bounds, count, 4242 + count);
            var batched = new double[count];
            region.SignedDistance(x, y, batched);
            for (int i = 0; i < count; i++)
            {
                double expected = region.SignedDistance(new Vector2d(x[i], y[i]));
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(batched[i]));
            }
        }
    }

    /// <summary>
    /// Points that land exactly on the boundary, on shared vertices and on the ray-parity
    /// piece breaks are where the half-open endpoint rule and the bounding-box reject earn
    /// their keep — an index or a reject that lost a segment would show up as a sign flip
    /// or a nonzero distance here.
    /// </summary>
    [Fact]
    public void OnBoundaryPoints_AreExactlyZeroAndBatchAgrees()
    {
        var sketch = Plate();
        var region = new SketchRegion(sketch);
        // Rectangle corners and edge midpoints, plus points on both bores' circles.
        var probes = new List<Vector2d>
        {
            new(-20, -12), new(20, -12), new(20, 12), new(-20, 12),
            new(0, -12), new(20, 0), new(0, 12), new(-20, 0),
        };
        for (int i = 0; i < 16; i++)
        {
            double a = 2 * Math.PI * i / 16;
            probes.Add(new Vector2d(-12 + 4 * Math.Cos(a), 4 * Math.Sin(a)));
            probes.Add(new Vector2d(12 + 4 * Math.Cos(a), 4 * Math.Sin(a)));
        }

        var x = probes.Select(p => p.X).ToArray();
        var y = probes.Select(p => p.Y).ToArray();
        var batched = new double[probes.Count];
        region.SignedDistance(x, y, batched);

        for (int i = 0; i < probes.Count; i++)
        {
            // Boundary points sit at distance zero up to the arithmetic that placed them.
            Assert.Equal(0, Math.Abs(region.SignedDistance(probes[i])), 12);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(region.SignedDistance(probes[i])),
                BitConverter.DoubleToInt64Bits(batched[i]));
        }
    }

    /// <summary>
    /// The extruded node answers the region once per distinct (x, y) instead of once per
    /// sample. That memoization must be invisible: identical results whether the batch is
    /// long constant-xy runs (how the polygonizer samples), all-distinct points, or a
    /// single point.
    /// </summary>
    [Theory]
    [InlineData("plate")]
    [InlineData("curvy")]
    public void ExtrudedRegionBatch_IsBitIdenticalToScalar(string name)
    {
        var sketch = Named(name);
        var field = Sdf.ExtrudedRegion(new SketchRegion(sketch), 6);
        AssertBatchMatchesScalar(field, sketch.Bounds, runLength: 37);
        AssertBatchMatchesScalar(field, sketch.Bounds, runLength: 1);
    }

    [Fact]
    public void RevolvedRegionBatch_IsBitIdenticalToScalar()
    {
        var profile = Sketch.Start(0, 0)
            .LineTo(8, 0)
            .BezierTo(new Vector2d(10, 4), new Vector2d(5, 8), new Vector2d(6, 14))
            .LineTo(0, 14)
            .Close();
        var field = Sdf.RevolvedRegion(new SketchRegion(profile, forRevolution: true));
        AssertBatchMatchesScalar(field, profile.Bounds, runLength: 23);
    }

    /// <summary>An <see cref="IPlanarRegion"/> that does NOT override the batch method must
    /// still work — the interface's default implementation is part of the contract.</summary>
    [Fact]
    public void ADefaultBatchRegion_StillLowersToACorrectPrism()
    {
        var field = Sdf.ExtrudedRegion(new UnitDisk(), 4);
        AssertBatchMatchesScalar(field, new Aabb((-1, -1, 0), (1, 1, 0)), runLength: 11);
        Assert.Equal(-0.5, field.Evaluate(new Vector3d(0.5, 0, 2)), 12);
    }

    private static void AssertBatchMatchesScalar(Sdf field, in Aabb planarBounds, int runLength)
    {
        var random = new Random(90210 + runLength);
        const int count = 2100;
        var x = new double[count];
        var y = new double[count];
        var z = new double[count];
        double px = 0, py = 0;
        for (int i = 0; i < count; i++)
        {
            if (i % runLength == 0)
            {
                px = planarBounds.Min.X - 2 + (planarBounds.Size.X + 4) * random.NextDouble();
                py = planarBounds.Min.Y - 2 + (planarBounds.Size.Y + 4) * random.NextDouble();
            }
            x[i] = px;
            y[i] = py;
            z[i] = -4 + 16 * random.NextDouble();
        }

        var batched = new double[count];
        var points = new Vector3d[count];
        for (int i = 0; i < count; i++)
            points[i] = new Vector3d(x[i], y[i], z[i]);
        field.Evaluate(points, batched);

        for (int i = 0; i < count; i++)
        {
            double expected = field.Evaluate(points[i]);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(batched[i]));
        }
    }

    private sealed class UnitDisk : IPlanarRegion
    {
        public double SignedDistance(in Vector2d point) => point.Length - 1;

        public Aabb Bounds => new((-1, -1, 0), (1, 1, 0));
    }
}
