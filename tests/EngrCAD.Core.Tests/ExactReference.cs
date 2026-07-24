using EngrCAD.Core;
using System.Numerics;

namespace EngrCAD.Core.Tests;

/// <summary>
/// Test-only exact ground truth: every double is decomposed exactly as M·2^E with a
/// BigInteger mantissa, so sums/differences/products of input coordinates are computed
/// with NO rounding at all. The predicates' signs are compared against these.
/// </summary>
internal readonly struct ExactValue
{
    public readonly BigInteger M;
    public readonly int E; // value = M * 2^E

    public ExactValue(BigInteger m, int e)
    {
        M = m;
        E = e;
    }

    public static ExactValue From(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        int exponent = (int)((bits >> 52) & 0x7FF);
        if (exponent == 0x7FF)
            throw new ArgumentException("Non-finite double.");
        long mantissa = bits & 0xF_FFFF_FFFF_FFFF;
        BigInteger m;
        int e;
        if (exponent == 0)
        {
            m = mantissa;
            e = -1074;
        }
        else
        {
            m = mantissa | (1L << 52);
            e = exponent - 1075;
        }
        if (bits < 0)
            m = -m;
        return new ExactValue(m, e);
    }

    public static ExactValue operator *(ExactValue a, ExactValue b) => new(a.M * b.M, a.E + b.E);

    public static ExactValue operator +(ExactValue a, ExactValue b) =>
        a.E == b.E ? new(a.M + b.M, a.E)
        : a.E > b.E ? new((a.M << (a.E - b.E)) + b.M, b.E)
        : new(a.M + (b.M << (b.E - a.E)), a.E);

    public static ExactValue operator -(ExactValue a, ExactValue b) => a + new ExactValue(-b.M, b.E);

    public int Sign => M.Sign;
}

internal static class ExactReference
{
    /// <summary>Exact sign of the orient2d determinant (a-c) × (b-c).</summary>
    public static int Orient2dSign(Vector2d a, Vector2d b, Vector2d c)
    {
        var acx = ExactValue.From(a.X) - ExactValue.From(c.X);
        var acy = ExactValue.From(a.Y) - ExactValue.From(c.Y);
        var bcx = ExactValue.From(b.X) - ExactValue.From(c.X);
        var bcy = ExactValue.From(b.Y) - ExactValue.From(c.Y);
        return (acx * bcy - acy * bcx).Sign;
    }

    /// <summary>Exact sign of the incircle determinant (Shewchuk's lifted form, translated by d).</summary>
    public static int InCircleSign(Vector2d a, Vector2d b, Vector2d c, Vector2d d)
    {
        var adx = ExactValue.From(a.X) - ExactValue.From(d.X);
        var ady = ExactValue.From(a.Y) - ExactValue.From(d.Y);
        var bdx = ExactValue.From(b.X) - ExactValue.From(d.X);
        var bdy = ExactValue.From(b.Y) - ExactValue.From(d.Y);
        var cdx = ExactValue.From(c.X) - ExactValue.From(d.X);
        var cdy = ExactValue.From(c.Y) - ExactValue.From(d.Y);

        var alift = adx * adx + ady * ady;
        var blift = bdx * bdx + bdy * bdy;
        var clift = cdx * cdx + cdy * cdy;

        var det = alift * (bdx * cdy - cdx * bdy)
                + blift * (cdx * ady - adx * cdy)
                + clift * (adx * bdy - bdx * ady);
        return det.Sign;
    }
}
