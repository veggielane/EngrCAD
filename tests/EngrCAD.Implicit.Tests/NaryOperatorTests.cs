using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Implicit.Tests;

public class NaryOperatorTests
{
    private const double Precision = 1e-12;

    /// <summary>Counting test double: verifies children are evaluated exactly once per query.</summary>
    private sealed class CountingSdf(Sdf inner) : Sdf
    {
        public int Evaluations;

        public override double Evaluate(in Vector3d point)
        {
            Evaluations++;
            return inner.Evaluate(point);
        }

        public override Aabb Bounds => inner.Bounds;
    }

    private static Sdf[] FiveSpheres() =>
    [
        Sdf.Sphere(1),
        Sdf.Sphere(0.8).Translate((1.5, 0, 0)),
        Sdf.Sphere(1.2).Translate((0, 1.7, 0.3)),
        Sdf.Sphere(0.6).Translate((-1.2, 0.4, -0.8)),
        Sdf.Sphere(0.9).Translate((0.5, -1.3, 0.9)),
    ];

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

    // ---- exact N-ary union / intersection ----

    [Fact]
    public void NaryUnion_MatchesChainedBinary_OnProbeGrid()
    {
        var spheres = FiveSpheres();
        var nary = Sdf.Union(spheres);
        var chained = spheres.Aggregate((acc, s) => acc.Union(s));

        foreach (var p in ProbeGrid(nary.Bounds.Expanded(0.5), 8))
            Assert.Equal(chained.Evaluate(p), nary.Evaluate(p), Precision);
        Assert.Equal(chained.Bounds, nary.Bounds);
    }

    [Fact]
    public void NaryIntersection_MatchesChainedBinary_OnProbeGrid()
    {
        Sdf[] shapes =
        [
            Sdf.Sphere(1),
            Sdf.Sphere(1).Translate((0.4, 0, 0)),
            Sdf.Box(2, 2, 2).Translate((0, 0.3, 0)),
            Sdf.Sphere(1.1).Translate((0, 0, -0.3)),
        ];
        var nary = Sdf.Intersection(shapes);
        var chained = shapes.Aggregate((acc, s) => acc.Intersect(s));

        foreach (var p in ProbeGrid(shapes[0].Bounds.Expanded(0.5), 8))
            Assert.Equal(chained.Evaluate(p), nary.Evaluate(p), Precision);
        Assert.Equal(chained.Bounds, nary.Bounds);
    }

    [Fact]
    public void NaryUnion_ParamsForm_IsExactMin()
    {
        var a = Sdf.Sphere(1);
        var b = Sdf.Sphere(1).Translate((1.5, 0, 0));
        var c = Sdf.Sphere(1).Translate((0, 1.5, 0));
        var union = Sdf.Union(a, b, c);

        var p = new Vector3d(0.6, 0.7, -0.2);
        double expected = Math.Min(a.Evaluate(p), Math.Min(b.Evaluate(p), c.Evaluate(p)));
        Assert.Equal(expected, union.Evaluate(p), Precision);
    }

    [Fact]
    public void NaryOperators_EvaluateEachChildOncePerQuery()
    {
        var p = new Vector3d(0.3, -0.2, 0.5);

        var unionChildren = FiveSpheres().Select(s => new CountingSdf(s)).ToArray();
        Sdf.Union(unionChildren).Evaluate(p);
        Assert.All(unionChildren, c => Assert.Equal(1, c.Evaluations));

        var intersectionChildren = FiveSpheres().Select(s => new CountingSdf(s)).ToArray();
        Sdf.Intersection(intersectionChildren).Evaluate(p);
        Assert.All(intersectionChildren, c => Assert.Equal(1, c.Evaluations));

        var smoothChildren = FiveSpheres().Select(s => new CountingSdf(s)).ToArray();
        Sdf.SmoothUnion(smoothChildren, 0.3).Evaluate(p);
        Assert.All(smoothChildren, c => Assert.Equal(1, c.Evaluations));
    }

    [Fact]
    public void NaryFactories_ValidateOperands()
    {
        Assert.Throws<ArgumentException>(() => Sdf.Union());
        Assert.Throws<ArgumentException>(() => Sdf.Intersection(Array.Empty<Sdf>()));
        Assert.Throws<ArgumentException>(() => Sdf.SmoothUnion(new List<Sdf>(), 0.3));
        Assert.Throws<ArgumentException>(() => Sdf.Union(Sdf.Sphere(1), null!));

        // A single operand passes through unchanged. (Note: Sdf.Union(oneSdf) parses as
        // the instance method, so the single-element case takes the list overload.)
        var sphere = Sdf.Sphere(1);
        Assert.Same(sphere, Sdf.Union(new[] { sphere }));
        Assert.Same(sphere, Sdf.Intersection(new[] { sphere }));
        Assert.Same(sphere, Sdf.SmoothUnion([sphere], 0.3));
    }

    [Fact]
    public void NaryBounds_AreConservative()
    {
        var spheres = FiveSpheres();

        var union = Sdf.Union(spheres);
        foreach (var s in spheres)
            Assert.True(union.Bounds.Contains(s.Bounds));

        var intersection = Sdf.Intersection(spheres[0], spheres[1]);
        Assert.True(spheres[0].Bounds.Contains(intersection.Bounds));
        Assert.True(spheres[1].Bounds.Contains(intersection.Bounds));
    }

    // ---- N-ary smooth union ----

    [Fact]
    public void NarySmoothUnion_TwoChildren_MatchesBinaryExactly()
    {
        var a = Sdf.Sphere(1);
        var b = Sdf.Sphere(1).Translate((1.5, 0, 0));
        var nary = Sdf.SmoothUnion([a, b], 0.4);
        var binary = a.SmoothUnion(b, 0.4);

        foreach (var p in ProbeGrid(nary.Bounds, 7))
            Assert.Equal(binary.Evaluate(p), nary.Evaluate(p), Precision);
    }

    [Fact]
    public void NarySmoothUnion_SignMatchesExactUnion_NoFlips()
    {
        var spheres = FiveSpheres();
        const double k = 0.3;
        var exact = Sdf.Union(spheres);
        var smooth = Sdf.SmoothUnion(spheres, k);
        double maxDip = 0.25 * (spheres.Length - 1) * k; // cumulative fold dip bound

        int exactFolds = 0;
        foreach (var p in ProbeGrid(smooth.Bounds, 10))
        {
            double de = exact.Evaluate(p);
            double ds = smooth.Evaluate(p);

            // The smooth field is a lower bound of the exact union (contains it — no
            // inside → outside flips) and dips at most (n-1)k/4 below it.
            Assert.True(ds <= de + Precision);
            Assert.True(ds >= de - maxDip - Precision);
            if (de < 0)
                Assert.True(ds < 0);
            if (de > maxDip)
                Assert.True(ds > 0); // any sign flip is confined to the blend band

            // Where the nearest child is separated from all others by more than the
            // accumulated fold width, the fold reduces exactly to the hard min.
            var sorted = spheres.Select(s => s.Evaluate(p)).OrderBy(d => d).ToArray();
            if (sorted[1] - sorted[0] >= k * (1 + 0.25 * (spheres.Length - 2)))
            {
                Assert.Equal(de, ds, Precision);
                exactFolds++;
            }
        }
        Assert.True(exactFolds > 50); // the exactness clause actually fired
    }

    [Fact]
    public void NarySmoothUnion_BoundsContainTheBlendedSolid()
    {
        var spheres = FiveSpheres();
        var smooth = Sdf.SmoothUnion(spheres, 0.5);
        var bounds = smooth.Bounds;

        Assert.True(bounds.Contains(Sdf.Union(spheres).Bounds.Expanded(0.5)));
        foreach (var p in ProbeGrid(bounds.Expanded(0.75), 12))
            if (smooth.Evaluate(p) < 0)
                Assert.True(bounds.Contains(p));
    }

    [Fact]
    public void NarySmoothUnion_PolygonizedVolume_AtLeastExactUnions()
    {
        Sdf[] spheres =
        [
            Sdf.Sphere(1),
            Sdf.Sphere(1).Translate((1.4, 0, 0)),
            Sdf.Sphere(1).Translate((0.7, 1.2, 0)),
        ];
        const double k = 0.3;

        double exactVolume = SurfaceNets.Polygonize(Sdf.Union(spheres), 64).Volume();
        double smoothVolume = SurfaceNets.Polygonize(Sdf.SmoothUnion(spheres, k), 64).Volume();

        // Smoothing only adds material (field ≤ exact union's), so the volume can only
        // grow; 1% headroom for the two discretizations. Sanity-bound the growth: the
        // bulge lives in the seam bands, well under 15% of the whole for k = 0.3.
        Assert.True(smoothVolume >= exactVolume * 0.99,
            $"smooth {smoothVolume} vs exact {exactVolume}");
        Assert.True(smoothVolume <= exactVolume * 1.15,
            $"smooth {smoothVolume} vs exact {exactVolume}");
    }

    // ---- falloff blend ----

    [Fact]
    public void Blend_OnSeamCurve_BumpsByFullBlendDistance()
    {
        // Unit spheres at ±0.8 on X intersect on the circle x = 0, y² + z² = 0.36.
        var a = Sdf.Sphere(1).Translate((-0.8, 0, 0));
        var b = Sdf.Sphere(1).Translate((0.8, 0, 0));
        var seamPoint = new Vector3d(0, 0.6, 0); // exactly on both surfaces
        const double d = 0.25;

        // Both kernels have K(0) = 1, so the field dips by exactly blendDistance there.
        Assert.Equal(-d, Sdf.Blend(a, b, d, Falloff.Wyvill).Evaluate(seamPoint), Precision);
        Assert.Equal(-d, Sdf.Blend(a, b, d, Falloff.Exponential).Evaluate(seamPoint), Precision);
    }

    [Fact]
    public void Blend_Wyvill_IsExactlyUnionOutsideTheBand()
    {
        var a = Sdf.Sphere(1).Translate((-0.8, 0, 0));
        var b = Sdf.Sphere(1).Translate((0.8, 0, 0));
        const double d = 0.25;
        var blend = Sdf.Blend(a, b, d, Falloff.Wyvill);
        var union = a | b;

        int outside = 0;
        foreach (var p in ProbeGrid(union.Bounds.Expanded(0.5), 9))
        {
            if (Math.Abs(a.Evaluate(p)) >= d || Math.Abs(b.Evaluate(p)) >= d)
            {
                Assert.Equal(union.Evaluate(p), blend.Evaluate(p), Precision);
                outside++;
            }
        }
        Assert.True(outside > 100);
    }

    [Theory]
    [InlineData(Falloff.Wyvill)]
    [InlineData(Falloff.Exponential)]
    public void Blend_ConvergesToPlainUnion_AsBlendDistanceVanishes(Falloff kernel)
    {
        var a = Sdf.Sphere(1).Translate((-0.8, 0, 0));
        var b = Sdf.Sphere(1).Translate((0.8, 0, 0));
        var union = a | b;

        foreach (double d in new[] { 0.25, 1e-3, 1e-6 })
        {
            var blend = Sdf.Blend(a, b, d, kernel);
            foreach (var p in ProbeGrid(union.Bounds, 6))
            {
                double diff = union.Evaluate(p) - blend.Evaluate(p);
                Assert.True(diff >= -Precision);      // blend only adds material
                Assert.True(diff <= d + Precision);   // bump bounded by blendDistance
            }
        }

        // blendDistance ≤ 0 degrades to the plain union outright.
        var degenerate = Sdf.Blend(a, b, 0);
        var probe = new Vector3d(0.1, 0.55, -0.2);
        Assert.Equal(union.Evaluate(probe), degenerate.Evaluate(probe), Precision);
    }

    [Fact]
    public void Blend_BoundsContainTheBlendedSolid()
    {
        var a = Sdf.Sphere(1).Translate((-0.8, 0, 0));
        var b = Sdf.Sphere(1).Translate((0.8, 0, 0));
        var blend = Sdf.Blend(a, b, 0.4, Falloff.Exponential);
        var bounds = blend.Bounds;

        Assert.True(bounds.Contains((a.Bounds.Union(b.Bounds)).Expanded(0.4)));
        foreach (var p in ProbeGrid(bounds.Expanded(0.6), 12))
            if (blend.Evaluate(p) < 0)
                Assert.True(bounds.Contains(p));
    }

    [Fact]
    public void Blend_PolygonizedVolume_GrowsWithFillet()
    {
        var a = Sdf.Sphere(1).Translate((-0.9, 0, 0));
        var b = Sdf.Sphere(1).Translate((0.9, 0, 0));

        double unionVolume = SurfaceNets.Polygonize(a | b, 64).Volume();
        double blendVolume = SurfaceNets.Polygonize(Sdf.Blend(a, b, 0.3), 64).Volume();

        Assert.True(blendVolume >= unionVolume * 0.99,
            $"blend {blendVolume} vs union {unionVolume}");
        Assert.True(blendVolume <= unionVolume * 1.20,
            $"blend {blendVolume} vs union {unionVolume}");
    }
}
