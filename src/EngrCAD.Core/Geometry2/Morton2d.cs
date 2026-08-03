namespace EngrCAD.Core.Geometry2;

/// <summary>
/// Morton (Z-order) index arithmetic on a 2D integer grid: the bijection between a cell
/// <c>(x, y)</c> and the single integer whose bits interleave the two coordinates, x on the
/// even bits and y on the odd ones.
///
/// <para><b>One copy, two consumers.</b> This is the interleave <c>PlanarSection</c> already
/// used to Morton-sort projected faces before folding them (locality is what makes a
/// balanced union tree cancel), and it is the index arithmetic
/// <see cref="SpaceFillingCurve"/>'s <see cref="SpaceFillingFamily.ZOrder"/> member walks.
/// The two must agree bit for bit or the same grid would be ordered two ways, so the
/// arithmetic lives here and both ask it rather than restating it.</para>
///
/// <para><b>Sixteen bits per axis.</b> An interleaved pair fills a <c>uint</c> exactly, so
/// coordinates are masked to their low 16 bits; a caller with a larger grid must quantise
/// first (which is what the silhouette sort does).</para>
/// </summary>
public static class Morton2d
{
    /// <summary>Bits carried per axis. Two axes interleaved fill a <c>uint</c>.</summary>
    public const int BitsPerAxis = 16;

    /// <summary>Largest coordinate an axis can carry (2^16 − 1).</summary>
    public const uint MaxCoordinate = (1u << BitsPerAxis) - 1;

    /// <summary>Spreads the low 16 bits of <paramref name="x"/> so every other bit is zero
    /// (the classic "part1by1"). Bits above 16 are dropped.</summary>
    public static uint Interleave(uint x)
    {
        x &= 0x0000FFFF;
        x = (x | (x << 8)) & 0x00FF00FF;
        x = (x | (x << 4)) & 0x0F0F0F0F;
        x = (x | (x << 2)) & 0x33333333;
        x = (x | (x << 1)) & 0x55555555;
        return x;
    }

    /// <summary>Gathers the even bits of <paramref name="x"/> back into the low 16 — the exact
    /// inverse of <see cref="Interleave"/> on its range.</summary>
    public static uint Compact(uint x)
    {
        x &= 0x55555555;
        x = (x | (x >> 1)) & 0x33333333;
        x = (x | (x >> 2)) & 0x0F0F0F0F;
        x = (x | (x >> 4)) & 0x00FF00FF;
        x = (x | (x >> 8)) & 0x0000FFFF;
        return x;
    }

    /// <summary>The Morton code of cell <c>(x, y)</c>: x on the even bits, y on the odd ones.</summary>
    public static uint Encode(uint x, uint y) => Interleave(x) | (Interleave(y) << 1);

    /// <summary>Recovers the cell a Morton code names. Exact inverse of <see cref="Encode"/>.</summary>
    public static void Decode(uint code, out uint x, out uint y)
    {
        x = Compact(code);
        y = Compact(code >> 1);
    }
}
