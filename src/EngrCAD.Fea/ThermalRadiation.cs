namespace EngrCAD.Fea;

/// <summary>One radiating surface: the selected facets exchange heat with surroundings at
/// <paramref name="SurroundingKelvin"/> through grey-body emissivity
/// <paramref name="Emissivity"/>, <c>q = sigma·epsilon·(T^4 - Ts^4)</c>. Temperatures must
/// be ABSOLUTE (kelvin): the fourth power is a statement about absolute temperature, and a
/// model built in celsius produces silently wrong physics no solver can detect — which is
/// why a non-positive surrounding refuses by name. Two surfaces on one facet ACCUMULATE,
/// as convective films do (radiating to the sky and to a nearby wall are two exchanges).</summary>
public sealed record RadiationSurface(
    Func<FacetRef, bool> Facets, double Emissivity, double SurroundingKelvin);

/// <summary>Settings for the radiation outer iteration.</summary>
public sealed record ThermalRadiationOptions
{
    /// <summary>Iteration cap; past it the solve refuses by name with the last change.</summary>
    public int MaxIterations { get; init; } = 50;

    /// <summary>Convergence: the largest facet-mean temperature change between iterations,
    /// relative to the temperature scale.</summary>
    public double RelativeTolerance { get; init; } = 1e-10;

    /// <summary>Under-relaxation on the linearization point (0, 1]. Radiation's Picard map
    /// OVERSHOOTS — the plain iteration was measured oscillating in a 1.7e-4 limit cycle
    /// on the equilibrium fixture — and half-stepping the linearization point damps it to
    /// clean linear convergence. 1 is the undamped map.</summary>
    public double Relaxation { get; init; } = 0.5;
}

/// <summary>A converged radiating solve: the final linearized steady results, and what the
/// iteration cost.</summary>
public sealed record ThermalRadiationResult(
    ThermalResults Results, int Iterations, double LastRelativeChange);

/// <summary>
/// Grey-body surface radiation as the OUTER iteration wrapping the linear steady solver —
/// the shape the conduction model's own remarks reserve for it, because
/// <c>sigma·epsilon·(T^4 - Ts^4)</c> is nonlinear in the unknown and everything else here
/// is one factorization. Each pass linearizes per FACET about the previous answer's facet
/// mean, <c>h_rad = sigma·epsilon·(T̄² + Ts²)(T̄ + Ts)</c> with ambient <c>Ts</c> — exactly
/// a convective film, assembled by the same surface quadrature through the model's
/// internal film overlay, so the radiating solve reuses the convection machinery rather
/// than restating it (and a radiating facet counts as DRIVEN for the same reason a
/// convective one does: it pins the temperature level).
/// </summary>
public static class ThermalRadiation
{
    /// <summary>The Stefan–Boltzmann constant in MODEL units, mW/(mm²·K⁴): the SI
    /// 5.670374419e-8 W/(m²·K⁴) through the film coefficient's own ×1e-3 conversion
    /// (the <c>ModelUnits</c> discipline — same route as `Convection`'s h).</summary>
    public const double StefanBoltzmann = 5.670374419e-11;

    /// <summary>Solves the steady conduction problem with the radiating surfaces, by
    /// Picard iteration on the per-facet linearization. The model itself is never
    /// mutated (the overlay is cleared in a finally), so it remains reusable.</summary>
    public static ThermalRadiationResult Solve(
        ThermalModel model, IReadOnlyList<RadiationSurface> surfaces,
        ThermalSolveOptions? solve = null, ThermalRadiationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(surfaces);
        options ??= new ThermalRadiationOptions();
        if (surfaces.Count == 0)
            throw new FeaException("At least one radiating surface is required.");
        if (!(options.Relaxation > 0) || options.Relaxation > 1)
            throw new FeaException(
                $"Relaxation must lie in (0, 1]; got {options.Relaxation:G4}.");

        var mesh = model.Mesh;
        var resolved = new List<(int[] Facets, double Emissivity, double Surrounding)>();
        foreach (var surface in surfaces)
        {
            ArgumentNullException.ThrowIfNull(surface.Facets);
            if (!(surface.Emissivity > 0) || surface.Emissivity > 1)
                throw new FeaException(
                    $"Emissivity must lie in (0, 1]; got {surface.Emissivity:G4}. Zero is "
                    + "no condition (leave the surface unmentioned), and above one is more "
                    + "than a black body.");
            if (!(surface.SurroundingKelvin > 0))
                throw new FeaException(
                    $"The surroundings must be a positive ABSOLUTE temperature (kelvin); got "
                    + $"{surface.SurroundingKelvin:G4}. Radiation's fourth power is a statement "
                    + "about absolute temperature — a model built in celsius is silently "
                    + "wrong physics, which is why the refusal is loud.");
            var matched = new List<int>();
            for (int f = 0; f < mesh.FacetCount; f++)
            {
                if (surface.Facets(model.Describe(f)))
                    matched.Add(f);
            }
            if (matched.Count == 0)
                throw new FeaException(
                    "A radiating surface selected no boundary facets, so it would have had "
                    + "no effect.");
            resolved.Add(([.. matched], surface.Emissivity, surface.SurroundingKelvin));
        }

        // The linearization point per facet: start at each facet's own surroundings —
        // the temperature the surface would sit at with nothing else driving it.
        var mean = new double[mesh.FacetCount];
        foreach (var (facets, _, surrounding) in resolved)
            foreach (int f in facets)
                mean[f] = surrounding;

        var film = new double[mesh.FacetCount];
        var supply = new double[mesh.FacetCount];
        ThermalResults? results = null;
        double change = double.PositiveInfinity;
        int iteration = 0;
        try
        {
            for (iteration = 1; iteration <= options.MaxIterations; iteration++)
            {
                Array.Clear(film);
                Array.Clear(supply);
                foreach (var (facets, emissivity, surrounding) in resolved)
                {
                    foreach (int f in facets)
                    {
                        double t = mean[f];
                        double h = StefanBoltzmann * emissivity
                            * (t * t + surrounding * surrounding) * (t + surrounding);
                        film[f] += h;
                        supply[f] += h * surrounding;
                    }
                }
                model.OverlayFilm = film;
                model.OverlaySupply = supply;
                results = ThermalSolver.Solve(model, solve);

                double scale = 1;
                change = 0;
                foreach (var (facets, _, _) in resolved)
                {
                    foreach (int f in facets)
                    {
                        var nodes = mesh.Facet(f);
                        double sum = 0;
                        foreach (int node in nodes)
                            sum += results.Temperature[node];
                        double next = sum / nodes.Length;
                        change = Math.Max(change, Math.Abs(next - mean[f]));
                        scale = Math.Max(scale, Math.Abs(next));
                        mean[f] += options.Relaxation * (next - mean[f]);
                    }
                }
                change /= scale;
                if (change < options.RelativeTolerance)
                    return new ThermalRadiationResult(results, iteration, change);
            }
        }
        finally
        {
            model.OverlayFilm = null;
            model.OverlaySupply = null;
        }
        throw new FeaException(
            $"The radiation iteration did not converge in {options.MaxIterations} passes; "
            + $"the last relative change was {change:E3} against a tolerance of "
            + $"{options.RelativeTolerance:E1}. A larger tolerance, more iterations, or a "
            + "check that the temperatures are on an ABSOLUTE scale are the usual fixes.");
    }
}
