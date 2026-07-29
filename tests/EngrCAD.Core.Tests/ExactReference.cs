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

    /// <summary>Exact sign of the orient3d determinant det[a-d; b-d; c-d].</summary>
    public static int Orient3dSign(Vector3d a, Vector3d b, Vector3d c, Vector3d d)
    {
        var adx = ExactValue.From(a.X) - ExactValue.From(d.X);
        var ady = ExactValue.From(a.Y) - ExactValue.From(d.Y);
        var adz = ExactValue.From(a.Z) - ExactValue.From(d.Z);
        var bdx = ExactValue.From(b.X) - ExactValue.From(d.X);
        var bdy = ExactValue.From(b.Y) - ExactValue.From(d.Y);
        var bdz = ExactValue.From(b.Z) - ExactValue.From(d.Z);
        var cdx = ExactValue.From(c.X) - ExactValue.From(d.X);
        var cdy = ExactValue.From(c.Y) - ExactValue.From(d.Y);
        var cdz = ExactValue.From(c.Z) - ExactValue.From(d.Z);

        var det = adz * (bdx * cdy - cdx * bdy)
                + bdz * (cdx * ady - adx * cdy)
                + cdz * (adx * bdy - bdx * ady);
        return det.Sign;
    }

    /// <summary>
    /// Exact sign of the insphere determinant (Shewchuk's lifted form, translated by e).
    /// Written as an INDEPENDENT cofactor expansion along the lift column rather than as a
    /// transcription of the production grouping, so agreement between the two is evidence
    /// rather than tautology.
    /// </summary>
    public static int InSphereSign(Vector3d a, Vector3d b, Vector3d c, Vector3d d, Vector3d e)
    {
        var rows = new ExactValue[4, 3];
        var points = new[] { a, b, c, d };
        for (int i = 0; i < 4; i++)
        {
            rows[i, 0] = ExactValue.From(points[i].X) - ExactValue.From(e.X);
            rows[i, 1] = ExactValue.From(points[i].Y) - ExactValue.From(e.Y);
            rows[i, 2] = ExactValue.From(points[i].Z) - ExactValue.From(e.Z);
        }

        var lifts = new ExactValue[4];
        for (int i = 0; i < 4; i++)
            lifts[i] = rows[i, 0] * rows[i, 0] + rows[i, 1] * rows[i, 1] + rows[i, 2] * rows[i, 2];

        // det = sum_i (-1)^(i+4) * lift_i * minor(i, lift column), rows 1-based in the sign.
        var det = new ExactValue(0, 0);
        for (int i = 0; i < 4; i++)
        {
            var others = new List<int>(3);
            for (int j = 0; j < 4; j++)
                if (j != i) others.Add(j);

            var term = lifts[i] * Det3(rows, others[0], others[1], others[2]);
            det = (i + 1 + 4) % 2 == 0 ? det + term : det - term;
        }
        return det.Sign;
    }

    private static ExactValue Det3(ExactValue[,] rows, int p, int q, int r) =>
        rows[p, 0] * (rows[q, 1] * rows[r, 2] - rows[q, 2] * rows[r, 1])
      - rows[p, 1] * (rows[q, 0] * rows[r, 2] - rows[q, 2] * rows[r, 0])
      + rows[p, 2] * (rows[q, 0] * rows[r, 1] - rows[q, 1] * rows[r, 0]);
}
