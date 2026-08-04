using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Implicit.Tests;

/// <summary>
/// The new primitives, each held to the fidelity its own documentation claims and no more.
/// <para>
/// <b>The oracle for every plane-bounded solid is the same and it is exact</b>: the distance
/// to a polyhedron's boundary is the minimum over its boundary TRIANGLES, which
/// <c>Distance3d.ClosestPointOnTriangle</c> answers in closed form, and the sign is the
/// half-space test. So a pyramid, a prism and a wedge are compared against the truth rather
/// than against a re-derivation of the formula being tested — which would agree with its own
/// transcription error.
/// </para>
/// <para>
/// <b>The ellipsoid is MEASURED, not asserted.</b> It is the one primitive here with no
/// closed-form distance, so the deliverable is the error curve against eccentricity rather
/// than a tolerance somebody picked.
/// </para>
/// </summary>
public class PrimitiveDistanceTests(ITestOutputHelper output)
{
    // ---- rounded box ----

    [Fact]
    public void RoundedBox_IsExact_AgainstTheOffsetOfItsInnerBox()
    {
        // The rounding IS an outward offset of the inner box, and an offset of an exact field
        // is exact — so the two spellings must agree to the bit, not to a tolerance.
        var rounded = Sdf.RoundedBox(10, 8, 6, 1.5);
        var byOffset = Sdf.Box(10 - 3, 8 - 3, 6 - 3).Offset(1.5);
        foreach (var p in DomainOperatorTests.Probes(seed: 4, count: 20000, extent: 12))
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(byOffset.Evaluate(p)),
                BitConverter.DoubleToInt64Bits(rounded.Evaluate(p)));
    }

    [Fact]
    public void RoundedBox_WithZeroRadius_IsThePlainBox()
    {
        var rounded = Sdf.RoundedBox(10, 8, 6, 0);
        var plain = Sdf.Box(10, 8, 6);
        foreach (var p in DomainOperatorTests.Probes(seed: 9, count: 5000, extent: 12))
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(plain.Evaluate(p)),
                BitConverter.DoubleToInt64Bits(rounded.Evaluate(p)));
    }

    [Fact]
    public void RoundedBox_RefusesARadiusLargerThanHalfTheSmallestSide()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Sdf.RoundedBox(10, 8, 4, 2.5));
        Assert.Contains("exceeds half the smallest side", ex.Message);
    }

    /// <summary>Volume: a rounded box is Steiner's formula on the inner box, exactly.</summary>
    [Fact]
    public void RoundedBox_MatchesSteinersVolume()
    {
        const double a = 20, b = 14, c = 10, r = 3;
        double ia = a - 2 * r, ib = b - 2 * r, ic = c - 2 * r;
        double analytic = ia * ib * ic                                   // core
            + 2 * r * (ia * ib + ib * ic + ic * ia)                       // slabs
            + Math.PI * r * r * (ia + ib + ic)                            // edge quarter-cylinders
            + 4.0 / 3.0 * Math.PI * r * r * r;                            // corner octants

        var mesh = SurfaceNets.Polygonize(Sdf.RoundedBox(a, b, c, r), resolution: 150);
        Assert.Equal(analytic, mesh.Volume(), analytic * 0.01);
    }

    // ---- round cone and link ----

    /// <summary>
    /// Equal radii make a round cone a capsule. The two spell the same geometry through
    /// different arithmetic (a tangent-line branch against a clamped segment projection), so
    /// they agree to round-off rather than to the bit — the comparison is a tolerance on the
    /// VALUE, never xUnit's decimal-places overload, which rounds both sides and can call
    /// 14.766179460678504 and ...501 unequal.
    /// </summary>
    [Fact]
    public void RoundCone_WithEqualRadii_IsACapsule()
    {
        var cone = Sdf.RoundCone(2, 2, 9);
        var capsule = Sdf.Capsule((0, 0, 0), (0, 0, 9), 2);
        double worst = 0;
        foreach (var p in DomainOperatorTests.Probes(seed: 31, count: 20000, extent: 14))
            worst = Math.Max(worst, Math.Abs(capsule.Evaluate(p) - cone.Evaluate(p)));
        output.WriteLine($"round cone vs capsule: worst absolute difference {worst:E3}");
        Assert.True(worst < 1e-13, $"worst difference {worst:R} is larger than round-off");
    }

    /// <summary>
    /// The exactness claim, checked where it can be checked in closed form: on the axis above
    /// the top cap and below the bottom one the nearest feature is that cap's sphere, so the
    /// distance is the sphere's exactly.
    /// </summary>
    [Fact]
    public void RoundCone_IsExactAgainstItsCapSpheres()
    {
        const double r0 = 3, r1 = 1, h = 8;
        var cone = Sdf.RoundCone(r0, r1, h);
        for (double t = 1; t < 6; t += 0.37)
        {
            Assert.Equal(t, cone.Evaluate((0, 0, h + r1 + t)), 12);
            Assert.Equal(t, cone.Evaluate((0, 0, -r0 - t)), 12);
        }
    }

    [Fact]
    public void RoundCone_RefusesOneSphereContainedInTheOther()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Sdf.RoundCone(5, 1, 3));
        Assert.Contains("contains the other", ex.Message);
    }

    [Fact]
    public void Link_WithZeroLength_IsExactlyATorus()
    {
        var link = Sdf.Link(6, 2, 0);
        var torus = Sdf.Torus(6, 2);
        foreach (var p in DomainOperatorTests.Probes(seed: 77, count: 20000, extent: 12))
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(torus.Evaluate(p)),
                BitConverter.DoubleToInt64Bits(link.Evaluate(p)));
    }

    /// <summary>
    /// A link is a torus split and pulled apart, so its volume is the torus's plus two
    /// straight tube runs — both closed forms, and nothing in the node knows either.
    /// </summary>
    [Fact]
    public void Link_MatchesTheTorusPlusStraightRunsVolume()
    {
        const double major = 6, minor = 1.5, half = 4;
        double analytic = 2 * Math.PI * Math.PI * major * minor * minor    // the torus
            + 2 * (2 * half) * Math.PI * minor * minor;                    // two straight runs
        var mesh = SurfaceNets.Polygonize(Sdf.Link(major, minor, half), resolution: 170);
        Assert.Equal(analytic, mesh.Volume(), analytic * 0.02);
    }

    // ---- polyhedral primitives, against the exact boundary oracle ----

    [Fact]
    public void Pyramid_IsExact_AgainstItsOwnBoundaryTriangles()
    {
        const double side = 10, height = 12;
        var pyramid = Sdf.Pyramid(side, height);
        double h = side / 2;
        Vector3d apex = (0, 0, height);
        Vector3d[] baseCorners = [(-h, -h, 0), (h, -h, 0), (h, h, 0), (-h, h, 0)];
        var triangles = new List<(Vector3d, Vector3d, Vector3d)>
        {
            (baseCorners[0], baseCorners[2], baseCorners[1]),
            (baseCorners[0], baseCorners[3], baseCorners[2]),
        };
        for (int i = 0; i < 4; i++)
            triangles.Add((baseCorners[i], baseCorners[(i + 1) % 4], apex));

        AssertMatchesBoundary(pyramid, triangles, seed: 202, extent: 18, tolerance: 1e-9);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(8)]
    public void Prism_IsExact_AgainstItsOwnBoundaryTriangles(int sides)
    {
        const double r = 6, height = 9;
        var prism = Sdf.Prism(sides, r, height);

        var top = new Vector3d[sides];
        var bottom = new Vector3d[sides];
        for (int i = 0; i < sides; i++)
        {
            double a = 2 * Math.PI * i / sides;
            top[i] = (r * Math.Cos(a), r * Math.Sin(a), height / 2);
            bottom[i] = (r * Math.Cos(a), r * Math.Sin(a), -height / 2);
        }
        var triangles = new List<(Vector3d, Vector3d, Vector3d)>();
        for (int i = 1; i + 1 < sides; i++)
        {
            triangles.Add((top[0], top[i], top[i + 1]));
            triangles.Add((bottom[0], bottom[i + 1], bottom[i]));
        }
        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            triangles.Add((bottom[i], bottom[j], top[j]));
            triangles.Add((bottom[i], top[j], top[i]));
        }

        AssertMatchesBoundary(prism, triangles, seed: 300 + sides, extent: 13, tolerance: 1e-9);
    }

    /// <summary>The regular prism's volume is the n-gon's closed form times the height.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    public void Prism_MatchesTheRegularPolygonVolume(int sides)
    {
        const double r = 6, height = 9;
        double area = 0.5 * sides * r * r * Math.Sin(2 * Math.PI / sides);
        var mesh = SurfaceNets.Polygonize(Sdf.Prism(sides, r, height), resolution: 150);
        Assert.Equal(area * height, mesh.Volume(), area * height * 0.02);
    }

    [Fact]
    public void Wedge_IsExact_AgainstItsOwnBoundaryTriangles()
    {
        const double sx = 12, sy = 8, sz = 10, topX = 4, offset = 1.5;
        var wedge = Sdf.Wedge(sx, sy, sz, topX, offset);

        double hx = sx / 2, hy = sy / 2, hz = sz / 2;
        double t0 = offset - topX / 2, t1 = offset + topX / 2;
        // Section in XZ, extruded along Y.
        Vector2d[] section = [new(-hx, -hz), new(hx, -hz), new(t1, hz), new(t0, hz)];
        var triangles = SectionPrismTriangles(section, hy);

        AssertMatchesBoundary(wedge, triangles, seed: 411, extent: 15, tolerance: 1e-9);
    }

    /// <summary>The wedge's volume is its trapezoidal section's area times its depth.</summary>
    [Fact]
    public void Wedge_MatchesTheTrapezoidVolume()
    {
        const double sx = 12, sy = 8, sz = 10, topX = 4;
        double analytic = 0.5 * (sx + topX) * sz * sy;
        var mesh = SurfaceNets.Polygonize(Sdf.Wedge(sx, sy, sz, topX, 1.5), resolution: 150);
        Assert.Equal(analytic, mesh.Volume(), analytic * 0.02);
    }

    [Fact]
    public void Prism_RefusesAConcaveOrClockwiseSection()
    {
        // Reached through the wedge's own guard rather than a private constructor: a negative
        // top width would wind the section the wrong way.
        Assert.Throws<ArgumentOutOfRangeException>(() => Sdf.Wedge(10, 10, 10, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sdf.Prism(2, 5, 5));
    }

    // ---- convex polyhedron ----

    /// <summary>
    /// The documented split: exact INSIDE (for a convex body the nearest boundary point lies
    /// on the nearest face plane) and a lower bound OUTSIDE. Both halves are asserted, because
    /// claiming only the bound would let an inside-wrong implementation pass.
    /// </summary>
    [Fact]
    public void ConvexPolyhedron_IsExactInside_AndALowerBoundOutside()
    {
        // A cube, whose exact field this project can spell independently.
        var planes = new List<(Vector3d, double)>
        {
            ((1, 0, 0), 4), ((-1, 0, 0), 4),
            ((0, 1, 0), 4), ((0, -1, 0), 4),
            ((0, 0, 1), 4), ((0, 0, -1), 4),
        };
        var solid = Sdf.ConvexPolyhedron(planes);
        var box = Sdf.Box(8, 8, 8);

        int outsideStrictlyShort = 0;
        foreach (var p in DomainOperatorTests.Probes(seed: 55, count: 20000, extent: 9))
        {
            double exact = box.Evaluate(p);
            double reported = solid.Evaluate(p);
            if (exact <= 0)
            {
                Assert.Equal(exact, reported, 12);
            }
            else
            {
                Assert.True(reported > 0, $"sign flipped at {p}");
                Assert.True(reported <= exact + 1e-12,
                    $"at {p}: reported {reported:R} exceeds the true distance {exact:R}");
                if (reported < exact - 1e-9)
                    outsideStrictlyShort++;
            }
        }

        // The bound really is short somewhere — near an edge or a corner — or the test would
        // be asserting nothing about the "lower bound" half.
        Assert.True(outsideStrictlyShort > 0, "the outside bound was never strict; the fixture proves nothing");
    }

    [Fact]
    public void ConvexPolyhedron_ReportsFiniteBoundsWhereIntersectionOfHalfSpacesCannot()
    {
        // A regular tetrahedron: four planes, no axis-aligned box among them.
        Vector3d[] corners = [(1, 1, 1), (1, -1, -1), (-1, 1, -1), (-1, -1, 1)];
        var planes = new List<(Vector3d, double)>();
        for (int skip = 0; skip < 4; skip++)
        {
            var used = corners.Where((_, i) => i != skip).ToArray();
            var n = (used[1] - used[0]).Cross(used[2] - used[0]).Normalized();
            if (n.Dot(corners[skip]) - n.Dot(used[0]) > 0)
                n = -n;
            planes.Add((n, n.Dot(used[0])));
        }

        var solid = Sdf.ConvexPolyhedron(planes);
        Assert.True(Sdf.IsFinite(solid.Bounds));
        foreach (var c in corners)
        {
            Assert.True(solid.Bounds.Contains(c), $"the enumerated bounds miss the vertex {c}");
        }

        // The half-space intersection over the identical planes reports nothing usable.
        var viaIntersection = Sdf.Intersection(
            planes.Select(pl => Sdf.HalfSpace(pl.Item1, pl.Item2)).ToArray());
        Assert.False(Sdf.IsFinite(viaIntersection.Bounds));
    }

    [Fact]
    public void ConvexPolyhedron_RefusesAnUnboundedPlaneSet()
    {
        // A slab plus two more planes: still open along one direction.
        var planes = new List<(Vector3d, double)>
        {
            ((0, 0, 1), 1), ((0, 0, -1), 1), ((1, 0, 0), 1), ((-1, 0, 0), 1),
        };
        var ex = Assert.Throws<ArgumentException>(() => Sdf.ConvexPolyhedron(planes));
        Assert.Contains("bounded", ex.Message);
    }

    // ---- the ellipsoid: measured, not asserted ----

    /// <summary>
    /// With equal semi-axes the expression reduces ALGEBRAICALLY to the sphere's exact
    /// <c>|p| − r</c> — which is the property that makes the approximation trustworthy in the
    /// regime where it is exact, and is worth pinning because it is the only regime where
    /// "exact" applies at all. The reduction is algebraic and not term-for-term (the value
    /// still travels through two divisions), so the two agree to round-off rather than to the
    /// bit, and the assertion says which.
    /// </summary>
    [Fact]
    public void Ellipsoid_WithEqualSemiAxes_ReducesToTheSpheresExactDistance()
    {
        var ellipsoid = Sdf.Ellipsoid(5, 5, 5);
        var sphere = Sdf.Sphere(5);
        double worst = 0;
        foreach (var p in DomainOperatorTests.Probes(seed: 61, count: 20000, extent: 14))
        {
            if (p.LengthSquared == 0)
                continue;
            double a = sphere.Evaluate(p);
            worst = Math.Max(worst, Math.Abs(a - ellipsoid.Evaluate(p)) / Math.Max(1, Math.Abs(a)));
        }
        output.WriteLine($"ellipsoid(5,5,5) vs sphere(5): worst relative difference {worst:E3}");
        Assert.True(worst < 1e-14, $"worst relative difference {worst:R} exceeds round-off");
    }

    [Fact]
    public void Ellipsoid_HasAnExactSign_AtEveryEccentricity()
    {
        foreach (var (a, b, c) in Eccentricities())
        {
            var ellipsoid = Sdf.Ellipsoid(a, b, c);
            foreach (var p in DomainOperatorTests.Probes(seed: 99, count: 4000, extent: 12))
            {
                double implicitValue =
                    p.X * p.X / (a * a) + p.Y * p.Y / (b * b) + p.Z * p.Z / (c * c) - 1;
                if (Math.Abs(implicitValue) < 1e-9)
                    continue;   // on the surface to within round-off; the sign is not defined
                Assert.Equal(Math.Sign(implicitValue), Math.Sign(ellipsoid.Evaluate(p)));
            }
        }
    }

    [Fact]
    public void Ellipsoid_AtTheCentre_ReportsTheTrueDistanceToTheNearestSurfacePoint()
    {
        var ellipsoid = Sdf.Ellipsoid(5, 3, 1.5);
        Assert.Equal(-1.5, ellipsoid.Evaluate(Vector3d.Zero), 12);
    }

    /// <summary>
    /// The measurement the folklore stands in for — and it says something sharper than
    /// "the error grows with eccentricity": <b>outside the solid the value is a genuine LOWER
    /// bound</b> (never nearer than the truth, which is the engine's contract), while
    /// <b>inside it over-reports depth</b>. Both directions are asserted, because a test that
    /// only bounded the magnitude of the error would not distinguish them, and they have
    /// different consequences downstream.
    /// </summary>
    [Fact]
    public void Ellipsoid_IsALowerBoundOutside_AndOverReportsDepthInside_Measured()
    {
        output.WriteLine("semi-axes         aspect   |reported|/|true| outside   inside");
        double weakestOutside = 1, strongestInside = 1;
        foreach (var (a, b, c) in Eccentricities())
        {
            var ellipsoid = Sdf.Ellipsoid(a, b, c);
            double outLo = 1, outHi = 1, inLo = 1, inHi = 1;
            foreach (var p in Probes(a, b, c, seed: 1234, count: 8000))
            {
                double truth = ExactEllipsoidDistance(p, a, b, c);
                if (Math.Abs(truth) < 1e-3)
                    continue;
                double ratio = Math.Abs(ellipsoid.Evaluate(p)) / Math.Abs(truth);
                if (truth > 0) { outLo = Math.Min(outLo, ratio); outHi = Math.Max(outHi, ratio); }
                else { inLo = Math.Min(inLo, ratio); inHi = Math.Max(inHi, ratio); }
            }
            weakestOutside = Math.Min(weakestOutside, outLo);
            strongestInside = Math.Max(strongestInside, inHi);

            output.WriteLine(
                $"{a,4:0.##} {b,4:0.##} {c,4:0.##}   {Math.Max(a, Math.Max(b, c)) / Math.Min(a, Math.Min(b, c)),5:0.##}   " +
                $"[{outLo,6:0.###}, {outHi,6:0.###}]              [{inLo,6:0.###}, {inHi,6:0.###}]");

            // The claim that matters: outside, never nearer than the truth.
            Assert.True(outHi <= 1 + 1e-9,
                $"ellipsoid {a}x{b}x{c}: reported up to {outHi:R}x the true distance OUTSIDE — " +
                "that is an over-estimate, which the cull and the projection step cannot absorb");
        }

        // The fixture must reach a real eccentricity, or the assertion above proves nothing.
        Assert.True(weakestOutside < 0.5, $"the outside bound was never loose (weakest {weakestOutside:R})");
        Assert.True(strongestInside > 2, $"the inside over-report never appeared ({strongestInside:R})");
    }

    /// <summary>
    /// The reported Lipschitz bound must actually cover the field, or the polygonizer's cull
    /// widens by too little and drops geometry. Measured by secants away from the centre,
    /// where the field is genuinely discontinuous and the node says so.
    /// </summary>
    [Fact]
    public void Ellipsoid_ReportedLipschitzBound_CoversTheMeasuredSecants()
    {
        output.WriteLine("semi-axes         reported   measured   measured/reported");
        double tightest = 0;
        foreach (var (a, b, c) in Eccentricities())
        {
            var ellipsoid = Sdf.Ellipsoid(a, b, c);
            double reported = ellipsoid.LipschitzBound(ellipsoid.Bounds.Expanded(10));
            double smallest = Math.Min(a, Math.Min(b, c));
            double worst = 0;

            var rng = new Random(7);
            for (int i = 0; i < 60000; i++)
            {
                var p = new Vector3d(
                    (rng.NextDouble() * 2 - 1) * 2.2 * a,
                    (rng.NextDouble() * 2 - 1) * 2.2 * b,
                    (rng.NextDouble() * 2 - 1) * 2.2 * c);
                if (p.Length < 0.5 * smallest)
                    continue;   // k0 < 1/2: the region the derivation excludes
                var d = new Vector3d(
                    rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5)
                    .Normalized() * (1e-5 * smallest);
                worst = Math.Max(worst, Math.Abs(ellipsoid.Evaluate(p + d) - ellipsoid.Evaluate(p)) / d.Length);
            }

            tightest = Math.Max(tightest, worst / reported);
            output.WriteLine($"{a,4:0.##} {b,4:0.##} {c,4:0.##}   {reported,8:0.###}   {worst,8:0.###}   {worst / reported,10:0.##}");
            Assert.True(worst <= reported,
                $"ellipsoid {a}x{b}x{c}: measured slope {worst:R} exceeds the reported bound {reported:R}");
        }

        // The bound's slack is real and documented; if this ever approaches 1 the derivation
        // has become tight and the residual filed against it can be closed.
        output.WriteLine($"tightest measured/reported over the family: {tightest:0.##}");
    }

    private static IEnumerable<(double A, double B, double C)> Eccentricities() =>
    [
        (5, 5, 5), (5, 5, 4), (5, 4, 3), (6, 3, 2), (8, 2, 2), (10, 2, 1),
    ];

    private static IEnumerable<Vector3d> Probes(double a, double b, double c, int seed, int count)
    {
        var rng = new Random(seed);
        for (int i = 0; i < count; i++)
            yield return new Vector3d(
                (rng.NextDouble() * 2 - 1) * 2.2 * a,
                (rng.NextDouble() * 2 - 1) * 2.2 * b,
                (rng.NextDouble() * 2 - 1) * 2.2 * c);
    }

    /// <summary>
    /// The EXACT point-to-ellipsoid distance, and the reason the earlier surface-scan oracle
    /// was replaced: a scan's own resolution error swamps the quantity being measured close to
    /// the surface, and duly reported an 86% "error" for a SPHERE, where the formula is exact.
    /// <para>
    /// The closest point satisfies <c>q_i = p_i·r_i²/(r_i² + λ)</c> for the Lagrange multiplier
    /// λ, so <c>F(λ) = Σ p_i²r_i²/(r_i² + λ)² − 1 = 0</c> — one scalar equation, strictly
    /// decreasing on <c>λ > −min(r_i²)</c>, so bisection needs no seed and converges to machine
    /// precision. It shares no arithmetic with the node under test.
    /// </para>
    /// </summary>
    private static double ExactEllipsoidDistance(in Vector3d p, double a, double b, double c)
    {
        double[] r2 = [a * a, b * b, c * c];
        double[] pi = [p.X, p.Y, p.Z];

        double F(double lambda)
        {
            double s = 0;
            for (int i = 0; i < 3; i++)
            {
                double den = r2[i] + lambda;
                s += pi[i] * pi[i] * r2[i] / (den * den);
            }
            return s - 1;
        }

        double minR2 = Math.Min(r2[0], Math.Min(r2[1], r2[2]));
        double lo = -minR2 + 1e-13 * minR2;
        double hi = Math.Max(1.0, p.Length * Math.Max(a, Math.Max(b, c)));
        while (F(hi) > 0)
            hi *= 2;
        while (F(lo) < 0)
            lo = lo * 0.5 - minR2 * 0.5;
        for (int i = 0; i < 200; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (F(mid) > 0)
                lo = mid;
            else
                hi = mid;
        }
        double lambdaStar = 0.5 * (lo + hi);
        var q = new Vector3d(
            p.X * r2[0] / (r2[0] + lambdaStar),
            p.Y * r2[1] / (r2[1] + lambdaStar),
            p.Z * r2[2] / (r2[2] + lambdaStar));
        double distance = (q - p).Length;
        double inside = p.X * p.X / r2[0] + p.Y * p.Y / r2[1] + p.Z * p.Z / r2[2] - 1;
        return inside < 0 ? -distance : distance;
    }

    // ---- shared boundary oracle ----

    private static List<(Vector3d, Vector3d, Vector3d)> SectionPrismTriangles(
        Vector2d[] sectionXz, double halfDepth)
    {
        var triangles = new List<(Vector3d, Vector3d, Vector3d)>();
        int n = sectionXz.Length;
        var near = new Vector3d[n];
        var far = new Vector3d[n];
        for (int i = 0; i < n; i++)
        {
            near[i] = (sectionXz[i].X, -halfDepth, sectionXz[i].Y);
            far[i] = (sectionXz[i].X, halfDepth, sectionXz[i].Y);
        }
        for (int i = 1; i + 1 < n; i++)
        {
            triangles.Add((near[0], near[i], near[i + 1]));
            triangles.Add((far[0], far[i + 1], far[i]));
        }
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            triangles.Add((near[i], near[j], far[j]));
            triangles.Add((near[i], far[j], far[i]));
        }
        return triangles;
    }

    /// <summary>
    /// The exact oracle for any solid whose boundary is a triangle set: |distance| is the
    /// minimum over the triangles, and the sign comes from the field itself (which the
    /// separate sign tests pin independently).
    /// </summary>
    private static void AssertMatchesBoundary(
        Sdf field, List<(Vector3d A, Vector3d B, Vector3d C)> triangles,
        int seed, double extent, double tolerance)
    {
        foreach (var p in DomainOperatorTests.Probes(seed, count: 3000, extent))
        {
            double best = double.PositiveInfinity;
            foreach (var (a, b, c) in triangles)
                best = Math.Min(best, (Distance3d.ClosestPointOnTriangle(p, a, b, c) - p).Length);
            double reported = field.Evaluate(p);
            Assert.Equal(best, Math.Abs(reported), tolerance * Math.Max(1, best));
        }
    }
}
