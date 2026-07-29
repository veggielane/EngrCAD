using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Euler column buckling for all four classical end conditions, plus the refinement study
/// that says the agreement is convergence rather than luck.
///
/// <para>Every comparison carries the same modelling caveat the modal beam tests carry, in
/// its buckling form: Euler's derivation has no shear deformation, a three-dimensional solid
/// has it, and it SOFTENS the column — so the measured critical load converges to a value
/// BELOW the Euler one by Engesser's ratio (0.5% here for the pinned-pinned column, 1.9% for
/// the fixed-fixed one, whose Euler load is four times larger). Both numbers are reported and
/// the assertion is against the shear-corrected value.</para>
/// </summary>
public class BucklingColumnTests(ITestOutputHelper output)
{
    private const double Length = 120.0;
    private const double Side = 6.0;

    private static readonly ColumnEnds[] AllEnds =
    [
        ColumnEnds.PinnedPinned,
        ColumnEnds.FixedFree,
        ColumnEnds.FixedPinned,
        ColumnEnds.FixedFixed,
    ];

    [Fact]
    public void EulerColumn_AllFourEndConditions_MatchTheClosedForm()
    {
        foreach (var ends in AllEnds)
        {
            var (model, reference) = BucklingFixtures.Column(
                ends, Length, Side, 24, 2, ElementOrder.Quadratic);
            var statics = StructuralSolver.Solve(model);
            var buckling = BucklingSolver.Solve(
                statics, new BucklingSolveOptions { ModeCount = 2 });

            double euler = BucklingFixtures.EulerLoad(
                ends, Length, Side, BucklingFixtures.Material);
            double corrected = BucklingFixtures.ShearCorrectedLoad(
                ends, Length, Side, BucklingFixtures.Material);
            double measured = buckling.CriticalLoadFactor * reference;

            output.WriteLine(
                $"{ends,-12} K = {BucklingFixtures.EffectiveLengthFactor(ends):F4}: "
                + $"Euler {euler:N1} N, Engesser {corrected:N1} N "
                + $"({BucklingFixtures.EngesserRatio(ends, Length, Side, BucklingFixtures.Material) - 1:P2}), "
                + $"measured {measured:N1} N  ->  "
                + $"{(measured - euler) / euler:P3} from Euler, "
                + $"{(measured - corrected) / corrected:P3} from Engesser "
                + $"(factor {buckling.CriticalLoadFactor:G6}, residual {buckling.Mode(1).Residual:E1})");

            Assert.Equal(0.0, buckling.Report.Shift);
            Assert.True(
                Math.Abs(measured - corrected) / corrected < 0.01,
                $"{ends}: measured {measured:N2} N against a shear-corrected "
                + $"{corrected:N2} N, {(measured - corrected) / corrected:P3}");
            Assert.True(buckling.Mode(1).Residual < 1e-7);

            // The second mode is a real one, not a repeat: the section is SQUARE, so every
            // buckling mode is a degenerate PAIR (the column can bow in Y or in Z), which is
            // the configuration a single-vector Lanczos cannot see without locking and
            // restarting. Its appearance here is that machinery working on a second physics.
            double split = Math.Abs(buckling.Mode(2).LoadFactor - buckling.Mode(1).LoadFactor)
                / buckling.Mode(1).LoadFactor;
            output.WriteLine($"    degenerate pair splits by {split:P4}");
            Assert.True(split < 0.02, $"{ends}: modes 1 and 2 should be a pair, split {split:P4}");
        }
    }

    [Fact]
    public void FixedFixedColumn_ReactionMatchesTheAnalyticPrestress()
    {
        // The one fixture loaded by an ENFORCED displacement, so its reference load is
        // computed rather than applied. With nu = 0 the stress is exactly E·delta/L, and
        // this is the assertion that says so before any eigenvalue is believed.
        var (model, reference) = BucklingFixtures.Column(
            ColumnEnds.FixedFixed, Length, Side, 16, 2, ElementOrder.Quadratic);
        var statics = StructuralSolver.Solve(model);

        var reaction = Vector3d.Zero;
        for (int node = 0; node < model.Mesh.NodeCount; node++)
        {
            if (model.RestraintOf(node).HasFlag(Dof.X)
                && model.Mesh.Position(node).X <= 0.5 * Length)
                reaction += new Vector3d(statics.ReactionAt(node).X, 0, 0);
        }

        output.WriteLine(
            $"enforced shortening reaction {reaction.X:N4} N against an analytic "
            + $"{reference:N4} N, {(reaction.X - reference) / reference:P6}");
        Assert.Equal(reference, reaction.X, 6 * Math.Abs(reference) * 1e-9);

        // And the stress field really is uniform, which is what makes Kg exact.
        double worst = 0;
        for (int e = 0; e < model.Mesh.ElementCount; e++)
        {
            var s = statics.ElementStress(e);
            worst = Math.Max(worst, Math.Abs(s.Xx + reference / (Side * Side)));
            worst = Math.Max(worst, Math.Abs(s.Yy));
            worst = Math.Max(worst, Math.Abs(s.Zz));
        }
        double scale = reference / (Side * Side);
        output.WriteLine($"worst deviation from a uniform -{scale:G6} MPa: {worst:E3} ({worst / scale:E2} relative)");
        Assert.True(worst / scale < 1e-12, $"the prestress is not uniform: {worst / scale:E2}");
    }

    [Fact]
    public void PinnedColumn_ConvergesFromAboveWithRefinement()
    {
        double euler = BucklingFixtures.EulerLoad(
            ColumnEnds.PinnedPinned, Length, Side, BucklingFixtures.Material);
        double corrected = BucklingFixtures.ShearCorrectedLoad(
            ColumnEnds.PinnedPinned, Length, Side, BucklingFixtures.Material);

        var measured = new List<double>();
        foreach (var (nx, across) in new[] { (4, 1), (8, 1), (16, 2), (32, 2) })
        {
            var (model, reference) = BucklingFixtures.Column(
                ColumnEnds.PinnedPinned, Length, Side, nx, across, ElementOrder.Quadratic);
            var buckling = BucklingSolver.Solve(
                StructuralSolver.Solve(model), new BucklingSolveOptions { ModeCount = 1 });
            double load = buckling.CriticalLoadFactor * reference;
            measured.Add(load);
            output.WriteLine(
                $"{nx}x{across}x{across} ({model.Mesh.ElementCount:N0} elements, "
                + $"{buckling.Report.FreeDofs:N0} DOF): {load:N2} N, "
                + $"{(load - euler) / euler:P4} from Euler, "
                + $"{(load - corrected) / corrected:P4} from Engesser, "
                + $"{buckling.Report.Iterations} Lanczos steps");
        }

        // Monotone from above: the discrete load factor is a Rayleigh quotient minimised over
        // the finite element subspace, and a coarser mesh is a smaller subspace. That is a
        // theorem about the method — and it holds STRICTLY here only because the prestress is
        // exact (see BucklingFixtures on why nu = 0 buys that), so it is the assertion worth
        // making rather than a tolerance on the finest value alone.
        for (int i = 1; i < measured.Count; i++)
        {
            Assert.True(
                measured[i] <= measured[i - 1],
                $"refinement {i} RAISED the critical load from {measured[i - 1]:N3} to "
                + $"{measured[i]:N3}, which a Rayleigh quotient over a growing subspace cannot do");
        }

        // The finest mesh sits below Euler, by about the shear correction — the direction is
        // asserted and the size is reported, exactly as the modal beam tests do with
        // Timoshenko.
        Assert.InRange((measured[^1] - euler) / euler, -0.02, 0.0);
        Assert.True(Math.Abs(measured[^1] - corrected) / corrected < 0.005);
    }

    [Fact]
    public void LinearTetrahedraAreUnusableForBuckling_AndTheGapIsMeasured()
    {
        // A finding rather than a check, and it is a strong one: 4-node tetrahedra are known
        // to be too stiff in bending, and a buckling load is a RATIO of a bending stiffness
        // to a geometric softening — so the over-stiffness enters the answer undiluted
        // instead of being averaged against anything. Where the static cantilever's tip
        // deflection is 14% low at 12 288 linear elements, the same elements put a column's
        // critical load an ORDER OF MAGNITUDE high on a coarse mesh, and they are still 20%+
        // high where the quadratic answer has converged to 0.2%. Measured here so the README
        // can say so with numbers instead of adjectives.
        double corrected = BucklingFixtures.ShearCorrectedLoad(
            ColumnEnds.PinnedPinned, Length, Side, BucklingFixtures.Material);

        var linear = new List<double>();
        foreach (var order in new[] { ElementOrder.Linear, ElementOrder.Quadratic })
        {
            var grids = order == ElementOrder.Linear
                ? new[] { (8, 1), (16, 2), (32, 3), (48, 4) }
                : [(8, 1), (16, 2), (32, 3)];
            foreach (var (nx, across) in grids)
            {
                var (model, reference) = BucklingFixtures.Column(
                    ColumnEnds.PinnedPinned, Length, Side, nx, across, order);
                var buckling = BucklingSolver.Solve(
                    StructuralSolver.Solve(model), new BucklingSolveOptions { ModeCount = 1 });
                double load = buckling.CriticalLoadFactor * reference;
                output.WriteLine(
                    $"{order,-9} {nx}x{across}x{across} "
                    + $"({buckling.Report.FreeDofs:N0} DOF): {load:N2} N, "
                    + $"{(load - corrected) / corrected:P2} from Engesser");
                if (order == ElementOrder.Linear)
                    linear.Add(load);
            }
        }

        // Every one of them is ABOVE the truth — the subspace argument holds for a bad
        // element as well as a good one — and they descend towards it, slowly.
        for (int i = 1; i < linear.Count; i++)
            Assert.True(linear[i] < linear[i - 1], $"linear refinement {i} did not descend");
        Assert.True(
            linear[0] / corrected > 5,
            $"the coarse linear column measured only {linear[0] / corrected:F2}x the true load; "
            + "if linear tets have stopped locking this documented limitation needs rewriting");
        Assert.True(linear[^1] > corrected);
    }

    [Fact]
    public void LoadFactorIsInverselyProportionalToTheReferenceLoad()
    {
        // An exact identity, and the cheapest possible check that the geometric stiffness is
        // LINEAR in the prestress: doubling the reference load must halve the factor, so
        // their product — the critical load — is unchanged to round-off. Anything quadratic
        // in the stress, or any absolute epsilon hiding in the assembly, breaks it.
        var (model, reference) = BucklingFixtures.Column(
            ColumnEnds.FixedFree, Length, Side, 12, 1, ElementOrder.Quadratic);
        var single = BucklingSolver.Solve(
            StructuralSolver.Solve(model), new BucklingSolveOptions { ModeCount = 1 });

        var (doubled, doubledReference) = BucklingFixtures.Column(
            ColumnEnds.FixedFree, Length, Side, 12, 1, ElementOrder.Quadratic);
        doubled.ClearLoads();
        doubled.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(-2 * doubledReference, 0, 0));
        var twice = BucklingSolver.Solve(
            StructuralSolver.Solve(doubled), new BucklingSolveOptions { ModeCount = 1 });

        double a = single.CriticalLoadFactor * reference;
        double b = twice.CriticalLoadFactor * 2 * doubledReference;
        output.WriteLine(
            $"P = {reference:N0} N gives factor {single.CriticalLoadFactor:G12} -> {a:G12} N; "
            + $"P = {2 * doubledReference:N0} N gives {twice.CriticalLoadFactor:G12} -> {b:G12} N; "
            + $"relative difference {Math.Abs(a - b) / a:E2}");
        Assert.True(Math.Abs(a - b) / a < 1e-10);
        Assert.Equal(0.5, twice.CriticalLoadFactor / single.CriticalLoadFactor, 1e-10);
    }

    [Fact]
    public void AFineSlenderMeshHitsTheResidualFloor_AndTheRefusalSaysSoRatherThanBlamingThePhysics()
    {
        // Found by a refinement study rather than reasoned to, and worth pinning: the
        // measured relative residual |K phi - lambda Kg phi| / (|K phi| + |lambda||Kg phi|) is
        // a TOTAL cancellation of two products, each accurate to about eps·kappa(K), so it
        // has a floor proportional to the conditioning of the stiffness matrix. A slender
        // column meshed finely enough drives kappa past 1e9 and the 1e-9 default becomes
        // unreachable — not because the iteration stalls but because no arithmetic can get
        // there. The answer is still perfectly good; only the acceptance test is wrong.
        //
        // The assertion is therefore about the MESSAGE as much as the number: a refusal that
        // said "no positive buckling factor exists" here would send someone to look at their
        // load direction for a model whose critical load was sitting in the iteration the
        // whole time. This is also the measurement BucklingSolveOptions.Tolerance's default
        // of 1e-7 was chosen from, which is why the refusal has to be provoked by asking for
        // the modal solver's 1e-9 explicitly.
        var (model, reference) = BucklingFixtures.Column(
            ColumnEnds.PinnedPinned, Length, Side, 48, 4, ElementOrder.Quadratic);
        var statics = StructuralSolver.Solve(model);

        var refusal = Assert.Throws<FeaException>(
            () => BucklingSolver.Solve(
                statics,
                // One restart, not the default eight: the point is made by the first run and
                // the other eight are a minute of arithmetic that cannot succeed.
                new BucklingSolveOptions { ModeCount = 1, Tolerance = 1e-9, MaxRestarts = 1 }));
        output.WriteLine(refusal.Message);
        Assert.Contains("did not converge", refusal.Message);
        Assert.Contains("FOUND a candidate", refusal.Message);

        var relaxed = BucklingSolver.Solve(
            statics, new BucklingSolveOptions { ModeCount = 1, Tolerance = 1e-5 });
        double corrected = BucklingFixtures.ShearCorrectedLoad(
            ColumnEnds.PinnedPinned, Length, Side, BucklingFixtures.Material);
        double load = relaxed.CriticalLoadFactor * reference;
        output.WriteLine(
            $"at a 1e-5 tolerance ({relaxed.Report.FreeDofs:N0} DOF): {load:N2} N, "
            + $"{(load - corrected) / corrected:P4} from Engesser, "
            + $"measured residual {relaxed.Mode(1).Residual:E2}");

        // And the relaxed answer is a GOOD one, which is the point: an eigenvalue is accurate
        // to roughly the square of the residual over the spectral gap, so a 1e-5 residual is
        // still orders of magnitude finer than the mesh it is computed on.
        Assert.True(Math.Abs(load - corrected) / corrected < 0.005);
    }

    [Fact]
    public void BuckledShapeIsTheExpectedHalfWave()
    {
        // The mode shape, not just its eigenvalue: a pinned-pinned column's first buckling
        // mode is a half sine, so the lateral displacement peaks at mid-span and vanishes at
        // both ends. Checking the SHAPE is what separates "an eigenvalue near the Euler load"
        // from "the Euler buckling mode".
        var (model, _) = BucklingFixtures.Column(
            ColumnEnds.PinnedPinned, Length, Side, 24, 2, ElementOrder.Quadratic);
        var buckling = BucklingSolver.Solve(
            StructuralSolver.Solve(model), new BucklingSolveOptions { ModeCount = 1 });
        var mode = buckling.Mode(1);
        var mesh = model.Mesh;

        // Lateral magnitude against a half sine, sampled on the centroidal line.
        double peak = 0;
        foreach (var u in mode.Shape)
            peak = Math.Max(peak, Math.Sqrt(u.Y * u.Y + u.Z * u.Z));

        double worst = 0;
        int sampled = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            var p = mesh.Position(v);
            if (Math.Abs(p.Y - Side / 2) > 1e-9 * Side || Math.Abs(p.Z - Side / 2) > 1e-9 * Side)
                continue;
            var u = mode.ShapeAt(v);
            double lateral = Math.Sqrt(u.Y * u.Y + u.Z * u.Z) / peak;
            double exact = Math.Sin(Math.PI * p.X / Length);
            worst = Math.Max(worst, Math.Abs(lateral - exact));
            sampled++;
        }

        output.WriteLine(
            $"{sampled} centroidal nodes: worst deviation from |sin(pi x/L)| is {worst:E3}");
        Assert.True(sampled > 10, $"only {sampled} centroidal nodes were found");
        Assert.True(worst < 0.01, $"the mode is not a half sine: worst deviation {worst:E3}");
    }
}
