using System.Numerics;
using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>Frequency lists for a sweep. Plain arrays, so a caller can just as well write
/// their own.</summary>
public static class HarmonicSweep
{
    /// <summary><paramref name="count"/> frequencies evenly spaced from
    /// <paramref name="fromHertz"/> to <paramref name="toHertz"/> inclusive.</summary>
    public static double[] Linear(double fromHertz, double toHertz, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(fromHertz);
        if (toHertz < fromHertz)
            throw new ArgumentException(
                $"The sweep must ascend; {fromHertz} to {toHertz} was given.", nameof(toHertz));
        var f = new double[count];
        // Exact endpoints rather than from + i·step, so the last entry IS toHertz.
        for (int i = 0; i < count; i++)
            f[i] = count == 1 ? fromHertz : fromHertz + (toHertz - fromHertz) * i / (count - 1.0);
        return f;
    }

    /// <summary><paramref name="count"/> frequencies evenly spaced in the LOGARITHM — the
    /// right sweep for a plot that spans decades, and the one that puts equal resolution on
    /// every resonance rather than crowding the low ones.</summary>
    public static double[] Logarithmic(double fromHertz, double toHertz, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fromHertz);
        if (toHertz < fromHertz)
            throw new ArgumentException(
                $"The sweep must ascend; {fromHertz} to {toHertz} was given.", nameof(toHertz));
        var f = new double[count];
        double a = Math.Log(fromHertz), b = Math.Log(toHertz);
        for (int i = 0; i < count; i++)
            f[i] = count == 1 ? fromHertz : Math.Exp(a + (b - a) * i / (count - 1.0));
        return f;
    }

    /// <summary>
    /// A band around a centre frequency, <c>centre·(1 ± halfWidthFraction)</c> — for resolving
    /// one resonance, where a whole-range sweep would step straight over it.
    ///
    /// <para>The reason to have this at all: a lightly damped peak is only
    /// <c>2·zeta</c> wide in relative terms, so a 1% damped mode at 1 kHz has a 20 Hz
    /// half-power band and a 100-point sweep from 0 to 2 kHz samples it twice.</para>
    /// </summary>
    public static double[] Around(double centreHertz, double halfWidthFraction, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(centreHertz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(halfWidthFraction);
        return Linear(
            centreHertz * (1 - halfWidthFraction), centreHertz * (1 + halfWidthFraction), count);
    }
}

/// <summary>Options for <see cref="HarmonicSolver.Solve"/>.</summary>
public sealed record HarmonicSolveOptions
{
    /// <summary>The frequencies to evaluate, in Hz. See <see cref="HarmonicSweep"/>.</summary>
    public required IReadOnlyList<double> Frequencies { get; init; }

    /// <summary>
    /// How much each mode is damped. Required rather than defaulted: a default would be this
    /// project inventing a material property, and the one honest default —
    /// <see cref="ModalDamping.None"/> — gives an infinite response at every resonance, which
    /// is a correct answer to a question almost nobody means to ask. Say it explicitly and it
    /// is allowed.
    /// </summary>
    public required ModalDamping Damping { get; init; }

    /// <summary>
    /// A static solve of the SAME model and the SAME load, which turns modal truncation from
    /// a caveat into a correction — the mode-acceleration (residual-flexibility) method.
    ///
    /// <para>Modal superposition keeps only the modes that were extracted, so it misses the
    /// static flexibility of every mode above them. Given <c>u_static = K^-1 f</c> the
    /// response can be written
    /// <c>u(W) = u_static + sum_n phi_n F_n [1/(w_n² - W² + 2i·zeta·w_n·W) - 1/w_n²]</c>,
    /// whose bracket VANISHES at <c>W = 0</c> — so the response is exactly the static answer
    /// there however few modes were kept, and the missing modes' contribution is carried at
    /// every other frequency as their (frequency-independent) static flexibility. The cost is
    /// one extra static solve, which the caller has usually already done.</para>
    ///
    /// <para>Null keeps the plain mode-displacement sum, whose low-frequency error is exactly
    /// the flexibility of the modes left out — reported as
    /// <see cref="HarmonicResponse.TruncationError"/> when a static solve is supplied and
    /// simply unknown when it is not.</para>
    /// </summary>
    public StructuralResults? StaticCorrection { get; init; }
}

/// <summary>
/// Steady-state response to a harmonic load, by MODAL SUPERPOSITION over modes that have
/// already been computed.
///
/// <para><b>This is the cheap method and the right first one.</b> The modes diagonalise the
/// equations of motion, so each one becomes a scalar oscillator
/// <c>q_n'' + 2·zeta_n·w_n·q_n' + w_n²·q_n = F_n</c> with the closed-form steady solution
/// <c>q_n(W) = F_n / (w_n² - W² + 2i·zeta_n·w_n·W)</c>, and the whole sweep costs one dot
/// product per mode plus a complex division per (mode, frequency) pair. Nothing is factorized
/// and nothing is assembled: a 500-point sweep over 10 modes is 5 000 divisions.</para>
///
/// <para><b>What the alternative buys, and when it is actually needed.</b> A DIRECT solve
/// (<see cref="DirectHarmonicSolver"/>) factorizes <c>(K - W²M + i·W·C)</c> at every
/// frequency — a complex factorization per point, hundreds of times the cost — and is the
/// only option in three cases, none of which apply here: damping that is NOT proportional
/// (see <see cref="RayleighDamping"/>, where the modes stop diagonalising C — the model's
/// own dashpots and per-region coefficients, which are exactly what that solver consumes),
/// material properties that vary WITH frequency (viscoelastic moduli, frequency-dependent
/// stiffness — the modal basis itself would change per point), and a load whose spatial
/// distribution changes with frequency. It is not a better version of this, it answers
/// questions this cannot express — on a proportionally damped model the two agree to the
/// truncation correction's own error, which is the cross-check the tests run.</para>
///
/// <para><b>The load comes from the modal model's own applied forces.</b> Every load type
/// reduces to consistent nodal forces at the moment it is applied (a pressure, a traction, a
/// total force, gravity), so one model carries supports, loads and the modes computed from
/// it, and there is no second place for a load to be specified and forgotten. A thermal load
/// is the exception and is refused by name: it enters the static solve as an element integral
/// rather than a nodal force, so accepting it here would silently drop it.</para>
///
/// <para><b>Rigid-body modes are refused rather than superposed.</b> A free-free body's
/// response to a harmonic force is unbounded as the frequency goes to zero (the body simply
/// accelerates away), so a rigid mode's <c>1/(0 - W²)</c> term is a real physical statement
/// and a useless one to plot: the answer is a rigid-body acceleration, not a vibration.</para>
/// </summary>
public static class HarmonicSolver
{
    /// <summary>Computes the steady-state response over a frequency sweep.</summary>
    /// <param name="modes">A completed modal solve — the basis, and the model the load comes
    /// from.</param>
    /// <param name="options">The sweep, the damping, and optionally a static solve for the
    /// truncation correction.</param>
    public static HarmonicResponse Solve(ModalResults modes, HarmonicSolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(modes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Damping);
        ArgumentNullException.ThrowIfNull(options.Frequencies);
        if (options.Frequencies.Count == 0)
            throw new ArgumentException(
                "A harmonic solve needs at least one frequency to evaluate at.",
                nameof(options));
        foreach (double f in options.Frequencies)
        {
            if (f < 0 || double.IsNaN(f))
                throw new ArgumentException(
                    $"Frequencies must be non-negative and finite; {f} was given.",
                    nameof(options));
        }

        var model = modes.Model;
        RequireLoad(model);
        RequireNoModelDamping(model);
        RequireNoRigidModes(modes);
        var correction = options.StaticCorrection;
        if (correction is not null)
            RequireMatchingStatic(model, correction);

        int modeCount = modes.Modes.Count;
        var ratios = new double[modeCount];
        var modalForces = new double[modeCount];
        for (int i = 0; i < modeCount; i++)
        {
            var mode = modes.Modes[i];
            ratios[i] = options.Damping.RatioForMode(mode.Number, mode.AngularFrequency);
            if (ratios[i] < 0)
                throw new FeaException(
                    $"The damping model returned a negative ratio ({ratios[i]:G6}) for mode "
                    + $"{mode.Number} at {mode.Frequency:N2} Hz. A negative damping ratio adds "
                    + "energy at every cycle, so the steady state it describes does not exist; "
                    + "if this came from RayleighDamping, its coefficients were built by hand "
                    + "rather than through FromRatios, which refuses the same thing up front.");

            // F_n = phi_n' f. The shape is zero at every restrained degree of freedom by
            // construction, so a load applied at a support contributes nothing - which is
            // correct, since a support does no work.
            double f = 0;
            for (int node = 0; node < model.Mesh.NodeCount; node++)
            {
                var force = model.ForceOf(node);
                // Exact-zero skip: most nodes carry no load at all.
                if (force.X == 0 && force.Y == 0 && force.Z == 0)
                    continue;
                var shape = mode.ShapeAt(node);
                f += shape.X * force.X + shape.Y * force.Y + shape.Z * force.Z;
            }
            modalForces[i] = f;
        }

        var coordinates = new Complex[options.Frequencies.Count][];
        for (int k = 0; k < options.Frequencies.Count; k++)
        {
            double omega = 2.0 * Math.PI * options.Frequencies[k];
            var row = new Complex[modeCount];
            for (int i = 0; i < modeCount; i++)
            {
                var mode = modes.Modes[i];
                double wn = mode.AngularFrequency;
                var denominator = new Complex(
                    wn * wn - omega * omega, 2.0 * ratios[i] * wn * omega);
                // An undamped mode driven at EXACTLY its own frequency has a zero denominator
                // and no steady state at all. That is the correct answer rather than a case
                // to guard against, so the division is left alone: it produces a non-finite
                // value (.NET's complex division returns NaN for a finite numerator over an
                // exactly zero denominator, not an infinity), and clamping it to a large
                // number nobody chose would be a quiet claim that a steady state exists.
                row[i] = modalForces[i] / denominator;
            }
            coordinates[k] = row;
        }

        Vector3d[]? staticDisplacement = null;
        double truncation = double.NaN;
        if (correction is not null)
        {
            staticDisplacement = [.. correction.Displacement];
            truncation = TruncationError(modes, modalForces, staticDisplacement);
        }

        return new HarmonicResponse(
            modes, [.. options.Frequencies], ratios, modalForces, coordinates,
            staticDisplacement, truncation, options.Damping.Describe());
    }

    /// <summary>
    /// The relative difference at ZERO frequency between the modal sum and the true static
    /// answer — i.e. exactly the flexibility the extracted modes leave out.
    ///
    /// <para>It is the honest measure of modal truncation and it is only computable when a
    /// static solve was supplied, which is why <see cref="HarmonicResponse.TruncationError"/>
    /// is NaN without one rather than an optimistic zero.</para>
    /// </summary>
    private static double TruncationError(
        ModalResults modes, double[] modalForces, Vector3d[] exact)
    {
        int nodes = modes.Mesh.NodeCount;
        var approximate = new Vector3d[nodes];
        for (int i = 0; i < modes.Modes.Count; i++)
        {
            var mode = modes.Modes[i];
            double scale = modalForces[i] / mode.Eigenvalue;
            for (int v = 0; v < nodes; v++)
                approximate[v] += mode.ShapeAt(v) * scale;
        }

        double worst = 0, peak = 0;
        for (int v = 0; v < nodes; v++)
        {
            peak = Math.Max(peak, exact[v].Length);
            worst = Math.Max(worst, (approximate[v] - exact[v]).Length);
        }
        // Exact-zero division guard: a load case with no displacement anywhere has already
        // been refused (a model with no load), so this is a guard rather than a case.
        return peak > 0 ? worst / peak : 0;
    }

    // Internal so DirectHarmonicSolver asks the SAME rule rather than restating it —
    // restating a shared test is how three recorded defects happened.
    internal static void RequireLoad(StructuralModel model)
    {
        if (model.ThermalDeltaT is not null)
            throw new FeaException(
                "The model carries a thermal load, and a harmonic analysis has nowhere to put "
                + "it: a thermal strain enters a static solve as an ELEMENT integral rather than "
                + "as a nodal force, so it cannot be projected onto a mode shape and accepting "
                + "it here would silently drop it. A thermal field is also a steady load rather "
                + "than an oscillating one. Build the harmonic model with the mechanical loads "
                + "only, and use ModalSolveOptions.Prestress if the thermal stress is meant to "
                + "stiffen the structure.");

        for (int node = 0; node < model.Mesh.NodeCount; node++)
        {
            var force = model.ForceOf(node);
            // Exact-zero semantic test: a model with no loads produces a response that is
            // identically zero at every frequency, which is not an answer worth returning.
            if (force.X != 0 || force.Y != 0 || force.Z != 0)
                return;
        }

        throw new FeaException(
            "The model carries no applied force, so the harmonic response would be exactly zero "
            + "at every frequency. A modal analysis ignores loads - it is a property of the "
            + "structure - so a model built for one often has none; apply the excitation "
            + "(Force, Pressure, Traction or NodalForce) to the SAME model, which is the model "
            + "this response reads its load from.");
    }

    /// <summary>
    /// Refuses a model carrying its own damping declarations — silently ignoring them
    /// would be worse than either honest answer. If the model's damping is
    /// non-proportional (a dashpot, differing per-region values), this route structurally
    /// CANNOT integrate it: the undamped modes stop diagonalising C and the per-mode
    /// scalar oscillators this method is made of no longer exist. If it is proportional,
    /// the caller has stated damping in two places (the model and
    /// <see cref="HarmonicSolveOptions.Damping"/>) and this method would apply only one of
    /// them. Either way the refusal names the solver that reads the model's own damping.
    /// </summary>
    private static void RequireNoModelDamping(StructuralModel model)
    {
        if (!model.HasDamping && !model.HasLossFactor)
            return;
        throw new FeaException(
            $"The model carries its own damping ({model.DampingDescription}), and modal "
            + "superposition cannot integrate it: this route's damping is the per-mode ratios "
            + "in HarmonicSolveOptions.Damping, which is complete only for proportional viscous "
            + "damping stated nowhere else. A model-carried C is non-proportional in general, "
            + "and a structural loss factor (eta·K, frequency-independent) has no per-mode real "
            + "ratio at all off resonance. Solve this model with DirectHarmonicSolver, which "
            + "assembles and factors the model's own damping — or state ratios on a model that "
            + "carries no damping declarations of its own.");
    }

    private static void RequireNoRigidModes(ModalResults modes)
    {
        if (modes.RigidBodyModes.Count == 0)
            return;
        throw new FeaException(
            $"The modal solve found {modes.RigidBodyModes.Count} rigid-body mode"
            + $"{(modes.RigidBodyModes.Count == 1 ? "" : "s")} "
            + $"({string.Join("; ", modes.RigidBodyModes.Select(m => m.Description))}), and modal "
            + "superposition has no term for one: its contribution is F_n/(0 - W²), which grows "
            + "without bound as the frequency falls because an unrestrained body under a "
            + "harmonic force simply accelerates away. That is a real statement about the "
            + "structure and a useless one to plot. Restrain the body, or ask a rigid-body "
            + "dynamics question rather than a vibration one.");
    }

    private static void RequireMatchingStatic(StructuralModel model, StructuralResults statics)
    {
        if (!ReferenceEquals(statics.Model, model))
            throw new FeaException(
                "HarmonicSolveOptions.StaticCorrection was solved on a different StructuralModel "
                + "instance from the one the modes came from. The correction is the difference "
                + "between the true static response to THIS load and the modal sum of it, so a "
                + "static solve of a different load - or of the same load under different "
                + "supports - would silently subtract the wrong thing. Solve the same model "
                + "statically and pass those results.");
        if (!statics.Report.Converged)
            throw new FeaException(
                "HarmonicSolveOptions.StaticCorrection did not converge (relative residual "
                + $"{statics.Report.RelativeResidual:E2}), so it is not the static response the "
                + "correction subtracts. Solve it with FeaSolveMethod.Direct, or tighten the CG "
                + "tolerance.");
    }
}
