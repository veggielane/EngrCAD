using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>
/// One lamina's elastic constants — the transversely isotropic idealisation a composite
/// data sheet quotes: stiff along the fibre (1), soft and isotropic across it (2 in the
/// ply plane, 3 through the thickness).
///
/// <para>The same five constants <see cref="ElasticLaw.TransverselyIsotropic"/> takes, and
/// that is not a coincidence — a <see cref="Ply"/>'s 6x6 is built by calling it, so a
/// laminate and a single off-axis lamina go through one rotation implementation.</para>
/// </summary>
public sealed record LaminaProperties
{
    /// <summary>Creates a lamina from its engineering constants (MPa).</summary>
    /// <param name="e1">Modulus along the fibre.</param>
    /// <param name="e2">Modulus across the fibre (= the through-thickness modulus).</param>
    /// <param name="nu12">Major Poisson's ratio for a fibre-direction pull.</param>
    /// <param name="g12">In-plane shear modulus.</param>
    /// <param name="nu23">
    /// Poisson's ratio of the transverse ISOTROPIC plane, which fixes the transverse shear
    /// modulus <c>G23 = E2 / (2(1 + nu23))</c>.
    /// <para>It has a default because data sheets rarely quote it, and the default is safe
    /// to have because its blast radius is MEASURED rather than assumed: every plane-stress
    /// quantity — <see cref="Laminate.A"/>, <see cref="Laminate.B"/>,
    /// <see cref="Laminate.D"/> and the in-plane equivalent constants — is a function of
    /// (E1, E2, nu12, G12) alone, so nu23 moves only the smeared law's THROUGH-THICKNESS
    /// block, which the smearing already qualifies. Pinned by a test at ROUND-OFF rather
    /// than bitwise: the reduced stiffness is reached by condensing the full 3D law, whose
    /// blocks do depend on nu23, so the independence is a cancellation — a theorem — rather
    /// than a structural fact about which terms were written down.</para>
    /// </param>
    /// <param name="name">Optional label for reports.</param>
    public LaminaProperties(
        double e1, double e2, double nu12, double g12, double nu23 = 0.4, string? name = null)
    {
        RequirePositive(e1, nameof(e1));
        RequirePositive(e2, nameof(e2));
        RequirePositive(g12, nameof(g12));
        if (!(nu12 > -1 && nu12 < 0.5 + 1e-12) || double.IsNaN(nu12))
        {
            throw new FeaException(
                $"nu12 = {nu12:G6} is outside (-1, 0.5]. The MAJOR ratio of a lamina is "
                + "bounded by sqrt(E1/E2) rather than by 0.5, but a value past 0.5 is almost "
                + "always the MINOR ratio nu21 transcribed into the wrong slot — and this "
                + "type derives nu21 from the compliance symmetry, so supplying it here "
                + "would state the same fact twice.");
        }
        E1 = e1;
        E2 = e2;
        Nu12 = nu12;
        G12 = g12;
        Nu23 = nu23;
        Name = name ?? "lamina";
    }

    /// <summary>Modulus along the fibre, MPa.</summary>
    public double E1 { get; }

    /// <summary>Modulus across the fibre, MPa.</summary>
    public double E2 { get; }

    /// <summary>Major Poisson's ratio for a fibre-direction pull.</summary>
    public double Nu12 { get; }

    /// <summary>In-plane shear modulus, MPa.</summary>
    public double G12 { get; }

    /// <summary>Poisson's ratio of the transverse isotropic plane.</summary>
    public double Nu23 { get; }

    /// <summary>Label.</summary>
    public string Name { get; }

    /// <summary>The transverse shear modulus, DERIVED — in an isotropic plane the shear
    /// modulus is not free.</summary>
    public double G23 => E2 / (2.0 * (1.0 + Nu23));

    private static void RequirePositive(double value, string name)
    {
        if (!(value > 0) || double.IsInfinity(value))
        {
            throw new FeaException(
                $"A lamina's {name} must be finite and positive; {value:G6} was supplied.");
        }
    }
}

/// <summary>
/// One ply in a stack: a lamina, the angle its fibres make with the laminate's own x axis
/// (degrees, counter-clockwise about the stacking normal), and its cured thickness.
/// </summary>
/// <param name="Material">The lamina's constants.</param>
/// <param name="AngleDegrees">Fibre angle from the laminate x axis, CCW about z.</param>
/// <param name="Thickness">Cured ply thickness, mm.</param>
public readonly record struct Ply(LaminaProperties Material, double AngleDegrees, double Thickness);

/// <summary>The in-plane or flexural engineering constants of a laminate treated as one
/// equivalent orthotropic sheet.</summary>
/// <param name="Ex">Modulus along the laminate x axis, MPa.</param>
/// <param name="Ey">Modulus along y, MPa.</param>
/// <param name="Gxy">In-plane shear modulus, MPa.</param>
/// <param name="NuXy">Major Poisson's ratio for an x pull.</param>
/// <param name="NuYx">The minor ratio, <c>NuXy · Ey / Ex</c>.</param>
public readonly record struct LaminateConstants(
    double Ex, double Ey, double Gxy, double NuXy, double NuYx);

/// <summary>
/// A stack of plies, and classical lamination theory over it: the <see cref="A"/>,
/// <see cref="B"/> and <see cref="D"/> matrices, the equivalent single-layer engineering
/// constants they imply, and <see cref="ToElasticLaw"/> — the smeared 3D law a solid
/// element can carry.
///
/// <para><b>This is a PROPERTY DERIVATION, not a new element.</b> Nothing in the solver
/// changes: the laminate produces an <see cref="ElasticLaw"/> and rides
/// <see cref="StructuralModel.SetElasticity"/> exactly as a hand-stated orthotropic law
/// does. What the type adds is the arithmetic that turns a layup into that law, and — as
/// importantly — the two numbers that say what the smearing cost
/// (<see cref="CouplingRatio"/>, <see cref="FlexuralDiscrepancy"/>).</para>
///
/// <para><b>What smearing drops, stated rather than implied.</b> A solid element carrying
/// one constitutive law has no memory of the stacking sequence through its thickness, so:
/// <list type="bullet">
/// <item>Bending–extension coupling (a non-zero <see cref="B"/>) cannot be represented at
/// all, and <see cref="ToElasticLaw"/> REFUSES an unsymmetric layup by name rather than
/// returning a law that is quietly wrong about warping.</item>
/// <item>Even for a symmetric layup the FLEXURAL stiffness is only reproduced to the extent
/// that <see cref="D"/> agrees with <c>h²·A/12</c> — the value a through-thickness-uniform
/// material would have. <see cref="FlexuralDiscrepancy"/> is that gap, measured, and for a
/// cross-ply it is large (the outer plies dominate bending and the smearing does not know
/// they are outside).</item>
/// <item>Interlaminar stresses and delamination are outside the model entirely: a smeared
/// law has no ply interfaces to separate.</item>
/// </list>
/// The way to keep any of them is the same one: mesh the plies as separate regions through
/// the thickness and give each its own <see cref="ElasticLaw"/>.</para>
///
/// <para><b>The homogenisation is mixed — parallel in-plane, series through the
/// thickness</b> — which is the same physics the PCB thermal smear stands on
/// (<c>PcbThermal</c>: layers conduct in PARALLEL in-plane and in SERIES through the
/// thickness). Here: every ply shares the in-plane strain (they are bonded, so the strains
/// add nothing) while they share the through-thickness stress (they stack, so the strains
/// add). Condensing that per ply and averaging gives a 6x6 that is symmetric BY
/// CONSTRUCTION and whose plane-stress reduction is exactly <c>A/h</c> — so the smeared law
/// and CLT cannot disagree about in-plane behaviour.</para>
/// </summary>
public sealed class Laminate
{
    /// <summary>
    /// The relative size of <see cref="B"/> at which <see cref="ToElasticLaw"/> calls a
    /// layup unsymmetric. Dimensionless (B is scaled by <c>A·h/2</c>), and three decades
    /// above the round-off a genuinely symmetric stack leaves — an unsymmetric one measures
    /// order 0.1, so nothing sits near the threshold.
    /// </summary>
    public const double SymmetryTolerance = 1e-9;

    private readonly Ply[] _plies;
    private readonly double[] _a = new double[9];
    private readonly double[] _b = new double[9];
    private readonly double[] _d = new double[9];
    private readonly double[] _smeared = new double[36];

    private Laminate(Ply[] plies)
    {
        _plies = plies;
        double h = 0;
        for (int k = 0; k < plies.Length; k++)
        {
            if (!(plies[k].Thickness > 0) || double.IsInfinity(plies[k].Thickness))
            {
                throw new FeaException(
                    $"Ply {k} has thickness {plies[k].Thickness:G6}; a ply's thickness must be "
                    + "finite and positive.");
            }
            ArgumentNullException.ThrowIfNull(plies[k].Material);
            h += plies[k].Thickness;
        }
        Thickness = h;

        // Interfaces measured from the MIDPLANE, which is what makes B the coupling matrix
        // rather than an arbitrary first moment: z runs -h/2 .. +h/2 bottom to top.
        var z = new double[plies.Length + 1];
        z[0] = -0.5 * h;
        for (int k = 0; k < plies.Length; k++)
            z[k + 1] = z[k] + plies[k].Thickness;

        // The mixed-homogenisation accumulators (see the class remarks):
        //   P    = <Qbar>            the plane-stress reduced stiffness, thickness-averaged
        //   qSum = <C_io C_oo^-1>    the in-plane response to a through-thickness stress
        //   rSum = <C_oo^-1>         the out-of-plane compliance, in series
        var p = new double[9];
        var qSum = new double[9];
        var rSum = new double[9];

        Span<double> qbar = stackalloc double[9];
        Span<double> w = stackalloc double[9];
        Span<double> cooInverse = stackalloc double[9];

        for (int k = 0; k < plies.Length; k++)
        {
            var ply = plies[k];
            PlyStiffness(ply, qbar, w, cooInverse);

            double t = ply.Thickness;
            double phi = t / h;
            double z0 = z[k], z1 = z[k + 1];
            double firstMoment = 0.5 * (z1 * z1 - z0 * z0);
            double secondMoment = (z1 * z1 * z1 - z0 * z0 * z0) / 3.0;

            for (int i = 0; i < 9; i++)
            {
                _a[i] += qbar[i] * t;
                _b[i] += qbar[i] * firstMoment;
                _d[i] += qbar[i] * secondMoment;
                p[i] += qbar[i] * phi;
                qSum[i] += w[i] * phi;
                rSum[i] += cooInverse[i] * phi;
            }
        }

        Symmetrise(_a);
        Symmetrise(_b);
        Symmetrise(_d);

        BuildSmeared(p, qSum, rSum);
    }

    // ---- construction ------------------------------------------------------------------

    /// <summary>A laminate from its plies, listed BOTTOM to TOP (the −z face first).</summary>
    public static Laminate Of(params Ply[] plies)
    {
        ArgumentNullException.ThrowIfNull(plies);
        if (plies.Length == 0)
            throw new FeaException("A laminate needs at least one ply.");
        return new Laminate((Ply[])plies.Clone());
    }

    /// <summary>A laminate of one material and one ply thickness at the stated angles,
    /// bottom to top — the <c>[0/90/45]</c> spelling.</summary>
    public static Laminate Stack(
        LaminaProperties material, double plyThickness, params double[] anglesDegrees)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(anglesDegrees);
        if (anglesDegrees.Length == 0)
            throw new FeaException("A laminate needs at least one ply.");
        var plies = new Ply[anglesDegrees.Length];
        for (int i = 0; i < anglesDegrees.Length; i++)
            plies[i] = new Ply(material, anglesDegrees[i], plyThickness);
        return new Laminate(plies);
    }

    /// <summary>
    /// The <c>[...]s</c> spelling: the stated angles, then their mirror image, so
    /// <c>Symmetric(m, t, 0, 90)</c> is <c>[0/90]s</c> = 0/90/90/0.
    /// <para>Worth having as its own factory rather than left to the caller, because a
    /// symmetric layup is the one <see cref="ToElasticLaw"/> accepts and spelling the mirror
    /// by hand is exactly where a stack comes to be almost symmetric.</para>
    /// </summary>
    public static Laminate Symmetric(
        LaminaProperties material, double plyThickness, params double[] anglesDegrees)
    {
        ArgumentNullException.ThrowIfNull(anglesDegrees);
        var all = new double[2 * anglesDegrees.Length];
        for (int i = 0; i < anglesDegrees.Length; i++)
        {
            all[i] = anglesDegrees[i];
            all[all.Length - 1 - i] = anglesDegrees[i];
        }
        return Stack(material, plyThickness, all);
    }

    // ---- what it is --------------------------------------------------------------------

    /// <summary>The plies, bottom to top.</summary>
    public IReadOnlyList<Ply> Plies => _plies;

    /// <summary>Total cured thickness h, mm.</summary>
    public double Thickness { get; }

    /// <summary>
    /// The EXTENSIONAL stiffness matrix, N/mm — 3x3 row-major over (x, y, xy) with
    /// engineering shear, so <c>N = A·eps</c> relates in-plane force per unit width to
    /// mid-plane strain. <c>A = sum(Qbar_k · t_k)</c>.
    /// </summary>
    public ReadOnlySpan<double> A => _a;

    /// <summary>
    /// The bending–extension COUPLING matrix, N — <c>B = ½ sum(Qbar_k (z_k² − z_{k−1}²))</c>.
    /// Exactly zero for a symmetric layup, and the reason
    /// <see cref="ToElasticLaw"/> refuses an unsymmetric one.
    /// </summary>
    public ReadOnlySpan<double> B => _b;

    /// <summary>
    /// The BENDING stiffness matrix, N·mm — <c>D = ⅓ sum(Qbar_k (z_k³ − z_{k−1}³))</c>, so
    /// <c>M = D·kappa</c> relates moment per unit width to curvature.
    /// </summary>
    public ReadOnlySpan<double> D => _d;

    /// <summary>
    /// The in-plane equivalent single-layer constants, from <c>a = A⁻¹</c>:
    /// <c>Ex = 1/(h·a11)</c>, <c>nu_xy = −a12/a11</c>, and so on. These are what
    /// <see cref="ToElasticLaw"/> reproduces exactly.
    /// </summary>
    public LaminateConstants InPlane => Constants(_a, Thickness);

    /// <summary>
    /// The FLEXURAL equivalent constants, from <c>d = D⁻¹</c> with the <c>h³/12</c> of a
    /// solid section: <c>Ex_f = 12/(h³·d11)</c>.
    /// <para>They differ from <see cref="InPlane"/> whenever the layup is not uniform
    /// through the thickness — a cross-ply's outer plies carry the bending — and the
    /// difference is precisely what a smeared solid law cannot know. See
    /// <see cref="FlexuralDiscrepancy"/>.</para>
    /// </summary>
    public LaminateConstants Flexural => Constants(_d, Thickness * Thickness * Thickness / 12.0);

    /// <summary>
    /// <c>max|B| / (max|A| · h/2)</c> — a dimensionless measure of bending–extension
    /// coupling. Zero (to round-off) for a symmetric layup; order 0.1 for an unsymmetric
    /// one.
    /// </summary>
    public double CouplingRatio
    {
        get
        {
            double a = MaxAbs(_a);
            double b = MaxAbs(_b);
            if (!(a > 0))
                return 0;
            return b / (a * 0.5 * Thickness);
        }
    }

    /// <summary>
    /// <c>max|D − h²A/12| / max|D|</c> — how far the real bending stiffness is from the one
    /// a through-thickness-uniform material of the same in-plane stiffness would have.
    ///
    /// <para><b>This is the number that says what the smeared law's bending answer is worth.</b>
    /// It is exactly zero for a stack of identically oriented plies (there is nothing to
    /// smear), and it grows with how much the layup varies through the thickness: a
    /// <c>[0/90]s</c> cross-ply's outer plies dominate the section modulus, so its flexural
    /// Ex is well above its in-plane Ex and a solid element carrying only the in-plane law
    /// under-predicts the bending stiffness by that ratio. Reported rather than refused,
    /// because refusing it would refuse every real laminate.</para>
    /// </summary>
    public double FlexuralDiscrepancy
    {
        get
        {
            double scale = MaxAbs(_d);
            if (!(scale > 0))
                return 0;
            double h2Over12 = Thickness * Thickness / 12.0;
            double worst = 0;
            for (int i = 0; i < 9; i++)
                worst = Math.Max(worst, Math.Abs(_d[i] - h2Over12 * _a[i]));
            return worst / scale;
        }
    }

    /// <summary>True when <see cref="CouplingRatio"/> is within
    /// <see cref="SymmetryTolerance"/> — measured, not read off the angle list.</summary>
    public bool IsSymmetric => CouplingRatio <= SymmetryTolerance;

    /// <summary>
    /// True when the shear–extension coupling terms A16 and A26 vanish — the classical
    /// "balanced" laminate, every +theta ply matched by a −theta one.
    /// <para>Measured off <see cref="A"/> rather than inferred from the angles, and the
    /// measurement reads EXACTLY zero for a balanced stack rather than nearly: a ply angle's
    /// sine and cosine are taken from its MAGNITUDE with the sign applied afterwards, so a
    /// ±theta pair's contributions are exact negatives and cancel bit for bit.</para>
    /// </summary>
    public bool IsBalanced
    {
        get
        {
            double scale = MaxAbs(_a);
            if (!(scale > 0))
                return true;
            return (Math.Abs(_a[2]) + Math.Abs(_a[5])) <= SymmetryTolerance * scale;
        }
    }

    /// <summary>
    /// The smeared 3D constitutive law, placed by <paramref name="frame"/>: its X axis is
    /// the laminate's 0-degree direction and its Z axis the stacking normal.
    ///
    /// <para>Refuses an UNSYMMETRIC layup by name — see the class remarks for why a solid
    /// element cannot carry <see cref="B"/>, and <see cref="FlexuralDiscrepancy"/> for the
    /// limitation that survives even a symmetric one.</para>
    /// </summary>
    /// <param name="frame">Where the laminate's own axes point in the model.</param>
    /// <param name="name">Optional label; defaults to a description of the stack.</param>
    public ElasticLaw ToElasticLaw(Frame3d frame, string? name = null)
    {
        double coupling = CouplingRatio;
        if (coupling > SymmetryTolerance)
        {
            int worst = 0;
            for (int i = 1; i < 9; i++)
            {
                if (Math.Abs(_b[i]) > Math.Abs(_b[worst]))
                    worst = i;
            }
            throw new FeaException(
                $"This layup is UNSYMMETRIC: B[{worst / 3},{worst % 3}] = {_b[worst]:G6} N, a "
                + $"coupling ratio of {coupling:G4} against a tolerance of {SymmetryTolerance:G2}. "
                + "A non-zero B means in-plane load produces curvature, which a solid element "
                + "carrying ONE constitutive law cannot represent at all — it has no memory of "
                + "which ply was on top. Either mirror the stack (Laminate.Symmetric), or mesh "
                + "the plies as separate regions through the thickness and give each its own "
                + "ElasticLaw.");
        }
        return ElasticLaw.Anisotropic(frame, _smeared, name ?? Describe());
    }

    /// <summary>The smeared law on the world axes (0 degrees along X, stacking along Z).</summary>
    public ElasticLaw ToElasticLaw(string? name = null) => ToElasticLaw(Frame3d.WorldXY, name);

    /// <summary>A short description of the stack — the layup and its thickness.</summary>
    public string Describe()
    {
        var angles = string.Join('/', _plies.Select(p => p.AngleDegrees.ToString("0.##")));
        return $"laminate [{angles}] of {_plies[0].Material.Name}, {Thickness:G4} mm";
    }

    /// <inheritdoc/>
    public override string ToString() => Describe();

    // ---- the arithmetic ----------------------------------------------------------------

    /// <summary>
    /// The sine and cosine of a ply angle, with quarter turns EXACT and a sign convention
    /// that makes a ±theta pair cancel bit for bit.
    ///
    /// <para>Both halves are load-bearing. The exact quarter turns (the repository's
    /// standing "a quarter turn is a sign swap, never a cos" rule) are what make a cross-ply
    /// laminate's A16, A26, D16 and D26 read EXACTLY zero rather than at 1e-17 — so
    /// "a cross-ply has no shear–extension coupling" is an identity a test can assert with
    /// <c>==</c>. And taking the magnitude's sine and negating it, rather than calling
    /// <c>Math.Sin</c> on a negative angle, guarantees that +theta and −theta produce exactly
    /// opposite Qbar16 terms, so a BALANCED stack's A16 cancels to exactly zero however
    /// awkward the angle.</para>
    /// </summary>
    internal static (double Cos, double Sin) CosSin(double degrees)
    {
        double sign = degrees < 0 ? -1.0 : 1.0;
        double magnitude = Math.Abs(degrees) % 360.0;
        (double c, double s) = magnitude switch
        {
            0.0 => (1.0, 0.0),
            90.0 => (0.0, 1.0),
            180.0 => (-1.0, 0.0),
            270.0 => (0.0, -1.0),
            _ => (Math.Cos(magnitude * Math.PI / 180.0), Math.Sin(magnitude * Math.PI / 180.0)),
        };
        return (c, sign * s);
    }

    /// <summary>
    /// One ply's contribution: the plane-stress reduced stiffness <c>Qbar</c> in laminate
    /// coordinates, plus the two blocks the mixed homogenisation needs.
    ///
    /// <para><b>The rotated 6x6 comes from <see cref="ElasticLaw.TransverselyIsotropic"/>
    /// rather than from a trigonometric expansion written here.</b> The Voigt rotation is
    /// the one piece of this arithmetic with a documented trap in it (the engineering-shear
    /// convention makes stress and strain transform by different matrices), it is already
    /// implemented and verified against an independent tensor-rotation oracle, and a second
    /// copy would be a second chance to get it wrong. Asking the shared rule is the
    /// recurring lesson.</para>
    ///
    /// <para><c>Qbar</c> is then the static condensation of the out-of-plane rows and
    /// columns, <c>C_ii − C_io C_oo⁻¹ C_oi</c>, which IS the plane-stress reduction: it is
    /// the stiffness seen when sigma_zz, tau_yz and tau_zx are free to relax to zero, which
    /// is what a thin ply's free surfaces do.</para>
    /// </summary>
    private static void PlyStiffness(
        in Ply ply, Span<double> qbar, Span<double> w, Span<double> cooInverse)
    {
        var (c, s) = CosSin(ply.AngleDegrees);
        var frame = Frame3d.FromOrthonormal(
            Vector3d.Zero, new Vector3d(c, s, 0), new Vector3d(-s, c, 0));
        var m = ply.Material;
        var law = ElasticLaw.TransverselyIsotropic(
            frame, m.E1, m.E2, m.Nu12, m.Nu23, m.G12, m.Name);
        var full = law.StiffnessMatrix;

        // Voigt order here is (xx, yy, zz, xy, yz, zx), so the IN-PLANE indices of a ply
        // whose free surfaces are the z faces are (0, 1, 3) and the out-of-plane ones
        // (2, 4, 5).
        ReadOnlySpan<int> inPlane = [0, 1, 3];
        ReadOnlySpan<int> outOfPlane = [2, 4, 5];

        Span<double> cii = stackalloc double[9];
        Span<double> cio = stackalloc double[9];
        Span<double> coo = stackalloc double[9];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                cii[i * 3 + j] = full[inPlane[i] * 6 + inPlane[j]];
                cio[i * 3 + j] = full[inPlane[i] * 6 + outOfPlane[j]];
                coo[i * 3 + j] = full[outOfPlane[i] * 6 + outOfPlane[j]];
            }
        }

        Invert3(coo, cooInverse, "a ply's out-of-plane stiffness block");
        Multiply3(cio, cooInverse, w);
        // Qbar = Cii - W · Cio', the static condensation.
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++)
                    sum += w[i * 3 + k] * cio[j * 3 + k];
                qbar[i * 3 + j] = cii[i * 3 + j] - sum;
            }
        }
        SymmetriseSpan(qbar);
    }

    /// <summary>
    /// Assembles the smeared 6x6 from the three averages (see the class remarks):
    /// <c>C* = [[P + Q·R⁻¹·Q', Q·R⁻¹], [R⁻¹·Q', R⁻¹]]</c>.
    /// <para>Symmetric BY CONSTRUCTION — the off-diagonal blocks are transposes of each
    /// other because <c>&lt;C_oo⁻¹ C_oi&gt;</c> is the transpose of <c>&lt;C_io C_oo⁻¹&gt;</c>
    /// — which is worth more than a symmetrisation pass would be: Maxwell reciprocity holds
    /// here because the construction is an energy statement, not because the last bits were
    /// averaged.</para>
    /// </summary>
    private void BuildSmeared(double[] p, double[] qSum, double[] rSum)
    {
        Span<double> rInverse = stackalloc double[9];
        Invert3(rSum, rInverse, "the laminate's averaged out-of-plane compliance");

        Span<double> qr = stackalloc double[9];           // Q · R⁻¹  (in-plane x out-of-plane)
        Multiply3(qSum, rInverse, qr);

        Span<double> ii = stackalloc double[9];           // P + Q·R⁻¹·Q'
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++)
                    sum += qr[i * 3 + k] * qSum[j * 3 + k];
                ii[i * 3 + j] = p[i * 3 + j] + sum;
            }
        }

        ReadOnlySpan<int> inPlane = [0, 1, 3];
        ReadOnlySpan<int> outOfPlane = [2, 4, 5];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                _smeared[inPlane[i] * 6 + inPlane[j]] = ii[i * 3 + j];
                _smeared[inPlane[i] * 6 + outOfPlane[j]] = qr[i * 3 + j];
                _smeared[outOfPlane[j] * 6 + inPlane[i]] = qr[i * 3 + j];
                _smeared[outOfPlane[i] * 6 + outOfPlane[j]] = rInverse[i * 3 + j];
            }
        }
        // The last bits only: the two products above are mathematically symmetric and the
        // blocks are placed as exact transposes, so this touches nothing but rounding — and
        // ElasticLaw.Anisotropic refuses a matrix that is not symmetric.
        for (int i = 0; i < 6; i++)
        {
            for (int j = i + 1; j < 6; j++)
            {
                double mean = 0.5 * (_smeared[i * 6 + j] + _smeared[j * 6 + i]);
                _smeared[i * 6 + j] = mean;
                _smeared[j * 6 + i] = mean;
            }
        }
    }

    /// <summary>Engineering constants from a 3x3 stiffness and the section factor that
    /// turns its inverse into a compliance per unit modulus (h for A, h³/12 for D).</summary>
    private static LaminateConstants Constants(double[] matrix, double section)
    {
        Span<double> inverse = stackalloc double[9];
        Invert3(matrix, inverse, "a laminate stiffness matrix");
        double ex = 1.0 / (section * inverse[0]);
        double ey = 1.0 / (section * inverse[4]);
        double gxy = 1.0 / (section * inverse[8]);
        double nuXy = -inverse[1] / inverse[0];
        double nuYx = nuXy * ey / ex;
        return new LaminateConstants(ex, ey, gxy, nuXy, nuYx);
    }

    private static void Multiply3(ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> result)
    {
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++)
                    sum += a[i * 3 + k] * b[k * 3 + j];
                result[i * 3 + j] = sum;
            }
        }
    }

    private static void Invert3(ReadOnlySpan<double> m, Span<double> result, string what)
    {
        double a = m[0], b = m[1], c = m[2];
        double d = m[3], e = m[4], f = m[5];
        double g = m[6], h = m[7], i = m[8];
        double c00 = e * i - f * h;
        double c01 = f * g - d * i;
        double c02 = d * h - e * g;
        double det = a * c00 + b * c01 + c * c02;

        double scale = 0;
        for (int k = 0; k < 9; k++)
            scale = Math.Max(scale, Math.Abs(m[k]));
        // Relative, never absolute: the entries are moduli in MPa and the same matrix in
        // another unit system must give the same verdict (the epsilon ladder's scale-free
        // tier). A determinant is cubic in the entries, hence the cube.
        if (!(Math.Abs(det) > 1e-14 * scale * scale * scale))
        {
            throw new FeaException(
                $"Cannot invert {what}: its determinant is {det:G6} against an entry scale of "
                + $"{scale:G6}, i.e. singular to working precision. The usual cause is a ply "
                + "with a zero or contradictory modulus.");
        }

        double inv = 1.0 / det;
        result[0] = c00 * inv;
        result[1] = (c * h - b * i) * inv;
        result[2] = (b * f - c * e) * inv;
        result[3] = c01 * inv;
        result[4] = (a * i - c * g) * inv;
        result[5] = (c * d - a * f) * inv;
        result[6] = c02 * inv;
        result[7] = (b * g - a * h) * inv;
        result[8] = (a * e - b * d) * inv;
    }

    private static double MaxAbs(ReadOnlySpan<double> values)
    {
        double worst = 0;
        foreach (double v in values)
            worst = Math.Max(worst, Math.Abs(v));
        return worst;
    }

    private static void Symmetrise(double[] m) => SymmetriseSpan(m);

    private static void SymmetriseSpan(Span<double> m)
    {
        for (int i = 0; i < 3; i++)
        {
            for (int j = i + 1; j < 3; j++)
            {
                double mean = 0.5 * (m[i * 3 + j] + m[j * 3 + i]);
                m[i * 3 + j] = mean;
                m[j * 3 + i] = mean;
            }
        }
    }
}
