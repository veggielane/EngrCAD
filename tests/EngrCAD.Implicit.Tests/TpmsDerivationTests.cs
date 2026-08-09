using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Implicit.Tests;

/// <summary>
/// The gradient constants as DERIVATIONS rather than as measured suprema. Every one of the
/// eight is now a closed form (see <c>Tpms.cs</c>' remarks), and this file checks two
/// different things about each: that the closed form agrees with a global scan of the field's
/// own gradient, and — separately — that the LOAD-BEARING STEP of the derivation holds, since
/// a value can agree by coincidence where a structural claim cannot.
/// <para>
/// The stored constants are deliberately not the derived values: they round UP at the sixth
/// significant figure, which is the safe direction for a bound that keeps the field
/// 1-Lipschitz in doubles. Both halves of that are asserted — sound (stored at least the
/// derived supremum) and tight (within a few parts per million of it).
/// </para>
/// </summary>
public class TpmsDerivationTests(ITestOutputHelper output)
{
    public static TheoryData<TpmsKind> Kinds
    {
        get
        {
            var data = new TheoryData<TpmsKind>();
            foreach (TpmsKind kind in Enum.GetValues<TpmsKind>())
                data.Add(kind);
            return data;
        }
    }

    /// <summary>
    /// The closed form against the field itself: a dense scan of <c>|grad F|</c> over the
    /// fundamental cell, refined by a shrinking-step coordinate climb because these maxima are
    /// isolated points that a grid alone systematically under-reports.
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void EachGradientConstant_MatchesTheGlobalSupremumOfTheGradient(TpmsKind kind)
    {
        double derived = DerivedSupremum(kind);
        var (measured, at) = GlobalSupremum(kind);
        double stored = Tpms.GradientBound(kind);

        output.WriteLine(
            $"{kind,-14} derived {derived:0.############}   measured {measured:0.############}   " +
            $"stored {stored:0.######}   at {at}");

        Assert.Equal(measured, derived, 8);

        // Sound: the stored constant is never below the true supremum, or the field stops
        // being 1-Lipschitz and the polygonizer's cull drops geometry.
        Assert.True(stored >= derived,
            $"{kind}: stored {stored:R} is below the derived supremum {derived:R}");
        // Tight: the rounding is at the sixth figure and costs wall thickness in proportion.
        Assert.True(stored <= derived * (1 + 1e-5),
            $"{kind}: stored {stored:R} is loose against the derived supremum {derived:R}");
    }

    /// <summary>
    /// The diagonal lemma's own step, which is what makes Lidinoid's and Split P's constants
    /// one-variable problems: every polynomial here is CYCLIC in (x, y, z), so on the diagonal
    /// the three partials are equal and <c>|grad F| = |F_diag'(t)| / sqrt(3)</c>. Checked as a
    /// structural identity on all eight kinds — including Fischer–Koch S, where the lemma holds
    /// and simply does not reach the global maximum.
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void OnTheDiagonal_TheThreePartialsAreEqual(TpmsKind kind)
    {
        var surface = TpmsSurface.For(kind);
        double worst = 0;
        for (int i = 0; i < 400; i++)
        {
            double t = 2 * Math.PI * i / 400.0;
            var g = Gradient(surface, t, t, t);
            worst = Math.Max(worst, Math.Max(Math.Abs(g.X - g.Y), Math.Abs(g.Y - g.Z)));

            // ... and the lemma's consequence: the norm is the diagonal derivative over sqrt 3.
            const double H = 1e-6;
            double slope = (surface.Value(t + H, t + H, t + H) - surface.Value(t - H, t - H, t - H)) / (2 * H);
            Assert.Equal(Math.Abs(slope) / Math.Sqrt(3), g.Length, 6);
        }
        output.WriteLine($"{kind,-14} worst partial disagreement on the diagonal {worst:E3}");
        // The floor is the central difference's own round-off, 2·eps·|F| / step, which is a
        // few times 1e-9 for the surfaces whose polynomial reaches 6 or 7 (I-WP, Neovius) —
        // an absolute bar tighter than that would be measuring the probe rather than the claim.
        Assert.True(worst < 1e-8, $"{kind}: the diagonal partials differ by {worst:R}");
    }

    /// <summary>
    /// Fischer–Koch S's own family, which the diagonal lemma does not reach: on
    /// <c>(t + 3π/2, t, π/4)</c> the polynomial vanishes identically — so its maximum sits ON
    /// the surface, as Schwarz P's does — and <c>|grad F|²</c> collapses to the degree-6
    /// polynomial <c>G(sin t)</c>. Both halves are asserted, because the reduction is the step
    /// that turns a three-variable optimisation into a quartic root.
    /// </summary>
    [Fact]
    public void FischerKochS_ReducesToOnePolynomialOnItsOwnFamily()
    {
        var surface = TpmsSurface.For(TpmsKind.FischerKochS);
        double worstValue = 0, worstGradient = 0;
        for (int i = 0; i < 2000; i++)
        {
            double t = 2 * Math.PI * i / 2000.0;
            double x = t + 3 * Math.PI / 2, y = t, z = Math.PI / 4;
            worstValue = Math.Max(worstValue, Math.Abs(surface.Value(x, y, z)));
            worstGradient = Math.Max(
                worstGradient, Math.Abs(Gradient(surface, x, y, z).Length - Math.Sqrt(G(Math.Sin(t)))));
        }
        output.WriteLine($"Fischer-Koch S on its family: |F| <= {worstValue:E3}, " +
                         $"| |grad F| - sqrt(G(sin t)) | <= {worstGradient:E3}");
        Assert.True(worstValue < 1e-12, "F does not vanish on the family");
        Assert.True(worstGradient < 1e-9, "the reduction to G(sin t) does not hold");
    }

    /// <summary>
    /// The quartic really is the one that matters: it has exactly one root in (0, 1), that root
    /// maximizes G, and the quintic it came from factors as stated. A quartic is solvable in
    /// radicals, which is what makes the constant a closed form rather than a measurement.
    /// </summary>
    [Fact]
    public void FischerKochS_MaximizerIsTheQuarticsRootInTheUnitInterval()
    {
        // The quintic G'(u) = sqrt(2)(3v^5 + 10v^4 - 4v^3 - 18v^2 - 3v + 4) at v = sqrt(2) u,
        // and its stated factorization. Asserted as an identity over the whole line, since two
        // polynomials agreeing at more points than their degree ARE the same polynomial.
        for (int i = -60; i <= 60; i++)
        {
            double v = i / 10.0;
            double quintic = 3 * Pow(v, 5) + 10 * Pow(v, 4) - 4 * Pow(v, 3) - 18 * v * v - 3 * v + 4;
            // Relative, because a degree-5 polynomial at v = 6 is in the thousands and an
            // absolute band there would be a statement about the magnitude, not the identity.
            double scale = Math.Max(1, Math.Abs(quintic));
            Assert.True(Math.Abs(quintic - (v + 1) * Quartic(v)) <= 1e-12 * scale);
            Assert.True(Math.Abs(Math.Sqrt(2) * quintic - GPrime(v / Math.Sqrt(2))) <= 1e-12 * scale);
        }

        double root = QuarticRootInUnitInterval();
        Assert.Equal(0.3969844262244973, root, 12);
        Assert.Equal(0, Quartic(root), 12);

        // It is the maximizer, not merely a critical point: nothing on the family beats it.
        double u = root / Math.Sqrt(2);
        double best = G(u);
        for (int i = 0; i <= 200000; i++)
            Assert.True(G(-1 + 2.0 * i / 200000) <= best + 1e-12);

        output.WriteLine($"Fischer-Koch S: v* = {root:R}, sup |grad F| = {Math.Sqrt(best):R}");
        Assert.Equal(2.4439726372930344, Math.Sqrt(best), 12);
    }

    // ---- the derived closed forms ----

    /// <summary>The supremum of <c>|grad F|</c>, in closed form, per the derivations recorded
    /// in <c>Tpms.cs</c>.</summary>
    private static double DerivedSupremum(TpmsKind kind) => kind switch
    {
        TpmsKind.SchwarzP or TpmsKind.SchwarzD or TpmsKind.Gyroid => Math.Sqrt(3),
        TpmsKind.Neovius => 7,
        TpmsKind.IwP => 3 * Math.Sqrt(3),
        // The diagonal lemma, spelled from each surface's own (a, b, e).
        TpmsKind.Lidinoid => DiagonalSupremum(a: 0.5, b: -0.5, e: 0),
        TpmsKind.SplitP => DiagonalSupremum(a: 1.1, b: -0.2, e: -0.4),
        TpmsKind.FischerKochS => Math.Sqrt(G(QuarticRootInUnitInterval() / Math.Sqrt(2))),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// <c>2 sqrt(3) |sin 2t| |A cos 2t + E|</c> maximized over t, with A = a − 2b and E = −e:
    /// the critical cosine solves <c>2A c² + E c − A = 0</c>.
    /// </summary>
    private static double DiagonalSupremum(double a, double b, double e)
    {
        double bigA = a - 2 * b, bigE = -e;
        double c = (-bigE + Math.Sqrt(bigE * bigE + 8 * bigA * bigA)) / (4 * bigA);
        return 2 * Math.Sqrt(3) * Math.Sqrt(1 - c * c) * Math.Abs(bigA * c + bigE);
    }

    /// <summary>Fischer–Koch S's family polynomial: <c>|grad F|²</c> at <c>u = sin t</c>.</summary>
    private static double G(double u)
    {
        double r2 = Math.Sqrt(2);
        return 4 * Pow(u, 6) + 8 * r2 * Pow(u, 5) - 4 * Pow(u, 4)
             - 12 * r2 * Pow(u, 3) - 3 * u * u + 4 * r2 * u + 5;
    }

    private static double GPrime(double u)
    {
        double r2 = Math.Sqrt(2);
        return 24 * Pow(u, 5) + 40 * r2 * Pow(u, 4) - 16 * Pow(u, 3)
             - 36 * r2 * u * u - 6 * u + 4 * r2;
    }

    private static double Quartic(double v) =>
        3 * Pow(v, 4) + 7 * Pow(v, 3) - 11 * v * v - 7 * v + 4;

    /// <summary>The unique root in (0, 1) — the quartic is +4 at 0 and −4 at 1, so a bracketed
    /// bisection needs no seed and converges to machine precision.</summary>
    private static double QuarticRootInUnitInterval()
    {
        double lo = 0, hi = 1;
        for (int i = 0; i < 200; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (Quartic(mid) > 0)
                lo = mid;
            else
                hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    private static double Pow(double v, int n)
    {
        double r = 1;
        for (int i = 0; i < n; i++)
            r *= v;
        return r;
    }

    // ---- the measurement the closed forms are checked against ----

    private static Vector3d Gradient(TpmsSurface surface, double x, double y, double z)
    {
        const double H = 1e-6;
        return new Vector3d(
            (surface.Value(x + H, y, z) - surface.Value(x - H, y, z)) / (2 * H),
            (surface.Value(x, y + H, z) - surface.Value(x, y - H, z)) / (2 * H),
            (surface.Value(x, y, z + H) - surface.Value(x, y, z - H)) / (2 * H));
    }

    /// <summary>A dense scan over the fundamental cell plus a shrinking-step climb from the
    /// best sample — the maxima are isolated points, so a grid alone under-reports them.</summary>
    private static (double Value, Vector3d At) GlobalSupremum(TpmsKind kind)
    {
        var surface = TpmsSurface.For(kind);
        const int Resolution = 44;
        double step = 2 * Math.PI / Resolution;
        double best = 0;
        var at = Vector3d.Zero;

        for (int i = 0; i < Resolution; i++)
            for (int j = 0; j < Resolution; j++)
                for (int k = 0; k < Resolution; k++)
                {
                    var p = new Vector3d(step * (i + 0.5), step * (j + 0.5), step * (k + 0.5));
                    double v = Gradient(surface, p.X, p.Y, p.Z).Length;
                    if (v > best)
                    {
                        best = v;
                        at = p;
                    }
                }

        double h = step;
        while (h > 1e-13)
        {
            bool moved = false;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0)
                            continue;
                        var q = at + new Vector3d(dx * h, dy * h, dz * h);
                        double v = Gradient(surface, q.X, q.Y, q.Z).Length;
                        if (v > best)
                        {
                            best = v;
                            at = q;
                            moved = true;
                        }
                    }
            if (!moved)
                h *= 0.5;
        }
        return (best, at);
    }
}
