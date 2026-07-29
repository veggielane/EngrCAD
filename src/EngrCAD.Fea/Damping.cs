namespace EngrCAD.Fea;

/// <summary>
/// Rayleigh (proportional) damping <c>C = alpha·M + beta·K</c>.
///
/// <para><b>What it buys is that the UNDAMPED modes still diagonalise the damped system.</b>
/// With <c>phi' M phi = I</c> and <c>phi' K phi = diag(omega_n²)</c>, a C built from those two
/// matrices satisfies <c>phi' C phi = diag(alpha + beta·omega_n²)</c> — diagonal, with no
/// coupling between modes — so the equations of motion separate into one scalar oscillator per
/// mode and the modal damping ratio is
/// <c>zeta_n = alpha/(2·omega_n) + beta·omega_n/2</c>. That single identity is the whole
/// reason this form is offered rather than a general damping matrix: it makes damping a
/// property of each mode, which is what modal superposition needs and what a real modal test
/// measures.</para>
///
/// <para><b>The shape of the curve is the thing to understand before choosing alpha and
/// beta.</b> The mass term damps LOW frequencies (it falls as 1/omega) and the stiffness term
/// damps HIGH ones (it rises linearly), so the ratio is a U with its minimum
/// <c>sqrt(alpha·beta)</c> at <c>omega = sqrt(alpha/beta)</c>. Fitting two frequencies
/// therefore pins the curve everywhere, and everything outside the fitted range is damped
/// MORE than either fitted value — often much more. A high-frequency mode under a
/// stiffness-proportional fit can end up over-damped without anyone choosing that, which is
/// why <see cref="RatioAtFrequency"/> exists and why
/// <see cref="HarmonicResponse"/> reports the ratio it used for every mode.</para>
///
/// <para><b>What is NOT covered, stated precisely rather than implied away.</b> Proportional
/// damping is the special case in which one damping matrix happens to be diagonalised by the
/// undamped real modes. Physical damping usually is not: a discrete dashpot, two materials
/// with different loss factors in one model, viscoelasticity, joint friction and structural
/// (hysteretic) damping all give a <c>C</c> that <c>phi' C phi</c> leaves with off-diagonal
/// terms. When that happens the modes of the damped system are no longer the undamped ones —
/// the eigenproblem becomes the QUADRATIC <c>(lambda²M + lambda·C + K) phi = 0</c>, whose
/// eigenvalues and eigenvectors are complex and whose standard solution linearises it into a
/// <c>2n</c>-dimensional state-space problem in a non-symmetric matrix pair. That is a
/// different solver, not a bigger version of this one, and nothing in this project attempts
/// it. Rayleigh damping remains the right model for the case it fits — material damping
/// spread uniformly through a single-material body — and a deliberate approximation
/// everywhere else.</para>
/// </summary>
/// <param name="Alpha">The mass-proportional coefficient, in 1/time.</param>
/// <param name="Beta">The stiffness-proportional coefficient, in time.</param>
public readonly record struct RayleighDamping(double Alpha, double Beta)
{
    /// <summary>No damping at all.</summary>
    public static RayleighDamping None => default;

    /// <summary>
    /// The coefficients that give the stated damping ratios at two stated frequencies — the
    /// only way anyone actually picks them.
    ///
    /// <para>Solving <c>zeta = alpha/(2w) + beta·w/2</c> at two points gives
    /// <c>beta = 2(zeta2·w2 - zeta1·w1)/(w2² - w1²)</c> and
    /// <c>alpha = 2·zeta1·w1 - beta·w1²</c>. Either coefficient can come out NEGATIVE, which
    /// is not a numerical accident but a statement that no U-shaped curve passes through the
    /// two points asked for — a negative alpha adds energy to the low modes and a negative
    /// beta to the high ones — so it is refused by name rather than returned.</para>
    /// </summary>
    /// <param name="frequency1">The lower fitting frequency, in Hz.</param>
    /// <param name="ratio1">The damping ratio there (0.02 is 2% of critical).</param>
    /// <param name="frequency2">The upper fitting frequency, in Hz.</param>
    /// <param name="ratio2">The damping ratio there.</param>
    public static RayleighDamping FromRatios(
        double frequency1, double ratio1, double frequency2, double ratio2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency2);
        ArgumentOutOfRangeException.ThrowIfNegative(ratio1);
        ArgumentOutOfRangeException.ThrowIfNegative(ratio2);
        if (frequency1 >= frequency2)
            throw new ArgumentException(
                $"The two fitting frequencies must be distinct and ascending; {frequency1} and "
                + $"{frequency2} were given. Two ratios at one frequency do not determine a "
                + "curve, and reversing them silently would fit a different one.",
                nameof(frequency2));

        double w1 = 2.0 * Math.PI * frequency1, w2 = 2.0 * Math.PI * frequency2;
        double beta = 2.0 * (ratio2 * w2 - ratio1 * w1) / (w2 * w2 - w1 * w1);
        double alpha = 2.0 * ratio1 * w1 - beta * w1 * w1;

        if (alpha < 0 || beta < 0)
            throw new ArgumentException(
                $"No Rayleigh curve passes through {ratio1:P2} at {frequency1:N1} Hz and "
                + $"{ratio2:P2} at {frequency2:N1} Hz: the fit gives alpha = {alpha:G6}, "
                + $"beta = {beta:G6}, and a negative coefficient means the damping ADDS energy "
                + "to one end of the spectrum. The ratio curve is a U — alpha/(2w) falling plus "
                + "beta·w/2 rising — so it cannot fall faster than 1/w between the two points. "
                + "Raise the second ratio, lower the first, or move the frequencies apart.");

        return new RayleighDamping(alpha, beta);
    }

    /// <summary>Pure mass-proportional damping giving <paramref name="ratio"/> at
    /// <paramref name="frequency"/> Hz, and MORE damping below it.</summary>
    public static RayleighDamping MassProportional(double frequency, double ratio)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        ArgumentOutOfRangeException.ThrowIfNegative(ratio);
        return new RayleighDamping(2.0 * ratio * 2.0 * Math.PI * frequency, 0.0);
    }

    /// <summary>Pure stiffness-proportional damping giving <paramref name="ratio"/> at
    /// <paramref name="frequency"/> Hz, and MORE damping above it — the form that quietly
    /// over-damps high modes.</summary>
    public static RayleighDamping StiffnessProportional(double frequency, double ratio)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        ArgumentOutOfRangeException.ThrowIfNegative(ratio);
        return new RayleighDamping(0.0, 2.0 * ratio / (2.0 * Math.PI * frequency));
    }

    /// <summary>The modal damping ratio at a circular frequency,
    /// <c>alpha/(2w) + beta·w/2</c>.</summary>
    public double RatioAt(double angularFrequency)
    {
        // Exact-zero division guard, and a semantic one: a rigid-body mode has no stiffness
        // to damp and the mass term's 1/w is infinite there, so the honest answer for w = 0 is
        // that mass-proportional damping does not define a RATIO (it defines a force). The
        // ratio is what every consumer here wants, so zero is returned and the rigid-mode case
        // is handled where it belongs, by not superposing rigid modes at all.
        if (angularFrequency <= 0)
            return 0;
        return Alpha / (2.0 * angularFrequency) + Beta * angularFrequency / 2.0;
    }

    /// <summary>The modal damping ratio at a frequency in Hz.</summary>
    public double RatioAtFrequency(double hertz) => RatioAt(2.0 * Math.PI * hertz);

    /// <summary>The lowest ratio the curve reaches, <c>sqrt(alpha·beta)</c>. Zero when either
    /// coefficient is zero, since one term alone is monotone and has no minimum.</summary>
    public double MinimumRatio => Math.Sqrt(Alpha * Beta);

    /// <summary>The frequency in Hz at which <see cref="MinimumRatio"/> occurs, or null when
    /// either coefficient is zero.</summary>
    public double? FrequencyOfMinimumRatio =>
        // Exact-zero division guard: a pure mass- or stiffness-proportional curve is monotone
        // and its minimum is at one end of the spectrum rather than at an interior frequency.
        Beta > 0 && Alpha > 0 ? Math.Sqrt(Alpha / Beta) / (2.0 * Math.PI) : null;

    /// <inheritdoc/>
    public override string ToString() =>
        $"Rayleigh(alpha {Alpha:G6}, beta {Beta:G6})"
        + (FrequencyOfMinimumRatio is { } f
            ? $", minimum {MinimumRatio:P3} at {f:N1} Hz"
            : "");
}

/// <summary>
/// How much each mode is damped — the only thing modal superposition needs to know about
/// damping, and deliberately a per-mode RATIO rather than a matrix.
///
/// <para>Once a damping model has been agreed to be proportional (see
/// <see cref="RayleighDamping"/> for exactly what that means and what it excludes), the matrix
/// <c>C</c> never has to be assembled at all: every consumer wants
/// <c>zeta_n</c>, and forming C in order to project it back down to the same numbers would be
/// arithmetic with nothing to show for it. So no damping matrix exists in this project, and
/// that is a design statement rather than an omission.</para>
/// </summary>
public abstract class ModalDamping
{
    /// <summary>The damping ratio for one mode: 0 is undamped, 1 is critical.</summary>
    /// <param name="number">The mode's 1-based number.</param>
    /// <param name="angularFrequency">Its circular natural frequency, in rad/time.</param>
    public abstract double RatioForMode(int number, double angularFrequency);

    /// <summary>A readable description, for a report.</summary>
    public abstract string Describe();

    /// <inheritdoc/>
    public override string ToString() => Describe();

    /// <summary>No damping. Every response is real and every resonance is infinite, which is
    /// the correct answer to the question asked and almost never the question meant.</summary>
    public static ModalDamping None { get; } = new UniformDamping(0.0);

    /// <summary>The same ratio for every mode — what a modal test usually reports and what a
    /// design code usually specifies (2% for welded steel, 5% for bolted structures).</summary>
    public static ModalDamping Uniform(double ratio)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ratio);
        return new UniformDamping(ratio);
    }

    /// <summary>Rayleigh damping's per-mode ratios,
    /// <c>alpha/(2·omega_n) + beta·omega_n/2</c>.</summary>
    public static ModalDamping Rayleigh(RayleighDamping damping) => new RayleighModalDamping(damping);

    /// <summary>Rayleigh damping fitted to two frequencies — sugar over
    /// <see cref="RayleighDamping.FromRatios"/>.</summary>
    public static ModalDamping Rayleigh(
        double frequency1, double ratio1, double frequency2, double ratio2) =>
        Rayleigh(RayleighDamping.FromRatios(frequency1, ratio1, frequency2, ratio2));

    /// <summary>
    /// One ratio per mode, in mode order — the form a measured modal survey comes in.
    /// Modes beyond the list take <paramref name="beyond"/>, which is stated rather than
    /// extrapolated because extrapolating a measured table is a modelling choice the table
    /// does not contain.
    /// </summary>
    public static ModalDamping PerMode(IReadOnlyList<double> ratios, double beyond = 0.0)
    {
        ArgumentNullException.ThrowIfNull(ratios);
        ArgumentOutOfRangeException.ThrowIfNegative(beyond);
        foreach (double r in ratios)
            ArgumentOutOfRangeException.ThrowIfNegative(r);
        return new TabulatedDamping([.. ratios], beyond);
    }

    private sealed class UniformDamping(double ratio) : ModalDamping
    {
        public override double RatioForMode(int number, double angularFrequency) => ratio;

        public override string Describe() =>
            ratio == 0 ? "undamped" : $"uniform modal damping {ratio:P3}";
    }

    private sealed class RayleighModalDamping(RayleighDamping damping) : ModalDamping
    {
        public override double RatioForMode(int number, double angularFrequency) =>
            damping.RatioAt(angularFrequency);

        public override string Describe() => damping.ToString();
    }

    private sealed class TabulatedDamping(double[] ratios, double beyond) : ModalDamping
    {
        public override double RatioForMode(int number, double angularFrequency) =>
            number >= 1 && number <= ratios.Length ? ratios[number - 1] : beyond;

        public override string Describe() =>
            $"per-mode damping ({string.Join(", ", ratios.Select(r => r.ToString("P2")))}"
            + $"; {beyond:P2} beyond mode {ratios.Length})";
    }
}
