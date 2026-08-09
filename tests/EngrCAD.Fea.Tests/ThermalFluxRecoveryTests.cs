using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Verification for superconvergent patch recovery of the HEAT FLUX and the Zienkiewicz-Zhu
/// error estimator it feeds — the thermal twin of <see cref="StressRecoveryTests"/>, over the
/// SAME shared patch machinery, run at 3 components rather than 6.
///
/// <para><b>The claim under test is a RATE, not a value.</b> Recovery does not make any one
/// answer right; it makes the nodal flux converge one order faster than direct evaluation does,
/// because a flux is one derivative down from temperature exactly as a stress is from
/// displacement. So the deliverable is a convergence table on a manufactured solution — the
/// only fixture with no competing modelling error in the way — measured as an L2 norm INSIDE
/// the elements over a refinement sequence, which is the form the recorded fixture traps (a
/// probe at a stationary point, an error read only at nodes) force.</para>
///
/// <para>The companion claim is the estimator's <b>effectivity index</b>: the estimated error
/// over the true error, which must approach 1 as the mesh refines. That is the one measurement
/// that says the estimator is an estimate of the error rather than merely a number that gets
/// smaller.</para>
/// </summary>
public class ThermalFluxRecoveryTests(ITestOutputHelper output)
{
    private static readonly Material Metal =
        new("recovery metal", 200_000, 0.3, 8e-9, thermalConductivity: 40.0);

    private static readonly Vector3d Size = new(2.0, 1.5, 1.0);
    private const double Amplitude = 5.0;

    /// <summary>T = A(x³ + y³ + z³ + x²y + y²z + z²x) — CUBIC, so its flux is genuinely
    /// quadratic and neither element order reproduces it exactly; and coupled, so no direction
    /// is independent of the others. The exact analogue of the cubic-displacement field the
    /// structural recovery suite uses.</summary>
    private static double Exact(Vector3d p) => Amplitude * (
        p.X * p.X * p.X + p.Y * p.Y * p.Y + p.Z * p.Z * p.Z
        + p.X * p.X * p.Y + p.Y * p.Y * p.Z + p.Z * p.Z * p.X);

    private static Vector3d ExactGradient(Vector3d p) => new(
        Amplitude * (3 * p.X * p.X + 2 * p.X * p.Y + p.Z * p.Z),
        Amplitude * (3 * p.Y * p.Y + p.X * p.X + 2 * p.Y * p.Z),
        Amplitude * (3 * p.Z * p.Z + p.Y * p.Y + 2 * p.Z * p.X));

    /// <summary>The exact heat flux, <c>q = -k·grad T</c>.</summary>
    private static Vector3d ExactFlux(Vector3d p) => ExactGradient(p) * -Metal.ThermalConductivity;

    /// <summary>The generation that makes <see cref="Exact"/> exact: <c>q = -k·laplacian(T)</c>
    /// with <c>laplacian(T) = A(8x + 8y + 8z)</c>.</summary>
    private static double Generation(Vector3d p) =>
        -Metal.ThermalConductivity * Amplitude * 8.0 * (p.X + p.Y + p.Z);

    private readonly record struct Run(
        double H, int Elements, double Direct, double Recovered, double TrueEnergy,
        double EstimatedEnergy, double Effectivity, int Fallbacks);

    private Run Solve(ElementOrder order, int divisions)
    {
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, Size, 2 * divisions, 2 * divisions, divisions);
        var mesh = order == ElementOrder.Linear ? AnalysisMesh.Of(tets) : AnalysisMesh.Quadratic(tets);

        var model = new ThermalModel(mesh, Metal);
        foreach (int node in model.NodesOn(Facets.All))
            model.TemperatureNode(node, Exact(mesh.Position(node)));
        model.Generation(Generation);

        var results = ThermalSolver.Solve(model);
        Assert.True(results.Report.RelativeResidual < 1e-8,
            $"solve residual {results.Report.RelativeResidual:E3}");

        results.FluxRecovery = FluxRecovery.Direct;
        var direct = results.NodalFlux.ToArray();
        results.FluxRecovery = FluxRecovery.Superconvergent;
        var recovered = results.NodalFlux.ToArray();

        // L2 error of each nodal field against the exact flux, integrated INSIDE the elements at
        // a rule rich enough for the integrand - never sampled at the nodes, which is where a
        // nodal field is by construction closest to whatever produced it.
        var rule = order == ElementOrder.Linear ? TetQuadrature.Degree3 : TetQuadrature.Degree5;
        int perElement = mesh.NodesPerElement;
        double directError = 0, recoveredError = 0, exactNorm = 0, trueEnergy = 0;
        double k = Metal.ThermalConductivity;

        var positions = new Vector3d[10];
        var temps = new double[10];
        var shape = new double[10];
        var grad = new Vector3d[10];

        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            for (int i = 0; i < perElement; i++)
            {
                positions[i] = mesh.Position(nodes[i]);
                temps[i] = results.TemperatureAt(nodes[i]);
            }

            for (int q = 0; q < rule.Count; q++)
            {
                var (r, s, t) = rule.Point(q);
                if (!TetElement.ShapeGradients(
                        mesh.Order, positions.AsSpan(0, perElement), r, s, t, grad, out double detJ))
                    continue;
                double weight = rule.Weight(q) * detJ;
                TetElement.ShapeValues(mesh.Order, r, s, t, shape);

                var at = Vector3d.Zero;
                for (int i = 0; i < perElement; i++)
                    at += positions[i] * shape[i];
                var exact = ExactFlux(at);

                directError += weight * Distance(direct, nodes, shape, perElement, exact);
                recoveredError += weight * Distance(recovered, nodes, shape, perElement, exact);
                exactNorm += weight * exact.LengthSquared;

                // The finite-element flux from the same shape gradients, and the TRUE error in
                // the same energy norm the estimator uses, so effectivity compares like with
                // like: the thermal energy inner product of a flux difference is |d|²/k.
                var gradT = Vector3d.Zero;
                for (int i = 0; i < perElement; i++)
                    gradT += grad[i] * temps[i];
                var fe = gradT * -k;
                trueEnergy += weight * (exact - fe).LengthSquared / k;
            }
        }

        var estimate = results.ErrorEstimate;
        double trueNorm = Math.Sqrt(trueEnergy);
        return new Run(
            Size.Z / divisions,
            mesh.ElementCount,
            Math.Sqrt(directError / exactNorm),
            Math.Sqrt(recoveredError / exactNorm),
            trueNorm,
            estimate.ErrorNorm,
            trueNorm > 0 ? estimate.ErrorNorm / trueNorm : double.NaN,
            estimate.FallbackNodes);
    }

    /// <summary>The L2 distance² between a nodal flux field, shape-interpolated at a point, and
    /// the exact flux there.</summary>
    private static double Distance(
        Vector3d[] nodal, ReadOnlySpan<int> nodes, double[] shape, int count, Vector3d exact)
    {
        var interp = Vector3d.Zero;
        for (int i = 0; i < count; i++)
            interp += nodal[nodes[i]] * shape[i];
        return (interp - exact).LengthSquared;
    }

    [Theory]
    [InlineData(ElementOrder.Linear, 2.0)]
    [InlineData(ElementOrder.Quadratic, 3.0)]
    public void RecoveredFluxConvergesAnOrderFasterThanDirectEvaluation(
        ElementOrder order, double theory)
    {
        // The quadratic sequence starts at 2, not 1: a 24-element box has NO interior corner
        // node, so no patch exists, nothing is recovered and the row would measure the fallback
        // rather than the recovery.
        int[] sequence = order == ElementOrder.Linear ? [2, 4, 8] : [2, 3, 4];
        var runs = sequence.Select(n => Solve(order, n)).ToArray();

        output.WriteLine($"{order}: theory {theory - 1:F0} direct, {theory:F0} recovered");
        output.WriteLine("     h    elements       direct    recovered   direct rate  rec. rate");
        for (int i = 0; i < runs.Length; i++)
        {
            var r = runs[i];
            string directRate = "-", recoveredRate = "-";
            if (i > 0)
            {
                double ratio = runs[i - 1].H / r.H;
                directRate = (Math.Log(runs[i - 1].Direct / r.Direct) / Math.Log(ratio)).ToString("F3");
                recoveredRate =
                    (Math.Log(runs[i - 1].Recovered / r.Recovered) / Math.Log(ratio)).ToString("F3");
            }
            output.WriteLine(
                $"{r.H,6:F3} {r.Elements,11:N0} {r.Direct,12:E3} {r.Recovered,12:E3} "
                + $"{directRate,13} {recoveredRate,10}   ({r.Fallbacks} fallback nodes)");
        }

        var finest = runs[^1];
        var previous = runs[^2];
        double h = previous.H / finest.H;
        double directOrder = Math.Log(previous.Direct / finest.Direct) / Math.Log(h);
        double recoveredOrder = Math.Log(previous.Recovered / finest.Recovered) / Math.Log(h);

        // The claim is comparative and is asserted comparatively: the recovered field is BOTH
        // more accurate and converging faster. A bare "the order is about p+1" would pass a
        // recovery that had simply scaled the same field.
        Assert.True(finest.Recovered < finest.Direct,
            $"recovered {finest.Recovered:E3} is not better than direct {finest.Direct:E3}");
        Assert.True(recoveredOrder > directOrder + 0.5,
            $"recovered order {recoveredOrder:F3} is not clearly above direct {directOrder:F3}");
        Assert.True(recoveredOrder > theory - 0.5,
            $"recovered order {recoveredOrder:F3} is below theory {theory:F1}");
        Assert.Equal(0, finest.Fallbacks);
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void TheErrorEstimatorsEffectivityApproachesOne(ElementOrder order)
    {
        int[] sequence = order == ElementOrder.Linear ? [2, 4, 8] : [2, 3, 4];
        var runs = sequence.Select(n => Solve(order, n)).ToArray();

        output.WriteLine($"{order}: effectivity = estimated / true error, in the energy norm |q|²/k");
        foreach (var r in runs)
        {
            output.WriteLine(
                $"  h {r.H:F3}, {r.Elements,6:N0} elements: true {r.TrueEnergy:E3}, "
                + $"estimated {r.EstimatedEnergy:E3}, theta = {r.Effectivity:F4}");
        }

        // A ZZ estimator is expected inside roughly [0.8, 1.2] and to tighten with refinement.
        // Both halves are asserted: a constant offset would satisfy the band alone and would
        // mean the estimator was measuring something else that happens to scale the same way.
        double finest = runs[^1].Effectivity;
        Assert.True(finest is > 0.8 and < 1.2, $"effectivity {finest:F4} is outside [0.8, 1.2]");
        Assert.True(
            Math.Abs(finest - 1) <= Math.Abs(runs[0].Effectivity - 1) + 1e-6,
            $"effectivity moved away from 1: {runs[0].Effectivity:F4} then {finest:F4}");
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void ALinearFluxFieldIsRecoveredEXACTLY(ElementOrder order)
    {
        // The consistency check every recovery scheme must pass: where the finite-element flux
        // IS the exact field, the recovery must return it unchanged rather than smoothing it. A
        // LINEAR flux field is the strongest form, because it is in the fitted polynomial's
        // space for both orders while still varying - a constant field would pass any averaging.
        //
        // The flux comes from a QUADRATIC temperature, which a 10-node element reproduces
        // exactly; for a 4-node element the flux is constant per element, so the exact linear
        // field is asserted only for the quadratic case and the linear case checks the constant.
        bool quadratic = order == ElementOrder.Quadratic;
        var tets = StructuredTetMesh.Box(Vector3d.Zero, Size, 4, 3, 2);
        var mesh = quadratic ? AnalysisMesh.Quadratic(tets) : AnalysisMesh.Of(tets);

        double Temperature(Vector3d p) => quadratic
            ? Amplitude * (p.X * p.X + p.Y * p.Y + p.Z * p.Z)
            : Amplitude * (p.X - 0.4 * p.Y + 0.7 * p.Z);
        Vector3d ExactGrad(Vector3d p) => quadratic
            ? new Vector3d(2 * Amplitude * p.X, 2 * Amplitude * p.Y, 2 * Amplitude * p.Z)
            : new Vector3d(Amplitude, -0.4 * Amplitude, 0.7 * Amplitude);

        var model = new ThermalModel(mesh, Metal);
        foreach (int node in model.NodesOn(Facets.All))
            model.TemperatureNode(node, Temperature(mesh.Position(node)));

        // The quadratic temperature is NOT harmonic, so prescribing it alone would solve a
        // different problem - the recorded fixture trap. laplacian(T) = 6A, so the generation is
        // the constant -k.6A. The linear field needs none, its second derivatives being zero.
        if (quadratic)
            model.Generation(_ => -Metal.ThermalConductivity * 6 * Amplitude);

        var results = ThermalSolver.Solve(model);
        results.FluxRecovery = FluxRecovery.Superconvergent;
        var recovered = results.NodalFlux;

        double k = Metal.ThermalConductivity, scale = 0, worst = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            var exact = ExactGrad(mesh.Position(v)) * -k;
            var got = recovered[v];
            scale = Math.Max(scale, exact.Length);
            worst = Math.Max(worst, (got - exact).Length);
        }

        output.WriteLine(
            $"{order}: worst recovered flux error {worst:E3} of {scale:E3} ({results.ErrorEstimate})");
        Assert.True(worst <= 1e-9 * scale, $"recovery is not exact: {worst:E3} of {scale:E3}");

        // And the estimator says so: where the recovery reproduces the finite-element field
        // exactly, the estimated error is zero — the identity that makes the estimator
        // meaningful rather than a smoothness measure.
        if (!quadratic)
        {
            Assert.True(results.ErrorEstimate.RelativeError < 1e-10,
                $"estimated error {results.ErrorEstimate.RelativeError:E3} on an exact field");
        }
    }

    [Fact]
    public void AMeshWithNoInteriorNodeReportsAnUNKNOWNErrorRatherThanZero()
    {
        // With no interior corner node there is no patch, the "recovered" flux IS the
        // finite-element flux, and the estimated error is the distance from something to itself.
        // NaN is the answer, following the structural estimate's own spelling.
        var tets = StructuredTetMesh.Box(Vector3d.Zero, Size, 2, 2, 1);
        var mesh = AnalysisMesh.Quadratic(tets);
        var model = new ThermalModel(mesh, Metal);
        foreach (int node in model.NodesOn(Facets.All))
            model.TemperatureNode(node, Exact(mesh.Position(node)));
        model.Generation(Generation);
        var results = ThermalSolver.Solve(model);

        var estimate = results.ErrorEstimate;
        output.WriteLine($"{mesh.ElementCount} elements: {estimate}");
        Assert.Equal(mesh.NodeCount, estimate.FallbackNodes);
        Assert.True(double.IsNaN(estimate.RelativeError), $"got {estimate.RelativeError:E3}");
        Assert.True(double.IsNaN(estimate.ErrorNorm));
        Assert.Contains("UNKNOWN", estimate.ToString());
    }

    [Fact]
    public void TheDefaultIsDirectAndSwitchingBackRestoresItBitForBit()
    {
        // The neutrality rule: recovery must be invisible until it is asked for, and asking for
        // it and changing one's mind must not leave a different answer behind — which is what
        // keeps every thermal verification figure this project quotes provably unmoved.
        var tets = StructuredTetMesh.Box(Vector3d.Zero, Size, 3, 2, 2);
        var mesh = AnalysisMesh.Of(tets);
        var model = new ThermalModel(mesh, Metal);
        foreach (int node in model.NodesOn(Facets.All))
            model.TemperatureNode(node, Exact(mesh.Position(node)));
        model.Generation(Generation);
        var results = ThermalSolver.Solve(model);

        Assert.Equal(FluxRecovery.Direct, results.FluxRecovery);
        var before = results.NodalFlux.ToArray();

        results.FluxRecovery = FluxRecovery.Superconvergent;
        var recovered = results.NodalFlux.ToArray();
        // The per-region accessor switches too: on a single-region mesh it is the node value.
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(recovered[recovered.Length / 2].X),
            BitConverter.DoubleToInt64Bits(results.NodalFluxIn(0, recovered.Length / 2).X));

        results.FluxRecovery = FluxRecovery.Direct;
        var after = results.NodalFlux.ToArray();

        double scale = 0;
        foreach (var v in before)
            scale = Math.Max(scale, v.Length);
        bool anyDifferent = false;
        for (int v = 0; v < before.Length; v++)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(before[v].X),
                BitConverter.DoubleToInt64Bits(after[v].X));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(before[v].Y),
                BitConverter.DoubleToInt64Bits(after[v].Y));
            if ((recovered[v] - before[v]).Length > 1e-6 * scale)
                anyDifferent = true;
        }
        Assert.True(anyDifferent, "recovery changed nothing, so the comparison proves nothing");
    }
}
