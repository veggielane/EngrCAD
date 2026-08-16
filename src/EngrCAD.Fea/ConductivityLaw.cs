using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>
/// The constitutive law a conduction element integrates: a symmetric positive-definite 3x3
/// thermal conductivity tensor K in the GLOBAL frame, so the heat flux is
/// <c>q = -K·grad T</c>.
///
/// <para><b>It sits BESIDE <see cref="Material"/> and <see cref="ElasticLaw"/> rather than
/// inside either, and the decision was the repo owner's.</b> A directional conductivity needs
/// a material FRAME — which way the carbon fibres run, which way the plate was rolled — and a
/// frame is not a property of the STUFF, it is a property of how the stuff was laid into THIS
/// part. So it is per-region analysis data, exactly as <see cref="ElasticLaw"/> is, and it
/// composes with a <see cref="Material"/> rather than replacing one: the density a modal solve
/// integrates and the name a report prints still come from there, and the scalar
/// <see cref="Material.ThermalConductivity"/> a simple model states is the isotropic law
/// <see cref="FromMaterial"/> gives. It is a SEPARATE type from <see cref="ElasticLaw"/> and
/// NOT a combined <c>MaterialLaw</c> carrying both halves — the two laws share a frame CONCEPT
/// but not a frame VALUE (a laminate's fibre direction is not the same fact as its conduction
/// axes need be), and one object carrying both would make the name a claim it cannot keep.</para>
///
/// <para><b>The frame is applied ONCE, at construction.</b> K is stored rotated into global
/// coordinates by a rank-2 tensor CONGRUENCE <c>K_global = R·K_material·Rᵀ</c> — simpler than
/// the elastic 6x6 Voigt rotation because a conductivity is an ordinary rank-2 tensor with no
/// engineering-shear convention to trap the derivation — so the element loop never sees a
/// frame, and <see cref="ThermalModel"/> caches the law per REGION so the rotation is paid once
/// per model, the same "invert the basis once" call <c>ElasticLaw</c> makes.</para>
///
/// <para><b>An isotropic law keeps the SCALAR path.</b> <see cref="IsIsotropic"/> is set by the
/// <see cref="FromMaterial"/> factory only, and every consumer branches on it, so a model that
/// does not ask for anisotropy assembles through exactly the arithmetic it always did — bit for
/// bit, which is what makes this feature safe to add under a verification suite whose headline
/// thermal figures are quoted to twelve digits.</para>
/// </summary>
public sealed class ConductivityLaw
{
    private readonly double[] _k;          // 3x3 global, row-major (9 entries)
    private double[]? _resistivity;        // 3x3 global inverse K^-1, lazy

    private ConductivityLaw(
        double[] k, bool isotropic, double isotropicConductivity, string description)
    {
        _k = k;
        IsIsotropic = isotropic;
        IsotropicConductivity = isotropicConductivity;
        Description = description;
    }

    /// <summary>True when the law came from an isotropic <see cref="Material"/>, which is what
    /// lets every consumer take the scalar path.</summary>
    public bool IsIsotropic { get; }

    /// <summary>The scalar conductivity k — meaningful only when <see cref="IsIsotropic"/>, and
    /// then exactly the <see cref="Material.ThermalConductivity"/> the law came from, so the
    /// scalar path is bit-for-bit what it was before this type existed.</summary>
    public double IsotropicConductivity { get; }

    /// <summary>A short human-readable statement of what the law is, for reports and refusal
    /// messages.</summary>
    public string Description { get; }

    /// <summary>The 3x3 conductivity tensor in GLOBAL coordinates, row-major.</summary>
    public ReadOnlySpan<double> ConductivityMatrix => _k;

    /// <summary>
    /// The 3x3 RESISTIVITY tensor <c>K⁻¹</c> in global coordinates, row-major — the inverse of
    /// <see cref="ConductivityMatrix"/>, computed once on first use.
    ///
    /// <para>It exists for the complementary energy inner product the flux error estimator
    /// needs (<c>q·K⁻¹·q</c>), the thermal analogue of the elastic compliance the ZZ estimator
    /// uses, which is why it is a stored inverse rather than a solve: the estimator evaluates it
    /// at every quadrature point of every element. It exists because K is positive definite,
    /// which is checked at construction for a stated tensor.</para>
    /// </summary>
    public ReadOnlySpan<double> ResistivityMatrix => _resistivity ??= Invert(_k);

    /// <summary>
    /// The heat flux <c>q = -K·grad T</c> for a temperature gradient.
    /// <para>The isotropic branch returns <c>gradient · -k</c> exactly, so a flux read off an
    /// isotropic model is bit-for-bit what it was before the tensor existed.</para>
    /// </summary>
    public Vector3d Flux(in Vector3d gradient)
    {
        if (IsIsotropic)
            return gradient * -IsotropicConductivity;
        return new Vector3d(
            -(_k[0] * gradient.X + _k[1] * gradient.Y + _k[2] * gradient.Z),
            -(_k[3] * gradient.X + _k[4] * gradient.Y + _k[5] * gradient.Z),
            -(_k[6] * gradient.X + _k[7] * gradient.Y + _k[8] * gradient.Z));
    }

    /// <summary>
    /// The complementary energy density of a flux, <c>q·K⁻¹·q</c> — the norm the ZZ error
    /// estimate measures a flux difference in, and <c>k·|grad T|²</c> by another name.
    /// <para>The isotropic branch returns <c>|q|²/k</c> exactly, which is the arithmetic the
    /// thermal error estimate has always used, so its measured figures do not move.</para>
    /// </summary>
    public double FluxEnergy(in Vector3d q)
    {
        if (IsIsotropic)
            return q.LengthSquared / IsotropicConductivity;
        var r = _resistivity ??= Invert(_k);
        double rx = r[0] * q.X + r[1] * q.Y + r[2] * q.Z;
        double ry = r[3] * q.X + r[4] * q.Y + r[5] * q.Z;
        double rz = r[6] * q.X + r[7] * q.Y + r[8] * q.Z;
        return q.X * rx + q.Y * ry + q.Z * rz;
    }

    // ---- factories -------------------------------------------------------------------

    /// <summary>
    /// The isotropic law a <see cref="Material"/> states, <c>k·I</c>. This is what every region
    /// gets unless a caller says otherwise, and it is the one law that keeps the scalar path.
    /// <para>Deliberately does NOT check positive-definiteness: a document material may state a
    /// zero conductivity (it is a valid thing to describe), and the refusal for a conduction
    /// solve of a zero-conductivity body belongs at the point of use, where it can name the
    /// property — <see cref="ElasticLaw.FromMaterial"/> makes the same choice about a zero
    /// modulus for the same reason.</para>
    /// </summary>
    /// <summary>An isotropic law from a bare scalar — the nonlinear solver's per-element
    /// linearization point (<see cref="ThermalNonlinear"/>).</summary>
    internal static ConductivityLaw Isotropic(double k) =>
        new([k, 0, 0, 0, k, 0, 0, 0, k], isotropic: true, k, "isotropic (nonlinear)");

    public static ConductivityLaw FromMaterial(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);
        double k = material.ThermalConductivity;
        return new ConductivityLaw(
            [k, 0, 0, 0, k, 0, 0, 0, k], isotropic: true, k, $"isotropic ({material.Name})");
    }

    /// <summary>
    /// An orthotropic law from three conductivities along the material frame's own axes, rotated
    /// into global coordinates by <paramref name="frame"/>. The common case: a laminate conducts
    /// well along the fibres and poorly across them.
    /// </summary>
    /// <param name="frame">The material axes in global coordinates. Only the AXES are read; the
    /// origin is ignored, since a conductivity has no position.</param>
    /// <param name="kx">Conductivity along the frame's X axis (mW/(mm·K), the SI W/(m·K)).</param>
    /// <param name="ky">Conductivity along Y.</param>
    /// <param name="kz">Conductivity along Z.</param>
    /// <param name="name">Optional label for reports.</param>
    public static ConductivityLaw Orthotropic(
        Frame3d frame, double kx, double ky, double kz, string? name = null)
    {
        RequirePositive(kx, nameof(kx));
        RequirePositive(ky, nameof(ky));
        RequirePositive(kz, nameof(kz));
        // A diagonal conductivity is positive definite exactly when its three entries are
        // positive, which the RequirePositive calls above have already established, so the
        // Cholesky the Anisotropic path runs would be redundant here.
        var k = new double[] { kx, 0, 0, 0, ky, 0, 0, 0, kz };
        RotateInPlace(k, frame);
        return new ConductivityLaw(k, isotropic: false, 0, name ?? "orthotropic");
    }

    /// <summary>
    /// A fully general anisotropic law from its symmetric 3x3 conductivity tensor, stated in the
    /// material frame. The tensor must be symmetric (Onsager reciprocity makes a conductivity
    /// tensor symmetric, so an asymmetric input is a transcription error, refused by name) and
    /// positive definite (heat flows down its own gradient, so an indefinite tensor would let it
    /// flow up one somewhere — refused with the leading minor that failed).
    /// </summary>
    /// <param name="frame">Material axes in global coordinates.</param>
    /// <param name="conductivity">9 entries, row-major, the symmetric material-frame tensor.</param>
    /// <param name="name">Optional label.</param>
    public static ConductivityLaw Anisotropic(
        Frame3d frame, ReadOnlySpan<double> conductivity, string? name = null)
    {
        if (conductivity.Length != 9)
        {
            throw new FeaException(
                "An anisotropic conductivity tensor is 3x3 = 9 entries, row-major; "
                + $"{conductivity.Length} were supplied.");
        }
        var k = conductivity.ToArray();

        double scale = 0;
        for (int i = 0; i < 9; i++)
            scale = Math.Max(scale, Math.Abs(k[i]));
        // Relative, never absolute: a conductivity is in mW/(mm·K) and the same tensor in
        // another unit system would fail an absolute test (the epsilon ladder's scale-free tier).
        double tolerance = 1e-12 * Math.Max(scale, double.Epsilon);
        for (int i = 0; i < 3; i++)
        {
            for (int j = i + 1; j < 3; j++)
            {
                if (Math.Abs(k[i * 3 + j] - k[j * 3 + i]) > tolerance)
                {
                    throw new FeaException(
                        $"The conductivity tensor is not symmetric: K[{i},{j}] = "
                        + $"{k[i * 3 + j]:G6} against K[{j},{i}] = {k[j * 3 + i]:G6}. Onsager "
                        + "reciprocity makes a conductivity tensor symmetric, so an asymmetric "
                        + "one is a transcription error rather than an exotic material.");
                }
                // Symmetrise the last bits so the assembled conduction matrix is symmetric
                // exactly rather than nearly, which is what SparseCholesky's upper-triangular
                // storage assumes.
                double mean = 0.5 * (k[i * 3 + j] + k[j * 3 + i]);
                k[i * 3 + j] = mean;
                k[j * 3 + i] = mean;
            }
        }
        RequirePositiveDefinite(k, name ?? "anisotropic");
        RotateInPlace(k, frame);
        return new ConductivityLaw(k, isotropic: false, 0, name ?? "anisotropic");
    }

    /// <inheritdoc/>
    public override string ToString() => Description;

    // ---- linear algebra --------------------------------------------------------------

    private static void RequirePositive(double value, string name)
    {
        if (!(value > 0))
        {
            throw new FeaException(
                $"{name} must be positive; {value:G6} was supplied. A zero conductivity is not a "
                + "poor conductor — it makes that direction of the conduction matrix identically "
                + "zero, so the refusal is here rather than at the factorization.");
        }
    }

    /// <summary>
    /// Refuses a 3x3 matrix that is not positive definite, by attempting a Cholesky and naming
    /// the leading minor that failed.
    ///
    /// <para><b>The Cholesky IS the statement "positive definite"</b>, the same call
    /// <see cref="ElasticLaw"/> makes one dimension up: a per-entry bound would re-derive it in
    /// a form easy to get subtly wrong, where a failed factorization names exactly which minor
    /// went non-positive. Without it a plausible-looking transcription gives a tensor with a
    /// negative eigenvalue — heat flowing up its own gradient somewhere — and the symptom is a
    /// factorization failure deep in the solver rather than a message about the material.</para>
    /// </summary>
    private static void RequirePositiveDefinite(double[] m, string name)
    {
        // The LDL' reduction in a scratch copy, so the diagonal pivots are the reduced
        // (Schur-complement) ones — a leading minor is non-positive iff a reduced pivot is,
        // which is what "positive definite" means.
        Span<double> a = stackalloc double[9];
        for (int i = 0; i < 9; i++)
            a[i] = m[i];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < i; j++)
            {
                double f = a[i * 3 + j] / a[j * 3 + j];
                for (int c = j; c < 3; c++)
                    a[i * 3 + c] -= f * a[j * 3 + c];
            }
            if (!(a[i * 3 + i] > 0))
            {
                throw new FeaException(
                    $"The conductivity tensor of '{name}' is not positive definite (leading "
                    + $"minor {i + 1} of 3 failed). Such a material would conduct heat up its own "
                    + "temperature gradient somewhere, which is unphysical; a conductivity tensor "
                    + "must be symmetric positive definite.");
            }
        }
    }

    /// <summary>The inverse of a symmetric 3x3 matrix, row-major, via cofactors — symmetrised so
    /// the quadratic form built from it is symmetric exactly rather than nearly.</summary>
    private static double[] Invert(double[] k)
    {
        double a = k[0], b = k[1], c = k[2];
        double d = k[3], e = k[4], f = k[5];
        double g = k[6], h = k[7], i = k[8];
        double c00 = e * i - f * h;
        double c01 = c * h - b * i;
        double c02 = b * f - c * e;
        double det = a * c00 + b * (f * g - d * i) + c * (d * h - e * g);
        double inv = 1.0 / det;

        var r = new double[9];
        r[0] = c00 * inv;
        r[4] = (a * i - c * g) * inv;
        r[8] = (a * e - b * d) * inv;
        double r01 = c01 * inv;
        double r02 = c02 * inv;
        double r12 = (c * d - a * f) * inv;
        r[1] = r[3] = r01;
        r[2] = r[6] = r02;
        r[5] = r[7] = r12;
        return r;
    }

    /// <summary>
    /// Rotates a symmetric 3x3 conductivity tensor from the material frame into global
    /// coordinates: <c>K_global = R·K_material·Rᵀ</c>, with R the matrix whose COLUMNS are the
    /// material axes expressed in global coordinates.
    ///
    /// <para>This is an ordinary rank-2 tensor congruence — no Voigt vector, no engineering
    /// shear — which is what makes it simpler than the elastic 6x6 Bond transformation, and it
    /// is derived rather than transcribed: a conductivity relates two vectors (flux and
    /// gradient), each of which transforms by R, so the tensor between them transforms by
    /// R on the left and Rᵀ on the right.</para>
    /// </summary>
    private static void RotateInPlace(double[] k, Frame3d frame)
    {
        // The world frame is the overwhelmingly common case and is EXACTLY the identity here;
        // skipping it is a semantic exact-zero test, not an optimisation, because it keeps an
        // unrotated tensor bit-for-bit the one the caller stated.
        if (frame.X == Vector3d.UnitX && frame.Y == Vector3d.UnitY && frame.Z == Vector3d.UnitZ)
            return;

        var x = frame.X;
        var y = frame.Y;
        var z = frame.Z;
        // R columns are the material axes in global coordinates.
        Span<double> r =
        [
            x.X, y.X, z.X,
            x.Y, y.Y, z.Y,
            x.Z, y.Z, z.Z,
        ];

        // RK = R · K.
        Span<double> rk = stackalloc double[9];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                double sum = 0;
                for (int p = 0; p < 3; p++)
                    sum += r[i * 3 + p] * k[p * 3 + j];
                rk[i * 3 + j] = sum;
            }
        }
        // K_global = RK · Rᵀ.
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                double sum = 0;
                for (int p = 0; p < 3; p++)
                    sum += rk[i * 3 + p] * r[j * 3 + p];
                k[i * 3 + j] = sum;
            }
        }
        // Symmetrise the last bits: the congruence of a symmetric matrix is symmetric
        // mathematically, and the assembled global matrix is stored upper-triangular, so a
        // few-ulp asymmetry would be silently resolved in favour of whichever entry the packer
        // saw.
        for (int i = 0; i < 3; i++)
        {
            for (int j = i + 1; j < 3; j++)
            {
                double mean = 0.5 * (k[i * 3 + j] + k[j * 3 + i]);
                k[i * 3 + j] = mean;
                k[j * 3 + i] = mean;
            }
        }
    }
}
