namespace EngrCAD.Fea;

/// <summary>
/// Natural-convection correlations for vertical parallel-plate fin channels — the
/// spreadsheet arithmetic a heatsink is sized by, transcribed so the thermal solver can
/// CHECK it rather than the two living in different tools. ⚠ Every constant here is a
/// verify-against-datasheet transcription (the <c>MarinFactors</c>/<c>StandardHoles</c>
/// convention), asserted in the form the reference states it: the Bar-Cohen &amp;
/// Rohsenow composite Nusselt correlation for symmetric isothermal plates, the Elenbaas
/// optimum <c>El = 54.3</c>, and dry-air properties at 300 K (Incropera's table). The
/// classic <c>Nu = 1.31</c> at the optimum is DERIVED from the composite rather than
/// stored — a second copy could only drift.
///
/// <para><b>Units are SI here and converted at the edges</b> (the <c>ModelUnits</c>
/// discipline): a correlation's inputs are metres and kelvin because that is the form a
/// datasheet states and a human checks; the callers that feed the mm-world (the FEA
/// cross-check's film coefficient, the sizing over a mm envelope) convert once, visibly.
/// Orientation is VERTICAL plates only — the one case transcribed — offered by name;
/// horizontal orientations and forced convection want their own correlations or a stated
/// film coefficient.</para>
/// </summary>
public static class NaturalConvection
{
    /// <summary>Dry air at 300 K, ⚠ transcribed (Incropera): thermal conductivity
    /// W/(m·K).</summary>
    public const double AirConductivity = 0.0263;

    /// <summary>Kinematic viscosity ν, m²/s. ⚠ transcribed.</summary>
    public const double AirKinematicViscosity = 1.589e-5;

    /// <summary>Thermal diffusivity α, m²/s. ⚠ transcribed.</summary>
    public const double AirThermalDiffusivity = 2.25e-5;

    /// <summary>The Elenbaas number at the OPTIMUM spacing of symmetric isothermal
    /// vertical plates, <c>El = Ra_S·S/L = 54.3</c>. ⚠ transcribed
    /// (Bar-Cohen &amp; Rohsenow).</summary>
    public const double ElenbaasOptimum = 54.3;

    private const double Gravity = 9.81;

    /// <summary>The Bar-Cohen &amp; Rohsenow composite Nusselt number for symmetric
    /// isothermal vertical plates at Elenbaas number <paramref name="elenbaas"/>:
    /// <c>Nu = [576/El² + 2.873/√El]^(−1/2)</c> — the fully-developed limit and the
    /// isolated-plate limit blended, valid across the whole spacing range, which is what
    /// makes the optimum below a derivation rather than a second constant.</summary>
    public static double Nusselt(double elenbaas)
    {
        if (!(elenbaas > 0) || !double.IsFinite(elenbaas))
            throw new ArgumentException(
                $"The Elenbaas number must be finite and positive; got {elenbaas:G4}.");
        return 1.0 / Math.Sqrt(
            576.0 / (elenbaas * elenbaas) + 2.873 / Math.Sqrt(elenbaas));
    }

    /// <summary>The Elenbaas optimum channel spacing (m) for plates of vertical length
    /// <paramref name="channelLengthMetres"/> at <paramref name="riseKelvin"/> above
    /// ambient <paramref name="ambientKelvin"/>: solving <c>El = gβΔT·S⁴/(ναL) = 54.3</c>
    /// for S — which carries the closed-form scaling the tests pin, S ∝ ΔT^(−1/4), so
    /// sixteen times the rise HALVES the optimum spacing exactly.</summary>
    public static double OptimumSpacing(
        double channelLengthMetres, double riseKelvin, double ambientKelvin = 300)
    {
        Require(channelLengthMetres, nameof(channelLengthMetres));
        Require(riseKelvin, nameof(riseKelvin));
        Require(ambientKelvin, nameof(ambientKelvin));
        // The ideal-gas expansion coefficient at the FILM temperature.
        double beta = 1.0 / (ambientKelvin + riseKelvin / 2);
        double rayleighPerS4 = Gravity * beta * riseKelvin
            / (AirKinematicViscosity * AirThermalDiffusivity);
        return Math.Pow(
            ElenbaasOptimum * channelLengthMetres / rayleighPerS4, 0.25);
    }

    /// <summary>The film coefficient (W/(m²·K)) at the OPTIMUM spacing: the composite
    /// Nusselt at El = 54.3 — the classic 1.31, derived — over that spacing.</summary>
    public static double FilmCoefficientAtOptimum(
        double channelLengthMetres, double riseKelvin, double ambientKelvin = 300)
    {
        double spacing = OptimumSpacing(channelLengthMetres, riseKelvin, ambientKelvin);
        return Nusselt(ElenbaasOptimum) * AirConductivity / spacing;
    }

    /// <summary>The efficiency of a thin rectangular fin with an ADIABATIC tip:
    /// <c>η = tanh(mH)/(mH)</c> with <c>m = √(2h/(k·t))</c> — exact for the 1D fin
    /// equation, and the FEA cross-check's discriminating row is a 3D conduction solve of
    /// the SAME fin agreeing with it. Inputs SI: h in W/(m²·K), k in W/(m·K), thickness
    /// and height in metres.</summary>
    public static double FinEfficiency(
        double filmCoefficient, double conductivity, double thicknessMetres,
        double heightMetres)
    {
        Require(filmCoefficient, nameof(filmCoefficient));
        Require(conductivity, nameof(conductivity));
        Require(thicknessMetres, nameof(thicknessMetres));
        Require(heightMetres, nameof(heightMetres));
        double m = Math.Sqrt(2 * filmCoefficient / (conductivity * thicknessMetres));
        double mh = m * heightMetres;
        return Math.Tanh(mh) / mh;
    }

    private static void Require(double value, string name)
    {
        if (!(value > 0) || !double.IsFinite(value))
            throw new ArgumentException($"{name} must be finite and positive; got {value:G4}.");
    }
}

/// <summary>What a heatsink is asked to do, in the units each number is stated in: the
/// dissipated power in WATTS, the allowable rise in KELVIN, the envelope in MILLIMETRES
/// (the model's own unit). Fins run along <see cref="BaseDepth"/>, which is the VERTICAL
/// dimension — the channel length the Elenbaas correlation reads — a stated convention,
/// since natural convection is orientation-specific and only the vertical case is
/// transcribed.</summary>
public sealed record HeatsinkSpec(
    double PowerWatts,
    double AllowableRise,
    double BaseWidth,
    double BaseDepth,
    double MaxFinHeight,
    double FinThickness = 2,
    double BaseThickness = 4,
    double ConductivityWPerMK = 200,
    double AmbientKelvin = 300);

/// <summary>A sized fin array: the count, the Elenbaas-optimal spacing, the shortest fin
/// height that meets the rise, and the numbers it was accepted on — film coefficient,
/// fin efficiency, thermal resistance (K/W) and the predicted rise, every one checkable
/// against the thermal solver's own conduction solve of the very solid this
/// describes.</summary>
public sealed record HeatsinkDesign(
    HeatsinkSpec Spec,
    int FinCount,
    double FinSpacing,
    double FinHeight,
    double FilmCoefficient,
    double FinEfficiency,
    double ThermalResistance,
    double PredictedRise);

/// <summary>
/// Sizes a natural-convection fin array from the correlations — the spreadsheet a
/// datasheet heatsink is chosen by, except that this one's answer can be CHECKED against
/// the repo's own thermal FEA (a conduction solve with the correlation's film coefficient
/// as the Convection BC on the generated solid). Spacing is the Elenbaas optimum at the
/// allowable rise; the fin height is the SHORTEST that meets the rise (found by bisection
/// on a quantity that is provably monotone: <c>d/dH[tanh(mH)/m] = sech²(mH) > 0</c>, so
/// the effective area η·A only grows with H); an envelope that cannot meet the rise even
/// at the maximum height refuses naming both the asked and the achievable rise.
/// </summary>
public static class HeatsinkSizing
{
    /// <summary>Sizes the array, or refuses naming the deficit.</summary>
    public static HeatsinkDesign Size(HeatsinkSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!(spec.PowerWatts > 0) || !(spec.AllowableRise > 0)
            || !(spec.BaseWidth > 0) || !(spec.BaseDepth > 0) || !(spec.MaxFinHeight > 0)
            || !(spec.FinThickness > 0) || !(spec.ConductivityWPerMK > 0))
            throw new ArgumentException("Every HeatsinkSpec quantity must be positive.");

        // SI at the correlation boundary, once and visibly: mm -> m.
        double lengthM = spec.BaseDepth / 1000;
        double widthM = spec.BaseWidth / 1000;
        double thicknessM = spec.FinThickness / 1000;

        double spacingM = NaturalConvection.OptimumSpacing(
            lengthM, spec.AllowableRise, spec.AmbientKelvin);
        double h = NaturalConvection.FilmCoefficientAtOptimum(
            lengthM, spec.AllowableRise, spec.AmbientKelvin);

        int count = (int)Math.Floor((widthM + spacingM) / (thicknessM + spacingM));
        if (count < 2)
            throw new FeaException(
                $"The base width {spec.BaseWidth:0.#} mm holds fewer than two fins at the "
                + $"Elenbaas-optimal spacing {spacingM * 1000:0.##} mm and thickness "
                + $"{spec.FinThickness:0.#} mm — there is no channel to convect from. "
                + "Widen the base or thin the fins.");

        double DissipationAt(double heightM)
        {
            double eta = NaturalConvection.FinEfficiency(
                h, spec.ConductivityWPerMK, thicknessM, heightM);
            double finArea = count * 2 * heightM * lengthM;
            double baseArea = (widthM - count * thicknessM) * lengthM;
            return h * (eta * finArea + baseArea) * spec.AllowableRise;
        }

        double maxHeightM = spec.MaxFinHeight / 1000;
        if (DissipationAt(maxHeightM) < spec.PowerWatts)
        {
            double achievable = DissipationAt(maxHeightM);
            throw new FeaException(
                $"The envelope cannot meet the rise: at the maximum fin height "
                + $"{spec.MaxFinHeight:0.#} mm the array dissipates {achievable:0.##} W at "
                + $"{spec.AllowableRise:0.#} K where {spec.PowerWatts:0.##} W was asked — "
                + $"short by {spec.PowerWatts - achievable:0.##} W. Enlarge the envelope, "
                + "allow a larger rise, or duct the flow.");
        }

        // The shortest height that meets the power — bisection on a monotone quantity.
        double lo = 1e-4, hi = maxHeightM;
        if (DissipationAt(lo) >= spec.PowerWatts)
        {
            hi = lo;
        }
        else
        {
            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (DissipationAt(mid) >= spec.PowerWatts)
                    hi = mid;
                else
                    lo = mid;
            }
        }
        double heightM = hi;

        double etaFinal = NaturalConvection.FinEfficiency(
            h, spec.ConductivityWPerMK, thicknessM, heightM);
        double effective = etaFinal * count * 2 * heightM * lengthM
            + (widthM - count * thicknessM) * lengthM;
        double resistance = 1.0 / (h * effective);

        return new HeatsinkDesign(
            spec, count, spacingM * 1000, heightM * 1000, h, etaFinal, resistance,
            spec.PowerWatts * resistance);
    }
}
