using System.Collections.Concurrent;

namespace EngrCAD.Implicit;

// How a lattice's VOLUME FRACTION is measured.
//
// It is measured rather than derived, and for both families the reason is the same: the
// quantity has no closed form worth quoting. A TPMS level set is a trigonometric variety, and
// a strut lattice's members overlap at every node so the sum of the capsule volumes
// over-counts. So one period is sampled at cell CENTRES and the empirical distribution of the
// field is what answers both directions of the question — "what fraction does this parameter
// give" and "what parameter gives this fraction".
//
// The samples are then COMPRESSED to a quantile table and thrown away. A million doubles per
// kind held for the life of the process to answer a question to three decimal places is the
// wrong trade; 4097 entries resolve the distribution to 0.024% of a fraction, two orders finer
// than the estimator's own accuracy, and cost 32 KB. That is what makes the table cacheable,
// and caching is what makes the second call free — a fit and its verification would otherwise
// sample the field twice over.
//
// TWO RESOLUTIONS, DELIBERATELY COPRIME. The parameter is solved on one grid and the achieved
// fraction re-measured on the other; 64 and 81 share no factor, so with centre sampling the
// two share no sample and the reported fraction is an independent measurement rather than a
// restatement of the solve. What the gap between them measures is the estimator's real
// uncertainty, which is why the reported value is the one from the finer grid.

/// <summary>
/// The empirical distribution of a periodic field over one cell, compressed to a quantile
/// table. See the file remarks for why it is sampled, why it is compressed and why there are
/// two resolutions.
/// </summary>
internal sealed class QuantileTable
{
    /// <summary>4096 intervals — 0.024% of a volume fraction, two orders finer than the
    /// sampling error the table is summarizing.</summary>
    public const int Steps = 4096;

    private readonly double[] _quantiles;

    private QuantileTable(double[] quantiles) => _quantiles = quantiles;

    public static QuantileTable Build(double[] samples)
    {
        Array.Sort(samples);
        var quantiles = new double[Steps + 1];
        for (int i = 0; i <= Steps; i++)
            quantiles[i] = samples[(int)Math.Round(i * (samples.Length - 1.0) / Steps)];
        return new QuantileTable(quantiles);
    }

    /// <summary>The value at the given fraction of the distribution.</summary>
    public double Quantile(double fraction) =>
        _quantiles[Math.Clamp((int)(fraction * Steps), 0, Steps)];

    /// <summary>The fraction of the cell whose value is at or below <paramref name="value"/>.</summary>
    public double FractionAtOrBelow(double value)
    {
        // Number of table entries <= value, by binary search over the ascending table.
        int lo = 0, hi = _quantiles.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_quantiles[mid] <= value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return (double)lo / _quantiles.Length;
    }

    /// <summary>The fraction of the cell whose value lies in [−<paramref name="level"/>,
    /// +<paramref name="level"/>] — a sheet's fraction, read off the same table as the
    /// solid's.</summary>
    public double FractionOfAbsAtOrBelow(double level) =>
        Math.Max(0, FractionAtOrBelow(level) - FractionAtOrBelow(-level));

    /// <summary>The inverse of <see cref="FractionOfAbsAtOrBelow"/>, by bisection — the
    /// symmetric band is not a quantile of the table, so it is solved rather than looked
    /// up.</summary>
    public double AbsQuantile(double fraction)
    {
        double lo = 0, hi = Math.Max(Math.Abs(_quantiles[0]), Math.Abs(_quantiles[Steps]));
        for (int i = 0; i < 80; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (FractionOfAbsAtOrBelow(mid) < fraction)
                lo = mid;
            else
                hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    // ---- the shared cache ----

    private static readonly ConcurrentDictionary<(object Owner, int Resolution), QuantileTable> Cache = new();

    /// <summary>The table for one field at one resolution, built once. Keyed on the owner
    /// object, so each surface or strut cell has its own and nothing has to name itself.
    /// <para>Two threads racing the same key can both run the factory — <c>GetOrAdd</c>'s
    /// documented behaviour — and that is harmless here rather than merely tolerated: the
    /// sample is a deterministic function of the field, so the loser's table is identical to
    /// the winner's and is simply dropped (the <c>LazyGridSdf</c> convention).</para></summary>
    public static QuantileTable For(object owner, int resolution, Func<int, double[]> sample) =>
        Cache.GetOrAdd((owner, resolution), key => Build(sample(key.Resolution)));

    /// <summary>The grid the parameter is solved on.</summary>
    public const int FitResolution = 64;

    /// <summary>The grid the achieved fraction is measured on — coprime with
    /// <see cref="FitResolution"/>, so the two share no sample.</summary>
    public const int CheckResolution = 81;
}
