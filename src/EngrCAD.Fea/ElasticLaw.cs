using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>
/// The constitutive law an element integrates: a 6x6 stiffness matrix D in the GLOBAL
/// frame, plus the thermal strain per degree that goes with it.
///
/// <para><b>Why this type exists beside <see cref="Material"/> rather than inside it.</b>
/// <see cref="Material"/> lives in <c>EngrCAD.Core</c> because the document model needs it
/// too — a part says what it is made of, a BOM weighs it, a viewer takes its colour. Those
/// consumers want a NAME, a DENSITY and a handful of scalars; none of them has any use for
/// a 21-constant elasticity tensor or a material FRAME, and a frame is not even a property
/// of the stuff — it is a property of how the stuff was laid into THIS part (a laminate's
/// ply direction, a rolled plate's rolling direction, a printed part's layer direction).
/// So the anisotropy is per-REGION analysis data, which is exactly what this project is,
/// and it composes with a <see cref="Material"/> rather than replacing one: the density a
/// modal solve integrates and the name a report prints still come from there.</para>
///
/// <para><b>The frame is applied ONCE, at construction.</b> D is stored rotated into global
/// coordinates, so the element loop never sees a frame and an anisotropic element costs
/// exactly one <c>B'DB</c> more than an isotropic one — the same "invert the basis once"
/// decision <c>LoftedSurface</c> makes about its cardinal basis, for the same reason: the
/// rotation is constant over the region, so recomputing it per element (or per quadrature
/// point) would be arithmetic with nothing to show for it.</para>
///
/// <para><b>An isotropic law keeps the index form.</b> <see cref="IsIsotropic"/> is set by
/// the <see cref="FromMaterial"/> factory only, and <c>TetElement.Stiffness</c> branches on
/// it, so every model that does not ask for anisotropy assembles through exactly the
/// arithmetic it always did — bit for bit, which is what makes this feature safe to add
/// under a verification suite whose headline numbers are quoted to twelve digits. The
/// general path is asserted against the index form on an isotropic law, so the two cannot
/// drift in MEANING while staying separate in ARITHMETIC.</para>
///
/// <para>Voigt order is <c>(xx, yy, zz, xy, yz, zx)</c> with engineering shear strains,
/// matching <see cref="Material.Stress"/> and <c>TetElement.StrainAt</c>.</para>
/// </summary>
public sealed class ElasticLaw
{
    private readonly double[] _d;
    private readonly double[] _thermalStrain;
    private readonly double[] _thermalStress;
    private double[]? _compliance;

    private ElasticLaw(
        double[] d,
        double[] thermalStrain,
        bool isotropic,
        bool statesOwnExpansion,
        double lambda,
        double mu,
        Frame3d frame,
        string description)
    {
        _d = d;
        _thermalStrain = thermalStrain;
        IsIsotropic = isotropic;
        StatesOwnThermalExpansion = statesOwnExpansion;
        Lambda = lambda;
        Mu = mu;
        Frame = frame;
        Description = description;

        _thermalStress = new double[6];
        MultiplyD(thermalStrain, _thermalStress);
        HasThermalStrain = false;
        for (int i = 0; i < 6; i++)
        {
            // Exact-zero semantic test (the ladder's scale-free tier): "no expansion
            // stated" is a case, not a small number. It matches Material's own convention
            // and StructuralResults' subtraction guard.
            if (thermalStrain[i] != 0)
                HasThermalStrain = true;
        }
    }

    /// <summary>True when the law came from an isotropic <see cref="Material"/>, which is
    /// what lets the assembly take the index form.</summary>
    public bool IsIsotropic { get; }

    /// <summary>
    /// The material axes in global coordinates — the frame the stiffness was rotated BY,
    /// kept so a consumer can rotate a stress back INTO them.
    ///
    /// <para><b>It is retained here rather than restated beside a strength set</b>, and the
    /// reason is the recurring one: a directional strength (<see cref="LaminaStrength"/>)
    /// is quoted along the same 1-2-3 axes the moduli are, so a second copy of the frame
    /// would be a second spelling of one fact, free to drift from the stiffness it is
    /// supposed to describe. <see cref="ToMaterialFrame"/> is the one way to ask.</para>
    ///
    /// <para>Meaningless but harmless for an isotropic law, where it is
    /// <see cref="Frame3d.WorldXY"/>: rotating an isotropic stress state changes its
    /// components and no isotropic consumer reads them.</para>
    /// </summary>
    public Frame3d Frame { get; }

    /// <summary>
    /// True when <see cref="WithThermalExpansion"/> stated the expansion HERE, so the
    /// <see cref="Material"/>'s scalar coefficient is not consulted.
    ///
    /// <para><b>This is a separate flag from <see cref="IsIsotropic"/> on purpose.</b> That
    /// one governs the STIFFNESS path, where its whole job is to keep an isotropic model
    /// bit-identical; reusing it for the thermal path would make
    /// <c>FromMaterial(m).WithThermalExpansion(...)</c> silently ignored — an elastically
    /// isotropic, thermally directional material is unusual but real, and the failure mode
    /// would be a stated coefficient with no effect, which is the worst kind.</para>
    /// </summary>
    public bool StatesOwnThermalExpansion { get; }

    /// <summary>Lame's first parameter — meaningful only when <see cref="IsIsotropic"/>.</summary>
    public double Lambda { get; }

    /// <summary>Shear modulus — meaningful only when <see cref="IsIsotropic"/>.</summary>
    public double Mu { get; }

    /// <summary>True when this law states any thermal expansion.</summary>
    public bool HasThermalStrain { get; }

    /// <summary>A short human-readable statement of what the law is, for reports and
    /// refusal messages.</summary>
    public string Description { get; }

    /// <summary>The 6x6 stiffness matrix in GLOBAL coordinates, row-major.</summary>
    public ReadOnlySpan<double> StiffnessMatrix => _d;

    /// <summary>
    /// The 6x6 COMPLIANCE matrix in global coordinates, row-major — the inverse of
    /// <see cref="StiffnessMatrix"/>, computed once on first use.
    ///
    /// <para>It exists for the energy inner product an error estimator needs
    /// (<c>dsigma' S dsigma</c>), which is why it is a matrix rather than a solve: the
    /// estimator evaluates it at every quadrature point of every element, and a cached
    /// inverse turns a factorization per point into a symmetric product. Formed by
    /// back-substituting the identity through a Cholesky of D, which exists because D is
    /// positive definite — checked at construction for a stated matrix, and implied by the
    /// compliance being positive definite for the derived ones.</para>
    /// </summary>
    public ReadOnlySpan<double> ComplianceMatrix => _compliance ??= InvertStiffness();

    private double[] InvertStiffness()
    {
        var l = Cholesky(_d, 6, 6);
        var s = new double[36];
        Span<double> y = stackalloc double[6];
        for (int c = 0; c < 6; c++)
        {
            for (int i = 0; i < 6; i++)
            {
                double sum = i == c ? 1.0 : 0.0;
                for (int k = 0; k < i; k++)
                    sum -= l[i * 6 + k] * y[k];
                y[i] = sum / l[i * 6 + i];
            }
            for (int i = 5; i >= 0; i--)
            {
                double sum = y[i];
                for (int k = i + 1; k < 6; k++)
                    sum -= l[k * 6 + i] * s[k * 6 + c];
                s[i * 6 + c] = sum / l[i * 6 + i];
            }
        }
        // Symmetrise the last bits, as the rotation does: the inverse of a symmetric matrix
        // is symmetric, and a quadratic form built from a nearly-symmetric one is not.
        for (int i = 0; i < 6; i++)
        {
            for (int j = i + 1; j < 6; j++)
            {
                double mean = 0.5 * (s[i * 6 + j] + s[j * 6 + i]);
                s[i * 6 + j] = mean;
                s[j * 6 + i] = mean;
            }
        }
        return s;
    }

    /// <summary>
    /// The free thermal strain per degree, as a Voigt ENGINEERING-shear 6-vector in global
    /// coordinates. Rotating an orthotropic expansion off-axis genuinely produces shear
    /// terms — a heated off-axis lamina shears — which is exactly why this is a 6-vector
    /// and not three numbers.
    /// </summary>
    public ReadOnlySpan<double> ThermalStrainPerDegree => _thermalStrain;

    /// <summary>
    /// <c>D · alpha</c>: the stress a fully restrained unit temperature rise would produce,
    /// which is the vector the thermal LOAD integral needs. Formed once here so the load and
    /// the stress recovery cannot use different constitutive matrices.
    /// </summary>
    public ReadOnlySpan<double> ThermalStressPerDegree => _thermalStress;

    /// <summary>Stress from strain, both Voigt 6-vectors with engineering shear.</summary>
    public void Stress(ReadOnlySpan<double> strain, Span<double> stress)
    {
        if (IsIsotropic)
        {
            // The same three-constant expression Material.Stress uses, reached through the
            // same code, so an isotropic law's recovered stress is bit-identical to what it
            // was before this type existed.
            double trace = strain[0] + strain[1] + strain[2];
            double lt = Lambda * trace;
            double twoMu = 2.0 * Mu;
            stress[0] = lt + twoMu * strain[0];
            stress[1] = lt + twoMu * strain[1];
            stress[2] = lt + twoMu * strain[2];
            stress[3] = Mu * strain[3];
            stress[4] = Mu * strain[4];
            stress[5] = Mu * strain[5];
            return;
        }
        MultiplyD(strain, stress);
    }

    /// <summary>
    /// A stress tensor written in the MATERIAL frame's own 1-2-3 axes — the components a
    /// directional failure criterion is stated in.
    ///
    /// <para><c>sigma_material = R' · sigma_global · R</c> with R the frame's axes as
    /// columns, evaluated as <c>e_i · (sigma · e_j)</c> so the whole thing is six dot
    /// products and no Voigt vector, no engineering-shear convention and no Bond
    /// transformation appear. That is deliberate: the STIFFNESS rotation next door goes
    /// through the Voigt stress transformation, and keeping the two derivations apart is
    /// what stopped a transposed rotation being invisible when this file was first
    /// written.</para>
    /// </summary>
    /// <param name="globalStress">A stress tensor in global coordinates.</param>
    public SymmetricTensor3 ToMaterialFrame(in SymmetricTensor3 globalStress)
    {
        var x = Frame.X;
        var y = Frame.Y;
        var z = Frame.Z;
        var sx = globalStress.Multiply(x);
        var sy = globalStress.Multiply(y);
        var sz = globalStress.Multiply(z);
        return new SymmetricTensor3(
            x.Dot(sx), y.Dot(sy), z.Dot(sz),
            x.Dot(sy), x.Dot(sz), y.Dot(sz));
    }

    private void MultiplyD(ReadOnlySpan<double> strain, Span<double> stress)
    {
        for (int i = 0; i < 6; i++)
        {
            double sum = 0;
            int row = i * 6;
            for (int j = 0; j < 6; j++)
                sum += _d[row + j] * strain[j];
            stress[i] = sum;
        }
    }

    // ---- factories -------------------------------------------------------------------

    /// <summary>
    /// The isotropic law a <see cref="Material"/> states. This is what every region gets
    /// unless a caller says otherwise, and it is the one law that keeps the index-form
    /// stiffness.
    /// </summary>
    public static ElasticLaw FromMaterial(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);
        double alpha = material.ThermalExpansion;
        return new ElasticLaw(
            material.ConstitutiveMatrix(),
            [alpha, alpha, alpha, 0, 0, 0],
            isotropic: true,
            statesOwnExpansion: false,
            material.Lambda,
            material.Mu,
            Frame3d.WorldXY,
            $"isotropic ({material.Name})");
    }

    /// <summary>
    /// An orthotropic law from the nine engineering constants, stated in the material frame
    /// and rotated into global coordinates by <paramref name="frame"/>.
    ///
    /// <para>Poisson's ratios follow the usual convention <c>nu_ij = -eps_j / eps_i</c> for
    /// a stress along <c>i</c>, so the MAJOR ratios are the ones quoted on a composite
    /// datasheet (<c>nu12</c> for a fibre-direction pull). The minor ratios are DERIVED from
    /// the symmetry of the compliance matrix, <c>nu_ji / E_j = nu_ij / E_i</c>, rather than
    /// taken as separate inputs — supplying both is how a data sheet transcription comes to
    /// contradict itself, and the contradiction would make the matrix non-symmetric with
    /// nothing to catch it.</para>
    /// </summary>
    /// <param name="frame">The material axes in global coordinates. Only the AXES are read;
    /// the origin is ignored, since a constitutive law has no position.</param>
    /// <param name="e1">Young's modulus along the frame's X axis, MPa.</param>
    /// <param name="e2">Young's modulus along Y, MPa.</param>
    /// <param name="e3">Young's modulus along Z, MPa.</param>
    /// <param name="nu12">Major Poisson's ratio for a pull along X (contraction along Y).</param>
    /// <param name="nu23">Major Poisson's ratio for a pull along Y (contraction along Z).</param>
    /// <param name="nu13">Major Poisson's ratio for a pull along X (contraction along Z).</param>
    /// <param name="g12">Shear modulus in the XY plane, MPa.</param>
    /// <param name="g23">Shear modulus in the YZ plane, MPa.</param>
    /// <param name="g13">Shear modulus in the ZX plane, MPa.</param>
    /// <param name="name">Optional label for reports.</param>
    public static ElasticLaw Orthotropic(
        Frame3d frame,
        double e1, double e2, double e3,
        double nu12, double nu23, double nu13,
        double g12, double g23, double g13,
        string? name = null)
    {
        RequirePositive(e1, nameof(e1));
        RequirePositive(e2, nameof(e2));
        RequirePositive(e3, nameof(e3));
        RequirePositive(g12, nameof(g12));
        RequirePositive(g23, nameof(g23));
        RequirePositive(g13, nameof(g13));

        // Compliance in the MATERIAL frame. The normal block is symmetric by construction:
        // S[i][j] is written once from the major ratio and mirrored, which is what makes
        // nu_ji implicit rather than a second input that could disagree.
        var s = new double[36];
        s[0] = 1.0 / e1;
        s[7] = 1.0 / e2;
        s[14] = 1.0 / e3;
        s[1] = s[6] = -nu12 / e1;
        s[2] = s[12] = -nu13 / e1;
        s[8] = s[13] = -nu23 / e2;
        s[21] = 1.0 / g12;
        s[28] = 1.0 / g23;
        s[35] = 1.0 / g13;

        var d = InvertCompliance(s, name ?? "orthotropic");
        RotateInPlace(d, frame);

        string label = name ?? "orthotropic";
        return new ElasticLaw(
            d, new double[6], isotropic: false, statesOwnExpansion: false, 0, 0, frame, label);
    }

    /// <summary>
    /// A transversely isotropic law — the common composite idealisation, where the material
    /// is isotropic in the plane PERPENDICULAR to the fibre. Sugar over
    /// <see cref="Orthotropic"/>: five independent constants instead of nine, with
    /// <c>E2 = E3</c>, <c>nu13 = nu12</c>, <c>G13 = G12</c> and the transverse shear
    /// <c>G23 = E2 / (2(1 + nu23))</c> DERIVED, because in an isotropic plane the shear
    /// modulus is not free.
    /// </summary>
    /// <param name="frame">Material axes; X is the fibre direction.</param>
    /// <param name="eAxial">Modulus along the fibre, MPa.</param>
    /// <param name="eTransverse">Modulus across the fibre, MPa.</param>
    /// <param name="nuAxialTransverse">Major ratio for an axial pull.</param>
    /// <param name="nuTransverse">In-plane ratio of the isotropic transverse plane.</param>
    /// <param name="gAxial">Axial-transverse shear modulus, MPa.</param>
    /// <param name="name">Optional label.</param>
    public static ElasticLaw TransverselyIsotropic(
        Frame3d frame,
        double eAxial,
        double eTransverse,
        double nuAxialTransverse,
        double nuTransverse,
        double gAxial,
        string? name = null)
    {
        RequirePositive(eTransverse, nameof(eTransverse));
        if (nuTransverse <= -1 || nuTransverse >= 0.5)
        {
            throw new FeaException(
                $"The transverse plane's Poisson's ratio {nuTransverse:G6} is outside "
                + "(-1, 0.5). That plane is ISOTROPIC by the definition of a transversely "
                + "isotropic material, so its ratio obeys an isotropic material's bounds.");
        }
        return Orthotropic(
            frame,
            eAxial, eTransverse, eTransverse,
            nuAxialTransverse, nuTransverse, nuAxialTransverse,
            gAxial, eTransverse / (2.0 * (1.0 + nuTransverse)), gAxial,
            name ?? "transversely isotropic");
    }

    /// <summary>
    /// A fully general anisotropic law from its 6x6 stiffness matrix, stated in the material
    /// frame. All 21 independent constants are the caller's to supply; the matrix must be
    /// symmetric and positive definite, and both are checked by name.
    /// </summary>
    /// <param name="frame">Material axes in global coordinates.</param>
    /// <param name="stiffness">36 entries, row-major, Voigt order (xx, yy, zz, xy, yz, zx)
    /// with engineering shear strains.</param>
    /// <param name="name">Optional label.</param>
    public static ElasticLaw Anisotropic(
        Frame3d frame, ReadOnlySpan<double> stiffness, string? name = null)
    {
        if (stiffness.Length != 36)
        {
            throw new FeaException(
                $"An anisotropic stiffness matrix is 6x6 = 36 entries, row-major; "
                + $"{stiffness.Length} were supplied.");
        }
        var d = stiffness.ToArray();

        double scale = 0;
        for (int i = 0; i < 36; i++)
            scale = Math.Max(scale, Math.Abs(d[i]));
        // Relative, never absolute: a stiffness is in MPa and the same matrix in another
        // unit system would fail an absolute test (the epsilon ladder's scale-free tier).
        double tolerance = 1e-12 * Math.Max(scale, double.Epsilon);
        for (int i = 0; i < 6; i++)
        {
            for (int j = i + 1; j < 6; j++)
            {
                if (Math.Abs(d[i * 6 + j] - d[j * 6 + i]) > tolerance)
                {
                    throw new FeaException(
                        $"The stiffness matrix is not symmetric: D[{i},{j}] = "
                        + $"{d[i * 6 + j]:G6} against D[{j},{i}] = {d[j * 6 + i]:G6}. A "
                        + "hyperelastic material's tensor has the major symmetry by the "
                        + "existence of a strain-energy function, so an asymmetric matrix "
                        + "is a transcription error rather than an exotic material.");
                }
                // Symmetrise the last bits so the assembled stiffness is symmetric exactly
                // rather than nearly, which is what SparseCholesky's upper-triangular
                // storage assumes.
                double mean = 0.5 * (d[i * 6 + j] + d[j * 6 + i]);
                d[i * 6 + j] = mean;
                d[j * 6 + i] = mean;
            }
        }
        RequirePositiveDefinite(d, 6, stride: 6, name ?? "anisotropic", "stiffness");
        RotateInPlace(d, frame);
        return new ElasticLaw(
            d, new double[6], isotropic: false, statesOwnExpansion: false, 0, 0, frame,
            name ?? "anisotropic");
    }

    /// <summary>
    /// The same law with a thermal expansion stated as three coefficients along the material
    /// frame's own axes (1/K). Directional expansion is the normal case for the materials
    /// this type exists for — a carbon lamina is very nearly zero along the fibre and large
    /// across it — so it is a vector rather than a scalar, and it is rotated into global
    /// coordinates with the same frame the stiffness was.
    /// </summary>
    public ElasticLaw WithThermalExpansion(
        Frame3d frame, double alpha1, double alpha2, double alpha3)
    {
        // The expansion is a symmetric TENSOR, so it rotates as A_g = R A_m R', which for a
        // diagonal A_m is one outer-product sum per axis. Building the tensor and reading
        // its Voigt engineering form is deliberately simpler than pushing the Voigt vector
        // through the strain transformation matrix - the two are the same map, and this one
        // needs no inverse.
        var x = frame.X;
        var y = frame.Y;
        var z = frame.Z;
        double axx = alpha1 * x.X * x.X + alpha2 * y.X * y.X + alpha3 * z.X * z.X;
        double ayy = alpha1 * x.Y * x.Y + alpha2 * y.Y * y.Y + alpha3 * z.Y * z.Y;
        double azz = alpha1 * x.Z * x.Z + alpha2 * y.Z * y.Z + alpha3 * z.Z * z.Z;
        double axy = alpha1 * x.X * x.Y + alpha2 * y.X * y.Y + alpha3 * z.X * z.Y;
        double ayz = alpha1 * x.Y * x.Z + alpha2 * y.Y * y.Z + alpha3 * z.Y * z.Z;
        double azx = alpha1 * x.Z * x.X + alpha2 * y.Z * y.X + alpha3 * z.Z * z.X;
        // Engineering shear: the Voigt strain vector carries 2 * the tensor component.
        double[] strain = [axx, ayy, azz, 2.0 * axy, 2.0 * ayz, 2.0 * azx];
        // The stiffness matrix is SHARED by reference: it is never mutated after
        // construction, and two laws differing only in expansion are the same elasticity.
        return new ElasticLaw(
            _d, strain, IsIsotropic, statesOwnExpansion: true, Lambda, Mu, Frame, Description);
    }

    /// <inheritdoc/>
    public override string ToString() => Description;

    // ---- linear algebra --------------------------------------------------------------

    private static void RequirePositive(double value, string name)
    {
        if (!(value > 0))
        {
            throw new FeaException(
                $"{name} must be positive; {value:G6} was supplied. A zero modulus is not a "
                + "soft material — it makes the stiffness singular, so the refusal is here "
                + "rather than at the factorization.");
        }
    }

    /// <summary>
    /// Inverts the compliance matrix. The normal 3x3 block and the three shear reciprocals
    /// are independent, so this is a 3x3 inverse and three divisions rather than a 6x6
    /// solve — and the 3x3 is inverted through a Cholesky, which is what makes the
    /// positive-definiteness check and the inversion ONE computation instead of two that
    /// could disagree.
    /// </summary>
    private static double[] InvertCompliance(double[] s, string name)
    {
        // The leading 3x3 block of a 6x6 array: the stride stays 6, which is exactly the
        // sort of thing a shared routine must be TOLD rather than infer from the order.
        RequirePositiveDefinite(s, 3, stride: 6, name, "compliance");

        double a = s[0], b = s[1], c = s[2];
        double e = s[7], f = s[8];
        double h = s[14];
        // Cofactors of the symmetric 3x3 [[a,b,c],[b,e,f],[c,f,h]].
        double c00 = e * h - f * f;
        double c01 = c * f - b * h;
        double c02 = b * f - c * e;
        double det = a * c00 + b * c01 + c * c02;
        double inv = 1.0 / det;

        var d = new double[36];
        d[0] = c00 * inv;
        d[1] = d[6] = c01 * inv;
        d[2] = d[12] = c02 * inv;
        d[7] = (a * h - c * c) * inv;
        d[8] = d[13] = (b * c - a * f) * inv;
        d[14] = (a * e - b * b) * inv;
        d[21] = 1.0 / s[21];
        d[28] = 1.0 / s[28];
        d[35] = 1.0 / s[35];
        return d;
    }

    /// <summary>
    /// Refuses a matrix that is not positive definite, by attempting a Cholesky and naming
    /// the leading minor that failed.
    ///
    /// <para><b>This is the check that catches a physically impossible material</b>, and it
    /// is worth more than a per-constant bound. The classical orthotropic restrictions
    /// (<c>|nu_ij| &lt; sqrt(E_i/E_j)</c> and a positive determinant) are exactly the
    /// statement that the compliance matrix is positive definite; testing them one at a time
    /// re-derives that statement in a form that is easy to get subtly wrong, where a
    /// Cholesky IS the statement. Without it a plausible-looking data sheet transcription
    /// gives a stiffness matrix with a negative eigenvalue — a material that RELEASES energy
    /// when strained — and the symptom is a factorization failure deep in the solver rather
    /// than a message about the material.</para>
    /// </summary>
    private static void RequirePositiveDefinite(
        double[] m, int n, int stride, string name, string what)
    {
        try
        {
            Cholesky(m, n, stride);
        }
        catch (FeaException ex)
        {
            throw new FeaException(
                $"The {what} matrix of '{name}' is not positive definite ({ex.Message}). Such "
                + "a material would release energy under some strain, which is unphysical; for "
                + "an orthotropic law the usual cause is a Poisson's ratio too large for its "
                + "modulus ratio — the bound is |nu_ij| < sqrt(E_i / E_j), with a further "
                + "condition coupling all three, and the pairwise bounds alone are NOT "
                + "sufficient.");
        }
    }

    /// <summary>
    /// The lower Cholesky factor of the leading n-by-n block of a row-major matrix of the
    /// given stride, throwing when a leading minor is non-positive.
    /// <para><b>The stride is a parameter rather than inferred from n</b>, because the same
    /// routine factors the 3x3 compliance block that lives INSIDE a 6x6 array and the whole
    /// 6x6. Inferring it read the right numbers from the wrong places and refused a
    /// perfectly valid isotropic compliance.</para>
    /// </summary>
    private static double[] Cholesky(double[] m, int n, int stride)
    {
        var l = new double[n * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double sum = m[i * stride + j];
                for (int k = 0; k < j; k++)
                    sum -= l[i * n + k] * l[j * n + k];
                if (i == j)
                {
                    if (!(sum > 0))
                        throw new FeaException($"leading minor {i + 1} of {n} failed");
                    l[i * n + j] = Math.Sqrt(sum);
                }
                else
                {
                    l[i * n + j] = sum / l[j * n + j];
                }
            }
        }
        return l;
    }

    /// <summary>
    /// Rotates a 6x6 stiffness matrix from the material frame into global coordinates:
    /// <c>D_global = K · D_material · K'</c>, with K the Voigt STRESS transformation.
    ///
    /// <para>The K-on-both-sides form (rather than <c>K D K^-1</c>) follows from the strain
    /// vector transforming by <c>K^-T</c>, which itself follows from the strain energy
    /// <c>s'e</c> being invariant — so the transformation is derived rather than
    /// transcribed, which matters because the engineering-shear convention makes the stress
    /// and strain Voigt vectors transform by DIFFERENT matrices and swapping them is the
    /// classic error in this formula.</para>
    /// </summary>
    private static void RotateInPlace(double[] d, Frame3d frame)
    {
        // R maps material components to global, so its columns are the material axes
        // expressed in global coordinates.
        var x = frame.X;
        var y = frame.Y;
        var z = frame.Z;
        double a11 = x.X, a12 = y.X, a13 = z.X;
        double a21 = x.Y, a22 = y.Y, a23 = z.Y;
        double a31 = x.Z, a32 = y.Z, a33 = z.Z;

        // The world frame is the overwhelmingly common case and is EXACTLY the identity
        // here; skipping it is a semantic exact-zero test, not an optimisation, because it
        // keeps an unrotated law's matrix bit-for-bit the one the caller stated.
        if (frame.X == Vector3d.UnitX && frame.Y == Vector3d.UnitY && frame.Z == Vector3d.UnitZ)
            return;

        Span<double> k = stackalloc double[36];
        // Rows 0..2: the normal stresses.
        Fill(k, 0, a11 * a11, a12 * a12, a13 * a13, 2 * a11 * a12, 2 * a12 * a13, 2 * a13 * a11);
        Fill(k, 1, a21 * a21, a22 * a22, a23 * a23, 2 * a21 * a22, 2 * a22 * a23, 2 * a23 * a21);
        Fill(k, 2, a31 * a31, a32 * a32, a33 * a33, 2 * a31 * a32, 2 * a32 * a33, 2 * a33 * a31);
        // Rows 3..5: the shear stresses, in the same (xy, yz, zx) order.
        Fill(k, 3, a11 * a21, a12 * a22, a13 * a23,
            a11 * a22 + a12 * a21, a12 * a23 + a13 * a22, a13 * a21 + a11 * a23);
        Fill(k, 4, a21 * a31, a22 * a32, a23 * a33,
            a21 * a32 + a22 * a31, a22 * a33 + a23 * a32, a23 * a31 + a21 * a33);
        Fill(k, 5, a31 * a11, a32 * a12, a33 * a13,
            a31 * a12 + a32 * a11, a32 * a13 + a33 * a12, a33 * a11 + a31 * a13);

        Span<double> kd = stackalloc double[36];
        for (int i = 0; i < 6; i++)
        {
            for (int j = 0; j < 6; j++)
            {
                double sum = 0;
                for (int p = 0; p < 6; p++)
                    sum += k[i * 6 + p] * d[p * 6 + j];
                kd[i * 6 + j] = sum;
            }
        }
        for (int i = 0; i < 6; i++)
        {
            for (int j = 0; j < 6; j++)
            {
                double sum = 0;
                for (int p = 0; p < 6; p++)
                    sum += kd[i * 6 + p] * k[j * 6 + p];
                d[i * 6 + j] = sum;
            }
        }
        // Symmetrise the last bits: the product is symmetric mathematically, and the
        // assembled global matrix is stored upper-triangular, so a few-ulp asymmetry would
        // be silently resolved in favour of whichever entry the packer saw.
        for (int i = 0; i < 6; i++)
        {
            for (int j = i + 1; j < 6; j++)
            {
                double mean = 0.5 * (d[i * 6 + j] + d[j * 6 + i]);
                d[i * 6 + j] = mean;
                d[j * 6 + i] = mean;
            }
        }

        static void Fill(Span<double> k, int row, double c0, double c1, double c2,
            double c3, double c4, double c5)
        {
            int at = row * 6;
            k[at] = c0;
            k[at + 1] = c1;
            k[at + 2] = c2;
            k[at + 3] = c3;
            k[at + 4] = c4;
            k[at + 5] = c5;
        }
    }
}
