namespace EngrCAD.Modeling;

/// <summary>What a stackup answers: the chain's nominal, its worst-case extremes, and
/// the statistical (RSS) band.</summary>
/// <param name="Nominal">The signed sum of the contributions' nominals.</param>
/// <param name="WorstCaseMin">Every contribution at the end of its band that shrinks
/// the result.</param>
/// <param name="WorstCaseMax">Every contribution at the end that grows it.</param>
/// <param name="RssMean">The centre of the statistical band — the sum of each
/// contribution's MID value, which differs from <paramref name="Nominal"/> exactly
/// when a tolerance is asymmetric.</param>
/// <param name="RssHalfWidth">Root-sum-square of the contributions' half-widths: the
/// usual normal-and-independent assumption, stated rather than implied.</param>
public readonly record struct StackupResult(
    double Nominal,
    double WorstCaseMin,
    double WorstCaseMax,
    double RssMean,
    double RssHalfWidth)
{
    /// <summary>The statistical band's low end.</summary>
    public double RssMin => RssMean - RssHalfWidth;

    /// <summary>The statistical band's high end.</summary>
    public double RssMax => RssMean + RssHalfWidth;
}

/// <summary>
/// A one-dimensional tolerance stackup: signed contributions summed worst-case and
/// root-sum-square. The CHAIN is the caller's design statement — which dimensions
/// contribute, in which direction — because nothing in the model carries it: mates
/// constrain poses and hold no toleranced dimensions, so a stackup derived from the
/// mate graph would be a guess about intent (the finding that scoped this API; a
/// dimension scheme on mates is filed).
///
/// <para><b>Asymmetric tolerances</b> are handled the textbook way and the treatment
/// is stated: worst-case uses each contribution's own signed band ends; RSS re-centres
/// each contribution on its MID value and root-sum-squares the half-widths, so
/// <see cref="StackupResult.RssMean"/> shifts away from the nominal exactly when a
/// band is asymmetric. A fit contributes its clearance band the same way
/// (<see cref="Add(string, IsoFit)"/>), so "the gap this pin can sit off-centre by"
/// enters a chain like any plate thickness.</para>
/// </summary>
public sealed class ToleranceStackup
{
    private readonly List<(string Name, int Direction, double Nominal, double Plus, double Minus)> _contributions = [];

    /// <summary>The contributions, in the order added.</summary>
    public IReadOnlyList<(string Name, int Direction, double Nominal, double Plus, double Minus)> Contributions =>
        _contributions;

    /// <summary>A dimension measured WITH the chain: nominal, +plus / −minus (both
    /// stated as non-negative magnitudes, so "50 +0.1/−0.05" is
    /// <c>Add("x", 50, 0.1, 0.05)</c>).</summary>
    public ToleranceStackup Add(string name, double nominal, double plus, double minus) =>
        Contribute(name, +1, nominal, plus, minus);

    /// <summary>A dimension measured AGAINST the chain (a shoulder eating into a
    /// gap).</summary>
    public ToleranceStackup Subtract(string name, double nominal, double plus, double minus) =>
        Contribute(name, -1, nominal, plus, minus);

    /// <summary>A fit's CLEARANCE as a contribution: nominal 0, band
    /// [<see cref="IsoFit.MinClearance"/>, <see cref="IsoFit.MaxClearance"/>] — how far
    /// the pin can float in its bore, entering the chain like any other dimension.</summary>
    public ToleranceStackup Add(string name, IsoFit fit)
    {
        ArgumentNullException.ThrowIfNull(fit);
        return Contribute(name, +1, 0, fit.MaxClearance, -fit.MinClearance);
    }

    private ToleranceStackup Contribute(string name, int direction, double nominal, double plus, double minus)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A contribution needs a name.", nameof(name));
        if (!double.IsFinite(nominal))
            throw new ArgumentOutOfRangeException(nameof(nominal));
        if (!double.IsFinite(plus) || !double.IsFinite(minus))
            throw new ArgumentOutOfRangeException(nameof(plus), "Tolerances must be finite.");
        if (plus + minus < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(plus),
                $"'{name}': the band +{plus}/−{minus} is inverted (its width is negative). " +
                "State an asymmetric band by its signed ends, e.g. +0/−0.05.");
        }
        _contributions.Add((name, direction, nominal, plus, minus));
        return this;
    }

    /// <summary>The chain's answer. Refuses an empty chain by name.</summary>
    public StackupResult Evaluate()
    {
        if (_contributions.Count == 0)
            throw new InvalidOperationException("The stackup has no contributions.");
        double nominal = 0, min = 0, max = 0, mean = 0, sumSquares = 0;
        foreach (var (_, direction, n, plus, minus) in _contributions)
        {
            // The contribution's own band, in chain direction. For direction −1 the
            // band's ends swap: subtracting a dimension at its largest shrinks the gap.
            double low = direction * n - (direction > 0 ? minus : plus);
            double high = direction * n + (direction > 0 ? plus : minus);
            nominal += direction * n;
            min += low;
            max += high;
            mean += (low + high) / 2;
            double half = (high - low) / 2;
            sumSquares += half * half;
        }
        return new StackupResult(nominal, min, max, mean, Math.Sqrt(sumSquares));
    }
}
