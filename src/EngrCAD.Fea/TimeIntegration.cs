namespace EngrCAD.Fea;

/// <summary>
/// A time-integration scheme for <c>M·a + C·v + K·u = f(t)</c>: the Newmark pair
/// <c>(beta, gamma)</c> together with the HHT <c>alpha</c> that weights the internal and
/// damping forces between the two ends of a step.
///
/// <para><b>One value carries the whole scheme, deliberately.</b> The alternative — an enum
/// beside two loose doubles — lets a caller state a member name and a pair of coefficients
/// that contradict it, and lets the solver decide which to believe. Here every member is a
/// named factory that computes its own coefficients, so a scheme is either one of the
/// families below or a <see cref="Newmark"/> pair the constructor has checked.</para>
///
/// <para><b>The stability rule is <c>2·beta &gt;= gamma &gt;= 1/2</c></b>, and both halves are
/// enforced rather than documented. <c>gamma &lt; 1/2</c> gives NEGATIVE numerical damping —
/// the amplitude grows step after step at every step size, so it is not a trade between
/// accuracy and cost but a wrong answer that looks like a resonance. <c>2·beta &lt; gamma</c>
/// is conditionally stable, and this solver has no way to tell a caller their step is safe:
/// the critical step is a fraction of the shortest period in the DISCRETE system, which is
/// the largest eigenvalue of a generalised problem nothing here computes. Both are refused
/// by name, and <see cref="CentralDifference"/> explains where the explicit family would
/// live instead.</para>
///
/// <para><b>Second-order accuracy needs <c>gamma = 1/2</c> exactly</b> — anything else is
/// first order, whatever beta is. That is the price of numerical damping in the Newmark
/// family and the whole reason HHT exists: HHT keeps <c>gamma = 1/2 - alpha</c> but
/// compensates with the alpha-weighted equilibrium, so it damps the high modes AND stays
/// second order. See <see cref="HilberHughesTaylor"/>.</para>
/// </summary>
public readonly record struct TimeIntegration
{
    private TimeIntegration(double beta, double gamma, double alpha, string name)
    {
        Beta = beta;
        Gamma = gamma;
        Alpha = alpha;
        Name = name;
    }

    /// <summary>Newmark's <c>beta</c>: how much of the end-of-step acceleration the
    /// displacement update carries.</summary>
    public double Beta { get; }

    /// <summary>Newmark's <c>gamma</c>: the same for the velocity update. Exactly 1/2 is
    /// second-order accurate and neutrally damped; more is first-order and dissipative.</summary>
    public double Gamma { get; }

    /// <summary>
    /// The HHT weighting, in <c>[-1/3, 0]</c>; exactly 0 is plain Newmark.
    ///
    /// <para>The convention is Hughes': the step enforces
    /// <c>M·a(n+1) + (1+alpha)·C·v(n+1) - alpha·C·v(n) + (1+alpha)·K·u(n+1) - alpha·K·u(n)
    /// = f(t(n+1) + alpha·dt)</c>. Sign conventions for alpha differ between references —
    /// some use <c>1-alpha</c> with a positive alpha — so the equation is written out here
    /// rather than named, and <c>alpha = 0</c> reducing to Newmark's average acceleration is
    /// asserted as an identity rather than assumed.</para>
    /// </summary>
    public double Alpha { get; }

    /// <summary>A short name for a report.</summary>
    public string Name { get; }

    /// <summary>
    /// Newmark's constant-average-acceleration member, <c>beta = 1/4, gamma = 1/2</c> — the
    /// trapezoidal rule, and the default.
    ///
    /// <para><b>It conserves energy EXACTLY for an undamped linear system</b>, which is why it
    /// is the default and why this project can check it as an identity rather than as a trend.
    /// The update relations collapse to
    /// <c>u(n+1) - u(n) = (dt/2)(v(n) + v(n+1))</c> and
    /// <c>v(n+1) - v(n) = (dt/2)(a(n) + a(n+1))</c>, so
    /// <c>E(n+1) - E(n) = (dt/4)·(v(n)+v(n+1))'·[M(a(n)+a(n+1)) + K(u(n)+u(n+1))]</c>,
    /// and the bracket is the equation of motion at both ends: exactly zero with no load and
    /// no damping. The same algebra with the load and damping terms kept gives the general
    /// balance <see cref="TransientSolveReport.EnergyBalanceResidual"/> measures.</para>
    ///
    /// <para>Unconditionally stable, second-order accurate, and neutrally damped: nothing
    /// decays that the physics does not decay. The last property is a cost as well as a
    /// virtue — the highest modes of a finite element mesh are numerical artefacts with no
    /// physical content, and this scheme keeps every one of them ringing for ever. That is
    /// what <see cref="HilberHughesTaylor"/> is for.</para>
    /// </summary>
    public static TimeIntegration AverageAcceleration { get; } =
        new(0.25, 0.5, 0.0, "Newmark average acceleration (beta 1/4, gamma 1/2)");

    /// <summary>
    /// An arbitrary Newmark pair, refused unless <c>2·beta &gt;= gamma &gt;= 1/2</c>.
    /// </summary>
    /// <param name="beta">Newmark's beta.</param>
    /// <param name="gamma">Newmark's gamma.</param>
    public static TimeIntegration Newmark(double beta, double gamma)
    {
        if (!double.IsFinite(beta) || !double.IsFinite(gamma))
            throw new ArgumentException(
                $"Newmark's coefficients must be finite; beta = {beta}, gamma = {gamma}.");
        if (gamma < 0.5)
            throw new ArgumentOutOfRangeException(
                nameof(gamma), gamma,
                "Newmark needs gamma >= 1/2. Below it the scheme has NEGATIVE numerical "
                + "damping: the amplitude grows step after step at EVERY step size, so it is "
                + "not a speed-for-accuracy trade but an answer that diverges while looking "
                + "like a resonance. Use gamma = 1/2 for the neutral, second-order member "
                + "(TimeIntegration.AverageAcceleration), or gamma > 1/2 for numerical damping "
                + "at first order (TimeIntegration.NumericallyDamped).");
        if (2.0 * beta < gamma)
            throw new ArgumentOutOfRangeException(
                nameof(beta), beta,
                $"Newmark is unconditionally stable only for 2·beta >= gamma, and "
                + $"2·{beta} < {gamma}. The conditionally stable members (central difference "
                + "at beta = 0, linear acceleration at beta = 1/6) are legitimate schemes, but "
                + "their stable step is a fraction of the shortest period in the DISCRETE "
                + "system - the largest eigenvalue of K·phi = lambda·M·phi, which nothing here "
                + "computes - so this solver cannot tell you whether your step is safe and will "
                + "not run a scheme whose answer silently explodes when it is not. Raise beta "
                + $"to at least {gamma / 2.0:G6}.");
        return new TimeIntegration(beta, gamma, 0.0, $"Newmark (beta {beta:G6}, gamma {gamma:G6})");
    }

    /// <summary>
    /// The numerically damped Newmark member for a stated <paramref name="gamma"/> above 1/2,
    /// taking the standard <c>beta = (gamma + 1/2)²/4</c>.
    ///
    /// <para>That beta is the one that makes the scheme unconditionally stable AND puts the
    /// two roots of the amplification polynomial together, which is the least oscillatory
    /// decay available at that gamma. <b>The accuracy cost is not a nuance</b>: any
    /// <c>gamma != 1/2</c> is FIRST order in the step, so halving the step halves the error
    /// instead of quartering it. <see cref="HilberHughesTaylor"/> buys the same high-frequency
    /// dissipation without that, and is the better tool unless you specifically want Newmark's
    /// own amplification.</para>
    /// </summary>
    public static TimeIntegration NumericallyDamped(double gamma)
    {
        if (gamma <= 0.5)
            throw new ArgumentOutOfRangeException(
                nameof(gamma), gamma,
                "Numerical damping needs gamma > 1/2; at exactly 1/2 the scheme is neutrally "
                + "damped and TimeIntegration.AverageAcceleration is that member by name.");
        double beta = (gamma + 0.5) * (gamma + 0.5) / 4.0;
        var scheme = Newmark(beta, gamma);
        return new TimeIntegration(
            scheme.Beta, scheme.Gamma, 0.0,
            $"Newmark with numerical damping (gamma {gamma:G6}, beta {beta:G6}; FIRST order)");
    }

    /// <summary>
    /// Hilber-Hughes-Taylor alpha, with <c>beta = (1-alpha)²/4</c> and
    /// <c>gamma = (1-2·alpha)/2</c> — <b>second-order accurate AND dissipative in the high
    /// modes</b>, which no Newmark member manages at once.
    ///
    /// <para>The mechanism is worth stating because it is what makes the accuracy survive.
    /// Newmark buys dissipation by raising gamma, which unbalances the velocity update and
    /// costs an order. HHT keeps the update relations and instead evaluates the INTERNAL and
    /// DAMPING forces at a weighted point between the two ends of the step,
    /// <c>(1+alpha)·(...)(n+1) - alpha·(...)(n)</c>, with the load at the matching instant
    /// <c>t(n+1) + alpha·dt</c>. The gamma it then chooses is above 1/2, but the alpha
    /// weighting cancels the leading error term the raised gamma would introduce.</para>
    ///
    /// <para><b>The range is [-1/3, 0] and it is enforced.</b> At alpha = 0 the scheme IS
    /// <see cref="AverageAcceleration"/> (beta = 1/4, gamma = 1/2 fall out of the formulas),
    /// which is asserted as an identity in the tests rather than trusted. At alpha = -1/3 the
    /// high-frequency spectral radius reaches 1/2, the most dissipation the family offers;
    /// below that it loses second-order accuracy. Positive alpha would AMPLIFY.</para>
    /// </summary>
    /// <param name="alpha">The weighting, in <c>[-1/3, 0]</c>.</param>
    public static TimeIntegration HilberHughesTaylor(double alpha)
    {
        if (!double.IsFinite(alpha) || alpha < -1.0 / 3.0 || alpha > 0)
            throw new ArgumentOutOfRangeException(
                nameof(alpha), alpha,
                "HHT's alpha must lie in [-1/3, 0]. Zero is Newmark's average acceleration "
                + "exactly; -1/3 damps the highest modes hardest (spectral radius 1/2 as "
                + "omega·dt grows). Below -1/3 the scheme drops to first order, and a POSITIVE "
                + "alpha amplifies rather than damps - it would grow the very modes the method "
                + "exists to remove.");
        double beta = (1.0 - alpha) * (1.0 - alpha) / 4.0;
        double gamma = (1.0 - 2.0 * alpha) / 2.0;
        return new TimeIntegration(
            beta, gamma, alpha,
            alpha == 0
                ? AverageAcceleration.Name
                : $"HHT-alpha ({alpha:G6}; rho_inf {(1.0 + alpha) / (1.0 - alpha):G4})");
    }

    /// <summary>
    /// The HHT member whose high-frequency spectral radius is <paramref name="spectralRadius"/>,
    /// from <c>alpha = (rho - 1)/(rho + 1)</c>.
    ///
    /// <para>This is the parameter a user actually has an opinion about: <c>rho</c> is the
    /// factor by which one step multiplies the amplitude of a mode so fast that
    /// <c>omega·dt</c> is effectively infinite — 1 keeps them for ever, 0.5 removes them in a
    /// few steps, and the mid-range is the usual choice for a mesh whose top modes are
    /// discretization artefacts. The relation is exact for this family and is <b>measured</b>
    /// in the tests (a single mode driven at <c>omega·dt = 1000</c> decays by exactly this
    /// factor per step) rather than transcribed and trusted.</para>
    /// </summary>
    /// <param name="spectralRadius">The asymptotic amplitude factor per step, in
    /// <c>[1/2, 1]</c>.</param>
    public static TimeIntegration ForSpectralRadius(double spectralRadius)
    {
        if (!double.IsFinite(spectralRadius) || spectralRadius < 0.5 || spectralRadius > 1.0)
            throw new ArgumentOutOfRangeException(
                nameof(spectralRadius), spectralRadius,
                "The high-frequency spectral radius must lie in [1/2, 1]: 1 is neutral "
                + "(Newmark's average acceleration) and 1/2 is the most dissipation the "
                + "second-order HHT family reaches, at alpha = -1/3. A smaller radius needs "
                + "the generalized-alpha family, which carries a second parameter and is not "
                + "offered here.");
        // Exact-1 test rather than a tolerance: the caller asking for a neutral scheme by
        // name should get the same value AverageAcceleration is, bit for bit, so a report
        // and a bit-identity test both read one member.
        if (spectralRadius == 1.0)
            return AverageAcceleration;
        return HilberHughesTaylor((spectralRadius - 1.0) / (spectralRadius + 1.0));
    }

    /// <summary>
    /// The explicit central-difference scheme, <b>not offered</b>, and the reason is
    /// structural rather than a matter of effort.
    ///
    /// <para>Central difference is <c>beta = 0, gamma = 1/2</c>, and its appeal is that with a
    /// DIAGONAL mass matrix no linear system is solved at all — each step is a division. This
    /// library has no diagonal mass matrix to offer for the element it recommends:
    /// <c>MassLumping.RowSum</c> is refused by name for 10-node tetrahedra because the row
    /// sums are <c>-V/20</c> at every corner node, a negative mass, and
    /// <c>MassLumping.Hrz</c> is available but is a scaled approximation whose error would
    /// then be inseparable from the integrator's. Offering an explicit scheme over a
    /// CONSISTENT mass matrix would mean solving a system every step, which is exactly what
    /// explicit integration exists not to do.</para>
    ///
    /// <para>The second reason is the one above: an explicit scheme is conditionally stable,
    /// and a stable step needs the largest eigenvalue of the discrete system. Both are filed
    /// rather than half-built. This member exists only so the refusal has a name to be found
    /// under.</para>
    /// </summary>
    public static TimeIntegration CentralDifference =>
        throw new NotSupportedException(
            "Explicit central-difference integration is not offered. It pays for itself only "
            + "with a DIAGONAL mass matrix, and this library refuses row-sum lumping for "
            + "10-node tetrahedra by name (the corner row sums are -V/20, a negative mass), so "
            + "an explicit step here would still solve a linear system - which is what explicit "
            + "integration exists to avoid. It is also conditionally stable, and the stable step "
            + "is set by the largest eigenvalue of K·phi = lambda·M·phi, which nothing here "
            + "computes. Use TimeIntegration.AverageAcceleration, which is unconditionally "
            + "stable and costs one factorization for the whole run.");

    /// <summary>True for every scheme this type will construct — both stability conditions
    /// are enforced at construction, so the property is a statement about the type rather
    /// than a test to perform.</summary>
    public bool IsUnconditionallyStable => 2.0 * Beta >= Gamma && Gamma >= 0.5;

    /// <summary>True only for the members with <c>gamma = 1/2</c> exactly (plain Newmark) and
    /// for every HHT member. Exact comparison, deliberately: second-order accuracy is an
    /// algebraic property of the coefficients, not a measurement, and a gamma a hair above
    /// 1/2 really is first order.</summary>
    public bool IsSecondOrder => Alpha != 0 || Gamma == 0.5;

    /// <summary>The asymptotic amplitude factor per step as <c>omega·dt</c> grows: 1 for the
    /// neutrally damped members, <c>(1+alpha)/(1-alpha)</c> for HHT. Null for a numerically
    /// damped Newmark member, whose limit depends on beta as well and is not a single stated
    /// constant of this family.</summary>
    public double? SpectralRadiusAtInfinity =>
        Alpha != 0 ? (1.0 + Alpha) / (1.0 - Alpha)
        : Gamma == 0.5 ? 1.0
        : null;

    /// <inheritdoc/>
    public override string ToString() => Name;
}
