using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Element-level checks on the consistent mass matrix — the SAME integral
/// <c>ThermalElement.Capacity</c> already needed, so these are the structural half of a
/// property <c>ThermalElementTests</c> states thermally.
///
/// <para><b>The negative control is the point.</b> A mass matrix's total is the body's mass
/// whatever rule integrates it, so "does it add up" cannot tell a correct rule from a wrong
/// one. What can is a comparison against a HIGHER rule on a case built to make the two
/// disagree.</para>
/// </summary>
public class MassMatrixTests(ITestOutputHelper output)
{
    private static readonly Vector3d[] Reference =
    [
        new(0, 0, 0), new(1.3, 0, 0), new(0.2, 1.1, 0), new(0.4, 0.3, 0.9),
    ];

    private static double ReferenceVolume =>
        TetMesh.SignedVolume(Reference[0], Reference[1], Reference[2], Reference[3]);

    private static Vector3d[] Quadratic(Vector3d[] corners, Vector3d? moveMidEdge = null)
    {
        var nodes = new Vector3d[10];
        Array.Copy(corners, nodes, 4);
        nodes[4] = (corners[0] + corners[1]) * 0.5;
        nodes[5] = (corners[1] + corners[2]) * 0.5;
        nodes[6] = (corners[0] + corners[2]) * 0.5;
        nodes[7] = (corners[0] + corners[3]) * 0.5;
        nodes[8] = (corners[1] + corners[3]) * 0.5;
        nodes[9] = (corners[2] + corners[3]) * 0.5;
        if (moveMidEdge is { } offset)
            nodes[4] += offset;
        return nodes;
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void ConsistentMass_SumsToTheElementsMass(ElementOrder order)
    {
        const double density = 7.85e-9;
        var nodes = order == ElementOrder.Linear ? Reference : Quadratic(Reference);
        int n = nodes.Length;
        var me = new double[n * n];
        TetElement.ConsistentMass(order, nodes, density, TetQuadrature.ForMass(order), me);

        double total = me.Sum();
        double expected = density * ReferenceVolume;
        output.WriteLine($"{order}: total {total:E12}, rho·V {expected:E12}");
        Assert.Equal(expected, total, 1e-13 * expected);

        // Symmetric, and positive definite: a mass matrix is the Gram matrix of the shape
        // functions under the L2 inner product, so it is SPD for any independent set. The
        // diagonal being positive is the cheap half; the row sums below are the surprising
        // half.
        for (int i = 0; i < n; i++)
        {
            Assert.True(me[i * n + i] > 0, $"diagonal entry {i} is not positive");
            for (int j = 0; j < n; j++)
                Assert.Equal(me[i * n + j], me[j * n + i], 1e-14 * Math.Abs(me[i * n + i]));
        }
    }

    [Fact]
    public void QuadraticRowSums_AreNegativeAtTheCorners()
    {
        // The reason MassLumping.RowSum is refused for 10-node elements, measured rather
        // than asserted from memory: integral(N_i dV) is -V/20 at a corner and V/5 at a
        // mid-edge node. It is the same integral TetElement.BodyLoadWeights documents for a
        // quadratic element's gravity load, and it sums correctly to V either way.
        const double density = 1.0;
        var nodes = Quadratic(Reference);
        var me = new double[100];
        TetElement.ConsistentMass(
            ElementOrder.Quadratic, nodes, density, TetQuadrature.ForMass(ElementOrder.Quadratic), me);

        double volume = ReferenceVolume;
        for (int i = 0; i < 10; i++)
        {
            double sum = 0;
            for (int j = 0; j < 10; j++)
                sum += me[i * 10 + j];
            double expected = i < 4 ? -volume / 20.0 : volume / 5.0;
            output.WriteLine($"row {i}: {sum:E6} (expected {expected:E6})");
            Assert.Equal(expected, sum, 1e-12 * volume);
            if (i < 4)
                Assert.True(sum < 0, $"corner row {i} should be NEGATIVE");
        }
    }

    [Theory]
    [InlineData(ElementOrder.Linear, 4, 1)]
    [InlineData(ElementOrder.Quadratic, 10, 4)]
    public void TheStiffnessRule_GivesASingularMassMatrixWhoseTotalIsStillExact(
        ElementOrder order, int size, int expectedRank)
    {
        // The silent failure, measured for both element orders. A stiffness matrix
        // integrates grad N · grad N (degree 2(p-1)) and a mass matrix N·N (degree 2p), so
        // the stiffness's rule under-integrates the mass by two degrees — and an n-point rule
        // can only produce a matrix of rank n, so the result is SINGULAR. Meanwhile
        // sum_ij N_i N_j = (sum_i N_i)² = 1 exactly, whatever the rule, so the matrix's TOTAL
        // is still exactly rho·V. "Does the mass matrix add up to the mass" — the obvious
        // sanity check, and the one a reviewer asks for — passes it every time.
        const double density = 3.0;
        var nodes = order == ElementOrder.Linear ? Reference : Quadratic(Reference);
        var wrong = new double[size * size];
        TetElement.ConsistentMass(order, nodes, density, TetQuadrature.For(order), wrong);

        double expected = density * ReferenceVolume;
        Assert.Equal(expected, wrong.Sum(), 1e-13 * expected);

        int rank = Rank(wrong, size);
        output.WriteLine(
            $"{order}: the stiffness's rule gives rank {rank} of {size}, total "
            + $"{wrong.Sum():E6} against the exact rho·V {expected:E6}");
        Assert.Equal(expectedRank, rank);

        // The rule the solver actually uses is full rank on the same element.
        var right = new double[size * size];
        TetElement.ConsistentMass(order, nodes, density, TetQuadrature.ForMass(order), right);
        Assert.Equal(size, Rank(right, size));
        Assert.Equal(expected, right.Sum(), 1e-13 * expected);

        if (order != ElementOrder.Linear)
            return;
        // A linear element's exact mass matrix is rho·V/10 on the diagonal and rho·V/20 off
        // it — closed-form values, so this is a check rather than a fingerprint.
        Assert.Equal(expected / 10.0, right[0], 1e-13 * expected);
        Assert.Equal(expected / 20.0, right[1], 1e-13 * expected);
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void MassMatrix_ReproducesTheTetrahedronsExactRotationalInertia(ElementOrder order)
    {
        // The strongest check available, and it needs no transcribed table: a rigid rotation
        // field u = omega x (r - c) is LINEAR in position, so both element orders represent
        // it exactly, and the quadratic form u'Mu is therefore the exact integral
        // integral(rho|omega x r|² dV) = omega' I omega with I the tetrahedron's own inertia
        // tensor about its centroid.
        //
        // That tensor comes from MeshMassProperties' closed-form tetrahedral moments —
        // completely independent arithmetic in another project, which is what makes this a
        // verification rather than a restatement.
        const double density = 7.85e-9;
        var nodes = order == ElementOrder.Linear ? Reference : Quadratic(Reference);
        int size = nodes.Length;
        var me = new double[size * size];
        TetElement.ConsistentMass(order, nodes, density, TetQuadrature.ForMass(order), me);

        var exact = TetrahedronProperties(Reference, density);
        var centroid = exact.Centroid;
        var inertia = exact.Inertia;

        foreach (var omega in new[]
                 {
                     Vector3d.UnitX, Vector3d.UnitY, Vector3d.UnitZ,
                     new Vector3d(0.6, -0.5, 0.7),
                 })
        {
            double measured = RigidRotationEnergy(me, nodes, centroid, omega);
            double reference =
                inertia.Xx * omega.X * omega.X
                + inertia.Yy * omega.Y * omega.Y
                + inertia.Zz * omega.Z * omega.Z
                + 2.0 * (inertia.Xy * omega.X * omega.Y
                         + inertia.Xz * omega.X * omega.Z
                         + inertia.Yz * omega.Y * omega.Z);
            output.WriteLine(
                $"{order} omega {omega}: u'Mu {measured:E12}, omega'I·omega {reference:E12}, "
                + $"relative {Math.Abs(measured - reference) / reference:E2}");
            Assert.Equal(reference, measured, 1e-12 * reference);
        }
    }

    [Fact]
    public void TheStiffnessRule_ReportsZeroRotationalInertia()
    {
        // The negative control with teeth. The one-point rule's mass matrix has every entry
        // equal, so u'Mu collapses to rho·V times the SQUARE OF THE MEAN nodal value — and
        // the mean of a rotation field about the centroid is exactly zero. A tetrahedron
        // integrated that way therefore has NO rotational inertia at all: it would spin up
        // under any torque and every torsional frequency in a model built on it would be
        // infinite. The total mass is still exactly right.
        const double density = 7.85e-9;
        var wrong = new double[16];
        TetElement.ConsistentMass(
            ElementOrder.Linear, Reference, density, TetQuadrature.For(ElementOrder.Linear), wrong);
        var right = new double[16];
        TetElement.ConsistentMass(
            ElementOrder.Linear, Reference, density,
            TetQuadrature.ForMass(ElementOrder.Linear), right);

        var exact = TetrahedronProperties(Reference, density);
        var omega = new Vector3d(0.6, -0.5, 0.7);
        double cheap = RigidRotationEnergy(wrong, Reference, exact.Centroid, omega);
        double correct = RigidRotationEnergy(right, Reference, exact.Centroid, omega);

        output.WriteLine($"stiffness rule: u'Mu = {cheap:E3}");
        output.WriteLine($"mass rule:      u'Mu = {correct:E3}");
        Assert.True(correct > 0);
        Assert.True(Math.Abs(cheap) < 1e-14 * correct,
            $"the one-point rule should report no rotational inertia, got {cheap:E3}");
    }

    [Fact]
    public void MassMatrix_AnnihilatesNothing_AndIsPositiveOnEveryRigidMotion()
    {
        // The counterpart of the stiffness's rigid-mode test, and the reason both are worth
        // having: a stiffness matrix gives ZERO energy on a rigid motion, while a mass matrix
        // gives the motion's kinetic energy, which is strictly positive. A sign or an
        // indexing error that made one look right would make the other look wrong.
        const double density = 7.85e-9;
        var nodes = Quadratic(Reference);
        var me = new double[100];
        TetElement.ConsistentMass(
            ElementOrder.Quadratic, nodes, density,
            TetQuadrature.ForMass(ElementOrder.Quadratic), me);

        var centre = Vector3d.Zero;
        foreach (var p in nodes)
            centre += p;
        centre /= nodes.Length;

        for (int k = 0; k < 6; k++)
        {
            var field = new double[10];
            for (int i = 0; i < 10; i++)
            {
                var motion = k switch
                {
                    0 => Vector3d.UnitX,
                    1 => Vector3d.UnitY,
                    2 => Vector3d.UnitZ,
                    3 => Vector3d.UnitX.Cross(nodes[i] - centre),
                    4 => Vector3d.UnitY.Cross(nodes[i] - centre),
                    _ => Vector3d.UnitZ.Cross(nodes[i] - centre),
                };
                // One component of the motion is enough: the 3x3 blocks are the scalar
                // matrix times the identity, so each axis carries its own copy.
                field[i] = motion.X + motion.Y + motion.Z;
            }

            double energy = 0;
            for (int i = 0; i < 10; i++)
            {
                for (int j = 0; j < 10; j++)
                    energy += field[i] * me[i * 10 + j] * field[j];
            }
            output.WriteLine($"rigid motion {k}: v' M v = {energy:E6}");
            Assert.True(energy > 0, $"rigid motion {k} carried no kinetic energy");
        }
    }

    /// <summary>Numerical rank of a small symmetric matrix, at a relative eigenvalue floor
    /// of 1e-12 against the largest — the scale-free tier, and the same floor the rigid-body
    /// null-space test uses.</summary>
    private static int Rank(double[] matrix, int size)
    {
        var (values, _) = SmallSymmetricEigen.Solve(matrix, size);
        double largest = values.Max(Math.Abs);
        return values.Count(v => Math.Abs(v) > 1e-12 * largest);
    }

    /// <summary><c>u' M u</c> for the rigid rotation <c>u = omega x (r - centre)</c>, over
    /// all three components (the 3x3 blocks are the scalar matrix times the identity, so
    /// each axis contributes its own copy of the same quadratic form).</summary>
    private static double RigidRotationEnergy(
        double[] me, Vector3d[] nodes, Vector3d centre, Vector3d omega)
    {
        int size = nodes.Length;
        double total = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            var field = new double[size];
            for (int i = 0; i < size; i++)
                field[i] = omega.Cross(nodes[i] - centre)[axis];
            for (int i = 0; i < size; i++)
            {
                for (int j = 0; j < size; j++)
                    total += field[i] * me[i * size + j] * field[j];
            }
        }
        return total;
    }

    /// <summary>The exact mass properties of a tetrahedron, from EngrCAD.Mesh's closed-form
    /// polyhedral moments — independent arithmetic in another project, which is what makes
    /// it a reference rather than a restatement.</summary>
    private static MassProperties TetrahedronProperties(Vector3d[] corners, double density)
    {
        // Faces of a positively oriented tetrahedron, wound OUTWARD; face i is opposite
        // vertex i.
        int[][] faces = [[1, 2, 3], [0, 3, 2], [0, 1, 3], [0, 2, 1]];
        var integrator = new MassPropertyIntegrator(corners[0]);
        foreach (var face in faces)
            integrator.AddTriangle(corners[face[0]], corners[face[1]], corners[face[2]]);
        return integrator.Complete(density);
    }
}
