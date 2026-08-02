using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Core.Tests;

public class Predicates3dTests
{
    // ---- Orient3d: conventions ----

    [Fact]
    public void Orient3d_MatchesShewchukSignConvention()
    {
        // a, b, c counter-clockwise seen from +z; d below the plane => positive.
        var a = new Vector3d(0, 0, 0);
        var b = new Vector3d(1, 0, 0);
        var c = new Vector3d(0, 1, 0);

        Assert.True(Predicates3d.Orient3d(a, b, c, new Vector3d(0, 0, -1)) > 0);
        Assert.True(Predicates3d.Orient3d(a, b, c, new Vector3d(0, 0, 1)) < 0);
        Assert.Equal(0.0, Predicates3d.Orient3d(a, b, c, new Vector3d(5, 7, 0)));
    }

    [Fact]
    public void SignedVolume6_IsSixTimesTheSignedVolumeWithTheOppositeSignToOrient3d()
    {
        var a = new Vector3d(0, 0, 0);
        var b = new Vector3d(1, 0, 0);
        var c = new Vector3d(0, 1, 0);
        var d = new Vector3d(0, 0, 1);

        // det[b-a, c-a, d-a] for the unit corner tetrahedron is 1, i.e. volume 1/6.
        Assert.Equal(1.0, Predicates3d.SignedVolume6(a, b, c, d), 12);
        Assert.Equal(-Predicates3d.Orient3d(a, b, c, d), Predicates3d.SignedVolume6(a, b, c, d));
        Assert.Equal(1, Predicates3d.SignedVolume6Sign(a, b, c, d));
        Assert.Equal(-1, Predicates3d.SignedVolume6Sign(a, b, d, c));
    }

    [Fact]
    public void Orient3d_ExactlyCoplanarAtHostileMagnitudes_IsExactlyZero()
    {
        // Points (t, 2t, 3t) all lie on the plane 3x - z = 0 (and y = 2x); the coordinates
        // below span ~130 decimal orders of magnitude, where naive arithmetic loses the
        // differences themselves.
        var p1 = new Vector3d(Math.Pow(2, -30), Math.Pow(2, -29), 3 * Math.Pow(2, -30));
        var p2 = new Vector3d(Math.Pow(2, 10), Math.Pow(2, 11), 3 * Math.Pow(2, 10));
        var p3 = new Vector3d(-Math.Pow(2, 400), -Math.Pow(2, 401), -3 * Math.Pow(2, 400));
        var p4 = new Vector3d(Math.Pow(2, 200), Math.Pow(2, 201), 3 * Math.Pow(2, 200));

        Assert.Equal(0.0, Predicates3d.Orient3d(p1, p2, p3, p4));
        Assert.Equal(0.0, Predicates3d.Orient3d(p2, p3, p4, p1));
        Assert.Equal(0.0, Predicates3d.Orient3d(p4, p1, p3, p2));
    }

    [Fact]
    public void Orient3d_KettnerStyleUlpGrid_MatchesExactWhereNaiveFails()
    {
        // The 3D analogue of the classic Kettner demonstration: perturb a point by an ulp
        // grid that STRADDLES the plane through three widely-spread points. The naive
        // determinant misclassifies a scatter of grid points; the predicate must match
        // exact arithmetic at every one.
        //
        // The straddling is the whole test, and getting it wrong is silent: the first
        // version of this used a plane 11.5 units away from the grid, so every point was
        // unambiguously on one side, the predicate agreed with exact arithmetic trivially,
        // and the only thing that failed was the guard below. b, c, d here span the plane
        // x = y, which the grid crosses whenever i and j differ.
        var b = new Vector3d(12, 12, 0);
        var c = new Vector3d(24, 24, 12);
        var d = new Vector3d(6, 6, 30);
        double ulp = Math.BitIncrement(0.5) - 0.5; // 2^-53

        int naiveDisagreements = 0;
        for (int i = 0; i < 48; i++)
        {
            for (int j = 0; j < 48; j++)
            {
                var p = new Vector3d(0.5 + i * ulp, 0.5 + j * ulp, 0.5);
                int exact = ExactReference.Orient3dSign(p, b, c, d);
                Assert.Equal(exact, Predicates3d.Orient3dSign(p, b, c, d));

                int naive = Math.Sign(NaiveOrient3d(p, b, c, d));
                if (naive != exact)
                    naiveDisagreements++;
            }
        }

        Assert.True(naiveDisagreements > 0, "expected the naive determinant to misclassify some grid points");
    }

    [Fact]
    public void Orient3d_NearCoplanarFuzz_MatchesExact()
    {
        var random = new Random(20260727);
        for (int iteration = 0; iteration < 20000; iteration++)
        {
            var a = RandomPoint(random);
            var b = RandomPoint(random);
            var c = RandomPoint(random);

            // d in (or a few ulps off) the plane abc: the hostile regime for doubles.
            double s = random.NextDouble(), t = random.NextDouble() * (1 - s);
            var d = a + (b - a) * s + (c - a) * t;
            d = Jitter(d, random);

            Assert.Equal(ExactReference.Orient3dSign(a, b, c, d), Predicates3d.Orient3dSign(a, b, c, d));
        }
    }

    [Fact]
    public void Orient3d_IsAntisymmetricUnderAnOddPermutation()
    {
        var random = new Random(11);
        for (int iteration = 0; iteration < 2000; iteration++)
        {
            var a = RandomPoint(random);
            var b = RandomPoint(random);
            var c = RandomPoint(random);
            var d = RandomPoint(random);

            int abcd = Predicates3d.Orient3dSign(a, b, c, d);
            Assert.Equal(-abcd, Predicates3d.Orient3dSign(b, a, c, d)); // one swap
            Assert.Equal(abcd, Predicates3d.Orient3dSign(b, a, d, c));  // two swaps
            Assert.Equal(-abcd, Predicates3d.Orient3dSign(d, b, c, a)); // one swap
        }
    }

    [Fact]
    public void Orient3d_ScaleFreedom_HoldsOverSixDecades()
    {
        var random = new Random(707);
        foreach (double scale in new[] { 1e-3, 1.0, 1e3 })
        {
            for (int iteration = 0; iteration < 2000; iteration++)
            {
                var a = RandomPoint(random) * scale;
                var b = RandomPoint(random) * scale;
                var c = RandomPoint(random) * scale;
                double s = random.NextDouble(), t = random.NextDouble() * (1 - s);
                var d = Jitter(a + (b - a) * s + (c - a) * t, random);

                Assert.Equal(ExactReference.Orient3dSign(a, b, c, d), Predicates3d.Orient3dSign(a, b, c, d));
            }
        }
    }

    // ---- InSphere ----

    [Fact]
    public void InSphere_ReportsBasicSigns()
    {
        // Positively oriented base tetrahedron with Orient3d > 0 (i.e. d BELOW abc).
        var a = new Vector3d(1, 0, 0);
        var b = new Vector3d(0, 1, 0);
        var c = new Vector3d(-1, 0, 0);
        var d = new Vector3d(0, 0, -1);
        Assert.True(Predicates3d.Orient3d(a, b, c, d) > 0);

        Assert.True(Predicates3d.InSphere(a, b, c, d, new Vector3d(0, 0, 0)) > 0);   // centre
        Assert.True(Predicates3d.InSphere(a, b, c, d, new Vector3d(0, 0, 5)) < 0);   // far outside
        Assert.Equal(0.0, Predicates3d.InSphere(a, b, c, d, new Vector3d(0, -1, 0))); // on the unit sphere
    }

    [Fact]
    public void InSphere_AllEightCubeCornersAreExactlyCospherical()
    {
        // The case a CAD tessellation hits constantly: a structured grid whose points are
        // exactly cospherical. Every such quintuple must return exactly 0.0, not noise.
        var corners = new List<Vector3d>();
        foreach (int x in new[] { -1, 1 })
            foreach (int y in new[] { -1, 1 })
                foreach (int z in new[] { -1, 1 })
                    corners.Add(new Vector3d(x, y, z));

        int tested = 0;
        for (int i = 0; i < 8; i++)
            for (int j = i + 1; j < 8; j++)
                for (int k = j + 1; k < 8; k++)
                    for (int l = k + 1; l < 8; l++)
                        for (int m = 0; m < 8; m++)
                        {
                            if (m == i || m == j || m == k || m == l) continue;
                            if (Predicates3d.Orient3dSign(corners[i], corners[j], corners[k], corners[l]) == 0)
                                continue;
                            Assert.Equal(0.0,
                                Predicates3d.InSphere(corners[i], corners[j], corners[k], corners[l], corners[m]));
                            tested++;
                        }

        Assert.True(tested > 100, $"expected many non-degenerate base tetrahedra, got {tested}");
    }

    [Fact]
    public void InSphereOriented_IsIndependentOfBaseOrientation()
    {
        var a = new Vector3d(1, 0, 0);
        var b = new Vector3d(0, 1, 0);
        var c = new Vector3d(-1, 0, 0);
        var d = new Vector3d(0, 0, -1);
        var inside = new Vector3d(0, 0, 0);
        var outside = new Vector3d(0, 0, 5);

        Assert.Equal(1, Predicates3d.InSphereOriented(a, b, c, d, inside));
        Assert.Equal(1, Predicates3d.InSphereOriented(b, a, c, d, inside)); // flipped base
        Assert.Equal(-1, Predicates3d.InSphereOriented(a, b, c, d, outside));
        Assert.Equal(-1, Predicates3d.InSphereOriented(b, a, c, d, outside));
    }

    [Fact]
    public void InSphereOriented_RefusesACoplanarBase()
    {
        var ex = Assert.Throws<ArgumentException>(() => Predicates3d.InSphereOriented(
            new Vector3d(0, 0, 0), new Vector3d(1, 0, 0), new Vector3d(0, 1, 0),
            new Vector3d(1, 1, 0), new Vector3d(0, 0, 1)));
        Assert.Contains("coplanar", ex.Message);
    }

    [Fact]
    public void InSphere_NearCosphericalFuzz_MatchesExact()
    {
        var random = new Random(4242);
        for (int iteration = 0; iteration < 6000; iteration++)
        {
            // Build four points on a sphere of radius r about a random centre, then place
            // the fifth on (or a few ulps off) the same sphere: the hostile regime.
            var centre = RandomPoint(random);
            double r = 0.5 + random.NextDouble() * 4;
            var a = centre + OnSphere(random) * r;
            var b = centre + OnSphere(random) * r;
            var c = centre + OnSphere(random) * r;
            var d = centre + OnSphere(random) * r;
            if (Predicates3d.Orient3dSign(a, b, c, d) == 0) continue;
            var e = Jitter(centre + OnSphere(random) * r, random);

            Assert.Equal(ExactReference.InSphereSign(a, b, c, d, e), Predicates3d.InSphereSign(a, b, c, d, e));
        }
    }

    [Fact]
    public void InSphere_RandomFuzz_MatchesExact()
    {
        var random = new Random(99991);
        for (int iteration = 0; iteration < 6000; iteration++)
        {
            var a = RandomPoint(random);
            var b = RandomPoint(random);
            var c = RandomPoint(random);
            var d = RandomPoint(random);
            var e = RandomPoint(random);
            if (Predicates3d.Orient3dSign(a, b, c, d) == 0) continue;

            Assert.Equal(ExactReference.InSphereSign(a, b, c, d, e), Predicates3d.InSphereSign(a, b, c, d, e));
        }
    }

    [Fact]
    public void InSphere_ScaleFreedom_HoldsOverSixDecades()
    {
        var random = new Random(31337);
        foreach (double scale in new[] { 1e-3, 1.0, 1e3 })
        {
            for (int iteration = 0; iteration < 1500; iteration++)
            {
                var centre = RandomPoint(random) * scale;
                double r = (0.5 + random.NextDouble() * 4) * scale;
                var a = centre + OnSphere(random) * r;
                var b = centre + OnSphere(random) * r;
                var c = centre + OnSphere(random) * r;
                var d = centre + OnSphere(random) * r;
                if (Predicates3d.Orient3dSign(a, b, c, d) == 0) continue;
                var e = Jitter(centre + OnSphere(random) * r, random);

                Assert.Equal(ExactReference.InSphereSign(a, b, c, d, e), Predicates3d.InSphereSign(a, b, c, d, e));
            }
        }
    }

    [Fact]
    public void InSphere_EscalatesOnlyForNearDegenerateInput()
    {
        // The escalation counter is what lets the mesher report the allocating stage's cost
        // honestly. Well-separated random points must never reach it.
        Predicates3d.ResetEscalationCounters();
        var random = new Random(5);
        int evaluated = 0;
        for (int iteration = 0; iteration < 20000; iteration++)
        {
            var a = RandomPoint(random);
            var b = RandomPoint(random);
            var c = RandomPoint(random);
            var d = RandomPoint(random);
            var e = RandomPoint(random);
            if (Predicates3d.Orient3dSign(a, b, c, d) == 0) continue;
            Predicates3d.InSphere(a, b, c, d, e);
            evaluated++;
        }

        Assert.True(evaluated > 19000);
        Assert.Equal(0, Predicates3d.InSphereEscalations);

        // ... and an exactly-cospherical quintuple must reach it.
        Predicates3d.InSphere(
            new Vector3d(1, 0, 0), new Vector3d(0, 1, 0), new Vector3d(-1, 0, 0),
            new Vector3d(0, 0, -1), new Vector3d(0, -1, 0));
        Assert.Equal(1, Predicates3d.InSphereEscalations);
        Predicates3d.ResetEscalationCounters();
    }

    // ---- the exact stage, locked against the BigInteger ground truth ----
    // (ExactReference is an independent BigInteger evaluation written as a different
    // cofactor expansion, so agreement is evidence rather than tautology. These fixtures
    // exist because the exact stage's arithmetic moved from BigInteger to span-based
    // fixed-width integers, and each targets a branch of that arithmetic.)

    /// <summary>The 30 integer points with |p|² = 25 — a lattice sphere every CAD
    /// tessellation's cospherical structure is a scaled cousin of. Exactly cospherical
    /// at every power-of-two scale, since scaling by 2^t is exact on doubles.</summary>
    private static List<Vector3d> LatticeSphere(double scale)
    {
        // Every permutation of (±3, ±4, 0), plus (±5, 0, 0) and its permutations.
        var points = new List<Vector3d>();
        foreach (var (x, y, z) in new (int, int, int)[]
        {
            (3, 4, 0), (3, 0, 4), (4, 3, 0), (4, 0, 3), (0, 3, 4), (0, 4, 3),
        })
        {
            for (int signs = 0; signs < 4; signs++)
            {
                int s1 = (signs & 1) == 0 ? 1 : -1;
                int s2 = (signs & 2) == 0 ? 1 : -1;
                // Apply the two signs to the two NONZERO slots in order.
                int[] v = [x, y, z];
                var w = new double[3];
                int applied = 0;
                for (int i = 0; i < 3; i++)
                {
                    if (v[i] == 0)
                    {
                        w[i] = 0;
                        continue;
                    }
                    w[i] = v[i] * (applied++ == 0 ? s1 : s2) * scale;
                }
                points.Add(new Vector3d(w[0], w[1], w[2]));
            }
        }
        points.Add(new Vector3d(5 * scale, 0, 0));
        points.Add(new Vector3d(-5 * scale, 0, 0));
        points.Add(new Vector3d(0, 5 * scale, 0));
        points.Add(new Vector3d(0, -5 * scale, 0));
        points.Add(new Vector3d(0, 0, 5 * scale));
        points.Add(new Vector3d(0, 0, -5 * scale));
        return points;
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(9.094947017729282e-13)] // 2^-40
    [InlineData(2.037035976334486e90)]  // 2^300
    public void InSphere_CosphericalLatticeFamilies_AreExactlyZeroAtEveryScale(double scale)
    {
        var points = LatticeSphere(scale);
        Predicates3d.ResetEscalationCounters();
        int tested = 0;
        // A deterministic strided subset of quintuples — every one an exact tie the
        // filter cannot settle, with exact zeros scattered through the coordinates
        // (which is what exercises the min-exponent-over-NONZERO rule).
        for (int i = 0; i < points.Count; i += 3)
            for (int j = i + 1; j < points.Count; j += 2)
                for (int k = j + 1; k < points.Count; k += 5)
                    for (int l = k + 1; l < points.Count; l += 7)
                    {
                        if (Predicates3d.Orient3dSign(points[i], points[j], points[k], points[l]) == 0)
                            continue;
                        int m = (l + 11) % points.Count;
                        if (m == i || m == j || m == k || m == l)
                            m = (m + 1) % points.Count;
                        Assert.Equal(0.0, Predicates3d.InSphere(points[i], points[j], points[k], points[l], points[m]));
                        tested++;
                    }

        Assert.True(tested > 200, $"expected many cospherical quintuples, got {tested}");
        Assert.True(Predicates3d.InSphereEscalations >= tested, "exact ties must all have escalated");
        Predicates3d.ResetEscalationCounters();
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(9.094947017729282e-13)]
    [InlineData(2.037035976334486e90)]
    public void InSphere_UlpPerturbedCospherical_MatchesBigIntegerGroundTruth(double scale)
    {
        var points = LatticeSphere(scale);
        var random = new Random(20260802);
        int compared = 0;
        for (int trial = 0; trial < 400; trial++)
        {
            var a = points[random.Next(points.Count)];
            var b = points[random.Next(points.Count)];
            var c = points[random.Next(points.Count)];
            var d = points[random.Next(points.Count)];
            var e = Jitter(points[random.Next(points.Count)], random);
            if (Predicates3d.Orient3dSign(a, b, c, d) == 0)
                continue;

            Assert.Equal(ExactReference.InSphereSign(a, b, c, d, e), Predicates3d.InSphereSign(a, b, c, d, e));
            compared++;
        }
        Assert.True(compared > 200, $"only {compared} non-degenerate trials");
    }

    /// <summary>
    /// A subnormal coordinate beside coordinates at 2^400 spreads the exponents ~1400
    /// bits, which is past the exact stage's stackalloc budget — the pooled branch. The
    /// counter assertion is what keeps this fixture honest: if the budget ever grows past
    /// the spread, the test would silently stop testing the pooled path.
    /// </summary>
    [Fact]
    public void InSphere_WideExponentSpread_TakesThePooledPathAndMatchesGroundTruth()
    {
        double big = Math.Pow(2, 400);
        var a = new Vector3d(big, 0, 0);
        var b = new Vector3d(-big, 0, 0);
        var c = new Vector3d(0, big, 0);
        var d = new Vector3d(0, 0, big);
        Assert.True(Predicates3d.Orient3d(a, b, c, d) != 0);

        Predicates3d.ResetEscalationCounters();
        // e sits on the sphere except for one subnormal x — outside it by s², an amount
        // the filter provably cannot settle, so the call must escalate.
        var e = new Vector3d(double.Epsilon, 0, -big);
        int sign = Predicates3d.InSphereSign(a, b, c, d, e);
        Assert.Equal(1, (int)Predicates3d.InSphereEscalations);
        Assert.Equal(1, (int)Predicates3d.InSpherePooledEscalations);
        Assert.Equal(ExactReference.InSphereSign(a, b, c, d, e), sign);
        Assert.Equal(-1, sign); // strictly outside, positively-oriented base

        // ... and exactly ON the sphere at the same spread — still the pooled path, still
        // an exact tie.
        var onSphere = new Vector3d(0, -big, 0);
        Assert.Equal(0.0, Predicates3d.InSphere(a, b, c, d, onSphere));

        // Subnormals in several coordinates, against the ground truth.
        var random = new Random(77);
        for (int trial = 0; trial < 50; trial++)
        {
            var p = new Vector3d(
                double.Epsilon * random.Next(1, 9),
                -double.Epsilon * random.Next(1, 9),
                -big);
            Assert.Equal(ExactReference.InSphereSign(a, b, c, d, p), Predicates3d.InSphereSign(a, b, c, d, p));
        }
        Predicates3d.ResetEscalationCounters();
    }

    [Fact]
    public void InSphere_AllZeroCoordinates_ReturnsExactlyZero()
    {
        // Degenerate on purpose: every minor of the all-zero configuration is zero, and
        // the exact stage's all-zero early-out must agree with what the BigInteger stage
        // computed for it (a zero determinant).
        var zero = Vector3d.Zero;
        Assert.Equal(0.0, Predicates3d.InSphere(zero, zero, zero, zero, zero));
    }

    /// <summary>
    /// The point of the rewrite: the exact stage allocates NOTHING. Measured over the
    /// exactly-cospherical octahedron (the always-escalating configuration, on the
    /// stackalloc path) and over the wide-spread fixture (the pooled path, which
    /// allocates nothing once the pool is warm).
    /// </summary>
    [Fact]
    public void InSphere_EscalatedCalls_DoNotAllocate()
    {
        var a = new Vector3d(1, 0, 0);
        var b = new Vector3d(0, 1, 0);
        var c = new Vector3d(-1, 0, 0);
        var d = new Vector3d(0, 0, -1);
        var e = new Vector3d(0, -1, 0);

        // Warm-up: JIT the path (and, for the pooled twin below, warm the pool).
        for (int i = 0; i < 16; i++)
            Predicates3d.InSphere(a, b, c, d, e);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            Predicates3d.InSphere(a, b, c, d, e);
        long stackPathBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, stackPathBytes);

        double big = Math.Pow(2, 400);
        var wa = new Vector3d(big, 0, 0);
        var wb = new Vector3d(-big, 0, 0);
        var wc = new Vector3d(0, big, 0);
        var wd = new Vector3d(0, 0, big);
        var we = new Vector3d(double.Epsilon, 0, -big);
        for (int i = 0; i < 16; i++)
            Predicates3d.InSphere(wa, wb, wc, wd, we);

        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
            Predicates3d.InSphere(wa, wb, wc, wd, we);
        long pooledPathBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, pooledPathBytes);
    }

    // ---- helpers ----

    private static double NaiveOrient3d(Vector3d a, Vector3d b, Vector3d c, Vector3d d)
    {
        double adx = a.X - d.X, ady = a.Y - d.Y, adz = a.Z - d.Z;
        double bdx = b.X - d.X, bdy = b.Y - d.Y, bdz = b.Z - d.Z;
        double cdx = c.X - d.X, cdy = c.Y - d.Y, cdz = c.Z - d.Z;
        return adz * (bdx * cdy - cdx * bdy)
             + bdz * (cdx * ady - adx * cdy)
             + cdz * (adx * bdy - bdx * ady);
    }

    private static Vector3d RandomPoint(Random random) => new(
        random.NextDouble() * 20 - 10, random.NextDouble() * 20 - 10, random.NextDouble() * 20 - 10);

    private static Vector3d OnSphere(Random random)
    {
        // Marsaglia: uniform on the unit sphere, and never the zero vector.
        while (true)
        {
            double x = random.NextDouble() * 2 - 1, y = random.NextDouble() * 2 - 1;
            double s = x * x + y * y;
            if (s >= 1 || s == 0) continue;
            double f = 2 * Math.Sqrt(1 - s);
            return new Vector3d(x * f, y * f, 1 - 2 * s);
        }
    }

    private static Vector3d Jitter(Vector3d p, Random random)
    {
        double x = p.X, y = p.Y, z = p.Z;
        for (int n = random.Next(3); n > 0; n--) x = Math.BitIncrement(x);
        for (int n = random.Next(3); n > 0; n--) y = Math.BitDecrement(y);
        for (int n = random.Next(3); n > 0; n--) z = Math.BitIncrement(z);
        return new Vector3d(x, y, z);
    }
}
