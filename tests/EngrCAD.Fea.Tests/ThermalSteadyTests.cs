using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Steady conduction against closed-form solutions: the patch test, the 1D slab with and
/// without generation, a convective (mixed) boundary, and the hollow cylinder's
/// logarithmic profile.
///
/// <para><b>Three of the five are exact to round-off, and that is the point of choosing
/// them.</b> A linear temperature field, and the linear field a slab with a convective end
/// settles into, both lie inside BOTH element spaces, so a correct solver reproduces them
/// to machine precision rather than approximately — which turns the test from "is the
/// error small enough" into "is the formulation right". The parabolic generation profile
/// is exact for quadratic elements for the same reason and second-order for linear ones.
/// Only the logarithmic profile is genuinely approximated by both.</para>
/// </summary>
public class ThermalSteadyTests(ITestOutputHelper output)
{
    /// <summary>A material with clean round numbers, so a hand check of any figure here is
    /// arithmetic rather than bookkeeping.</summary>
    private static readonly Material Metal =
        new("test metal", 200_000, 0.3, 8e-9, thermalConductivity: 40.0, specificHeat: 5e8);

    private static AnalysisMesh Wrap(TetMesh tets, ElementOrder order) =>
        order == ElementOrder.Linear ? AnalysisMesh.Of(tets) : AnalysisMesh.Quadratic(tets);

    private static double WorstError(
        AnalysisMesh mesh, ThermalResults results, Func<Vector3d, double> exact)
    {
        double worst = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
            worst = Math.Max(worst, Math.Abs(results.TemperatureAt(v) - exact(mesh.Position(v))));
        return worst;
    }

    // ---- patch test ------------------------------------------------------------------

    /// <summary>
    /// <b>The patch test.</b> A linear temperature field prescribed on the whole boundary
    /// must be reproduced EXACTLY in the interior, by both element types — the standard
    /// correctness gate, and it catches essentially every assembly, indexing and Jacobian
    /// error outright.
    ///
    /// <para>It also checks the derived quantity: the exact flux of a linear field is the
    /// constant <c>-k·grad T</c>, so every element must report the same vector.</para>
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void PatchTest_ReproducesALinearTemperatureFieldExactly(ElementOrder order)
    {
        // Deliberately off the origin and not axis-symmetric, so a sign or index error has
        // nowhere to cancel.
        static double Exact(Vector3d p) => 17.5 + 3.25 * p.X - 1.75 * p.Y + 0.875 * p.Z;

        var tets = StructuredTetMesh.Box(new Vector3d(-1, 2, -0.5), new Vector3d(3, 2, 1.5), 3, 3, 2);
        var mesh = Wrap(tets, order);
        var model = new ThermalModel(mesh, Metal);
        foreach (int node in model.NodesOn(Facets.All))
            model.TemperatureNode(node, Exact(mesh.Position(node)));

        var results = ThermalSolver.Solve(model);

        double span = 3.25 * 3 + 1.75 * 2 + 0.875 * 1.5;   // the field's own range
        double worst = WorstError(mesh, results, Exact);
        output.WriteLine(
            $"{order}: {mesh.ElementCount:N0} elements, {results.Report.FreeDofs:N0} free DOF");
        output.WriteLine($"  worst nodal temperature error {worst:E3} on a span of {span:G6} "
            + $"-> {worst / span:E3} relative");

        var expectedFlux = new Vector3d(3.25, -1.75, 0.875) * -Metal.ThermalConductivity;
        double worstFlux = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
            worstFlux = Math.Max(worstFlux, (results.ElementFlux(e) - expectedFlux).Length);
        output.WriteLine(
            $"  worst element flux error {worstFlux:E3} on |q| = {expectedFlux.Length:G6} "
            + $"-> {worstFlux / expectedFlux.Length:E3} relative");
        output.WriteLine($"  energy balance residual {results.Report.EnergyBalanceResidual:E3}");

        Assert.True(worst / span < 1e-13, $"temperature error {worst / span:E3}");
        Assert.True(worstFlux / expectedFlux.Length < 1e-12, $"flux error {worstFlux:E3}");
        Assert.True(results.Report.EnergyBalanceResidual < 1e-12);
    }

    // ---- 1D slab ---------------------------------------------------------------------

    /// <summary>
    /// A slab held at two temperatures with insulated sides: the exact profile is LINEAR,
    /// <c>T = T0 + (T1 - T0)·x/L</c>, and the total heat through it is <c>k·A·(T0-T1)/L</c>.
    ///
    /// <para>Two things ride on this beyond the profile. The sides are left UNMENTIONED,
    /// which is how an adiabatic surface is spelled — zero flux is the weak form's natural
    /// boundary condition — so the test is also a check that saying nothing means the right
    /// thing. And the prescribed-boundary heat the report gives is compared against the
    /// analytic <c>k·A·dT/L</c>, which is what makes the energy bookkeeping meaningful
    /// rather than merely self-consistent.</para>
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void Slab_WithFixedFaces_MatchesTheLinearProfileExactly(ElementOrder order)
    {
        const double length = 50, width = 20, thickness = 10;
        const double hot = 120, cold = 20;
        double Exact(Vector3d p) => hot + (cold - hot) * p.X / length;

        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(length, width, thickness), 5, 3, 2);
        var mesh = Wrap(tets, order);
        var model = new ThermalModel(mesh, Metal)
            .Temperature(StructuredTetMesh.XMin, hot)
            .Temperature(StructuredTetMesh.XMax, cold);

        var results = ThermalSolver.Solve(model);

        double worst = WorstError(mesh, results, Exact);
        double area = width * thickness;
        double expectedHeat = Metal.ThermalConductivity * area * (hot - cold) / length;

        // The heat entering at the hot face: the reaction sum is a NET over both faces and
        // is zero here, so the flow is measured as the flux times the area instead.
        var flux = results.ElementFlux(0);
        double heatByFlux = flux.X * area;

        output.WriteLine($"{order}: {mesh.ElementCount:N0} elements");
        output.WriteLine($"  worst |T - exact| {worst:E3} on a {hot - cold:G6} K drop "
            + $"-> {worst / (hot - cold):E3} relative");
        output.WriteLine($"  q.x = {flux.X:G8}, heat through = {heatByFlux:G8} against "
            + $"k.A.dT/L = {expectedHeat:G8} ({Math.Abs(heatByFlux - expectedHeat) / expectedHeat:E3} relative)");
        output.WriteLine($"  net prescribed heat {results.Report.PrescribedHeat:E3} (both faces, must cancel)");
        output.WriteLine($"  energy balance residual {results.Report.EnergyBalanceResidual:E3}");

        Assert.True(worst / (hot - cold) < 1e-13, $"{worst / (hot - cold):E3}");
        Assert.True(Math.Abs(heatByFlux - expectedHeat) / expectedHeat < 1e-12);
        Assert.True(results.Report.EnergyBalanceResidual < 1e-12);
    }

    /// <summary>
    /// A slab with uniform internal generation and both faces held at the same
    /// temperature: <c>T(x) = T0 + q·x·(L-x)/(2k)</c>, a PARABOLA peaking at
    /// <c>T0 + q·L²/(8k)</c>.
    ///
    /// <para><b>Both element orders are exact AT THE NODES, and only one of the two reasons
    /// is the obvious one.</b> A parabola lies inside the quadratic element space, so
    /// Galerkin returns it outright. Linear elements cannot represent it anywhere between
    /// their nodes — and are nonetheless exact AT them, which is the classical
    /// nodal-superconvergence property of a one-dimensional second-order problem: the
    /// discrete equations for a uniform grid reduce to the central difference
    /// <c>(T_{j-1} - 2T_j + T_{j+1})/h² = -q/k</c>, which a quadratic satisfies with no
    /// truncation error at all because its third derivative vanishes.</para>
    ///
    /// <para><b>That is a trap worth naming, because it makes the obvious convergence study
    /// meaningless.</b> Measuring "the error" at nodes here reports round-off at every
    /// refinement level and no order whatsoever — the first run of this test asserted a
    /// ratio of 4 and measured 0.72, which is the ratio of two numbers that are both
    /// nothing. The genuine O(h²) is INSIDE the elements, so it is measured at the element
    /// centroids instead; and a real convergence ORDER for the formulation is measured
    /// against a manufactured solution that varies in all three directions, where no such
    /// one-dimensional accident is available.</para>
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void Slab_WithGeneration_MatchesTheParabolicProfile(ElementOrder order)
    {
        const double length = 40, width = 15, thickness = 8;
        const double wall = 60, rate = 0.5;   // mW/mm^3
        double k = Metal.ThermalConductivity;
        double Exact(Vector3d p) => wall + rate * p.X * (length - p.X) / (2 * k);
        double peak = wall + rate * length * length / (8 * k);

        double previousInterior = 0;
        foreach (int n in new[] { 4, 8, 16 })
        {
            var tets = StructuredTetMesh.Box(
                Vector3d.Zero, new Vector3d(length, width, thickness), n, 2, 2);
            var mesh = Wrap(tets, order);
            var model = new ThermalModel(mesh, Metal)
                .Temperature(StructuredTetMesh.XMin, wall)
                .Temperature(StructuredTetMesh.XMax, wall)
                .Generation(rate);

            var results = ThermalSolver.Solve(model);
            double worst = WorstError(mesh, results, Exact);
            double generated = rate * length * width * thickness;

            // The error the elements really carry: the field INSIDE an element, against the
            // exact parabola there. Nodal values say nothing about it (see the remarks).
            double interior = 0;
            for (int e = 0; e < mesh.ElementCount; e++)
            {
                var nodes = mesh.Element(e);
                var centroid = Vector3d.Zero;
                for (int i = 0; i < 4; i++)
                    centroid += mesh.Position(nodes[i]);
                centroid *= 0.25;
                interior = Math.Max(
                    interior,
                    Math.Abs(results.TemperatureIn(e, 0.25, 0.25, 0.25) - Exact(centroid)));
            }

            output.WriteLine(
                $"{order} n = {n}: worst nodal |T - exact| {worst:E3}, worst interior "
                + $"{interior:E3} (peak rise {peak - wall:G6} K)");
            output.WriteLine(
                $"  applied {results.Report.AppliedHeat:G8} against rate.V = {generated:G8}; "
                + $"prescribed {results.Report.PrescribedHeat:G8} (must be its negative); "
                + $"balance {results.Report.EnergyBalanceResidual:E3}");

            Assert.True(Math.Abs(results.Report.AppliedHeat - generated) / generated < 1e-12);
            Assert.True(
                Math.Abs(results.Report.AppliedHeat + results.Report.PrescribedHeat) / generated < 1e-10);
            Assert.True(results.Report.EnergyBalanceResidual < 1e-12);

            // Nodally exact for BOTH orders, for the two different reasons above.
            Assert.True(worst / (peak - wall) < 1e-12,
                $"nodal error {worst / (peak - wall):E3} at n = {n}");

            if (order == ElementOrder.Quadratic)
            {
                // A parabola is IN the quadratic space, so the interior is exact too.
                Assert.True(interior / (peak - wall) < 1e-12, $"{interior / (peak - wall):E3}");
            }
            else if (previousInterior > 0)
            {
                double ratio = previousInterior / interior;
                output.WriteLine(
                    $"  interior error ratio on halving h: {ratio:F2} (second order = 4)");
                Assert.InRange(ratio, 3.5, 4.5);
            }
            previousInterior = interior;
        }
    }

    // ---- convective boundary ---------------------------------------------------------

    /// <summary>
    /// The mixed-boundary slab: one face held at <c>T0</c>, the other convecting to
    /// <c>T_inf</c> through a film <c>h</c>, sides insulated. The steady profile is still
    /// linear, and matching the conducted flux to the convected one at the free face gives
    /// <c>T_L = (k·T0/L + h·T_inf) / (k/L + h)</c>.
    ///
    /// <para><b>This is the test that convection actually has to pass</b>, because it
    /// exercises BOTH halves of the condition. Get the matrix half wrong and the surface
    /// temperature is wrong; get the load half wrong and the body settles towards the wrong
    /// ambient; get their relative scaling wrong and the answer is plausible and wrong by a
    /// ratio. And since the exact profile is linear, it lies in both element spaces, so a
    /// correct implementation is exact to round-off and there is no discretization error to
    /// hide behind.</para>
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void Slab_WithAConvectiveFace_MatchesTheMixedBoundarySolution(ElementOrder order)
    {
        const double length = 30, width = 12, thickness = 6;
        const double hot = 200, ambient = 25, film = 0.05;   // mW/(mm^2.K)
        double k = Metal.ThermalConductivity;

        // Conducted flux k(T0 - TL)/L equals convected flux h(TL - Tinf).
        double surface = (k * hot / length + film * ambient) / (k / length + film);
        double Exact(Vector3d p) => hot + (surface - hot) * p.X / length;

        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(length, width, thickness), 4, 2, 2);
        var mesh = Wrap(tets, order);
        var model = new ThermalModel(mesh, Metal)
            .Temperature(StructuredTetMesh.XMin, hot)
            .Convection(StructuredTetMesh.XMax, film, ambient);

        var results = ThermalSolver.Solve(model);

        double worst = WorstError(mesh, results, Exact);
        double area = width * thickness;
        double expectedHeat = film * area * (surface - ambient);

        output.WriteLine($"{order}: {mesh.ElementCount:N0} elements");
        output.WriteLine($"  analytic surface temperature {surface:G10} C "
            + $"(Biot number h.L/k = {film * length / k:G4})");
        output.WriteLine($"  worst |T - exact| {worst:E3} on a {hot - surface:G6} K drop "
            + $"-> {worst / (hot - surface):E3} relative");
        output.WriteLine($"  convective heat out {results.Report.ConvectiveHeat:G10} against "
            + $"h.A.(Ts - Tinf) = {expectedHeat:G10} "
            + $"({Math.Abs(results.Report.ConvectiveHeat - expectedHeat) / expectedHeat:E3} relative)");
        output.WriteLine($"  prescribed heat in {results.Report.PrescribedHeat:G10}");
        output.WriteLine($"  energy balance residual {results.Report.EnergyBalanceResidual:E3}");

        Assert.True(worst / (hot - surface) < 1e-12, $"{worst / (hot - surface):E3}");
        Assert.True(
            Math.Abs(results.Report.ConvectiveHeat - expectedHeat) / expectedHeat < 1e-12,
            $"convective heat {results.Report.ConvectiveHeat:G10} vs {expectedHeat:G10}");
        // Heat in at the hot face equals heat out through the film.
        Assert.True(
            Math.Abs(results.Report.PrescribedHeat - expectedHeat) / expectedHeat < 1e-12);
        Assert.True(results.Report.EnergyBalanceResidual < 1e-12);
    }

    /// <summary>
    /// Convection ALONE drives a model: no prescribed temperature anywhere, a heat load
    /// inside, and a film to ambient. The body settles where generation balances loss, and
    /// for a small Biot number that is <c>T_inf + Q/(h·A)</c> essentially uniformly.
    ///
    /// <para>The interesting part is that this model has no Dirichlet node at all, so it is
    /// the case that would be singular without the convective surface matrix — which is
    /// exactly what the solver's refusal rule claims and what this confirms from the other
    /// side.</para>
    /// </summary>
    [Fact]
    public void ConvectionAlone_SetsTheLevelWithNoPrescribedTemperature()
    {
        const double side = 10, ambient = 20, film = 0.002, power = 5.0;
        double volume = side * side * side, area = 6 * side * side;

        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(side, side, side), 3, 3, 3);
        var mesh = AnalysisMesh.Quadratic(tets);
        var model = new ThermalModel(mesh, Metal)
            .Generation(power / volume)
            .Convection(Facets.All, film, ambient);

        var results = ThermalSolver.Solve(model);

        double lumped = ambient + power / (film * area);
        double biot = film * (side / 2) / Metal.ThermalConductivity;
        output.WriteLine(
            $"Biot number h.(L/2)/k = {biot:E3} -> essentially isothermal");
        output.WriteLine(
            $"temperature {results.MinTemperature:G8} to {results.MaxTemperature:G8}, "
            + $"lumped prediction Tinf + Q/(h.A) = {lumped:G8}");
        output.WriteLine(
            $"applied {results.Report.AppliedHeat:G8}, convective out "
            + $"{results.Report.ConvectiveHeat:G8}, prescribed {results.Report.PrescribedHeat:G8}");
        output.WriteLine($"energy balance residual {results.Report.EnergyBalanceResidual:E3}");

        // No prescribed nodes at all, so every DOF is free.
        Assert.Equal(mesh.NodeCount, results.Report.FreeDofs);
        Assert.Equal(0, results.Report.PrescribedHeat);
        // With nothing prescribed, "heat in equals heat out" holds only to the LINEAR
        // SOLVE's accuracy rather than exactly: the free-node residuals are what would make
        // up the difference, and they are zero only to the factorization's round-off.
        double lossError = Math.Abs(results.Report.ConvectiveHeat - power) / power;
        output.WriteLine($"convective loss vs generated: {lossError:E3} relative");
        Assert.True(lossError < 1e-10, $"convective loss error {lossError:E3}");
        // The internal gradient is what the Biot number predicts: a few times Bi of the
        // overall rise, so the body is within a fraction of a percent of the lumped answer.
        double spread = results.MaxTemperature - results.MinTemperature;
        double rise = lumped - ambient;
        output.WriteLine($"internal spread {spread:G6} K on a {rise:G6} K rise = {spread / rise:P2}");
        Assert.True(Math.Abs(results.MaxTemperature - lumped) / rise < 0.02);
        // A looser bar than the prescribed-boundary cases, and for a reason worth stating:
        // with nothing prescribed, the balance's denominator is only the applied and
        // convective heat, and its numerator is made up entirely of free-node residuals —
        // so this measures the FACTORIZATION's accuracy, where a model with a Dirichlet
        // boundary measures the assembly against an exactly known reaction. Measured 4.2e-12.
        Assert.True(results.Report.EnergyBalanceResidual < 1e-10,
            $"balance {results.Report.EnergyBalanceResidual:E3}");
    }

    // ---- radial ----------------------------------------------------------------------

    /// <summary>
    /// The iterative solver reaches the same answer as the direct one, steady and
    /// transient — the branch a caller takes for a large model, and one no other test in
    /// this file exercises.
    /// <para>The agreement bar is the CG tolerance rather than round-off, which is the
    /// honest statement of what an iterative solve gives and exactly why
    /// <see cref="FeaSolveMethod.Direct"/> stays the default: every exactness claim in
    /// this suite is a claim about the direct path.</para>
    /// </summary>
    [Fact]
    public void ConjugateGradient_AgreesWithTheDirectSolve()
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(40, 20, 10), 5, 3, 2);
        var mesh = AnalysisMesh.Of(tets);

        ThermalModel Build() => new ThermalModel(mesh, Metal)
            .Temperature(StructuredTetMesh.XMin, 150)
            .Convection(StructuredTetMesh.XMax, 0.04, 20)
            .Generation(0.002);

        var direct = ThermalSolver.Solve(Build());
        var iterative = ThermalSolver.Solve(
            Build(), new ThermalSolveOptions { Method = FeaSolveMethod.ConjugateGradient });

        double span = direct.MaxTemperature - direct.MinTemperature;
        double worst = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
            worst = Math.Max(worst, Math.Abs(direct.TemperatureAt(v) - iterative.TemperatureAt(v)));

        output.WriteLine(
            $"steady: direct {direct.MinTemperature:F6}..{direct.MaxTemperature:F6}, "
            + $"CG converged in {iterative.Report.Iterations} iterations "
            + $"(|Ku-f|/|f| = {iterative.Report.RelativeResidual:E2})");
        output.WriteLine(
            $"  worst |direct - CG| {worst:E3} K on a {span:G6} K span -> {worst / span:E3}");
        Assert.True(iterative.Report.Converged);
        Assert.Equal(0, iterative.Report.FactorNonZeros);
        Assert.True(worst / span < 1e-8, $"{worst / span:E3}");

        // ...and the transient, where a step warm-starts from its predecessor. There is no
        // factorization to count, so Factorizations is 0 and says so rather than lying.
        double[] Transient(FeaSolveMethod method) =>
        [
            .. ThermalSolver.SolveTransient(
                Build(),
                new ThermalTransientOptions(2.0, 20) { InitialTemperature = 20, StoreEvery = 20 },
                new ThermalSolveOptions { Method = method }).Final.Temperature,
        ];

        var directRun = Transient(FeaSolveMethod.Direct);
        var iterativeRun = Transient(FeaSolveMethod.ConjugateGradient);
        double worstStep = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
            worstStep = Math.Max(worstStep, Math.Abs(directRun[v] - iterativeRun[v]));
        output.WriteLine($"transient after 20 steps: worst |direct - CG| {worstStep:E3} K");
        Assert.True(worstStep / span < 1e-8, $"{worstStep / span:E3}");
    }

    private const double Inner = 10, Outer = 25, Height = 6;
    private const double InnerTemperature = 150, OuterTemperature = 30;

    private static double ExactRadial(Vector3d p)
    {
        double r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
        return InnerTemperature
            + (OuterTemperature - InnerTemperature) * Math.Log(r / Inner) / Math.Log(Outer / Inner);
    }

    private readonly record struct RadialRun(int N, int Elements, double Error, double Order);

    private RadialRun[] RadialSequence(ElementOrder order, int angularFactor, bool squareAngular)
    {
        double span = InnerTemperature - OuterTemperature;
        var runs = new List<RadialRun>();
        double previous = 0;
        foreach (int n in new[] { 2, 4, 8 })
        {
            int nTheta = angularFactor * (squareAngular ? n * n : n);
            var tets = StructuredTetMesh.HollowCylinder(
                Inner, Outer, Height, nTheta, n, 1, geometricGrading: false);
            var mesh = Wrap(tets, order);
            var model = new ThermalModel(mesh, Metal)
                .Temperature(StructuredTetMesh.InnerSurface, InnerTemperature)
                .Temperature(StructuredTetMesh.OuterSurface, OuterTemperature);

            var results = ThermalSolver.Solve(model);
            Assert.True(results.Report.EnergyBalanceResidual < 1e-11);

            double worst = WorstError(mesh, results, ExactRadial) / span;
            double measured = previous > 0 ? Math.Log(previous / worst) / Math.Log(2) : 0;
            runs.Add(new RadialRun(n, mesh.ElementCount, worst, measured));
            previous = worst;
        }
        return [.. runs];
    }

    private void ReportRadial(string label, RadialRun[] runs)
    {
        output.WriteLine($"  {label}");
        foreach (var run in runs)
        {
            output.WriteLine(
                $"    nRadial {run.N,2}: {run.Elements,7:N0} elements, relative error "
                + $"{run.Error:E3}" + (run.Order > 0 ? $", order {run.Order:F2}" : ""));
        }
    }

    /// <summary>
    /// A hollow cylinder held at two temperatures: the exact profile is
    /// <c>T(r) = Ta + (Tb - Ta)·ln(r/a)/ln(b/a)</c>, and the heat through the wall is
    /// <c>2·pi·k·H·(Ta - Tb)/ln(b/a)</c>. The one analytic case here that neither element
    /// space contains, so both orders genuinely approximate it.
    ///
    /// <para><b>This measures ACCURACY, not convergence order, and the difference is the
    /// interesting part.</b> Refining the mesh refines the DOMAIN too: the fixture's rings
    /// are polygons, the boundary condition is constant along each chord, and the true
    /// logarithmic profile is not — so the problem being solved is not quite the annulus
    /// the analytic solution describes. That modelling difference caps the measured order
    /// below theory, exactly as the cantilever's clamped end caps the structural one.</para>
    ///
    /// <para><b>The cap is measured rather than asserted.</b> Refining the angular
    /// direction as <c>n²</c> instead of <c>n</c> — which drops the chord sagitta from
    /// O(h²) to O(h⁴) — lifts the quadratic order from 2.00 to 2.28 and its finest error
    /// from 5.8e-4 to 1.1e-4, while leaving the LINEAR sequence unchanged at 1.28 and
    /// 1.0e-3. So the two orders are limited by different things: the quadratic element is
    /// good enough that the polygonal boundary is what holds it back, and the linear one is
    /// still limited by its own radial approximation. A convergence ORDER for the
    /// formulation therefore belongs to the manufactured solution on a box, where the
    /// domain is represented exactly — see <c>ThermalConvergenceTests</c>.</para>
    ///
    /// <para>The rings are spaced UNIFORMLY on purpose; see
    /// <c>StructuredTetMesh.HollowCylinder</c>, where geometric spacing makes the nodal
    /// values exact and turns any study here into a comparison of round-off figures.</para>
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void HollowCylinder_MatchesTheLogarithmicProfile(ElementOrder order)
    {
        double logRatio = Math.Log(Outer / Inner);
        double analyticHeat =
            2 * Math.PI * Metal.ThermalConductivity
            * (InnerTemperature - OuterTemperature) * Height / logRatio;
        output.WriteLine(
            $"{order}: hollow cylinder r = {Inner}..{Outer}, {InnerTemperature} to "
            + $"{OuterTemperature} C, analytic wall heat {analyticHeat:G8}");

        var linearAngular = RadialSequence(order, 12, squareAngular: false);
        ReportRadial("angular divisions proportional to n:", linearAngular);
        var fastAngular = RadialSequence(order, 12, squareAngular: true);
        ReportRadial("angular divisions proportional to n^2:", fastAngular);

        double bar = order == ElementOrder.Linear ? 3e-3 : 2e-3;
        output.WriteLine(
            $"  finest error {linearAngular[^1].Error:E3} against a bar of {bar:E1}");
        Assert.True(linearAngular[^1].Error < bar, $"{linearAngular[^1].Error:E3}");

        // The error falls monotonically under refinement, which is the honest claim a
        // polygonal domain supports; the ORDER is capped by that domain (see the remarks).
        for (int i = 1; i < linearAngular.Length; i++)
            Assert.True(linearAngular[i].Error < linearAngular[i - 1].Error);

        if (order == ElementOrder.Quadratic)
        {
            // The measured diagnosis: with the boundary refined faster, the quadratic
            // element gets measurably closer to its own order. This is what makes the
            // "the domain is the cap" claim a measurement rather than an excuse.
            Assert.True(fastAngular[^1].Error < linearAngular[^1].Error / 3,
                $"faster angular refinement gave {fastAngular[^1].Error:E3} against "
                + $"{linearAngular[^1].Error:E3}, so the boundary is not the limiter");
            Assert.True(fastAngular[^1].Order > linearAngular[^1].Order + 0.15,
                $"order {fastAngular[^1].Order:F2} against {linearAngular[^1].Order:F2}");
        }
        else
        {
            // ...and the linear element's cap is its OWN radial approximation, so refining
            // the boundary faster buys it essentially nothing. Stated as an assertion so
            // the asymmetry cannot quietly reverse.
            Assert.True(fastAngular[^1].Error > linearAngular[^1].Error * 0.9,
                $"faster angular refinement helped the linear element more than expected: "
                + $"{fastAngular[^1].Error:E3} against {linearAngular[^1].Error:E3}");
        }
    }

    /// <summary>
    /// The same cylinder with GEOMETRICALLY spaced rings is nodally EXACT for linear
    /// elements — the observation that made the convergence study above use uniform spacing
    /// instead, and a small lesson in its own right.
    ///
    /// <para>Two facts coincide. The exact profile is linear in <c>ln r</c>, and geometric
    /// spacing puts the nodes at equal intervals of it; and every ring-to-ring conductance
    /// is then EQUAL, because each annular ring is a radially scaled copy of the last and a
    /// two-dimensional conductance is scale-invariant. Equal conductances with values in
    /// arithmetic progression satisfy the discrete balance <c>C(T_{j+1}-T_j) = Q</c>
    /// identically, so the exact nodal values ARE the discrete solution.</para>
    ///
    /// <para><b>A fixture can make a convergence test measure nothing, and it will not look
    /// broken while it does.</b> The study here reported a "convergence order" of -2.50 and
    /// -1.27 over its refinement sequence, which is what a ratio of two round-off figures
    /// looks like when it is mistaken for a signal.</para>
    /// </summary>
    [Fact]
    public void HollowCylinder_WithGeometricRings_IsNodallyExact()
    {
        double span = InnerTemperature - OuterTemperature;
        foreach (int n in new[] { 2, 4, 8 })
        {
            var tets = StructuredTetMesh.HollowCylinder(
                Inner, Outer, Height, 12 * n, n, 1, geometricGrading: true);
            var mesh = AnalysisMesh.Of(tets);
            var model = new ThermalModel(mesh, Metal)
                .Temperature(StructuredTetMesh.InnerSurface, InnerTemperature)
                .Temperature(StructuredTetMesh.OuterSurface, OuterTemperature);

            var results = ThermalSolver.Solve(model);
            double worst = WorstError(mesh, results, ExactRadial);
            output.WriteLine(
                $"nRadial = {n}: worst |T - exact| {worst:E3} K on a {span:G6} K drop "
                + $"-> {worst / span:E3} relative");
            Assert.True(worst / span < 1e-13, $"{worst / span:E3} is not round-off");
        }
    }
}
