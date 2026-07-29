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
