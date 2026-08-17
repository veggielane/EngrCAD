using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>Which directional failure criterion a <see cref="FailureAnalysis"/> evaluates.</summary>
public enum FailureCriterion
{
    /// <summary>Maximum stress: each material-frame component against its own allowable,
    /// independently. Non-interactive, so it names a MODE — and under-predicts wherever two
    /// components genuinely interact.</summary>
    MaxStress,

    /// <summary>Tsai–Hill: a quadratic distortional-energy criterion, the orthotropic
    /// extension of von Mises. Interactive; no distinct tension/compression term, so the
    /// allowable is picked per component by the sign of that component's stress.</summary>
    TsaiHill,

    /// <summary>Tsai–Wu: a full quadratic tensor polynomial with LINEAR terms, so tension
    /// and compression are distinguished continuously rather than by a switch. The general
    /// choice, at the cost of one coefficient no uniaxial test determines
    /// (<see cref="LaminaStrength.F12Star"/>).</summary>
    TsaiWu,
}

/// <summary>Which component drove a <see cref="FailureCriterion.MaxStress"/> index.</summary>
public enum FailureMode
{
    /// <summary>No stress at all — nothing drove anything.</summary>
    None,

    /// <summary>Fibre-direction tension, against Xt.</summary>
    FibreTension,

    /// <summary>Fibre-direction compression, against Xc.</summary>
    FibreCompression,

    /// <summary>Transverse tension, against Yt — matrix cracking.</summary>
    MatrixTension,

    /// <summary>Transverse compression, against Yc.</summary>
    MatrixCompression,

    /// <summary>In-plane shear, against S.</summary>
    Shear,

    /// <summary>An interactive criterion reached its limit; no single component owns it.</summary>
    Interactive,
}

/// <summary>What a criterion said about one stress state.</summary>
/// <param name="Index">The load-normalised failure index, <c>1 / StrengthRatio</c>: 1 is
/// exactly at the limit, above 1 is failure, and it scales LINEARLY with the load for every
/// criterion here.</param>
/// <param name="StrengthRatio">The multiplier on this stress state at which the criterion is
/// met — the strength ratio R, and the number an engineer reads as a safety factor. Positive
/// infinity when no scaling of this state ever reaches the limit.</param>
/// <param name="Mode">Which component drove it (<see cref="FailureMode.Interactive"/> for
/// the quadratic criteria).</param>
public readonly record struct FailureEvaluation(double Index, double StrengthRatio, FailureMode Mode);

/// <summary>
/// A lamina's directional strengths, quoted along the SAME 1-2-3 axes as its moduli — five
/// numbers for the plane-stress state a thin ply carries.
///
/// <para><b>It sits beside <see cref="ElasticLaw"/> rather than on <see cref="Material"/>,
/// and the argument is the frame's</b> (design.md §3h): a directional strength means nothing
/// without knowing which way the fibres run, and which way they run is a property of how the
/// stuff was laid into this part rather than of the stuff. So it is per-REGION analysis data,
/// and it takes its frame from the region's own <see cref="ElasticLaw"/> instead of carrying
/// a second copy that could drift from the stiffness it describes.</para>
///
/// <para><b>The compressive strengths are POSITIVE magnitudes</b>, which is the data-sheet
/// convention and a real trap — a transcription that writes Xc as −1500 would make every
/// index nonsense — so a non-positive value is refused by name.</para>
///
/// <para><b>What is NOT here:</b> the through-thickness allowables (Zt, Zc) and the
/// interlaminar shear strengths (S13, S23). A ply's strengths are quoted in-plane, and
/// interlaminar failure is DELAMINATION — a different mechanism, evaluated on a different
/// quantity, and not something a smeared solid law can see (it has no ply interfaces). The
/// out-of-plane stress a solve does produce is therefore not silently ignored: it is
/// measured and reported as <see cref="FailureResults.MaxOutOfPlaneFraction"/>, so a user can
/// tell whether a plane-stress criterion means anything at the point in question.</para>
/// </summary>
public sealed record LaminaStrength
{
    private readonly double _f12Star = -0.5;

    /// <summary>Creates a strength set. All five are positive magnitudes in MPa.</summary>
    /// <param name="xt">Longitudinal (fibre-direction) tensile strength.</param>
    /// <param name="xc">Longitudinal compressive strength, as a positive magnitude.</param>
    /// <param name="yt">Transverse tensile strength.</param>
    /// <param name="yc">Transverse compressive strength, as a positive magnitude.</param>
    /// <param name="s">In-plane shear strength (sign-independent by symmetry).</param>
    /// <param name="name">Optional label for reports and refusal messages.</param>
    public LaminaStrength(double xt, double xc, double yt, double yc, double s, string? name = null)
    {
        Require(xt, nameof(xt));
        Require(xc, nameof(xc));
        Require(yt, nameof(yt));
        Require(yc, nameof(yc));
        Require(s, nameof(s));
        Xt = xt;
        Xc = xc;
        Yt = yt;
        Yc = yc;
        S = s;
        Name = name ?? "lamina strength";
    }

    /// <summary>Longitudinal tensile strength, MPa.</summary>
    public double Xt { get; }

    /// <summary>Longitudinal compressive strength as a positive magnitude, MPa.</summary>
    public double Xc { get; }

    /// <summary>Transverse tensile strength, MPa.</summary>
    public double Yt { get; }

    /// <summary>Transverse compressive strength as a positive magnitude, MPa.</summary>
    public double Yc { get; }

    /// <summary>In-plane shear strength, MPa.</summary>
    public double S { get; }

    /// <summary>Label.</summary>
    public string Name { get; init; } = "lamina strength";

    /// <summary>
    /// Tsai–Wu's normalised interaction coefficient <c>F12* = F12 / sqrt(F11·F22)</c>.
    ///
    /// <para><b>This is the one number no uniaxial test determines</b>, which is the honest
    /// objection to Tsai–Wu and the reason it is a stated parameter here rather than a
    /// constant buried in the evaluator: measuring it needs a biaxial test, and the
    /// literature's values scatter. The default −0.5 is the generalised von Mises choice —
    /// it is what makes Tsai–Wu reduce to the Tsai–Hill interaction term for a material with
    /// equal tensile and compressive strengths — and is the value most references adopt when
    /// no biaxial data exists.</para>
    ///
    /// <para>Bounded by <c>|F12*| &lt; 1</c>, refused by name outside it: at ±1 the quadratic
    /// form stops being positive definite and the failure surface opens into an unbounded
    /// hyperboloid, so some biaxial stress state of any magnitude would be reported safe.
    /// The same "a Cholesky IS the statement" argument the elastic law makes about a
    /// compliance matrix, one dimension down and in closed form.</para>
    /// </summary>
    public double F12Star
    {
        get => _f12Star;
        init
        {
            if (!(Math.Abs(value) < 1.0) || double.IsNaN(value))
            {
                throw new FeaException(
                    $"The Tsai-Wu interaction coefficient F12* = {value:G6} must satisfy "
                    + "|F12*| < 1. At or past 1 the quadratic form is no longer positive "
                    + "definite, so the failure surface is an open hyperboloid rather than an "
                    + "ellipsoid and an arbitrarily large biaxial stress would be reported as "
                    + "safe.");
            }
            _f12Star = value;
        }
    }

    /// <summary>
    /// Evaluates one criterion against a stress state written in the MATERIAL frame.
    ///
    /// <para><b>The published quantity is the load-normalised index 1/R, not the raw
    /// polynomial.</b> Tsai–Hill's and Tsai–Wu's left-hand sides are quadratic, so their
    /// numeric values are not comparable with each other, with max-stress, or with
    /// themselves at a different load. The strength RATIO is, and it is what an engineer
    /// wants anyway: R = 2 means the load can double. Normalising by it also makes the
    /// uniaxial reduction exact — a pure fibre-direction tension gives R = Xt/sigma1 for all
    /// three criteria, which is the identity the tests pin.</para>
    /// </summary>
    /// <param name="criterion">Which criterion.</param>
    /// <param name="sigma1">Fibre-direction normal stress, MPa (signed).</param>
    /// <param name="sigma2">Transverse normal stress, MPa (signed).</param>
    /// <param name="tau12">In-plane shear stress, MPa.</param>
    public FailureEvaluation Evaluate(
        FailureCriterion criterion, double sigma1, double sigma2, double tau12) =>
        criterion switch
        {
            FailureCriterion.MaxStress => MaxStress(sigma1, sigma2, tau12),
            FailureCriterion.TsaiHill => TsaiHill(sigma1, sigma2, tau12),
            FailureCriterion.TsaiWu => TsaiWu(sigma1, sigma2, tau12),
            _ => throw new FeaException($"Unknown failure criterion {criterion}."),
        };

    /// <summary>Evaluates a criterion against a material-frame stress tensor, reading the
    /// three in-plane components. See the type remarks for why the out-of-plane ones are
    /// reported rather than consumed.</summary>
    public FailureEvaluation Evaluate(FailureCriterion criterion, in SymmetricTensor3 materialStress) =>
        Evaluate(criterion, materialStress.Xx, materialStress.Yy, materialStress.Xy);

    private FailureEvaluation MaxStress(double s1, double s2, double t12)
    {
        double r1 = s1 >= 0 ? s1 / Xt : -s1 / Xc;
        double r2 = s2 >= 0 ? s2 / Yt : -s2 / Yc;
        double r6 = Math.Abs(t12) / S;

        double index = r1;
        var mode = s1 >= 0 ? FailureMode.FibreTension : FailureMode.FibreCompression;
        if (r2 > index)
        {
            index = r2;
            mode = s2 >= 0 ? FailureMode.MatrixTension : FailureMode.MatrixCompression;
        }
        if (r6 > index)
        {
            index = r6;
            mode = FailureMode.Shear;
        }
        // Exact-zero semantic test: an unstressed point has no mode, and reporting one
        // would put a spurious "fibre tension" on every node of an unloaded region.
        if (index == 0)
            return new FailureEvaluation(0, double.PositiveInfinity, FailureMode.None);
        return new FailureEvaluation(index, 1.0 / index, mode);
    }

    private FailureEvaluation TsaiHill(double s1, double s2, double t12)
    {
        // The allowables are picked per component by the sign of that component's stress —
        // Tsai-Hill has no linear term to distinguish them continuously, which is exactly
        // the limitation Tsai-Wu exists to remove.
        double x = s1 >= 0 ? Xt : Xc;
        double y = s2 >= 0 ? Yt : Yc;
        double lhs = s1 * s1 / (x * x) - s1 * s2 / (x * x) + s2 * s2 / (y * y) + t12 * t12 / (S * S);
        if (!(lhs > 0))
            return new FailureEvaluation(0, double.PositiveInfinity, FailureMode.None);
        // Homogeneous of degree 2, so the load multiplier is one square root away.
        double index = Math.Sqrt(lhs);
        return new FailureEvaluation(index, 1.0 / index, FailureMode.Interactive);
    }

    private FailureEvaluation TsaiWu(double s1, double s2, double t12)
    {
        double f1 = 1.0 / Xt - 1.0 / Xc;
        double f2 = 1.0 / Yt - 1.0 / Yc;
        double f11 = 1.0 / (Xt * Xc);
        double f22 = 1.0 / (Yt * Yc);
        double f66 = 1.0 / (S * S);
        double f12 = F12Star * Math.Sqrt(f11 * f22);

        double quadratic = f11 * s1 * s1 + f22 * s2 * s2 + f66 * t12 * t12 + 2.0 * f12 * s1 * s2;
        double linear = f1 * s1 + f2 * s2;

        // R solves quadratic·R² + linear·R = 1. The quadratic form is positive definite
        // (|F12*| < 1 is checked at construction), so it vanishes only at zero stress.
        if (!(quadratic > 0))
        {
            return linear > 0
                ? new FailureEvaluation(linear, 1.0 / linear, FailureMode.Interactive)
                : new FailureEvaluation(0, double.PositiveInfinity, FailureMode.None);
        }
        double ratio = (-linear + Math.Sqrt(linear * linear + 4.0 * quadratic)) / (2.0 * quadratic);
        return new FailureEvaluation(1.0 / ratio, ratio, FailureMode.Interactive);
    }

    private static void Require(double value, string name)
    {
        if (!(value > 0) || double.IsInfinity(value))
        {
            throw new FeaException(
                $"The strength '{name}' must be finite and positive; {value:G6} was supplied. "
                + "The compressive allowables Xc and Yc are stated as positive MAGNITUDES, the "
                + "data-sheet convention — a negative there is a transcription error, and it "
                + "would invert every compressive index rather than merely shifting it.");
        }
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Name}: Xt {Xt:G4} Xc {Xc:G4} Yt {Yt:G4} Yc {Yc:G4} S {S:G4} MPa";
}
