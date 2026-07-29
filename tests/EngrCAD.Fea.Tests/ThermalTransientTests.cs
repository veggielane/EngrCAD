using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Transient conduction: the lumped-capacitance exponential, the semi-infinite erfc
/// profile, the time-stepping ORDER of each scheme, and the two structural claims the
/// solver makes about a constant step — that it factors once, and that the first law holds
/// across the whole run.
/// </summary>
public class ThermalTransientTests(ITestOutputHelper output)
{
    /// <summary>
    /// Round numbers on purpose: <c>rho·c = 8e-9 · 5e8 = 4.0</c> mJ/(mm³·K) and
    /// <c>alpha = k/(rho·c) = 40/4 = 10</c> mm²/s exactly, so every time constant below is
    /// arithmetic rather than bookkeeping.
    /// </summary>
    private static readonly Material Metal = new(
        "transient metal", 200_000, 0.3, 8e-9,
        thermalConductivity: 40.0, specificHeat: 5e8);

    private const double Alpha = 10.0;   // k / (rho.c), mm^2/s

    /// <summary>
    /// The error function, Abramowitz and Stegun 7.1.26 — a rational-times-Gaussian fit
    /// with <c>|error| &lt;= 1.5e-7</c>, which is four orders below the discretization
    /// error it is compared against here, so it is not the limiter. (The BCL has no
    /// <c>erf</c>, and the alternative — a series good to machine precision — would be more
    /// code for accuracy nothing needs.)
    /// </summary>
    private static double Erf(double x)
    {
        const double p = 0.3275911;
        const double a1 = 0.254829592, a2 = -0.284496736, a3 = 1.421413741;
        const double a4 = -1.453152027, a5 = 1.061405429;
        double sign = Math.Sign(x);
        x = Math.Abs(x);
        double t = 1.0 / (1.0 + p * x);
        double poly = t * (a1 + t * (a2 + t * (a3 + t * (a4 + t * a5))));
        return sign * (1.0 - poly * Math.Exp(-x * x));
    }

    // ---- lumped capacitance ----------------------------------------------------------

    /// <summary>
    /// A small-Biot body cooling by convection decays exponentially:
    /// <c>T(t) = T_inf + (T0 - T_inf)·exp(-t/tau)</c> with <c>tau = rho·c·V/(h·A)</c>.
    ///
    /// <para>This is the whole capacity formulation in one number. The time constant is the
    /// ratio of the capacity matrix's total to the convection matrix's total, so a capacity
    /// off by any factor — a missing density, a specific heat in the wrong unit system, a
    /// quadrature rule two degrees too low — moves the measured decay by exactly that
    /// factor and nothing else changes.</para>
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void LumpedCapacitance_DecaysExponentially(ElementOrder order)
    {
        const double side = 10, initial = 200, ambient = 20, film = 0.05;
        double volume = side * side * side, area = 6 * side * side;
        double tau = Metal.VolumetricHeatCapacity * volume / (film * area);
        double characteristic = volume / area;
        double biot = film * characteristic / Metal.ThermalConductivity;

        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(side, side, side), 3, 3, 3);
        var mesh = order == ElementOrder.Linear
            ? AnalysisMesh.Of(tets)
            : AnalysisMesh.Quadratic(tets);
        var model = new ThermalModel(mesh, Metal).Convection(Facets.All, film, ambient);

        // Crank-Nicolson: the transient is smooth (the initial condition is uniform and the
        // only forcing is a film), so there is no step change to ring on, and its second
        // order buys accuracy the check can then attribute to the physics.
        var transient = new ThermalTransientOptions(tau / 40, 200)
        {
            Scheme = ThermalTimeScheme.CrankNicolson,
            InitialTemperature = initial,
            StoreEvery = 20,
        };
        var run = ThermalSolver.SolveTransient(model, transient);

        output.WriteLine(
            $"{order}: tau = rho.c.V/(h.A) = {tau:G8} s, Biot = h.(V/A)/k = {biot:E3}");
        output.WriteLine($"  {run.Report.Steps} steps of {transient.TimeStep:G6} s to "
            + $"t = {transient.Duration:G6} s ({transient.Duration / tau:F1} tau)");
        output.WriteLine($"  {run.Report.Factorizations} factorization for the whole run");
        output.WriteLine($"  first-law residual over the run {run.Report.EnergyBalanceResidual:E3}");
        output.WriteLine($"  {"t/tau",8} {"mean T",12} {"analytic",12} {"error",10} {"relative",10}");

        double worst = 0;
        foreach (var state in run.States)
        {
            // The body is essentially isothermal at this Biot number, so the mean over the
            // nodes is the number to compare; the spread is reported separately below.
            double mean = state.Temperature.Average();
            double analytic = ambient + (initial - ambient) * Math.Exp(-state.Time / tau);
            double error = Math.Abs(mean - analytic);
            worst = Math.Max(worst, error / (initial - ambient));
            output.WriteLine(
                $"  {state.Time / tau,8:F2} {mean,12:F5} {analytic,12:F5} {error,10:E2} "
                + $"{error / (initial - ambient),10:E2}");
        }

        double spread = run.Final.MaxTemperature - run.Final.MinTemperature;
        output.WriteLine(
            $"  worst deviation {worst:E3} of the initial excess; final internal spread "
            + $"{spread:E3} K (Biot predicts order {biot:E1})");

        Assert.Equal(1, run.Report.Factorizations);
        Assert.True(run.Report.EnergyBalanceResidual < 1e-10,
            $"first law {run.Report.EnergyBalanceResidual:E3}");
        // The bar is the Biot number's own order: a lumped model is exactly what a body
        // with an internal gradient is NOT, so agreeing to better than Bi would be luck.
        Assert.True(worst < 5 * biot, $"worst deviation {worst:E3} against 5.Bi = {5 * biot:E3}");
    }

    // ---- semi-infinite solid ---------------------------------------------------------

    /// <summary>
    /// A bar initially at <c>T0</c> whose face is suddenly held at <c>Ts</c> follows the
    /// semi-infinite solution <c>T(x,t) = Ts + (T0 - Ts)·erf(x / (2·sqrt(alpha·t)))</c>
    /// while the thermal penetration stays short of the far end.
    ///
    /// <para><b>The case backward Euler exists for.</b> The initial condition disagrees with
    /// the boundary condition at t = 0 — that is the whole problem — so the stiffest modes
    /// are excited at full amplitude on the first step, which is exactly where an
    /// A-stable-but-not-L-stable scheme rings. Solved here with backward Euler for that
    /// reason; the same run under Crank-Nicolson is the oscillation test below.</para>
    /// </summary>
    [Fact]
    public void SemiInfiniteSolid_MatchesTheErfcProfile()
    {
        const double length = 40, initial = 20, surface = 100;
        const double endTime = 2.0;
        double penetration = 4 * Math.Sqrt(Alpha * endTime);

        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(length, 4, 4), 40, 1, 1);
        var mesh = AnalysisMesh.Of(tets);
        var model = new ThermalModel(mesh, Metal)
            .Temperature(StructuredTetMesh.XMin, surface);

        const int steps = 400;
        var transient = new ThermalTransientOptions(endTime / steps, steps)
        {
            Scheme = ThermalTimeScheme.BackwardEuler,
            InitialTemperature = initial,
            StoreEvery = steps,
        };
        var run = ThermalSolver.SolveTransient(model, transient);

        output.WriteLine(
            $"alpha = {Alpha:G6} mm^2/s, t = {endTime:G6} s, penetration 4.sqrt(alpha.t) = "
            + $"{penetration:G6} mm in a {length:G6} mm bar");
        output.WriteLine(
            $"{steps} backward-Euler steps of {transient.TimeStep:G6} s, "
            + $"{run.Report.Factorizations} factorization, first-law residual "
            + $"{run.Report.EnergyBalanceResidual:E3}");
        output.WriteLine($"  {"x",6} {"FE",10} {"erfc solution",14} {"error",10}");

        var final = run.Final;
        double worst = 0;
        double denominator = 2 * Math.Sqrt(Alpha * endTime);
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            var p = mesh.Position(v);
            // One row of nodes is enough, and picking one keeps the table readable.
            if (p.Y != 0 || p.Z != 0)
                continue;
            double analytic = surface + (initial - surface) * Erf(p.X / denominator);
            double error = Math.Abs(final.TemperatureAt(v) - analytic);
            worst = Math.Max(worst, error);
            if (p.X <= 20 && Math.Abs(p.X % 2) < 1e-9)
            {
                output.WriteLine(
                    $"  {p.X,6:F1} {final.TemperatureAt(v),10:F4} {analytic,14:F4} {error,10:F4}");
            }
        }

        double span = surface - initial;
        output.WriteLine($"worst |FE - erfc| {worst:F4} K on an {span:G6} K step "
            + $"-> {worst / span:E3} relative");

        Assert.Equal(1, run.Report.Factorizations);
        Assert.True(run.Report.EnergyBalanceResidual < 1e-10,
            $"first law {run.Report.EnergyBalanceResidual:E3}");
        // The bar is the discretization's, not the erf fit's: h = 1 mm against a profile
        // whose scale is sqrt(alpha.t) = 4.5 mm, plus a first-order scheme at dt = 5 ms.
        Assert.True(worst / span < 0.02, $"{worst / span:E3}");
    }

    // ---- time-stepping order ---------------------------------------------------------

    /// <summary>
    /// The measured order in TIME of each scheme: backward Euler 1, Crank-Nicolson 2.
    ///
    /// <para><b>Measured against a reference solution of the SAME semi-discrete system</b>,
    /// not against an analytic answer. That is the point: comparing to the lumped
    /// exponential would fold the SPATIAL discretization into the error and cap the measured
    /// order at whatever the mesh contributes — the same trap the manufactured-solution
    /// study exists to avoid, one dimension over. Refining only the step and comparing to a
    /// far smaller step isolates the time integration exactly.</para>
    ///
    /// <para><b>The step range has to put EVERY mode in the asymptotic regime, and the
    /// first version of this test did not.</b> A conduction system's fastest mode is the
    /// internal one, roughly <c>alpha/h²</c>, and a scheme is only at its own order once
    /// <c>lambda·dt</c> is small for that mode too. Run over a full time constant with
    /// eight steps, Crank-Nicolson reported orders of 1.80, 3.72 and 3.21 across one
    /// refinement sequence — not noise but a genuine sign change in the error, the coarse
    /// steps being far outside the regime where an order means anything. Over a quarter of
    /// the time constant with the step small against <c>h²/alpha</c>, it measures 2.</para>
    /// </summary>
    [Theory]
    [InlineData(ThermalTimeScheme.BackwardEuler, 1.0)]
    [InlineData(ThermalTimeScheme.CrankNicolson, 2.0)]
    public void TimeStepping_ConvergesAtTheSchemeOrder(ThermalTimeScheme scheme, double theory)
    {
        const double side = 10, initial = 200, ambient = 20, film = 0.05;
        const int divisions = 2;
        double volume = side * side * side, area = 6 * side * side;
        double tau = Metal.VolumetricHeatCapacity * volume / (film * area);
        double endTime = tau / 4;
        double h = side / divisions;
        double fastMode = Alpha / (h * h);   // the internal conduction rate, 1/s

        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(side, side, side), divisions, divisions, divisions);
        var mesh = AnalysisMesh.Of(tets);

        double[] Run(int steps)
        {
            var model = new ThermalModel(mesh, Metal).Convection(Facets.All, film, ambient);
            var options = new ThermalTransientOptions(endTime / steps, steps)
            {
                Scheme = scheme,
                InitialTemperature = initial,
                StoreEvery = steps,
            };
            return [.. ThermalSolver.SolveTransient(model, options).Final.Temperature];
        }

        var reference = Run(4096);
        output.WriteLine(
            $"{scheme}: box cooling from {initial} to {ambient} C, tau = {tau:G6} s, run to "
            + $"{endTime:G6} s, reference at {endTime / 4096:G6} s");
        output.WriteLine(
            $"  fastest mode alpha/h^2 = {fastMode:G4} /s, so the asymptotic regime needs "
            + $"dt well under {1 / fastMode:G4} s");
        output.WriteLine($"  {"steps",8} {"dt",12} {"lambda.dt",10} {"max |T - ref|",14} {"ratio",8} {"order",8}");

        double previous = 0, lastOrder = 0;
        foreach (int steps in new[] { 32, 64, 128, 256 })
        {
            var solution = Run(steps);
            double worst = 0;
            for (int v = 0; v < solution.Length; v++)
                worst = Math.Max(worst, Math.Abs(solution[v] - reference[v]));

            string ratio = "-", orderText = "-";
            if (previous > 0)
            {
                lastOrder = Math.Log(previous / worst) / Math.Log(2);
                ratio = (previous / worst).ToString("F2");
                orderText = lastOrder.ToString("F2");
            }
            output.WriteLine(
                $"  {steps,8} {endTime / steps,12:G6} {fastMode * endTime / steps,10:F3} "
                + $"{worst,14:E3} {ratio,8} {orderText,8}");
            previous = worst;
        }

        output.WriteLine($"  measured order {lastOrder:F2} against theory {theory:F0}");
        Assert.InRange(lastOrder, theory - 0.25, theory + 0.35);
    }

    // ---- the two schemes' characters -------------------------------------------------

    /// <summary>
    /// The reason backward Euler is the default: on a STEP change in a boundary
    /// temperature, Crank-Nicolson's amplification factor for the stiffest modes approaches
    /// <b>-1</b>, so those modes alternate in sign instead of decaying. Backward Euler's
    /// approaches 0, which is what the physics does.
    ///
    /// <para><b>Two quantities are measured, because they say different things and the
    /// first version of this test conflated them.</b> RINGING — a node whose temperature
    /// reverses direction from step to step, when the physical answer is monotone heating —
    /// is the scheme's own defect and separates the two cleanly. UNDERSHOOT below the
    /// initial temperature is <i>not</i> a scheme defect: it is the CONSISTENT capacity
    /// matrix's, whose off-diagonal terms let a node's neighbours pull it below the range
    /// when the step is short against <c>h²/alpha</c>, and BOTH schemes show it. Reporting
    /// the second as evidence for the first is how "backward Euler is monotone" gets
    /// written down and then measured at 5.8%.</para>
    /// </summary>
    [Fact]
    public void CrankNicolson_RingsOnAStepChangeWhereBackwardEulerDoesNot()
    {
        const double length = 40, initial = 20, surface = 100;
        double span = surface - initial;

        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(length, 4, 4), 40, 1, 1);
        var mesh = AnalysisMesh.Of(tets);

        (int Reversals, double Undershoot, double Worst, double Late) Measure(
            ThermalTimeScheme scheme, double step, int steps)
        {
            var model = new ThermalModel(mesh, Metal).Temperature(StructuredTetMesh.XMin, surface);
            var run = ThermalSolver.SolveTransient(
                model,
                new ThermalTransientOptions(step, steps)
                {
                    Scheme = scheme,
                    InitialTemperature = initial,
                    StoreEvery = 1,
                });

            // Heat only ever enters, from the hottest boundary there is, so the physical
            // answer warms monotonically at every node. A reversal is numerical.
            int reversals = 0;
            double undershoot = 0, worstBackward = 0, lateBackward = 0;
            for (int node = 0; node < mesh.NodeCount; node++)
            {
                var history = run.HistoryOf(node);
                foreach (double t in history)
                    undershoot = Math.Max(undershoot, initial - t);
                for (int i = 1; i < history.Length; i++)
                {
                    double move = history[i] - history[i - 1];
                    if (move >= 0)
                        continue;
                    // A backward move is numerical: heat only enters this bar.
                    // A tolerance on the SPAN, not on the values: a node that has settled
                    // wobbles in the last bits and that is not ringing.
                    if (-move > 1e-9 * span)
                        reversals++;
                    worstBackward = Math.Max(worstBackward, -move);
                    // "Late" = after the first few steps, which is what separates a single
                    // settling event from an oscillation that persists.
                    if (i >= 5)
                        lateBackward = Math.Max(lateBackward, -move);
                }
            }
            return (reversals, undershoot, worstBackward, lateBackward);
        }

        double h = length / 40;
        double diffusionStep = h * h / Alpha;   // the step at which lambda.dt is about 1
        output.WriteLine(
            $"quenched bar, {mesh.NodeCount} nodes, physical range [{initial}, {surface}] C; "
            + $"h = {h:G4} mm, so the stiffest mode's own time is h^2/alpha = "
            + $"{diffusionStep:G4} s");
        output.WriteLine("backward moves are numerical: heat only ever enters this bar.");
        output.WriteLine(
            $"  {"dt",7} {"lam.dt",7} {"scheme",16} {"moves",6} {"worst back",12} "
            + $"{"after step 5",13} {"undershoot",11}");

        double lateBe = 0, lateCn = 0, undershootBe = 0, undershootCn = 0;
        double smallStepBe = 0;
        foreach (double step in new[] { 2.0, 1.0, 0.5, 0.02, 0.005 })
        {
            var be = Measure(ThermalTimeScheme.BackwardEuler, step, 20);
            var cn = Measure(ThermalTimeScheme.CrankNicolson, step, 20);
            output.WriteLine(
                $"  {step,7:G4} {step / diffusionStep,7:F1} {"backward Euler",16} {be.Reversals,6} "
                + $"{be.Worst / span,12:P3} {be.Late / span,13:P4} {be.Undershoot / span,11:P3}");
            output.WriteLine(
                $"  {step,7:G4} {step / diffusionStep,7:F1} {"Crank-Nicolson",16} {cn.Reversals,6} "
                + $"{cn.Worst / span,12:P3} {cn.Late / span,13:P4} {cn.Undershoot / span,11:P3}");

            // The scheme comparison belongs to the STIFF regime, where lambda.dt is large
            // and the two amplification factors genuinely differ; the small steps are the
            // capacity matrix's regime and are reported beside it.
            if (step >= 0.5)
            {
                lateBe = Math.Max(lateBe, be.Late);
                lateCn = Math.Max(lateCn, cn.Late);
            }
            else
            {
                smallStepBe = Math.Max(smallStepBe, be.Undershoot);
            }
            undershootBe = Math.Max(undershootBe, be.Undershoot);
            undershootCn = Math.Max(undershootCn, cn.Undershoot);
        }

        output.WriteLine("");
        output.WriteLine(
            $"stiff regime (dt >= 0.5 s, lambda.dt >= 5): worst backward move after step 5 is "
            + $"backward Euler {lateBe / span:P4}, Crank-Nicolson {lateCn / span:P4}");
        output.WriteLine(
            $"small-step regime (dt <= 0.02 s): backward Euler still undershoots by "
            + $"{smallStepBe / span:P3} - that is the CONSISTENT CAPACITY, not the scheme");

        // The scheme's own property, in the regime where the schemes differ: with the step
        // long against h^2/alpha, backward Euler kills the stiff modes in one step and never
        // moves backwards again, while Crank-Nicolson's amplification approaches -1 and it
        // keeps ringing.
        Assert.Equal(0.0, lateBe);
        Assert.True(lateCn / span > 0.001,
            $"Crank-Nicolson's late backward move is only {lateCn / span:P4}, so this test does "
            + "not demonstrate the ringing it claims");

        // ...and the honest counterweight: at a SHORT step both schemes undershoot, because
        // that is the consistent capacity matrix and not the time integration. Asserted so
        // the distinction cannot quietly be re-attributed to the scheme.
        Assert.True(smallStepBe / span > 0.01,
            $"backward Euler undershot by only {smallStepBe / span:P3} at a short step; the "
            + "consistent-capacity undershoot documented here is not present, so the note "
            + "beside it is stale");
    }

    /// <summary>
    /// A transient with a generation source reaches the STEADY answer as time passes — the
    /// check that the two solvers agree, and the one that would catch a capacity matrix
    /// polluting the steady operator or a load applied at the wrong point in the step.
    /// </summary>
    [Fact]
    public void Transient_ApproachesTheSteadySolution()
    {
        const double side = 20, ambient = 25, film = 0.01, power = 40.0;
        double volume = side * side * side;

        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(side, side, side), 3, 3, 3);
        var mesh = AnalysisMesh.Of(tets);

        ThermalModel Build() => new ThermalModel(mesh, Metal)
            .Generation(power / volume)
            .Convection(Facets.All, film, ambient);

        var steady = ThermalSolver.Solve(Build());
        double tau = Metal.VolumetricHeatCapacity * volume / (film * 6 * side * side);
        var run = ThermalSolver.SolveTransient(
            Build(),
            new ThermalTransientOptions(tau / 20, 400)
            {
                Scheme = ThermalTimeScheme.BackwardEuler,
                InitialTemperature = ambient,
                StoreEvery = 100,
            });

        double worst = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
            worst = Math.Max(worst, Math.Abs(run.Final.TemperatureAt(v) - steady.TemperatureAt(v)));
        double rise = steady.MaxTemperature - ambient;

        output.WriteLine($"tau = {tau:G6} s, run to {run.Report.Duration:G6} s = "
            + $"{run.Report.Duration / tau:F0} tau");
        output.WriteLine(
            $"steady {steady.MinTemperature:F6} to {steady.MaxTemperature:F6} C; "
            + $"transient final {run.Final.MinTemperature:F6} to {run.Final.MaxTemperature:F6} C");
        output.WriteLine(
            $"worst |transient - steady| {worst:E3} K on a {rise:G6} K rise "
            + $"-> {worst / rise:E3} relative (exp(-20) = {Math.Exp(-20):E2})");
        output.WriteLine($"first-law residual over the run {run.Report.EnergyBalanceResidual:E3}");

        Assert.Equal(1, run.Report.Factorizations);
        Assert.True(worst / rise < 1e-6, $"{worst / rise:E3}");
        Assert.True(run.Report.EnergyBalanceResidual < 1e-10);
    }

    /// <summary>
    /// The stored states are what they claim: the initial condition applies to the FREE
    /// nodes while a prescribed temperature wins at t = 0, <c>StoreEvery</c> is honoured,
    /// and the final step is always stored whatever it was.
    /// </summary>
    [Fact]
    public void TransientStates_ApplyBoundaryConditionsAtTimeZeroAndHonourStoreEvery()
    {
        const double initial = 20, surface = 100;
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(20, 4, 4), 10, 1, 1);
        var mesh = AnalysisMesh.Of(tets);
        var model = new ThermalModel(mesh, Metal).Temperature(StructuredTetMesh.XMin, surface);

        var run = ThermalSolver.SolveTransient(
            model,
            new ThermalTransientOptions(0.01, 25)
            {
                InitialTemperature = initial,
                StoreEvery = 10,
            });

        // Steps 0, 10, 20 and the final 25 — which is NOT a multiple of StoreEvery, so it
        // is stored because it is the last, not because it landed on the stride.
        Assert.Equal(4, run.Count);
        Assert.Equal([0, 0.1, 0.2, 0.25], run.Times.Select(t => Math.Round(t, 10)));

        var prescribedNodes = model.NodesOn(Facets.Tag(StructuredTetMesh.XMin)).ToHashSet();
        foreach (var state in run.States)
        {
            for (int node = 0; node < mesh.NodeCount; node++)
            {
                if (prescribedNodes.Contains(node))
                    Assert.Equal(surface, state.TemperatureAt(node), 10);
            }
        }
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            if (!prescribedNodes.Contains(node))
                Assert.Equal(initial, run.Initial.TemperatureAt(node), 12);
        }
        output.WriteLine(
            $"t = 0: free nodes at {initial} C, the prescribed face already at {surface} C");

        var history = run.HistoryOf(model.NodesOn(Facets.Tag(StructuredTetMesh.XMax))[0]);
        output.WriteLine(
            $"far-end history: {string.Join(", ", history.Select(h => h.ToString("F6")))}");

        // The far end warms, but only ALMOST monotonically: the consistent capacity matrix
        // lets a node dip below its neighbours' pull when the step is short against
        // h^2/alpha, and the far end of a 20 mm bar has barely heard about the quench after
        // 0.25 s. The bar is a fraction of the 80 K step, not zero, and the dip is reported
        // rather than asserted away.
        double worstDip = 0;
        for (int i = 1; i < history.Length; i++)
            worstDip = Math.Max(worstDip, history[i - 1] - history[i]);
        output.WriteLine(
            $"worst step-to-step dip {worstDip:E3} K on an {surface - initial:G4} K step "
            + $"-> {worstDip / (surface - initial):E2} (the consistent capacity's undershoot)");
        Assert.True(worstDip / (surface - initial) < 1e-5, $"{worstDip:E3}");
    }
}
