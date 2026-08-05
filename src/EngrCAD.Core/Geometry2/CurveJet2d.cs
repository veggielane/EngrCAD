namespace EngrCAD.Core.Geometry2;

/// <summary>
/// The local GRAPH JET of a curve branch leaving a point: the Taylor coefficients
/// a₂, a₃, … of <c>y = f(x)</c> in the branch's own tangent frame, where x runs along the
/// departure tangent and y along its left normal. This is what orders two edges that leave a
/// node with the SAME tangent, and it is what lets <see cref="CurvedArrangement2d"/> carry
/// cubic Béziers rather than only lines and arcs.
///
/// <para><b>Why a bounded jet is SOUND here, when it is not in general.</b> Two distinct
/// real-analytic branches can agree to any finite order, so "compare derivatives up to N" is
/// not a decision procedure for arbitrary curves — design.md said so, and for transcendental
/// curves it is right. But this tier's carriers are all ALGEBRAIC of low degree: a line is
/// implicit degree 1, a circle 2, and a cubic Bézier at most 3. Two plane algebraic curves
/// with no common component meet with total intersection multiplicity exactly d₁·d₂
/// (Bézout), and contact of order m between two smooth branches contributes multiplicity m
/// at that point alone. So two branches of this tier that agree through order d₁·d₂ ≤ 9 CANNOT
/// be distinct carriers — they share a component. The jet is therefore complete at
/// <see cref="Order"/>, and a tie through it is a statement about the input ("these two edges
/// lie on one carrier") rather than a failure of the comparison.</para>
///
/// <para><b>It degenerates to the incumbent rule exactly.</b> For a line every coefficient is
/// zero; for an arc a₂ = κ/2 and the rest are functions of κ alone. So two arcs tie at a₂ iff
/// they tie at every order iff they are the same circle, and a line never ties with an arc at
/// a₂ unless the arc is straight — which is precisely the completeness argument the
/// lines-and-arcs tier already stood on, now stated as the k = 2 case of a general one.</para>
///
/// <para><b>How it is computed.</b> Each branch's position is expanded as a truncated power
/// series in its OWN parameter about the departure point (exact for all three shapes: a line
/// is degree 1, a cubic is degree 3, and a circle's series is the Taylor series of sine and
/// cosine). The series is rotated so its linear term points along +x — so a₁ is exactly zero
/// and no shared frame has to be agreed between the two branches — then x(τ) is REVERTED to
/// τ(x) and y(τ(x)) composed, which gives f's coefficients. Reversion and composition are
/// ordinary truncated-series arithmetic; nothing is fitted and no tolerance enters.</para>
/// </summary>
public static class CurveJet2d
{
    /// <summary>
    /// Highest graph coefficient computed. Bézout bounds the contact order of two distinct
    /// carriers in this tier by 3·3 = 9, so a difference must appear at or before a₉; one
    /// spare order is carried so the bound is reached rather than approached.
    /// </summary>
    public const int Order = 10;

    /// <summary>Number of comparable coefficients, a₂ … a_Order.</summary>
    public const int Coefficients = Order - 1;

    /// <summary>
    /// The graph coefficients a₂ … a_Order of the branch of <paramref name="edge"/> leaving
    /// its start (<paramref name="atStart"/>) or its end, written into
    /// <paramref name="into"/> (length <see cref="Coefficients"/>). A branch leaving the END
    /// travels with the parameter DECREASING, which is the reversed curve — so the sign
    /// convention needs no second code path.
    /// </summary>
    public static void GraphCoefficients(in CurvedEdge2d edge, bool atStart, Span<double> into)
    {
        Span<double> x = stackalloc double[Order + 1];
        Span<double> y = stackalloc double[Order + 1];
        LocalSeries(edge, atStart, x, y);
        GraphFromSeries(x, y, into);
    }

    /// <summary>
    /// Position series of the departing branch in world axes: p(τ) = Σ (x_k, y_k) τ^k about
    /// the departure point, with τ ≥ 0 travelling INTO the edge.
    /// </summary>
    private static void LocalSeries(
        in CurvedEdge2d edge, bool atStart, Span<double> x, Span<double> y)
    {
        x.Clear();
        y.Clear();
        var branch = atStart ? edge : edge.Reversed();
        switch (branch.Kind)
        {
            case CurvedEdgeKind.Arc:
            {
                // p(τ) = C + r·(cos(θ₀ + στ), sin(θ₀ + στ)); the Taylor coefficients of sine
                // and cosine are exact rationals times powers of the angular rate.
                double theta = branch.StartAngle;
                double rate = branch.SweepAngle;
                double c = Math.Cos(theta), s = Math.Sin(theta);
                double power = 1;
                double factorial = 1;
                for (int k = 0; k <= Order; k++)
                {
                    // d^k/dτ^k of cos(θ + στ) is σ^k · cos(θ + kπ/2).
                    (double dc, double ds) = (k % 4) switch
                    {
                        0 => (c, s),
                        1 => (-s, c),
                        2 => (-c, -s),
                        _ => (s, -c),
                    };
                    x[k] = branch.Radius * power * dc / factorial;
                    y[k] = branch.Radius * power * ds / factorial;
                    power *= rate;
                    factorial *= k + 1;
                }
                x[0] = branch.Start.X;
                y[0] = branch.Start.Y;
                break;
            }

            case CurvedEdgeKind.Bezier:
            {
                var (p0, p1, p2, p3) = branch.ControlPoints;
                var c1 = (p1 - p0) * 3;
                var c2 = (p0 - p1 * 2 + p2) * 3;
                var c3 = p3 - p0 + (p1 - p2) * 3;
                x[0] = p0.X; x[1] = c1.X; x[2] = c2.X; x[3] = c3.X;
                y[0] = p0.Y; y[1] = c1.Y; y[2] = c2.Y; y[3] = c3.Y;
                break;
            }

            default:
            {
                var direction = branch.End - branch.Start;
                x[0] = branch.Start.X; x[1] = direction.X;
                y[0] = branch.Start.Y; y[1] = direction.Y;
                break;
            }
        }
    }

    /// <summary>
    /// Rotates the series so its linear term is +x, reverts x(τ) and composes y — giving the
    /// coefficients of y as a function of x, with a₀ = a₁ = 0 by construction.
    /// </summary>
    private static void GraphFromSeries(Span<double> x, Span<double> y, Span<double> into)
    {
        into.Clear();
        double dx = x[1], dy = y[1];
        double speed = Math.Sqrt(dx * dx + dy * dy);
        // Exact-zero guard: a cusp has no departure frame, and a caller that reached here
        // with one has already been given a direction by TangentAt's l'Hopital fallback —
        // reporting a flat jet keeps the sort total rather than producing NaNs.
        if (!(speed > 0))
            return;
        double ux = dx / speed, uy = dy / speed;

        Span<double> a = stackalloc double[Order + 1];
        Span<double> b = stackalloc double[Order + 1];
        for (int k = 1; k <= Order; k++)
        {
            // Rotate by −atan2(uy, ux): (X, Y) -> (X·ux + Y·uy, −X·uy + Y·ux). The constant
            // term is dropped, which puts the departure point at the origin exactly.
            a[k] = x[k] * ux + y[k] * uy;
            b[k] = -x[k] * uy + y[k] * ux;
        }
        a[0] = 0;
        b[0] = 0;

        Span<double> inverse = stackalloc double[Order + 1];
        Revert(a, inverse);
        Span<double> graph = stackalloc double[Order + 1];
        Compose(b, inverse, graph);
        for (int k = 2; k <= Order; k++)
            into[k - 2] = graph[k];
    }

    /// <summary>
    /// Series reversion: given <c>x(τ) = Σ_{k≥1} a_k τ^k</c> with a₁ ≠ 0, the coefficients of
    /// <c>τ(x) = Σ_{k≥1} g_k x^k</c>. Solved order by order — at order n the composition
    /// x(τ(x)) contains g_n only through the linear term a₁·g_n, so g_n falls out of the
    /// residual with no iteration.
    /// </summary>
    private static void Revert(ReadOnlySpan<double> a, Span<double> g)
    {
        g.Clear();
        g[1] = 1 / a[1];
        Span<double> composed = stackalloc double[Order + 1];
        for (int n = 2; n <= Order; n++)
        {
            Compose(a, g, composed);     // g[n] is still 0 here, so this is the residual
            g[n] = -composed[n] / a[1];
        }
    }

    /// <summary>Truncated composition f(g(x)) for series with g₀ = 0, by Horner over the
    /// series product.</summary>
    private static void Compose(ReadOnlySpan<double> f, ReadOnlySpan<double> g, Span<double> into)
    {
        Span<double> accumulator = stackalloc double[Order + 1];
        Span<double> scratch = stackalloc double[Order + 1];
        accumulator.Clear();
        for (int k = Order; k >= 0; k--)
        {
            Multiply(accumulator, g, scratch);
            scratch[0] += f[k];
            scratch.CopyTo(accumulator);
        }
        accumulator.CopyTo(into);
    }

    /// <summary>Truncated product of two series.</summary>
    private static void Multiply(ReadOnlySpan<double> p, ReadOnlySpan<double> q, Span<double> into)
    {
        into.Clear();
        for (int i = 0; i <= Order; i++)
        {
            if (p[i] == 0)
                continue;
            for (int j = 0; i + j <= Order; j++)
                into[i + j] += p[i] * q[j];
        }
    }
}
