using System.Diagnostics;
using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The cantilever: tip deflection against beam theory, and the convergence study.
///
/// <para><b>The analytic value is a MODEL, not the truth, and the difference is the
/// interesting part.</b> Euler-Bernoulli gives d = PL^3/(3EI) and assumes plane sections
/// stay plane with no shear deformation; Timoshenko adds PL/(kGA) with k = 5/6 for a
/// rectangle. A three-dimensional elasticity solve includes shear deformation
/// automatically, so it should land near the Timoshenko value — but from BELOW, because
/// the built-in end here is a genuine three-dimensional clamp (every displacement zero
/// over that whole face), which suppresses the Poisson contraction and the warping beam
/// theory allows and is therefore stiffer than either beam model's root condition. Both
/// figures are reported so the reader can see which one the mesh walks towards.</para>
///
/// <para><b>Convergence order is measured against the converged FINITE-ELEMENT answer</b>
/// (Richardson-extrapolated from the quadratic sequence), not against the beam formula.
/// An order measured against a different model's answer stalls at the modelling
/// difference and reports an order of zero, which says nothing about the elements.</para>
///
/// <para><b>The meshes are structured</b> (<see cref="StructuredTetMesh"/>) so the
/// refinement sequence is exactly geometrically similar — every level has the same element
/// shape and half the size — which is what makes an observed order meaningful. See that
/// class for the measurement that rules the Delaunay mesher out for this fixture.</para>
/// </summary>
public class FeaCantileverTests(ITestOutputHelper output)
{
    private const double Length = 100.0;
    private const double Width = 10.0;
    private const double Height = 10.0;
    private const double TipLoad = 1000.0;
    private static readonly Material Steel = new("cantilever steel", 210_000, 0.3);

    private static double SecondMoment => Width * Height * Height * Height / 12.0;

    private static double EulerBernoulli =>
        TipLoad * Length * Length * Length / (3.0 * Steel.YoungsModulus * SecondMoment);

    private static double Timoshenko =>
        EulerBernoulli + TipLoad * Length / ((5.0 / 6.0) * Steel.ShearModulus * Width * Height);

    private readonly record struct Run(
        int Level, double Size, int Elements, int FreeDofs, double Deflection, double Milliseconds);

    /// <summary>Level L divides the beam into 4·2^L by 2^L by 2^L cells, so every level's
    /// elements are similar to every other's and the size halves each time.</summary>
    private Run Solve(ElementOrder order, int level)
    {
        int n = 1 << level;
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(Length, Width, Height), 4 * n, n, n);
        var mesh = order == ElementOrder.Linear ? AnalysisMesh.Of(tets) : AnalysisMesh.Quadratic(tets);

        var model = new StructuralModel(mesh, Steel);
        model.Fix(StructuredTetMesh.XMin);
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -TipLoad));

        var stopwatch = Stopwatch.StartNew();
        var results = StructuralSolver.Solve(model);
        double ms = stopwatch.Elapsed.TotalMilliseconds;

        // The section's MEAN deflection — the quantity beam theory reports. A single
        // corner node would carry the local disturbance of the applied traction.
        var tipNodes = model.NodesOn(Facets.Tag(StructuredTetMesh.XMax));
        double sum = 0;
        foreach (int node in tipNodes)
            sum += results.DisplacementAt(node).Z;

        Assert.True(results.Report.EquilibriumResidual < 1e-9,
            $"equilibrium residual {results.Report.EquilibriumResidual:E3}");
        Assert.Equal(-TipLoad, results.Report.AppliedForce.Z, TipLoad * 1e-10);
        Assert.True(results.Report.RelativeResidual < 1e-8,
            $"solve residual {results.Report.RelativeResidual:E3}");

        return new Run(
            level, Width / n, mesh.ElementCount, results.Report.FreeDofs, -sum / tipNodes.Count, ms);
    }

    [Fact]
    public void TipDeflection_ConvergesOnBeamTheoryAndQuadraticElementsConvergeFaster()
    {
        var linear = new[] { 1, 2, 3 }.Select(l => Solve(ElementOrder.Linear, l)).ToArray();
        var quadratic = new[] { 0, 1, 2 }.Select(l => Solve(ElementOrder.Quadratic, l)).ToArray();

        double reference = Extrapolate(
            quadratic[0].Deflection, quadratic[1].Deflection, quadratic[2].Deflection);

        output.WriteLine(
            $"cantilever {Length} x {Width} x {Height}, tip load {TipLoad} N, "
            + $"E = {Steel.YoungsModulus} MPa, nu = {Steel.PoissonsRatio}");
        output.WriteLine($"Euler-Bernoulli PL^3/3EI = {EulerBernoulli:F5} mm");
        output.WriteLine($"Timoshenko (k = 5/6)     = {Timoshenko:F5} mm");
        output.WriteLine("");
        Report("linear", linear, reference);
        output.WriteLine("");
        Report("quadratic", quadratic, reference);
        output.WriteLine("");
        output.WriteLine($"extrapolated finite-element limit {reference:F5} mm");
        output.WriteLine($"  vs Euler-Bernoulli: {(reference / EulerBernoulli - 1) * 100:+0.00;-0.00}%");
        output.WriteLine($"  vs Timoshenko:      {(reference / Timoshenko - 1) * 100:+0.00;-0.00}%");

        // A displacement-based element is always too stiff, so every deflection must be
        // below the limit and must rise as the mesh refines.
        foreach (var runs in new[] { linear, quadratic })
        {
            for (int i = 0; i < runs.Length; i++)
            {
                Assert.True(runs[i].Deflection < reference,
                    $"deflection {runs[i].Deflection:F5} exceeds the limit {reference:F5}");
                if (i > 0)
                {
                    Assert.True(runs[i].Deflection > runs[i - 1].Deflection,
                        $"deflection did not increase: {runs[i - 1].Deflection} -> {runs[i].Deflection}");
                }
            }
        }

        // Quadratic beats linear at every SHARED refinement level — the whole reason to
        // pay for the extra nodes.
        foreach (int level in new[] { 1, 2 })
        {
            var l = linear.Single(r => r.Level == level);
            var q = quadratic.Single(r => r.Level == level);
            double linearError = Math.Abs(l.Deflection - reference);
            double quadraticError = Math.Abs(q.Deflection - reference);
            Assert.True(quadraticError < linearError * 0.25,
                $"level {level}: quadratic error {quadraticError:E3} is not well below linear {linearError:E3}");
        }

        // The converged answer must sit between the two beam models, on the stiff side of
        // Timoshenko: a three-dimensional clamp is stiffer than a beam's built-in end.
        // Outside that band is a modelling error, not a mesh one.
        Assert.InRange(reference, EulerBernoulli * 0.95, Timoshenko * 1.02);

        // And the finest quadratic mesh alone is already within 1% of the limit.
        Assert.True(Math.Abs(quadratic[^1].Deflection - reference) < 0.01 * reference,
            $"finest quadratic {quadratic[^1].Deflection:F5} vs limit {reference:F5}");
    }

    private void Report(string label, Run[] runs, double reference)
    {
        output.WriteLine(
            $"{label,-10} {"h",7} {"elements",10} {"free DOF",10} {"tip (mm)",11} {"err %",9} {"order",7} {"ms",8}");
        for (int i = 0; i < runs.Length; i++)
        {
            double error = Math.Abs(runs[i].Deflection - reference);
            string order = "-";
            if (i > 0)
            {
                double previous = Math.Abs(runs[i - 1].Deflection - reference);
                if (error > 0 && previous > 0)
                    order = (Math.Log(previous / error) / Math.Log(2.0)).ToString("F2");
            }
            output.WriteLine(
                $"{"",-10} {runs[i].Size,7:F3} {runs[i].Elements,10:N0} {runs[i].FreeDofs,10:N0} "
                + $"{runs[i].Deflection,11:F5} {error / reference * 100,9:F3} {order,7} {runs[i].Milliseconds,8:F0}");
        }
    }

    /// <summary>Richardson extrapolation of a geometric refinement sequence with a fixed
    /// convergence order: the limit is c + (c-b)^2 / ((b-a) - (c-b)).</summary>
    private static double Extrapolate(double a, double b, double c)
    {
        double d1 = b - a, d2 = c - b;
        double denominator = d1 - d2;
        if (Math.Abs(denominator) < 1e-15 * Math.Abs(c))
            return c;
        return c + d2 * d2 / denominator;
    }
}
