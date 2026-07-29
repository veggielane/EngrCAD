using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Thermal-to-structural coupling: a temperature field becomes an initial strain
/// <c>eps0 = alpha·dT</c>, and the two textbook bars are the verification.
///
/// <para><b>Both cases are exact to round-off, and that is why they were chosen.</b> The
/// free bar's answer is the linear displacement field <c>alpha·dT·x</c> and the constrained
/// bar's is a uniform strain state; both lie inside BOTH element spaces, so a correct
/// implementation reproduces them at machine precision and there is no discretization
/// error to hide a factor-of-two in. Between them they pin the two halves of the coupling
/// that can be got wrong independently: the LOAD (the free bar expands by the right amount)
/// and the STRESS RECOVERY (the free bar carries no stress while doing it).</para>
/// </summary>
public class ThermalCouplingTests(ITestOutputHelper output)
{
    /// <summary>Steel with a round expansion coefficient, so <c>E·alpha·dT</c> is
    /// arithmetic: 210000 × 12e-6 = 2.52 MPa per kelvin.</summary>
    private static readonly Material Steel = new(
        "coupling steel", 210_000, 0.3, 7.85e-9,
        thermalConductivity: 50.0, specificHeat: 4.6e8, thermalExpansion: 12e-6);

    private const double Length = 60, Width = 20, Thickness = 10;

    private static AnalysisMesh Mesh(ElementOrder order)
    {
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(Length, Width, Thickness), 3, 2, 2);
        return order == ElementOrder.Linear ? AnalysisMesh.Of(tets) : AnalysisMesh.Quadratic(tets);
    }

    /// <summary>
    /// <b>The free bar.</b> Three symmetry rollers hold the body against rigid motion while
    /// leaving it free to grow in every direction; a uniform temperature rise then produces
    /// the pure dilatation <c>u = alpha·dT·x</c> and <b>zero stress</b>.
    ///
    /// <para>The zero-stress half is the one that catches a missing subtraction in the
    /// recovery, and it is not a subtle error: without it the bar would report
    /// <c>E·alpha·dT</c> = 126 MPa at a 50 K rise, on a body under no load at all.</para>
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void FreeBar_ExpandsWithoutStress(ElementOrder order)
    {
        const double deltaT = 50;
        var mesh = Mesh(order);
        var model = new StructuralModel(mesh, Steel)
            // Three symmetry planes: each holds ONE component on its own face, so the body
            // is restrained against all six rigid motions and free to expand.
            .Fix(Facets.OnPlane(Vector3d.Zero, Vector3d.UnitX), Dof.X)
            .Fix(Facets.OnPlane(Vector3d.Zero, Vector3d.UnitY), Dof.Y)
            .Fix(Facets.OnPlane(Vector3d.Zero, Vector3d.UnitZ), Dof.Z)
            .UniformThermalLoad(deltaT);

        var results = StructuralSolver.Solve(model);

        double expansion = Steel.ThermalExpansion * deltaT;
        double expectedTip = expansion * Length;

        // Displacement: u = alpha.dT.x at every node, in all three directions.
        double worstDisplacement = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            var expected = mesh.Position(v) * expansion;
            worstDisplacement = Math.Max(
                worstDisplacement, (results.DisplacementAt(v) - expected).Length);
        }

        double peakStress = results.MaxVonMises;
        double spurious = Steel.YoungsModulus * expansion;   // what a missing subtraction gives

        output.WriteLine($"{order}: dT = {deltaT} K, alpha.dT = {expansion:E4}");
        output.WriteLine(
            $"  free growth over {Length} mm: {expectedTip:F6} mm expected, "
            + $"worst nodal displacement error {worstDisplacement:E3} mm "
            + $"({worstDisplacement / expectedTip:E3} relative)");
        output.WriteLine(
            $"  peak von Mises {peakStress:E3} MPa against E.alpha.dT = {spurious:F2} MPa "
            + $"-> {peakStress / spurious:E3} of the value a missing eps0 subtraction gives");
        output.WriteLine(
            $"  applied force resultant {results.Report.AppliedForce.Length:E3} N "
            + "(a thermal load is self-equilibrated)");
        output.WriteLine($"  equilibrium residual {results.Report.EquilibriumResidual:E3}");

        Assert.True(worstDisplacement / expectedTip < 1e-11, $"{worstDisplacement:E3}");
        Assert.True(peakStress / spurious < 1e-11, $"peak von Mises {peakStress:E3} MPa");
        // A thermal load's nodal forces sum to exactly zero, so it adds nothing to the
        // applied resultant and the equilibrium check keeps its meaning.
        Assert.True(results.Report.AppliedForce.Length < 1e-9);
        Assert.True(results.Report.EquilibriumResidual < 1e-12);
    }

    /// <summary>
    /// <b>The constrained bar.</b> Held against expansion along x at both ends but free to
    /// grow sideways, a uniform temperature rise produces the classic
    /// <c>sigma_xx = -E·alpha·dT</c> — compressive, and independent of the bar's length and
    /// of Poisson's ratio.
    ///
    /// <para>That independence is what makes the case a real check rather than a
    /// tautology. The stress falls out of <c>sigma = D(eps - eps0)</c> with
    /// <c>eps_xx = 0</c> and <c>sigma_yy = sigma_zz = 0</c>: the lateral strain settles at
    /// <c>beta = (3.lambda + 2.mu)·alpha·dT / (2.lambda + 2.mu)</c> and substituting it
    /// collapses the axial stress to <c>-mu(3.lambda+2.mu)/(lambda+mu)·alpha·dT</c>, which
    /// is exactly <c>-E·alpha·dT</c>. A coupling that used the wrong modulus — E instead of
    /// <c>E/(1-2.nu)</c> in the load, say — gets the free bar right and this one wrong by
    /// <c>1/(1-2.nu)</c> = 2.5 at nu = 0.3.</para>
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void ConstrainedBar_CarriesMinusEAlphaDeltaT(ElementOrder order)
    {
        const double deltaT = 50;
        var mesh = Mesh(order);
        var model = new StructuralModel(mesh, Steel)
            // Both ends held against axial motion, and two symmetry planes so the bar can
            // still grow sideways: eps_xx = 0 with sigma_yy = sigma_zz = 0.
            .Fix(Facets.OnPlane(Vector3d.Zero, Vector3d.UnitX), Dof.X)
            .Fix(Facets.OnPlane(new Vector3d(Length, 0, 0), Vector3d.UnitX), Dof.X)
            .Fix(Facets.OnPlane(Vector3d.Zero, Vector3d.UnitY), Dof.Y)
            .Fix(Facets.OnPlane(Vector3d.Zero, Vector3d.UnitZ), Dof.Z)
            .UniformThermalLoad(deltaT);

        var results = StructuralSolver.Solve(model);

        double expected = -Steel.YoungsModulus * Steel.ThermalExpansion * deltaT;
        double lateral =
            (3 * Steel.Lambda + 2 * Steel.Mu) * Steel.ThermalExpansion * deltaT
            / (2 * Steel.Lambda + 2 * Steel.Mu);

        double worstAxial = 0, worstLateral = 0, worstShear = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var stress = results.ElementStress(e);
            worstAxial = Math.Max(worstAxial, Math.Abs(stress.Xx - expected));
            worstLateral = Math.Max(worstLateral, Math.Max(Math.Abs(stress.Yy), Math.Abs(stress.Zz)));
            worstShear = Math.Max(
                worstShear,
                Math.Max(Math.Abs(stress.Xy), Math.Max(Math.Abs(stress.Yz), Math.Abs(stress.Xz))));
        }

        // The lateral growth the analytic solution predicts, at the far corner.
        int corner = 0;
        double best = double.MinValue;
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            var p = mesh.Position(v);
            if (p.Y + p.Z > best)
            {
                best = p.Y + p.Z;
                corner = v;
            }
        }
        var cornerPosition = mesh.Position(corner);
        var expectedCorner = new Vector3d(0, lateral * cornerPosition.Y, lateral * cornerPosition.Z);
        double cornerError = (results.DisplacementAt(corner) - expectedCorner).Length;

        output.WriteLine(
            $"{order}: dT = {deltaT} K, E.alpha.dT = "
            + $"{Steel.YoungsModulus * Steel.ThermalExpansion * deltaT:F4} MPa");
        output.WriteLine(
            $"  sigma_xx: worst |measured - ({expected:F4})| = {worstAxial:E3} MPa "
            + $"-> {worstAxial / Math.Abs(expected):E3} relative");
        output.WriteLine(
            $"  sigma_yy, sigma_zz: worst {worstLateral:E3} MPa (must vanish); "
            + $"shear worst {worstShear:E3} MPa");
        output.WriteLine(
            $"  lateral strain beta = {lateral:E4}; corner at {cornerPosition} moves "
            + $"{results.DisplacementAt(corner)}, expected {expectedCorner}, error {cornerError:E3}");
        output.WriteLine(
            $"  reaction resultant {results.Report.ReactionForce.Length:E3} N, "
            + $"equilibrium {results.Report.EquilibriumResidual:E3}");

        Assert.True(worstAxial / Math.Abs(expected) < 1e-11, $"{worstAxial:E3} MPa");
        Assert.True(worstLateral / Math.Abs(expected) < 1e-11, $"{worstLateral:E3} MPa");
        Assert.True(worstShear / Math.Abs(expected) < 1e-11, $"{worstShear:E3} MPa");
        Assert.True(cornerError / (lateral * Width) < 1e-10, $"{cornerError:E3} mm");
        Assert.True(results.Report.EquilibriumResidual < 1e-12);
    }

    /// <summary>
    /// The whole pipeline: solve conduction, hand the temperature field to a structural
    /// model over the SAME mesh, solve again.
    ///
    /// <para><b>The claim being checked is that a LINEAR temperature field is stress-free
    /// in an unconstrained body</b>, which is more than the uniform case and is the exact
    /// condition: a thermal strain <c>eps0 = alpha·dT·I</c> is compatible — some
    /// displacement field produces it — precisely when <c>dT</c> is affine in position,
    /// since Saint-Venant then forces every second derivative of it to vanish. Real thermal
    /// stress comes from a CONSTRAINT or from a curved profile, and this pins that nothing
    /// spurious appears before either.</para>
    ///
    /// <para><b>Two traps, and the first version of this test fell into both.</b> The
    /// stress-free displacement field for <c>dT = a + b·x</c> is <b>quadratic</b>, not
    /// linear: <c>u = (a·x + b·x²/2 - b(y² + z²)/2, (a + b·x)y, (a + b·x)z)</c>, where the
    /// <c>-(b/2)(y² + z²)</c> term exists solely to cancel the shear <c>b·y</c> that
    /// <c>u_y = (a+bx)y</c> otherwise introduces. Which means, second, that holding whole
    /// symmetry PLANES over-constrains it — <c>u_x</c> is not zero on the <c>x = 0</c> face
    /// — and the model then genuinely carries stress (measured 67 MPa, a quarter of
    /// <c>E·alpha·dT</c>, from restraints alone). A statically determinate 3-2-1 restraint
    /// removes the six rigid motions and nothing else, which is what "unconstrained"
    /// has to mean here.</para>
    ///
    /// <para>Displacement is therefore checked as a DISTANCE between two nodes, which is
    /// invariant under the rigid motion the 3-2-1 scheme happens to pick, while stress is
    /// invariant anyway.</para>
    /// </summary>
    [Fact]
    public void ThermalSolveDrivesStructuralSolve_OnOneMesh()
    {
        const double hot = 120, cold = 20, reference = 20;
        var mesh = Mesh(ElementOrder.Quadratic);

        var thermal = new ThermalModel(mesh, Steel)
            .Temperature(StructuredTetMesh.XMin, hot)
            .Temperature(StructuredTetMesh.XMax, cold);
        var temperature = ThermalSolver.Solve(thermal);

        int origin = NodeAt(mesh, Vector3d.Zero);
        int farEnd = NodeAt(mesh, new Vector3d(Length, 0, 0));
        int sideNode = NodeAt(mesh, new Vector3d(0, Width, 0));

        var structural = new StructuralModel(mesh, Steel)
            // 3-2-1: six degrees of freedom removed, no more.
            .FixNode(origin, Dof.All)
            .FixNode(farEnd, Dof.Y | Dof.Z)
            .FixNode(sideNode, Dof.Z)
            .ThermalLoad(temperature, reference);

        var results = StructuralSolver.Solve(structural);

        // The axial growth is alpha times the INTEGRAL of dT along the bar, which is a
        // distance and so survives the rigid motion the restraint picked.
        double alpha = Steel.ThermalExpansion;
        double gradient = (cold - hot) / Length;
        double expectedGrowth = alpha * ((hot - reference) * Length + 0.5 * gradient * Length * Length);

        double before = mesh.Position(farEnd).DistanceTo(mesh.Position(origin));
        double after = (mesh.Position(farEnd) + results.DisplacementAt(farEnd))
            .DistanceTo(mesh.Position(origin) + results.DisplacementAt(origin));
        double growth = after - before;

        double spurious = Steel.YoungsModulus * alpha * (hot - reference);
        output.WriteLine(
            $"conduction {temperature.MinTemperature:F2} to {temperature.MaxTemperature:F2} C, "
            + $"energy balance {temperature.Report.EnergyBalanceResidual:E2}");
        output.WriteLine(
            $"axis length {before:F4} -> {after:F6} mm, growth {growth:F6} mm against "
            + $"alpha.integral(dT dx)/L = {expectedGrowth:F6} mm "
            + $"({Math.Abs(growth - expectedGrowth) / expectedGrowth:E2} relative)");
        output.WriteLine(
            $"peak von Mises {results.MaxVonMises:E3} MPa against E.alpha.dT_max = "
            + $"{spurious:F2} MPa -> {results.MaxVonMises / spurious:E3}");
        output.WriteLine($"equilibrium residual {results.Report.EquilibriumResidual:E3}");

        Assert.True(Math.Abs(growth - expectedGrowth) / expectedGrowth < 1e-9,
            $"growth {growth:F8} against {expectedGrowth:F8}");
        Assert.True(results.MaxVonMises / spurious < 1e-10,
            $"free expansion produced {results.MaxVonMises:E3} MPa");
        Assert.True(results.Report.EquilibriumResidual < 1e-10);
    }

    /// <summary>The node at an exact position — the structured fixture puts nodes on grid
    /// points, so this is an exact-bit lookup rather than a nearest search.</summary>
    private static int NodeAt(AnalysisMesh mesh, Vector3d position)
    {
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            if (mesh.Position(v) == position)
                return v;
        }
        throw new InvalidOperationException($"no node at {position}");
    }

    /// <summary>
    /// The stress scales linearly with the temperature rise, and the sign is right in both
    /// directions: heating a constrained bar compresses it, cooling it puts it in tension.
    /// <para>Cheap, and it catches an absolute-value or a squared term that both bars above
    /// would pass at a single positive dT.</para>
    /// </summary>
    [Theory]
    [InlineData(-40)]
    [InlineData(-5)]
    [InlineData(5)]
    [InlineData(80)]
    public void ConstrainedBarStress_IsLinearInDeltaTAndSignedCorrectly(double deltaT)
    {
        var mesh = Mesh(ElementOrder.Linear);
        var model = new StructuralModel(mesh, Steel)
            .Fix(Facets.OnPlane(Vector3d.Zero, Vector3d.UnitX), Dof.X)
            .Fix(Facets.OnPlane(new Vector3d(Length, 0, 0), Vector3d.UnitX), Dof.X)
            .Fix(Facets.OnPlane(Vector3d.Zero, Vector3d.UnitY), Dof.Y)
            .Fix(Facets.OnPlane(Vector3d.Zero, Vector3d.UnitZ), Dof.Z)
            .UniformThermalLoad(deltaT);

        var results = StructuralSolver.Solve(model);
        double expected = -Steel.YoungsModulus * Steel.ThermalExpansion * deltaT;
        double measured = results.ElementStress(0).Xx;

        output.WriteLine(
            $"dT = {deltaT,5} K -> sigma_xx {measured,10:F5} MPa, expected {expected,10:F5} "
            + $"({Math.Abs(measured - expected) / Math.Abs(expected):E2} relative)");

        Assert.Equal(Math.Sign(-deltaT), Math.Sign(measured));
        Assert.True(Math.Abs(measured - expected) / Math.Abs(expected) < 1e-11);
    }
}
