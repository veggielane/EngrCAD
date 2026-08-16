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

/// <summary>A property-nonlinear transient's states — one per stored step, each a full
/// <see cref="ThermalResults"/> — plus the run's aggregated honesty numbers.
/// <para><see cref="Factorizations"/> equals the step count BY CONSTRUCTION: a property
/// nonlinearity re-assembles and re-factors every step, which is exactly the
/// one-factorization amortisation the linear transient's report celebrates and this one
/// necessarily gives up.</para>
/// <para><see cref="ElementConductivity"/>/<see cref="ElementCapacity"/> carry the FINAL
/// step's per-element values (NaN where the element kept its model law) — the flux
/// caveat on <see cref="ThermalNonlinearResult"/> applies to every stored state's
/// accessors here too, since the overlays are cleared when the solve returns.</para>
/// </summary>
public sealed record ThermalNonlinearTransientResult(
    IReadOnlyList<ThermalResults> States,
    IReadOnlyList<double> Times,
    double WorstEnergyBalanceResidual,
    double WorstRelativeResidual,
    bool Converged,
    int Factorizations,
    IReadOnlyList<double> ElementConductivity,
    IReadOnlyList<double> ElementCapacity);

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

    /// <summary>
    /// A property-nonlinear TRANSIENT: temperature-dependent conductivity and/or heat
    /// capacity, stepped as a sequence of one-step constant-property transients — each
    /// step evaluates the laws per element at the step's START temperatures, sets the
    /// internal overlays, and runs <see cref="ThermalSolver.SolveTransient"/> for one
    /// step seeded from the previous state. That composition reuses the linear
    /// stepper's whole machinery (the theta schemes, lumping, prescribed snapping, the
    /// per-step first-law identity) verbatim, and it states the cost honestly: a
    /// property nonlinearity re-assembles and re-factors EVERY step — the
    /// one-factorization amortisation is exactly what k(T)/c(T) necessarily gives up
    /// (<see cref="ThermalNonlinearTransientResult.Factorizations"/> says so).
    /// <para><b>Property evaluation is explicit in the step</b> (start-of-step
    /// temperatures), which is first-order in the property — matching backward Euler's
    /// own order; under Crank–Nicolson the property term still limits the run to first
    /// order, stated rather than discovered. Time-varying LOAD or prescribed laws are
    /// refused by name: a sub-run's clock restarts at zero, so composing them needs
    /// law re-basing, a filed follow-up.</para>
    /// <para>The capacity law returns the SPECIFIC heat c(T) in the material's own
    /// units (the datasheet quantity); the overlay carries rho·c, so a law returning
    /// exactly the material's constant reproduces the plain transient bit for bit —
    /// the degeneration the tests hold.</para>
    /// </summary>
    public static ThermalNonlinearTransientResult SolveTransient(
        ThermalModel model,
        ThermalTransientOptions transient,
        IReadOnlyDictionary<int, Func<double, double>>? conductivityByRegion = null,
        IReadOnlyDictionary<int, Func<double, double>>? capacityByRegion = null,
        ThermalSolveOptions? solve = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(transient);
        if ((conductivityByRegion?.Count ?? 0) == 0 && (capacityByRegion?.Count ?? 0) == 0)
            throw new FeaException(
                "At least one conductivity or capacity law is required; with neither, "
                + "use ThermalSolver.SolveTransient.");
        if (model.HasTimeLaws)
            throw new FeaException(
                "A property-nonlinear transient cannot carry time-varying load or "
                + "prescribed laws yet: each step runs as its own one-step transient "
                + "whose clock restarts at zero, so the laws would be evaluated at the "
                + "wrong instants. Re-basing the laws per step is a filed follow-up.");

        var mesh = model.Mesh;
        var conductivityLaws = new Func<double, double>?[mesh.ElementCount];
        if (conductivityByRegion is not null)
            MapLaws(model, conductivityByRegion, conductivityLaws, requireIsotropic: true);
        var capacityLaws = new Func<double, double>?[mesh.ElementCount];
        var density = new double[mesh.ElementCount];
        if (capacityByRegion is not null)
        {
            MapLaws(model, capacityByRegion, capacityLaws, requireIsotropic: false);
            for (int e = 0; e < mesh.ElementCount; e++)
                density[e] = model.MaterialOf(e).Density;
        }

        var current = new double[mesh.NodeCount];
        if (transient.InitialField is { } field0)
        {
            if (field0.Count != mesh.NodeCount)
                throw new FeaException(
                    $"InitialField has {field0.Count} values for {mesh.NodeCount} nodes.");
            for (int n = 0; n < mesh.NodeCount; n++)
                current[n] = field0[n];
        }
        else
        {
            Array.Fill(current, transient.InitialTemperature);
        }

        var k = new double[mesh.ElementCount];
        var c = new double[mesh.ElementCount];
        var states = new List<ThermalResults>();
        var times = new List<double>();
        double worstEnergy = 0, worstResidual = 0;
        bool converged = true;
        try
        {
            for (int step = 1; step <= transient.Steps; step++)
            {
                for (int e = 0; e < mesh.ElementCount; e++)
                {
                    double mean = ElementMean(mesh, current, e);
                    if (conductivityLaws[e] is { } kLaw)
                    {
                        double value = kLaw(mean);
                        if (!(value > 0) || !double.IsFinite(value))
                            throw new FeaException(
                                $"The conductivity law returned {value:G4} at T = {mean:G6}; "
                                + "a conductivity must be finite and positive.");
                        k[e] = value;
                    }
                    else
                    {
                        k[e] = double.NaN;
                    }
                    if (capacityLaws[e] is { } cLaw)
                    {
                        double value = cLaw(mean);
                        if (!(value > 0) || !double.IsFinite(value))
                            throw new FeaException(
                                $"The capacity law returned {value:G4} at T = {mean:G6}; "
                                + "a specific heat must be finite and positive, or the "
                                + "capacity matrix stops being positive definite.");
                        c[e] = density[e] * value;
                    }
                    else
                    {
                        c[e] = double.NaN;
                    }
                }
                model.OverlayConductivity = conductivityByRegion is null ? null : k;
                model.OverlayCapacity = capacityByRegion is null ? null : c;

                var subOptions = new ThermalTransientOptions(transient.TimeStep, 1)
                {
                    Scheme = transient.Scheme,
                    Lumping = transient.Lumping,
                    InitialField = current,
                };
                var run = ThermalSolver.SolveTransient(model, subOptions, solve);
                worstEnergy = Math.Max(worstEnergy, run.Report.EnergyBalanceResidual);
                worstResidual = Math.Max(worstResidual, run.Report.WorstRelativeResidual);
                converged &= run.Report.Converged;

                if (step == 1)
                {
                    // The t = 0 state, prescribed snapping applied — the first
                    // sub-run's own initial, exactly what the linear run stores.
                    states.Add(run.States[0]);
                    times.Add(0);
                    transient.OnState?.Invoke(run.States[0]);
                }
                var end = run.States[^1];
                current = (double[])end.Temperature.ToArray().Clone();
                if (step % transient.StoreEvery == 0 || step == transient.Steps)
                {
                    states.Add(end);
                    times.Add(step * transient.TimeStep);
                    transient.OnState?.Invoke(end);
                }
            }
        }
        finally
        {
            model.OverlayConductivity = null;
            model.OverlayCapacity = null;
        }

        if (!transient.RetainStates && states.Count > 2)
            states.RemoveRange(1, states.Count - 2);

        return new ThermalNonlinearTransientResult(
            states, times, worstEnergy, worstResidual, converged,
            Factorizations: transient.Steps,
            [.. k], [.. c]);
    }

    /// <summary>The per-element law map one dictionary contributes, with the same
    /// refusals the steady solve makes (an unknown region, a temperature law composed
    /// onto a directional conductivity).</summary>
    private static void MapLaws(
        ThermalModel model, IReadOnlyDictionary<int, Func<double, double>> byRegion,
        Func<double, double>?[] laws, bool requireIsotropic)
    {
        var mesh = model.Mesh;
        foreach (var (region, law) in byRegion)
        {
            ArgumentNullException.ThrowIfNull(law);
            bool any = false;
            for (int e = 0; e < mesh.ElementCount; e++)
            {
                if (mesh.RegionOf(e) != region)
                    continue;
                if (requireIsotropic && !model.ConductivityLawOf(e).IsIsotropic)
                    throw new FeaException(
                        $"Region {region} carries a DIRECTIONAL ConductivityLaw and cannot "
                        + "also take a temperature law — the composition would be a "
                        + "temperature-dependent tensor, which is a different feature.");
                laws[e] = law;
                any = true;
            }
            if (!any)
                throw new FeaException(
                    $"No element carries region id {region}; the law would have no effect.");
        }
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
