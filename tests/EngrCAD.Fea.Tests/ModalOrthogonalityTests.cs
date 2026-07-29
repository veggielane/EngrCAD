using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The identities that define a modal basis: <c>phi_i' M phi_j = delta_ij</c> and
/// <c>phi_i' K phi_j = lambda_i · delta_ij</c>.
///
/// <para><b>The products are assembled here, independently of the solver.</b> Element by
/// element, from <c>TetElement.Stiffness</c> and <c>TetElement.ConsistentMass</c> — the same
/// rule as the static solver's "index form asserted against an explicit B'DB written
/// independently". Reading the solver's own reduced matrices back would test that the
/// eigensolver is consistent with itself, which it is by construction and which proves
/// nothing.</para>
/// </summary>
public class ModalOrthogonalityTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void ModesAreMassOrthonormalAndStiffnessOrthogonal(ElementOrder order)
    {
        var mesh = ModalFixtures.Beam(60, 12, 8, 6, 2, 2, order);
        var model = new StructuralModel(mesh, ModalFixtures.Steel);
        model.Fix(Facets.Tag(StructuredTetMesh.XMin));
        var results = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 5 });

        var shapes = results.Modes.Select(m => m.Shape.ToArray()).ToArray();
        double worstMass = 0, worstStiffness = 0;

        for (int i = 0; i < shapes.Length; i++)
        {
            for (int j = 0; j < shapes.Length; j++)
            {
                double mass = Quadratic(model, shapes[i], shapes[j], Matrix.Mass);
                double stiffness = Quadratic(model, shapes[i], shapes[j], Matrix.Stiffness);
                double expectedMass = i == j ? 1.0 : 0.0;
                double expectedStiffness = i == j ? results.Mode(i + 1).Eigenvalue : 0.0;

                // Off-diagonal entries are compared against the DIAGONAL's scale, not against
                // zero: "phi_1' K phi_2 is small" only means something relative to
                // phi_1' K phi_1, which is lambda_1 and carries the model's units.
                worstMass = Math.Max(worstMass, Math.Abs(mass - expectedMass));
                worstStiffness = Math.Max(
                    worstStiffness,
                    Math.Abs(stiffness - expectedStiffness) / results.Mode(i + 1).Eigenvalue);
            }
        }

        output.WriteLine($"{order}: worst |phi_i' M phi_j - delta_ij| = {worstMass:E3}");
        output.WriteLine(
            $"{order}: worst |phi_i' K phi_j - lambda_i·delta_ij| / lambda_i = {worstStiffness:E3}");

        Assert.True(worstMass < 1e-11, $"mass orthonormality off by {worstMass:E3}");
        Assert.True(worstStiffness < 1e-8, $"stiffness orthogonality off by {worstStiffness:E3}");
    }

    [Fact]
    public void ModeShapesAreDeterministic_IncludingTheirSign()
    {
        // A mode shape's sign is arbitrary mathematically, so the solver pins it. Two solves
        // of the same model must therefore agree bit for bit — otherwise a saved result, a
        // committed image or an animation would flip between runs.
        var mesh = ModalFixtures.Beam(60, 12, 8, 6, 2, 2, ElementOrder.Linear);
        StructuralModel Build()
        {
            var model = new StructuralModel(mesh, ModalFixtures.Steel);
            model.Fix(Facets.Tag(StructuredTetMesh.XMin));
            return model;
        }

        var first = ModalSolver.Solve(Build(), new ModalSolveOptions { ModeCount = 4 });
        var second = ModalSolver.Solve(Build(), new ModalSolveOptions { ModeCount = 4 });

        for (int number = 1; number <= 4; number++)
        {
            Assert.Equal(first.Mode(number).Eigenvalue, second.Mode(number).Eigenvalue);
            for (int v = 0; v < mesh.NodeCount; v++)
                Assert.Equal(first.Mode(number).ShapeAt(v), second.Mode(number).ShapeAt(v));
        }

        // And the convention itself: the largest component is positive.
        foreach (var mode in first.Modes)
        {
            double largest = 0;
            foreach (var u in mode.Shape)
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    if (Math.Abs(u[axis]) > Math.Abs(largest))
                        largest = u[axis];
                }
            }
            output.WriteLine($"mode {mode.Number}: largest component {largest:E3}");
            Assert.True(largest > 0, $"mode {mode.Number}'s largest component is negative");
        }
    }

    [Fact]
    public void EffectiveMassesSumToTheParticipatingMass()
    {
        // The identity that makes "have I extracted enough modes?" answerable: summed over
        // ALL modes the effective masses recover iota' M iota exactly. Over a handful they
        // recover a fraction, and the fraction is the number to look at — the usual bar is
        // 90%. The cantilever's first bending mode is the textbook case at about 61% of the
        // beam's mass in the transverse direction.
        // A RECTANGULAR section, deliberately. On a square one the first two modes are a
        // degenerate pair, and the eigenvectors of a degenerate pair are an ARBITRARY basis
        // of their eigenspace — so each of them comes out as a mixture of the two bending
        // directions and carries half the effective mass in each (measured on the square
        // 10 x 10 cantilever: 2.399e-5 in Y and 2.399e-5 in Z for BOTH of modes 1 and 2,
        // summing across the pair to the same 61% one mode carries here). A per-direction
        // effective mass is therefore a property of an EIGENSPACE, not of a mode, wherever
        // the spectrum is degenerate.
        const double length = 100.0, width = 12.0, depth = 8.0;
        var mesh = ModalFixtures.Beam(length, width, depth, 20, 2, 2, ElementOrder.Quadratic);
        var model = new StructuralModel(mesh, ModalFixtures.Steel);
        model.Fix(Facets.Tag(StructuredTetMesh.XMin));
        var results = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 8 });

        double beamMass = ModalFixtures.Steel.Density * length * width * depth;
        output.WriteLine(
            $"total mass {results.TotalMass:E6} (analytic {beamMass:E6}), "
            + $"participating {results.ParticipatingMass}");
        Assert.Equal(beamMass, results.TotalMass, 1e-12 * beamMass);

        double fraction = results.Mode(1).EffectiveMass.Z / beamMass;
        output.WriteLine(
            $"mode 1 effective mass {results.Mode(1).EffectiveMass.Z:E6} = {fraction:P2} of the beam");
        // 0.6132 is the classical first-mode participation of a uniform cantilever. The 3D
        // model's clamped end holds a little mass rigidly, so the measured share sits just
        // below it — reported rather than tuned.
        Assert.InRange(fraction, 0.56, 0.62);

        var extracted = results.ExtractedEffectiveMass;
        output.WriteLine(
            $"eight modes account for {extracted.X / results.ParticipatingMass.X:P1} of X, "
            + $"{extracted.Y / results.ParticipatingMass.Y:P1} of Y, "
            + $"{extracted.Z / results.ParticipatingMass.Z:P1} of Z");
        // No mode's effective mass can exceed the participating total; that is the identity's
        // one-sided half, and it holds however few modes have been extracted.
        Assert.True(extracted.X <= results.ParticipatingMass.X * (1 + 1e-9));
        Assert.True(extracted.Y <= results.ParticipatingMass.Y * (1 + 1e-9));
        Assert.True(extracted.Z <= results.ParticipatingMass.Z * (1 + 1e-9));
    }

    private enum Matrix { Stiffness, Mass }

    /// <summary>
    /// <c>a' X b</c> for X assembled element by element from <see cref="TetElement"/> —
    /// written here rather than read off the solver, so the two are independent.
    /// </summary>
    private static double Quadratic(
        StructuralModel model, Vector3d[] a, Vector3d[] b, Matrix which)
    {
        var mesh = model.Mesh;
        int perElement = mesh.NodesPerElement;
        int elementDofs = 3 * perElement;
        var positions = new Vector3d[perElement];
        var ke = new double[elementDofs * elementDofs];
        var me = new double[perElement * perElement];
        double total = 0;

        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            for (int i = 0; i < perElement; i++)
                positions[i] = mesh.Position(nodes[i]);

            if (which == Matrix.Stiffness)
            {
                TetElement.Stiffness(
                    mesh.Order, positions, model.MaterialOf(e),
                    TetQuadrature.For(mesh.Order), ke);
                for (int i = 0; i < elementDofs; i++)
                {
                    double ai = a[nodes[i / 3]][i % 3];
                    if (ai == 0)
                        continue;
                    for (int j = 0; j < elementDofs; j++)
                        total += ai * ke[i * elementDofs + j] * b[nodes[j / 3]][j % 3];
                }
                continue;
            }

            TetElement.ConsistentMass(
                mesh.Order, positions, model.MaterialOf(e).Density,
                TetQuadrature.ForMass(mesh.Order), me);
            for (int i = 0; i < perElement; i++)
            {
                for (int j = 0; j < perElement; j++)
                {
                    double v = me[i * perElement + j];
                    for (int axis = 0; axis < 3; axis++)
                        total += a[nodes[i]][axis] * v * b[nodes[j]][axis];
                }
            }
        }
        return total;
    }
}
