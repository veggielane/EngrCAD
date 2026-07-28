using static EngrCAD.Core.ShewchukExpansions;

namespace EngrCAD.Core;

/// <summary>
/// Adaptive-exact 2D geometric predicates — a faithful C# port of Jonathan Richard
/// Shewchuk's public-domain <c>predicates.c</c> ("Adaptive Precision Floating-Point
/// Arithmetic and Fast Robust Geometric Predicates", 1997).
///
/// <para><see cref="Orient2d"/> and <see cref="InCircle"/> return a double whose SIGN is
/// exactly correct for every finite double input: each predicate first evaluates a cheap
/// floating-point approximation with a forward error bound, and only when the
/// approximation's magnitude falls inside the bound does it escalate through Shewchuk's
/// adaptive stages, ultimately computing the determinant as an exact multi-term
/// floating-point expansion. Exactly-degenerate inputs (collinear triples, cocircular
/// quadruples) therefore return exactly 0.0.</para>
///
/// <para>Correctness of the expansion arithmetic requires IEEE-754 double semantics with
/// round-to-nearest and NO fused multiply-add contraction. .NET guarantees both: every
/// C# floating-point operation rounds individually (RyuJIT never fuses a*b+c into an FMA
/// unless <c>Math.FusedMultiplyAdd</c> is called explicitly), and x64/arm64 have no x87
/// extended-precision registers. Inputs must be finite; magnitudes near the
/// overflow/underflow limits (roughly |x| &gt; 1e150 or subnormal differences) fall
/// outside Shewchuk's analysis, as in the original.</para>
///
/// <para>All expansion temporaries live in <c>stackalloc</c> spans — the predicates never
/// allocate on the heap.</para>
/// </summary>
public static class Predicates2d
{
    // Error-bound coefficients, exactly as computed by exactinit() in predicates.c.
    // (Epsilon / Splitter / ResultErrBound and the expansion macros live in
    // ShewchukExpansions, shared with Predicates3d.)
    private const double CcwErrBoundA = (3.0 + 16.0 * Epsilon) * Epsilon;
    private const double CcwErrBoundB = (2.0 + 12.0 * Epsilon) * Epsilon;
    private const double CcwErrBoundC = (9.0 + 64.0 * Epsilon) * Epsilon * Epsilon;
    private const double IccErrBoundA = (10.0 + 96.0 * Epsilon) * Epsilon;
    private const double IccErrBoundB = (4.0 + 48.0 * Epsilon) * Epsilon;
    private const double IccErrBoundC = (44.0 + 576.0 * Epsilon) * Epsilon * Epsilon;

    // ---- public API ----

    /// <summary>
    /// Sign-exact orientation test: positive when <paramref name="a"/>, <paramref name="b"/>,
    /// <paramref name="c"/> wind counter-clockwise, negative when clockwise, and exactly 0.0
    /// when the three points are exactly collinear. The magnitude approximates twice the
    /// signed triangle area, but only the sign is guaranteed.
    /// </summary>
    public static double Orient2d(in Vector2d a, in Vector2d b, in Vector2d c)
    {
        double detleft = (a.X - c.X) * (b.Y - c.Y);
        double detright = (a.Y - c.Y) * (b.X - c.X);
        double det = detleft - detright;
        double detsum;

        if (detleft > 0.0)
        {
            if (detright <= 0.0)
                return det;
            detsum = detleft + detright;
        }
        else if (detleft < 0.0)
        {
            if (detright >= 0.0)
                return det;
            detsum = -detleft - detright;
        }
        else
        {
            return det;
        }

        double errbound = CcwErrBoundA * detsum;
        if (det >= errbound || -det >= errbound)
            return det;

        return Orient2dAdapt(a, b, c, detsum);
    }

    /// <summary>-1, 0, or +1 — the exact sign of <see cref="Orient2d"/>.</summary>
    public static int Orient2dSign(in Vector2d a, in Vector2d b, in Vector2d c) =>
        Math.Sign(Orient2d(a, b, c));

    /// <summary>
    /// Sign-exact in-circle test: positive when <paramref name="d"/> lies strictly inside
    /// the circle through <paramref name="a"/>, <paramref name="b"/>, <paramref name="c"/>,
    /// negative when strictly outside, and exactly 0.0 when the four points are exactly
    /// cocircular — PROVIDED a, b, c wind counter-clockwise (a clockwise triple flips the
    /// sign, as in Shewchuk's original).
    /// </summary>
    public static double InCircle(in Vector2d a, in Vector2d b, in Vector2d c, in Vector2d d)
    {
        double adx = a.X - d.X;
        double bdx = b.X - d.X;
        double cdx = c.X - d.X;
        double ady = a.Y - d.Y;
        double bdy = b.Y - d.Y;
        double cdy = c.Y - d.Y;

        double bdxcdy = bdx * cdy;
        double cdxbdy = cdx * bdy;
        double alift = adx * adx + ady * ady;

        double cdxady = cdx * ady;
        double adxcdy = adx * cdy;
        double blift = bdx * bdx + bdy * bdy;

        double adxbdy = adx * bdy;
        double bdxady = bdx * ady;
        double clift = cdx * cdx + cdy * cdy;

        double det = alift * (bdxcdy - cdxbdy)
                   + blift * (cdxady - adxcdy)
                   + clift * (adxbdy - bdxady);

        double permanent = (Math.Abs(bdxcdy) + Math.Abs(cdxbdy)) * alift
                         + (Math.Abs(cdxady) + Math.Abs(adxcdy)) * blift
                         + (Math.Abs(adxbdy) + Math.Abs(bdxady)) * clift;
        double errbound = IccErrBoundA * permanent;
        if (det > errbound || -det > errbound)
            return det;

        return InCircleAdapt(a, b, c, d, permanent);
    }

    /// <summary>-1, 0, or +1 — the exact sign of <see cref="InCircle"/>.</summary>
    public static int InCircleSign(in Vector2d a, in Vector2d b, in Vector2d c, in Vector2d d) =>
        Math.Sign(InCircle(a, b, c, d));

    // ---- orient2d adaptive stages (orient2dadapt) ----

    private static double Orient2dAdapt(in Vector2d pa, in Vector2d pb, in Vector2d pc, double detsum)
    {
        double acx = pa.X - pc.X;
        double bcx = pb.X - pc.X;
        double acy = pa.Y - pc.Y;
        double bcy = pb.Y - pc.Y;

        TwoProduct(acx, bcy, out double detleft, out double detlefttail);
        TwoProduct(acy, bcx, out double detright, out double detrighttail);

        Span<double> b = stackalloc double[4];
        TwoTwoDiff(detleft, detlefttail, detright, detrighttail,
            out double b3, out b[2], out b[1], out b[0]);
        b[3] = b3;

        double det = Estimate(b, 4);
        double errbound = CcwErrBoundB * detsum;
        if (det >= errbound || -det >= errbound)
            return det;

        TwoDiffTail(pa.X, pc.X, acx, out double acxtail);
        TwoDiffTail(pb.X, pc.X, bcx, out double bcxtail);
        TwoDiffTail(pa.Y, pc.Y, acy, out double acytail);
        TwoDiffTail(pb.Y, pc.Y, bcy, out double bcytail);

        if (acxtail == 0.0 && acytail == 0.0 && bcxtail == 0.0 && bcytail == 0.0)
            return det;

        errbound = CcwErrBoundC * detsum + ResultErrBound * Math.Abs(det);
        det += (acx * bcytail + bcy * acxtail) - (acy * bcxtail + bcx * acytail);
        if (det >= errbound || -det >= errbound)
            return det;

        Span<double> u = stackalloc double[4];
        Span<double> c1 = stackalloc double[8];
        Span<double> c2 = stackalloc double[12];
        Span<double> d = stackalloc double[16];

        TwoProduct(acxtail, bcy, out double s1, out double s0);
        TwoProduct(acytail, bcx, out double t1, out double t0);
        TwoTwoDiff(s1, s0, t1, t0, out double u3, out u[2], out u[1], out u[0]);
        u[3] = u3;
        int c1Length = FastExpansionSumZeroElim(b, 4, u, 4, c1);

        TwoProduct(acx, bcytail, out s1, out s0);
        TwoProduct(acy, bcxtail, out t1, out t0);
        TwoTwoDiff(s1, s0, t1, t0, out u3, out u[2], out u[1], out u[0]);
        u[3] = u3;
        int c2Length = FastExpansionSumZeroElim(c1, c1Length, u, 4, c2);

        TwoProduct(acxtail, bcytail, out s1, out s0);
        TwoProduct(acytail, bcxtail, out t1, out t0);
        TwoTwoDiff(s1, s0, t1, t0, out u3, out u[2], out u[1], out u[0]);
        u[3] = u3;
        int dLength = FastExpansionSumZeroElim(c2, c2Length, u, 4, d);

        return d[dLength - 1];
    }

    // ---- incircle adaptive stages (incircleadapt) ----

    private static double InCircleAdapt(in Vector2d pa, in Vector2d pb, in Vector2d pc, in Vector2d pd, double permanent)
    {
        double adx = pa.X - pd.X;
        double bdx = pb.X - pd.X;
        double cdx = pc.X - pd.X;
        double ady = pa.Y - pd.Y;
        double bdy = pb.Y - pd.Y;
        double cdy = pc.Y - pd.Y;

        Span<double> bc = stackalloc double[4];
        Span<double> ca = stackalloc double[4];
        Span<double> ab = stackalloc double[4];
        Span<double> axbc = stackalloc double[8];
        Span<double> axxbc = stackalloc double[16];
        Span<double> aybc = stackalloc double[8];
        Span<double> ayybc = stackalloc double[16];
        Span<double> adet = stackalloc double[32];
        Span<double> bxca = stackalloc double[8];
        Span<double> bxxca = stackalloc double[16];
        Span<double> byca = stackalloc double[8];
        Span<double> byyca = stackalloc double[16];
        Span<double> bdet = stackalloc double[32];
        Span<double> cxab = stackalloc double[8];
        Span<double> cxxab = stackalloc double[16];
        Span<double> cyab = stackalloc double[8];
        Span<double> cyyab = stackalloc double[16];
        Span<double> cdet = stackalloc double[32];
        Span<double> abdet = stackalloc double[64];

        TwoProduct(bdx, cdy, out double bdxcdy1, out double bdxcdy0);
        TwoProduct(cdx, bdy, out double cdxbdy1, out double cdxbdy0);
        TwoTwoDiff(bdxcdy1, bdxcdy0, cdxbdy1, cdxbdy0, out double bc3, out bc[2], out bc[1], out bc[0]);
        bc[3] = bc3;
        int axbclen = ScaleExpansionZeroElim(bc, 4, adx, axbc);
        int axxbclen = ScaleExpansionZeroElim(axbc, axbclen, adx, axxbc);
        int aybclen = ScaleExpansionZeroElim(bc, 4, ady, aybc);
        int ayybclen = ScaleExpansionZeroElim(aybc, aybclen, ady, ayybc);
        int alen = FastExpansionSumZeroElim(axxbc, axxbclen, ayybc, ayybclen, adet);

        TwoProduct(cdx, ady, out double cdxady1, out double cdxady0);
        TwoProduct(adx, cdy, out double adxcdy1, out double adxcdy0);
        TwoTwoDiff(cdxady1, cdxady0, adxcdy1, adxcdy0, out double ca3, out ca[2], out ca[1], out ca[0]);
        ca[3] = ca3;
        int bxcalen = ScaleExpansionZeroElim(ca, 4, bdx, bxca);
        int bxxcalen = ScaleExpansionZeroElim(bxca, bxcalen, bdx, bxxca);
        int bycalen = ScaleExpansionZeroElim(ca, 4, bdy, byca);
        int byycalen = ScaleExpansionZeroElim(byca, bycalen, bdy, byyca);
        int blen = FastExpansionSumZeroElim(bxxca, bxxcalen, byyca, byycalen, bdet);

        TwoProduct(adx, bdy, out double adxbdy1, out double adxbdy0);
        TwoProduct(bdx, ady, out double bdxady1, out double bdxady0);
        TwoTwoDiff(adxbdy1, adxbdy0, bdxady1, bdxady0, out double ab3, out ab[2], out ab[1], out ab[0]);
        ab[3] = ab3;
        int cxablen = ScaleExpansionZeroElim(ab, 4, cdx, cxab);
        int cxxablen = ScaleExpansionZeroElim(cxab, cxablen, cdx, cxxab);
        int cyablen = ScaleExpansionZeroElim(ab, 4, cdy, cyab);
        int cyyablen = ScaleExpansionZeroElim(cyab, cyablen, cdy, cyyab);
        int clen = FastExpansionSumZeroElim(cxxab, cxxablen, cyyab, cyyablen, cdet);

        int ablen = FastExpansionSumZeroElim(adet, alen, bdet, blen, abdet);

        Span<double> fin1 = stackalloc double[1152];
        Span<double> fin2 = stackalloc double[1152];
        int finlength = FastExpansionSumZeroElim(abdet, ablen, cdet, clen, fin1);

        double det = Estimate(fin1, finlength);
        double errbound = IccErrBoundB * permanent;
        if (det >= errbound || -det >= errbound)
            return det;

        TwoDiffTail(pa.X, pd.X, adx, out double adxtail);
        TwoDiffTail(pa.Y, pd.Y, ady, out double adytail);
        TwoDiffTail(pb.X, pd.X, bdx, out double bdxtail);
        TwoDiffTail(pb.Y, pd.Y, bdy, out double bdytail);
        TwoDiffTail(pc.X, pd.X, cdx, out double cdxtail);
        TwoDiffTail(pc.Y, pd.Y, cdy, out double cdytail);
        if (adxtail == 0.0 && bdxtail == 0.0 && cdxtail == 0.0
            && adytail == 0.0 && bdytail == 0.0 && cdytail == 0.0)
            return det;

        errbound = IccErrBoundC * permanent + ResultErrBound * Math.Abs(det);
        det += ((adx * adx + ady * ady) * ((bdx * cdytail + cdy * bdxtail)
                                          - (bdy * cdxtail + cdx * bdytail))
                + 2.0 * (adx * adxtail + ady * adytail) * (bdx * cdy - bdy * cdx))
             + ((bdx * bdx + bdy * bdy) * ((cdx * adytail + ady * cdxtail)
                                           - (cdy * adxtail + adx * cdytail))
                + 2.0 * (bdx * bdxtail + bdy * bdytail) * (cdx * ady - cdy * adx))
             + ((cdx * cdx + cdy * cdy) * ((adx * bdytail + bdy * adxtail)
                                           - (ady * bdxtail + bdx * adytail))
                + 2.0 * (cdx * cdxtail + cdy * cdytail) * (adx * bdy - ady * bdx));
        if (det >= errbound || -det >= errbound)
            return det;

        // Full exact evaluation from here on: accumulate every remaining term of the
        // exactly-expanded determinant into the running expansion (fin1/fin2 ping-pong).
        Span<double> temp8 = stackalloc double[8];
        Span<double> temp16a = stackalloc double[16];
        Span<double> temp16b = stackalloc double[16];
        Span<double> temp16c = stackalloc double[16];
        Span<double> temp32a = stackalloc double[32];
        Span<double> temp32b = stackalloc double[32];
        Span<double> temp48 = stackalloc double[48];
        Span<double> temp64 = stackalloc double[64];
        Span<double> aa = stackalloc double[4];
        Span<double> bb = stackalloc double[4];
        Span<double> cc = stackalloc double[4];
        Span<double> u = stackalloc double[4];
        Span<double> v = stackalloc double[4];
        Span<double> axtbb = stackalloc double[8];
        Span<double> axtcc = stackalloc double[8];
        Span<double> aytbb = stackalloc double[8];
        Span<double> aytcc = stackalloc double[8];
        Span<double> bxtaa = stackalloc double[8];
        Span<double> bxtcc = stackalloc double[8];
        Span<double> bytaa = stackalloc double[8];
        Span<double> bytcc = stackalloc double[8];
        Span<double> cxtaa = stackalloc double[8];
        Span<double> cxtbb = stackalloc double[8];
        Span<double> cytaa = stackalloc double[8];
        Span<double> cytbb = stackalloc double[8];
        Span<double> axtbc = stackalloc double[8];
        Span<double> aytbc = stackalloc double[8];
        Span<double> bxtca = stackalloc double[8];
        Span<double> bytca = stackalloc double[8];
        Span<double> cxtab = stackalloc double[8];
        Span<double> cytab = stackalloc double[8];
        Span<double> axtbct = stackalloc double[16];
        Span<double> aytbct = stackalloc double[16];
        Span<double> bxtcat = stackalloc double[16];
        Span<double> bytcat = stackalloc double[16];
        Span<double> cxtabt = stackalloc double[16];
        Span<double> cytabt = stackalloc double[16];
        Span<double> axtbctt = stackalloc double[8];
        Span<double> aytbctt = stackalloc double[8];
        Span<double> bxtcatt = stackalloc double[8];
        Span<double> bytcatt = stackalloc double[8];
        Span<double> cxtabtt = stackalloc double[8];
        Span<double> cytabtt = stackalloc double[8];
        Span<double> abt = stackalloc double[8];
        Span<double> bct = stackalloc double[8];
        Span<double> cat = stackalloc double[8];
        Span<double> abtt = stackalloc double[4];
        Span<double> bctt = stackalloc double[4];
        Span<double> catt = stackalloc double[4];

        var finnow = fin1;
        var finother = fin2;

        int axtbclen = 0, aytbclen = 0, bxtcalen = 0, bytcalen = 0, cxtablen = 0, cytablen = 0;

        if (bdxtail != 0.0 || bdytail != 0.0 || cdxtail != 0.0 || cdytail != 0.0)
        {
            Square(adx, out double adxadx1, out double adxadx0);
            Square(ady, out double adyady1, out double adyady0);
            TwoTwoSum(adxadx1, adxadx0, adyady1, adyady0, out double aa3, out aa[2], out aa[1], out aa[0]);
            aa[3] = aa3;
        }
        if (cdxtail != 0.0 || cdytail != 0.0 || adxtail != 0.0 || adytail != 0.0)
        {
            Square(bdx, out double bdxbdx1, out double bdxbdx0);
            Square(bdy, out double bdybdy1, out double bdybdy0);
            TwoTwoSum(bdxbdx1, bdxbdx0, bdybdy1, bdybdy0, out double bb3, out bb[2], out bb[1], out bb[0]);
            bb[3] = bb3;
        }
        if (adxtail != 0.0 || adytail != 0.0 || bdxtail != 0.0 || bdytail != 0.0)
        {
            Square(cdx, out double cdxcdx1, out double cdxcdx0);
            Square(cdy, out double cdycdy1, out double cdycdy0);
            TwoTwoSum(cdxcdx1, cdxcdx0, cdycdy1, cdycdy0, out double cc3, out cc[2], out cc[1], out cc[0]);
            cc[3] = cc3;
        }

        int temp8len, temp16alen, temp16blen, temp16clen, temp32alen, temp32blen, temp48len, temp64len;
        double ti1, ti0, tj1, tj0, u3, v3, negate;

        if (adxtail != 0.0)
        {
            axtbclen = ScaleExpansionZeroElim(bc, 4, adxtail, axtbc);
            temp16alen = ScaleExpansionZeroElim(axtbc, axtbclen, 2.0 * adx, temp16a);

            int axtcclen = ScaleExpansionZeroElim(cc, 4, adxtail, axtcc);
            temp16blen = ScaleExpansionZeroElim(axtcc, axtcclen, bdy, temp16b);

            int axtbblen = ScaleExpansionZeroElim(bb, 4, adxtail, axtbb);
            temp16clen = ScaleExpansionZeroElim(axtbb, axtbblen, -cdy, temp16c);

            temp32alen = FastExpansionSumZeroElim(temp16a, temp16alen, temp16b, temp16blen, temp32a);
            temp48len = FastExpansionSumZeroElim(temp16c, temp16clen, temp32a, temp32alen, temp48);
            finlength = FastExpansionSumZeroElim(finnow, finlength, temp48, temp48len, finother);
            { var finswap = finnow; finnow = finother; finother = finswap; }
        }
        if (adytail != 0.0)
        {
            aytbclen = ScaleExpansionZeroElim(bc, 4, adytail, aytbc);
            temp16alen = ScaleExpansionZeroElim(aytbc, aytbclen, 2.0 * ady, temp16a);

            int aytbblen = ScaleExpansionZeroElim(bb, 4, adytail, aytbb);
            temp16blen = ScaleExpansionZeroElim(aytbb, aytbblen, cdx, temp16b);

            int aytcclen = ScaleExpansionZeroElim(cc, 4, adytail, aytcc);
            temp16clen = ScaleExpansionZeroElim(aytcc, aytcclen, -bdx, temp16c);

            temp32alen = FastExpansionSumZeroElim(temp16a, temp16alen, temp16b, temp16blen, temp32a);
            temp48len = FastExpansionSumZeroElim(temp16c, temp16clen, temp32a, temp32alen, temp48);
            finlength = FastExpansionSumZeroElim(finnow, finlength, temp48, temp48len, finother);
            { var finswap = finnow; finnow = finother; finother = finswap; }
        }
        if (bdxtail != 0.0)
        {
            bxtcalen = ScaleExpansionZeroElim(ca, 4, bdxtail, bxtca);
            temp16alen = ScaleExpansionZeroElim(bxtca, bxtcalen, 2.0 * bdx, temp16a);

            int bxtaalen = ScaleExpansionZeroElim(aa, 4, bdxtail, bxtaa);
            temp16blen = ScaleExpansionZeroElim(bxtaa, bxtaalen, cdy, temp16b);

            int bxtcclen = ScaleExpansionZeroElim(cc, 4, bdxtail, bxtcc);
            temp16clen = ScaleExpansionZeroElim(bxtcc, bxtcclen, -ady, temp16c);

            temp32alen = FastExpansionSumZeroElim(temp16a, temp16alen, temp16b, temp16blen, temp32a);
            temp48len = FastExpansionSumZeroElim(temp16c, temp16clen, temp32a, temp32alen, temp48);
            finlength = FastExpansionSumZeroElim(finnow, finlength, temp48, temp48len, finother);
            { var finswap = finnow; finnow = finother; finother = finswap; }
        }
        if (bdytail != 0.0)
        {
            bytcalen = ScaleExpansionZeroElim(ca, 4, bdytail, bytca);
            temp16alen = ScaleExpansionZeroElim(bytca, bytcalen, 2.0 * bdy, temp16a);

            int bytcclen = ScaleExpansionZeroElim(cc, 4, bdytail, bytcc);
            temp16blen = ScaleExpansionZeroElim(bytcc, bytcclen, adx, temp16b);

            int bytaalen = ScaleExpansionZeroElim(aa, 4, bdytail, bytaa);
            temp16clen = ScaleExpansionZeroElim(bytaa, bytaalen, -cdx, temp16c);

            temp32alen = FastExpansionSumZeroElim(temp16a, temp16alen, temp16b, temp16blen, temp32a);
            temp48len = FastExpansionSumZeroElim(temp16c, temp16clen, temp32a, temp32alen, temp48);
            finlength = FastExpansionSumZeroElim(finnow, finlength, temp48, temp48len, finother);
            { var finswap = finnow; finnow = finother; finother = finswap; }
        }
        if (cdxtail != 0.0)
        {
            cxtablen = ScaleExpansionZeroElim(ab, 4, cdxtail, cxtab);
            temp16alen = ScaleExpansionZeroElim(cxtab, cxtablen, 2.0 * cdx, temp16a);

            int cxtbblen = ScaleExpansionZeroElim(bb, 4, cdxtail, cxtbb);
            temp16blen = ScaleExpansionZeroElim(cxtbb, cxtbblen, ady, temp16b);

            int cxtaalen = ScaleExpansionZeroElim(aa, 4, cdxtail, cxtaa);
            temp16clen = ScaleExpansionZeroElim(cxtaa, cxtaalen, -bdy, temp16c);

            temp32alen = FastExpansionSumZeroElim(temp16a, temp16alen, temp16b, temp16blen, temp32a);
            temp48len = FastExpansionSumZeroElim(temp16c, temp16clen, temp32a, temp32alen, temp48);
            finlength = FastExpansionSumZeroElim(finnow, finlength, temp48, temp48len, finother);
            { var finswap = finnow; finnow = finother; finother = finswap; }
        }
        if (cdytail != 0.0)
        {
            cytablen = ScaleExpansionZeroElim(ab, 4, cdytail, cytab);
            temp16alen = ScaleExpansionZeroElim(cytab, cytablen, 2.0 * cdy, temp16a);

            int cytaalen = ScaleExpansionZeroElim(aa, 4, cdytail, cytaa);
            temp16blen = ScaleExpansionZeroElim(cytaa, cytaalen, bdx, temp16b);

            int cytbblen = ScaleExpansionZeroElim(bb, 4, cdytail, cytbb);
            temp16clen = ScaleExpansionZeroElim(cytbb, cytbblen, -adx, temp16c);

            temp32alen = FastExpansionSumZeroElim(temp16a, temp16alen, temp16b, temp16blen, temp32a);
            temp48len = FastExpansionSumZeroElim(temp16c, temp16clen, temp32a, temp32alen, temp48);
            finlength = FastExpansionSumZeroElim(finnow, finlength, temp48, temp48len, finother);
            { var finswap = finnow; finnow = finother; finother = finswap; }
        }

        if (adxtail != 0.0 || adytail != 0.0)
        {
            int bctlen, bcttlen;
            if (bdxtail != 0.0 || bdytail != 0.0 || cdxtail != 0.0 || cdytail != 0.0)
            {
                TwoProduct(bdxtail, cdy, out ti1, out ti0);
                TwoProduct(bdx, cdytail, out tj1, out tj0);
                TwoTwoSum(ti1, ti0, tj1, tj0, out u3, out u[2], out u[1], out u[0]);
                u[3] = u3;
                negate = -bdy;
                TwoProduct(cdxtail, negate, out ti1, out ti0);
                negate = -bdytail;
                TwoProduct(cdx, negate, out tj1, out tj0);
                TwoTwoSum(ti1, ti0, tj1, tj0, out v3, out v[2], out v[1], out v[0]);
                v[3] = v3;
                bctlen = FastExpansionSumZeroElim(u, 4, v, 4, bct);

                TwoProduct(bdxtail, cdytail, out ti1, out ti0);
                TwoProduct(cdxtail, bdytail, out tj1, out tj0);
                TwoTwoDiff(ti1, ti0, tj1, tj0, out double bctt3, out bctt[2], out bctt[1], out bctt[0]);
                bctt[3] = bctt3;
                bcttlen = 4;
            }
            else
            {
                bct[0] = 0.0;
                bctlen = 1;
                bctt[0] = 0.0;
                bcttlen = 1;
            }

            if (adxtail != 0.0)
            {
                temp16alen = ScaleExpansionZeroElim(axtbc, axtbclen, adxtail, temp16a);
                int axtbctlen = ScaleExpansionZeroElim(bct, bctlen, adxtail, axtbct);
                temp32alen = ScaleExpansionZeroElim(axtbct, axtbctlen, 2.0 * adx, temp32a);
                temp48len = FastExpansionSumZeroElim(temp16a, temp16alen, temp32a, temp32alen, temp48);
                finlength = FastExpansionSumZeroElim(finnow, finlength, temp48, temp48len, finother);
                { var finswap = finnow; finnow = finother; finother = finswap; }
                if (bdytail != 0.0)
                {
                    temp8len = ScaleExpansionZeroElim(cc, 4, adxtail, temp8);
                    temp16alen = ScaleExpansionZeroElim(temp8, temp8len, bdytail, temp16a);
                    finlength = FastExpansionSumZeroElim(finnow, finlength, temp16a, temp16alen, finother);
                    { var finswap = finnow; finnow = finother; finother = finswap; }
                }
                if (cdytail != 0.0)
                {
                    temp8len = ScaleExpansionZeroElim(bb, 4, -adxtail, temp8);
                    temp16alen = ScaleExpansionZeroElim(temp8, temp8len, cdytail, temp16a);
                    finlength = FastExpansionSumZeroElim(finnow, finlength, temp16a, temp16alen, finother);
                    { var finswap = finnow; finnow = finother; finother = finswap; }
                }

                temp32alen = ScaleExpansionZeroElim(axtbct, axtbctlen, adxtail, temp32a);
                int axtbcttlen = ScaleExpansionZeroElim(bctt, bcttlen, adxtail, axtbctt);
                temp16alen = ScaleExpansionZeroElim(axtbctt, axtbcttlen, 2.0 * adx, temp16a);
                temp16blen = ScaleExpansionZeroElim(axtbctt, axtbcttlen, adxtail, temp16b);
                temp32blen = FastExpansionSumZeroElim(temp16a, temp16alen, temp16b, temp16blen, temp32b);
                temp64len = FastExpansionSumZeroElim(temp32a, temp32alen, temp32b, temp32blen, temp64);
                finlength = FastExpansionSumZeroElim(finnow, finlength, temp64, temp64len, finother);
                { var finswap = finnow; finnow = finother; finother = finswap; }
            }
            if (adytail != 0.0)
            {
                temp16alen = ScaleExpansionZeroElim(aytbc, aytbclen, adytail, temp16a);
                int aytbctlen = ScaleExpansionZeroElim(bct, bctlen, adytail, aytbct);
                temp32alen = ScaleExpansionZeroElim(aytbct, aytbctlen, 2.0 * ady, temp32a);
                temp48len = FastExpansionSumZeroElim(temp16a, temp16alen, temp32a, temp32alen, temp48);
                finlength = FastExpansionSumZeroElim(finnow, finlength, temp48, temp48len, finother);
                { var finswap = finnow; finnow = finother; finother = finswap; }

                temp32alen = ScaleExpansionZeroElim(aytbct, aytbctlen, adytail, temp32a);
                int aytbcttlen = ScaleExpansionZeroElim(bctt, bcttlen, adytail, aytbctt);
                temp16alen = ScaleExpansionZeroElim(aytbctt, aytbcttlen, 2.0 * ady, temp16a);
                temp16blen = ScaleExpansionZeroElim(aytbctt, aytbcttlen, adytail, temp16b);
                temp32blen = FastExpansionSumZeroElim(temp16a, temp16alen, temp16b, temp16blen, temp32b);
                temp64len = FastExpansionSumZeroElim(temp32a, temp32alen, temp32b, temp32blen, temp64);
                finlength = FastExpansionSumZeroElim(finnow, finlength, temp64, temp64len, finother);
                { var finswap = finnow; finnow = finother; finother = finswap; }
            }
        }

        if (bdxtail != 0.0 || bdytail != 0.0)
        {
            int catlen, cattlen;
            if (cdxtail != 0.0 || cdytail != 0.0 || adxtail != 0.0 || adytail != 0.0)
            {
                TwoProduct(cdxtail, ady, out ti1, out ti0);
                TwoProduct(cdx, adytail, out tj1, out tj0);
                TwoTwoSum(ti1, ti0, tj1, tj0, out u3, out u[2], out u[1], out u[0]);
                u[3] = u3;
                negate = -cdy;
                TwoProduct(adxtail, negate, out ti1, out ti0);
                negate = -cdytail;
                TwoProduct(adx, negate, out tj1, out tj0);
                TwoTwoSum(ti1, ti0, tj1, tj0, out v3, out v[2], out v[1], out v[0]);
                v[3] = v3;
                catlen = FastExpansionSumZeroElim(u, 4, v, 4, cat);

                TwoProduct(cdxtail, adytail, out ti1, out ti0);
                TwoProduct(adxtail, cdytail, out tj1, out tj0);
                TwoTwoDiff(ti1, ti0, tj1, tj0, out double catt3, out catt[2], out catt[1], out catt[0]);
                catt[3] = catt3;
                cattlen = 4;
            }
            else
            {
                cat[0] = 0.0;
                catlen = 1;
                catt[0] = 0.0;
                cattlen = 1;
            }

            if (bdxtail != 0.0)
            {
                temp16alen = ScaleExpansionZeroElim(bxtca, bxtcalen, bdxtail, temp16a);
                int bxtcatlen = ScaleExpansionZeroElim(cat, catlen, bdxtail, bxtcat);
                temp32alen = ScaleExpansionZeroElim(bxtcat, bxtcatlen, 2.0 * bdx, temp32a);
                temp48len = FastExpansionSumZeroElim(temp16a, temp16alen, temp32a, temp32alen, temp48);
                finlength = FastExpansionSumZeroElim(finnow, finlength, temp48, temp48len, finother);
                { var finswap = finnow; finnow = finother; finother = finswap; }
                if (cdytail != 0.0)
                {
                    temp8len = ScaleExpansionZeroElim(aa, 4, bdxtail, temp8);
                    temp16alen = ScaleExpansionZeroElim(temp8, temp8len, cdytail, temp16a);
                    finlength = FastExpansionSumZeroElim(finnow, finlength, temp16a, temp16alen, finother);
                    { var finswap = finnow; finnow = finother; finother = finswap; }
                }
                if (adytail != 0.0)
                {
                    temp8len = ScaleExpansionZeroElim(cc, 4, -bdxtail, temp8);
                    temp16alen = ScaleExpansionZeroElim(temp8, temp8len, adytail, temp16a);
                    finlength = FastExpansionSumZeroElim(finnow, finlength, temp16a, temp16alen, finother);
                    { var finswap = finnow; finnow = finother; finother = finswap; }
                }

                temp32alen = ScaleExpansionZeroElim(bxtcat, bxtcatlen, bdxtail, temp32a);
                int bxtcattlen = ScaleExpansionZeroElim(catt, cattlen, bdxtail, bxtcatt);
                temp16alen = ScaleExpansionZeroElim(bxtcatt, bxtcattlen, 2.0 * bdx, temp16a);
                temp16blen = ScaleExpansionZeroElim(bxtcatt, bxtcattlen, bdxtail, temp16b);
                temp32blen = FastExpansionSumZeroElim(temp16a, temp16alen, temp16b, temp16blen, temp32b);
                temp64len = FastExpansionSumZeroElim(temp32a, temp32alen, temp32b, temp32blen, temp64);
                finlength = FastExpansionSumZeroElim(finnow, finlength, temp64, temp64len, finother);
                { var finswap = finnow; finnow = finother; finother = finswap; }
            }
            if (bdytail != 0.0)
            {
                temp16alen = ScaleExpansionZeroElim(bytca, bytcalen, bdytail, temp16a);
                int bytcatlen = ScaleExpansionZeroElim(cat, catlen, bdytail, bytcat);
                temp32alen = ScaleExpansionZeroElim(bytcat, bytcatlen, 2.0 * bdy, temp32a);
                temp48len = FastExpansionSumZeroElim(temp16a, temp16alen, temp32a, temp32alen, temp48);
                finlength = FastExpansionSumZeroElim(finnow, finlength, temp48, temp48len, finother);
                { var finswap = finnow; finnow = finother; finother = finswap; }

                temp32alen = ScaleExpansionZeroElim(bytcat, bytcatlen, bdytail, temp32a);
                int bytcattlen = ScaleExpansionZeroElim(catt, cattlen, bdytail, bytcatt);
                temp16alen = ScaleExpansionZeroElim(bytcatt, bytcattlen, 2.0 * bdy, temp16a);
                temp16blen = ScaleExpansionZeroElim(bytcatt, bytcattlen, bdytail, temp16b);
                temp32blen = FastExpansionSumZeroElim(temp16a, temp16alen, temp16b, temp16blen, temp32b);
                temp64len = FastExpansionSumZeroElim(temp32a, temp32alen, temp32b, temp32blen, temp64);
                finlength = FastExpansionSumZeroElim(finnow, finlength, temp64, temp64len, finother);
                { var finswap = finnow; finnow = finother; finother = finswap; }
            }
        }

        if (cdxtail != 0.0 || cdytail != 0.0)
        {
            int abtlen, abttlen;
            if (adxtail != 0.0 || adytail != 0.0 || bdxtail != 0.0 || bdytail != 0.0)
            {
                TwoProduct(adxtail, bdy, out ti1, out ti0);
                TwoProduct(adx, bdytail, out tj1, out tj0);
                TwoTwoSum(ti1, ti0, tj1, tj0, out u3, out u[2], out u[1], out u[0]);
                u[3] = u3;
                negate = -ady;
                TwoProduct(bdxtail, negate, out ti1, out ti0);
                negate = -adytail;
                TwoProduct(bdx, negate, out tj1, out tj0);
                TwoTwoSum(ti1, ti0, tj1, tj0, out v3, out v[2], out v[1], out v[0]);
                v[3] = v3;
                abtlen = FastExpansionSumZeroElim(u, 4, v, 4, abt);

                TwoProduct(adxtail, bdytail, out ti1, out ti0);
                TwoProduct(bdxtail, adytail, out tj1, out tj0);
                TwoTwoDiff(ti1, ti0, tj1, tj0, out double abtt3, out abtt[2], out abtt[1], out abtt[0]);
                abtt[3] = abtt3;
                abttlen = 4;
            }
            else
            {
                abt[0] = 0.0;
                abtlen = 1;
                abtt[0] = 0.0;
                abttlen = 1;
            }

            if (cdxtail != 0.0)
            {
                temp16alen = ScaleExpansionZeroElim(cxtab, cxtablen, cdxtail, temp16a);
                int cxtabtlen = ScaleExpansionZeroElim(abt, abtlen, cdxtail, cxtabt);
                temp32alen = ScaleExpansionZeroElim(cxtabt, cxtabtlen, 2.0 * cdx, temp32a);
                temp48len = FastExpansionSumZeroElim(temp16a, temp16alen, temp32a, temp32alen, temp48);
                finlength = FastExpansionSumZeroElim(finnow, finlength, temp48, temp48len, finother);
                { var finswap = finnow; finnow = finother; finother = finswap; }
                if (adytail != 0.0)
                {
                    temp8len = ScaleExpansionZeroElim(bb, 4, cdxtail, temp8);
                    temp16alen = ScaleExpansionZeroElim(temp8, temp8len, adytail, temp16a);
                    finlength = FastExpansionSumZeroElim(finnow, finlength, temp16a, temp16alen, finother);
                    { var finswap = finnow; finnow = finother; finother = finswap; }
                }
                if (bdytail != 0.0)
                {
                    temp8len = ScaleExpansionZeroElim(aa, 4, -cdxtail, temp8);
                    temp16alen = ScaleExpansionZeroElim(temp8, temp8len, bdytail, temp16a);
                    finlength = FastExpansionSumZeroElim(finnow, finlength, temp16a, temp16alen, finother);
                    { var finswap = finnow; finnow = finother; finother = finswap; }
                }

                temp32alen = ScaleExpansionZeroElim(cxtabt, cxtabtlen, cdxtail, temp32a);
                int cxtabttlen = ScaleExpansionZeroElim(abtt, abttlen, cdxtail, cxtabtt);
                temp16alen = ScaleExpansionZeroElim(cxtabtt, cxtabttlen, 2.0 * cdx, temp16a);
                temp16blen = ScaleExpansionZeroElim(cxtabtt, cxtabttlen, cdxtail, temp16b);
                temp32blen = FastExpansionSumZeroElim(temp16a, temp16alen, temp16b, temp16blen, temp32b);
                temp64len = FastExpansionSumZeroElim(temp32a, temp32alen, temp32b, temp32blen, temp64);
                finlength = FastExpansionSumZeroElim(finnow, finlength, temp64, temp64len, finother);
                { var finswap = finnow; finnow = finother; finother = finswap; }
            }
            if (cdytail != 0.0)
            {
                temp16alen = ScaleExpansionZeroElim(cytab, cytablen, cdytail, temp16a);
                int cytabtlen = ScaleExpansionZeroElim(abt, abtlen, cdytail, cytabt);
                temp32alen = ScaleExpansionZeroElim(cytabt, cytabtlen, 2.0 * cdy, temp32a);
                temp48len = FastExpansionSumZeroElim(temp16a, temp16alen, temp32a, temp32alen, temp48);
                finlength = FastExpansionSumZeroElim(finnow, finlength, temp48, temp48len, finother);
                { var finswap = finnow; finnow = finother; finother = finswap; }

                temp32alen = ScaleExpansionZeroElim(cytabt, cytabtlen, cdytail, temp32a);
                int cytabttlen = ScaleExpansionZeroElim(abtt, abttlen, cdytail, cytabtt);
                temp16alen = ScaleExpansionZeroElim(cytabtt, cytabttlen, 2.0 * cdy, temp16a);
                temp16blen = ScaleExpansionZeroElim(cytabtt, cytabttlen, cdytail, temp16b);
                temp32blen = FastExpansionSumZeroElim(temp16a, temp16alen, temp16b, temp16blen, temp32b);
                temp64len = FastExpansionSumZeroElim(temp32a, temp32alen, temp32b, temp32blen, temp64);
                finlength = FastExpansionSumZeroElim(finnow, finlength, temp64, temp64len, finother);
                { var finswap = finnow; finnow = finother; finother = finswap; }
            }
        }

        return finnow[finlength - 1];
    }
}
