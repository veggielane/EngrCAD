using System.Numerics;
using static EngrCAD.Core.ShewchukExpansions;

namespace EngrCAD.Core;

/// <summary>
/// Adaptive-exact 3D geometric predicates — the companion of <see cref="Predicates2d"/>,
/// following Jonathan Richard Shewchuk's public-domain <c>predicates.c</c> ("Adaptive
/// Precision Floating-Point Arithmetic and Fast Robust Geometric Predicates", 1997).
///
/// <para><see cref="Orient3d"/> and <see cref="InSphere"/> return a double whose SIGN is
/// exactly correct for every finite double input. Both evaluate a cheap floating-point
/// approximation guarded by Shewchuk's forward error bound, and escalate only when the
/// approximation's magnitude falls inside that bound. Exactly-degenerate inputs (coplanar
/// quadruples, cospherical quintuples) therefore return exactly 0.0 — which is what lets a
/// Delaunay tetrahedralization treat cospherical point sets as a *tie* rather than as noise.</para>
///
/// <para><b>The two exact stages are built differently, deliberately.</b> <see cref="Orient3d"/>
/// escalates to Shewchuk's <c>orient3dexact</c>: expansion arithmetic in <c>stackalloc</c>
/// spans whose longest intermediate is 96 doubles, so the whole predicate never allocates.
/// <see cref="InSphere"/> escalates to a <see cref="BigInteger"/> evaluation of the same
/// determinant over exactly-decomposed doubles. The reason is size: the expansion form of
/// <c>insphereexact</c> needs ~6000-component intermediates and several hundred lines of
/// hand-unrolled sign bookkeeping, and a transcription of that is a liability rather than
/// an asset — whereas the BigInteger form is *visibly* the determinant and is the same
/// ground truth the tests would check it against anyway. The cost is confined to the
/// escalation path: the filter carries every configuration that is not within its own error
/// bound of degenerate, and a degenerate one is exactly where a wrong answer is fatal.
/// (Filed in todo.md: an <c>ArrayPool</c>-backed expansion form if profiling ever shows the
/// escalation rate matters.)</para>
///
/// <para>Correctness of the expansion arithmetic requires IEEE-754 double semantics with
/// round-to-nearest and NO fused multiply-add contraction — see
/// <see cref="ShewchukExpansions"/>. Inputs must be finite; magnitudes near the
/// overflow/underflow limits fall outside Shewchuk's analysis, as in the original.</para>
/// </summary>
public static class Predicates3d
{
    // Error-bound coefficients, exactly as computed by exactinit() in predicates.c.
    private const double O3dErrBoundA = (7.0 + 56.0 * Epsilon) * Epsilon;
    private const double IspErrBoundA = (16.0 + 224.0 * Epsilon) * Epsilon;

    // ---- public API ----

    /// <summary>
    /// Sign-exact orientation test. Returns a positive value when <paramref name="d"/> lies
    /// BELOW the plane through <paramref name="a"/>, <paramref name="b"/>,
    /// <paramref name="c"/> — "below" meaning a, b, c appear counter-clockwise when viewed
    /// from above — negative when above, and exactly 0.0 when the four points are exactly
    /// coplanar. This is Shewchuk's convention: the value is the determinant
    /// <c>det[a-d; b-d; c-d]</c>, which is <b>minus</b> six times the signed volume of the
    /// tetrahedron (a, b, c, d). Use <see cref="SignedVolume6"/> when the volume's own sign
    /// is what you want.
    /// </summary>
    public static double Orient3d(in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d d)
    {
        double adx = a.X - d.X;
        double bdx = b.X - d.X;
        double cdx = c.X - d.X;
        double ady = a.Y - d.Y;
        double bdy = b.Y - d.Y;
        double cdy = c.Y - d.Y;
        double adz = a.Z - d.Z;
        double bdz = b.Z - d.Z;
        double cdz = c.Z - d.Z;

        double bdxcdy = bdx * cdy;
        double cdxbdy = cdx * bdy;
        double cdxady = cdx * ady;
        double adxcdy = adx * cdy;
        double adxbdy = adx * bdy;
        double bdxady = bdx * ady;

        double det = adz * (bdxcdy - cdxbdy)
                   + bdz * (cdxady - adxcdy)
                   + cdz * (adxbdy - bdxady);

        double permanent = (Math.Abs(bdxcdy) + Math.Abs(cdxbdy)) * Math.Abs(adz)
                         + (Math.Abs(cdxady) + Math.Abs(adxcdy)) * Math.Abs(bdz)
                         + (Math.Abs(adxbdy) + Math.Abs(bdxady)) * Math.Abs(cdz);
        double errbound = O3dErrBoundA * permanent;
        if (det > errbound || -det > errbound)
            return det;

        return Orient3dExact(a, b, c, d);
    }

    /// <summary>-1, 0, or +1 — the exact sign of <see cref="Orient3d"/>.</summary>
    public static int Orient3dSign(in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d d) =>
        Math.Sign(Orient3d(a, b, c, d));

    /// <summary>
    /// Sign-exact signed volume of the tetrahedron (a, b, c, d), scaled by 6: positive when
    /// (a, b, c, d) is positively oriented (d on the side the triangle a→b→c's right-hand
    /// normal points to). This is simply <c>-Orient3d(a, b, c, d)</c>, named because the
    /// sign flip between the two conventions is a standing trap. Only the sign is exact;
    /// the magnitude is the ordinary floating-point estimate unless the exact stage ran.
    /// </summary>
    public static double SignedVolume6(in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d d) =>
        -Orient3d(a, b, c, d);

    /// <summary>-1, 0, or +1 — the exact sign of <see cref="SignedVolume6"/>.</summary>
    public static int SignedVolume6Sign(in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d d) =>
        -Math.Sign(Orient3d(a, b, c, d));

    /// <summary>
    /// Sign-exact in-sphere test: positive when <paramref name="e"/> lies strictly inside
    /// the sphere through <paramref name="a"/>, <paramref name="b"/>, <paramref name="c"/>,
    /// <paramref name="d"/>, negative when strictly outside, and exactly 0.0 when the five
    /// points are exactly cospherical — PROVIDED <c>Orient3d(a, b, c, d) &gt; 0</c>. A
    /// negatively-oriented base tetrahedron flips the sign, as in Shewchuk's original;
    /// <see cref="InSphereOriented"/> removes that precondition.
    /// </summary>
    public static double InSphere(in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d d, in Vector3d e)
    {
        double aex = a.X - e.X, aey = a.Y - e.Y, aez = a.Z - e.Z;
        double bex = b.X - e.X, bey = b.Y - e.Y, bez = b.Z - e.Z;
        double cex = c.X - e.X, cey = c.Y - e.Y, cez = c.Z - e.Z;
        double dex = d.X - e.X, dey = d.Y - e.Y, dez = d.Z - e.Z;

        double aexbey = aex * bey, bexaey = bex * aey, ab = aexbey - bexaey;
        double bexcey = bex * cey, cexbey = cex * bey, bc = bexcey - cexbey;
        double cexdey = cex * dey, dexcey = dex * cey, cd = cexdey - dexcey;
        double dexaey = dex * aey, aexdey = aex * dey, da = dexaey - aexdey;
        double aexcey = aex * cey, cexaey = cex * aey, ac = aexcey - cexaey;
        double bexdey = bex * dey, dexbey = dex * bey, bd = bexdey - dexbey;

        // det3 of the (x, y, z) rows of each face, expanded along the z column.
        double abc = aez * bc - bez * ac + cez * ab;
        double bcd = bez * cd - cez * bd + dez * bc;
        double cda = cez * da + dez * ac + aez * cd;
        double dab = dez * ab + aez * bd + bez * da;

        double alift = aex * aex + aey * aey + aez * aez;
        double blift = bex * bex + bey * bey + bez * bez;
        double clift = cex * cex + cey * cey + cez * cez;
        double dlift = dex * dex + dey * dey + dez * dez;

        double det = (dlift * abc - clift * dab) + (blift * cda - alift * bcd);

        double aezplus = Math.Abs(aez), bezplus = Math.Abs(bez);
        double cezplus = Math.Abs(cez), dezplus = Math.Abs(dez);
        double aexbeyplus = Math.Abs(aexbey), bexaeyplus = Math.Abs(bexaey);
        double bexceyplus = Math.Abs(bexcey), cexbeyplus = Math.Abs(cexbey);
        double cexdeyplus = Math.Abs(cexdey), dexceyplus = Math.Abs(dexcey);
        double dexaeyplus = Math.Abs(dexaey), aexdeyplus = Math.Abs(aexdey);
        double aexceyplus = Math.Abs(aexcey), cexaeyplus = Math.Abs(cexaey);
        double bexdeyplus = Math.Abs(bexdey), dexbeyplus = Math.Abs(dexbey);

        double permanent =
            ((cexdeyplus + dexceyplus) * bezplus
             + (dexbeyplus + bexdeyplus) * cezplus
             + (bexceyplus + cexbeyplus) * dezplus) * alift
          + ((dexaeyplus + aexdeyplus) * cezplus
             + (aexceyplus + cexaeyplus) * dezplus
             + (cexdeyplus + dexceyplus) * aezplus) * blift
          + ((aexbeyplus + bexaeyplus) * dezplus
             + (bexdeyplus + dexbeyplus) * aezplus
             + (dexaeyplus + aexdeyplus) * bezplus) * clift
          + ((bexceyplus + cexbeyplus) * aezplus
             + (cexaeyplus + aexceyplus) * bezplus
             + (aexbeyplus + bexaeyplus) * cezplus) * dlift;

        double errbound = IspErrBoundA * permanent;
        if (det > errbound || -det > errbound)
            return det;

        Interlocked.Increment(ref _inSphereEscalations);
        return InSphereExactSign(a, b, c, d, e);
    }

    /// <summary>-1, 0, or +1 — the exact sign of <see cref="InSphere"/>.</summary>
    public static int InSphereSign(in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d d, in Vector3d e) =>
        Math.Sign(InSphere(a, b, c, d, e));

    /// <summary>
    /// Orientation-independent in-sphere test: +1 when <paramref name="e"/> is strictly
    /// inside the sphere through the (possibly negatively oriented) tetrahedron
    /// a, b, c, d, -1 when strictly outside, 0 when exactly cospherical. Throws when
    /// a, b, c, d are exactly coplanar, since they then define no sphere.
    /// </summary>
    public static int InSphereOriented(in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d d, in Vector3d e)
    {
        int orientation = Orient3dSign(a, b, c, d);
        if (orientation == 0)
            throw new ArgumentException(
                "InSphereOriented: the four base points are exactly coplanar and define no sphere.");
        return orientation * InSphereSign(a, b, c, d, e);
    }

    // ---- escalation diagnostics ----

    private static long _inSphereEscalations;

    /// <summary>
    /// How many <see cref="InSphere"/> calls have escalated to the exact (allocating)
    /// stage in this process. Diagnostic only — it lets a mesher report the escalation
    /// rate honestly instead of guessing at it.
    /// </summary>
    public static long InSphereEscalations => Interlocked.Read(ref _inSphereEscalations);

    /// <summary>Resets <see cref="InSphereEscalations"/> (for benchmarks and tests).</summary>
    public static void ResetEscalationCounters() => Interlocked.Exchange(ref _inSphereEscalations, 0);

    // ---- orient3d exact stage (orient3dexact) ----

    /// <summary>
    /// The 4x4 determinant |ax ay az 1; bx by bz 1; cx cy cz 1; dx dy dz 1| evaluated as an
    /// exact expansion. Laplace-expanded by complementary 2x2 minors of the (x, y) columns:
    /// the (z, 1) minors collapse to plain coordinate differences, which is what keeps the
    /// intermediates down to 96 components.
    /// </summary>
    private static double Orient3dExact(in Vector3d pa, in Vector3d pb, in Vector3d pc, in Vector3d pd)
    {
        Span<double> ab = stackalloc double[4];
        Span<double> bc = stackalloc double[4];
        Span<double> cd = stackalloc double[4];
        Span<double> da = stackalloc double[4];
        Span<double> ac = stackalloc double[4];
        Span<double> bd = stackalloc double[4];

        TwoProduct(pa.X, pb.Y, out double axby1, out double axby0);
        TwoProduct(pb.X, pa.Y, out double bxay1, out double bxay0);
        TwoTwoDiff(axby1, axby0, bxay1, bxay0, out double ab3, out ab[2], out ab[1], out ab[0]);
        ab[3] = ab3;

        TwoProduct(pb.X, pc.Y, out double bxcy1, out double bxcy0);
        TwoProduct(pc.X, pb.Y, out double cxby1, out double cxby0);
        TwoTwoDiff(bxcy1, bxcy0, cxby1, cxby0, out double bc3, out bc[2], out bc[1], out bc[0]);
        bc[3] = bc3;

        TwoProduct(pc.X, pd.Y, out double cxdy1, out double cxdy0);
        TwoProduct(pd.X, pc.Y, out double dxcy1, out double dxcy0);
        TwoTwoDiff(cxdy1, cxdy0, dxcy1, dxcy0, out double cd3, out cd[2], out cd[1], out cd[0]);
        cd[3] = cd3;

        TwoProduct(pd.X, pa.Y, out double dxay1, out double dxay0);
        TwoProduct(pa.X, pd.Y, out double axdy1, out double axdy0);
        TwoTwoDiff(dxay1, dxay0, axdy1, axdy0, out double da3, out da[2], out da[1], out da[0]);
        da[3] = da3;

        TwoProduct(pa.X, pc.Y, out double axcy1, out double axcy0);
        TwoProduct(pc.X, pa.Y, out double cxay1, out double cxay0);
        TwoTwoDiff(axcy1, axcy0, cxay1, cxay0, out double ac3, out ac[2], out ac[1], out ac[0]);
        ac[3] = ac3;

        TwoProduct(pb.X, pd.Y, out double bxdy1, out double bxdy0);
        TwoProduct(pd.X, pb.Y, out double dxby1, out double dxby0);
        TwoTwoDiff(bxdy1, bxdy0, dxby1, dxby0, out double bd3, out bd[2], out bd[1], out bd[0]);
        bd[3] = bd3;

        Span<double> temp8 = stackalloc double[8];
        Span<double> abc = stackalloc double[12];
        Span<double> bcd = stackalloc double[12];
        Span<double> cda = stackalloc double[12];
        Span<double> dab = stackalloc double[12];

        int templen = FastExpansionSumZeroElim(cd, 4, da, 4, temp8);
        int cdalen = FastExpansionSumZeroElim(temp8, templen, ac, 4, cda);
        templen = FastExpansionSumZeroElim(da, 4, ab, 4, temp8);
        int dablen = FastExpansionSumZeroElim(temp8, templen, bd, 4, dab);
        for (int i = 0; i < 4; i++)
        {
            bd[i] = -bd[i];
            ac[i] = -ac[i];
        }
        templen = FastExpansionSumZeroElim(ab, 4, bc, 4, temp8);
        int abclen = FastExpansionSumZeroElim(temp8, templen, ac, 4, abc);
        templen = FastExpansionSumZeroElim(bc, 4, cd, 4, temp8);
        int bcdlen = FastExpansionSumZeroElim(temp8, templen, bd, 4, bcd);

        Span<double> adet = stackalloc double[24];
        Span<double> bdet = stackalloc double[24];
        Span<double> cdet = stackalloc double[24];
        Span<double> ddet = stackalloc double[24];

        int alen = ScaleExpansionZeroElim(bcd, bcdlen, pa.Z, adet);
        int blen = ScaleExpansionZeroElim(cda, cdalen, -pb.Z, bdet);
        int clen = ScaleExpansionZeroElim(dab, dablen, pc.Z, cdet);
        int dlen = ScaleExpansionZeroElim(abc, abclen, -pd.Z, ddet);

        Span<double> abdet = stackalloc double[48];
        Span<double> cddet = stackalloc double[48];
        Span<double> deter = stackalloc double[96];

        int ablen = FastExpansionSumZeroElim(adet, alen, bdet, blen, abdet);
        int cdlen = FastExpansionSumZeroElim(cdet, clen, ddet, dlen, cddet);
        int deterlen = FastExpansionSumZeroElim(abdet, ablen, cddet, cdlen, deter);

        return deter[deterlen - 1];
    }

    // ---- insphere exact stage ----

    /// <summary>
    /// The exact sign of the in-sphere determinant, computed in <see cref="BigInteger"/>.
    ///
    /// <para>Every finite double is exactly M·2^E with a 53-bit integer M. Rewriting all
    /// fifteen input coordinates over one common exponent 2^Emin turns them into exact
    /// integers scaled by a single POSITIVE factor s = 2^Emin. Each term of the determinant
    /// has total degree 5 in the coordinates (three degree-1 entries from the difference
    /// columns and one degree-2 entry from the lift column), so the determinant scales as
    /// s^5 &gt; 0 and the sign is unaffected — which is why the scale never has to be
    /// tracked. The BigInteger arithmetic that follows is then exact by construction.</para>
    ///
    /// <para>Returned as a double (-1, 0 or +1) so the caller's <c>Math.Sign</c> is
    /// unchanged; the magnitude carries no information on this path, which is true of the
    /// last stage of every adaptive predicate.</para>
    /// </summary>
    private static double InSphereExactSign(
        in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d d, in Vector3d e)
    {
        Span<double> raw = stackalloc double[15];
        raw[0] = a.X; raw[1] = a.Y; raw[2] = a.Z;
        raw[3] = b.X; raw[4] = b.Y; raw[5] = b.Z;
        raw[6] = c.X; raw[7] = c.Y; raw[8] = c.Z;
        raw[9] = d.X; raw[10] = d.Y; raw[11] = d.Z;
        raw[12] = e.X; raw[13] = e.Y; raw[14] = e.Z;

        Span<int> exponents = stackalloc int[15];
        int minExponent = int.MaxValue;
        for (int i = 0; i < 15; i++)
        {
            Decompose(raw[i], out _, out int exponent);
            exponents[i] = exponent;
            if (exponent < minExponent)
                minExponent = exponent;
        }

        var scaled = new BigInteger[15];
        for (int i = 0; i < 15; i++)
        {
            Decompose(raw[i], out BigInteger mantissa, out _);
            scaled[i] = mantissa << (exponents[i] - minExponent);
        }

        BigInteger aex = scaled[0] - scaled[12], aey = scaled[1] - scaled[13], aez = scaled[2] - scaled[14];
        BigInteger bex = scaled[3] - scaled[12], bey = scaled[4] - scaled[13], bez = scaled[5] - scaled[14];
        BigInteger cex = scaled[6] - scaled[12], cey = scaled[7] - scaled[13], cez = scaled[8] - scaled[14];
        BigInteger dex = scaled[9] - scaled[12], dey = scaled[10] - scaled[13], dez = scaled[11] - scaled[14];

        BigInteger ab = aex * bey - bex * aey;
        BigInteger bc = bex * cey - cex * bey;
        BigInteger cd = cex * dey - dex * cey;
        BigInteger da = dex * aey - aex * dey;
        BigInteger ac = aex * cey - cex * aey;
        BigInteger bd = bex * dey - dex * bey;

        BigInteger abc = aez * bc - bez * ac + cez * ab;
        BigInteger bcd = bez * cd - cez * bd + dez * bc;
        BigInteger cda = cez * da + dez * ac + aez * cd;
        BigInteger dab = dez * ab + aez * bd + bez * da;

        BigInteger alift = aex * aex + aey * aey + aez * aez;
        BigInteger blift = bex * bex + bey * bey + bez * bez;
        BigInteger clift = cex * cex + cey * cey + cez * cez;
        BigInteger dlift = dex * dex + dey * dey + dez * dez;

        BigInteger det = (dlift * abc - clift * dab) + (blift * cda - alift * bcd);
        return det.Sign;
    }

    /// <summary>Exact decomposition of a finite double into mantissa * 2^exponent.</summary>
    private static void Decompose(double value, out BigInteger mantissa, out int exponent)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        int biased = (int)((bits >> 52) & 0x7FF);
        if (biased == 0x7FF)
            throw new ArgumentException("Predicates3d requires finite coordinates.");
        long fraction = bits & 0xF_FFFF_FFFF_FFFF;
        if (biased == 0)
        {
            mantissa = fraction;
            exponent = -1074;
        }
        else
        {
            mantissa = fraction | (1L << 52);
            exponent = biased - 1075;
        }
        if (bits < 0)
            mantissa = -mantissa;
    }
}
