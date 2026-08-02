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
/// spans whose longest intermediate is 96 doubles. <see cref="InSphere"/> escalates to an
/// exact INTEGER evaluation of the same determinant over exactly-decomposed doubles —
/// sign-magnitude big integers in <c>stackalloc</c> (or, for coordinates spread over
/// hundreds of orders of magnitude, pooled) <c>ulong</c> buffers, so neither predicate
/// allocates. The integer form is kept over a transcription of Shewchuk's
/// <c>insphereexact</c> because that expansion form needs ~6000-component intermediates and
/// several hundred lines of hand-unrolled sign bookkeeping — a liability rather than an
/// asset — whereas the integer evaluation is *visibly* the determinant. It used to run on
/// <c>System.Numerics.BigInteger</c>, which was measured at 5 698 bytes and 9 229 ns per
/// escalated call — and cospherical input is the NORMAL case for CAD tessellations (58% of
/// a sphere mesh's total allocation), which is what paid for the span form. The
/// independent BigInteger evaluation lives on as the test suite's ground truth
/// (<c>ExactReference</c>), written as a different cofactor expansion so agreement is
/// evidence rather than tautology.</para>
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
    private static long _inSpherePooledEscalations;

    /// <summary>
    /// How many <see cref="InSphere"/> calls have escalated to the exact stage in this
    /// process. Diagnostic only — it lets a mesher report the escalation rate honestly
    /// instead of guessing at it.
    /// </summary>
    public static long InSphereEscalations => Interlocked.Read(ref _inSphereEscalations);

    /// <summary>
    /// How many of those escalations had coordinates spread so widely in exponent that the
    /// exact stage's arena outgrew its stackalloc and rented from the pool instead —
    /// zero for ordinary CAD input. Diagnostic, and what lets the pooled path's regression
    /// fixture assert it still CARRIES the configuration it exists to test.
    /// </summary>
    public static long InSpherePooledEscalations => Interlocked.Read(ref _inSpherePooledEscalations);

    /// <summary>Resets the escalation counters (for benchmarks and tests).</summary>
    public static void ResetEscalationCounters()
    {
        Interlocked.Exchange(ref _inSphereEscalations, 0);
        Interlocked.Exchange(ref _inSpherePooledEscalations, 0);
    }

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
    /// The exact sign of the in-sphere determinant, computed in fixed-width integer
    /// arithmetic over <c>stackalloc</c> (or pooled) buffers — no heap allocation.
    ///
    /// <para>Every finite double is exactly M·2^E with a 53-bit integer M. Rewriting all
    /// fifteen input coordinates over one common exponent 2^Emin turns them into exact
    /// integers scaled by a single POSITIVE factor s = 2^Emin. Each term of the determinant
    /// has total degree 5 in the coordinates (three degree-1 entries from the difference
    /// columns and one degree-2 entry from the lift column), so the determinant scales as
    /// s^5 &gt; 0 and the sign is unaffected — which is why the scale never has to be
    /// tracked. The integer arithmetic that follows is then exact by construction, and the
    /// computation below reads exactly like the determinant it is (the same grouping the
    /// filter stage evaluates in doubles).</para>
    ///
    /// <para>Emin is taken over the NONZERO coordinates only — a zero scales to zero
    /// whatever the shift, while its stored exponent is −1074 and would otherwise widen
    /// every operand by ~1000 bits whenever any coordinate is exactly zero (a constant
    /// occurrence in CAD input). Skipping zeros changes only the positive scale factor, so
    /// the sign is untouched.</para>
    ///
    /// <para><b>Buffer sizing is a proof, not a guess.</b> With S the largest exponent
    /// spread among nonzero coordinates, every scaled coordinate is below 2^B for
    /// B = 53 + S, so differences are below 2^(B+1), the 2×2 minors below 2^(2B+3), the
    /// 3×3 face minors below 2^(3B+6), the lifts below 2^(2B+4) and the determinant below
    /// 2^(5B+12) — each tier's word count is that bound plus a margin word. For ordinary
    /// CAD coordinates (S a few dozen bits) the whole arena is a few hundred words and
    /// lives on the stack; coordinates spread over hundreds of orders of magnitude push it
    /// to an <see cref="System.Buffers.ArrayPool{T}"/> rental, which allocates nothing
    /// once the pool is warm.</para>
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

        Span<long> mantissa = stackalloc long[15];
        Span<int> exponent = stackalloc int[15];
        int minExponent = int.MaxValue;
        for (int i = 0; i < 15; i++)
        {
            Decompose(raw[i], out mantissa[i], out exponent[i]);
            if (mantissa[i] != 0 && exponent[i] < minExponent)
                minExponent = exponent[i];
        }
        if (minExponent == int.MaxValue)
            return 0.0; // all fifteen coordinates are exactly zero — every minor is zero

        Span<int> shift = stackalloc int[15];
        int maxShift = 0;
        for (int i = 0; i < 15; i++)
        {
            shift[i] = mantissa[i] == 0 ? 0 : exponent[i] - minExponent;
            if (shift[i] > maxShift)
                maxShift = shift[i];
        }

        // Word budget per tier, from the bit bounds in the summary (Words(bits) + 1 margin,
        // with the composite tiers stated directly in words so a product provably fits its
        // destination: a k-word by m-word product writes at most k + m words).
        int bitsB = 53 + maxShift;
        int wordsScaled = ((bitsB + 63) >> 6) + 1;
        int wordsDiff = ((bitsB + 1 + 63) >> 6) + 1;
        int wordsPair = 2 * wordsDiff + 1;
        int wordsFace = wordsDiff + wordsPair + 2;
        int wordsLift = 2 * wordsDiff + 2;
        int wordsDet = wordsLift + wordsFace + 2;
        int wordsTemp = wordsLift + wordsFace + 1;
        int total = 2 * wordsScaled + 12 * wordsDiff + 6 * wordsPair + 4 * wordsFace
                  + 4 * wordsLift + wordsDet + wordsTemp;

        ulong[]? rented = null;
        if (total > StackArenaWords)
        {
            rented = System.Buffers.ArrayPool<ulong>.Shared.Rent(total);
            Interlocked.Increment(ref _inSpherePooledEscalations);
        }
        try
        {
            Span<ulong> buffer = rented is null ? stackalloc ulong[StackArenaWords] : rented;
            var arena = new ExactArena(buffer);

            var t1 = arena.Take(wordsScaled);
            var t2 = arena.Take(wordsScaled);
            var tmp = arena.Take(wordsTemp);

            // Differences against e — the translated coordinates the determinant is in.
            var aex = arena.Take(wordsDiff);
            var aey = arena.Take(wordsDiff);
            var aez = arena.Take(wordsDiff);
            var bex = arena.Take(wordsDiff);
            var bey = arena.Take(wordsDiff);
            var bez = arena.Take(wordsDiff);
            var cex = arena.Take(wordsDiff);
            var cey = arena.Take(wordsDiff);
            var cez = arena.Take(wordsDiff);
            var dex = arena.Take(wordsDiff);
            var dey = arena.Take(wordsDiff);
            var dez = arena.Take(wordsDiff);
            ScaledDifference(ref aex, ref t1, ref t2, mantissa[0], shift[0], mantissa[12], shift[12]);
            ScaledDifference(ref aey, ref t1, ref t2, mantissa[1], shift[1], mantissa[13], shift[13]);
            ScaledDifference(ref aez, ref t1, ref t2, mantissa[2], shift[2], mantissa[14], shift[14]);
            ScaledDifference(ref bex, ref t1, ref t2, mantissa[3], shift[3], mantissa[12], shift[12]);
            ScaledDifference(ref bey, ref t1, ref t2, mantissa[4], shift[4], mantissa[13], shift[13]);
            ScaledDifference(ref bez, ref t1, ref t2, mantissa[5], shift[5], mantissa[14], shift[14]);
            ScaledDifference(ref cex, ref t1, ref t2, mantissa[6], shift[6], mantissa[12], shift[12]);
            ScaledDifference(ref cey, ref t1, ref t2, mantissa[7], shift[7], mantissa[13], shift[13]);
            ScaledDifference(ref cez, ref t1, ref t2, mantissa[8], shift[8], mantissa[14], shift[14]);
            ScaledDifference(ref dex, ref t1, ref t2, mantissa[9], shift[9], mantissa[12], shift[12]);
            ScaledDifference(ref dey, ref t1, ref t2, mantissa[10], shift[10], mantissa[13], shift[13]);
            ScaledDifference(ref dez, ref t1, ref t2, mantissa[11], shift[11], mantissa[14], shift[14]);

            // 2×2 minors of the (x, y) columns.
            var ab = arena.Take(wordsPair);
            var bc = arena.Take(wordsPair);
            var cd = arena.Take(wordsPair);
            var da = arena.Take(wordsPair);
            var ac = arena.Take(wordsPair);
            var bd = arena.Take(wordsPair);
            ProductDifference(ref ab, ref tmp, aex, bey, bex, aey);
            ProductDifference(ref bc, ref tmp, bex, cey, cex, bey);
            ProductDifference(ref cd, ref tmp, cex, dey, dex, cey);
            ProductDifference(ref da, ref tmp, dex, aey, aex, dey);
            ProductDifference(ref ac, ref tmp, aex, cey, cex, aey);
            ProductDifference(ref bd, ref tmp, bex, dey, dex, bey);

            // det3 of the (x, y, z) rows of each face, expanded along the z column —
            // the filter stage's grouping, verbatim.
            var abc = arena.Take(wordsFace);
            var bcd = arena.Take(wordsFace);
            var cda = arena.Take(wordsFace);
            var dab = arena.Take(wordsFace);
            SignedTriple(ref abc, ref tmp, aez, bc, 1, bez, ac, -1, cez, ab, 1);
            SignedTriple(ref bcd, ref tmp, bez, cd, 1, cez, bd, -1, dez, bc, 1);
            SignedTriple(ref cda, ref tmp, cez, da, 1, dez, ac, 1, aez, cd, 1);
            SignedTriple(ref dab, ref tmp, dez, ab, 1, aez, bd, 1, bez, da, 1);

            var alift = arena.Take(wordsLift);
            var blift = arena.Take(wordsLift);
            var clift = arena.Take(wordsLift);
            var dlift = arena.Take(wordsLift);
            SumOfSquares(ref alift, ref tmp, aex, aey, aez);
            SumOfSquares(ref blift, ref tmp, bex, bey, bez);
            SumOfSquares(ref clift, ref tmp, cex, cey, cez);
            SumOfSquares(ref dlift, ref tmp, dex, dey, dez);

            // det = (dlift·abc − clift·dab) + (blift·cda − alift·bcd) — exact, so the
            // association is free and the sequential accumulation below is the same value.
            var det = arena.Take(wordsDet);
            Multiply(dlift, abc, ref tmp);
            Accumulate(ref det, tmp, 1);
            Multiply(clift, dab, ref tmp);
            Accumulate(ref det, tmp, -1);
            Multiply(blift, cda, ref tmp);
            Accumulate(ref det, tmp, 1);
            Multiply(alift, bcd, ref tmp);
            Accumulate(ref det, tmp, -1);
            return det.Sign;
        }
        finally
        {
            if (rented is not null)
                System.Buffers.ArrayPool<ulong>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// The exponent spread (in 64-bit words of arena) below which the whole exact stage
    /// fits in one 8 KB stackalloc — ordinary CAD coordinates use a few hundred words of
    /// it; anything wider (coordinates hundreds of orders of magnitude apart) rents from
    /// the shared pool instead, which allocates nothing once the pool is warm.
    /// </summary>
    private const int StackArenaWords = 1024;

    /// <summary>Exact decomposition of a finite double into mantissa · 2^exponent
    /// (|mantissa| &lt; 2^53, so it is exactly a long).</summary>
    private static void Decompose(double value, out long mantissa, out int exponent)
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

    // ---- exact integer arithmetic on spans (sign-magnitude, little-endian ulong words) ----
    //
    // The invariant throughout: Len counts significant words (no leading zero word), and
    // Sign == 0 exactly when Len == 0. Buffers are NOT zero-initialized beyond what an
    // operation writes — every routine defines its output words explicitly and reads only
    // up to Len, so the arithmetic is independent of stackalloc zeroing.

    /// <summary>A bump allocator over one contiguous buffer: each intermediate takes its
    /// slice once, sized by the proven tier bound.</summary>
    private ref struct ExactArena(Span<ulong> buffer)
    {
        private readonly Span<ulong> _buffer = buffer;
        private int _used;

        public Big Take(int words)
        {
            var slice = _buffer.Slice(_used, words);
            _used += words;
            return new Big(slice);
        }
    }

    /// <summary>A signed arbitrary-precision integer over a caller-owned span.</summary>
    private ref struct Big(Span<ulong> mag)
    {
        public readonly Span<ulong> Mag = mag;
        public int Len;
        public int Sign;
    }

    /// <summary>dst = mantissaA·2^shiftA − mantissaB·2^shiftB, via two scratch loads.</summary>
    private static void ScaledDifference(
        ref Big dst, ref Big t1, ref Big t2, long mantissaA, int shiftA, long mantissaB, int shiftB)
    {
        SetShiftedMantissa(ref t1, mantissaA, shiftA);
        SetShiftedMantissa(ref t2, mantissaB, shiftB);
        dst.Len = 0;
        dst.Sign = 0;
        Accumulate(ref dst, t1, 1);
        Accumulate(ref dst, t2, -1);
    }

    /// <summary>dst = x1·y1 − x2·y2 (a 2×2 minor), products staged through tmp.</summary>
    private static void ProductDifference(
        ref Big dst, ref Big tmp, in Big x1, in Big y1, in Big x2, in Big y2)
    {
        dst.Len = 0;
        dst.Sign = 0;
        Multiply(x1, y1, ref tmp);
        Accumulate(ref dst, tmp, 1);
        Multiply(x2, y2, ref tmp);
        Accumulate(ref dst, tmp, -1);
    }

    /// <summary>dst = s1·(z1·m1) + s2·(z2·m2) + s3·(z3·m3) (a face's 3×3 minor).</summary>
    private static void SignedTriple(
        ref Big dst, ref Big tmp,
        in Big z1, in Big m1, int s1, in Big z2, in Big m2, int s2, in Big z3, in Big m3, int s3)
    {
        dst.Len = 0;
        dst.Sign = 0;
        Multiply(z1, m1, ref tmp);
        Accumulate(ref dst, tmp, s1);
        Multiply(z2, m2, ref tmp);
        Accumulate(ref dst, tmp, s2);
        Multiply(z3, m3, ref tmp);
        Accumulate(ref dst, tmp, s3);
    }

    /// <summary>dst = x² + y² + z² (a lift).</summary>
    private static void SumOfSquares(ref Big dst, ref Big tmp, in Big x, in Big y, in Big z)
    {
        dst.Len = 0;
        dst.Sign = 0;
        Multiply(x, x, ref tmp);
        Accumulate(ref dst, tmp, 1);
        Multiply(y, y, ref tmp);
        Accumulate(ref dst, tmp, 1);
        Multiply(z, z, ref tmp);
        Accumulate(ref dst, tmp, 1);
    }

    /// <summary>v = mantissa · 2^shift, exactly (|mantissa| &lt; 2^53, shift ≥ 0).</summary>
    private static void SetShiftedMantissa(ref Big v, long mantissa, int shift)
    {
        if (mantissa == 0)
        {
            v.Len = 0;
            v.Sign = 0;
            return;
        }
        v.Sign = mantissa > 0 ? 1 : -1;
        ulong m = mantissa > 0 ? (ulong)mantissa : (ulong)(-mantissa);
        int wordShift = shift >> 6;
        int bitShift = shift & 63;
        var mag = v.Mag;
        for (int i = 0; i < wordShift; i++)
            mag[i] = 0;
        if (bitShift == 0)
        {
            mag[wordShift] = m;
            v.Len = wordShift + 1;
        }
        else
        {
            mag[wordShift] = m << bitShift;
            ulong hi = m >> (64 - bitShift);
            if (hi != 0)
            {
                mag[wordShift + 1] = hi;
                v.Len = wordShift + 2;
            }
            else
            {
                v.Len = wordShift + 1;
            }
        }
    }

    /// <summary>acc += sign · term, in place. acc's capacity covers the result by the tier
    /// bounds (an in-place add only extends when the value genuinely grows).</summary>
    private static void Accumulate(ref Big acc, in Big term, int sign)
    {
        int termSign = term.Sign * sign;
        if (termSign == 0)
            return;
        if (acc.Sign == 0)
        {
            term.Mag[..term.Len].CopyTo(acc.Mag);
            acc.Len = term.Len;
            acc.Sign = termSign;
            return;
        }
        if (acc.Sign == termSign)
        {
            AddMagnitudeInPlace(ref acc, term);
            return;
        }
        int compare = CompareMagnitude(acc, term);
        if (compare == 0)
        {
            acc.Len = 0;
            acc.Sign = 0;
            return;
        }
        if (compare > 0)
        {
            SubtractMagnitudeInPlace(ref acc, term);
        }
        else
        {
            SubtractMagnitudeReversedInPlace(ref acc, term);
            acc.Sign = termSign;
        }
    }

    private static int CompareMagnitude(in Big a, in Big b)
    {
        if (a.Len != b.Len)
            return a.Len > b.Len ? 1 : -1;
        for (int i = a.Len - 1; i >= 0; i--)
        {
            if (a.Mag[i] != b.Mag[i])
                return a.Mag[i] > b.Mag[i] ? 1 : -1;
        }
        return 0;
    }

    private static void AddMagnitudeInPlace(ref Big acc, in Big term)
    {
        var accMag = acc.Mag;
        var termMag = term.Mag;
        int n = Math.Max(acc.Len, term.Len);
        ulong carry = 0;
        for (int i = 0; i < n; i++)
        {
            ulong x = i < acc.Len ? accMag[i] : 0;
            ulong y = i < term.Len ? termMag[i] : 0;
            ulong partial = x + y;
            ulong carry1 = partial < x ? 1UL : 0UL;
            ulong sum = partial + carry;
            ulong carry2 = sum < partial ? 1UL : 0UL;
            accMag[i] = sum;
            carry = carry1 + carry2;
        }
        if (carry != 0)
        {
            accMag[n] = carry;
            acc.Len = n + 1;
        }
        else
        {
            acc.Len = n;
        }
    }

    /// <summary>acc = |acc| − |term|, requiring |acc| &gt; |term|; sign unchanged.</summary>
    private static void SubtractMagnitudeInPlace(ref Big acc, in Big term)
    {
        var accMag = acc.Mag;
        var termMag = term.Mag;
        ulong borrow = 0;
        for (int i = 0; i < acc.Len; i++)
        {
            ulong x = accMag[i];
            ulong y = i < term.Len ? termMag[i] : 0;
            ulong partial = x - y;
            ulong borrow1 = x < y ? 1UL : 0UL;
            ulong difference = partial - borrow;
            ulong borrow2 = partial < borrow ? 1UL : 0UL;
            accMag[i] = difference;
            borrow = borrow1 + borrow2;
        }
        int len = acc.Len;
        while (len > 0 && accMag[len - 1] == 0)
            len--;
        acc.Len = len;
    }

    /// <summary>acc = |term| − |acc|, requiring |term| &gt; |acc| (the reversed direction
    /// reads each of acc's words before overwriting it, so it is safe in place).</summary>
    private static void SubtractMagnitudeReversedInPlace(ref Big acc, in Big term)
    {
        var accMag = acc.Mag;
        var termMag = term.Mag;
        ulong borrow = 0;
        for (int i = 0; i < term.Len; i++)
        {
            ulong x = termMag[i];
            ulong y = i < acc.Len ? accMag[i] : 0;
            ulong partial = x - y;
            ulong borrow1 = x < y ? 1UL : 0UL;
            ulong difference = partial - borrow;
            ulong borrow2 = partial < borrow ? 1UL : 0UL;
            accMag[i] = difference;
            borrow = borrow1 + borrow2;
        }
        int len = term.Len;
        while (len > 0 && accMag[len - 1] == 0)
            len--;
        acc.Len = len;
    }

    /// <summary>dst = a · b (schoolbook; dst must not alias a or b). The carry chain is the
    /// standard one — a·b + word + carry fits 128 bits exactly, so the per-step carry
    /// cannot overflow.</summary>
    private static void Multiply(in Big a, in Big b, ref Big dst)
    {
        if (a.Sign == 0 || b.Sign == 0)
        {
            dst.Len = 0;
            dst.Sign = 0;
            return;
        }
        var aMag = a.Mag;
        var bMag = b.Mag;
        var dMag = dst.Mag;
        int n = a.Len + b.Len;
        for (int i = 0; i < n; i++)
            dMag[i] = 0;
        for (int i = 0; i < a.Len; i++)
        {
            ulong ai = aMag[i];
            if (ai == 0)
                continue;
            ulong carry = 0;
            for (int j = 0; j < b.Len; j++)
            {
                ulong high = Math.BigMul(ai, bMag[j], out ulong low);
                ulong partial = dMag[i + j] + low;
                ulong carry1 = partial < low ? 1UL : 0UL;
                ulong sum = partial + carry;
                ulong carry2 = sum < partial ? 1UL : 0UL;
                dMag[i + j] = sum;
                carry = high + carry1 + carry2;
            }
            int k = i + b.Len;
            while (carry != 0)
            {
                ulong sum = dMag[k] + carry;
                carry = sum < carry ? 1UL : 0UL;
                dMag[k] = sum;
                k++;
            }
        }
        int len = n;
        while (len > 0 && dMag[len - 1] == 0)
            len--;
        dst.Len = len;
        dst.Sign = a.Sign * b.Sign;
    }
}
