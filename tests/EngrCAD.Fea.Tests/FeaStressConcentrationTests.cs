using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The stress-concentration benchmark: a plate in tension with a central circular hole,
/// against Kirsch's classical solution and its finite-width correction.
///
/// <para><b>Kirsch (1898)</b> gives, for an INFINITE plate under uniaxial tension s, the
/// tangential stress on the hole boundary <c>s_tt = s(1 - 2cos2t)</c>, peaking at
/// <c>3s</c> at the two points on the diameter perpendicular to the load. Three is the
/// number everyone quotes, and it is the wrong number for any real plate.</para>
///
/// <para><b>The finite-width correction used here is Howland's exact strip solution, in
/// Peterson's polynomial fit</b> (Chart 4.1), stated against the NET section stress:
/// <c>K_tn = 2 + 0.284L - 0.600L^2 + 1.32L^3</c> with <c>L = 1 - d/W</c>, valid for
/// d/W up to 0.5 and reducing to 3.004 as d/W goes to zero. At the d/W = 0.25 used here
/// it gives K_tn = 2.4324, i.e. <c>K_tg = 3.2432</c> against the gross section — nearly
/// 8% above the textbook 3, which is exactly why the correction has to be stated rather
/// than assumed away.</para>
///
/// <para><b>Plane strain, imposed exactly</b> by fixing every node's z displacement. Both
/// the infinite-plate and the finite-strip in-plane stresses are independent of Poisson's
/// ratio and of the plane-stress/plane-strain choice, because the hole boundary is
/// traction free and therefore carries no resultant (Michell's condition). Fixing z
/// removes the through-thickness variation a real 3D plate has, so the comparison is
/// against the two-dimensional theory the benchmark is stated in rather than against a
/// thickness-averaged approximation of it.</para>
///
/// <para><b>The four theoretical peak nodes disagree, and that is a MESH property worth
/// reporting rather than averaging away silently.</b> Kirsch's maximum occurs at both +90
/// and -90 degrees, and the exact plane-strain solution is independent of z, so all four
/// nodes (two angles, two thickness layers) should read the same. They do not, and the
/// reason is instructive: Kuhn's subdivision picks each cell's diagonals by LOGICAL INDEX
/// ORDER, which no reflection preserves, so the tetrahedral topology adjoining the z = 0
/// face is not the mirror of the one adjoining z = t. Measured here, the y-reflection is
/// exact to the last bit while the z-reflection is not — the spread across the four is a
/// direct measurement of the discretization error rather than an estimate of it, and it
/// shrinks with refinement. Their mean is the symmetrized estimate.</para>
/// </summary>
public class FeaStressConcentrationTests(ITestOutputHelper output)
{
    private const double HalfLength = 60.0;   // plate 120 long
    private const double HalfWidth = 20.0;    // plate 40 wide
    private const double Thickness = 2.0;
    private const double HoleRadius = 5.0;    // d/W = 0.25
    private const double TotalForce = 60_000.0;
    private static readonly Material Steel = new("plate steel", 210_000, 0.3);

    private static double DiameterRatio => 2 * HoleRadius / (2 * HalfWidth);

    /// <summary>Peterson's fit to Howland's finite-strip solution, on the NET section.</summary>
    private static double HowlandKtn
    {
        get
        {
            double l = 1.0 - DiameterRatio;
            return 2.0 + 0.284 * l - 0.600 * l * l + 1.32 * l * l * l;
        }
    }

    private static double GrossStress => TotalForce / (2 * HalfWidth * Thickness);

    private static double NetStress => TotalForce / ((2 * HalfWidth - 2 * HoleRadius) * Thickness);

    private readonly record struct Run(
        int Theta, int Radial, int Elements, int FreeDofs,
        double[] PeakNodes, double HoleMaximum, double FarField)
    {
        /// <summary>The mean over the four theoretical peak nodes — the estimate with the
        /// subdivision's own asymmetry cancelled.</summary>
        public double Symmetrized => PeakNodes.Average();

        /// <summary>The spread across the four, relative — the discretization error,
        /// measured rather than estimated.</summary>
        public double Asymmetry => (PeakNodes.Max() - PeakNodes.Min()) / Symmetrized;
    }

    private Run Solve(ElementOrder order, int nTheta, int nRadial)
    {
        var tets = StructuredTetMesh.PlateWithHole(
            HalfLength, HalfWidth, Thickness, HoleRadius, nTheta, nRadial, nZ: 1);
        var mesh = order == ElementOrder.Linear ? AnalysisMesh.Of(tets) : AnalysisMesh.Quadratic(tets);
        var model = new StructuralModel(mesh, Steel);

        // Plane strain, exactly: no z displacement anywhere.
        for (int node = 0; node < mesh.NodeCount; node++)
            model.FixNode(node, Dof.Z);

        model.Fix(StructuredTetMesh.XMin, Dof.X);
        model.FixNode(NearestNode(mesh, new Vector3d(-HalfLength, 0, 0)), Dof.Y);
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(TotalForce, 0, 0));

        var results = StructuralSolver.Solve(model);
        Assert.True(results.Report.EquilibriumResidual < 1e-9,
            $"equilibrium residual {results.Report.EquilibriumResidual:E3}");

        // Kirsch's peak sits at the two points on the diameter perpendicular to the load,
        // where the state is uniaxial along x, so sigma_xx there IS the tangential stress.
        // Both thickness layers are read, because the mesh is not symmetric in z even
        // though the problem is.
        double[] peaks =
        [
            results.NodalStress[NearestNode(mesh, new Vector3d(0, HoleRadius, 0))].Xx,
            results.NodalStress[NearestNode(mesh, new Vector3d(0, HoleRadius, Thickness))].Xx,
            results.NodalStress[NearestNode(mesh, new Vector3d(0, -HoleRadius, 0))].Xx,
            results.NodalStress[NearestNode(mesh, new Vector3d(0, -HoleRadius, Thickness))].Xx,
        ];

        // And the largest first principal stress anywhere on the hole — what an engineer
        // reads off a plot, and a check that Kirsch's LOCATION is right and not just the
        // value there.
        double holeMaximum = 0;
        foreach (int facet in model.FacetsMatching(Facets.Tag(StructuredTetMesh.Hole)))
        {
            foreach (int node in mesh.Facet(facet))
                holeMaximum = Math.Max(holeMaximum, results.PrincipalStress(node).S1);
        }

        double farField = results.NodalStress[NearestNode(mesh, new Vector3d(HalfLength, 0, 0))].Xx;
        return new Run(nTheta, nRadial, mesh.ElementCount, results.Report.FreeDofs,
            peaks, holeMaximum, farField);
    }

    [Fact]
    public void PlateWithHole_MatchesHowlandsFiniteWidthStressConcentration()
    {
        var linear = new[] { (32, 4), (64, 8), (128, 16) }
            .Select(m => Solve(ElementOrder.Linear, m.Item1, m.Item2)).ToArray();
        var quadratic = new[] { (32, 4), (64, 8) }
            .Select(m => Solve(ElementOrder.Quadratic, m.Item1, m.Item2)).ToArray();

        double expectedPeak = HowlandKtn * NetStress;
        output.WriteLine(
            $"plate {2 * HalfLength} x {2 * HalfWidth} x {Thickness}, hole d = {2 * HoleRadius}, "
            + $"d/W = {DiameterRatio:F2}, total force {TotalForce:N0} N");
        output.WriteLine($"gross stress {GrossStress:F3} MPa, net stress {NetStress:F3} MPa");
        output.WriteLine($"Kirsch (infinite plate)      K_t  = 3.0000, peak {3 * GrossStress:F3} MPa");
        output.WriteLine(
            $"Howland/Peterson finite width K_tn = {HowlandKtn:F4} on net "
            + $"(= {HowlandKtn * NetStress / GrossStress:F4} on gross), peak {expectedPeak:F3} MPa");
        output.WriteLine("");
        Report("linear", linear, expectedPeak);
        output.WriteLine("");
        Report("quadratic", quadratic, expectedPeak);

        // The far-field stress is the gross stress; getting it wrong makes every K_t
        // below meaningless.
        foreach (var run in linear.Concat(quadratic))
        {
            Assert.True(Math.Abs(run.FarField - GrossStress) < 0.02 * GrossStress,
                $"far-field {run.FarField:F3} vs gross {GrossStress:F3}");
        }

        // Both element types approach the peak FROM BELOW — a displacement formulation
        // underestimates a stress concentration — and refinement closes the gap.
        foreach (var runs in new[] { linear, quadratic })
        {
            for (int i = 1; i < runs.Length; i++)
            {
                Assert.True(runs[i].Symmetrized > runs[i - 1].Symmetrized,
                    $"peak did not rise on refinement: {runs[i - 1].Symmetrized:F3} -> {runs[i].Symmetrized:F3}");
                Assert.True(runs[i].Asymmetry < runs[i - 1].Asymmetry,
                    $"the mesh's own asymmetry did not shrink: "
                    + $"{runs[i - 1].Asymmetry:P2} -> {runs[i].Asymmetry:P2}");
                Assert.True(runs[i].Symmetrized < expectedPeak,
                    $"peak {runs[i].Symmetrized:F3} passed Howland's {expectedPeak:F3} from below");
            }
        }

        // Kirsch's LOCATION, held to the mesh's own accuracy. The maximum anywhere on the
        // hole may exceed the largest reading at the theoretical points only by less than
        // the spread the mesh already admits across four nodes that theory says are
        // identical — a self-calibrating bar, because a claim about where the peak is
        // cannot be sharper than the discretization that measures it. (On a coarse
        // quadratic mesh the winner is a MID-EDGE node a few degrees away, whose position
        // is a chord midpoint and therefore sits inside the true circle: a straight-sided
        // element approximates a curved boundary from within.)
        foreach (var run in linear.Concat(quadratic))
        {
            double larger = run.PeakNodes.Max();
            double excess = run.HoleMaximum / larger - 1.0;
            Assert.True(excess <= run.Asymmetry + 1e-3,
                $"a point elsewhere on the hole ({run.HoleMaximum:F3}) beat the theoretical "
                + $"location ({larger:F3}) by {excess:P2}, more than the mesh's own "
                + $"{run.Asymmetry:P2} spread");
        }

        double linearError = Math.Abs(linear[^1].Symmetrized - expectedPeak) / expectedPeak;
        double quadraticError = Math.Abs(quadratic[^1].Symmetrized - expectedPeak) / expectedPeak;
        output.WriteLine("");
        output.WriteLine(
            $"finest linear    K_tn = {linear[^1].Symmetrized / NetStress:F4} "
            + $"({(linear[^1].Symmetrized / expectedPeak - 1) * 100:+0.00;-0.00}% vs Howland), "
            + $"mesh asymmetry {linear[^1].Asymmetry:P2}");
        output.WriteLine(
            $"finest quadratic K_tn = {quadratic[^1].Symmetrized / NetStress:F4} "
            + $"({(quadratic[^1].Symmetrized / expectedPeak - 1) * 100:+0.00;-0.00}% vs Howland), "
            + $"mesh asymmetry {quadratic[^1].Asymmetry:P2}");

        Assert.True(quadraticError < 0.02,
            $"quadratic peak is {quadraticError * 100:F2}% from Howland's {expectedPeak:F3} MPa");
        Assert.True(linearError < 0.05,
            $"linear peak is {linearError * 100:F2}% from Howland's {expectedPeak:F3} MPa");
    }

    private void Report(string label, Run[] runs, double expectedPeak)
    {
        output.WriteLine(
            $"{label,-10} {"n_theta",8} {"n_r",5} {"elements",10} {"free DOF",9} "
            + $"{"low MPa",9} {"high MPa",9} {"mean",9} {"K_tn",7} {"err %",7} {"spread%",8} {"far MPa",9}");
        foreach (var run in runs)
        {
            output.WriteLine(
                $"{"",-10} {run.Theta,8} {run.Radial,5} {run.Elements,10:N0} {run.FreeDofs,9:N0} "
                + $"{run.PeakNodes.Min(),9:F1} {run.PeakNodes.Max(),9:F1} {run.Symmetrized,9:F1} "
                + $"{run.Symmetrized / NetStress,7:F4} {(run.Symmetrized / expectedPeak - 1) * 100,7:F2} "
                + $"{run.Asymmetry * 100,8:F2} {run.FarField,9:F2}");
        }
    }

    private static int NearestNode(AnalysisMesh mesh, Vector3d target)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            double d = mesh.Position(v).DistanceSquaredTo(target);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = v;
            }
        }
        return best;
    }
}
