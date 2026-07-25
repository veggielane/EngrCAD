using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Implicit.Tests;

/// <summary>
/// The narrow-band grid's contract: exact where it claims to be (inside the band, i.e.
/// everywhere the zero level set lives), correctly signed everywhere, an honest
/// over-estimate outside the band, and materially cheaper than a dense bake.
/// </summary>
public class NarrowBandSdfTests
{
    private const double Precision = 1e-12;

    /// <summary>Walks the grid sample positions of a baked region.</summary>
    private static IEnumerable<Vector3d> Samples(Aabb region, double cellSize)
    {
        int nx = (int)Math.Round(region.Size.X / cellSize);
        int ny = (int)Math.Round(region.Size.Y / cellSize);
        int nz = (int)Math.Round(region.Size.Z / cellSize);
        for (int k = 0; k <= nz; k++)
            for (int j = 0; j <= ny; j++)
                for (int i = 0; i <= nx; i++)
                    yield return region.Min + new Vector3d(i * cellSize, j * cellSize, k * cellSize);
    }

    // ---- exactness inside the band ----

    [Fact]
    public void NarrowBand_ReproducesADenseBake_InsideTheBand()
    {
        var source = Sdf.Sphere(10);
        var region = source.Bounds.Expanded(2);
        const double cell = 0.25;
        const double band = 1.0;

        var dense = source.Sampled(region, cell);
        var narrow = source.NarrowBand(region, cell, band);

        int checkedSamples = 0;
        foreach (var p in Samples(region, cell))
        {
            // Sphere distance is exact, so |source| IS the true distance to the surface.
            if (Math.Abs(source.Evaluate(p)) > band)
                continue;
            Assert.Equal(dense.Evaluate(p), narrow.Evaluate(p), Precision);
            checkedSamples++;
        }
        Assert.True(checkedSamples > 10_000, $"only {checkedSamples} band samples checked");
    }

    [Fact]
    public void NarrowBand_ReproducesADenseBake_ForACsgTree_InsideTheBand()
    {
        // Difference and union make the field a lower bound rather than exact — the
        // culling precondition — so this is the case that must not silently misclassify.
        var source = (Sdf.Box(12, 8, 6) | Sdf.Sphere(5).Translate((6, 0, 0)))
            - Sdf.Cylinder(2, 20);
        var region = source.Bounds.Expanded(2);
        const double cell = 0.2;
        const double band = 0.8;

        var dense = source.Sampled(region, cell);
        var narrow = source.NarrowBand(region, cell, band);

        foreach (var p in Samples(region, cell))
        {
            if (Math.Abs(source.Evaluate(p)) > band)
                continue;
            Assert.Equal(dense.Evaluate(p), narrow.Evaluate(p), Precision);
        }
    }

    // ---- sign everywhere ----

    [Fact]
    public void NarrowBand_SignIsExact_AtEverySample_IncludingTheSweptRegion()
    {
        var source = (Sdf.Box(10, 6, 4) - Sdf.Cylinder(1.5, 20)) | Sdf.Torus(6, 1);
        var region = source.Bounds.Expanded(3);
        const double cell = 0.25;

        var narrow = source.NarrowBand(region, cell, 0.5);

        foreach (var p in Samples(region, cell))
        {
            double expected = source.Evaluate(p);
            double actual = narrow.Evaluate(p);
            if (Math.Abs(expected) < 1e-12)
                continue; // exactly on the surface: either sign is defensible
            Assert.True(Math.Sign(expected) == Math.Sign(actual),
                $"sign flipped at {p}: source {expected:R}, narrow band {actual:R}");
        }
    }

    // ---- the swept region's honesty ----

    [Fact]
    public void NarrowBand_OverEstimatesTheTrueDistance_OutsideTheBand()
    {
        // A sphere's distance is analytic everywhere, so the swept values can be measured
        // against ground truth rather than against another approximation.
        const double radius = 8;
        const double cell = 0.25;
        const double band = 0.5;
        var source = Sdf.Sphere(radius);
        var region = source.Bounds.Expanded(8);
        var narrow = source.NarrowBand(region, cell, band);

        double worstRatio = 1;
        int checkedSamples = 0;
        foreach (var p in Samples(region, cell))
        {
            double truth = p.Length - radius;
            if (Math.Abs(truth) <= band + 4 * cell)
                continue;
            double actual = narrow.Evaluate(p);

            // Never short: each swept sample is min(exact band value + chamfer path), and
            // the true distance is 1-Lipschitz, so the triangle inequality bounds it below.
            Assert.True(Math.Abs(actual) >= Math.Abs(truth) - 1e-9,
                $"under-estimate at {p}: |{actual:R}| < |{truth:R}|");
            worstRatio = Math.Max(worstRatio, Math.Abs(actual) / Math.Abs(truth));
            checkedSamples++;
        }
        Assert.True(checkedSamples > 1000);
        // Borgefors' anisotropy bound for the 26-neighbour chamfer metric with true
        // Euclidean step lengths is ~13%; the measured worst case here is 1.112x.
        Assert.True(worstRatio < 1.14, $"chamfer over-estimate reached {worstRatio:F4}x");
        Assert.True(worstRatio > 1.0, "the chamfer metric should be measurably anisotropic");
    }

    // ---- the point of the exercise ----

    [Fact]
    public void NarrowBand_EvaluatesFarFewerSamplesThanADenseBake()
    {
        var source = Sdf.Sphere(10);
        var region = source.Bounds.Expanded(4);
        const double cell = 0.15;

        var narrow = (NarrowBandSdf)source.NarrowBand(region, cell, 2 * cell);
        var frame = narrow.Bounds;
        long total = (long)(Math.Round(frame.Size.X / cell) + 1)
                   * (long)(Math.Round(frame.Size.Y / cell) + 1)
                   * (long)(Math.Round(frame.Size.Z / cell) + 1);

        long spent = narrow.ExactSamples + narrow.Probes;
        Assert.True(spent < total / 3,
            $"narrow band spent {spent} evaluations of {total} samples (expected well under a third)");
        // Probing the octree is meant to be noise next to the leaves it saves.
        Assert.True(narrow.Probes < narrow.ExactSamples / 4,
            $"{narrow.Probes} probes against {narrow.ExactSamples} exact samples");
    }

    // ---- end to end ----

    [Fact]
    public void NarrowBand_PolygonizesToTheAnalyticVolume()
    {
        const double radius = 10;
        var source = Sdf.Sphere(radius);
        const double cell = 0.25;
        var narrow = source.NarrowBand(cell, 2 * cell);

        var mesh = SurfaceNets.Polygonize(narrow, narrow.Bounds, 96);
        double expected = 4.0 / 3.0 * Math.PI * radius * radius * radius;
        Assert.True(mesh.IsClosed);
        Assert.Equal(expected, mesh.Volume(), expected * 0.01);
    }

    // ---- degenerate and guard cases ----

    [Fact]
    public void NarrowBand_HandlesARegionThatMissesTheSurfaceEntirely()
    {
        var source = Sdf.Sphere(2);
        var region = new Aabb((20, 20, 20), (24, 24, 24)); // nowhere near the sphere
        var narrow = source.NarrowBand(region, 0.5, 1.0);

        foreach (var p in Samples(region, 0.5))
        {
            double actual = narrow.Evaluate(p);
            Assert.True(double.IsFinite(actual), $"non-finite value at {p}");
            Assert.True(actual > 0, $"outside sample reported inside at {p}");
        }
        // With no band to seed from, the grid falls back to a dense bake, so it is exact.
        Assert.Equal(source.Evaluate((22, 22, 22)), narrow.Evaluate((22, 22, 22)), Precision);
    }

    [Fact]
    public void NarrowBand_BoundsAreTheBakedRegion()
    {
        var region = new Aabb((-4, -4, -4), (4, 4, 4));
        var narrow = Sdf.Sphere(2).NarrowBand(region, 0.5, 1);
        Assert.Equal(region.Min.X, narrow.Bounds.Min.X, Precision);
        Assert.Equal(region.Max.Z, narrow.Bounds.Max.Z, Precision);
    }

    [Fact]
    public void NarrowBand_OverOwnBounds_ContainsTheSolid()
    {
        var source = Sdf.Box(6, 4, 2);
        var narrow = source.NarrowBand(0.25);
        Assert.True(narrow.Bounds.Contains(source.Bounds.Min));
        Assert.True(narrow.Bounds.Contains(source.Bounds.Max));
        Assert.True(narrow.Evaluate((0, 0, 0)) < 0);
        Assert.True(narrow.Evaluate((10, 0, 0)) > 0);
    }

    [Fact]
    public void NarrowBand_RejectsUnboundedFieldsWithoutARegion() =>
        Assert.Throws<InvalidOperationException>(() => Sdf.HalfSpace((0, 0, 1), 0).NarrowBand(0.5));

    [Fact]
    public void NarrowBand_RejectsANegativeBandWidth() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Sdf.Sphere(1).NarrowBand(new Aabb((-2, -2, -2), (2, 2, 2)), 0.5, -1));
}
