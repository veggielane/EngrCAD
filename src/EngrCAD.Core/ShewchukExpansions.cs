using System.Runtime.CompilerServices;

namespace EngrCAD.Core;

/// <summary>
/// Shewchuk's exact floating-point expansion arithmetic — the shared substrate of
/// <see cref="Predicates2d"/> and <see cref="Predicates3d"/>.
///
/// <para>An <em>expansion</em> is a sequence of non-overlapping doubles whose exact sum is
/// the represented value, ordered smallest-magnitude first. Every routine here computes an
/// expansion that is <em>exactly</em> equal to the arithmetic result, so a predicate built
/// from them is sign-exact for all finite double inputs.</para>
///
/// <para>Correctness requires IEEE-754 doubles with round-to-nearest and NO fused
/// multiply-add contraction. .NET guarantees both: every C# floating-point operation rounds
/// individually (RyuJIT never fuses <c>a*b+c</c> unless <see cref="Math.FusedMultiplyAdd"/>
/// is called explicitly), and x64/arm64 have no x87 extended-precision registers.</para>
///
/// <para>These bodies are a faithful transcription of the macros in Shewchuk's
/// public-domain <c>predicates.c</c> ("Adaptive Precision Floating-Point Arithmetic and Fast
/// Robust Geometric Predicates", 1997). They are the algorithm — never "clean them up".
/// They live here rather than being copied into each predicate class because two copies of
/// a numerical routine drift (see the <c>Distance3d</c> note in CLAUDE.md, where exactly
/// that happened and only one copy kept its degeneracy guards).</para>
/// </summary>
internal static class ShewchukExpansions
{
    /// <summary>2^-53: half an ulp of 1.0 — the unit roundoff used in all of Shewchuk's bounds.</summary>
    public const double Epsilon = 1.1102230246251565e-16;

    /// <summary>2^27 + 1: splits a 53-bit significand into two 26-bit halves (Dekker splitting).</summary>
    public const double Splitter = 134217729.0;

    /// <summary>Bound on the round-off of summing an expansion's components (exactinit's resulterrbound).</summary>
    public const double ResultErrBound = (3.0 + 8.0 * Epsilon) * Epsilon;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FastTwoSum(double a, double b, out double x, out double y)
    {
        x = a + b;
        double bvirt = x - a;
        y = b - bvirt;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TwoSum(double a, double b, out double x, out double y)
    {
        x = a + b;
        double bvirt = x - a;
        double avirt = x - bvirt;
        double bround = b - bvirt;
        double around = a - avirt;
        y = around + bround;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TwoDiff(double a, double b, out double x, out double y)
    {
        x = a - b;
        TwoDiffTail(a, b, x, out y);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TwoDiffTail(double a, double b, double x, out double y)
    {
        double bvirt = a - x;
        double avirt = x + bvirt;
        double bround = bvirt - b;
        double around = a - avirt;
        y = around + bround;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Split(double a, out double hi, out double lo)
    {
        double c = Splitter * a;
        double abig = c - a;
        hi = c - abig;
        lo = a - hi;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TwoProduct(double a, double b, out double x, out double y)
    {
        x = a * b;
        Split(a, out double ahi, out double alo);
        Split(b, out double bhi, out double blo);
        double err1 = x - ahi * bhi;
        double err2 = err1 - alo * bhi;
        double err3 = err2 - ahi * blo;
        y = alo * blo - err3;
    }

    /// <summary>Two_Product where b has already been split into bhi/blo.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TwoProductPresplit(double a, double b, double bhi, double blo, out double x, out double y)
    {
        x = a * b;
        Split(a, out double ahi, out double alo);
        double err1 = x - ahi * bhi;
        double err2 = err1 - alo * bhi;
        double err3 = err2 - ahi * blo;
        y = alo * blo - err3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Square(double a, out double x, out double y)
    {
        x = a * a;
        Split(a, out double ahi, out double alo);
        double err1 = x - ahi * ahi;
        double err3 = err1 - (ahi + ahi) * alo;
        y = alo * alo - err3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TwoOneSum(double a1, double a0, double b, out double x2, out double x1, out double x0)
    {
        TwoSum(a0, b, out double i, out x0);
        TwoSum(a1, i, out x2, out x1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TwoOneDiff(double a1, double a0, double b, out double x2, out double x1, out double x0)
    {
        TwoDiff(a0, b, out double i, out x0);
        TwoSum(a1, i, out x2, out x1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TwoTwoSum(double a1, double a0, double b1, double b0,
        out double x3, out double x2, out double x1, out double x0)
    {
        TwoOneSum(a1, a0, b0, out double j, out double r0, out x0);
        TwoOneSum(j, r0, b1, out x3, out x2, out x1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TwoTwoDiff(double a1, double a0, double b1, double b0,
        out double x3, out double x2, out double x1, out double x0)
    {
        TwoOneDiff(a1, a0, b0, out double j, out double r0, out x0);
        TwoOneDiff(j, r0, b1, out x3, out x2, out x1);
    }

    /// <summary>
    /// fast_expansion_sum_zeroelim: sums two nonoverlapping expansions, eliminating zero
    /// components. h must not alias e or f. Returns the length of h.
    /// </summary>
    public static int FastExpansionSumZeroElim(
        ReadOnlySpan<double> e, int elen, ReadOnlySpan<double> f, int flen, Span<double> h)
    {
        double q, qnew, hh;
        int eindex = 0, findex = 0, hindex = 0;
        double enow = e[0], fnow = f[0];

        // (The original reads one slot past the consumed array; those values are never
        // used, so here every advance guards the read instead.)
        if ((fnow > enow) == (fnow > -enow))
        {
            q = enow;
            if (++eindex < elen) enow = e[eindex];
        }
        else
        {
            q = fnow;
            if (++findex < flen) fnow = f[findex];
        }

        if (eindex < elen && findex < flen)
        {
            if ((fnow > enow) == (fnow > -enow))
            {
                FastTwoSum(enow, q, out qnew, out hh);
                if (++eindex < elen) enow = e[eindex];
            }
            else
            {
                FastTwoSum(fnow, q, out qnew, out hh);
                if (++findex < flen) fnow = f[findex];
            }
            q = qnew;
            if (hh != 0.0) h[hindex++] = hh;

            while (eindex < elen && findex < flen)
            {
                if ((fnow > enow) == (fnow > -enow))
                {
                    TwoSum(q, enow, out qnew, out hh);
                    if (++eindex < elen) enow = e[eindex];
                }
                else
                {
                    TwoSum(q, fnow, out qnew, out hh);
                    if (++findex < flen) fnow = f[findex];
                }
                q = qnew;
                if (hh != 0.0) h[hindex++] = hh;
            }
        }

        while (eindex < elen)
        {
            TwoSum(q, enow, out qnew, out hh);
            if (++eindex < elen) enow = e[eindex];
            q = qnew;
            if (hh != 0.0) h[hindex++] = hh;
        }
        while (findex < flen)
        {
            TwoSum(q, fnow, out qnew, out hh);
            if (++findex < flen) fnow = f[findex];
            q = qnew;
            if (hh != 0.0) h[hindex++] = hh;
        }
        if (q != 0.0 || hindex == 0) h[hindex++] = q;
        return hindex;
    }

    /// <summary>
    /// scale_expansion_zeroelim: multiplies an expansion by a double, eliminating zero
    /// components. h must not alias e. Returns the length of h.
    /// </summary>
    public static int ScaleExpansionZeroElim(ReadOnlySpan<double> e, int elen, double b, Span<double> h)
    {
        Split(b, out double bhi, out double blo);
        TwoProductPresplit(e[0], b, bhi, blo, out double q, out double hh);
        int hindex = 0;
        if (hh != 0.0) h[hindex++] = hh;
        for (int eindex = 1; eindex < elen; eindex++)
        {
            TwoProductPresplit(e[eindex], b, bhi, blo, out double product1, out double product0);
            TwoSum(q, product0, out double sum, out hh);
            if (hh != 0.0) h[hindex++] = hh;
            FastTwoSum(product1, sum, out q, out hh);
            if (hh != 0.0) h[hindex++] = hh;
        }
        if (q != 0.0 || hindex == 0) h[hindex++] = q;
        return hindex;
    }

    /// <summary>Cheap non-exact estimate: the plain sum of an expansion's components.</summary>
    public static double Estimate(ReadOnlySpan<double> e, int elen)
    {
        double q = e[0];
        for (int i = 1; i < elen; i++)
            q += e[i];
        return q;
    }
}
