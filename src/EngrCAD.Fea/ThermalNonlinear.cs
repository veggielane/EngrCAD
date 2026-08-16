namespace EngrCAD.Fea;

/// <summary>Settings for the nonlinear-conductivity outer iteration.</summary>
public sealed record ThermalNonlinearOptions
{
    /// <summary>Iteration cap; past it the solve refuses by name with the last change.</summary>
    public int MaxIterations { get; init; } = 50;

    /// <summary>Convergence: the largest element-mean temperature change between
    /// iterations, relative to the temperature scale.</summary>
    public double RelativeTolerance { get; init; } = 1e-10;

    /// <summary>Under-relaxation on the linearization temperatures (0, 1] — the radiation
    /// iteration's own remedy for a Picard map that overshoots; 1 is undamped.</summary>
    public double Relaxation { get; init; } = 0.5;
}

/// <summary>A converged nonlinear-conductivity solve.
/// <para><b>The flux caveat, stated rather than discovered</b>: the returned
/// <see cref="ThermalResults"/>' flux accessors read the MODEL's own (constant) laws,
/// because the overlay is cleared when the solve returns — the temperature field is the
/// nonlinear answer, but a flux is <c>−k·∇T</c> and the k the accessor multiplies by is
/// the constant one. <see cref="ElementConductivity"/> carries the CONVERGED per-element
/// k (NaN where the element kept its model law), so the nonlinear flux is
/// <c>ElementFlux(e) · ElementConductivity[e] / k_model</c>.</para></summary>
public sealed record ThermalNonlinearResult(
    ThermalResults Results, int Iterations, double LastRelativeChange,
    IReadOnlyList<double> ElementConductivity);

/// <summary>
/// Temperature-dependent conductivity as the OUTER iteration wrapping the linear steady
/// solver — the second consumer of the shape <see cref="ThermalRadiation"/> established,
/// with the one structural difference stated up front: a radiating pass moves only the
/// LOAD, while a conductivity pass changes the MATRIX, so every iteration re-assembles and
/// re-factors. Each pass evaluates the law per ELEMENT at the element's node-mean
/// temperature from the previous answer and solves the linear problem with that scalar
/// field (through the model's internal per-element overlay, so the user's model is never
/// mutated); a converged fixed point is a solution of the true k(T) problem in the
/// per-element-constant sense the discretization carries anyway.
/// </summary>
public static class ThermalNonlinear
{
    /// <summary>Solves steady conduction with per-region temperature-dependent
    /// conductivities, <c>k = law(T)</c> in mW/(mm·K) as everywhere. Regions not named
    /// keep their material's constant conductivity; a region carrying a DIRECTIONAL
    /// <c>ConductivityLaw</c> cannot also take a temperature law (refused by name — the
    /// composition wants a temperature-dependent tensor, a different feature).</summary>
    public static ThermalNonlinearResult Solve(
        ThermalModel model, IReadOnlyDictionary<int, Func<double, double>> conductivityByRegion,
        ThermalSolveOptions? solve = null, ThermalNonlinearOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(conductivityByRegion);
        options ??= new ThermalNonlinearOptions();
        if (conductivityByRegion.Count == 0)
            throw new FeaException("At least one region's conductivity law is required.");
        if (!(options.Relaxation > 0) || options.Relaxation > 1)
            throw new FeaException(
                $"Relaxation must lie in (0, 1]; got {options.Relaxation:G4}.");

        var mesh = model.Mesh;
        var laws = new Func<double, double>?[mesh.ElementCount];
        var lawed = new bool[mesh.ElementCount];
        foreach (var (region, law) in conductivityByRegion)
        {
            ArgumentNullException.ThrowIfNull(law);
            bool any = false;
            for (int e = 0; e < mesh.ElementCount; e++)
            {
                if (mesh.RegionOf(e) != region)
                    continue;
                if (!model.ConductivityLawOf(e).IsIsotropic)
                    throw new FeaException(
                        $"Region {region} carries a DIRECTIONAL ConductivityLaw and cannot "
                        + "also take a temperature law — the composition would be a "
                        + "temperature-dependent tensor, which is a different feature.");
                laws[e] = law;
                lawed[e] = true;
                any = true;
            }
            if (!any)
                throw new FeaException(
                    $"No element carries region id {region}; the law would have no effect.");
        }

        // The linearization temperatures per element, seeded from the model's own constant-k
        // steady answer — the natural starting point, and it also validates the model
        // (restraints, drive) before the loop spends anything.
        var seed = ThermalSolver.Solve(model, solve);
        var mean = new double[mesh.ElementCount];
        for (int e = 0; e < mesh.ElementCount; e++)
            mean[e] = ElementMean(mesh, seed.Temperature, e);

        var k = new double[mesh.ElementCount];
        ThermalResults results = seed;
        double change = double.PositiveInfinity;
        try
        {
            for (int iteration = 1; iteration <= options.MaxIterations; iteration++)
            {
                for (int e = 0; e < mesh.ElementCount; e++)
                {
                    if (laws[e] is { } law)
                    {
                        double value = law(mean[e]);
                        if (!(value > 0) || !double.IsFinite(value))
                            throw new FeaException(
                                $"The conductivity law returned {value:G4} at "
                                + $"T = {mean[e]:G6}; a conductivity must be finite and "
                                + "positive, or the matrix stops being positive definite.");
                        k[e] = value;
                    }
                    else
                    {
                        k[e] = double.NaN;           // keep the model's own law (see assembly)
                    }
                }
                model.OverlayConductivity = k;
                results = ThermalSolver.Solve(model, solve);

                double scale = 1;
                change = 0;
                for (int e = 0; e < mesh.ElementCount; e++)
                {
                    if (!lawed[e])
                        continue;
                    double next = ElementMean(mesh, results.Temperature, e);
                    change = Math.Max(change, Math.Abs(next - mean[e]));
                    scale = Math.Max(scale, Math.Abs(next));
                    mean[e] += options.Relaxation * (next - mean[e]);
                }
                change /= scale;
                if (change < options.RelativeTolerance)
                    return new ThermalNonlinearResult(
                        results, iteration, change, (double[])k.Clone());
            }
        }
        finally
        {
            model.OverlayConductivity = null;
        }
        throw new FeaException(
            $"The nonlinear-conductivity iteration did not converge in "
            + $"{options.MaxIterations} passes; the last relative change was {change:E3} "
            + $"against a tolerance of {options.RelativeTolerance:E1}. More iterations, a "
            + "larger tolerance or a gentler Relaxation are the usual fixes.");
    }

    private static double ElementMean(AnalysisMesh mesh, IReadOnlyList<double> field, int e)
    {
        var nodes = mesh.Element(e);
        double sum = 0;
        foreach (int node in nodes)
            sum += field[node];
        return sum / nodes.Length;
    }
}
