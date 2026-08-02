namespace EngrCAD.Fea;

/// <summary>
/// A stress-life (S-N) curve in Basquin form — <c>sigma_a = sigma'_f · (2N)^b</c>, the
/// alternating stress amplitude a FULLY REVERSED (R = -1) uniaxial test survives for
/// N cycles — plus the ultimate strength the mean-stress corrections need and, for the
/// materials that have one, an endurance knee beyond which the line goes flat.
///
/// <para><b>The coefficients are stored in the form a human can check.</b>
/// <see cref="FatigueStrengthCoefficient"/> is in MPa — which is both this repository's
/// model unit (<c>ModelUnits</c>) and the unit every fatigue datasheet quotes, so unlike
/// the density lesson there is no conversion to get wrong; the transcription test asserts
/// the stored values in exactly that form. <see cref="FatigueStrengthExponent"/> is the
/// dimensionless slope, negative by definition (stress falls with life) and refused
/// otherwise by name.</para>
///
/// <para><b>The endurance limit is DERIVED, never stored beside the line.</b> A steel's
/// limit is the Basquin line evaluated at its own knee
/// (<see cref="EnduranceLife"/>), so the two cannot drift — the same one-source-of-truth
/// rule that keeps the fine-pitch thread table from carrying a tap-drill column. A
/// material with no endurance limit (the aluminium rows) states
/// <see cref="EnduranceLife"/> = null and the line extends indefinitely — that
/// distinction is real metallurgy (steels arrest small cracks below a threshold,
/// face-centred-cubic aluminium does not) and is carried rather than smoothed over.</para>
///
/// <para><b>Validity is high-cycle.</b> Basquin describes the elastic-strain regime;
/// below roughly 10³ cycles plastic strain dominates (Coffin–Manson territory) and the
/// numbers here are extrapolations. The arithmetic still answers — refusing would turn a
/// gross overload into an exception instead of a red node — but a life under 10³ should
/// be read as "fails fast", not as a schedule.</para>
/// </summary>
public sealed class SnCurve
{
    /// <summary>
    /// Creates a curve. See the class remarks for the meaning and units of each value.
    /// </summary>
    /// <param name="name">Material designation, e.g. "SAE 1045 HR".</param>
    /// <param name="fatigueStrengthCoefficient">sigma'_f in MPa — the Basquin line's
    /// stress at ONE reversal (2N = 1).</param>
    /// <param name="fatigueStrengthExponent">b, dimensionless and negative.</param>
    /// <param name="ultimateStrength">S_ut in MPa — the mean-stress corrections' static
    /// anchor, required because the default correction (Goodman) cannot work without
    /// it.</param>
    /// <param name="enduranceLife">The knee in CYCLES beyond which the line is flat at
    /// <see cref="EnduranceLimit"/>, or null for a material with no endurance limit.</param>
    public SnCurve(
        string name,
        double fatigueStrengthCoefficient,
        double fatigueStrengthExponent,
        double ultimateStrength,
        double? enduranceLife = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!(fatigueStrengthCoefficient > 0))
            throw new FeaException(
                $"'{name}': the fatigue strength coefficient (sigma'_f) is a stress and must be "
                + $"positive; {fatigueStrengthCoefficient} MPa was given.");
        if (!(fatigueStrengthExponent < 0))
            throw new FeaException(
                $"'{name}': the fatigue strength exponent (b) is negative by definition — "
                + $"alternating stress falls as life grows — but {fatigueStrengthExponent} was "
                + "given. A positive exponent would report longer lives at higher stress.");
        if (!(ultimateStrength > 0))
            throw new FeaException(
                $"'{name}': the ultimate strength anchors the Goodman and Gerber lines and must "
                + $"be positive; {ultimateStrength} MPa was given.");
        if (enduranceLife is { } knee && !(knee >= 1))
            throw new FeaException(
                $"'{name}': an endurance knee below one cycle ({knee}) is not a fatigue "
                + "quantity. Pass null for a material with no endurance limit.");

        Name = name;
        FatigueStrengthCoefficient = fatigueStrengthCoefficient;
        FatigueStrengthExponent = fatigueStrengthExponent;
        UltimateStrength = ultimateStrength;
        EnduranceLife = enduranceLife;
    }

    /// <summary>Material designation.</summary>
    public string Name { get; }

    /// <summary>sigma'_f in MPa: the Basquin line's stress at one reversal.
    /// <see cref="StressAt"/>(0.5) returns exactly this value, which is the transcription
    /// test's exact half.</summary>
    public double FatigueStrengthCoefficient { get; }

    /// <summary>b, the Basquin slope. Dimensionless, negative.</summary>
    public double FatigueStrengthExponent { get; }

    /// <summary>S_ut in MPa — where the Goodman and Gerber lines meet the mean-stress
    /// axis: at a mean of S_ut the allowable alternating stress is exactly zero.</summary>
    public double UltimateStrength { get; }

    /// <summary>The knee in cycles beyond which the curve is flat, or null when the
    /// material has no endurance limit and the line extends indefinitely.</summary>
    public double? EnduranceLife { get; }

    /// <summary>Whether the material has an endurance limit (steels do, aluminium does
    /// not — a real metallurgical distinction, not a data gap).</summary>
    public bool HasEnduranceLimit => EnduranceLife is not null;

    /// <summary>The endurance limit in MPa — the Basquin line AT its own knee, derived
    /// rather than stored so the two cannot drift. Null for a material without one.</summary>
    public double? EnduranceLimit =>
        EnduranceLife is { } knee ? BasquinStress(knee) : null;

    private double BasquinStress(double cycles) =>
        FatigueStrengthCoefficient * Math.Pow(2 * cycles, FatigueStrengthExponent);

    /// <summary>
    /// The fully reversed alternating stress (MPa) survived for <paramref name="cycles"/>:
    /// the Basquin line up to the knee, the endurance limit beyond it.
    /// </summary>
    public double StressAt(double cycles)
    {
        if (!(cycles > 0))
            throw new ArgumentOutOfRangeException(
                nameof(cycles), cycles, "A life is a positive cycle count.");
        if (EnduranceLife is { } knee && cycles >= knee)
            return BasquinStress(knee);
        return BasquinStress(cycles);
    }

    /// <summary>
    /// Cycles to failure at a fully reversed amplitude of <paramref name="amplitude"/> MPa:
    /// <c>N = ½·(sigma_a / sigma'_f)^(1/b)</c>, or <see cref="double.PositiveInfinity"/> at
    /// or below the endurance limit (and for a zero amplitude, whatever the material —
    /// nothing alternating never accumulates a cycle).
    /// <para>An amplitude at or above sigma'_f returns a life below one reversal — a
    /// statement about gross overload, not a schedule; see the class remarks on
    /// validity.</para>
    /// </summary>
    public double LifeAt(double amplitude)
    {
        if (amplitude < 0)
            throw new ArgumentOutOfRangeException(
                nameof(amplitude), amplitude,
                "An alternating stress amplitude is non-negative by construction.");
        if (amplitude == 0)
            return double.PositiveInfinity;
        if (EnduranceLimit is { } limit && amplitude <= limit)
            return double.PositiveInfinity;
        return 0.5 * Math.Pow(amplitude / FatigueStrengthCoefficient, 1.0 / FatigueStrengthExponent);
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Name}: sigma'_f {FatigueStrengthCoefficient:G6} MPa, b {FatigueStrengthExponent:G4}, "
        + $"S_ut {UltimateStrength:G6} MPa"
        + (EnduranceLimit is { } e ? $", endurance {e:G4} MPa at {EnduranceLife:G2} cycles" : ", no endurance limit");
}

/// <summary>
/// Basquin parameters for a handful of common engineering materials. <b>Transcribed from
/// published strain-life compilations (SAE J1099 / Dowling's tables) and flagged
/// verify-against-datasheet</b>, exactly as <c>StandardHoles</c>' Trisert rows and
/// <c>SheetMaterials</c>' K-factors are: fatigue constants are fitted to specific heats,
/// finishes and specimen geometries, published sources genuinely disagree (a 6061-T6
/// rotating-beam figure and its strain-life fit differ by tens of percent), and the
/// authority is the datasheet for your material condition, not this file. Polished
/// laboratory specimens throughout — no surface-finish, size or reliability (Marin)
/// factors are applied, so these are upper bounds for a machined part.
///
/// <para>The steel rows carry the conventional 10⁶-cycle endurance knee; the aluminium
/// rows carry none, because the material has none.</para>
/// </summary>
public static class FatigueMaterials
{
    /// <summary>SAE 1015 normalized carbon steel.</summary>
    public static readonly SnCurve Steel1015 =
        new("SAE 1015 normalized", 827, -0.11, 415, 1e6);

    /// <summary>SAE 1045 hot-rolled medium-carbon steel.</summary>
    public static readonly SnCurve Steel1045 =
        new("SAE 1045 HR", 948, -0.092, 621, 1e6);

    /// <summary>AISI 4340 quenched-and-tempered alloy steel (aircraft quality).</summary>
    public static readonly SnCurve Steel4340 =
        new("AISI 4340 QT", 1758, -0.0977, 1241, 1e6);

    /// <summary>2024-T351 aluminium plate. No endurance limit.</summary>
    public static readonly SnCurve Aluminium2024T351 =
        new("2024-T351", 927, -0.113, 469);

    /// <summary>6061-T6 aluminium. No endurance limit.</summary>
    public static readonly SnCurve Aluminium6061T6 =
        new("6061-T6", 535, -0.102, 310);

    /// <summary>7075-T6 aluminium. No endurance limit.</summary>
    public static readonly SnCurve Aluminium7075T6 =
        new("7075-T6", 1466, -0.143, 578);

    /// <summary>Every curve in the catalogue.</summary>
    public static IReadOnlyList<SnCurve> All { get; } =
    [
        Steel1015, Steel1045, Steel4340,
        Aluminium2024T351, Aluminium6061T6, Aluminium7075T6,
    ];
}
