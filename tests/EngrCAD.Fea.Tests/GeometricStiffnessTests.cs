using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Element-level checks on <see cref="TetElement.GeometricStiffness"/> — the identities that
/// hold for ANY prestress and any element, which is what makes them able to catch a defect a
/// column comparison would absorb into a percent.
/// </summary>
public class GeometricStiffnessTests(ITestOutputHelper output)
{
    private static readonly Vector3d[] LinearTet =
    [
        new(0.3, 0.1, -0.2),
        new(2.7, 0.4, 0.1),
        new(0.6, 3.1, 0.5),
        new(0.2, 0.7, 2.4),
    ];

    /// <summary>A deliberately mixed prestress: three unequal normal components and all three
    /// shears, so no term of the contraction can be zero by accident.</summary>
    private static readonly double[] MixedStress = [-37.0, 12.0, -5.0, 8.0, -3.0, 6.5];

    private static Vector3d[] Quadratic(Vector3d[] corners)
    {
        var nodes = new Vector3d[10];
        Array.Copy(corners, nodes, 4);
        // The QuadraticTet mid-edge order: (0,1) (1,2) (0,2) (0,3) (1,3) (2,3).
        (int A, int B)[] edges = [(0, 1), (1, 2), (0, 2), (0, 3), (1, 3), (2, 3)];
        for (int i = 0; i < 6; i++)
            nodes[4 + i] = (corners[edges[i].A] + corners[edges[i].B]) * 0.5;
        return nodes;
    }

    private static double[] Repeat(double[] stress, int points)
    {
        var all = new double[6 * points];
        for (int q = 0; q < points; q++)
            Array.Copy(stress, 0, all, 6 * q, 6);
        return all;
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void EveryRowSumsToZero_SoARigidTranslationCarriesNoGeometricStiffness(ElementOrder order)
    {
        // The exact identity, and it holds for any stress at any quadrature point: the shape
        // functions are a partition of unity, so their gradients sum to exactly zero and
        // sum_b Kg_ab = integral(grad N_a · sigma · sum_b grad N_b) = 0. Physically it says a
        // rigid translation of a prestressed body does no work against the prestress, which
        // is the geometric-stiffness counterpart of the mass matrix's rows summing to the
        // nodal mass.
        var nodes = order == ElementOrder.Linear ? LinearTet : Quadratic(LinearTet);
        var rule = TetQuadrature.ForGeometric(order);
        var kg = new double[nodes.Length * nodes.Length];
        TetElement.GeometricStiffness(order, nodes, Repeat(MixedStress, rule.Count), rule, kg);

        double scale = 0;
        foreach (double v in kg)
            scale = Math.Max(scale, Math.Abs(v));

        double worst = 0;
        for (int i = 0; i < nodes.Length; i++)
        {
            double sum = 0;
            for (int j = 0; j < nodes.Length; j++)
                sum += kg[i * nodes.Length + j];
            worst = Math.Max(worst, Math.Abs(sum));
        }
        output.WriteLine($"{order}: worst row sum {worst:E3} against entries of scale {scale:E3} "
            + $"({worst / scale:E2} relative)");
        Assert.True(worst / scale < 1e-14, $"row sums are not zero: {worst / scale:E2} relative");
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void TheMatrixIsSymmetric(ElementOrder order)
    {
        var nodes = order == ElementOrder.Linear ? LinearTet : Quadratic(LinearTet);
        var rule = TetQuadrature.ForGeometric(order);
        int n = nodes.Length;
        var kg = new double[n * n];
        TetElement.GeometricStiffness(order, nodes, Repeat(MixedStress, rule.Count), rule, kg);

        double scale = 0, worst = 0;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                scale = Math.Max(scale, Math.Abs(kg[i * n + j]));
                worst = Math.Max(worst, Math.Abs(kg[i * n + j] - kg[j * n + i]));
            }
        }
        output.WriteLine($"{order}: worst asymmetry {worst / scale:E2} relative");
        Assert.True(worst / scale < 1e-15);
    }

    [Fact]
    public void ProductionRuleIsExact_ComparedAgainstAnIndependentIntegrator()
    {
        // TetQuadrature.ForGeometric picks Degree3 for a 10-node element because the
        // integrand is cubic. That claim is checked the way the elastic stiffness's is:
        // integrate the same thing with a rule that is exact two degrees higher and demand
        // the same matrix. It also settles the doc comment's other claim — that Degree3's
        // NEGATIVE centroid weight is harmless here — since a negative weight cannot change
        // the value of an integral the rule integrates exactly.
        var nodes = Quadratic(LinearTet);
        int n = nodes.Length;
        var cheap = new double[n * n];
        var rich = new double[n * n];
        TetElement.GeometricStiffness(
            ElementOrder.Quadratic, nodes, Repeat(MixedStress, TetQuadrature.Degree3.Count),
            TetQuadrature.Degree3, cheap);
        TetElement.GeometricStiffness(
            ElementOrder.Quadratic, nodes, Repeat(MixedStress, TetQuadrature.Degree5.Count),
            TetQuadrature.Degree5, rich);

        double scale = 0, worst = 0;
        for (int i = 0; i < n * n; i++)
        {
            scale = Math.Max(scale, Math.Abs(rich[i]));
            worst = Math.Max(worst, Math.Abs(cheap[i] - rich[i]));
        }
        output.WriteLine(
            $"Degree3 against Degree5 (15 points): worst difference {worst:E3} on entries of "
            + $"scale {scale:E3}, {worst / scale:E2} relative");
        Assert.True(worst / scale < 1e-13, $"the degree-3 rule is not exact: {worst / scale:E2}");
    }

    [Fact]
    public void LinearElement_MatchesTheClosedFormUnderUniaxialStress()
    {
        // For a 4-node tetrahedron the gradients and the stress are both constant, so the
        // integral is exactly V·(grad N_a · sigma · grad N_b) with no quadrature involved at
        // all. Under a pure sigma_xx that is V·sigma_xx·N_a,x·N_b,x — computed here from the
        // element's own gradients, which is an independent arithmetic path to the same number.
        const double sxx = -42.0;
        var nodes = LinearTet;
        var rule = TetQuadrature.ForGeometric(ElementOrder.Linear);
        var kg = new double[16];
        TetElement.GeometricStiffness(
            ElementOrder.Linear, nodes, Repeat([sxx, 0, 0, 0, 0, 0], rule.Count), rule, kg);

        Span<Vector3d> grad = stackalloc Vector3d[10];
        Assert.True(TetElement.ShapeGradients(
            ElementOrder.Linear, nodes, 0.25, 0.25, 0.25, grad, out double detJ));
        double volume = detJ / 6.0;

        double worst = 0, scale = 0;
        for (int a = 0; a < 4; a++)
        {
            for (int b = 0; b < 4; b++)
            {
                double expected = volume * sxx * grad[a].X * grad[b].X;
                scale = Math.Max(scale, Math.Abs(expected));
                worst = Math.Max(worst, Math.Abs(kg[a * 4 + b] - expected));
            }
        }
        output.WriteLine(
            $"volume {volume:G6}: worst deviation from V·sigma_xx·N_a,x·N_b,x is {worst:E3} "
            + $"({worst / scale:E2} relative)");
        Assert.True(worst / scale < 1e-14);
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void UniformCompressionMakesTheMatrixNegativeSemiDefinite(ElementOrder order)
    {
        // The claim BucklingSolver's remarks make about why a column is a well-behaved case:
        // under a uniform uniaxial compression sigma_xx = -s the integral collapses to
        // -s·integral(|du/dx|²), which is non-positive for EVERY displacement field. It is
        // what makes -Kg positive semi-definite there, so the buckling pencil is definite and
        // every load factor is positive — the indefiniteness the solver is built to survive
        // belongs to bending and mixed prestress, not to this. Under TENSION the same
        // quantity flips sign, which is stress stiffening.
        var nodes = order == ElementOrder.Linear ? LinearTet : Quadratic(LinearTet);
        var rule = TetQuadrature.ForGeometric(order);
        int n = nodes.Length;
        var compressed = new double[n * n];
        var stretched = new double[n * n];
        TetElement.GeometricStiffness(
            order, nodes, Repeat([-17.0, 0, 0, 0, 0, 0], rule.Count), rule, compressed);
        TetElement.GeometricStiffness(
            order, nodes, Repeat([17.0, 0, 0, 0, 0, 0], rule.Count), rule, stretched);

        var random = new Random(20260729);
        double worstCompressed = double.NegativeInfinity, bestStretched = double.NegativeInfinity;
        for (int trial = 0; trial < 200; trial++)
        {
            var u = new double[n];
            for (int i = 0; i < n; i++)
                u[i] = random.NextDouble() * 2 - 1;
            double qc = 0, qs = 0;
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    qc += u[i] * compressed[i * n + j] * u[j];
                    qs += u[i] * stretched[i * n + j] * u[j];
                }
            }
            worstCompressed = Math.Max(worstCompressed, qc);
            bestStretched = Math.Max(bestStretched, qs);
        }

        output.WriteLine(
            $"{order}: largest u'Kg u over 200 random fields is {worstCompressed:E3} under "
            + $"compression and {bestStretched:E3} under the same tension");
        Assert.True(worstCompressed <= 0);
        Assert.True(bestStretched > 0);
    }

    [Fact]
    public void ZeroStressGivesTheZeroMatrix()
    {
        // Exactly zero rather than nearly: every term is a product with a stress component,
        // so a stress-free element contributes nothing at all. It is the identity behind
        // BucklingSolver's refusal of a load-free reference solve.
        var nodes = Quadratic(LinearTet);
        var rule = TetQuadrature.ForGeometric(ElementOrder.Quadratic);
        var kg = new double[100];
        TetElement.GeometricStiffness(
            ElementOrder.Quadratic, nodes, new double[6 * rule.Count], rule, kg);
        Assert.All(kg, v => Assert.Equal(0.0, v));
    }
}
