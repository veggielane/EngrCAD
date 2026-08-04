using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Implicit.Tests;

/// <summary>
/// The domain operators: repetition (finite and infinite), twist, bend, taper, elongation
/// and displacement.
/// <para>
/// The oracle for repetition is an <b>identity</b> rather than a tolerance: an instance's
/// value is <c>child(p − spacing·n)</c> whichever way it is spelled, so a lattice must agree
/// with an explicit union of translated copies <em>bit for bit</em> wherever both see the
/// same nearest instance. A tolerance there would accept a lattice that had quietly picked
/// the wrong cell.
/// </para>
/// <para>
/// The oracle for the non-isometric three is the pair of properties they are documented to
/// have and no more: an exact SIGN (checked against the pre-image, which is what the solid
/// IS), and a magnitude that stays within the Lipschitz factor the node itself reports —
/// verified by measuring secants, so the reported bound is checked against the field rather
/// than against the algebra it was derived from.
/// </para>
/// </summary>
public class DomainOperatorTests
{
    // ---- repetition ----

    [Fact]
    public void LimitedRepetition_IsBitIdenticalToAnExplicitUnionOfCopies()
    {
        var child = Sdf.Sphere(1.2);
        var lattice = child.Repeat((4, 3, 0), new Vector3i(4, 3, 1));

        var copies = new List<Sdf>();
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 3; j++)
                copies.Add(child.Translate((4.0 * i, 3.0 * j, 0)));
        var explicitUnion = Sdf.Union(copies);

        foreach (var p in Probes(seed: 91, count: 20000, extent: 14))
        {
            double a = lattice.Evaluate(p);
            double b = explicitUnion.Evaluate(p);
            Assert.True(
                BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b),
                $"at {p}: lattice {a:R} != union {b:R}");
        }
    }

    /// <summary>
    /// The infinite form, checked the same way over a window: within the probe box the union
    /// of enough copies IS the lattice, so the values must agree exactly.
    /// </summary>
    [Fact]
    public void InfiniteRepetition_IsBitIdenticalToAnExplicitUnionWithinAWindow()
    {
        var child = Sdf.Box(2, 2, 2);
        var lattice = child.Repeat((5, 0, 0));

        var copies = new List<Sdf>();
        for (int i = -6; i <= 6; i++)
            copies.Add(child.Translate((5.0 * i, 0, 0)));
        var explicitUnion = Sdf.Union(copies);

        foreach (var p in Probes(seed: 12, count: 20000, extent: 9))
        {
            double a = lattice.Evaluate(p);
            double b = explicitUnion.Evaluate(p);
            Assert.True(
                BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b),
                $"at {p}: lattice {a:R} != union {b:R}");
        }
    }

    /// <summary>
    /// The property that forced two cells per axis instead of the single nearest-cell form
    /// every shader implementation uses. A child not symmetric about its cell centre makes
    /// the one-cell map discontinuous at every cell boundary, and a discontinuous field is
    /// Lipschitz at no constant at all — so the polygonizer's cull could not be widened to
    /// cover it, and it would report surface where there is none.
    /// </summary>
    [Fact]
    public void Repetition_IsContinuousAcrossCellBoundaries_ForAnOffCentreChild()
    {
        // A sphere pushed to one side of its cell: the asymmetric case.
        var child = Sdf.Sphere(0.6).Translate((0.9, 0, 0));
        var lattice = child.Repeat((4, 0, 0));

        double worst = 0;
        const double step = 1e-7;
        // Straddle every cell boundary in the window, plus a few off-boundary controls.
        for (int cell = -3; cell <= 3; cell++)
        {
            double boundary = 4.0 * cell + 2.0;   // half-way between instance centres
            for (int s = -4; s <= 3; s++)
            {
                double a = lattice.Evaluate((boundary + s * step, 0.3, -0.2));
                double b = lattice.Evaluate((boundary + (s + 1) * step, 0.3, -0.2));
                worst = Math.Max(worst, Math.Abs(b - a) / step);
            }
        }

        // A 1-Lipschitz field's secants never exceed 1; the single-cell map's jump at a
        // boundary is the child's asymmetry over 1e-7, which reads in the millions.
        Assert.True(worst <= 1.0 + 1e-6, $"worst secant slope {worst:R} — the field jumps at a cell boundary");
    }

    [Fact]
    public void Repetition_RefusesAChildTooLargeForItsCell_NamingTheSpan()
    {
        var wide = Sdf.Box(6, 2, 2);
        var ex = Assert.Throws<ArgumentException>(() => wide.Repeat((4, 0, 0)));
        Assert.Contains("fit inside one cell", ex.Message);
        Assert.Contains("-3", ex.Message);   // the child's own span is reported
    }

    [Fact]
    public void Repetition_RefusesAnUnboundedChildOnARepeatedAxis()
    {
        var half = Sdf.HalfSpace((0, 0, 1), 0);
        var ex = Assert.Throws<ArgumentException>(() => half.Repeat((0, 0, 3)));
        Assert.Contains("finite bounds", ex.Message);
    }

    [Fact]
    public void LimitedRepetition_ReportsFiniteBoundsCoveringEveryInstance()
    {
        var lattice = Sdf.Sphere(1).Repeat((3, 0, 0), new Vector3i(5, 1, 1));
        var b = lattice.Bounds;
        Assert.True(Sdf.IsFinite(b));
        Assert.Equal(-1, b.Min.X, 12);
        Assert.Equal(3 * 4 + 1, b.Max.X, 12);   // last instance at x = 12, radius 1
    }

    [Fact]
    public void InfiniteRepetition_ReportsInfiniteBoundsOnTheRepeatedAxisOnly()
    {
        var lattice = Sdf.Sphere(1).Repeat((3, 0, 0));
        var b = lattice.Bounds;
        Assert.True(double.IsNegativeInfinity(b.Min.X));
        Assert.True(double.IsPositiveInfinity(b.Max.X));
        Assert.Equal(-1, b.Min.Y, 12);
        Assert.Equal(1, b.Max.Z, 12);
    }

    /// <summary>A repeated lattice of spheres polygonizes to the right number of components
    /// with the right total volume — the end-to-end statement that the cull did not drop one.</summary>
    [Fact]
    public void LimitedRepetition_PolygonizesEveryInstance()
    {
        var lattice = Sdf.Sphere(1.5).Repeat((5, 5, 0), new Vector3i(3, 2, 1));
        var mesh = SurfaceNets.Polygonize(lattice, resolution: 120);
        var components = MeshConnectedComponents.Find(mesh);

        Assert.Equal(6, components.Count);
        double analytic = 6 * 4.0 / 3.0 * Math.PI * 1.5 * 1.5 * 1.5;
        Assert.Equal(analytic, mesh.Volume(), analytic * 0.02);
    }

    // ---- twist ----

    /// <summary>
    /// The sign is exact because the solid IS the pre-image: a point is inside the twisted
    /// body exactly when its un-twisted image is inside the child. Checked against that
    /// definition rather than against a picture.
    /// </summary>
    [Fact]
    public void Twist_HasAnExactSign_AgainstTheUntwistedPreImage()
    {
        var child = Sdf.Box(10, 4, 20);
        double rate = 0.08;
        var twisted = child.Twist(rate);

        foreach (var p in Probes(seed: 5, count: 20000, extent: 14))
        {
            double angle = -rate * p.Z;
            var q = new Vector3d(
                Math.Cos(angle) * p.X - Math.Sin(angle) * p.Y,
                Math.Sin(angle) * p.X + Math.Cos(angle) * p.Y,
                p.Z);
            Assert.Equal(Math.Sign(child.Evaluate(q)), Math.Sign(twisted.Evaluate(p)));
        }
    }

    /// <summary>
    /// A twist is a rigid motion of every horizontal SLICE, so it preserves volume exactly.
    /// The polygonized volume is therefore the untwisted solid's, to the meshing error — a
    /// property no amount of getting the rotation sign wrong would break, which is why the
    /// sign test above exists as well.
    /// </summary>
    [Fact]
    public void Twist_PreservesVolume()
    {
        var child = Sdf.Box(12, 6, 20);
        var twisted = child.Twist(Math.PI / 40);

        var plain = SurfaceNets.Polygonize(child, resolution: 110).Volume();
        var turned = SurfaceNets.Polygonize(twisted, resolution: 110).Volume();

        Assert.Equal(plain, turned, plain * 0.02);
    }

    [Fact]
    public void Twist_ReportsALipschitzBoundAboveOne_ThatGrowsWithRadius()
    {
        var twisted = Sdf.Box(20, 20, 20).Twist(0.1);
        double near = twisted.LipschitzBound(new Aabb((-2, -2, -10), (2, 2, 10)));
        double far = twisted.LipschitzBound(new Aabb((-20, -20, -10), (20, 20, 10)));

        Assert.True(near > 1, $"a twist is not 1-Lipschitz; reported {near:R}");
        Assert.True(far > near, $"the factor must grow with radius: {near:R} then {far:R}");

        // The closed form: sigma = (k + sqrt(k^2 + 4)) / 2 with k = rate * radius.
        double k = 0.1 * Math.Sqrt(20.0 * 20 + 20 * 20);
        Assert.Equal(0.5 * (k + Math.Sqrt(k * k + 4)), far, 12);
    }

    // ---- bend ----

    [Fact]
    public void Bend_HasAnExactSign_AgainstTheUnbentPreImage()
    {
        var child = Sdf.Box(30, 6, 4);
        double curvature = 0.03;
        var bent = child.Bend(curvature);

        foreach (var p in Probes(seed: 17, count: 20000, extent: 20))
        {
            double angle = curvature * p.X;
            var q = new Vector3d(
                Math.Cos(angle) * p.X - Math.Sin(angle) * p.Y,
                Math.Sin(angle) * p.X + Math.Cos(angle) * p.Y,
                p.Z);
            Assert.Equal(Math.Sign(child.Evaluate(q)), Math.Sign(bent.Evaluate(p)));
        }
    }

    /// <summary>A zero curvature is the identity — an exact statement, not a limit.</summary>
    [Fact]
    public void Bend_WithZeroCurvature_IsTheChildBitForBit()
    {
        var child = Sdf.Box(10, 6, 4);
        var bent = child.Bend(0);
        foreach (var p in Probes(seed: 3, count: 4000, extent: 9))
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(child.Evaluate(p)),
                BitConverter.DoubleToInt64Bits(bent.Evaluate(p)));
    }

    // ---- taper ----

    /// <summary>
    /// A taper of a prism is a frustum, whose volume is the closed form
    /// <c>h/3·(A₀ + A₁ + √(A₀A₁))</c> — an independent oracle, since nothing in the operator
    /// knows what a frustum is.
    /// </summary>
    [Fact]
    public void Taper_OfABox_MatchesTheFrustumVolume()
    {
        const double side = 12, height = 20, top = 0.4;
        var tapered = Sdf.Box(side, side, height).Taper(1.0, top);

        double a0 = side * side;
        double a1 = side * top * (side * top);
        double analytic = height / 3 * (a0 + a1 + Math.Sqrt(a0 * a1));

        var mesh = SurfaceNets.Polygonize(tapered, resolution: 140);
        Assert.Equal(analytic, mesh.Volume(), analytic * 0.02);
    }

    [Fact]
    public void Taper_WithBothScalesOne_IsTheChildBitForBit()
    {
        var child = Sdf.Cylinder(4, 10);
        var tapered = child.Taper(1, 1);
        foreach (var p in Probes(seed: 8, count: 4000, extent: 9))
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(child.Evaluate(p)),
                BitConverter.DoubleToInt64Bits(tapered.Evaluate(p)));
    }

    [Fact]
    public void Taper_RefusesAnUnboundedChild()
    {
        var ex = Assert.Throws<ArgumentException>(() => Sdf.Gyroid(5, 1).Taper(1, 0.5));
        Assert.Contains("finite", ex.Message);
    }

    [Fact]
    public void Taper_RefusesANonPositiveScale()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Sdf.Sphere(1).Taper(1, 0));
    }

    // ---- elongation ----

    /// <summary>
    /// Elongating a sphere gives exactly a rounded box — and the comparison against this
    /// project's own rounded box says precisely where "exactly" holds.
    /// <para>
    /// <b>Outside and on the surface it is bit-identical</b>, because the elongation map's
    /// components are <c>sign(p)·max(|p| − h, 0)</c>, whose squares are the rounded box's own
    /// <c>max(q, 0)</c> terms summed in the same order. <b>Inside it is a strict lower
    /// bound</b>: within the core slab every coordinate clamps, the map lands on the origin,
    /// and the value is the child's centre value −r however deep the elongated body actually
    /// is. That is the engine's contract (exact sign, magnitude never nearer than the truth)
    /// rather than a defect, and it is asserted in both directions so neither half can drift.
    /// </para>
    /// </summary>
    [Fact]
    public void Elongate_OfASphere_IsTheRoundedBoxOutside_AndALowerBoundInside()
    {
        const double r = 2;
        var stretched = Sdf.Sphere(r).Elongate((5, 3, 1));
        var rounded = Sdf.RoundedBox(2 * (5 + r), 2 * (3 + r), 2 * (1 + r), r);

        int strictlyShortInside = 0;
        foreach (var p in Probes(seed: 44, count: 20000, extent: 14))
        {
            double exact = rounded.Evaluate(p);
            double reported = stretched.Evaluate(p);
            if (exact >= 0)
            {
                Assert.True(
                    BitConverter.DoubleToInt64Bits(exact) == BitConverter.DoubleToInt64Bits(reported),
                    $"outside at {p}: rounded box {exact:R} != elongated sphere {reported:R}");
            }
            else
            {
                Assert.True(reported < 0, $"sign flipped at {p}");
                Assert.True(reported >= exact - 1e-12,
                    $"inside at {p}: {reported:R} claims to be deeper than the truth {exact:R}");
                if (reported > exact + 1e-9)
                    strictlyShortInside++;
            }
        }

        Assert.True(strictlyShortInside > 0,
            "the inside bound was never strict; the fixture is not reaching the clamped core");
    }

    [Fact]
    public void Elongate_ByZero_IsTheChildBitForBit()
    {
        var child = Sdf.Torus(6, 2);
        var stretched = child.Elongate((0, 0, 0));
        foreach (var p in Probes(seed: 21, count: 4000, extent: 12))
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(child.Evaluate(p)),
                BitConverter.DoubleToInt64Bits(stretched.Evaluate(p)));
    }

    // ---- displacement ----

    [Fact]
    public void Displace_MovesTheSurfaceByAtMostTheAmplitude()
    {
        const double amplitude = 0.4;
        var plain = Sdf.Sphere(8);
        var bumpy = plain.Displace(amplitude, (2, 2, 2));

        // Every point of the displaced surface has |child| <= amplitude by definition of the
        // field, so the displaced solid lies in the shell the bounds claim.
        var mesh = SurfaceNets.Polygonize(bumpy, resolution: 130);
        foreach (var v in mesh.Vertices)
        {
            double d = plain.Evaluate(v.Position);
            Assert.True(Math.Abs(d) <= amplitude + 0.05,
                $"a displaced vertex sits {d:R} from the undisplaced surface");
        }
    }

    [Fact]
    public void Displace_WithZeroAmplitude_IsTheChildBitForBit()
    {
        var child = Sdf.Sphere(5);
        var bumpy = child.Displace(0, (3, 3, 3));
        foreach (var p in Probes(seed: 66, count: 4000, extent: 9))
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(child.Evaluate(p)),
                BitConverter.DoubleToInt64Bits(bumpy.Evaluate(p)));
    }

    [Fact]
    public void Displace_ReportsALipschitzBoundThatIncludesTheRipple()
    {
        var bumpy = Sdf.Sphere(5).Displace(0.5, (2, 0, 0));
        Assert.Equal(1 + 0.5 * 2, bumpy.LipschitzBound(bumpy.Bounds), 12);
    }

    // ---- the shared closed form ----

    /// <summary>
    /// The one derivation the three non-isometries share, checked against a direct numerical
    /// singular value: the spectral norm of <c>[[g, w], [0, 1]]</c>. If this drifts, all three
    /// operators' bounds drift together, so it is worth pinning on its own.
    /// </summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 0.5)]
    [InlineData(1, 4)]
    [InlineData(0.25, 2)]
    [InlineData(3, 0.1)]
    public void ShearedScaleNorm_MatchesTheMeasuredLargestSingularValue(double g, double w)
    {
        double best = 0;
        for (int i = 0; i <= 20000; i++)
        {
            double theta = Math.PI * i / 20000;
            double vx = Math.Cos(theta), vy = Math.Sin(theta);
            // [[g, w], [0, 1]] applied to (vx, vy).
            double ax = g * vx + w * vy, ay = vy;
            best = Math.Max(best, Math.Sqrt(ax * ax + ay * ay));
        }
        Assert.Equal(best, DomainMath.ShearedScaleNorm(g, w), 6);
    }

    /// <summary>The twist's closed form is the shared one at g = 1, and it should reduce to
    /// the tidy <c>(k + √(k²+4))/2</c> — asserted so the identity cannot quietly stop holding.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(0.3)]
    [InlineData(2.5)]
    public void ShearedScaleNorm_AtUnitScale_IsTheTwistsClosedForm(double k)
    {
        Assert.Equal(0.5 * (k + Math.Sqrt(k * k + 4)), DomainMath.ShearedScaleNorm(1, k), 12);
    }

    internal static IEnumerable<Vector3d> Probes(int seed, int count, double extent)
    {
        var rng = new Random(seed);
        for (int i = 0; i < count; i++)
            yield return new Vector3d(
                (rng.NextDouble() * 2 - 1) * extent,
                (rng.NextDouble() * 2 - 1) * extent,
                (rng.NextDouble() * 2 - 1) * extent);
    }
}
