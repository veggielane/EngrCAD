using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Conduction convergence order by the METHOD OF MANUFACTURED SOLUTIONS — the only way to
/// measure an element's order without a competing modelling error in the way.
///
/// <para><b>How it works.</b> Choose a smooth temperature field T, put it through the
/// conduction operator analytically to get the volumetric generation
/// <c>q = -k·laplacian(T)</c> that makes it an exact solution, then prescribe T on the
/// whole boundary and apply q. The exact answer is then T, everywhere, on a domain a box
/// mesh represents EXACTLY — unlike the hollow cylinder, whose polygonal rings are a
/// different body from the annulus the analytic profile describes, and whose measured
/// order is capped by that difference rather than by the elements. The theoretical rates
/// for a degree-p element are O(h^(p+1)) in the L2 norm of the temperature and O(h^p) in
/// the energy (gradient) norm, and both are measured here.</para>
///
/// <para><b>The chosen field is QUARTIC, and that is a trap this suite walked into with a
/// cubic one first.</b> A cubic field on a uniform mesh is reproduced <b>exactly at the
/// nodes</b> by both element types — the finite-element stencil's truncation error is
/// proportional to a fourth derivative, which a cubic does not have — so a study measuring
/// nodal values reports round-off at every refinement level and no order at all (measured:
/// 2.2e-13 worst nodal error on a field of scale 113). Adding an <c>x²y²</c> term restores
/// a non-zero fourth derivative and with it a genuine finite-element error. A quadratic
/// field would have been worse still, being exact for 10-node elements everywhere and
/// reporting an infinite order.</para>
///
/// <para>The generation load is integrated with a degree-5 rule for the reason the
/// structural twin records — under-integrating a load caps a study at the QUADRATURE's
/// order and looks exactly like a formulation defect. That claim gets its own negative
/// control below.</para>
/// </summary>
public class ThermalConvergenceTests(ITestOutputHelper output)
{
    private static readonly Material Metal =
        new("convergence metal", 200_000, 0.3, 8e-9, thermalConductivity: 40.0);

    private static readonly Vector3d Size = new(2.0, 1.5, 1.0);
    private const double Amplitude = 5.0;

    /// <summary>T = A·(x³ + y³ + z³ + x²y + y²z + z²x + x²y²) — quartic, with cross terms
    /// so no direction is independent of the others, and with a non-zero fourth derivative
    /// so the nodal values are not accidentally exact (see the class remarks).</summary>
    private static double Exact(Vector3d p) => Amplitude * (
        p.X * p.X * p.X + p.Y * p.Y * p.Y + p.Z * p.Z * p.Z
        + p.X * p.X * p.Y + p.Y * p.Y * p.Z + p.Z * p.Z * p.X
        + p.X * p.X * p.Y * p.Y);

    /// <summary>The exact gradient of <see cref="Exact"/>.</summary>
    private static Vector3d ExactGradient(Vector3d p) => new(
        Amplitude * (3 * p.X * p.X + 2 * p.X * p.Y + p.Z * p.Z + 2 * p.X * p.Y * p.Y),
        Amplitude * (3 * p.Y * p.Y + p.X * p.X + 2 * p.Y * p.Z + 2 * p.X * p.X * p.Y),
        Amplitude * (3 * p.Z * p.Z + p.Y * p.Y + 2 * p.Z * p.X));

    /// <summary>
    /// The generation that makes <see cref="Exact"/> exact: <c>q = -k·laplacian(T)</c> with
    /// <c>laplacian(T) = A·(8x + 8y + 8z + 2x² + 2y²)</c> — term by term,
    /// <c>d²/dx² = 6x + 2y + 2y²</c>, <c>d²/dy² = 6y + 2z + 2x²</c> and
    /// <c>d²/dz² = 6z + 2x</c>.
    /// </summary>
    private static double Generation(Vector3d p) =>
        -Metal.ThermalConductivity * Amplitude
        * (8.0 * (p.X + p.Y + p.Z) + 2.0 * (p.X * p.X + p.Y * p.Y));

    private readonly record struct Run(
        int Divisions, double H, int Elements, int FreeDofs, double L2, double Energy);

    private static Run Solve(ElementOrder order, int divisions)
    {
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, Size, 2 * divisions, 2 * divisions, divisions);
        var mesh = order == ElementOrder.Linear
            ? AnalysisMesh.Of(tets)
            : AnalysisMesh.Quadratic(tets);

        var model = new ThermalModel(mesh, Metal);
        foreach (int node in model.NodesOn(Facets.All))
            model.TemperatureNode(node, Exact(mesh.Position(node)));
        model.Generation(Generation);

        var results = ThermalSolver.Solve(model);
        Assert.True(results.Report.RelativeResidual < 1e-8,
            $"solve residual {results.Report.RelativeResidual:E3}");

        // Both norms integrated element by element at the element's own centroid, and both
        // normalised by the exact field's own norm so they are relative and scale-free.
        double errorL2 = 0, exactL2 = 0, errorEnergy = 0, exactEnergy = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            double volume = mesh.ElementVolume(e);
            var nodes = mesh.Element(e);
            var centroid = Vector3d.Zero;
            for (int i = 0; i < 4; i++)
                centroid += mesh.Position(nodes[i]);
            centroid *= 0.25;

            double interpolated = results.TemperatureIn(e, 0.25, 0.25, 0.25);
            double exact = Exact(centroid);
            errorL2 += volume * (interpolated - exact) * (interpolated - exact);
            exactL2 += volume * exact * exact;

            // The energy norm of a conduction problem is the gradient's, weighted by k —
            // the exact analogue of the structural strain-energy norm.
            var gradientError = results.ElementGradient(e) - ExactGradient(centroid);
            errorEnergy += volume * Metal.ThermalConductivity * gradientError.LengthSquared;
            exactEnergy += volume * Metal.ThermalConductivity * ExactGradient(centroid).LengthSquared;
        }

        return new Run(
            divisions, Size.Z / divisions, mesh.ElementCount, results.Report.FreeDofs,
            Math.Sqrt(errorL2 / exactL2), Math.Sqrt(errorEnergy / exactEnergy));
    }

    [Fact]
    public void ManufacturedSolution_ConvergesAtTheTheoreticalOrderForBothElementTypes()
    {
        var linear = new[] { 2, 4, 8 }.Select(d => Solve(ElementOrder.Linear, d)).ToArray();
        var quadratic = new[] { 1, 2, 4 }.Select(d => Solve(ElementOrder.Quadratic, d)).ToArray();

        output.WriteLine(
            "manufactured solution T = 5(x^3 + y^3 + z^3 + x^2 y + y^2 z + z^2 x + x^2 y^2) "
            + $"on a {Size.X} x {Size.Y} x {Size.Z} box, Dirichlet everywhere,");
        output.WriteLine(
            "generation q = -k.laplacian(T) = -40 . 5 . (8(x + y + z) + 2(x^2 + y^2))");
        output.WriteLine("");
        Report("linear", linear);
        output.WriteLine("");
        Report("quadratic", quadratic);

        double linearL2 = Order(linear, r => r.L2);
        double linearEnergy = Order(linear, r => r.Energy);
        double quadraticL2 = Order(quadratic, r => r.L2);
        double quadraticEnergy = Order(quadratic, r => r.Energy);

        output.WriteLine("");
        output.WriteLine(
            $"observed orders: linear L2 {linearL2:F2} (theory 2), energy {linearEnergy:F2} "
            + $"(theory 1); quadratic L2 {quadraticL2:F2} (theory 3), energy "
            + $"{quadraticEnergy:F2} (theory 2)");

        Assert.InRange(linearL2, 1.7, 2.4);
        Assert.InRange(linearEnergy, 0.8, 1.4);
        Assert.InRange(quadraticL2, 2.6, 3.6);
        Assert.InRange(quadraticEnergy, 1.7, 2.4);

        // The point of the exercise: quadratic elements converge strictly faster in both
        // norms.
        Assert.True(quadraticL2 > linearL2 + 0.5,
            $"quadratic L2 order {quadraticL2:F2} does not beat linear {linearL2:F2}");
        Assert.True(quadraticEnergy > linearEnergy + 0.5,
            $"quadratic energy order {quadraticEnergy:F2} does not beat linear {linearEnergy:F2}");
    }

    /// <summary>
    /// The negative control for the manufactured study: integrating the generation with the
    /// ELEMENT's own rule instead of degree 5 caps the measured order, which is the trap
    /// the structural work recorded and the reason
    /// <c>ThermalModel.Generation(Func&lt;Vector3d, double&gt;)</c> hard-codes a degree-5
    /// rule.
    /// <para>Without this, "the load is integrated well enough" is an assumption; with it,
    /// the difference is a number.</para>
    /// </summary>
    [Fact]
    public void UnderIntegratingTheGenerationLoad_ChangesTheAnswer()
    {
        const int divisions = 4;
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, Size, 2 * divisions, 2 * divisions, divisions);
        var mesh = AnalysisMesh.Quadratic(tets);

        static ThermalModel Build(AnalysisMesh mesh)
        {
            var model = new ThermalModel(mesh, Metal);
            foreach (int node in model.NodesOn(Facets.All))
                model.TemperatureNode(node, Exact(mesh.Position(node)));
            return model;
        }

        // The production path: degree 5.
        var accurate = ThermalSolver.Solve(Build(mesh).Generation(Generation));

        // A deliberately under-integrated load, built here rather than exposed as an
        // option: the element's own degree-2 rule against a linear generation field.
        var crude = Build(mesh);
        var loads = new double[mesh.NodesPerElement];
        var positions = new Vector3d[mesh.NodesPerElement];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            for (int i = 0; i < mesh.NodesPerElement; i++)
                positions[i] = mesh.Position(nodes[i]);
            ThermalElement.Generation(
                mesh.Order, positions, TetQuadrature.Degree1, Generation, loads);
            for (int i = 0; i < mesh.NodesPerElement; i++)
                crude.NodalHeat(nodes[i], loads[i]);
        }
        var crudeResults = ThermalSolver.Solve(crude);

        double worstAccurate = 0, worstCrude = 0, scale = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            double exact = Exact(mesh.Position(v));
            scale = Math.Max(scale, Math.Abs(exact));
            worstAccurate = Math.Max(worstAccurate, Math.Abs(accurate.TemperatureAt(v) - exact));
            worstCrude = Math.Max(worstCrude, Math.Abs(crudeResults.TemperatureAt(v) - exact));
        }

        output.WriteLine($"field scale {scale:G6}");
        output.WriteLine($"  degree-5 generation load: worst nodal error {worstAccurate:E3} "
            + $"({worstAccurate / scale:E3} relative)");
        output.WriteLine($"  degree-1 generation load: worst nodal error {worstCrude:E3} "
            + $"({worstCrude / scale:E3} relative)");
        output.WriteLine(
            $"  the crude load is {worstCrude / worstAccurate:G3}x worse, and since the "
            + "accurate one is at the mesh's own error, essentially all of that gap is "
            + "quadrature");

        Assert.True(worstCrude > 3 * worstAccurate,
            $"the crude load gave {worstCrude:E3} against {worstAccurate:E3}, so this control "
            + "does not show that the quadrature matters");
    }

    private static double Order(Run[] runs, Func<Run, double> measure)
    {
        // The order over the LAST pair: a least-squares fit would weight the coarse
        // pre-asymptotic level equally.
        double coarse = measure(runs[^2]), fine = measure(runs[^1]);
        return Math.Log(coarse / fine) / Math.Log(runs[^2].H / runs[^1].H);
    }

    private void Report(string label, Run[] runs)
    {
        output.WriteLine(
            $"{label,-10} {"h",8} {"elements",10} {"free DOF",10} {"L2 err",12} {"order",7} "
            + $"{"energy err",12} {"order",7}");
        for (int i = 0; i < runs.Length; i++)
        {
            string l2Order = "-", energyOrder = "-";
            if (i > 0)
            {
                double ratio = Math.Log(runs[i - 1].H / runs[i].H);
                l2Order = (Math.Log(runs[i - 1].L2 / runs[i].L2) / ratio).ToString("F2");
                energyOrder = (Math.Log(runs[i - 1].Energy / runs[i].Energy) / ratio).ToString("F2");
            }
            output.WriteLine(
                $"{"",-10} {runs[i].H,8:F4} {runs[i].Elements,10:N0} {runs[i].FreeDofs,10:N0} "
                + $"{runs[i].L2,12:E3} {l2Order,7} {runs[i].Energy,12:E3} {energyOrder,7}");
        }
    }
}
