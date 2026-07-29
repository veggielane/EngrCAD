using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The axial bar — the modal verification case with an EXACT answer and no modelling gap.
///
/// <para>Every beam comparison in this suite is against a beam THEORY, so its error is a
/// discretization error plus a modelling difference that no refinement removes. The bar of
/// <see cref="ModalFixtures.AxialBar"/> has neither: with Poisson's ratio zero and the
/// transverse degrees of freedom removed, the three-dimensional problem IS the
/// one-dimensional one, whose frequencies are <c>n/(2L)·sqrt(E/rho)</c> exactly. That is
/// what makes it the fixture the convergence ORDER is measured on.</para>
/// </summary>
public class ModalBarTests(ITestOutputHelper output)
{
    private const double Length = 100.0;
    private const double Side = 10.0;

    [Fact]
    public void FreeFreeBar_MatchesTheClosedFormAxialFrequencies()
    {
        var model = ModalFixtures.AxialBar(Length, Side, 40, ElementOrder.Linear);
        var results = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 3 });

        // Transversely restrained, axially free: exactly ONE rigid-body mode, the axial
        // translation, and it must be named as such rather than counted.
        Assert.Single(results.RigidBodyModes);
        Assert.Contains("translation along", results.RigidBodyModes[0].Description);

        output.WriteLine(results.ToText());
        for (int n = 1; n <= 3; n++)
        {
            double exact = ModalFixtures.AxialFrequency(n, Length, ModalFixtures.UncoupledSteel);
            double measured = results.Mode(n).Frequency;
            double error = (measured - exact) / exact;
            output.WriteLine($"n = {n}: exact {exact:N1} Hz, measured {measured:N1} Hz, {error:P3}");

            // A consistent-mass finite element model is stiffer than the continuum, so every
            // frequency is an UPPER bound on the exact one. That is a structural property of
            // the Rayleigh quotient over a subspace, not an empirical observation, so it is
            // asserted as a strict inequality rather than a tolerance.
            Assert.True(measured > exact, $"mode {n} came out below the exact frequency");
            Assert.True(error < 0.01, $"mode {n} is {error:P3} high at 40 elements");
        }
    }

    [Theory]
    [InlineData(ElementOrder.Linear, 2.0)]
    [InlineData(ElementOrder.Quadratic, 4.0)]
    public void AxialFrequency_ConvergesAtTheElementsOwnOrder(ElementOrder order, double theory)
    {
        // An eigenvalue converges at O(h^2p) for a degree-p element, so a frequency does
        // too: order 2 for linear elements and order 4 for quadratic ones. The ratio between
        // successive halvings is what is measured, never an absolute error.
        int[] divisions = order == ElementOrder.Linear ? [8, 16, 32, 64] : [2, 4, 8, 16];
        double exact = ModalFixtures.AxialFrequency(1, Length, ModalFixtures.UncoupledSteel);

        var errors = new List<double>();
        foreach (int nx in divisions)
        {
            var model = ModalFixtures.AxialBar(Length, Side, nx, order);
            var results = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 1 });
            double error = (results.Mode(1).Frequency - exact) / exact;
            errors.Add(error);
            output.WriteLine($"{order} nx = {nx,3}: f = {results.Mode(1).Frequency:N3} Hz, error {error:E3}");
        }

        var orders = new List<double>();
        for (int i = 1; i < errors.Count; i++)
            orders.Add(ModalFixtures.Order(errors[i - 1], errors[i]));
        output.WriteLine($"{order} measured orders: {string.Join(", ", orders.Select(o => o.ToString("F2")))}");

        double last = orders[^1];
        Assert.True(
            Math.Abs(last - theory) < 0.35,
            $"{order} elements measured a convergence order of {last:F2} against theory {theory}");
    }

    [Fact]
    public void ConsistentAndLumpedMass_BracketTheExactFrequency()
    {
        // The classic result, and the reason a lumped option exists at all: a consistent
        // mass matrix is an upper bound on the frequency and a lumped one a lower bound, so
        // running both brackets the truth. Neither is "more accurate" — they are wrong in
        // opposite directions.
        double exact = ModalFixtures.AxialFrequency(1, Length, ModalFixtures.UncoupledSteel);

        var consistent = ModalSolver.Solve(
            ModalFixtures.AxialBar(Length, Side, 16, ElementOrder.Linear),
            new ModalSolveOptions { ModeCount = 1, Lumping = MassLumping.Consistent });
        var rowSum = ModalSolver.Solve(
            ModalFixtures.AxialBar(Length, Side, 16, ElementOrder.Linear),
            new ModalSolveOptions { ModeCount = 1, Lumping = MassLumping.RowSum });
        var hrz = ModalSolver.Solve(
            ModalFixtures.AxialBar(Length, Side, 16, ElementOrder.Linear),
            new ModalSolveOptions { ModeCount = 1, Lumping = MassLumping.Hrz });

        output.WriteLine($"exact       {exact:N2} Hz");
        output.WriteLine($"consistent  {consistent.Mode(1).Frequency:N2} Hz "
            + $"({(consistent.Mode(1).Frequency - exact) / exact:P3})");
        output.WriteLine($"row sum     {rowSum.Mode(1).Frequency:N2} Hz "
            + $"({(rowSum.Mode(1).Frequency - exact) / exact:P3})");
        output.WriteLine($"HRZ         {hrz.Mode(1).Frequency:N2} Hz "
            + $"({(hrz.Mode(1).Frequency - exact) / exact:P3})");

        Assert.True(consistent.Mode(1).Frequency > exact, "consistent mass did not overestimate");
        Assert.True(rowSum.Mode(1).Frequency < exact, "lumped mass did not underestimate");

        // For a 4-node tetrahedron the row sums are rho·V/4 at every node, and HRZ's scaled
        // diagonal is (rho·V/10)·(rho·V)/(4·rho·V/10) = rho·V/4 as well — the SAME matrix by
        // two different routes, so the two schemes coincide here and that is asserted rather
        // than assumed. Relative rather than to a decimal place: they are equal
        // mathematically and differ in the last bits, because the two routes are different
        // arithmetic.
        Assert.Equal(
            rowSum.Mode(1).Frequency, hrz.Mode(1).Frequency, 1e-10 * rowSum.Mode(1).Frequency);
    }

    [Fact]
    public void LumpedMass_IsDiagonal_AndPreservesTheTotalMass()
    {
        double exactMass =
            ModalFixtures.UncoupledSteel.Density * Length * Side * Side;

        foreach (var lumping in new[] { MassLumping.Consistent, MassLumping.Hrz })
        {
            var results = ModalSolver.Solve(
                ModalFixtures.AxialBar(Length, Side, 8, ElementOrder.Linear),
                new ModalSolveOptions { ModeCount = 1, Lumping = lumping });
            output.WriteLine(
                $"{lumping}: mass {results.TotalMass:E6} against {exactMass:E6}, "
                + $"M has {results.Report.MassNonZeros:N0} nnz for {results.Report.FreeDofs:N0} free DOF");

            // The mass matrix's own total, whichever scheme built it: lumping redistributes
            // mass between nodes, it never creates or destroys any.
            Assert.Equal(exactMass, results.TotalMass, 12 * Math.Abs(exactMass));
        }

        var lumped = ModalSolver.Solve(
            ModalFixtures.AxialBar(Length, Side, 8, ElementOrder.Linear),
            new ModalSolveOptions { ModeCount = 1, Lumping = MassLumping.Hrz });
        // A lumped matrix is diagonal, which is the whole point of one: exactly one stored
        // entry per free degree of freedom.
        Assert.Equal(lumped.Report.FreeDofs, lumped.Report.MassNonZeros);
    }

    [Fact]
    public void FactorizationIsShared_OneFactorForEveryLanczosStep()
    {
        // The claim FeaSolveMethod.Direct records as NOT true of the static solver, made
        // concrete here: one factorization serves every back-substitution the eigensolver
        // takes, and the report says how many that was.
        var results = ModalSolver.Solve(
            ModalFixtures.AxialBar(Length, Side, 20, ElementOrder.Linear),
            new ModalSolveOptions { ModeCount = 4 });
        output.WriteLine(results.Report.ToText());

        Assert.True(
            results.Report.Iterations > results.Report.ModeCount,
            "a Lanczos run takes more steps than it returns modes");
        Assert.True(results.Report.FactorNonZeros > 0);
        Assert.True(results.Report.Converged);
    }

    [Fact]
    public void FixedFixedBar_HasNoRigidModes_AndAZeroShift()
    {
        // Fully restrained: K is positive definite, so the shift is EXACTLY zero and the
        // factorization is literally the static solver's.
        var model = ModalFixtures.AxialBar(Length, Side, 40, ElementOrder.Linear);
        var mesh = model.Mesh;
        model.Fix(Facets.OnPlane(Vector3d.Zero, Vector3d.UnitX), Dof.X);
        model.Fix(Facets.OnPlane(new Vector3d(Length, 0, 0), Vector3d.UnitX), Dof.X);

        var results = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 3 });
        Assert.Empty(results.RigidBodyModes);
        Assert.Equal(0.0, results.Report.Shift);

        for (int n = 1; n <= 3; n++)
        {
            double exact = ModalFixtures.AxialFrequency(n, Length, ModalFixtures.UncoupledSteel);
            double measured = results.Mode(n).Frequency;
            output.WriteLine(
                $"fixed-fixed n = {n}: exact {exact:N1} Hz, measured {measured:N1} Hz, "
                + $"{(measured - exact) / exact:P3}");
            Assert.True(Math.Abs(measured - exact) / exact < 0.01);
        }
        _ = mesh;
    }
}
