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
    private readonly double? _haibachKnee;
    private readonly double? _haibachExponent;

    public SnCurve(
        string name,
        double fatigueStrengthCoefficient,
        double fatigueStrengthExponent,
        double ultimateStrength,
        double? enduranceLife = null)
        : this(name, fatigueStrengthCoefficient, fatigueStrengthExponent, ultimateStrength,
            enduranceLife, null, null)
    {
    }

    /// <summary>The full constructor, including the optional Miner–Haibach continuation past a
    /// knee. Internal because a Haibach curve is DERIVED (<see cref="WithHaibachSlope"/>) rather
    /// than stated — the transcribed rows carry no continuation of their own.</summary>
    private SnCurve(
        string name,
        double fatigueStrengthCoefficient,
        double fatigueStrengthExponent,
        double ultimateStrength,
        double? enduranceLife,
        double? haibachKnee,
        double? haibachExponent)
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
        _haibachKnee = haibachKnee;
        _haibachExponent = haibachExponent;
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

    /// <summary>Whether this curve carries a Miner–Haibach sloped continuation past its knee
    /// (<see cref="WithHaibachSlope"/>) rather than the flat endurance line — in which case it
    /// has no infinite-life plateau (<see cref="HasEnduranceLimit"/> is false).</summary>
    public bool HasHaibachContinuation => _haibachKnee is not null;

    /// <summary>
    /// The fully reversed alternating stress (MPa) survived for <paramref name="cycles"/>:
    /// the Basquin line up to the knee, then either the FLAT endurance line or, for a
    /// Miner–Haibach curve, the shallower sloped continuation.
    /// </summary>
    public double StressAt(double cycles)
    {
        if (!(cycles > 0))
            throw new ArgumentOutOfRangeException(
                nameof(cycles), cycles, "A life is a positive cycle count.");
        // Miner–Haibach: beyond the knee the line continues at a shallower slope through the
        // knee point, so it keeps falling rather than plateauing.
        if (_haibachKnee is { } hk && cycles >= hk)
            return BasquinStress(hk) * Math.Pow(cycles / hk, _haibachExponent!.Value);
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
        // Miner–Haibach: a stress below the knee stress has a FINITE life on the shallower
        // continuation, which is the whole point — small cycles below the endurance limit DO
        // accumulate damage once larger ones have started cracks, so the flat line understates
        // variable-amplitude damage. (A Haibach curve has EnduranceLimit == null, so the plateau
        // branch above is skipped and this one carries the sub-knee amplitudes.)
        if (_haibachKnee is { } hk)
        {
            double kneeStress = BasquinStress(hk);
            if (amplitude <= kneeStress)
                return hk * Math.Pow(amplitude / kneeStress, 1.0 / _haibachExponent!.Value);
        }
        return 0.5 * Math.Pow(amplitude / FatigueStrengthCoefficient, 1.0 / FatigueStrengthExponent);
    }

    /// <summary>
    /// The Miner–Haibach modification: continue the S-N line past the endurance knee at a
    /// SHALLOWER slope instead of leaving it flat — the standard variable-amplitude remedy for
    /// the step a flat line puts in the damage function.
    ///
    /// <para><b>Why the flat line is a constant-amplitude idea.</b> Below the endurance limit a
    /// single amplitude arrests small cracks and accumulates no damage — true for
    /// constant-amplitude loading. Under a SPECTRUM the larger cycles start cracks, and then the
    /// small sub-limit cycles DO grow them, so the flat line understates the damage. Haibach
    /// continues the line past the knee at slope <c>b' = b/(2 + b)</c> — the classical
    /// <c>k' = 2k − 1</c> in the Wöhler-slope convention, so a sub-limit cycle now carries a
    /// small finite life rather than none.</para>
    ///
    /// <para><b>The continuation removes the endurance PLATEAU, which is a real consequence
    /// rather than a side effect.</b> The result has no infinite-life strength
    /// (<see cref="HasEnduranceLimit"/> is false), so a safety factor against infinite life no
    /// longer exists for it and the fatigue machinery requires a stated design life — the SAME
    /// rule a knee-less aluminium curve already earns, so it is one rule and not two. The
    /// damage function is then CONTINUOUS across the knee (a cycle just under the limit carries a
    /// small damage rather than exactly zero), which is exactly the step the flat line's own
    /// artefact put there.</para>
    ///
    /// <para>Only a curve WITH a knee can be continued past it — a knee-less material's line
    /// already falls forever, and a curve already carrying a continuation is refused.</para>
    /// </summary>
    public SnCurve WithHaibachSlope()
    {
        if (_haibachKnee is not null)
            throw new FeaException(
                $"'{Name}' already carries a Miner–Haibach continuation.");
        if (EnduranceLife is not { } knee)
            throw new FeaException(
                $"'{Name}' has no endurance knee to continue past — a knee-less material's line "
                + "already falls forever, so there is no flat endurance segment for Haibach to "
                + "slope. The modification is for a steel whose FLAT endurance line understates "
                + "variable-amplitude damage.");
        // b' = b/(2 + b): the classical k' = 2k − 1 for the Wöhler slope k = -1/b, expressed in
        // the stress-versus-N exponent this curve uses (see the class remarks).
        double haibachExponent = FatigueStrengthExponent / (2.0 + FatigueStrengthExponent);
        return new SnCurve(
            $"{Name} (Haibach)", FatigueStrengthCoefficient, FatigueStrengthExponent,
            UltimateStrength, enduranceLife: null, haibachKnee: knee, haibachExponent);
    }

    /// <summary>
    /// A DERIVED curve for a real part: the endurance limit knocked down by the combined
    /// Marin factor while the transcribed row stays pristine — the derived-vs-stored rule
    /// the endurance limit itself already follows.
    ///
    /// <para><b>The construction is the classical two-anchor re-fit, and the pivot has a
    /// physical reason.</b> The factors multiply the ENDURANCE LIMIT, not sigma'_f
    /// (surface finish, size and scatter govern crack INITIATION, which dominates at long
    /// life; at 10³ cycles plastic strain dominates and the factors classically do not
    /// apply), so the corrected line passes through the pristine line's own 10³-cycle
    /// point and through <c>factor·(endurance limit)</c> at the unchanged knee — exactly
    /// Shigley's construction with the curve's own 10³ value as the low-cycle anchor
    /// rather than a second transcribed constant. The ultimate strength is untouched: a
    /// surface finish does not change a static failure.</para>
    ///
    /// <para>A factor of exactly 1 returns THIS curve verbatim (re-deriving would move
    /// the coefficients by ulps for nothing). A material with no endurance limit is
    /// refused by name — the factors anchor on the limit, and a knee-less material needs
    /// a stated reference life, which is a different construction.</para>
    /// </summary>
    /// <param name="enduranceFactor">The combined Marin factor in (0, 1] — typically
    /// <c>MarinFactors.Surface(...) · MarinFactors.Size(...) ·
    /// MarinFactors.Reliability(...)</c>, or use
    /// <see cref="WithFactors(SurfaceFinish, double?, double)"/>.</param>
    /// <param name="note">What the factor accounts for, appended to the name (e.g.
    /// "machined, d 25, R99"). Null keeps a generated "xK" suffix.</param>
    public SnCurve WithEnduranceFactor(double enduranceFactor, string? note = null)
    {
        if (!(enduranceFactor > 0) || enduranceFactor > 1)
            throw new FeaException(
                $"'{Name}': the combined Marin factor must be in (0, 1]; {enduranceFactor} was "
                + "given. A factor above one would claim the part outlasts the polished "
                + "laboratory specimen its own data came from.");
        if (EnduranceLife is not { } knee)
            throw new FeaException(
                $"'{Name}' has no endurance limit for a Marin factor to knock down. The "
                + "classical correction anchors on the limit; a material without one "
                + "(aluminium) needs the factor applied at a stated reference life, which is "
                + "a different construction and is not offered as a silent default.");
        if (knee <= MarinPivotLife)
            throw new FeaException(
                $"'{Name}': the endurance knee ({knee:G3} cycles) is at or below the 10³-cycle "
                + "pivot the correction turns about, so the corrected line would tilt inside "
                + "the low-cycle regime Basquin does not describe. Marin factors assume a "
                + "high-cycle knee (the catalogue's steels put it at 10⁶).");
        // Exact-1 identity: no arithmetic, so the pristine curve comes back verbatim.
        if (enduranceFactor == 1.0)
            return this;

        // The corrected line through (1e3, this line's own value) and
        // (knee, factor·limit): b' from the two anchors, sigma'_f' from the pivot.
        double pivotStress = BasquinStress(MarinPivotLife);
        double correctedLimit = enduranceFactor * BasquinStress(knee);
        double b = Math.Log(correctedLimit / pivotStress) / Math.Log(knee / MarinPivotLife);
        double coefficient = pivotStress / Math.Pow(2 * MarinPivotLife, b);
        string name = note is null
            ? $"{Name} (x{enduranceFactor:G4} endurance)"
            : $"{Name} ({note})";
        return new SnCurve(name, coefficient, b, UltimateStrength, knee);
    }

    /// <summary>
    /// The knee-less material's Marin correction: the combined factor applied at a STATED
    /// reference life, re-fitting through the same 10³ pivot — the honest version for
    /// aluminium, which has no endurance limit for <see cref="WithEnduranceFactor"/> to anchor
    /// on.
    ///
    /// <para><b>The reference life is required, not defaulted, because it IS the claim being
    /// made.</b> A knee-less S-N line falls forever, so "the endurance strength" is only
    /// meaningful at a stated life — the aluminium convention is 5×10⁸ cycles (the rotating-beam
    /// run-out) — and the factor knocks THAT strength down. The corrected line passes through
    /// the pristine line's own 10³-cycle point (the pivot, for the same reason
    /// <see cref="WithEnduranceFactor"/> uses it: below it plastic strain dominates and the
    /// factors do not apply) and through <c>factor·(pristine strength at the reference life)</c>,
    /// and it stays KNEE-LESS — the correction does not invent an endurance limit the material
    /// does not have.</para>
    ///
    /// <para>A curve that HAS a knee is refused by name (use <see cref="WithEnduranceFactor"/>,
    /// whose reference IS the knee); a factor of exactly 1 returns this curve verbatim; a
    /// reference life at or below the 10³ pivot is refused, since the correction would tilt the
    /// low-cycle regime Basquin does not describe.</para>
    /// </summary>
    /// <param name="enduranceFactor">The combined Marin factor in (0, 1].</param>
    /// <param name="referenceLife">The life in CYCLES at which the factor is applied (5e8 is
    /// the aluminium rotating-beam convention). Above the 10³ pivot.</param>
    /// <param name="note">What the factor accounts for, appended to the name.</param>
    public SnCurve WithEnduranceFactorAt(
        double enduranceFactor, double referenceLife, string? note = null)
    {
        if (!(enduranceFactor > 0) || enduranceFactor > 1)
            throw new FeaException(
                $"'{Name}': the combined Marin factor must be in (0, 1]; {enduranceFactor} was "
                + "given. A factor above one would claim the part outlasts the polished "
                + "laboratory specimen its own data came from.");
        if (HasEnduranceLimit)
            throw new FeaException(
                $"'{Name}' has an endurance knee, so its reference life IS that knee — use "
                + "WithEnduranceFactor, which anchors on it. WithEnduranceFactorAt is for a "
                + "knee-less material (aluminium), whose reference life is not fixed by the "
                + "curve and must be stated.");
        if (!(referenceLife > MarinPivotLife) || double.IsInfinity(referenceLife))
            throw new FeaException(
                $"'{Name}': the reference life must be finite and above the 10³-cycle pivot the "
                + $"correction turns about; {referenceLife:G3} cycles was given. State the "
                + "high-cycle life the endurance factor is quoted at (5e8 is the aluminium "
                + "rotating-beam convention).");
        // Exact-1 identity: the pristine curve comes back verbatim, no arithmetic to move ulps.
        if (enduranceFactor == 1.0)
            return this;

        double pivotStress = BasquinStress(MarinPivotLife);
        double correctedRefStress = enduranceFactor * BasquinStress(referenceLife);
        double b = Math.Log(correctedRefStress / pivotStress) / Math.Log(referenceLife / MarinPivotLife);
        double coefficient = pivotStress / Math.Pow(2 * MarinPivotLife, b);
        string name = note is null
            ? $"{Name} (x{enduranceFactor:G4} at {referenceLife:G3} cycles)"
            : $"{Name} ({note})";
        // Still knee-less: the correction does not invent an endurance limit.
        return new SnCurve(name, coefficient, b, UltimateStrength, null);
    }

    /// <summary>
    /// The knee-less twin of <see cref="WithFactors(SurfaceFinish, double?, double)"/>: the
    /// standard Marin factor applied at a stated reference life through
    /// <see cref="WithEnduranceFactorAt"/>. For aluminium, where a reference life must be given.
    /// </summary>
    /// <param name="finish">The part's surface condition at the crack-starting surface.</param>
    /// <param name="referenceLife">The life the endurance factor is quoted at (5e8 is the
    /// aluminium rotating-beam convention).</param>
    /// <param name="diameterMm">Section diameter for the size effect, or null.</param>
    /// <param name="reliability">The survival probability; one of the standard levels.</param>
    public SnCurve WithFactorsAt(
        SurfaceFinish finish, double referenceLife, double? diameterMm = null,
        double reliability = 0.5)
    {
        double ka = MarinFactors.Surface(finish, UltimateStrength);
        double kb = diameterMm is { } d ? MarinFactors.Size(d) : 1.0;
        double ke = MarinFactors.Reliability(reliability);
        string note = $"{finish} at {referenceLife:G3}"
            + (diameterMm is { } dd ? $", d {dd:G3}" : "")
            + (reliability != 0.5 ? $", R {reliability:G6}" : "");
        return WithEnduranceFactorAt(ka * kb * ke, referenceLife, note);
    }

    /// <summary>
    /// <see cref="WithEnduranceFactor"/> with the factor built from the standard Marin
    /// correlations: <c>Surface(finish, S_ut) · Size(diameter) ·
    /// Reliability(reliability)</c>. See <see cref="MarinFactors"/> for what each
    /// correlation is and its validity limits.
    /// </summary>
    /// <param name="finish">The part's surface condition at the crack-starting surface.</param>
    /// <param name="diameterMm">Section diameter in mm for the size effect (rotating
    /// bending/torsion), or null for no size effect (axial loading, or the test-specimen
    /// scale).</param>
    /// <param name="reliability">The survival probability the numbers should hold at
    /// (default 0.5 — the median, which is what the raw data is). Must be one of the
    /// standard tabulated levels; see <see cref="MarinFactors.Reliability"/>.</param>
    public SnCurve WithFactors(
        SurfaceFinish finish, double? diameterMm = null, double reliability = 0.5)
    {
        double ka = MarinFactors.Surface(finish, UltimateStrength);
        double kb = diameterMm is { } d ? MarinFactors.Size(d) : 1.0;
        double ke = MarinFactors.Reliability(reliability);
        string note = $"{finish}"
            + (diameterMm is { } dd ? $", d {dd:G3}" : "")
            + (reliability != 0.5 ? $", R {reliability:G6}" : "");
        return WithEnduranceFactor(ka * kb * ke, note);
    }

    /// <summary>The pivot the Marin correction turns about: 10³ cycles, Basquin's own
    /// high-cycle validity floor (below it plastic strain dominates and the surface/size
    /// factors classically do not apply).</summary>
    internal const double MarinPivotLife = 1e3;

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Name}: sigma'_f {FatigueStrengthCoefficient:G6} MPa, b {FatigueStrengthExponent:G4}, "
        + $"S_ut {UltimateStrength:G6} MPa"
        + (EnduranceLimit is { } e ? $", endurance {e:G4} MPa at {EnduranceLife:G2} cycles"
            : _haibachKnee is { } hk
                ? $", Haibach slope {_haibachExponent:G4} past {hk:G2} cycles (no plateau)"
                : ", no endurance limit");
}

/// <summary>The surface condition at the crack-starting surface — the rows of the
/// Marin surface-factor correlation.</summary>
public enum SurfaceFinish
{
    /// <summary>Ground. The gentlest penalty; at low ultimate strengths the correlation
    /// crosses 1 and is clamped there (the polished specimen IS the baseline).</summary>
    Ground,

    /// <summary>Machined or cold-drawn — one row in the classical table.</summary>
    Machined,

    /// <summary>Cold-drawn: the same correlation row as <see cref="Machined"/>.</summary>
    ColdDrawn,

    /// <summary>Hot-rolled, scale and decarburization included.</summary>
    HotRolled,

    /// <summary>As-forged — the heaviest penalty.</summary>
    AsForged,
}

/// <summary>
/// The classical Marin endurance-limit modification factors. <b>Transcribed from the
/// standard machine-design correlations (Shigley/Mischke's fits to Lipson–Noll and the
/// 8%-standard-deviation reliability transform) and flagged verify-against-datasheet</b>,
/// exactly as <c>FatigueMaterials</c>' rows are: the correlations are fits to scattered
/// mid-century data, published sources disagree in the last digits, and the authority for
/// a real part is its own test programme. Each factor multiplies the ENDURANCE LIMIT —
/// see <see cref="SnCurve.WithEnduranceFactor"/> for how a corrected curve is derived
/// without touching the transcribed row.
/// </summary>
public static class MarinFactors
{
    /// <summary>
    /// The surface factor <c>k_a = a·S_ut^b</c> (Shigley table 6-2 constants, S_ut in
    /// MPa), clamped at 1: the polished specimen is the baseline, and the ground
    /// correlation crosses 1 below about 215 MPa — extrapolation noise, not a real
    /// strengthening.
    /// </summary>
    public static double Surface(SurfaceFinish finish, double ultimateStrengthMpa)
    {
        if (!(ultimateStrengthMpa > 0))
            throw new FeaException(
                $"The surface factor needs a positive ultimate strength; {ultimateStrengthMpa} "
                + "MPa was given.");
        var (a, b) = finish switch
        {
            SurfaceFinish.Ground => (1.58, -0.085),
            SurfaceFinish.Machined or SurfaceFinish.ColdDrawn => (4.51, -0.265),
            SurfaceFinish.HotRolled => (57.7, -0.718),
            SurfaceFinish.AsForged => (272.0, -0.995),
            _ => throw new ArgumentOutOfRangeException(nameof(finish), finish, null),
        };
        return Math.Min(1.0, a * Math.Pow(ultimateStrengthMpa, b));
    }

    /// <summary>
    /// The size factor for a round section in rotating bending or torsion:
    /// <c>(d/7.62)^-0.107</c> for 2.79–51 mm, <c>1.51·d^-0.157</c> for 51–254 mm
    /// (the two branches agree at the 51 mm seam to the correlations' own fit accuracy).
    /// Below 2.79 mm the part IS the specimen scale and the factor is 1; above 254 mm
    /// the correlation has no data and is refused rather than extrapolated. Axial
    /// loading has no size effect — pass no diameter at all
    /// (<see cref="SnCurve.WithFactors"/>'s null).
    /// </summary>
    public static double Size(double diameterMm)
    {
        if (!(diameterMm > 0))
            throw new FeaException(
                $"The size factor needs a positive diameter; {diameterMm} mm was given.");
        if (diameterMm > 254)
            throw new FeaException(
                $"The size correlation stops at 254 mm and {diameterMm} mm was asked for; "
                + "beyond it there is no data to extrapolate, and a large shaft's endurance "
                + "wants its own test programme (0.6 is the classical floor engineers assume, "
                + "and assuming it silently is not this library's call).");
        if (diameterMm < 2.79)
            return 1.0;
        return diameterMm <= 51
            ? Math.Pow(diameterMm / 7.62, -0.107)
            : 1.51 * Math.Pow(diameterMm, -0.157);
    }

    /// <summary>
    /// The reliability factor <c>k_e = 1 - 0.08·z_R</c> — the endurance limit shifted
    /// down its own assumed 8%-of-mean scatter to the stated survival probability. The
    /// standard tabulated levels only; anything else is refused naming them, because
    /// interpolating a normal quantile through a table would invent precision the
    /// 8%-scatter assumption does not have.
    /// </summary>
    /// <param name="reliability">0.5, 0.9, 0.95, 0.99, 0.999, 0.9999, 0.99999 or
    /// 0.999999.</param>
    public static double Reliability(double reliability) => reliability switch
    {
        0.5 => 1.000,
        0.9 => 0.897,
        0.95 => 0.868,
        0.99 => 0.814,
        0.999 => 0.753,
        0.9999 => 0.702,
        0.99999 => 0.659,
        0.999999 => 0.620,
        _ => throw new FeaException(
            $"No reliability factor is tabulated for {reliability}. The standard levels are "
            + "0.5, 0.9, 0.95, 0.99, 0.999, 0.9999, 0.99999 and 0.999999 — interpolating "
            + "between them would invent precision the underlying 8%-scatter assumption "
            + "does not have."),
    };
}

/// <summary>
/// Basquin parameters for a handful of common engineering materials. <b>Transcribed from
/// published strain-life compilations (SAE J1099 / Dowling's tables) and flagged
/// verify-against-datasheet</b>, exactly as <c>StandardHoles</c>' Trisert rows and
/// <c>SheetMaterials</c>' K-factors are: fatigue constants are fitted to specific heats,
/// finishes and specimen geometries, published sources genuinely disagree (a 6061-T6
/// rotating-beam figure and its strain-life fit differ by tens of percent), and the
/// authority is the datasheet for your material condition, not this file. Polished
/// laboratory specimens throughout — the rows are upper bounds for a machined part, and
/// <see cref="SnCurve.WithFactors(SurfaceFinish, double?, double)"/> is how a real part's
/// surface, size and reliability knock the endurance end down without the transcription
/// ever being edited.
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
