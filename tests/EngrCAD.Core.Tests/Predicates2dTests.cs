using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Core.Tests;

public class Predicates2dTests
{
    // ---- Orient2d ----

    [Fact]
    public void Orient2d_ReportsBasicSigns()
    {
        Assert.True(Predicates2d.Orient2d((0, 0), (1, 0), (0, 1)) > 0); // CCW
        Assert.True(Predicates2d.Orient2d((0, 0), (0, 1), (1, 0)) < 0); // CW
        Assert.Equal(0.0, Predicates2d.Orient2d((0, 0), (1, 1), (2, 2)));
    }

    [Fact]
    public void Orient2d_ExactlyCollinearAtHostileMagnitudes_IsExactlyZero()
    {
        // Points (3t, t) lie exactly on the line y = x/3 for any exactly-representable t;
        // the coordinates below span ~130 decimal orders of magnitude.
        var p1 = new Vector2d(3 * Math.Pow(2, -30), Math.Pow(2, -30));
        var p2 = new Vector2d(3 * Math.Pow(2, 10), Math.Pow(2, 10));
        var p3 = new Vector2d(-3 * Math.Pow(2, 400), -Math.Pow(2, 400));

        Assert.Equal(0.0, Predicates2d.Orient2d(p1, p2, p3));
        Assert.Equal(0.0, Predicates2d.Orient2d(p2, p3, p1));
        Assert.Equal(0.0, Predicates2d.Orient2d(p3, p1, p2));
        Assert.Equal(0.0, Predicates2d.Orient2d(p2, p1, p3));
    }

    [Fact]
    public void Orient2d_KettnerGrid_MatchesExactWhereNaiveFails()
    {
        // The classic robustness demonstration (Kettner et al., "Classroom examples of
        // robustness problems in geometric computations"): perturb (0.5, 0.5) by a grid of
        // ulps against the line through (12, 12) and (24, 24). The naive determinant gets
        // a wild pattern of signs wrong; the adaptive predicate must match exact
        // arithmetic everywhere.
        var b = new Vector2d(12, 12);
        var c = new Vector2d(24, 24);
        double ulp = Math.BitIncrement(0.5) - 0.5; // 2^-53

        int naiveDisagreements = 0;
        for (int i = 0; i < 64; i++)
        {
            for (int j = 0; j < 64; j++)
            {
                var p = new Vector2d(0.5 + i * ulp, 0.5 + j * ulp);
                int exact = ExactReference.Orient2dSign(p, b, c);
                Assert.Equal(exact, Predicates2d.Orient2dSign(p, b, c));

                int naive = Math.Sign((p.X - c.X) * (b.Y - c.Y) - (p.Y - c.Y) * (b.X - c.X));
                if (naive != exact)
                    naiveDisagreements++;
            }
        }

        // The test only demonstrates something if the naive filter genuinely fails here.
        Assert.True(naiveDisagreements > 0, "expected the naive determinant to misclassify some grid points");
    }

    [Fact]
    public void Orient2d_NearCollinearFuzz_MatchesExact()
    {
        var random = new Random(20260724);
        for (int iteration = 0; iteration < 5000; iteration++)
        {
            var a = new Vector2d(random.NextDouble() * 20 - 10, random.NextDouble() * 20 - 10);
            var vb = new Vector2d(random.NextDouble() * 20 - 10, random.NextDouble() * 20 - 10);
            // c on (or a few ulps off) the segment a-b: the hostile regime for doubles.
            var c = Vector2d.Lerp(a, vb, random.NextDouble());
            double cx = c.X, cy = c.Y;
            for (int n = random.Next(4); n > 0; n--) cx = Math.BitIncrement(cx);
            for (int n = random.Next(4); n > 0; n--) cy = Math.BitDecrement(cy);
            c = new Vector2d(cx, cy);

            Assert.Equal(ExactReference.Orient2dSign(a, vb, c), Predicates2d.Orient2dSign(a, vb, c));
        }
    }

    [Fact]
    public void Orient2d_SafeFilterRegion_AgreesWithNaiveDeterminant()
    {
        // Where the stage-A filter declares the naive determinant safe, Orient2d returns
        // it verbatim — fuzz that the fast path and the exact sign agree there.
        var random = new Random(42);
        const double ccwErrBoundA = (3.0 + 16.0 * 1.1102230246251565e-16) * 1.1102230246251565e-16;
        int safeCount = 0;
        for (int iteration = 0; iteration < 5000; iteration++)
        {
            var a = new Vector2d(random.NextDouble() * 200 - 100, random.NextDouble() * 200 - 100);
            var b = new Vector2d(random.NextDouble() * 200 - 100, random.NextDouble() * 200 - 100);
            var c = new Vector2d(random.NextDouble() * 200 - 100, random.NextDouble() * 200 - 100);

            double detleft = (a.X - c.X) * (b.Y - c.Y);
            double detright = (a.Y - c.Y) * (b.X - c.X);
            double det = detleft - detright;
            double detsum = Math.Abs(detleft) + Math.Abs(detright);
            if (Math.Abs(det) < ccwErrBoundA * detsum)
                continue; // filter would escalate — not the case under test
            safeCount++;

            Assert.Equal(det, Predicates2d.Orient2d(a, b, c));
            Assert.Equal(ExactReference.Orient2dSign(a, b, c), Math.Sign(det));
        }
        Assert.True(safeCount > 4000); // random points are overwhelmingly non-degenerate
    }

    // ---- InCircle ----

    [Fact]
    public void InCircle_ReportsBasicSigns()
    {
        // CCW triple on the radius-5 circle about the origin.
        var a = new Vector2d(5, 0);
        var b = new Vector2d(0, 5);
        var c = new Vector2d(-5, 0);

        Assert.True(Predicates2d.InCircle(a, b, c, (0, 0)) > 0);   // strictly inside
        Assert.True(Predicates2d.InCircle(a, b, c, (6, 0)) < 0);   // strictly outside
        Assert.Equal(0.0, Predicates2d.InCircle(a, b, c, (0, -5))); // exactly cocircular
        Assert.Equal(0.0, Predicates2d.InCircle(a, b, c, (3, -4))); // 3-4-5 point
    }

    [Fact]
    public void InCircle_ExactlyCocircularQuadruples_AreExactlyZero()
    {
        // Integer points on the radius-25 circle, exact at every power-of-two scale.
        Vector2d[] onCircle = [(25, 0), (0, 25), (-25, 0), (7, 24), (15, 20), (20, -15), (-24, 7)];
        double[] scales = [1.0, Math.Pow(2, 40), Math.Pow(2, -40)];
        foreach (double s in scales)
        {
            var a = new Vector2d(25 * s, 0);
            var b = new Vector2d(0, 25 * s);
            var c = new Vector2d(-25 * s, 0);
            foreach (var p in onCircle)
            {
                var d = new Vector2d(p.X * s, p.Y * s);
                Assert.Equal(0.0, Predicates2d.InCircle(a, b, c, d));
            }
        }
    }

    [Fact]
    public void InCircle_UlpPerturbationsOfCocircularPoints_MatchExact()
    {
        var a = new Vector2d(25, 0);
        var b = new Vector2d(0, 25);
        var c = new Vector2d(-25, 0);
        // Nudge a cocircular point by single ulps in each direction: the determinant is
        // ~ulp-sized, far below the floating-point filter — the adaptive stages decide.
        Vector2d[] bases = [(7, 24), (15, 20), (20, -15)];
        foreach (var p in bases)
        {
            foreach (var d in new Vector2d[]
                     {
                         (Math.BitIncrement(p.X), p.Y), (Math.BitDecrement(p.X), p.Y),
                         (p.X, Math.BitIncrement(p.Y)), (p.X, Math.BitDecrement(p.Y)),
                     })
            {
                int exact = ExactReference.InCircleSign(a, b, c, d);
                Assert.NotEqual(0, exact); // one ulp off the circle is not on it
                Assert.Equal(exact, Predicates2d.InCircleSign(a, b, c, d));
            }
        }
    }

    [Fact]
    public void InCircle_NearCocircularFuzz_MatchesExact()
    {
        var random = new Random(7182818);
        for (int iteration = 0; iteration < 1500; iteration++)
        {
            double cx = random.NextDouble() * 10 - 5;
            double cy = random.NextDouble() * 10 - 5;
            double r = 1 + 9 * random.NextDouble();

            Vector2d OnCircle()
            {
                double angle = random.NextDouble() * 2 * Math.PI;
                return new Vector2d(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle));
            }

            // Four rounded points of one circle: near-cocircular, the hostile regime.
            var a = OnCircle();
            var b = OnCircle();
            var c = OnCircle();
            var d = OnCircle();
            Assert.Equal(ExactReference.InCircleSign(a, b, c, d), Predicates2d.InCircleSign(a, b, c, d));
        }
    }

    [Fact]
    public void InCircle_NearCollinearQuadruples_MatchExactThroughTheDeepestStages()
    {
        // Four points within ulps of one line: the circle degenerates and the incircle
        // determinant collapses through every adaptive stage into the full exact
        // expansion. Unperturbed, the quadruple is exactly degenerate (determinant 0).
        var b = new Vector2d(12, 12);
        var c = new Vector2d(24, 24);
        double ulp = Math.BitIncrement(0.5) - 0.5;
        double ulp6 = Math.BitIncrement(6.0) - 6.0;

        Assert.Equal(0.0, Predicates2d.InCircle((0.5, 0.5), b, c, (6, 6)));

        // Exactly-degenerate quadruple in which EVERY pairwise coordinate difference has a
        // nonzero roundoff tail (0.5+kε−6−γ etc. are inexact): the adaptive evaluation
        // must run its complete second-order expansion to reach the exact 0.0.
        double db = Math.BitIncrement(12.0) - 12.0;
        double dd = Math.BitIncrement(6.0) - 6.0;
        for (int k = 1; k <= 5; k++)
        {
            var pk = new Vector2d(0.5 + k * ulp, 0.5 + k * ulp);
            var bk = new Vector2d(12 + db, 12 + db);
            var dk = new Vector2d(6 + dd, 6 + dd);
            Assert.Equal(0.0, Predicates2d.InCircle(pk, bk, c, dk));
            Assert.Equal(0, ExactReference.InCircleSign(pk, bk, c, dk));
        }

        for (int i = 0; i <= 10; i++)
        {
            for (int j = 0; j <= 10; j++)
            {
                var p = new Vector2d(0.5 + i * ulp, 0.5 + j * ulp);
                for (int k = -2; k <= 2; k++)
                {
                    var d = new Vector2d(6 + k * ulp6, 6);
                    Assert.Equal(
                        ExactReference.InCircleSign(p, b, c, d),
                        Predicates2d.InCircleSign(p, b, c, d));
                }
            }
        }
    }

    [Fact]
    public void InCircle_GeneralPositionFuzz_MatchesExact()
    {
        var random = new Random(314159);
        for (int iteration = 0; iteration < 3000; iteration++)
        {
            Vector2d Next() => new(random.NextDouble() * 40 - 20, random.NextDouble() * 40 - 20);
            var a = Next();
            var b = Next();
            var c = Next();
            var d = Next();
            Assert.Equal(ExactReference.InCircleSign(a, b, c, d), Predicates2d.InCircleSign(a, b, c, d));
        }
    }
}
