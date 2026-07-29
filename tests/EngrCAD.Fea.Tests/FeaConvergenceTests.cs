using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Convergence order by the METHOD OF MANUFACTURED SOLUTIONS — the only way to measure an
/// element's order without a competing modelling error in the way.
///
/// <para><b>How it works.</b> Choose a smooth displacement field u, put it through the
/// elasticity operator analytically to get the body force
/// <c>f = -(L+M)·grad(div u) - M·laplacian(u)</c> that makes it an exact solution, then
/// prescribe u on the whole boundary and apply f. The exact answer is then u, everywhere,
/// with no singularity anywhere in the domain or on its boundary — unlike the cantilever,
/// whose clamped edge carries one and whose order is limited by it rather than by the
/// elements. The theoretical rates for a degree-p element are O(h^(p+1)) in the L2 norm
/// of the displacement and O(h^p) in the energy norm, and both are measured here.</para>
///
/// <para>The chosen field is CUBIC, so neither element type can reproduce it exactly: a
/// quadratic field would be exact for 10-node elements and report an infinite order.</para>
/// </summary>
public class FeaConvergenceTests(ITestOutputHelper output)
{
    private static readonly Material Steel = new("convergence steel", 210_000, 0.3);
    private static readonly Vector3d Size = new(2.0, 1.5, 1.0);
    private const double Amplitude = 1e-3;

    /// <summary>u = A·(x^3 + y^2, y^3 + z^2, z^3 + x^2) — cubic, with cross terms so no
    /// component is independent of the others.</summary>
    private static Vector3d Exact(Vector3d p) => new(
        Amplitude * (p.X * p.X * p.X + p.Y * p.Y),
        Amplitude * (p.Y * p.Y * p.Y + p.Z * p.Z),
        Amplitude * (p.Z * p.Z * p.Z + p.X * p.X));

    /// <summary>The exact strain tensor of <see cref="Exact"/>.</summary>
    private static SymmetricTensor3 ExactStrain(Vector3d p) => new(
        Amplitude * 3 * p.X * p.X,
        Amplitude * 3 * p.Y * p.Y,
        Amplitude * 3 * p.Z * p.Z,
        Amplitude * 0.5 * (2 * p.Y),   // d(ux)/dy = 2y, d(uy)/dx = 0
        Amplitude * 0.5 * (2 * p.X),   // d(ux)/dz = 0,  d(uz)/dx = 2x
        Amplitude * 0.5 * (2 * p.Z));  // d(uy)/dz = 2z, d(uz)/dy = 0

    /// <summary>
    /// The body force that makes <see cref="Exact"/> an exact solution:
    /// div u = 3A(x^2 + y^2 + z^2), so grad(div u) = 6A(x, y, z); and
    /// laplacian(u) = A(6x + 2, 6y + 2, 6z + 2).
    /// </summary>
    private static Vector3d Body(Vector3d p)
    {
        double lambda = Steel.Lambda, mu = Steel.Mu;
        var gradDiv = new Vector3d(6 * Amplitude * p.X, 6 * Amplitude * p.Y, 6 * Amplitude * p.Z);
        var laplacian = new Vector3d(
            Amplitude * (6 * p.X + 2), Amplitude * (6 * p.Y + 2), Amplitude * (6 * p.Z + 2));
        return -((lambda + mu) * gradDiv + mu * laplacian);
    }

    private readonly record struct Run(int Divisions, double H, int Elements, int FreeDofs, double L2, double Energy);

    private Run Solve(ElementOrder order, int divisions)
    {
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, Size, 2 * divisions, 2 * divisions, divisions);
        var mesh = order == ElementOrder.Linear ? AnalysisMesh.Of(tets) : AnalysisMesh.Quadratic(tets);

        var model = new StructuralModel(mesh, Steel);
        foreach (int node in model.NodesOn(Facets.All))
            model.PrescribeNode(node, Exact(mesh.Position(node)));
        model.BodyForce(Body);

        var results = StructuralSolver.Solve(model);

        // L2 norm of the displacement error, integrated element by element at the
        // element's own quadrature points; energy norm from the strain error against the
        // same material. Both normalised by the exact field's own norm, so they are
        // relative and scale-free.
        double errorL2 = 0, exactL2 = 0, errorEnergy = 0, exactEnergy = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            double volume = mesh.ElementVolume(e);
            var centroid = Vector3d.Zero;
            var nodes = mesh.Element(e);
            for (int i = 0; i < 4; i++)
                centroid += mesh.Position(nodes[i]);
            centroid *= 0.25;

            // Displacement at the centroid, interpolated from the element's own nodes.
            var interpolated = Vector3d.Zero;
            var shape = new double[10];
            TetElement.ShapeValues(mesh.Order, 0.25, 0.25, 0.25, shape);
            for (int i = 0; i < mesh.NodesPerElement; i++)
                interpolated += results.DisplacementAt(nodes[i]) * shape[i];

            var exact = Exact(centroid);
            errorL2 += volume * (interpolated - exact).LengthSquared;
            exactL2 += volume * exact.LengthSquared;

            var strainError = results.ElementStrain(e) - ExactStrain(centroid);
            var exactStrain = ExactStrain(centroid);
            errorEnergy += volume * EnergyNorm(strainError);
            exactEnergy += volume * EnergyNorm(exactStrain);
        }

        Assert.True(results.Report.RelativeResidual < 1e-8,
            $"solve residual {results.Report.RelativeResidual:E3}");

        return new Run(
            divisions, Size.Z / divisions, mesh.ElementCount, results.Report.FreeDofs,
            Math.Sqrt(errorL2 / exactL2), Math.Sqrt(errorEnergy / exactEnergy));
    }

    /// <summary>The strain-energy density 2·U = e : D : e of a strain tensor.</summary>
    private static double EnergyNorm(in SymmetricTensor3 strain)
    {
        double trace = strain.Trace;
        double contraction =
            strain.Xx * strain.Xx + strain.Yy * strain.Yy + strain.Zz * strain.Zz
            + 2 * (strain.Xy * strain.Xy + strain.Yz * strain.Yz + strain.Xz * strain.Xz);
        return Steel.Lambda * trace * trace + 2.0 * Steel.Mu * contraction;
    }

    [Fact]
    public void ManufacturedSolution_ConvergesAtTheTheoreticalOrderForBothElementTypes()
    {
        var linear = new[] { 2, 4, 8 }.Select(d => Solve(ElementOrder.Linear, d)).ToArray();
        var quadratic = new[] { 1, 2, 4 }.Select(d => Solve(ElementOrder.Quadratic, d)).ToArray();

        output.WriteLine(
            "manufactured solution u = 1e-3·(x^3 + y^2, y^3 + z^2, z^3 + x^2) on a "
            + $"{Size.X} x {Size.Y} x {Size.Z} box, Dirichlet everywhere");
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
            $"observed orders: linear L2 {linearL2:F2} (theory 2), energy {linearEnergy:F2} (theory 1); "
            + $"quadratic L2 {quadraticL2:F2} (theory 3), energy {quadraticEnergy:F2} (theory 2)");

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

    private static double Order(Run[] runs, Func<Run, double> measure)
    {
        // The order over the WHOLE sequence: a least-squares fit would weight the coarse
        // pre-asymptotic level equally, so the last pair is the honest estimate.
        double coarse = measure(runs[^2]), fine = measure(runs[^1]);
        return Math.Log(coarse / fine) / Math.Log(runs[^1].H > 0 ? runs[^2].H / runs[^1].H : 2.0);
    }

    private void Report(string label, Run[] runs)
    {
        output.WriteLine(
            $"{label,-10} {"h",8} {"elements",10} {"free DOF",10} {"L2 err",12} {"order",7} {"energy err",12} {"order",7}");
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
