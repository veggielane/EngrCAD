using System.Collections.Concurrent;
using System.Linq.Expressions;
using EngrCAD.Core;

namespace EngrCAD.Implicit;

// Triply periodic minimal surfaces (TPMS).
//
// THESE ARE NOT DISTANCE FIELDS, and the whole file is about turning them into ones this
// engine can trust. Each surface is a level set F(p) = 0 of a trigonometric polynomial —
// the "nodal approximation" the literature uses, which is not the exact minimal surface but
// is what every implicit lattice in additive manufacturing is built from. F itself is
// neither a distance nor 1-Lipschitz: its gradient magnitude varies over space, so |F| says
// nothing about how far away the surface is until it is divided by something.
//
// What it is divided by is max |grad F|, and that constant is the load-bearing number in
// this file. Dividing by the GLOBAL maximum makes the result 1-Lipschitz, and a 1-Lipschitz
// function that vanishes exactly on the surface is a LOWER BOUND on the true distance
// (|g(p)| = |g(p) - g(nearest surface point)| <= |p - nearest|), which is the engine's
// standing field contract. Dividing by anything SMALLER breaks it: SurfaceNets' block cull,
// the narrow-band octree and the projection target all read |d| as a promise that no surface
// is nearer than that, so an under-sized constant drops geometry SILENTLY — the same failure
// Sdf.LipschitzBound exists to prevent, and the same one the Sdf.Sampled sqrt(3) defect had
// been hiding.
//
// So every constant below is DERIVED and MEASURED — the derivation says what the number IS,
// the measurement says the stored double covers it (TpmsTests re-measures the supremum over
// the fundamental cell, and TpmsDerivationTests checks each closed form against that scan AND
// checks the load-bearing step of each derivation rather than only its answer).
//
//   Schwarz P   grad F = -(sin x, sin y, sin z), so |grad F| <= sqrt(3), attained at
//               (pi/2, pi/2, pi/2) — which is ON the surface.
//   Schwarz D   F = sin x cos(y-z) + cos x sin(y+z) after the product-to-sum collapse;
//               writing u = y-z, v = y+z the Gram quadratic reduces to
//               A^2(c^2-2s^2) + C^2(s^2-2c^2) - 2csAC + 2 over |A|,|C| <= 1, whose maximum
//               over the square is 3 (at s = 0 or c = 0). So sqrt(3), attained at the origin.
//   Gyroid      at x = t, y = -t, z = -t two components are identically 1 and the third is
//               cos 2t, so the maximum 3 is attained at t = 0: sqrt(3). (This is the constant
//               Sdf.Gyroid has always used.)
//   Neovius     dF/dx = -sin x (3 + 4 cos y cos z), so |dF/dx| <= 7, and at (pi/2, 0, 0) the
//               other two partials vanish: exactly 7.
//   I-WP        dF/dz = 2 sin z (2 cos z - cos x - cos y); with x = y = 0 that is
//               4 sin z (1 - cos z), maximized at cos z = -1/2 giving 3 sqrt(3), and the
//               other two partials vanish there.
//
// THE DIAGONAL LEMMA covers the last three, and it is ONE derivation rather than three.
// Every polynomial here is CYCLIC in (x, y, z), so the diagonal x = y = z = t is invariant
// under the cycle and the three partials are equal on it — which makes |grad F| there simply
// |F_diag'(t)| / sqrt(3), a ONE-VARIABLE problem. For the shape both Lidinoid and Split P
// have, a*sum(sin2x sin z cos y) + b*sum(cos2x cos2y) + e*sum(cos2x), the diagonal restriction
// is F_diag = (3a/2) sin^2 2t + 3b cos^2 2t + 3e cos 2t, so
//
//      F_diag' = 6 sin2t [ (a - 2b) cos2t - e ]   and   |grad F| = 2 sqrt(3) |s| |A c + E|
//
// with s = sin2t, c = cos2t, A = a - 2b, E = -e. Maximizing (1 - c^2)(Ac + E)^2 gives
// 2A c^2 + E c - A = 0, i.e. c* = (-E + sqrt(E^2 + 8A^2)) / (4A).
//
//   Lidinoid    a = 1/2, b = -1/2, e = 0, so A = 3/2 and E = 0: c* = 1/sqrt(2), and
//               2 sqrt(3) * (1/sqrt(2)) * (3/(2 sqrt(2))) = 3 sqrt(3) / 2 — EXACTLY the
//               constant the dense scan had been landing on with no derivation attempted.
//   Split P     a = 1.1, b = -0.2, e = -0.4, so A = 3/2 and E = 2/5:
//               c* = (sqrt(454) - 2) / 30, and the supremum is
//               2 sqrt(3) sqrt(1 - c*^2) (3c*/2 + 2/5) = 3.620073899187... — a quadratic
//               surd, so the constant is closed form after all.
//
// FISCHER-KOCH S is the one member whose maximum is NOT on the diagonal (there it is only
// sqrt(3), a local maximum), so it needs its own invariant family, and the family the scan
// points at is (x, y, z) = (t + 3pi/2, t, pi/4). On it cos2z = 0 and sin2z = 1, F vanishes
// identically (so the maximum sits ON the surface, as Schwarz P's does), dF/dy = -dF/dx, and
// substituting s = sin t collapses |grad F|^2 to the degree-6 polynomial
//
//      G(u) = 4u^6 + 8 sqrt(2) u^5 - 4u^4 - 12 sqrt(2) u^3 - 3u^2 + 4 sqrt(2) u + 5.
//
// The substitution v = sqrt(2) u clears the radicals from its derivative:
// G'(u) = sqrt(2) (3v^5 + 10v^4 - 4v^3 - 18v^2 - 3v + 4) = sqrt(2) (v + 1)(3v^4 + 7v^3 -
// 11v^2 - 7v + 4). So the maximizer is a root of an INTEGER QUARTIC — solvable in radicals,
// which makes the constant an explicit algebraic number of degree at most 4 over Q(sqrt 2)
// rather than the "no closed form found" the file used to record. The root in (0, 1) is
// v* = 0.3969844262244973..., and sqrt(G(v*/sqrt 2)) = 2.4439726372930344, which the dense
// global scan reproduces to its last digit.
//
// THE STORED CONSTANTS ARE UNCHANGED by any of that, deliberately: they already round the
// supremum UP at the sixth significant figure, which is the safe direction and is worth about
// three parts per million of wall thickness, while re-storing them would move every rendered
// lattice for nothing. What the derivations buy is that the numbers are now known rather than
// merely measured.
//
// THE COST OF THE CONSTANT IS WALL THICKNESS, and it is worth stating plainly because it is
// what a user notices first. The sheet |F| <= bound*omega*t/2 has local half-thickness
// (bound / |grad F|) * t/2, so the requested thickness is a LOWER bound on the wall and the
// excess is exactly the factor by which the local gradient falls short of the global
// maximum. Measured on the level set (median / worst): gyroid 1.15 / 1.22, Schwarz D
// 1.19 / 1.22, Schwarz P 1.36 / 1.73, I-WP 1.22 / 1.59, Fischer-Koch S 1.41 / 2.02,
// Split P 1.59 / 2.83, Neovius 2.69 / 6.99, Lidinoid 2.18 / 56.9 (its level set passes
// through a near-critical point where |grad F| falls to 0.046). Which is why VOLUME
// FRACTION, not thickness, is the parameter this file offers as the engineering one.

/// <summary>
/// The triply periodic minimal surfaces this engine can build. Each is the zero level set of
/// a trigonometric polynomial — the standard nodal approximation, not the exact minimal
/// surface — and each comes in two genuinely different solids, a SHEET (the surface
/// thickened) and a SOLID/network (the material on one side of a level set). See
/// <see cref="Sdf.TpmsSheet"/> and <see cref="Sdf.TpmsSolid"/>.
/// </summary>
public enum TpmsKind
{
    /// <summary>Schwarz P ("primitive"): <c>cos x + cos y + cos z</c>.</summary>
    SchwarzP,

    /// <summary>Schwarz D ("diamond").</summary>
    SchwarzD,

    /// <summary>The gyroid — Schoen's surface, and the additive-manufacturing default.</summary>
    Gyroid,

    /// <summary>Neovius: <c>3(cos x + cos y + cos z) + 4 cos x cos y cos z</c>.</summary>
    Neovius,

    /// <summary>I-WP ("I-graph and wrapped package"). Its two labyrinths are NOT congruent,
    /// so unlike the four above its zero level does not split space evenly — measured
    /// 46.9%.</summary>
    IwP,

    /// <summary>Lidinoid — a member of the gyroid's associate family. Its zero level
    /// encloses a measured 38.5%.</summary>
    Lidinoid,

    /// <summary>Fischer–Koch S.</summary>
    FischerKochS,

    /// <summary>Split P. Its zero level encloses a measured 51.0%.</summary>
    SplitP,
}

/// <summary>
/// What a volume-fraction request produced: the field, the fraction that was asked for, the
/// fraction that was MEASURED on the result, and the parameter that got there. Reporting the
/// achieved value rather than echoing the request is the <c>BiArcFit.MaxDeviation</c>
/// convention — a lattice's volume fraction is a solved quantity, and the solve is over a
/// sampled measure of the unit cell, so it lands near the request rather than on it.
/// </summary>
/// <param name="Field">The lattice field, ready to intersect with a solid.</param>
/// <param name="RequestedVolumeFraction">What the caller asked for.</param>
/// <param name="VolumeFraction">What the result measures, on a grid independent of the one
/// the parameter was solved on.</param>
/// <param name="Parameter">The parameter that produced it: a sheet thickness, a level, or a
/// strut diameter, depending on which factory returned this.</param>
public sealed record LatticeFit(
    Sdf Field, double RequestedVolumeFraction, double VolumeFraction, double Parameter);

/// <summary>
/// The TPMS catalogue's metadata and the volume-fraction solves. The fields themselves come
/// from <see cref="Sdf.TpmsSheet"/> / <see cref="Sdf.TpmsSolid"/>.
/// </summary>
public static class Tpms
{
    /// <summary>
    /// <c>max |grad F|</c> over all of space, in the surface's own normalized coordinates
    /// (x = 2*pi*X / cellSize). This is the constant every field of that kind divides by, and
    /// it is what makes the result 1-Lipschitz; see the file remarks for the derivations and
    /// for what a loose constant costs.
    /// </summary>
    public static double GradientBound(TpmsKind kind) => TpmsSurface.For(kind).GradientBound;

    /// <summary>
    /// The fraction of space the SOLID <c>F &lt;= level</c> occupies — measured on a sampled
    /// unit cell rather than quoted, so the answer is this implementation's answer. Level 0
    /// splits space exactly evenly for every kind whose polynomial has an antisymmetry
    /// (Schwarz P, Schwarz D, the gyroid, Neovius, Fischer–Koch S) and measurably does not
    /// for the other three.
    /// </summary>
    public static double SolidVolumeFraction(TpmsKind kind, double level) =>
        TpmsSurface.For(kind).Table(QuantileTable.CheckResolution).FractionAtOrBelow(level);

    /// <summary>
    /// The fraction of space the SHEET of the given thickness occupies, with the thickness
    /// stated as a ratio to the cell size (the quantity is scale-free). Measured, like
    /// <see cref="SolidVolumeFraction"/>.
    /// </summary>
    public static double SheetVolumeFraction(TpmsKind kind, double thicknessPerCell)
    {
        var surface = TpmsSurface.For(kind);
        return surface.Table(QuantileTable.CheckResolution)
            .FractionOfAbsAtOrBelow(surface.SheetLevel(thicknessPerCell));
    }

    /// <summary>
    /// The sheet of the given kind whose material occupies <paramref name="volumeFraction"/>
    /// of space — the parameter an engineer actually specifies, where
    /// <see cref="Sdf.TpmsSheet"/>'s thickness is only a lower bound on the wall (see the
    /// remarks on <see cref="Sdf.TpmsSheet"/>). The thickness is solved as a quantile of |F|
    /// over a sampled unit cell and the achieved fraction is then re-measured on a finer,
    /// disjoint grid.
    /// </summary>
    public static LatticeFit SheetForVolumeFraction(
        TpmsKind kind, double cellSize, double volumeFraction)
    {
        RequireFraction(volumeFraction);
        var surface = TpmsSurface.For(kind);
        double level = surface.Table(QuantileTable.FitResolution).AbsQuantile(volumeFraction);
        double thickness = surface.ThicknessForLevel(level) * cellSize;
        return new LatticeFit(
            Sdf.TpmsSheet(kind, cellSize, thickness),
            volumeFraction,
            surface.Table(QuantileTable.CheckResolution).FractionOfAbsAtOrBelow(level),
            thickness);
    }

    /// <summary>
    /// The solid (network) of the given kind whose material occupies
    /// <paramref name="volumeFraction"/> of space: the level is solved as a quantile of F
    /// over a sampled unit cell. See <see cref="SheetForVolumeFraction"/> for the estimator.
    /// </summary>
    public static LatticeFit SolidForVolumeFraction(
        TpmsKind kind, double cellSize, double volumeFraction)
    {
        RequireFraction(volumeFraction);
        var surface = TpmsSurface.For(kind);
        double level = surface.Table(QuantileTable.FitResolution).Quantile(volumeFraction);
        return new LatticeFit(
            Sdf.TpmsSolid(kind, cellSize, level),
            volumeFraction,
            surface.Table(QuantileTable.CheckResolution).FractionAtOrBelow(level),
            level);
    }

    /// <summary>
    /// A GRADED sheet stated as a volume fraction rather than as a thickness — the parameter an
    /// engineer specifies, varying over space. The caller's fraction grading is pushed through
    /// the same measured cell distribution the uniform solve uses, as a piecewise-linear ladder
    /// so the composed Lipschitz constant is exact and the per-query cost is a table lookup
    /// rather than a bisection.
    /// <para>
    /// The achieved fraction is NOT reported here and that is deliberate: a graded field is not
    /// periodic, so "sample one cell" — the estimator both uniform solves rest on — has nothing
    /// to sample. What the grading states is the fraction the LOCAL cell would have.
    /// </para>
    /// </summary>
    public static Sdf GradedSheetForVolumeFraction(
        TpmsKind kind, double cellSize, LatticeGrading volumeFraction)
    {
        ArgumentNullException.ThrowIfNull(volumeFraction);
        RequireFraction(volumeFraction.Minimum);
        RequireFraction(volumeFraction.Maximum);
        var surface = TpmsSurface.For(kind);
        var table = surface.Table(QuantileTable.FitResolution);
        return Sdf.TpmsSheet(kind, cellSize,
            volumeFraction.Through(f => surface.ThicknessForLevel(table.AbsQuantile(f)) * cellSize));
    }

    /// <summary>
    /// The network twin of <see cref="GradedSheetForVolumeFraction"/>: the LEVEL graded so the
    /// local cell carries the stated fraction.
    /// </summary>
    public static Sdf GradedSolidForVolumeFraction(
        TpmsKind kind, double cellSize, LatticeGrading volumeFraction)
    {
        ArgumentNullException.ThrowIfNull(volumeFraction);
        RequireFraction(volumeFraction.Minimum);
        RequireFraction(volumeFraction.Maximum);
        var surface = TpmsSurface.For(kind);
        var table = surface.Table(QuantileTable.FitResolution);
        return Sdf.TpmsSolid(kind, cellSize, volumeFraction.Through(table.Quantile));
    }

    /// <summary>
    /// The wall a sheet of the given nominal thickness actually has — the number
    /// <see cref="Sdf.TpmsSheet(TpmsKind, double, double)"/>'s remarks quote as a table, made
    /// queryable and measured on THIS implementation's own level set.
    /// <para>
    /// <b>The excess is inherent, not a defect to be tuned away.</b> A sheet is the band
    /// <c>|F| ≤ L</c>, whose perpendicular width at a surface point is <c>2L/|grad F|</c>, so
    /// the wall varies with the gradient wherever the gradient varies — which it does, for every
    /// one of these polynomials. Normalizing by the LOCAL gradient instead of the global maximum
    /// would make the wall first-order uniform and would cost the 1-Lipschitz contract that
    /// makes the field a usable distance at all (Lidinoid's level set passes a near-critical
    /// point where |grad F| falls to 0.046, so the local form is 20-odd times steeper there).
    /// So the wall is REPORTED rather than corrected, and
    /// <see cref="SheetForWallThickness"/> solves the nominal thickness that lands a stated
    /// median wall.
    /// </para>
    /// </summary>
    public static SheetWall WallThickness(TpmsKind kind, double cellSize, double thickness)
    {
        if (!(cellSize > 0))
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "The cell size must be positive.");
        if (thickness < 0)
            throw new ArgumentOutOfRangeException(nameof(thickness), thickness, "The thickness must be non-negative.");
        var factors = TpmsSurface.For(kind).WallFactors();
        return new SheetWall(
            thickness, thickness * factors.Minimum, thickness * factors.Median, thickness * factors.Maximum);
    }

    /// <summary>
    /// The sheet whose MEDIAN wall is <paramref name="wall"/> — the inverse of
    /// <see cref="WallThickness"/>, and the call to reach for when a print process has a minimum
    /// feature size in mind. The relation is exactly linear in the nominal thickness (the excess
    /// factor is a property of the surface, not of the thickness), so the solve is one division
    /// and the reported wall is what the same measurement gives back.
    /// </summary>
    public static (Sdf Field, SheetWall Wall) SheetForWallThickness(
        TpmsKind kind, double cellSize, double wall)
    {
        if (!(wall > 0))
            throw new ArgumentOutOfRangeException(nameof(wall), wall, "The wall thickness must be positive.");
        double thickness = wall / TpmsSurface.For(kind).WallFactors().Median;
        return (Sdf.TpmsSheet(kind, cellSize, thickness), WallThickness(kind, cellSize, thickness));
    }

    private static void RequireFraction(double fraction)
    {
        if (!(fraction > 0 && fraction < 1))
            throw new ArgumentOutOfRangeException(
                nameof(fraction), fraction, "A volume fraction must lie strictly between 0 and 1.");
    }
}

/// <summary>
/// What a TPMS sheet's wall actually measures: the nominal thickness asked for and the
/// distribution of the wall it produces, over the surface. See <see cref="Tpms.WallThickness"/>
/// for why they differ and why that is structural.
/// </summary>
/// <param name="Nominal">The thickness the field was built with — a guaranteed MINIMUM wall,
/// which is why <see cref="Minimum"/> equals it to the measurement's own accuracy.</param>
/// <param name="Minimum">The thinnest wall on the surface.</param>
/// <param name="Median">The wall half the surface is thinner than — the figure to compare
/// against a process's feature size.</param>
/// <param name="Maximum">The thickest wall, as the same first-order relation. It is a genuine
/// UPPER bound (measured against a direct march of the field on all eight surfaces) and it
/// over-states where the gradient nearly vanishes, because the band there is no longer thin
/// against the surface's own curvature — Neovius and Lidinoid are the two measurably affected,
/// reading 4.5 and 3.3 against a marched 0.38. Read it as "how non-uniform is this surface"
/// rather than as a length a caller can plan around.</param>
public readonly record struct SheetWall(double Nominal, double Minimum, double Median, double Maximum)
{
    /// <summary>How much thicker the typical wall is than the nominal thickness.</summary>
    public double MedianExcess => Nominal > 0 ? Median / Nominal : 1;

    /// <summary>How much thicker the worst wall is than the nominal thickness.</summary>
    public double WorstExcess => Nominal > 0 ? Maximum / Nominal : 1;
}

/// <summary>
/// One surface's trigonometric polynomial, held as a term list rather than as code.
/// <para>
/// <b>The term list is the single source of truth for three things at once</b> — the scalar
/// <see cref="Value"/>, the compiled expression (<see cref="Build"/>) and the gradient the
/// tests measure — which is what makes the compiled form bit-identical <em>by construction</em>
/// rather than by testing, exactly as every other node in this engine achieves it. Eight
/// hand-written formula pairs would be sixteen copies free to drift.
/// </para>
/// <para>
/// Each term is a coefficient times a product of factors, and each factor is one entry of a
/// twelve-slot table of sines and cosines of x, y, z and their doubles. Evaluation multiplies
/// the factors in listed order and applies the coefficient LAST, skipping it when it is
/// exactly 1 — which is what keeps <see cref="Sdf.Gyroid"/> bit-for-bit the field it has
/// always been (its three terms have unit coefficients, so the products are literally
/// <c>sin x * cos y</c>), and that identity is asserted against a transcription of the old
/// closed form.
/// </para>
/// </summary>
internal sealed class TpmsSurface
{
    // Slot layout for the trig table.
    private const int SinX = 0, CosX = 1, SinY = 2, CosY = 3, SinZ = 4, CosZ = 5;
    private const int Sin2X = 6, Cos2X = 7, Sin2Y = 8, Cos2Y = 9, Sin2Z = 10, Cos2Z = 11;
    private const int SlotCount = 12;

    private readonly double[] _coefficients;
    private readonly int[][] _factors;
    private readonly int[] _usedSlots;

    /// <summary>See the file remarks: derived where a derivation exists, measured in every
    /// case, and never below the true supremum.</summary>
    public double GradientBound { get; }

    public TpmsKind Kind { get; }

    private TpmsSurface(TpmsKind kind, double gradientBound, (double, int[])[] terms)
    {
        Kind = kind;
        GradientBound = gradientBound;
        _coefficients = new double[terms.Length];
        _factors = new int[terms.Length][];
        var used = new SortedSet<int>();
        for (int i = 0; i < terms.Length; i++)
        {
            (_coefficients[i], _factors[i]) = terms[i];
            foreach (int slot in _factors[i])
                used.Add(slot);
        }
        _usedSlots = [.. used];
    }

    public static TpmsSurface For(TpmsKind kind) => kind switch
    {
        TpmsKind.SchwarzP => SchwarzP,
        TpmsKind.SchwarzD => SchwarzD,
        TpmsKind.Gyroid => Gyroid,
        TpmsKind.Neovius => Neovius,
        TpmsKind.IwP => IwP,
        TpmsKind.Lidinoid => Lidinoid,
        TpmsKind.FischerKochS => FischerKochS,
        TpmsKind.SplitP => SplitP,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown TPMS kind."),
    };

    private static readonly TpmsSurface SchwarzP = new(TpmsKind.SchwarzP, Math.Sqrt(3),
    [
        (1, [CosX]), (1, [CosY]), (1, [CosZ]),
    ]);

    private static readonly TpmsSurface SchwarzD = new(TpmsKind.SchwarzD, Math.Sqrt(3),
    [
        (1, [SinX, SinY, SinZ]), (1, [SinX, CosY, CosZ]),
        (1, [CosX, SinY, CosZ]), (1, [CosX, CosY, SinZ]),
    ]);

    private static readonly TpmsSurface Gyroid = new(TpmsKind.Gyroid, Math.Sqrt(3),
    [
        (1, [SinX, CosY]), (1, [SinY, CosZ]), (1, [SinZ, CosX]),
    ]);

    private static readonly TpmsSurface Neovius = new(TpmsKind.Neovius, 7,
    [
        (3, [CosX]), (3, [CosY]), (3, [CosZ]), (4, [CosX, CosY, CosZ]),
    ]);

    private static readonly TpmsSurface IwP = new(TpmsKind.IwP, 3 * Math.Sqrt(3),
    [
        (2, [CosX, CosY]), (2, [CosY, CosZ]), (2, [CosZ, CosX]),
        (-1, [Cos2X]), (-1, [Cos2Y]), (-1, [Cos2Z]),
    ]);

    // 3 sqrt(3) / 2 exactly, by the diagonal lemma at A = 3/2, E = 0 (see the file remarks).
    private static readonly TpmsSurface Lidinoid = new(TpmsKind.Lidinoid, 1.5 * Math.Sqrt(3),
    [
        (0.5, [Sin2X, CosY, SinZ]), (0.5, [Sin2Y, CosZ, SinX]), (0.5, [Sin2Z, CosX, SinY]),
        (-0.5, [Cos2X, Cos2Y]), (-0.5, [Cos2Y, Cos2Z]), (-0.5, [Cos2Z, Cos2X]),
        (0.15, []),
    ]);

    // sqrt(G(v*/sqrt 2)) = 2.4439726372930344 with v* the root in (0, 1) of the integer
    // quartic 3v^4 + 7v^3 - 11v^2 - 7v + 4 (see the file remarks); stored rounded UP at the
    // sixth figure, which is the safe direction.
    private static readonly TpmsSurface FischerKochS = new(TpmsKind.FischerKochS, 2.44398,
    [
        (1, [Cos2X, SinY, CosZ]), (1, [Cos2Y, SinZ, CosX]), (1, [Cos2Z, SinX, CosY]),
    ]);

    // The diagonal lemma at A = 3/2, E = 2/5: c* = (sqrt(454) - 2)/30 and the supremum is
    // 2 sqrt(3) sqrt(1 - c*^2)(3c*/2 + 2/5) = 3.620073899187; stored rounded UP at the sixth
    // figure (see the file remarks).
    private static readonly TpmsSurface SplitP = new(TpmsKind.SplitP, 3.62008,
    [
        (1.1, [Sin2X, SinZ, CosY]), (1.1, [Sin2Y, SinX, CosZ]), (1.1, [Sin2Z, SinY, CosX]),
        (-0.2, [Cos2X, Cos2Y]), (-0.2, [Cos2Y, Cos2Z]), (-0.2, [Cos2Z, Cos2X]),
        (-0.4, [Cos2X]), (-0.4, [Cos2Y]), (-0.4, [Cos2Z]),
    ]);

    /// <summary>F at a point in the surface's own normalized coordinates.</summary>
    public double Value(double x, double y, double z)
    {
        Span<double> t = stackalloc double[SlotCount];
        foreach (int slot in _usedSlots)
            t[slot] = Slot(slot, x, y, z);

        double sum = Term(0, t);
        for (int i = 1; i < _coefficients.Length; i++)
            sum += Term(i, t);
        return sum;
    }

    private double Term(int index, ReadOnlySpan<double> t)
    {
        var factors = _factors[index];
        double coefficient = _coefficients[index];
        if (factors.Length == 0)
            return coefficient;
        double v = t[factors[0]];
        for (int j = 1; j < factors.Length; j++)
            v *= t[factors[j]];
        // Applied last, and skipped at exactly 1 — see the class remarks: this is what keeps
        // the gyroid's terms the bare products they have always been.
        return coefficient == 1 ? v : v * coefficient;
    }

    private static double Slot(int slot, double x, double y, double z) => slot switch
    {
        SinX => Math.Sin(x),
        CosX => Math.Cos(x),
        SinY => Math.Sin(y),
        CosY => Math.Cos(y),
        SinZ => Math.Sin(z),
        CosZ => Math.Cos(z),
        Sin2X => Math.Sin(2 * x),
        Cos2X => Math.Cos(2 * x),
        Sin2Y => Math.Sin(2 * y),
        Cos2Y => Math.Cos(2 * y),
        Sin2Z => Math.Sin(2 * z),
        _ => Math.Cos(2 * z),
    };

    /// <summary>The compiled form: the same term list, the same order, the same skipped unit
    /// coefficients — so it is bit-identical to <see cref="Value"/> by construction.</summary>
    public Expression Build(SdfExpression b, Expression x, Expression y, Expression z)
    {
        var slots = new Expression[SlotCount];
        foreach (int slot in _usedSlots)
            slots[slot] = b.Let(SlotExpression(slot, x, y, z));

        Expression sum = TermExpression(0, slots);
        for (int i = 1; i < _coefficients.Length; i++)
            sum = Expression.Add(sum, TermExpression(i, slots));
        return sum;
    }

    private Expression TermExpression(int index, Expression[] slots)
    {
        var factors = _factors[index];
        double coefficient = _coefficients[index];
        if (factors.Length == 0)
            return SdfExpression.Const(coefficient);
        Expression v = slots[factors[0]];
        for (int j = 1; j < factors.Length; j++)
            v = Expression.Multiply(v, slots[factors[j]]);
        return coefficient == 1 ? v : Expression.Multiply(v, SdfExpression.Const(coefficient));
    }

    private static Expression SlotExpression(int slot, Expression x, Expression y, Expression z)
    {
        var two = SdfExpression.Const(2);
        return slot switch
        {
            SinX => SdfExpression.Sin(x),
            CosX => SdfExpression.Cos(x),
            SinY => SdfExpression.Sin(y),
            CosY => SdfExpression.Cos(y),
            SinZ => SdfExpression.Sin(z),
            CosZ => SdfExpression.Cos(z),
            Sin2X => SdfExpression.Sin(Expression.Multiply(two, x)),
            Cos2X => SdfExpression.Cos(Expression.Multiply(two, x)),
            Sin2Y => SdfExpression.Sin(Expression.Multiply(two, y)),
            Cos2Y => SdfExpression.Cos(Expression.Multiply(two, y)),
            Sin2Z => SdfExpression.Sin(Expression.Multiply(two, z)),
            _ => SdfExpression.Cos(Expression.Multiply(two, z)),
        };
    }

    // ---- the measured level/thickness/fraction relations ----

    /// <summary>The level of |F| that a sheet of the given thickness-to-cell ratio reaches:
    /// the field is <c>|F| / (bound * omega) - t/2</c>, so the sheet is
    /// <c>|F| &lt;= bound * omega * t / 2 = pi * bound * (t / cell)</c>.</summary>
    public double SheetLevel(double thicknessPerCell) =>
        Math.PI * GradientBound * thicknessPerCell;

    /// <summary>The inverse of <see cref="SheetLevel"/>.</summary>
    public double ThicknessForLevel(double level) => level / (Math.PI * GradientBound);

    /// <summary>The distribution of F over one period, sampled at cell CENTRES and cached —
    /// see <see cref="QuantileTable"/> for why the samples are compressed and why there are
    /// two resolutions.</summary>
    public QuantileTable Table(int resolution) =>
        QuantileTable.For(this, resolution, Sample);

    // ---- the wall the sheet actually has ----

    /// <summary>
    /// The local wall factor <c>bound / |grad F|</c> over the ZERO level set: min, median and
    /// max, cached per surface. The sheet's perpendicular half-width at a surface point is
    /// <c>(bound/|grad F|)·t/2</c>, so multiplying a nominal thickness by these gives the wall
    /// — a FIRST-ORDER relation, exact in the thin-wall limit, which is the regime a sheet
    /// lattice is used in and is verified against a direct bisection of the field in the tests.
    /// <para>
    /// <b>The samples are weighted by |grad F|, and that is not a detail.</b> A uniform grid
    /// samples SPACE, and the band <c>|F| ≤ δ</c> is thick exactly where the gradient is small,
    /// so an unweighted median would be pulled toward the thick-wall regions by the very
    /// quantity being measured. Weighting by the gradient makes the measure uniform over the
    /// SURFACE, which is what "the median wall of this sheet" means.
    /// </para>
    /// </summary>
    public (double Minimum, double Median, double Maximum) WallFactors() =>
        WallFactorCache.GetOrAdd(this, static s => s.MeasureWallFactors());

    private static readonly ConcurrentDictionary<TpmsSurface, (double, double, double)>
        WallFactorCache = new();

    private (double, double, double) MeasureWallFactors()
    {
        const int Resolution = 96;
        const double Step = 1e-5;
        double h = 2 * Math.PI / Resolution;
        // The band's half-width in F: a cell diagonal's worth of gradient, so every surface
        // crossing lands in it and nothing far from the surface does.
        double band = 2 * h * GradientBound;

        var samples = new List<(double Factor, double Weight)>();
        for (int i = 0; i < Resolution; i++)
        {
            double x = h * (i + 0.5);
            for (int j = 0; j < Resolution; j++)
            {
                double y = h * (j + 0.5);
                for (int k = 0; k < Resolution; k++)
                {
                    double z = h * (k + 0.5);
                    if (Math.Abs(Value(x, y, z)) > band)
                        continue;
                    double gx = Value(x + Step, y, z) - Value(x - Step, y, z);
                    double gy = Value(x, y + Step, z) - Value(x, y - Step, z);
                    double gz = Value(x, y, z + Step) - Value(x, y, z - Step);
                    double g = Math.Sqrt(gx * gx + gy * gy + gz * gz) / (2 * Step);
                    if (g <= 0)
                        continue;
                    samples.Add((GradientBound / g, g));
                }
            }
        }

        if (samples.Count == 0)
            return (1, 1, 1);
        samples.Sort((p, q) => p.Factor.CompareTo(q.Factor));
        double total = 0;
        foreach (var (_, w) in samples)
            total += w;

        double running = 0;
        double median = samples[^1].Factor;
        foreach (var (factor, weight) in samples)
        {
            running += weight;
            if (running >= total / 2)
            {
                median = factor;
                break;
            }
        }
        return (samples[0].Factor, median, samples[^1].Factor);
    }

    private double[] Sample(int resolution)
    {
        double step = 2 * Math.PI / resolution;
        var values = new double[resolution * resolution * resolution];
        int at = 0;
        for (int i = 0; i < resolution; i++)
        {
            double x = step * (i + 0.5);
            for (int j = 0; j < resolution; j++)
            {
                double y = step * (j + 0.5);
                for (int k = 0; k < resolution; k++)
                    values[at++] = Value(x, y, step * (k + 0.5));
            }
        }
        return values;
    }
}

/// <summary>
/// The SHEET: the surface thickened into a wall. See <see cref="Sdf.TpmsSheet"/>.
/// </summary>
internal sealed class TpmsSheetSdf : Sdf
{
    private readonly TpmsSurface _surface;
    private readonly double _omega;
    private readonly double _thickness;

    public TpmsSheetSdf(TpmsSurface surface, double cellSize, double thickness)
    {
        _surface = surface;
        _omega = 2 * Math.PI / cellSize;
        _thickness = thickness;
    }

    public override double Evaluate(in Vector3d p)
    {
        double g = _surface.Value(p.X * _omega, p.Y * _omega, p.Z * _omega);
        return Math.Abs(g) / (_surface.GradientBound * _omega) - _thickness / 2;
    }

    public override Aabb Bounds => InfiniteBounds;

    /// <summary>
    /// Compiles even though it does not vectorize, and the asymmetry is the point: an
    /// expression tree calls <see cref="Math.Sin"/> itself, so the compiled form is the same
    /// function on the same arguments and is bit-identical — whereas a SIMD kernel would have
    /// to substitute a vector sine, and .NET's vector transcendentals differ from
    /// <see cref="Math"/>'s by an ulp on tens of thousands of inputs in 200 000 (measured).
    /// Compilation and vectorization are not the same trade.
    /// </summary>
    internal override Expression BuildExpression(SdfExpression b)
    {
        var w = SdfExpression.Const(_omega);
        var x = b.Let(Expression.Multiply(b.X, w));
        var y = b.Let(Expression.Multiply(b.Y, w));
        var z = b.Let(Expression.Multiply(b.Z, w));
        var g = _surface.Build(b, x, y, z);
        return Expression.Subtract(
            Expression.Divide(
                SdfExpression.Abs(g), SdfExpression.Const(_surface.GradientBound * _omega)),
            SdfExpression.Const(_thickness / 2));
    }
}

/// <summary>
/// The SOLID (network): the material on one side of a level set. See
/// <see cref="Sdf.TpmsSolid"/>.
/// </summary>
internal sealed class TpmsSolidSdf : Sdf
{
    private readonly TpmsSurface _surface;
    private readonly double _omega;
    private readonly double _level;

    public TpmsSolidSdf(TpmsSurface surface, double cellSize, double level)
    {
        _surface = surface;
        _omega = 2 * Math.PI / cellSize;
        _level = level;
    }

    public override double Evaluate(in Vector3d p)
    {
        double g = _surface.Value(p.X * _omega, p.Y * _omega, p.Z * _omega);
        return (g - _level) / (_surface.GradientBound * _omega);
    }

    public override Aabb Bounds => InfiniteBounds;

    /// <inheritdoc cref="TpmsSheetSdf.BuildExpression"/>
    internal override Expression BuildExpression(SdfExpression b)
    {
        var w = SdfExpression.Const(_omega);
        var x = b.Let(Expression.Multiply(b.X, w));
        var y = b.Let(Expression.Multiply(b.Y, w));
        var z = b.Let(Expression.Multiply(b.Z, w));
        var g = _surface.Build(b, x, y, z);
        return Expression.Divide(
            Expression.Subtract(g, SdfExpression.Const(_level)),
            SdfExpression.Const(_surface.GradientBound * _omega));
    }
}
