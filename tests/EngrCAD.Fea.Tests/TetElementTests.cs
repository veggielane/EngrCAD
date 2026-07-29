using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Element-level verification: the properties an element stiffness matrix must have
/// before any assembly, solve or benchmark means anything.
/// </summary>
public class TetElementTests
{
    private static readonly Material Steel = new("test steel", 210_000, 0.3, 7.85e-9);

    /// <summary>A deliberately irregular tetrahedron — nothing axis-aligned, nothing
    /// equilateral, so no accidental symmetry can make a wrong formula look right.</summary>
    private static Vector3d[] SkewTet() =>
    [
        new(0.3, -0.2, 0.1),
        new(4.1, 0.4, -0.3),
        new(0.8, 3.2, 0.6),
        new(1.1, 0.9, 2.7),
    ];

    private static Vector3d[] SkewTet10()
    {
        var c = SkewTet();
        return
        [
            c[0], c[1], c[2], c[3],
            (c[0] + c[1]) * 0.5,
            (c[1] + c[2]) * 0.5,
            (c[0] + c[2]) * 0.5,
            (c[0] + c[3]) * 0.5,
            (c[1] + c[3]) * 0.5,
            (c[2] + c[3]) * 0.5,
        ];
    }

    private static double[] Stiffness(ElementOrder order, Vector3d[] nodes, TetQuadrature rule)
    {
        int dofs = 3 * nodes.Length;
        var ke = new double[dofs * dofs];
        TetElement.Stiffness(order, nodes, Steel, rule, ke);
        return ke;
    }

    private static double MaxAbs(double[] a)
    {
        double best = 0;
        foreach (double v in a)
            best = Math.Max(best, Math.Abs(v));
        return best;
    }

    // ---- the rigid-body test: the single strongest element-level check ----

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void ElementStiffness_AnnihilatesAllSixRigidBodyModes(ElementOrder order)
    {
        // K·r = 0 for every rigid motion r is not one property among many: it is what
        // makes the element frame-indifferent, and essentially every sign, index or
        // Jacobian error breaks it. A rotation field is LINEAR in position, so it is
        // exactly representable by both element types - there is no discretization error
        // to hide behind and the residual is pure round-off.
        var nodes = order == ElementOrder.Linear ? SkewTet() : SkewTet10();
        var ke = Stiffness(order, nodes, TetQuadrature.For(order));
        int n = nodes.Length;
        int dofs = 3 * n;
        double scale = MaxAbs(ke);

        for (int mode = 0; mode < 6; mode++)
        {
            var r = new double[dofs];
            double magnitude = 0;
            for (int i = 0; i < n; i++)
            {
                Vector3d u = mode switch
                {
                    0 => Vector3d.UnitX,
                    1 => Vector3d.UnitY,
                    2 => Vector3d.UnitZ,
                    3 => Vector3d.UnitX.Cross(nodes[i]),
                    4 => Vector3d.UnitY.Cross(nodes[i]),
                    _ => Vector3d.UnitZ.Cross(nodes[i]),
                };
                r[3 * i] = u.X;
                r[3 * i + 1] = u.Y;
                r[3 * i + 2] = u.Z;
                magnitude = Math.Max(magnitude, u.Length);
            }

            for (int row = 0; row < dofs; row++)
            {
                double sum = 0;
                for (int col = 0; col < dofs; col++)
                    sum += ke[row * dofs + col] * r[col];
                Assert.True(Math.Abs(sum) <= 1e-9 * scale * magnitude * dofs,
                    $"{order} mode {mode} row {row}: force {sum:E3} against scale {scale:E3}");
            }
        }
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void ElementStiffness_IsSymmetricAndPositiveSemiDefinite(ElementOrder order)
    {
        var nodes = order == ElementOrder.Linear ? SkewTet() : SkewTet10();
        var ke = Stiffness(order, nodes, TetQuadrature.For(order));
        int dofs = 3 * nodes.Length;
        double scale = MaxAbs(ke);

        for (int i = 0; i < dofs; i++)
        {
            for (int j = 0; j < dofs; j++)
            {
                Assert.True(Math.Abs(ke[i * dofs + j] - ke[j * dofs + i]) <= 1e-12 * scale,
                    $"asymmetry at ({i}, {j})");
            }
            Assert.True(ke[i * dofs + i] > 0, $"diagonal {i} is {ke[i * dofs + i]:E3}");
        }

        // Positive semi-definiteness over a deterministic spread of directions.
        for (int seed = 0; seed < 40; seed++)
        {
            var v = new double[dofs];
            for (int i = 0; i < dofs; i++)
                v[i] = Math.Sin(1.7 * seed + 0.31 * i) + 0.4 * Math.Cos(0.9 * seed - 0.13 * i);
            double energy = 0;
            for (int i = 0; i < dofs; i++)
            {
                for (int j = 0; j < dofs; j++)
                    energy += v[i] * ke[i * dofs + j] * v[j];
            }
            Assert.True(energy >= -1e-9 * scale, $"negative energy {energy:E3} for direction {seed}");
        }
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void IndexFormStiffness_EqualsExplicitBTransposeDB(ElementOrder order)
    {
        // The production path collapses B'DB into an index expression. Here is the
        // textbook form, written independently, over the same quadrature.
        var nodes = order == ElementOrder.Linear ? SkewTet() : SkewTet10();
        var rule = TetQuadrature.For(order);
        var expected = Stiffness(order, nodes, rule);

        int n = nodes.Length;
        int dofs = 3 * n;
        var d = Steel.ConstitutiveMatrix();
        var actual = new double[dofs * dofs];
        var gradient = new Vector3d[10];

        for (int q = 0; q < rule.Count; q++)
        {
            var (r, s, t) = rule.Point(q);
            Assert.True(TetElement.ShapeGradients(order, nodes, r, s, t, gradient, out double detJ));
            double weight = rule.Weight(q) * detJ;

            // B is 6 x dofs; column (3i + a) is the a-th displacement of node i.
            var b = new double[6 * dofs];
            for (int i = 0; i < n; i++)
            {
                var g = gradient[i];
                b[0 * dofs + 3 * i + 0] = g.X;
                b[1 * dofs + 3 * i + 1] = g.Y;
                b[2 * dofs + 3 * i + 2] = g.Z;
                b[3 * dofs + 3 * i + 0] = g.Y;
                b[3 * dofs + 3 * i + 1] = g.X;
                b[4 * dofs + 3 * i + 1] = g.Z;
                b[4 * dofs + 3 * i + 2] = g.Y;
                b[5 * dofs + 3 * i + 0] = g.Z;
                b[5 * dofs + 3 * i + 2] = g.X;
            }

            for (int i = 0; i < dofs; i++)
            {
                for (int j = 0; j < dofs; j++)
                {
                    double sum = 0;
                    for (int k = 0; k < 6; k++)
                    {
                        for (int l = 0; l < 6; l++)
                            sum += b[k * dofs + i] * d[k * 6 + l] * b[l * dofs + j];
                    }
                    actual[i * dofs + j] += weight * sum;
                }
            }
        }

        double scale = MaxAbs(expected);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= 1e-10 * scale, $"entry {i}");
    }

    [Fact]
    public void QuadraticStiffness_IsUnchangedByARicherQuadratureRule()
    {
        // The production rule is degree 2. That is exact ONLY because a straight-sided
        // 10-node tetrahedron has a constant Jacobian, which makes B linear and B'DB
        // quadratic. Comparing against an independent degree-3 rule is precisely the
        // statement that the element really is straight-sided - and it is the test that
        // would fail the day curved (iso-parametric) elements arrive without a matching
        // change here.
        var nodes = SkewTet10();
        var degree2 = Stiffness(ElementOrder.Quadratic, nodes, TetQuadrature.Degree2);
        var degree3 = Stiffness(ElementOrder.Quadratic, nodes, TetQuadrature.Degree3);
        double scale = MaxAbs(degree2);

        for (int i = 0; i < degree2.Length; i++)
            Assert.True(Math.Abs(degree2[i] - degree3[i]) <= 1e-10 * scale, $"entry {i}");
    }

    [Fact]
    public void CurvedElement_IsWhereTheTwoRulesDisagree()
    {
        // The negative control for the test above: move ONE mid-edge node off its
        // midpoint and the two rules must part company, or the comparison was proving
        // nothing.
        var nodes = SkewTet10();
        nodes[5] += new Vector3d(0.35, -0.22, 0.18);
        var degree2 = Stiffness(ElementOrder.Quadratic, nodes, TetQuadrature.Degree2);
        var degree3 = Stiffness(ElementOrder.Quadratic, nodes, TetQuadrature.Degree3);

        double scale = MaxAbs(degree2);
        double worst = 0;
        for (int i = 0; i < degree2.Length; i++)
            worst = Math.Max(worst, Math.Abs(degree2[i] - degree3[i]));
        Assert.True(worst > 1e-4 * scale,
            $"a curved element should integrate differently; worst difference {worst / scale:E3} relative");
    }

    // ---- consistent load vectors ----

    [Fact]
    public void LinearBodyLoadWeights_AreAQuarterOfTheVolumeEach()
    {
        var nodes = SkewTet();
        var weights = new double[4];
        TetElement.BodyLoadWeights(ElementOrder.Linear, nodes, TetQuadrature.Degree1, weights);

        double volume = TetMesh.SignedVolume(nodes[0], nodes[1], nodes[2], nodes[3]);
        foreach (double w in weights)
            Assert.Equal(volume / 4.0, w, volume * 1e-12);
        Assert.Equal(volume, weights.Sum(), volume * 1e-12);
    }

    [Fact]
    public void QuadraticBodyLoadWeights_AreNegativeAtTheCornersAndStillSumToTheVolume()
    {
        // -V/20 at each corner, +V/5 at each mid-edge node. The negative corner loads look
        // like a sign error and are not: they are what makes the consistent load
        // reproduce a quadratic field exactly. 4·(-1/20) + 6·(1/5) = 1.
        var nodes = SkewTet10();
        var weights = new double[10];
        TetElement.BodyLoadWeights(ElementOrder.Quadratic, nodes, TetQuadrature.Degree2, weights);

        double volume = TetMesh.SignedVolume(nodes[0], nodes[1], nodes[2], nodes[3]);
        for (int i = 0; i < 4; i++)
            Assert.Equal(-volume / 20.0, weights[i], volume * 1e-12);
        for (int i = 4; i < 10; i++)
            Assert.Equal(volume / 5.0, weights[i], volume * 1e-12);
        Assert.Equal(volume, weights.Sum(), volume * 1e-12);
    }

    [Fact]
    public void LinearFacetLoadWeights_AreAThirdOfTheAreaEach()
    {
        Vector3d[] facet = [new(0.2, 0.1, 0), new(3.4, 0.5, 0), new(1.1, 2.9, 0)];
        var weights = new double[3];
        TetElement.FacetLoadWeights(ElementOrder.Linear, facet, weights);

        double area = (facet[1] - facet[0]).Cross(facet[2] - facet[0]).Length * 0.5;
        foreach (double w in weights)
            Assert.Equal(area / 3.0, w, area * 1e-12);
    }

    [Fact]
    public void QuadraticFacetLoadWeights_AreExactlyZeroAtTheCorners()
    {
        // The textbook result that looks like a bug the first time a pressure load is
        // inspected node by node: a uniform traction on a 6-node triangle puts nothing on
        // the corners and A/3 on each mid-edge node.
        Vector3d[] corners = [new(0.2, 0.1, 0), new(3.4, 0.5, 0), new(1.1, 2.9, 0)];
        Vector3d[] facet =
        [
            corners[0], corners[1], corners[2],
            (corners[0] + corners[1]) * 0.5,
            (corners[1] + corners[2]) * 0.5,
            (corners[2] + corners[0]) * 0.5,
        ];
        var weights = new double[6];
        TetElement.FacetLoadWeights(ElementOrder.Quadratic, facet, weights);

        double area = (corners[1] - corners[0]).Cross(corners[2] - corners[0]).Length * 0.5;
        for (int i = 0; i < 3; i++)
            Assert.Equal(0.0, weights[i], area * 1e-12);
        for (int i = 3; i < 6; i++)
            Assert.Equal(area / 3.0, weights[i], area * 1e-12);
        Assert.Equal(area, weights.Sum(), area * 1e-12);
    }

    // ---- the constitutive law ----

    [Fact]
    public void ConstitutiveMatrix_ReproducesUniaxialTensionAndPureShear()
    {
        var material = new Material("check", 200_000, 0.3);

        // Uniaxial stress: strain (1e-3, -nu·1e-3, -nu·1e-3) must give (E·1e-3, 0, 0).
        double e = 1e-3;
        Span<double> strain = [e, -0.3 * e, -0.3 * e, 0, 0, 0];
        Span<double> stress = stackalloc double[6];
        material.Stress(strain, stress);
        Assert.Equal(200_000 * e, stress[0], 200_000 * e * 1e-12);
        Assert.Equal(0.0, stress[1], 1e-9);
        Assert.Equal(0.0, stress[2], 1e-9);

        // Pure shear: tau = G·gamma.
        Span<double> shear = [0, 0, 0, e, 0, 0];
        material.Stress(shear, stress);
        Assert.Equal(material.ShearModulus * e, stress[3], 1e-12);
        Assert.Equal(0.0, stress[0], 1e-12);
    }

    [Fact]
    public void VonMises_MatchesTheClosedFormOnUniaxialAndPureShear()
    {
        Assert.Equal(150.0, TetElement.VonMises(new SymmetricTensor3(150, 0, 0, 0, 0, 0)), 1e-12);
        // Pure shear tau: von Mises = sqrt(3)·tau.
        Assert.Equal(
            Math.Sqrt(3.0) * 40.0,
            TetElement.VonMises(new SymmetricTensor3(0, 0, 0, 40, 0, 0)),
            1e-12);
        // Hydrostatic pressure carries no von Mises stress at all.
        Assert.Equal(0.0, TetElement.VonMises(new SymmetricTensor3(-90, -90, -90, 0, 0, 0)), 1e-12);
    }
}
