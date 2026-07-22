namespace EngrCAD.Core;

/// <summary>
/// Central tolerance policy for the kernel. All floating-point comparisons in geometric
/// code must go through a <see cref="Tolerance"/> — never compare doubles with ==.
/// </summary>
/// <remarks>
/// <see cref="Linear"/> is in model units; <see cref="Angular"/> is in radians.
/// Pass a <see cref="Tolerance"/> explicitly through algorithms rather than reading
/// <see cref="Default"/> deep inside them, so callers can tighten or loosen precision.
/// </remarks>
public readonly struct Tolerance
{
    public double Linear { get; }
    public double Angular { get; }

    public static readonly Tolerance Default = new(1e-9, 1e-9);

    public Tolerance(double linear, double angular)
    {
        if (linear <= 0) throw new ArgumentOutOfRangeException(nameof(linear), "Tolerance must be positive.");
        if (angular <= 0) throw new ArgumentOutOfRangeException(nameof(angular), "Tolerance must be positive.");
        Linear = linear;
        Angular = angular;
    }

    public bool IsZero(double value) => Math.Abs(value) <= Linear;

    public bool AreEqual(double a, double b) => Math.Abs(a - b) <= Linear;

    /// <summary>True if <paramref name="a"/> is strictly less than <paramref name="b"/> beyond tolerance.</summary>
    public bool IsLess(double a, double b) => b - a > Linear;

    /// <summary>True if <paramref name="a"/> is strictly greater than <paramref name="b"/> beyond tolerance.</summary>
    public bool IsGreater(double a, double b) => a - b > Linear;

    /// <summary>-1, 0, or 1 with equality decided by <see cref="Linear"/>.</summary>
    public int Compare(double a, double b) => AreEqual(a, b) ? 0 : (a < b ? -1 : 1);

    public bool IsZeroAngle(double radians) => Math.Abs(radians) <= Angular;

    public bool AreEqualAngles(double a, double b) => Math.Abs(a - b) <= Angular;
}
